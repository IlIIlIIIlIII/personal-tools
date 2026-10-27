#pragma once
// Runs in PersonalToolsNpuHost.exe (or a test), never in audiodg. One worker per
// shared channel owns its own DTLN-AEC model on the NPU and retries with backoff
// when the runtime cannot be loaded or inference fails.
#include "NeuralIpc.hpp"
#include "RealtimeThread.hpp"
#include "EchoDelay.hpp"
#include "MicTiming.hpp"
#include "DtlnNpu.hpp"
#include "AEC.hpp"
#include <algorithm>
#include <bit>
#include <chrono>
#include <functional>
#include <memory>
#include <numbers>
#include <thread>
#include <vector>

namespace personaltools::win {
// Near-end sound during a pause in playback (speech, typing) can leave the model's recurrent
// state passing the microphone through, and the next playback then leaked for up to a second
// (2026-10-20 recordings: 2-10 dB instead of ~40 dB in its first half second). When the
// reference resumes after a quiet second in which the model removed less than 12 dB, the
// playback starts from a clean state. A state that was still suppressing (recent playback) is
// kept: clearing it unconditionally cost up to ~30 dB at such onsets. Works on the 16 kHz hops.
class PlaybackOnset {
public:
    static constexpr unsigned quietHops=125; // 1 s
    // Before the hop is processed; true when the model state should be cleared.
    bool resumes(const float* reference){
        float energy=0;for(unsigned k=0;k<128;++k)energy+=reference[k]*reference[k];
        quiet_=energy<128e-9f; // below -90 dBFS: loopback silence, or no reference at all
        const bool clear=!quiet_&&run_>=quietHops&&output_>input_*.063; // -12 dB
        run_=quiet_?run_+1:0;
        return clear;
    }
    // After the same hop: how much the model removes while the speaker is quiet (~0.3 s average).
    void processed(const float* microphone,const float* output){
        if(!quiet_)return;
        double in=0,out=0;
        for(unsigned k=0;k<128;++k){in+=microphone[k]*static_cast<double>(microphone[k]);out+=output[k]*static_cast<double>(output[k]);}
        input_+=.03*(in-input_);output_+=.03*(out-output_);
    }
private:
    unsigned run_=0;bool quiet_=false;double input_=0,output_=0;
};
// The same stuck state without a silent pause: the reference stays faintly on (-57 dBFS between
// a call partner's sentences, quiet passages in a video), which the NPU's FP16 inference barely
// registers at this microphone level, so the model sits passing the microphone. When playback then
// jumps 15 dB above its recent level the echo went through nearly unattenuated for 0.3-1.3 s.
// A cleared state cancels within ~0.3 s on the NPU. Only a model removing less than 12 dB is
// cleared, at most once a second and not in a stream's first second (the level average is still
// settling). Clearing more often (10 dB jumps) leaked less but cut more near-end speech; see
// docs/measurements/aec-jump-2026-10-21.md.
class PlaybackJump {
public:
    static constexpr unsigned minimumGapHops=125; // 1 s
    // Before the hop is processed; true when the model state should be cleared.
    bool jumps(const float* reference){
        double energy=0;for(unsigned k=0;k<128;++k)energy+=reference[k]*static_cast<double>(reference[k]);
        const bool clear=energy>128e-6&&energy>average_*31.6&&output_>input_*.063&&since_>=minimumGapHops; // -60 dBFS, +15 dB, -12 dB
        average_+=(energy-average_)/32; // ~0.25 s
        since_=clear?0:since_+1;
        return clear;
    }
    // After the same hop: how much the model removes overall (~0.3 s average).
    void processed(const float* microphone,const float* output){
        double in=0,out=0;
        for(unsigned k=0;k<128;++k){in+=microphone[k]*static_cast<double>(microphone[k]);out+=output[k]*static_cast<double>(output[k]);}
        input_+=.03*(in-input_);output_+=.03*(out-output_);
    }
private:
    double average_=0,input_=0,output_=0;unsigned since_=0;
};
template<class Model=DtlnNpu>
class NeuralHost {
public:
    static constexpr unsigned latencyFrames=4096, hop=384, packetFrames=neural::packetFrames, ringSize=neural::ringSize;
    static constexpr unsigned modelDelay=1152+62;
    static constexpr std::chrono::milliseconds maxRetryDelay{30000};
    using Log=std::function<void(const std::string&)>;
    using DelayChanged=std::function<void(int microseconds)>;
    using Stalled=std::function<void(const std::string& reason)>;
    // initialDelayUs: echo delay measured earlier for the current output device (-1 = unknown);
    // delayChanged is told when a channel adopts a different one, to keep it for next time.
    // stalled is told when a microphone stream stops delivering real input (MicTiming.hpp), and
    // again every minute while it stays that way, so the device can be restarted.
    NeuralHost(std::filesystem::path folder,std::wstring section=neural::Mapping::defaultName(),
               std::chrono::milliseconds retryDelay=std::chrono::milliseconds(1000),Log log={},
               int initialDelayUs=-1,DelayChanged delayChanged={},Stalled stalled={})
        :folder_(std::move(folder)),section_(std::move(section)),retryDelay_(retryDelay),log_(std::move(log)),
         delayChanged_(std::move(delayChanged)),stalled_(std::move(stalled)),knownDelay_(initialDelayUs<0?-1:initialDelayUs*16/1000),supervisor_([this]{supervise();}){}
    ~NeuralHost(){quit_.store(true);if(supervisor_.joinable())supervisor_.join();}
    NeuralHost(const NeuralHost&)=delete;
    bool attached()const{return attached_.load();}
private:
    struct Fir{
        std::array<float,63>h{},history{};unsigned pos=0;
        Fir(){double sum=0;for(int k=0;k<63;++k){double x=k-31.;double s=x==0?0.30:std::sin(std::numbers::pi*0.30*x)/(std::numbers::pi*x);h[k]=static_cast<float>(s*(0.54-0.46*std::cos(2*std::numbers::pi*k/62)));sum+=h[k];}for(auto& v:h)v/=static_cast<float>(sum);}
        float tick(float x){history[pos]=x;float y=0;for(unsigned k=0;k<63;++k)y+=h[k]*history[(pos+63-k)%63];pos=(pos+1)%63;return y;}
        void reset(){history.fill(0);pos=0;}
    };
    // Per-stream signal path; recreated whenever the APO claims the channel again.
    struct Pipeline{
        StereoReferenceTimeline timeline;Fir micFir,refFir,outFir;PlaybackOnset onset;PlaybackJump jump;
        std::array<float,packetFrames>left{},right{};std::array<float,128>mic{},ref{},out{};
        std::uint64_t expected=0,base=0,processed=0,referenceEnd=0;unsigned count=0;bool unreferenced=false;
        // Microphone time expected for `stampIndex`, following slow clock drift (see step()).
        std::uint64_t stampTime=0,stampIndex=0;bool stamped=false;
        MicTiming timing;
    };
    void report(const std::string& message){if(log_)try{log_(message);}catch(...){}}
    bool pause(std::chrono::milliseconds delay,neural::Channel* channel=nullptr){
        const auto until=std::chrono::steady_clock::now()+delay;
        while(!quit_.load()){
            if(channel)channel->hostTick.store(GetTickCount64(),std::memory_order_release);
            if(std::chrono::steady_clock::now()>=until)return true;
            Sleep(20);
        }
        return false;
    }
    void supervise()noexcept{
        neural::Mapping mapping;
        // The audio engine creates the section when a microphone stream opens.
        while(!quit_.load()&&!mapping.open(section_,false))if(!pause(std::chrono::milliseconds(500)))return;
        if(quit_.load())return;
        attached_.store(true);
        mapping.get()->hostPid.store(GetCurrentProcessId());
        report("attached to shared section");
        std::vector<std::thread> workers;
        for(auto& channel:mapping.get()->channels)workers.emplace_back([this,&channel]{work(channel);});
        for(auto& worker:workers)worker.join();
    }
    void work(neural::Channel& c)noexcept{
        RealtimeThread realtime;RealtimeThread::Timer timer;std::unique_ptr<Model> model;auto pipeline=std::make_unique<Pipeline>();
        // Outlives streams: the delay belongs to the output device, not to one capture stream.
        auto echo=std::make_unique<EchoDelayEstimator>();
        std::uint32_t generation=0;auto delay=retryDelay_;std::uint64_t healthySince=0;
        LARGE_INTEGER frequency{};QueryPerformanceFrequency(&frequency);
        while(!quit_.load()){
            c.hostTick.store(GetTickCount64(),std::memory_order_release);
            if(c.owner.load(std::memory_order_acquire)==0){Sleep(20);continue;}
            const auto current=c.generation.load(std::memory_order_acquire);
            if(current!=generation){
                generation=current;pipeline=std::make_unique<Pipeline>();echo->restart();
                // Start aligned with what this or another channel already measured for the device.
                if(const int known=knownDelay_.load();known>=0&&known!=echo->delay())echo->seed(known);
                if(model)model->reset();
                c.hostGeneration.store(generation,std::memory_order_release);
            }
            if(!model){
                try{
                    c.state.store(neural::hostOpening);
                    auto opened=std::make_unique<Model>();opened->open(folder_);
                    model=std::move(opened);pipeline=std::make_unique<Pipeline>();
                    discard(c);c.state.store(neural::hostRunning);healthySince=c.completed.load();
                    report("NPU model ready");
                }catch(const NpuFailure& e){if(!fail(c,static_cast<int>(e.stage),e.what(),delay))return;continue;}
                catch(const std::exception& e){if(!fail(c,static_cast<int>(NpuStage::unknown),e.what(),delay))return;continue;}
                catch(...){if(!fail(c,static_cast<int>(NpuStage::unknown),"unknown",delay))return;continue;}
            }
            try{
                if(!step(c,*model,*pipeline,*echo,generation,frequency))timer.wait(1000);
                // About 10 s of healthy inference resets the backoff.
                if(c.completed.load()-healthySince>=1250)delay=retryDelay_;
            }
            catch(const NpuFailure& e){model.reset();if(!fail(c,static_cast<int>(e.stage),e.what(),delay))return;}
            catch(const std::exception& e){model.reset();if(!fail(c,static_cast<int>(NpuStage::unknown),e.what(),delay))return;}
            catch(...){model.reset();if(!fail(c,static_cast<int>(NpuStage::unknown),"unknown",delay))return;}
        }
    }
    bool fail(neural::Channel& c,int stage,const char* what,std::chrono::milliseconds& delay){
        c.failure.store(stage);c.state.store(neural::hostFailed);
        report("NPU failure stage="+std::to_string(stage)+": "+what);
        if(!pause(delay,&c))return false;
        delay=std::min(delay*2,std::chrono::milliseconds(maxRetryDelay));
        c.restarts.fetch_add(1);
        return true;
    }
    static void discard(neural::Channel& c){neural::Packet p;while(c.reference.peek(p))c.reference.pop();while(c.microphone.peek(p))c.microphone.pop();}
    // Loopback delivers nothing while the speaker is idle. Fill up to 1 s of such a gap with
    // silence so that microphone time just before playback resumes maps to silence rather
    // than to the end of the previous playback (the timeline ring itself has no gaps).
    static void pushReference(Pipeline& s,const neural::Packet& r){
        static constexpr std::array<float,packetFrames> silence{};
        if(s.referenceEnd&&r.time>s.referenceEnd+50000){
            const auto gap=std::min<std::uint64_t>((r.time-s.referenceEnd)*48000/10000000,48000);
            for(auto k=gap/packetFrames*packetFrames;k>0;k-=packetFrames)
                s.timeline.push(silence.data(),silence.data(),packetFrames,48000,r.time-(k*10000000+24000)/48000,10000000);
        }
        s.timeline.push(r.samples.data(),r.samples.data(),r.frames,48000,r.time,10000000);
        s.referenceEnd=r.time+(r.frames*10000000ull+24000)/48000;
    }
    // Returns false when no microphone packet could be processed yet.
    bool step(neural::Channel& c,Model& model,Pipeline& s,EchoDelayEstimator& echo,std::uint32_t generation,const LARGE_INTEGER& frequency){
        neural::Packet r,p;
        while(c.reference.peek(r)){if(r.generation==generation)pushReference(s,r);c.reference.pop();}
        if(!c.microphone.peek(p))return false;
        if(p.generation!=generation){c.microphone.pop();return true;}
        LARGE_INTEGER qpc{};QueryPerformanceCounter(&qpc);
        const auto now=static_cast<std::uint64_t>(static_cast<long double>(qpc.QuadPart)*10000000/frequency.QuadPart);
        if(p.time+160000>now)return false; // allow loopback packets to arrive
        // WASAPI can deliver the last loopback packet after the mic packet; wait within the
        // latency budget. After that the speaker is idle (or the reference is late) and the
        // packet is processed against silence: the output stays processed instead of switching
        // to raw microphone audio, which carries the echo tail and a ~20 dB higher noise floor.
        // The reference is rendered later by the measured echo delay minus a margin (EchoDelay.hpp).
        const bool available=s.timeline.render(p.time-echo.shift100ns(),10000000,s.left.data(),s.right.data(),p.frames);
        if(!available&&p.time+300000>now)return false;
        c.microphone.pop();
        // Only a break in the microphone sample sequence realigns the output. Timestamp jitter
        // and reference gaps need no model reset: the reference is rendered from each p.time.
        if(p.index!=s.expected){
            model.reset();s.micFir.reset();s.refFir.reset();s.outFir.reset();s.count=0;s.processed=0;s.base=p.index;s.stamped=false;
        }
        s.expected=p.index+p.frames;
        // A microphone whose timestamps disagree with its sample count cannot be lined up with the
        // reference (2026-10-24: a stuck MOTU delivered half the samples with real-time stamps, and
        // the echo delay jumped between 0 and 250 ms). Packets more than 5 ms off the drift-following
        // expectation (normally within 3 ms) restart the delay measurement's window.
        if(s.stamped){
            const auto expectedTime=static_cast<std::int64_t>(s.stampTime)+static_cast<std::int64_t>((p.index-s.stampIndex)*10000000ull/48000);
            const auto deviation=static_cast<std::int64_t>(p.time)-expectedTime;
            if(deviation>50000||deviation<-50000){echo.restart();s.stampTime=p.time;}
            else s.stampTime=static_cast<std::uint64_t>(expectedTime+deviation/16);
        }else{s.stampTime=p.time;s.stamped=true;}
        s.stampIndex=p.index;
        if(s.timing.add(p.index,p.time,p.frames,p.samples.data()))report("microphone stalled: "+s.timing.describe());
        if(s.timing.stalled()){
            std::uint64_t last=lastStall_.load();const std::uint64_t now=GetTickCount64();
            if(stalled_&&(last==0||now-last>=60000)&&lastStall_.compare_exchange_strong(last,now))try{stalled_(s.timing.describe());}catch(...){}
        }
        s.unreferenced=s.unreferenced||!available;
        for(unsigned i=0;i<p.frames;++i){
            const float m=s.micFir.tick(p.samples[i]),f=s.refFir.tick(available?s.left[i]:0.f);
            if((p.index+i-s.base)%3==0){s.mic[s.count]=m;s.ref[s.count]=f;++s.count;echo.push(m,f);}
            if(s.count==128){
                const bool resumed=s.onset.resumes(s.ref.data()),jumped=s.jump.jumps(s.ref.data());
                if(resumed||jumped)model.resetState();
                model.process(s.mic.data(),s.ref.data(),s.out.data());
                s.onset.processed(s.mic.data(),s.out.data());s.jump.processed(s.mic.data(),s.out.data());
                for(unsigned j=0;j<hop;++j){
                    const float value=s.outFir.tick(j%3==0?s.out[j/3]*3.f:0.f);
                    const auto idx=s.base+s.processed+j;
                    if(s.processed+j>=modelDelay){
                        auto& slot=c.results[idx%ringSize];
                        slot.stamp.store(0,std::memory_order_release);
                        slot.sample.store(std::bit_cast<std::uint32_t>(value),std::memory_order_relaxed);
                        slot.stamp.store(neural::stamp(generation,idx),std::memory_order_release);
                    }
                }
                c.completed.fetch_add(1);if(s.unreferenced)c.unreferenced.fetch_add(1,std::memory_order_relaxed);
                s.processed+=hop;s.count=0;s.unreferenced=!available;
                // After the results are out: the estimate runs an FFT every half second.
                if(echo.update())report("echo delay "+std::to_string(echo.delay()*1000/16)+" us, reference shift "+std::to_string(echo.shift()*1000/16)+" us");
                if(const int known=knownDelay_.load();echo.delay()>=0&&(known<0||std::abs(echo.delay()-known)>=EchoDelayEstimator::deadBand)){
                    knownDelay_.store(echo.delay()); // estimates wander by a few ms; keep only real changes
                    if(delayChanged_)try{delayChanged_(echo.delay()*1000/16);}catch(...){}
                }
                c.echoDelayUs.store(echo.delay()<0?-1:echo.delay()*1000/16,std::memory_order_relaxed);
                c.referenceShiftUs.store(echo.shift()*1000/16,std::memory_order_relaxed);
            }
        }
        return true;
    }
    std::filesystem::path folder_;std::wstring section_;std::chrono::milliseconds retryDelay_;Log log_;DelayChanged delayChanged_;Stalled stalled_;
    std::atomic<int> knownDelay_; // 16 kHz samples, shared by the channels
    std::atomic<std::uint64_t> lastStall_{0}; // GetTickCount64 of the last stalled_ call, shared by the channels
    std::atomic<bool> quit_{false},attached_{false};
    // Constructed last: the supervisor must not observe uninitialized members.
    std::thread supervisor_;
};
}
