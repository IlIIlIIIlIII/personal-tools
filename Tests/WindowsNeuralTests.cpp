#include "NeuralStream.hpp"
#include "NeuralHost.hpp"
#include <memory>
#include <cstdio>
#include <cstdlib>
#include <random>

using personaltools::win::NeuralStream;
using personaltools::win::NeuralHost;
namespace neural=personaltools::win::neural;

// Stand-in for DtlnNpu: halves the 16 kHz signal so routed output is recognisable.
struct HalfModel {
    static inline std::atomic<int> opens{0},resets{0},stateResets{0};
    void open(const std::filesystem::path& folder){
        ++opens;
        if(!std::filesystem::exists(folder))throw personaltools::win::NpuFailure(personaltools::win::NpuStage::runtime,"missing test runtime");
    }
    void reset(){++resets;}
    void resetState(){++stateResets;}
    void process(const float* mic,const float*,float* out){for(int k=0;k<128;++k)out[k]=mic[k]*.5f;}
};
static std::uint64_t now100ns(){LARGE_INTEGER qpc{},f{};QueryPerformanceCounter(&qpc);QueryPerformanceFrequency(&f);return static_cast<std::uint64_t>(static_cast<long double>(qpc.QuadPart)*10000000/f.QuadPart);}
template<class Predicate> static bool until(Predicate p,int ms=5000){for(int i=0;i<ms/10;++i){if(p())return true;Sleep(10);}return p();}

// Feeds the estimator like the host does: the reference it sees is already shifted by shift().
static int runDelay(personaltools::win::EchoDelayEstimator& e,const std::vector<float>& ref,int echoDelay,int from,int to,float noise){
    std::mt19937 random(7);std::normal_distribution<float> gauss(0.f,1.f);int changes=0;
    for(int n=from;n<to;++n){
        const int shifted=n-e.shift();
        const float mic=(n-echoDelay>=0?0.3f*ref[n-echoDelay]:0.f)+noise*gauss(random);
        e.push(mic,shifted>=0?ref[shifted]:0.f);
        if(e.update())++changes;
    }
    return changes;
}

// A call whose far end plays the microphone back `back` samples later, at 16 kHz: talk spurts of
// the user (`talk`) and of the partner (`partner`), the room echoing the speaker after `echo`.
// Feeds the estimator like the host; returns the largest shift it applied.
static int runCall(personaltools::win::EchoDelayEstimator& e,int seconds,int echo,int back,float talk,float partner,unsigned seed){
    const int n=seconds*16000;std::mt19937 random(seed);std::normal_distribution<float> gauss(0.f,1.f);std::uniform_real_distribution<float> uniform(0.f,1.f);
    auto spurts=[&](float amplitude,float pauses){
        std::vector<float> v(n);int i=0;
        while(i<n){
            const int on=static_cast<int>((0.3f+1.7f*uniform(random))*16000),off=static_cast<int>((0.2f+2.8f*uniform(random)*pauses)*16000);
            float low=0;for(int k=0;k<on&&i<n;++k,++i){low+=0.2f*(gauss(random)-low);v[i]=amplitude*3*low*(0.6f+0.4f*std::sin(6.2832f*4*k/16000.f));}
            i+=off;
        }
        return v;
    };
    const auto user=spurts(talk,1.f),remote=spurts(partner,1.6f);std::vector<float> mic(n),ref(n);int largest=e.shift();
    for(int i=0;i<n;++i){
        ref[i]=remote[i]+(i>=back?0.5f*mic[i-back]:0.f);
        mic[i]=user[i]+(i>=echo?0.3f*ref[i-echo]:0.f)+0.0005f*gauss(random);
        const int shifted=i-e.shift();e.push(mic[i],shifted>=0?ref[shifted]:0.f);e.update();largest=std::max(largest,e.shift());
    }
    return largest;
}

