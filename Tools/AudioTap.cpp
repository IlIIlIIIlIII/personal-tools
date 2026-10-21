// Records the selected speaker's WASAPI loopback (what Windows plays) and the selected
// microphone at the same time, then reports the speaker-to-microphone response at a few
// frequencies (Welch H1 estimate with coherence). Running it with the speaker EQ on and off
// while any app plays shows whether that app's sound goes through the EQ.
// Usage: PersonalToolsAudioTap <seconds> [prefix for loop.wav/mic.wav]
#include "Shared.hpp"
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>
#include <algorithm>
#include <cmath>
#include <complex>
#include <cstdio>
#include <numbers>
#include <string>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;
using namespace personaltools::win;
namespace {
void writeWav(const std::string& path,const std::vector<float>& s){
    FILE* f=std::fopen(path.c_str(),"wb");if(!f)return;
    const std::uint32_t bytes=static_cast<std::uint32_t>(s.size()*4),riff=36+bytes,rate=48000,byteRate=rate*4,fmt=16;const std::uint16_t format=3,ch=1,align=4,bits=32;
    std::fwrite("RIFF",1,4,f);std::fwrite(&riff,4,1,f);std::fwrite("WAVEfmt ",1,8,f);std::fwrite(&fmt,4,1,f);std::fwrite(&format,2,1,f);std::fwrite(&ch,2,1,f);
    std::fwrite(&rate,4,1,f);std::fwrite(&byteRate,4,1,f);std::fwrite(&align,2,1,f);std::fwrite(&bits,2,1,f);std::fwrite("data",1,4,f);std::fwrite(&bytes,4,1,f);
    std::fwrite(s.data(),4,s.size(),f);std::fclose(f);
}
// Shared-mode 48 kHz float stream (loopback of a render endpoint, or a capture endpoint); first channel or L+R mix.
bool record(const wchar_t* id,bool loopback,double seconds,std::vector<float>& out){
    ComPtr<IMMDeviceEnumerator> e;ComPtr<IMMDevice> d;ComPtr<IAudioClient> c;ComPtr<IAudioCaptureClient> cap;
    if(FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&e)))||FAILED(e->GetDevice(id,&d))||
       FAILED(d->Activate(__uuidof(IAudioClient),CLSCTX_ALL,nullptr,reinterpret_cast<void**>(c.GetAddressOf()))))return false;
    WAVEFORMATEX fmt{};fmt.wFormatTag=WAVE_FORMAT_IEEE_FLOAT;fmt.nChannels=2;fmt.nSamplesPerSec=48000;fmt.wBitsPerSample=32;fmt.nBlockAlign=8;fmt.nAvgBytesPerSec=384000;
    if(FAILED(c->Initialize(AUDCLNT_SHAREMODE_SHARED,(loopback?AUDCLNT_STREAMFLAGS_LOOPBACK:0)|AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM|AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
        2000000,0,&fmt,nullptr))||FAILED(c->GetService(IID_PPV_ARGS(&cap)))||FAILED(c->Start()))return false;
    const auto end=GetTickCount64()+static_cast<ULONGLONG>(seconds*1000);
    while(GetTickCount64()<end){
        UINT32 n=0;
        while(SUCCEEDED(cap->GetNextPacketSize(&n))&&n){
            BYTE* b=nullptr;UINT32 frames=0;DWORD flags=0;
            if(FAILED(cap->GetBuffer(&b,&frames,&flags,nullptr,nullptr)))break;
            const auto* x=reinterpret_cast<const float*>(b);
            for(UINT32 i=0;i<frames;++i)out.push_back((flags&AUDCLNT_BUFFERFLAGS_SILENT)?0.f:(loopback?0.5f*(x[i*2]+x[i*2+1]):x[i*2]));
            cap->ReleaseBuffer(frames);
        }
        Sleep(5);
    }
    c->Stop();return true;
}
void fft(std::vector<std::complex<double>>& a){
    const size_t n=a.size();
    for(size_t i=1,j=0;i<n;++i){size_t bit=n>>1;for(;j&bit;bit>>=1)j^=bit;j^=bit;if(i<j)std::swap(a[i],a[j]);}
    for(size_t len=2;len<=n;len<<=1){const std::complex<double> w(std::cos(-2*std::numbers::pi/len),std::sin(-2*std::numbers::pi/len));
        for(size_t i=0;i<n;i+=len){std::complex<double> v(1,0);for(size_t k=0;k<len/2;++k){auto u=a[i+k],t=a[i+k+len/2]*v;a[i+k]=u+t;a[i+k+len/2]=u-t;v*=w;}}}
}
}
int main(int argc,char** argv){
    const double seconds=argc>1?std::atof(argv[1]):8;const std::string prefix=argc>2?argv[2]:"";
    if(FAILED(CoInitializeEx(nullptr,COINIT_MULTITHREADED)))return 2;
    SharedFile settings;Configuration config;LONG revision=0;
    if(!settings.open()||!settings.read(config,revision)){std::puts("{\"error\":\"settings not found\"}");return 3;}
    std::vector<float> loop,mic;bool okLoop=false,okMic=false;
    std::thread t([&]{CoInitializeEx(nullptr,COINIT_MULTITHREADED);okLoop=record(config.renderId,true,seconds,loop);CoUninitialize();});
    okMic=record(config.captureId,false,seconds,mic);t.join();
    if(!okLoop||!okMic){std::printf("{\"error\":\"capture failed\",\"loopback\":%d,\"microphone\":%d}\n",okLoop,okMic);return 4;}
    if(!prefix.empty()){writeWav(prefix+"-loop.wav",loop);writeWav(prefix+"-mic.wav",mic);}
    // H1 = Sxy/Sxx per bin, 32768-point Hann segments (0.68 s, much longer than the ~40-200 ms path), 50% overlap.
    constexpr size_t n=32768;const size_t len=std::min(loop.size(),mic.size());
    std::vector<std::complex<double>> sxy(n/2),x(n),y(n);std::vector<double> sxx(n/2),syy(n/2);int segments=0;
    for(size_t s=0;s+n<=len;s+=n/2,++segments){
        for(size_t i=0;i<n;++i){const double h=0.5-0.5*std::cos(2*std::numbers::pi*i/(n-1));x[i]=loop[s+i]*h;y[i]=mic[s+i]*h;}
        fft(x);fft(y);
        for(size_t k=0;k<n/2;++k){sxy[k]+=std::conj(x[k])*y[k];sxx[k]+=std::norm(x[k]);syy[k]+=std::norm(y[k]);}
    }
    double loopPower=0;for(size_t i=0;i<len;++i)loopPower+=loop[i]*static_cast<double>(loop[i]);
    std::printf("{\"seconds\":%.1f,\"segments\":%d,\"loopbackDb\":%.1f,\"bands\":[",len/48000.0,segments,10*std::log10(loopPower/std::max<size_t>(len,1)+1e-14));
    const double bands[][2]={{60,72},{130,150},{350,450},{900,1100}};
    for(size_t b=0;b<4;++b){
        std::complex<double> xy;double xx=0,yy=0,coherence=0;int bins=0;
        for(size_t k=static_cast<size_t>(bands[b][0]*n/48000);k<=static_cast<size_t>(bands[b][1]*n/48000);++k){xy+=sxy[k];xx+=sxx[k];yy+=syy[k];coherence+=std::norm(sxy[k])/(sxx[k]*syy[k]+1e-30);++bins;}
        // Band response: power-weighted |H1| (sum of |Sxy| over sum of Sxx keeps phase drift across bins from cancelling).
        double magnitude=0;for(size_t k=static_cast<size_t>(bands[b][0]*n/48000);k<=static_cast<size_t>(bands[b][1]*n/48000);++k)magnitude+=std::abs(sxy[k]);
        std::printf("%s{\"from\":%.0f,\"to\":%.0f,\"responseDb\":%.1f,\"loopbackBandDb\":%.1f,\"coherence\":%.2f}",b?",":"",bands[b][0],bands[b][1],
            20*std::log10(magnitude/(xx+1e-30)+1e-12),10*std::log10(xx/std::max(segments,1)+1e-30),coherence/std::max(bins,1));
    }
    std::puts("]}");
    CoUninitialize();
}
