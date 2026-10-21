#pragma once
#include "DtlnNpu.hpp"
#include <atomic>
#include <bit>
#include <chrono>
#include <mutex>
#include <thread>
#include <spa/support/thread.h>

namespace personaltools::linuxaudio {
// A single callback supplies already-aligned microphone and sink-monitor samples.
// A bounded SPSC queue carries them to inference; no model work happens on RT.
class NeuralProcessor {
public:
    static constexpr unsigned latencyFrames=4096, modelDelay=1152+62;
    static constexpr unsigned packetFrames=256, queueSize=128, ringSize=32768;
    NeuralProcessor(std::filesystem::path models,std::filesystem::path runtime,spa_thread_utils* threadUtils=nullptr)
        :models_(std::move(models)),runtime_(std::move(runtime)),threadUtils_(threadUtils){
        if(threadUtils_){
            const spa_dict_item items[]={{SPA_KEY_THREAD_NAME,"personal-tools-npu"}};
            const spa_dict properties=SPA_DICT_INIT_ARRAY(items);
            managedWorker_=spa_thread_utils_create(threadUtils_,&properties,[](void* data)->void*{
                auto& self=*static_cast<NeuralProcessor*>(data);
                while(!self.workerCreated_.load(std::memory_order_acquire))std::this_thread::yield();
                self.run(self.models_,self.runtime_);return nullptr;
            },this);
            if(!managedWorker_)throw std::runtime_error("Cannot create NPU inference worker");
            workerCreated_.store(true,std::memory_order_release);
        }else worker_=std::thread([this]{run(models_,runtime_);});
    }
    ~NeuralProcessor(){quit_=true;if(managedWorker_)spa_thread_utils_join(threadUtils_,managedWorker_,nullptr);if(worker_.joinable())worker_.join();}
    int state()const{return state_.load(std::memory_order_relaxed);}
    std::string error()const{std::lock_guard guard(errorMutex_);return error_;}
    std::uint64_t completed()const{return completed_.load();}
    bool usingNpu()const{return usingNpu_.load();}
    std::uint64_t dropped()const{return dropped_.load();}
    std::uint64_t misses()const{return misses_.load();}
    std::uint64_t maximumInferenceNs()const{return maxInferenceNs_.load();}
    std::uint64_t discontinuities()const{return discontinuities_.load();}
    bool recentlyDegraded()const{return healthyFrames_.load(std::memory_order_relaxed)<48000;}
    bool realtime()const{return realtime_.load(std::memory_order_relaxed);}
    void submit(const float* microphone,const float* reference,unsigned frames,
                std::uint64_t time,bool available){
        for(unsigned off=0;off<frames;off+=packetFrames){
            auto w=write_.load(std::memory_order_relaxed);
            if(w-read_.load(std::memory_order_acquire)>=queueSize){dropped_++;break;}
            auto& p=queue_[w%queueSize];p.frames=std::min(packetFrames,frames-off);
            p.index=position_+off;p.time=time+(off*10000000ull+24000)/48000;p.available=available;
            for(unsigned i=0;i<p.frames;++i){p.mic[i]=microphone[off+i];p.ref[i]=reference[off+i];}
            write_.store(w+1,std::memory_order_release);
        }
    }
    // fallback is independently processed DSP audio with the same 4096-frame delay.
    float output(float fallback,bool enabled){
        float wet=lastWet_;bool found=false;
        if(position_>=latencyFrames){
            const auto target=position_-latencyFrames+modelDelay;
            const auto& slot=results_[target%ringSize];
            const auto stamp=slot.stamp.load(std::memory_order_acquire);
            if(stamp==target+1){
                wet=std::bit_cast<float>(slot.sample.load(std::memory_order_relaxed));
                found=slot.stamp.load(std::memory_order_acquire)==stamp;
            }
        }
        if(found)lastWet_=wet;
        const float target=enabled&&state()==2&&found?1.f:0.f;
        usingNpu_.store(target>0,std::memory_order_relaxed);
        blend_+=std::clamp(target-blend_,-1.f/240.f,1.f/240.f);
        if(enabled&&position_>=latencyFrames&&!found){misses_++;healthyFrames_.store(0,std::memory_order_relaxed);}
        else if(found&&healthyFrames_.load(std::memory_order_relaxed)<48000)healthyFrames_.fetch_add(1,std::memory_order_relaxed);
        ++position_;return fallback+(lastWet_-fallback)*blend_;
    }
private:
    struct Packet{std::uint64_t index=0,time=0;unsigned frames=0;bool available=false;std::array<float,packetFrames>mic{},ref{};};
    struct Result{std::atomic<std::uint64_t>stamp{0};std::atomic<std::uint32_t>sample{0};};
    std::array<Packet,queueSize>queue_{};
    std::array<Result,ringSize>results_{};
    alignas(64)std::atomic<std::uint64_t>write_{0};
    alignas(64)std::atomic<std::uint64_t>read_{0};
    std::uint64_t position_=0;float lastWet_=0,blend_=0;
    std::atomic<bool>usingNpu_{false};
    std::atomic<bool>quit_{false};std::atomic<int>state_{0};
    std::atomic<std::uint64_t>completed_{0},dropped_{0},misses_{0},maxInferenceNs_{0},discontinuities_{0};
    std::atomic<unsigned>healthyFrames_{0};
    std::atomic<bool>realtime_{false};
    mutable std::mutex errorMutex_;std::string error_;
    struct Fir{
        std::array<float,63>h{},history{};unsigned pos=0;
        Fir(){double sum=0;for(int k=0;k<63;++k){double x=k-31.;double s=x==0?.30:std::sin(std::numbers::pi*.30*x)/(std::numbers::pi*x);h[k]=static_cast<float>(s*(.54-.46*std::cos(2*std::numbers::pi*k/62)));sum+=h[k];}for(auto& v:h)v/=static_cast<float>(sum);}
        float tick(float x){history[pos]=x;float y=0;for(unsigned k=0;k<63;++k)y+=h[k]*history[(pos+63-k)%63];pos=(pos+1)%63;return y;}
        void reset(){history.fill(0);pos=0;}
    };
    void run(const std::filesystem::path& models,const std::filesystem::path& runtime)noexcept{
        try{
            state_=1;DtlnNpu model;model.open(models,runtime);
            // Compilation and its helper threads stay at normal priority. Only
            // the bounded inference loop requests RTKit priority 10, below the
            // PipeWire graph callbacks. Failure retains honest ordinary scheduling.
            if(threadUtils_)realtime_=spa_thread_utils_acquire_rt(threadUtils_,managedWorker_,10)>=0;
            state_=2;
            Fir micFir,refFir,outFir;std::array<float,128>mic{},ref{},out{};
            std::uint64_t expected=0,expectedTime=0,base=0,processed=0;unsigned count=0;bool good=true;
            while(!quit_.load()){
                const auto r=read_.load(std::memory_order_relaxed);
                if(r==write_.load(std::memory_order_acquire)){std::this_thread::sleep_for(std::chrono::milliseconds(1));continue;}
                const auto p=queue_[r%queueSize];read_.store(r+1,std::memory_order_release);
                const auto drift=p.time>expectedTime?p.time-expectedTime:expectedTime-p.time;
                if(p.index!=expected||(expectedTime&&drift>10000)){
                    ++discontinuities_;
                    model.reset();micFir.reset();refFir.reset();outFir.reset();count=0;processed=0;base=p.index;good=true;
                }
                expected=p.index+p.frames;expectedTime=p.time+(p.frames*10000000ull+24000)/48000;
                for(unsigned i=0;i<p.frames;++i){
                    const float m=micFir.tick(p.mic[i]),f=refFir.tick(p.available?p.ref[i]:0.f);good=good&&p.available;
                    if((p.index+i-base)%3==0){mic[count]=m;ref[count]=f;++count;}
                    if(count==128){
                        const auto start=std::chrono::steady_clock::now();model.process(mic.data(),ref.data(),out.data());
                        const auto ns=std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now()-start).count();
                        auto old=maxInferenceNs_.load();while(static_cast<std::uint64_t>(ns)>old&&!maxInferenceNs_.compare_exchange_weak(old,ns)){}
                        for(unsigned j=0;j<384;++j){
                            const float value=outFir.tick(j%3==0?out[j/3]*3.f:0.f);const auto idx=base+processed+j;
                            if(good&&processed+j>=modelDelay){auto& s=results_[idx%ringSize];s.stamp.store(0,std::memory_order_release);s.sample.store(std::bit_cast<std::uint32_t>(value),std::memory_order_relaxed);s.stamp.store(idx+1,std::memory_order_release);}
                        }
                        ++completed_;processed+=384;count=0;good=true;
                    }
                }
            }
        }catch(const std::exception& e){std::lock_guard guard(errorMutex_);error_=e.what();state_=-1;}
        catch(...){std::lock_guard guard(errorMutex_);error_="Unknown NPU worker error";state_=-1;}
    }
    std::filesystem::path models_,runtime_;
    spa_thread_utils* threadUtils_=nullptr;
    spa_thread* managedWorker_=nullptr;
    std::atomic<bool>workerCreated_{false};
    std::thread worker_; // last: every field is initialized before the worker starts
};
}