int main(){
    {
        // Microphone packets of 480 frames (10 ms at 48 kHz); `step` is the packet timestamp advance.
        using personaltools::win::MicTiming;
        struct Feed{MicTiming t;std::uint64_t index=0,time=1000000000;int reported=0;
            void run(int packets,const std::function<std::uint64_t(int)>& step){for(int n=0;n<packets;++n){if(t.add(index,time,480))++reported;index+=480;time+=step(n);}}};
        {
            // 10 minutes with ±2 ms jitter and a clock 0.1 % off: never a stall.
            Feed f;std::mt19937 random(5);std::uniform_int_distribution<int> jitter(-20000,20000);
            f.run(60000,[&](int){return static_cast<std::uint64_t>(100100+jitter(random));});
            if(f.reported||f.t.stalled()){printf("ratio %.3f\n",f.t.lastRatio());puts("A healthy microphone was taken for a stalled one");return 1;}
        }
        for(const std::uint64_t step:{150000ull,200000ull}) {
            // 2026-10-24/25 MOTU: 2/3 (or 1/2) of the samples, timestamps 1.5x (2x) the samples' time.
            Feed f;f.run(500,[](int){return std::uint64_t{100000};});
            f.run(1000,[&](int){return step;});
            if(f.reported!=1||!f.t.stalled()){printf("step %llu reported %d ratio %.3f\n",static_cast<unsigned long long>(step),f.reported,f.t.lastRatio());puts("A stalled microphone was not reported exactly once");return 1;}
            // Within two 3 s windows of stalled time (plus the window it started in).
            Feed g;g.run(500,[](int){return std::uint64_t{100000};});int at=-1;
            for(int n=0;n<1000&&at<0;++n){g.run(1,[&](int){return step;});if(g.reported)at=n;}
            if(at<0||at*step>3*30000000ull){printf("reported after %d packets\n",at);puts("A stalled microphone was reported too late");return 1;}
        }
        {
            // One freeze of 2 s without lost samples spoils one window, not two.
            Feed f;f.run(600,[](int n){return n==400?std::uint64_t{20000000}:std::uint64_t{100000};});
            if(f.reported||f.t.stalled()){puts("A single freeze was taken for a stalled microphone");return 1;}
        }
        {
            // A new stream (index does not continue) starts over.
            Feed f;f.run(1000,[](int){return std::uint64_t{150000};});
            if(!f.t.stalled()){puts("Stall not detected before the restart");return 1;}
            f.index+=48000*5;f.run(1000,[](int){return std::uint64_t{100000};});
            if(f.t.stalled()||f.reported!=1){puts("A restarted microphone stayed stalled");return 1;}
        }
        // With samples: 256-frame packets as the APO sends them; `step` advances the timestamp per packet.
        struct Live{MicTiming t;std::uint64_t index=0,time=1000000000;int reported=0,phase=0;std::mt19937 random{9};std::array<float,256> older{},newer{};
            enum Kind{noise,frozen,zeros,code};
            // Noise: a quiet room through the preamp. Frozen: the last 512 frames of it, over and over.
            // Code: a muted input that sends -1 LSB (24 bit) instead of zeros.
            void run(int packets,Kind kind,std::uint64_t step=53333){
                std::normal_distribution<float> gauss(0.f,0.003f);
                for(int n=0;n<packets;++n){
                    std::array<float,256> s{};
                    if(kind==noise){for(auto& x:s)x=gauss(random);older=newer;newer=s;phase=0;}
                    else if(kind==frozen)s=phase++%2?newer:older;
                    else if(kind==code)s.fill(-1.f/8388608.f);
                    if(t.add(index,time,256,s.data()))++reported;index+=256;time+=step;
                }
            }};
        {
            // 10 minutes of real input with ±2 ms jitter, then 10 minutes muted (exact zeros at the right
            // rate), then a muted input that sends a constant code.
            Live f;std::mt19937 random(5);std::uniform_int_distribution<int> jitter(-20000,20000);
            for(int n=0;n<112500;++n){f.run(1,Live::noise,53333+jitter(random));}
            f.run(112500,Live::zeros);f.run(1000,Live::noise);f.run(2000,Live::code);
            if(f.reported||f.t.stalled()){puts("A healthy or muted microphone was taken for a stalled one");return 1;}
        }
        for(const std::uint64_t step:{53333ull,80000ull}) {
            // 2026-10-24 MOTU: the last 512 frames repeated bit for bit (timestamps in step or running 1.5x).
            Live f;f.run(1000,Live::noise);int at=-1;
            for(int n=0;n<2000&&at<0;++n){f.run(1,Live::frozen,step);if(f.reported)at=n;}
            if(at<0||at*256>60000||f.t.reason()!=MicTiming::Reason::frozen||f.t.describe().find("512")==std::string::npos){
                printf("step %llu reported after %d packets: %s\n",static_cast<unsigned long long>(step),at,f.t.describe().c_str());puts("A frozen microphone was not reported within about 1 s");return 1;}
            // Real input again on the same stream: no longer stalled, and a second freeze is reported again.
            f.run(400,Live::noise);
            if(f.t.stalled()){puts("A microphone whose input came back stayed stalled");return 1;}
            f.run(400,Live::frozen);
            if(f.reported!=2){puts("A second freeze was not reported");return 1;}
        }
        {
            // Zeros at half rate (10-24: 24k frames/s): timestamps twice the samples' time, found by the
            // timestamp windows (two 3 s windows, plus the one it started in).
            Live f;f.run(1000,Live::noise);int at=-1;
            for(int n=0;n<2000&&at<0;++n){f.run(1,Live::zeros,106667);if(f.reported)at=n;}
            if(at<0||at*106667ull>3*30000000ull||f.t.reason()!=MicTiming::Reason::timing){printf("reported after %d packets\n",at);puts("A microphone delivering zeros at half rate was not reported within 9 s");return 1;}
        }
        {
            // A single 0.5 s system stall (samples lost) just after muting is not a stall.
            Live f;f.run(1000,Live::noise);f.run(20,Live::zeros);f.run(1,Live::zeros,5053333);f.run(2000,Live::zeros);
            if(f.reported){printf("ratio %.3f\n",f.t.lastRatio());puts("One lost half second while muted was taken for a stalled microphone");return 1;}
        }
        puts("Microphone timestamp stall detection passed");
    }
    {
        using personaltools::win::EchoDelayEstimator;
        constexpr int margin=EchoDelayEstimator::marginSamples;
        // Playback in 1.5 s parts with 1.2 s silences (where a new shift may be applied).
        std::mt19937 random(3);std::normal_distribution<float> gauss(0.f,0.1f);
        std::vector<float> ref(16000*24);for(size_t n=0;n<ref.size();++n)ref[n]=n%43200<24000?gauss(random):0.f;
        // Playback with only short pauses between words.
        std::vector<float> steady(16000*60);for(size_t n=0;n<steady.size();++n)steady[n]=n%19200<16000?gauss(random):0.f;
        {
            // Pauses between words are not enough: a small correction (4 ms) waits instead of
            // moving the reference mid-playback.
            EchoDelayEstimator e;e.seed(592);runDelay(e,steady,656,0,16000*8,0.003f);
            if(std::abs(e.delay()-656)>2||e.shift()!=592-margin){printf("echo delay %d shift %d\n",e.delay(),e.shift());puts("Reference moved while it was playing");return 1;}
        }
        {
            // The first measurement, 24 ms away from the unshifted reference, does not wait.
            EchoDelayEstimator e;runDelay(e,steady,592,0,16000*8,0.003f);
            if(std::abs(e.shift()-(592-margin))>2){printf("echo delay %d shift %d\n",e.delay(),e.shift());puts("The first echo delay waited for a pause");return 1;}
        }
        {
            // 2026-10-24: the reference had been moved step by step to ~1 s behind the echo. A shift
            // far past the echo comes back while the playback goes on.
            EchoDelayEstimator e;e.seed(3200);runDelay(e,steady,592,0,16000*60,0.003f);
            if(std::abs(e.delay()-592)>2||std::abs(e.shift()-(592-margin))>2){printf("echo delay %d shift %d\n",e.delay(),e.shift());puts("A reference shifted far past the echo was not pulled back");return 1;}
        }
        {
            // Delays beyond what the estimator searches (250 ms) are neither remembered nor measured.
            EchoDelayEstimator e;e.seed(15898);
            if(e.delay()!=-1||e.shift()!=0){puts("An unreachable remembered echo delay was applied");return 1;}
            runDelay(e,steady,4800,0,16000*20,0.003f);
            if(e.delay()>EchoDelayEstimator::maxDelay||e.shift()>EchoDelayEstimator::maxDelay){printf("echo delay %d shift %d\n",e.delay(),e.shift());puts("An echo delay beyond 250 ms was adopted");return 1;}
        }
        {
            // Talking far above the playback is no echo measurement.
            EchoDelayEstimator e;std::mt19937 talk(11);std::normal_distribution<float> voice(0.f,1.f);
            for(int n=0;n<16000*10;++n){const float r=0.003f*voice(talk),m=0.2f*voice(talk);e.push(m,r);e.update();}
            if(e.delay()!=-1||e.nearEnd()==0){printf("echo delay %d near-end windows %d\n",e.delay(),e.nearEnd());puts("Near-end speech was taken for an echo");return 1;}
        }
        {
            // The same sound from the speaker (35 ms) and from Bluetooth earphones near the microphone
            // (225 ms), the earphone path changing every 15 s: the speaker delay stays while it shows,
            // and only an earphone path that keeps dominating takes over, once.
            auto run=[&](std::vector<float> ratios,int& changes){
                const int segment=15*16000,n=segment*static_cast<int>(ratios.size());
                std::mt19937 noise(9);std::normal_distribution<float> g(0.f,1.f);std::uniform_real_distribution<float> u(0.f,1.f);
                std::vector<float> play(n);
                for(int i=0;i<n;){const int on=static_cast<int>((0.5f+2.f*u(noise))*16000),off=static_cast<int>(0.3f*u(noise)*16000);
                    float low=0;for(int k=0;k<on&&i<n;++k,++i){low+=0.3f*(g(noise)-low);play[i]=0.3f*low;}i+=off;}
                EchoDelayEstimator e;e.seed(560);changes=0;
                for(int i=0;i<n;++i){
                    const float m=(i>=560?0.15f*play[i-560]:0.f)+(i>=3600?0.15f*ratios[i/segment]*play[i-3600]:0.f)+0.002f*g(noise);
                    const int shifted=i-e.shift();e.push(m,shifted>=0?play[shifted]:0.f);changes+=e.update();
                }
                return e.delay();
            };
            int changes=0;
            const int flapping=run({0.3f,8,0.3f,8,0.3f,8,0.3f,8},changes);
            if(std::abs(flapping-560)>16||changes!=0){printf("echo delay %d after %d changes\n",flapping,changes);puts("A varying earphone path moved the speaker echo delay");return 1;}
            const int taken=run({1,2,4,8,16,16,16},changes);
            if(std::abs(taken-3600)>16||changes!=1){printf("echo delay %d after %d changes\n",taken,changes);puts("A lasting earphone path was not taken over exactly once");return 1;}
        }
        for(unsigned seed=1;seed<=4;++seed){
            // A call partner sending the user's voice back ~190 ms later keeps the adopted delay.
            EchoDelayEstimator e;e.seed(592);
            const int largest=runCall(e,180,592,2900+100*static_cast<int>(seed),0.05f,0.01f,seed);
            if(std::abs(e.delay()-592)>16||largest>592-margin+EchoDelayEstimator::deadBand){printf("seed %u echo delay %d largest shift %d\n",seed,e.delay(),largest);puts("A call loop moved the echo delay");return 1;}
        }
        EchoDelayEstimator e;
        runDelay(e,ref,592,0,16000*6,0.003f); // 37 ms, like the USB DAC measured on 2026-10-20
        if(std::abs(e.delay()-592)>2||std::abs(e.shift()-(592-EchoDelayEstimator::marginSamples))>2){printf("echo delay %d shift %d\n",e.delay(),e.shift());puts("Echo delay was not found");return 1;}
        if(runDelay(e,ref,592,16000*6,16000*10,0.003f)!=0){puts("A stable echo delay moved the reference again");return 1;}
        // A faster output device: the reference is now shifted past the echo and must come back.
        runDelay(e,ref,200,16000*10,16000*24,0.003f);
        if(std::abs(e.delay()-200)>2||e.shift()!=0){printf("echo delay %d shift %d\n",e.delay(),e.shift());puts("Reference shifted past the echo was not pulled back");return 1;}
        EchoDelayEstimator quiet;std::vector<float> silence(ref.size());
        runDelay(quiet,silence,592,0,16000*4,0.003f);
        if(quiet.delay()!=-1||quiet.shift()!=0){puts("Echo delay changed without playback");return 1;}
        EchoDelayEstimator seeded;seeded.seed(592);
        if(seeded.shift()!=592-EchoDelayEstimator::marginSamples){puts("A remembered echo delay was not applied at once");return 1;}
    }
    puts("Echo delay estimation passed");
    {
        using personaltools::win::PlaybackOnset;
        // Hops of room sound with a silent reference, the model keeping `gain` of it, then playback.
        auto onsetClears=[](unsigned quietHops,float gain){
            PlaybackOnset onset;std::array<float,128> mic{},out{},quiet{},playing{};
            std::mt19937 random(9);std::normal_distribution<float> gauss(0.f,.01f);
            for(auto& v:playing)v=gauss(random);
            for(unsigned hop=0;hop<quietHops;++hop){
                for(unsigned k=0;k<128;++k){mic[k]=gauss(random);out[k]=gain*mic[k];}
                if(onset.resumes(quiet.data())){puts("A silent reference cleared the model state");std::exit(1);}
                onset.processed(mic.data(),out.data());
            }
            const bool clear=onset.resumes(playing.data());onset.processed(mic.data(),out.data());
            if(onset.resumes(playing.data())){puts("Continuing playback cleared the model state again");std::exit(1);}
            return clear;
        };
        if(!onsetClears(200,1.f)){puts("Playback after a passed-through pause kept the model state");return 1;}
        if(!onsetClears(200,.4f)){puts("Playback after a pause reduced by only 8 dB kept the model state");return 1;}
        if(onsetClears(200,.15f)){puts("Playback after a suppressed pause cleared the model state");return 1;}
        if(onsetClears(60,1.f)){puts("A short pause between words cleared the model state");return 1;}
    }
    puts("Playback onset state reset passed");
    {
        using personaltools::win::PlaybackJump;
        // A faint reference (-57 dBFS) for `hops`, the model keeping `gain` of the room sound, then
        // playback at `loud` times the faint level. Returns whether the first loud hop cleared the state.
        auto jumpClears=[](unsigned hops,float gain,float loud){
            PlaybackJump jump;std::array<float,128> mic{},out{},faint{},playing{};
            std::mt19937 random(11);std::normal_distribution<float> gauss(0.f,1.f);
            for(unsigned k=0;k<128;++k){faint[k]=.0014f*gauss(random);playing[k]=loud*faint[k];}
            for(unsigned hop=0;hop<hops;++hop){
                for(unsigned k=0;k<128;++k){mic[k]=.003f*gauss(random);out[k]=gain*mic[k];}
                if(jump.jumps(faint.data())){puts("A steady faint reference cleared the model state");std::exit(1);}
                jump.processed(mic.data(),out.data());
            }
            const bool clear=jump.jumps(playing.data());jump.processed(mic.data(),out.data());
            for(unsigned hop=0;hop<20;++hop){
                if(jump.jumps(playing.data())){puts("Continuing playback cleared the model state again");std::exit(1);}
                jump.processed(mic.data(),out.data());
            }
            return clear;
        };
        if(!jumpClears(200,1.f,20.f)){puts("Loud playback after a passed-through faint passage kept the model state");return 1;}
        if(!jumpClears(200,.4f,20.f)){puts("Loud playback after a passage reduced by only 8 dB kept the model state");return 1;}
        if(jumpClears(200,.15f,20.f)){puts("Loud playback after a suppressed passage cleared the model state");return 1;}
        if(jumpClears(200,1.f,3.f)){puts("A 10 dB rise in playback cleared the model state");return 1;}
        if(jumpClears(60,1.f,20.f)){puts("A jump in the first second of a stream cleared the model state");return 1;}
        // A jump that stays below -60 dBFS is not playback the echo could be heard in.
        PlaybackJump quiet;std::array<float,128> mic{},out{},silent{},faint{};
        for(unsigned k=0;k<128;++k){mic[k]=.003f*((k*7919)%13/6.f-1);out[k]=mic[k];faint[k]=k%2?.0005f:-.0005f;}
        for(unsigned hop=0;hop<200;++hop){quiet.jumps(silent.data());quiet.processed(mic.data(),out.data());}
        if(quiet.jumps(faint.data())){puts("A -66 dBFS reference cleared the model state");return 1;}
        // A crescendo of 20 dB per second never stands 15 dB above its own recent level.
        PlaybackJump rising;std::array<float,128> level{};
        for(unsigned hop=0;hop<375;++hop){
            const float a=.0001f*std::pow(10.f,hop/125.f); // -80 to -20 dBFS
            for(unsigned k=0;k<128;++k)level[k]=k%2?a:-a;
            if(rising.jumps(level.data())){puts("A gradual crescendo cleared the model state");return 1;}
            rising.processed(mic.data(),out.data());
        }
    }
    puts("Playback jump state reset passed");
    personaltools::win::NeuralOutputMixer mixer;
    for(unsigned i=0;i<250;++i)mixer.process(.9f,.05f,true,true);
    float previous=.05f;
    for(unsigned i=0;i<240;++i){
        const float value=mixer.process(.9f,.9f,false,true);
        if(std::abs(value-previous)>.004f){puts("Missing neural frame jumped directly to raw audio");return 1;}
        previous=value;
    }
    if(std::abs(previous-.9f)>.0001f){puts("Failure fallback did not reach raw audio");return 1;}
    for(unsigned i=0;i<240;++i){
        const float value=mixer.process(.9f,.05f,true,true);
        if(std::abs(value-previous)>.004f){puts("Recovered neural output jumped");return 1;}
        previous=value;
    }
    if(std::abs(previous-.05f)>.0001f){puts("Recovered model remained bypassed");return 1;}
    {
        // No NPU host: input 1 must pass with fixed latency, including toggle changes.
        auto stream=std::make_unique<NeuralStream>();
        if(!stream->connected()){puts("Stream could not claim a shared channel");return 1;}
        if(stream->state()!=neural::hostMissing){puts("Missing host was not reported");return 1;}
        std::array<float,514> input{},output{};
        unsigned index=0;
        for(unsigned callback=0;callback<100;++callback){
            const unsigned frames=callback%2?257:113;
            for(unsigned i=0;i<frames;++i){input[i*2]=float((index+i)%997)/2000.f;input[i*2+1]=-0.9f;}
            stream->microphone(input.data(),output.data(),frames,2,2,10000000ull+index*10000000ull/48000,false,callback%3!=0);
            for(unsigned i=0;i<frames;++i){
                auto sample=index+i;const float wanted=sample<NeuralStream::latencyFrames?0.f:float((sample-NeuralStream::latencyFrames)%997)/2000.f;
                if(output[i*2]!=wanted||output[i*2+1]!=wanted){puts("Neural failure fallback changed microphone samples/latency");return 1;}
            }
            index+=frames;
        }
    }
    puts("Missing-host fallback and channel/latency preservation passed");
    {
        // Four streams claim four channels; a fifth runs dry; released channels are reused.
        std::vector<std::unique_ptr<NeuralStream>> streams;
        for(unsigned i=0;i<neural::channelCount;++i)streams.push_back(std::make_unique<NeuralStream>());
        NeuralStream extra;
        if(extra.connected()||!streams.back()->connected()){puts("Channel claiming is wrong");return 1;}
        streams.pop_back();
        NeuralStream reuse;
        if(!reuse.connected()){puts("Released channel was not reused");return 1;}
    }
    puts("Shared channel claiming passed");
    {
        // A host whose runtime fails retries with backoff and reports the stage.
        NeuralStream stream;
        NeuralHost<HalfModel> host(L"Z:\\PersonalTools-absent-test-model",neural::Mapping::defaultName(),std::chrono::milliseconds(20));
        if(!until([&]{return stream.restarts()>=3;})){puts("NPU host did not retry after a failed runtime load");return 1;}
        if(stream.failure()!=static_cast<int>(personaltools::win::NpuStage::runtime)){puts("NPU failure stage was not reported");return 1;}
        if(stream.state()!=neural::hostFailed&&stream.state()!=neural::hostOpening){puts("Retrying NPU host reported an unexpected state");return 1;}
    }
    puts("NPU host retry with backoff passed");
    {
        // End to end through the shared section: the processed signal returns with the fixed latency.
        wchar_t temp[MAX_PATH]{};GetTempPathW(MAX_PATH,temp);
        NeuralStream stream;
        NeuralHost<HalfModel> host(temp,neural::Mapping::defaultName(),std::chrono::milliseconds(20));
        if(!until([&]{return stream.state()==neural::hostRunning;})){puts("Host never reached running state");return 1;}
        constexpr unsigned chunk=480,total=48000*3;
        std::vector<float> in(total),out(total),stereo(chunk*2);
        for(unsigned n=0;n<total;++n)in[n]=0.2f*std::sin(2*3.14159265f*300.f*n/48000.f);
        const auto start=now100ns();
        for(unsigned pos=0;pos<total;pos+=chunk){
            const auto time=start+static_cast<std::uint64_t>(pos)*10000000ull/48000;
            while(now100ns()<time)Sleep(1);
            for(unsigned i=0;i<chunk;++i)stereo[i*2]=stereo[i*2+1]=in[pos+i];
            stream.reference(stereo.data(),chunk,2,time,false);
            stream.microphone(in.data()+pos,out.data()+pos,chunk,1,1,time,false,true);
        }
        double inEnergy=0,outEnergy=0;
        for(unsigned n=48000*2;n<total;++n){const double x=in[n-NeuralStream::latencyFrames];inEnergy+=x*x;outEnergy+=out[n]*(double)out[n];}
        const double ratio=outEnergy/inEnergy;
        printf("cross-thread host: completed=%llu misses=%llu energy_ratio=%.3f\n",static_cast<unsigned long long>(stream.completed()),static_cast<unsigned long long>(stream.misses()),ratio);
        if(stream.completed()<200||ratio<.15||ratio>.35){puts("Host output did not come back through the shared section");return 1;}
    }
    puts("Shared-memory NPU host round trip passed");
    {
        // Speaker idle (no loopback reference), playback, a reference gap, playback again, with
        // +1.5 ms microphone timestamp jitter on every other callback. The output must stay
        // processed throughout: raw microphone audio carries the echo tail and room noise.
        wchar_t temp[MAX_PATH]{};GetTempPathW(MAX_PATH,temp);
        NeuralStream stream;
        NeuralHost<HalfModel> host(temp,neural::Mapping::defaultName(),std::chrono::milliseconds(20));
        if(!until([&]{return stream.state()==neural::hostRunning;})){puts("Host never reached running state");return 1;}
        constexpr unsigned chunk=480,total=48000*4;
        std::vector<float> in(total),out(total),stereo(chunk*2);
        for(unsigned n=0;n<total;++n)in[n]=0.2f*std::sin(2*3.14159265f*300.f*n/48000.f);
        const auto start=now100ns();
        std::uint64_t warmMisses=0;int warmResets=0;
        for(unsigned pos=0,k=0;pos<total;pos+=chunk,++k){
            const auto time=start+static_cast<std::uint64_t>(pos)*10000000ull/48000;
            while(now100ns()<time)Sleep(1);
            if((pos>=48000&&pos<96000)||pos>=144000){
                for(unsigned i=0;i<chunk;++i)stereo[i*2]=stereo[i*2+1]=0.1f*std::sin(2*3.14159265f*700.f*(pos+i)/48000.f);
                stream.reference(stereo.data(),chunk,2,time,false);
            }
            stream.microphone(in.data()+pos,out.data()+pos,chunk,1,1,time+(k%2?15000:0),false,true);
            if(pos==24000){warmMisses=stream.misses();warmResets=HalfModel::resets.load();}
        }
        auto ratio=[&](unsigned from,unsigned to){double a=0,b=0;for(unsigned n=from;n<to;++n){const double x=in[n-NeuralStream::latencyFrames];a+=x*x;b+=out[n]*(double)out[n];}return b/a;};
        const double idle=ratio(30000,48000),playing=ratio(60000,96000),gap=ratio(105000,144000),resumed=ratio(150000,total);
        const auto misses=stream.misses()-warmMisses;const int resets=HalfModel::resets.load()-warmResets;
        printf("idle/reference gaps: idle=%.3f playing=%.3f gap=%.3f resumed=%.3f misses=%llu resets=%d\n",idle,playing,gap,resumed,static_cast<unsigned long long>(misses),resets);
        for(double r:{idle,playing,gap,resumed})if(r<.15||r>.35){puts("Output switched to raw audio while the speaker was idle or the reference paused");return 1;}
        if(misses>2*NeuralStream::hop){puts("Processed output was missing while the speaker was idle");return 1;}
        if(resets!=0){puts("Timestamp jitter or a reference gap reset the model");return 1;}
    }
    puts("Speaker idle, reference gap and timestamp jitter passed");
    {
        // Through the host: an echo 40 ms behind the reference timestamps is measured and the
        // reference is rendered 27 ms later (13 ms margin), published for diagnostics.
        wchar_t temp[MAX_PATH]{};GetTempPathW(MAX_PATH,temp);
        NeuralStream stream;
        std::atomic<int> saved{-1};
        NeuralHost<HalfModel> host(temp,neural::Mapping::defaultName(),std::chrono::milliseconds(20),{},-1,[&](int us){saved=us;});
        if(!until([&]{return stream.state()==neural::hostRunning;})){puts("Host never reached running state");return 1;}
        neural::Mapping view;view.open(neural::Mapping::defaultName(),false);
        neural::Channel* channel=nullptr;for(auto& c:view.get()->channels)if(c.owner.load())channel=&c;
        constexpr unsigned chunk=480,total=48000*6,echo=1920;
        std::mt19937 random(5);std::normal_distribution<float> gauss(0.f,0.1f);
        std::vector<float> ref(total),mic(total),out(total),stereo(chunk*2);
        for(unsigned n=0;n<total;++n)ref[n]=n%129600<72000?gauss(random):0.f; // 1.5 s playing, 1.2 s silence
        for(unsigned n=echo;n<total;++n)mic[n]=0.3f*ref[n-echo];
        const auto start=now100ns();
        for(unsigned pos=0;pos<total;pos+=chunk){
            const auto time=start+static_cast<std::uint64_t>(pos)*10000000ull/48000;
            while(now100ns()<time)Sleep(1);
            for(unsigned i=0;i<chunk;++i)stereo[i*2]=stereo[i*2+1]=ref[pos+i];
            stream.reference(stereo.data(),chunk,2,time,false);
            stream.microphone(mic.data()+pos,out.data()+pos,chunk,1,1,time,false,true);
        }
        const int delayUs=channel?channel->echoDelayUs.load():-2,shiftUs=channel?channel->referenceShiftUs.load():-2;
        printf("host echo delay: %d us, reference shift %d us, misses=%llu\n",delayUs,shiftUs,static_cast<unsigned long long>(stream.misses()));
        if(std::abs(delayUs-40000)>200||std::abs(shiftUs-27000)>200){puts("Host did not align the reference to the measured echo delay");return 1;}
        if(std::abs(saved.load()-40000)>200){puts("Measured echo delay was not handed over for the next start");return 1;}
    }
    {
        // 2026-10-24: a stuck MOTU delivered half the samples its real-time timestamps cover. Lined
        // up by those timestamps, its microphone matched every other 10 ms of the reference 40 ms
        // late; nothing may be measured from such a stream.
        wchar_t temp[MAX_PATH]{};GetTempPathW(MAX_PATH,temp);
        NeuralStream stream;
        NeuralHost<HalfModel> host(temp,neural::Mapping::defaultName(),std::chrono::milliseconds(20),{},-1);
        if(!until([&]{return stream.state()==neural::hostRunning;})){puts("Host never reached running state");return 1;}
        neural::Mapping view;view.open(neural::Mapping::defaultName(),false);
        neural::Channel* channel=nullptr;for(auto& c:view.get()->channels)if(c.owner.load())channel=&c;
        constexpr unsigned chunk=480,total=48000*6,echo=1920;
        std::mt19937 random(6);std::normal_distribution<float> gauss(0.f,0.1f);
        std::vector<float> ref(total),mic(total/2),out(total/2),stereo(chunk*2);
        for(auto& v:ref)v=gauss(random);
        // Microphone sample n is stamped with the time of reference sample seen(n).
        auto seen=[&](unsigned n){return ref[(n/chunk)*2*chunk+n%chunk];};
        for(unsigned n=echo;n<total/2;++n)mic[n]=0.3f*seen(n-echo);
        const auto start=now100ns();
        for(unsigned pos=0;pos<total;pos+=chunk){
            const auto time=start+static_cast<std::uint64_t>(pos)*10000000ull/48000;
            while(now100ns()<time)Sleep(1);
            for(unsigned i=0;i<chunk;++i)stereo[i*2]=stereo[i*2+1]=ref[pos+i];
            stream.reference(stereo.data(),chunk,2,time,false);
            if(pos/chunk%2==0)stream.microphone(mic.data()+pos/2,out.data()+pos/2,chunk,1,1,time,false,true);
        }
        const int delayUs=channel?channel->echoDelayUs.load():-2;
        printf("half-rate microphone: echo delay %d us\n",delayUs);
        if(delayUs!=-1){puts("An echo delay was measured from a microphone whose timestamps disagree with its samples");return 1;}
    }
    {
        // A remembered delay applies from the first block of the next stream.
        NeuralStream stream;
        wchar_t temp[MAX_PATH]{};GetTempPathW(MAX_PATH,temp);
        NeuralHost<HalfModel> host(temp,neural::Mapping::defaultName(),std::chrono::milliseconds(20),{},40000);
        if(!until([&]{return stream.state()==neural::hostRunning;})){puts("Host never reached running state");return 1;}
        neural::Mapping view;view.open(neural::Mapping::defaultName(),false);
        neural::Channel* channel=nullptr;for(auto& c:view.get()->channels)if(c.owner.load())channel=&c;
        std::vector<float> in(480),out(480);const auto start=now100ns();
        for(unsigned k=0;k<10;++k){while(now100ns()<start+k*100000ull)Sleep(1);stream.microphone(in.data(),out.data(),480,1,1,start+k*100000ull,false,true);}
        if(!until([&]{return channel&&channel->referenceShiftUs.load()==27000;},1000)){puts("Remembered echo delay was not applied at stream start");return 1;}
    }
    puts("Host echo delay alignment passed");
}
