#pragma once
// A capture stream that stopped delivering real input. From 2026-10-24 the MOTU M Series input
// broke in Call of Duty's loading hiccups and never came back by itself: it repeated its last 512
// frames bit for bit (or delivered zeros) while its position ran on a ~15.6 ms timer, so packet
// timestamps spanned 1.5-2x the time their samples cover. Only a device restart (or replug) helped.
// Fed with every microphone packet (48 kHz sample index, 100 ns timestamp, samples), it flags:
// - frozen: 1 s of samples each equal to the sample one driver buffer earlier (a few common
//   sizes), with the value changing somewhere in it. Real input never repeats exactly; found
//   about 1 s in. Silence and constant codes (a muted input) do not count.
// - timing: two consecutive 3 s windows over 1.25x (or under 0.8x). Normal streams cover their
//   timestamps to within 0.1 %; one system stall (~0.5 s) moves a single window to about 1.17x.
//   This also catches a device that delivers zeros at the wrong rate.
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <string>

namespace personaltools::win {
class MicTiming {
public:
    static constexpr std::uint64_t windowTime=30000000; // 3 s in 100 ns units
    static constexpr double fast=1.25,slow=0.8;
    static constexpr int windowsNeeded=2;
    static constexpr std::uint64_t frozenFrames=48000; // 1 s
    // Driver buffer sizes (frames) a stuck device may repeat; 512 on 2026-10-24.
    static constexpr std::array<unsigned,9> lags{128,256,384,480,512,960,1024,1920,2048};
    enum class Reason{none,timing,frozen};
    void reset(){started_=false;offWindows_=0;stalled_=false;lastRatio_=1;reason_=Reason::none;restartSamples();}
    // Returns true when the stream has just become stalled. Without samples only the timestamps count.
    // A stream whose input comes back (new samples, timestamps in step again) is no longer stalled.
    bool add(std::uint64_t index,std::uint64_t time,unsigned frames,const float* samples=nullptr){
        // A break in the sample sequence or time running backwards starts the measurement over.
        if(!started_||index!=next_||time<windowStart_){
            started_=true;windowIndex_=index;windowStart_=time;next_=index+frames;offWindows_=0;stalled_=false;reason_=Reason::none;
            restartSamples();
            if(samples)feed(frames,samples);
            return false;
        }
        next_=index+frames;
        Reason found=samples&&feed(frames,samples)?Reason::frozen:Reason::none;
        const auto span=time-windowStart_;
        if(span>=windowTime&&index!=windowIndex_){
            const double covered=static_cast<double>(index-windowIndex_)*10000000.0/48000.0;
            lastRatio_=static_cast<double>(span)/covered;
            windowIndex_=index;windowStart_=time;
            offWindows_=(lastRatio_>fast||lastRatio_<slow)?offWindows_+1:0;
        }
        if(found==Reason::none&&offWindows_>=windowsNeeded)found=Reason::timing;
        const bool was=stalled_;stalled_=found!=Reason::none;
        if(!stalled_)reason_=Reason::none;
        if(!stalled_||was)return false;
        reason_=found;
        return true;
    }
    bool stalled()const{return stalled_;}
    Reason reason()const{return reason_;}
    double lastRatio()const{return lastRatio_;} // timestamp span / sample span of the last window
    // For the log and the stall event, e.g. "samples repeat every 512 frames".
    std::string describe()const{
        char text[96];
        if(reason_==Reason::frozen)std::snprintf(text,sizeof(text),"samples repeat every %u frames",frozenLag_);
        else std::snprintf(text,sizeof(text),"packet timestamps span %.6fx the time their samples cover",lastRatio_);
        return text;
    }
private:
    static constexpr std::size_t historySize=4096; // power of two above the longest lag
    void restartSamples(){filled_=0;lastChange_=0;runs_.fill(0);}
    // Returns true while the samples are frozen.
    bool feed(unsigned frames,const float* samples){
        for(unsigned i=0;i<frames;++i){
            const float x=samples[i];
            for(std::size_t k=0;k<lags.size();++k)
                runs_[k]=filled_>=lags[k]&&history_[(filled_-lags[k])&(historySize-1)]==x?runs_[k]+1:0;
            if(filled_==0||history_[(filled_-1)&(historySize-1)]!=x)lastChange_=filled_+1;
            history_[filled_&(historySize-1)]=x;++filled_;
        }
        for(std::size_t k=0;k<lags.size();++k)
            if(runs_[k]>=frozenFrames&&lastChange_>filled_-runs_[k]){frozenLag_=lags[k];return true;}
        return false;
    }
    bool started_=false,stalled_=false;int offWindows_=0;Reason reason_=Reason::none;
    std::uint64_t next_=0,windowIndex_=0,windowStart_=0;double lastRatio_=1;
    std::array<float,historySize> history_{};std::array<std::uint64_t,lags.size()> runs_{};
    std::uint64_t filled_=0,lastChange_=0;unsigned frozenLag_=0; // lastChange_: 1-based position of the last sample unlike the one before
};
}
