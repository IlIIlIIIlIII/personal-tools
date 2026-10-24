#pragma once
// Echo delay behind the loopback reference timestamps (output buffering in the driver and
// DAC plus the acoustic path). DTLN-AEC cancels far better when the reference it sees
// leads the echo by only a little: on a USB DAC with a 35 ms delay, rendering the reference
// ~22 ms later cut the 100 ms windows with under 15 dB of echo reduction from 100 to 20.
// GCC-PHAT on the 16 kHz model inputs, averaged over successive half-second windows while
// the speaker plays; a delay is adopted only after three consistent estimates. Moving the
// reference disturbs the model's recurrent state for ~300 ms even inside a pause between
// words, so a correction of up to 10 ms waits for a full second without playback (or one
// minute); a larger one, including the first measurement, is applied at once. The host seeds
// the delay remembered for the output device.
// Delays are searched as absolute delays (0-250 ms) whatever the current shift. The search
// used to be relative to the shifted reference, so in a Discord call whose partner sent the
// user's voice back ~190 ms later, peaks of that loop moved the shift step by step from 35 ms
// to 1.4 s, where the real echo was out of reach and cancellation stopped (2026-10-24,
// docs/measurements/aec-delay-runaway-2026-10-24.md).
#include <algorithm>
#include <array>
#include <cmath>
#include <complex>
#include <cstdint>
#include <numbers>
#include <vector>

