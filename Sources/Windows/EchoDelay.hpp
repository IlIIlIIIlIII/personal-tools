#pragma once
// Echo delay behind the loopback reference timestamps (output buffering in the driver and
// DAC plus the acoustic path). DTLN-AEC cancels far better when the reference it sees
// leads the echo by only a little: on a USB DAC with a 35 ms delay, rendering the reference
// ~22 ms later cut the 100 ms windows with under 15 dB of echo reduction from 100 to 20.
// GCC-PHAT on the 16 kHz model inputs, averaged over successive half-second windows while
// the speaker plays; a delay is adopted only after three consistent estimates. Moving the
// reference disturbs the model's recurrent state for ~300 ms even inside a pause between
// words, so a new shift waits for a full second without playback (or one minute), and the
// host seeds the delay remembered for the output device.
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
    static constexpr int rate=16000,window=8192,size=16384,maxLag=4000,minLag=-1600,interval=8000;
    // Reference kept 13 ms ahead of the echo onset: over five room recordings (34-36 ms delay),
    // 20-25 ms of shift left the fewest weak windows; at the delay itself cancellation collapses.
    static constexpr int marginSamples=208;
    EchoDelayEstimator():mic_(window),ref_(window),average_(size),work_(size),other_(size){}
    // Clears the signal history but keeps an adopted delay (it belongs to the output device).
    void restart(){std::fill(mic_.begin(),mic_.end(),0.f);std::fill(ref_.begin(),ref_.end(),0.f);pos_=0;count_=0;filled_=0;clearAverage();}
    // A delay measured earlier for the same output device (16 kHz samples); applied at once.
    void seed(int delay){if(delay<0)return;delay_=delay;shift_=target_=std::max(0,delay-marginSamples);clearAverage();}
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
        }
        averaged_=true;
        work_=average_;fft(work_,true);
        // Lag l > 0: the microphone follows the reference by l samples. Negative lags catch a
        // reference that was shifted past the echo (for example after an output device change).
        int best=0;float peak=-1;double sum=0;int n=0;
        for(int l=minLag;l<=maxLag;++l){const float v=std::abs(work_[(l+size)%size].real());sum+=v;++n;if(v>peak){peak=v;best=l;}}
        lastLag_=best;lastRatio_=static_cast<float>(peak*n/(sum+1e-30));
        if(peak<8*sum/n)return false;
        const int delay=best+shift_;
        if(std::abs(delay-candidate_)<=16)++hits_;else{candidate_=delay;hits_=1;}
        if(hits_<3)return false;
        delay_=candidate_;
        const int shift=std::max(0,delay_-marginSamples);
        if(std::abs(shift-shift_)<32){target_=shift_;pending_=0;return false;} // ignore changes below 2 ms
        if(shift!=target_){target_=shift;pending_=0;}
        return false;
    }
    int delay()const{return delay_;}          // 16 kHz samples, -1 until known
    int shift()const{return shift_;}          // 16 kHz samples the reference is rendered later
    std::uint64_t shift100ns()const{return static_cast<std::uint64_t>(shift_)*625;}
    // Diagnostics of the latest evaluated window.
    int lastLag()const{return lastLag_;}
    float lastRatio()const{return lastRatio_;}
    int evaluations()const{return evaluations_;}
private:
    void clearAverage(){averaged_=false;hits_=0;candidate_=-100000;}
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
    int pos_=0,count_=0,filled_=0,candidate_=-100000,hits_=0,delay_=-1,shift_=0,target_=0,quiet_=0,pending_=0,lastLag_=0,evaluations_=0;
    float lastRatio_=0;bool averaged_=false;
};
}
