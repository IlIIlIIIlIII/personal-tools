#pragma once
// A capture stream whose packet timestamps no longer match its samples. On 2026-10-24/25 the
// MOTU M Series input broke after Call of Duty's freezes: it went on delivering 2/3 (later 1/2)
// of the samples with real-time stamps and a repeating buffer, and only a device restart (or
// replug) brought it back. Normal streams cover their timestamps to within 0.1 % (ratio 1.000).
// Fed with every microphone packet (48 kHz sample index, 100 ns timestamp); the stream counts as
// stalled after two consecutive 3 s windows whose timestamps span over 1.25x (or under 0.8x) the
// time their samples cover. One system stall (~0.5 s) moves a single window to about 1.17x.
#include <cstdint>

namespace personaltools::win {
class MicTiming {
public:
    static constexpr std::uint64_t windowTime=30000000; // 3 s in 100 ns units
    static constexpr double fast=1.25,slow=0.8;
    static constexpr int windowsNeeded=2;
    void reset(){started_=false;offWindows_=0;stalled_=false;lastRatio_=1;}
    // Returns true when the stream has just become stalled.
    bool add(std::uint64_t index,std::uint64_t time,unsigned frames){
        // A break in the sample sequence or time running backwards starts the measurement over.
        if(!started_||index!=next_||time<windowStart_){
            started_=true;windowIndex_=index;windowStart_=time;next_=index+frames;offWindows_=0;stalled_=false;
            return false;
        }
        next_=index+frames;
        const auto span=time-windowStart_;
        if(span<windowTime||index==windowIndex_)return false;
        const double covered=static_cast<double>(index-windowIndex_)*10000000.0/48000.0;
        lastRatio_=static_cast<double>(span)/covered;
        windowIndex_=index;windowStart_=time;
        offWindows_=(lastRatio_>fast||lastRatio_<slow)?offWindows_+1:0;
        const bool was=stalled_;stalled_=offWindows_>=windowsNeeded;
        return stalled_&&!was;
    }
    bool stalled()const{return stalled_;}
    double lastRatio()const{return lastRatio_;} // timestamp span / sample span of the last window
private:
    bool started_=false,stalled_=false;int offWindows_=0;
    std::uint64_t next_=0,windowIndex_=0,windowStart_=0;double lastRatio_=1;
};
}