namespace personaltools::win {
class EchoDelayEstimator {
public:
    static constexpr int rate=16000,window=8192,size=16384,interval=8000;
    // Longest echo delay accepted (16 kHz samples, 250 ms); also the limit for a remembered one.
    static constexpr int maxDelay=4000;
    // Microphone ahead of the reference by up to 400 ms: the reference carries the microphone back.
    static constexpr int returnRange=6400;
    // Reference kept 13 ms ahead of the echo onset: over five room recordings (34-36 ms delay),
    // 20-25 ms of shift left the fewest weak windows; at the delay itself cancellation collapses.
    static constexpr int marginSamples=208;
    // Shift changes below 3 ms are ignored: two arrivals 2.2 ms apart used to swap the shift every
    // few minutes, and each move disturbs the model.
    static constexpr int deadBand=48;
    // Leaving an adopted delay by more than 10 ms needs three of the last eight clear windows at
    // the new delay (not necessarily in a row) and the long-term correlation to be clearly
    // stronger there than at the adopted delay. That correlation averages the windows' cross
    // spectra in absolute delay coordinates over about 10 s of playback: single windows peak at
    // random delays while the user talks or the call loops, but the echo path keeps its phase and
    // is what remains. A seeded delay is kept for the first 20 evaluated windows either way.
    static constexpr int moveDistance=160,moveEvidence=20;
    static constexpr float moveMargin=1.25f;
    EchoDelayEstimator():mic_(window),ref_(window),average_(size),work_(size),other_(size),history_(size){recent_.fill(-100000);}
    // Clears the signal history but keeps an adopted delay (it belongs to the output device).
    void restart(){std::fill(mic_.begin(),mic_.end(),0.f);std::fill(ref_.begin(),ref_.end(),0.f);pos_=0;count_=0;filled_=0;clearAverage();}
    // A delay measured earlier for the same output device (16 kHz samples); applied at once.
    void seed(int delay){if(delay<0||delay>maxDelay)return;delay_=delay;shift_=target_=std::max(0,delay-marginSamples);clearAverage();}
    // Microphone and the reference exactly as given to the model, i.e. already shifted by shift().
    void push(float mic,float ref){
        mic_[pos_]=mic;ref_[pos_]=ref;pos_=(pos_+1)%window;++count_;filled_=std::min(filled_+1,window);
        quiet_=std::abs(ref)<1e-4f?quiet_+1:0;if(target_!=shift_)++pending_;
    }
    // Call after push; returns true when the applied shift changed.
    bool update(){
        if(target_!=shift_&&(quiet_>=rate||pending_>=60*rate)){shift_=target_;pending_=0;clearAverage();return true;}
        if(count_<interval||filled_<window)return false;
        count_=0;
        double em=0,er=0;for(int i=0;i<window;++i){em+=mic_[i]*static_cast<double>(mic_[i]);er+=ref_[i]*static_cast<double>(ref_[i]);}
        // Speaker playing (reference above -60 dBFS) and something reaching the microphone.
        if(er/window<1e-6||em/window<1e-9)return false;
        // Near-end speech dominates when the microphone is more than 15 dB above the reference
        // (2026-10-24 call: talking +25..+35 dB, echo alone within ±3 dB). Such windows peak at
        // the voice coming back from the call or at random delays, never reliably at the echo.
        if(em>31.6*er){++nearEnd_;return false;}
        ++evaluations_;
        // Tapered: the cut edges of both windows otherwise correlate at lag 0, which outweighed
        // the echo where playback started or stopped inside the window.
        for(int i=0;i<size;++i){
            const float taper=i<window?static_cast<float>(0.5-0.5*std::cos(2*std::numbers::pi*i/(window-1))):0.f;
            work_[i]=i<window?taper*ref_[(pos_+i)%window]:0.f;other_[i]=i<window?taper*mic_[(pos_+i)%window]:0.f;
        }
        fft(work_,false);fft(other_,false);
        for(int k=0;k<size;++k){
            auto c=std::conj(work_[k])*other_[k];const float m=std::abs(c);
            c=m>1e-20f?c/m:std::complex<float>{};
            average_[k]=averaged_?0.6f*average_[k]+0.4f*c:c;
            other_[k]=c; // this window alone, for the long-term average
        }
        averaged_=true;
        work_=average_;fft(work_,true);
        // Correlation where the microphone follows the unshifted reference by `delay` samples. The
        // reference given here is already shifted, so a shift past the echo shows as a negative lag.
        const auto at=[&](int delay){return std::abs(work_[((delay-shift_)%size+size)%size].real());};
        float peak=0;double sum=0;
        for(int d=0;d<=maxDelay;++d){const float v=at(d);sum+=v;peak=std::max(peak,v);}
        const auto mean=static_cast<float>(sum/(maxDelay+1));
        lastRatio_=peak/(mean+1e-30f);
        // A call partner (or a monitor) playing the microphone back: the microphone then matches the
        // reference best ahead of it, and the loop's repeats pose as longer echo delays.
        float back=0;
        for(int d=std::max(-returnRange,shift_-(window-512));d<0;++d)back=std::max(back,at(d));
        if(back>peak){++returned_;hits_=0;candidate_=-100000;return false;}
        {   // Rotated by the shift (a delay of `shift` samples) into absolute delay coordinates.
            const std::complex<double> step=std::polar(1.0,-2*std::numbers::pi*shift_/size);std::complex<double> rotation(1,0);
            for(int k=0;k<size;++k){history_[k]+=0.05f*(other_[k]*std::complex<float>(rotation)-history_[k]);rotation*=step;}
            profiled_=std::min(profiled_+1,moveEvidence);
        }
        if(peak<=8*mean)return false;
        // The echo onset: the earliest clear arrival at least 60 % as strong as the strongest one,
        // at its own maximum. Room reflections and loop repeats come later.
        const float level=std::max(0.6f*peak,8*mean);
        int best=0;
        while(best<maxDelay&&at(best)<level)++best;
        while(best<maxDelay&&at(best+1)>at(best))++best;
        lastLag_=best-shift_;
        const int delay=best;
        recent_[recentPos_++%recent_.size()]=delay;
        if(delay_>=0&&std::abs(delay-delay_)>moveDistance){
            // Clear windows are sparse while the user talks; one stray peak must not reset the count.
            int votes=0;for(const int r:recent_)votes+=std::abs(r-delay)<=48;
            if(votes<3||!longTermPrefers(delay))return false;
            recent_.fill(-100000);delay_=delay;
        }else{
            if(std::abs(delay-candidate_)<=16)++hits_;else{candidate_=delay;hits_=1;}
            if(hits_<3)return false;
            delay_=candidate_;
        }
        const int shift=std::max(0,delay_-marginSamples);
        if(std::abs(shift-shift_)<deadBand){target_=shift_;pending_=0;return false;}
        // A reference more than 10 ms off barely cancels anything: move it now instead of waiting
        // for a pause, the model's ~300 ms of settling costs less.
        if(std::abs(shift-shift_)>moveDistance){shift_=target_=shift;pending_=0;clearAverage();return true;}
        // Estimates wander by a millisecond or two; that must not restart the one-minute wait.
        if(std::abs(shift-target_)>=deadBand)pending_=0;
        target_=shift;
        return false;
    }
    int delay()const{return delay_;}          // 16 kHz samples, -1 until known
    int shift()const{return shift_;}          // 16 kHz samples the reference is rendered later
    std::uint64_t shift100ns()const{return static_cast<std::uint64_t>(shift_)*625;}
    // Diagnostics of the latest evaluated window.
    int lastLag()const{return lastLag_;}
    float lastRatio()const{return lastRatio_;}
    int evaluations()const{return evaluations_;}
    int returned()const{return returned_;}   // evaluations skipped: the reference carried the microphone back
    int nearEnd()const{return nearEnd_;}     // windows skipped: near-end sound far above the reference
private:
    void clearAverage(){averaged_=false;hits_=0;candidate_=-100000;}
    // Whether the long-term correlation shows a clear arrival at `delay` that is
    // stronger than the one at the adopted delay. Overwrites work_.
    bool longTermPrefers(int delay){
        if(profiled_<moveEvidence)return false;
        work_=history_;fft(work_,true);
        const auto at=[&](int d){return std::abs(work_[d].real());};
        double sum=0;for(int d=0;d<=maxDelay;++d)sum+=at(d);
        const auto mean=static_cast<float>(sum/(maxDelay+1));
        // Within 3 ms: arrivals 2 ms apart (34 and 36 ms here) merge in the long-term average.
        const auto around=[&](int centre){float v=0;for(int d=std::max(0,centre-48);d<=std::min(maxDelay,centre+48);++d)v=std::max(v,at(d));return v;};
        // Noise alone peaks near 3.3 times the mean over these 4001 delays.
        const float candidate=around(delay);
        return candidate>5*mean&&candidate>=moveMargin*around(delay_);
    }
    static void fft(std::vector<std::complex<float>>& data,bool inverse){
        const int n=static_cast<int>(data.size());
        for(int i=1,j=0;i<n;++i){int bit=n>>1;for(;j&bit;bit>>=1)j^=bit;j^=bit;if(i<j)std::swap(data[i],data[j]);}
        for(int length=2;length<=n;length<<=1){
            const double angle=2*std::numbers::pi/length*(inverse?1:-1);
            const std::complex<double> step(std::cos(angle),std::sin(angle));
            for(int i=0;i<n;i+=length){
                std::complex<double> w(1,0);
                for(int k=0;k<length/2;++k){const auto u=data[i+k],v=data[i+k+length/2]*std::complex<float>(w);data[i+k]=u+v;data[i+k+length/2]=u-v;w*=step;}
            }
        }
        if(inverse)for(auto& x:data)x/=static_cast<float>(n);
    }
    std::vector<float> mic_,ref_;std::vector<std::complex<float>> average_,work_,other_;
    std::vector<std::complex<float>> history_; // long-term cross spectrum, absolute delay coordinates
    std::array<int,8> recent_{}; // latest clear estimates (16 kHz samples)
    unsigned recentPos_=0;
    int pos_=0,count_=0,filled_=0,candidate_=-100000,hits_=0,delay_=-1,shift_=0,target_=0,quiet_=0,pending_=0,lastLag_=0,evaluations_=0,returned_=0,profiled_=0,nearEnd_=0;
    float lastRatio_=0;bool averaged_=false;
};
}
