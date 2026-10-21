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

int main(){
    {
        using personaltools::win::EchoDelayEstimator;
        // Playback in 1.5 s parts with 1.2 s silences (where a new shift may be applied).
        std::mt19937 random(3);std::normal_distribution<float> gauss(0.f,0.1f);
        std::vector<float> ref(16000*16);for(size_t n=0;n<ref.size();++n)ref[n]=n%43200<24000?gauss(random):0.f;
        std::vector<float> steady(16000*16);for(size_t n=0;n<steady.size();++n)steady[n]=n%19200<16000?gauss(random):0.f;
        {
            // Pauses between words are not enough: the shift waits instead of moving the reference mid-playback.
            EchoDelayEstimator e;runDelay(e,steady,592,0,16000*8,0.003f);
            if(e.delay()<0||e.shift()!=0){puts("Reference moved while it was playing");return 1;}
        }
        EchoDelayEstimator e;
        runDelay(e,ref,592,0,16000*6,0.003f); // 37 ms, like the USB DAC measured on 2026-10-20
        if(std::abs(e.delay()-592)>2||std::abs(e.shift()-(592-EchoDelayEstimator::marginSamples))>2){printf("echo delay %d shift %d\n",e.delay(),e.shift());puts("Echo delay was not found");return 1;}
        if(runDelay(e,ref,592,16000*6,16000*10,0.003f)!=0){puts("A stable echo delay moved the reference again");return 1;}
        // A faster output device: the reference is now shifted past the echo and must come back.
        runDelay(e,ref,200,16000*10,16000*16,0.003f);
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
