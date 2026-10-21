#pragma once
#include "NeuralIpc.hpp"
#include <algorithm>
#include <bit>
#include <cmath>

namespace personaltools::win {
// A missing result has no wet sample to crossfade from. Retain the last valid
// value during the short transition; substituting dry here bypasses the ramp.
class NeuralOutputMixer {
public:
    float process(float dry,float wet,bool available,bool enabled) {
        if(available)lastWet_=wet;
        const float aim=enabled&&available?1.f:0.f;
        blend_+=std::clamp(aim-blend_,-1.f/240.f,1.f/240.f);
        return dry+(lastWet_-dry)*blend_;
    }
private:
    float lastWet_=0.f,blend_=0.f;
};
// Audio-thread side of NPU echo cancellation. Packets go to the NPU host process
// through a claimed shared-memory channel; results come back keyed by sample index.
// Missing/late host output fades to latency-matched original microphone audio.
// Construction and destruction happen on LockForProcess/UnlockForProcess only.
class NeuralStream {
public:
    static constexpr unsigned latencyFrames=4096; // 85.3 ms, including NPU scheduling jitter.
    static constexpr unsigned hop=384, packetFrames=neural::packetFrames, ringSize=neural::ringSize;
    static constexpr unsigned modelDelay=1152+62; // 24 ms model + two 63-tap FIRs.
    explicit NeuralStream(const std::wstring& section=neural::Mapping::defaultName()) {
        if(!mapping_.open(section,true))return;
        static std::atomic<std::uint32_t> counter{0};
        token_=(static_cast<std::uint64_t>(GetCurrentProcessId())<<32)|(counter.fetch_add(1)+1);
        auto& channels=mapping_.get()->channels;
        // Second pass reclaims channels left by an audio engine that exited without releasing them.
        for(int pass=0;pass<2&&!channel_;++pass)for(auto& c:channels){
            auto owner=c.owner.load();
            const bool stale=pass==1&&owner!=0&&(owner>>32)!=GetCurrentProcessId()&&GetTickCount64()-c.apoTick.load()>5000;
            if((owner==0||stale)&&c.owner.compare_exchange_strong(owner,token_)){channel_=&c;break;}
        }
        if(!channel_)return;
        generation_=channel_->generation.fetch_add(1)+1;
        channel_->apoTick.store(GetTickCount64());
    }
    ~NeuralStream(){if(channel_){auto token=token_;channel_->owner.compare_exchange_strong(token,0);}}
    NeuralStream(const NeuralStream&)=delete;
    // 2 running, 1 preparing, 0 idle, -1 host retrying after a failure, -2 no NPU host.
    int state()const{
        if(!channel_)return neural::hostMissing;
        const auto host=channel_->hostTick.load(std::memory_order_acquire);
        if(host==0||GetTickCount64()-host>2000)return neural::hostMissing;
        if(channel_->hostGeneration.load(std::memory_order_acquire)!=generation_)return neural::hostOpening;
        return channel_->state.load(std::memory_order_acquire);
    }
    std::uint64_t misses()const{return misses_;}
    std::uint64_t completed()const{return channel_?channel_->completed.load(std::memory_order_relaxed):0;}
    long restarts()const{return channel_?channel_->restarts.load(std::memory_order_relaxed):0;}
    int failure()const{return channel_?channel_->failure.load(std::memory_order_relaxed):0;}
    bool connected()const{return channel_!=nullptr;}
    void reference(const float* input,unsigned frames,unsigned channels,std::uint64_t time,bool silent){
        if(!channel_)return;
        for(unsigned off=0;off<frames;off+=packetFrames){
            neural::Packet p;p.frames=std::min(packetFrames,frames-off);p.time=time+(off*10000000ull+24000)/48000;p.generation=generation_;
            for(unsigned i=0;i<p.frames;++i)p.samples[i]=silent?0.f:0.5f*(input[(off+i)*channels]+input[(off+i)*channels+channels-1]);
            if(!channel_->reference.push(p)){channel_->lost.fetch_add(1,std::memory_order_relaxed);break;}
        }
    }
    void microphone(const float* input,float* output,unsigned frames,unsigned channels,unsigned outChannels,std::uint64_t time,bool silent,bool enabled){
        const bool running=enabled&&state()==neural::hostRunning;
        if(channel_){
            channel_->apoTick.store(GetTickCount64(),std::memory_order_relaxed);
            for(unsigned off=0;off<frames;off+=packetFrames){
                neural::Packet p;p.frames=std::min(packetFrames,frames-off);p.time=time+(off*10000000ull+24000)/48000;p.index=position_+off;p.generation=generation_;
                for(unsigned i=0;i<p.frames;++i){const auto v=silent?0.f:input[(off+i)*channels];p.samples[i]=std::isfinite(v)?v:0.f;}
                if(!channel_->microphone.push(p))channel_->lost.fetch_add(1,std::memory_order_relaxed);
            }
        }
        unsigned missing=0;
        for(unsigned i=0;i<frames;++i){
            const auto index=position_+i;
            const float dry=dry_[index%latencyFrames];
            const float sample=silent?0.f:input[i*channels];dry_[index%latencyFrames]=std::isfinite(sample)?sample:0.f;
            float wet=dry;bool found=false;
            if(channel_&&index>=latencyFrames){
                const auto target=index-latencyFrames+modelDelay;
                const auto& s=channel_->results[target%ringSize];
                const auto expected=neural::stamp(generation_,target);
                if(s.stamp.load(std::memory_order_acquire)==expected){
                    const float value=std::bit_cast<float>(s.sample.load(std::memory_order_relaxed));
                    found=s.stamp.load(std::memory_order_acquire)==expected;
                    if(found)wet=value;
                }
            }
            const float value=mixer_.process(dry,wet,found,running);
            for(unsigned c=0;c<outChannels;++c)output[i*outChannels+c]=value;
            if(enabled&&!found&&index>=latencyFrames){++misses_;if(running)++missing;}
        }
        if(missing)channel_->apoMisses.fetch_add(missing,std::memory_order_relaxed); // running implies a channel
        position_+=frames;
    }
private:
    neural::Mapping mapping_;
    neural::Channel* channel_=nullptr;
    std::uint64_t token_=0;
    std::uint32_t generation_=0;
    std::array<float,latencyFrames>dry_{};std::uint64_t position_=0,misses_=0;NeuralOutputMixer mixer_;
};
}
