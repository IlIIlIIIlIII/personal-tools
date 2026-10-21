// Offline replay of recorded NPU echo-cancellation input (PersonalToolsAecWatch record files)
// through the same signal path as NeuralHost, with the reference rendered D ms earlier.
// Reports the echo delay of the recording and per-100 ms echo reduction for each D.
// Usage: PersonalToolsAecReplay <npu dir> <record prefix incl. -chN> <out dir> [D ms ... | host]
// "host [delay us]" instead replays in real time through NeuralStream + NeuralHost (production
// code), optionally starting from a remembered echo delay.
#include "NeuralStream.hpp"
#include "NeuralHost.hpp"
#include <algorithm>
#include <cstdio>
#include <numbers>
#include <string>
#include <vector>

using namespace personaltools::win;
using personaltools::StereoReferenceTimeline;
namespace {
struct Fir{
    std::array<float,63>h{},history{};unsigned pos=0;
    Fir(){double sum=0;for(int k=0;k<63;++k){double x=k-31.;double s=x==0?0.30:std::sin(std::numbers::pi*0.30*x)/(std::numbers::pi*x);h[k]=static_cast<float>(s*(0.54-0.46*std::cos(2*std::numbers::pi*k/62)));sum+=h[k];}for(auto& v:h)v/=static_cast<float>(sum);}
    float tick(float x){history[pos]=x;float y=0;for(unsigned k=0;k<63;++k)y+=h[k]*history[(pos+63-k)%63];pos=(pos+1)%63;return y;}
};
std::vector<neural::Packet> load(const std::string& path){
    std::vector<neural::Packet> packets;FILE* f=std::fopen(path.c_str(),"rb");if(!f)return packets;
    neural::Packet p;while(std::fread(&p,sizeof(p),1,f)==1)packets.push_back(p);std::fclose(f);return packets;
}
void writeWav(const std::string& path,const std::vector<float>& samples){
    FILE* f=std::fopen(path.c_str(),"wb");if(!f)return;
    const std::uint32_t bytes=static_cast<std::uint32_t>(samples.size()*4),riff=36+bytes,rate=48000,byteRate=rate*4,fmtSize=16;const std::uint16_t format=3,channels=1,align=4,bits=32;
    std::fwrite("RIFF",1,4,f);std::fwrite(&riff,4,1,f);std::fwrite("WAVEfmt ",1,8,f);std::fwrite(&fmtSize,4,1,f);std::fwrite(&format,2,1,f);std::fwrite(&channels,2,1,f);
    std::fwrite(&rate,4,1,f);std::fwrite(&byteRate,4,1,f);std::fwrite(&align,2,1,f);std::fwrite(&bits,2,1,f);std::fwrite("data",1,4,f);std::fwrite(&bytes,4,1,f);
    std::fwrite(samples.data(),4,samples.size(),f);std::fclose(f);
}
}
int main(int argc,char** argv){
    if(argc<4){std::puts("usage: PersonalToolsAecReplay <npu dir> <record prefix> <out dir> [D ms ...]");return 2;}
    const std::string prefix=argv[2],outDir=argv[3];
    auto mic=load(prefix+"-mic.bin");auto ref=load(prefix+"-ref.bin");
    if(mic.empty()){std::puts("{\"error\":\"no microphone packets\"}");return 3;}
    std::sort(mic.begin(),mic.end(),[](auto& a,auto& b){return a.index<b.index;});
    mic.erase(std::unique(mic.begin(),mic.end(),[](auto& a,auto& b){return a.index==b.index;}),mic.end());
    std::sort(ref.begin(),ref.end(),[](auto& a,auto& b){return a.time<b.time;});
    ref.erase(std::unique(ref.begin(),ref.end(),[](auto& a,auto& b){return a.time==b.time;}),ref.end());
    std::vector<double> shifts;for(int i=4;i<argc;++i)shifts.push_back(std::atof(argv[i]));
    if(shifts.empty())shifts={0};
    const auto first=mic.front().index;
    std::vector<float> raw(mic.back().index+mic.back().frames-first);
    for(const auto& p:mic)std::copy_n(p.samples.data(),p.frames,raw.begin()+static_cast<std::ptrdiff_t>(p.index-first));
    writeWav(outDir+"\\raw.wav",raw);
    // Reference rendered on the microphone timeline at offset -D (ms); gaps filled with silence like the host.
    auto renderReference=[&](double shiftMs){
        StereoReferenceTimeline timeline;std::vector<float> out(raw.size());std::uint64_t end=0;size_t next=0;
        static constexpr std::array<float,neural::packetFrames> silence{};
        std::array<float,neural::packetFrames> l{},r{};
        const auto shift=static_cast<std::uint64_t>(shiftMs*10000);
        for(const auto& p:mic){
            // Feed every reference packet that the host could have had by this packet (time+30 ms).
            while(next<ref.size()&&ref[next].time<=p.time+300000){
                const auto& q=ref[next++];
                if(end&&q.time>end+50000){
                    const auto gap=std::min<std::uint64_t>((q.time-end)*48000/10000000,48000);
                    for(auto k=gap/neural::packetFrames*neural::packetFrames;k>0;k-=neural::packetFrames)
                        timeline.push(silence.data(),silence.data(),neural::packetFrames,48000,q.time-(k*10000000+24000)/48000,10000000);
                }
                timeline.push(q.samples.data(),q.samples.data(),q.frames,48000,q.time,10000000);end=q.time+(q.frames*10000000ull+24000)/48000;
            }
            if(timeline.render(p.time-shift,10000000,l.data(),r.data(),p.frames))
                std::copy_n(l.data(),p.frames,out.begin()+static_cast<std::ptrdiff_t>(p.index-first));
        }
        return out;
    };
    // Echo delay: cross-correlation of the microphone with the unshifted reference, 0-300 ms.
    const auto ref0=renderReference(0);
    double best=-1;int bestLag=0;std::vector<double> corr(301);
    for(int lagMs=0;lagMs<=300;++lagMs){
        const int lag=lagMs*48;double xy=0,xx=0,yy=0;
        for(size_t n=lag;n<raw.size();n+=3){xy+=raw[n]*static_cast<double>(ref0[n-lag]);xx+=ref0[n-lag]*static_cast<double>(ref0[n-lag]);yy+=raw[n]*static_cast<double>(raw[n]);}
        corr[lagMs]=xy/std::sqrt(xx*yy+1e-30);if(std::abs(corr[lagMs])>best){best=std::abs(corr[lagMs]);bestLag=lagMs;}
    }
    // Refine around the coarse peak at sample resolution.
    int fine=bestLag*48;double fineBest=-1;
    for(int lag=std::max(0,bestLag*48-60);lag<=bestLag*48+60;++lag){
        double xy=0;for(size_t n=lag;n<raw.size();n+=2)xy+=raw[n]*static_cast<double>(ref0[n-lag]);
        if(std::abs(xy)>fineBest){fineBest=std::abs(xy);fine=lag;}
    }
    std::printf("{\"samples\":%zu,\"micPackets\":%zu,\"refPackets\":%zu,\"echoDelayMs\":%.2f,\"correlation\":%.3f,\"runs\":[",raw.size(),mic.size(),ref.size(),fine/48.0,best);
    constexpr unsigned modelDelay=1152+62,hop=384,window=4800;
    bool firstRun=true;
    // Echo reduction per 100 ms window where the (delay-aligned) reference is active.
    auto evaluate=[&](const char* name,double shift,const std::vector<float>& out){
        std::vector<std::pair<double,double>> erle;double refMax=0;std::vector<double> refEnergy;
        for(size_t w=0;w+window+fine<raw.size();w+=window/2){double e=0;for(size_t n=w;n<w+window;++n)e+=ref0[n]*static_cast<double>(ref0[n]);refEnergy.push_back(e);refMax=std::max(refMax,e);}
        for(size_t k=0,w=0;w+window+fine<raw.size();w+=window/2,++k){
            if(refEnergy[k]<refMax*0.03)continue;
            double a=0,b=0;for(size_t n=w+fine;n<w+fine+window;++n){a+=raw[n]*static_cast<double>(raw[n]);b+=out[n]*static_cast<double>(out[n]);}
            erle.push_back({(w+fine)/48000.0,10*std::log10((a+1e-12)/(b+1e-12))});
        }
        std::vector<double> sorted;for(auto& e:erle)sorted.push_back(e.second);std::sort(sorted.begin(),sorted.end());
        auto worst=erle;std::sort(worst.begin(),worst.end(),[](auto& x,auto& y){return x.second<y.second;});
        std::printf("%s{\"run\":\"%s\",\"shiftMs\":%.1f,\"windows\":%zu,\"medianDb\":%.1f,\"p10Db\":%.1f,\"minDb\":%.1f,\"under15dB\":%lld,\"worst\":[",firstRun?"":",",name,shift,sorted.size(),
            sorted.empty()?0:sorted[sorted.size()/2],sorted.empty()?0:sorted[sorted.size()/10],sorted.empty()?0:sorted[0],
            static_cast<long long>(std::count_if(sorted.begin(),sorted.end(),[](double x){return x<15;})));
        for(size_t i=0;i<std::min<size_t>(5,worst.size());++i)std::printf("%s[%.2f,%.1f]",i?",":"",worst[i].first,worst[i].second);
        std::printf("]}");std::fflush(stdout);firstRun=false;
    };
    // The live host output recorded by the watcher: results[k] holds the output for microphone sample k-modelDelay.
    if(FILE* f=std::fopen((prefix+"-out.bin").c_str(),"rb")){
        std::uint64_t start=0;std::vector<float> live(raw.size());float v=0;
        if(std::fread(&start,sizeof(start),1,f)==1)for(std::uint64_t idx=start;std::fread(&v,sizeof(v),1,f)==1;++idx)
            if(idx>=first+modelDelay&&idx-modelDelay-first<live.size())live[idx-modelDelay-first]=v;
        std::fclose(f);evaluate("live",0,live);
    }
    if(argc>4&&std::string(argv[4])=="host"){
        // End to end through the production code: the recorded packets are re-timed and pushed in
        // real time into a NeuralStream (APO side) served by an in-process NeuralHost<DtlnNpu>.
        const std::wstring section=L"Local\\PersonalToolsNeural-replay-"+std::to_wstring(GetCurrentProcessId());
        NeuralStream stream(section);
        NeuralHost<> host(std::filesystem::absolute(argv[1]),section,std::chrono::milliseconds(1000),
            [](const std::string& message){std::fprintf(stderr,"host: %s\n",message.c_str());},argc>5?std::atoi(argv[5]):-1);
        for(int i=0;i<1000&&stream.state()!=neural::hostRunning;++i)Sleep(10);
        LARGE_INTEGER q{},fq{};QueryPerformanceFrequency(&fq);
        auto now=[&]{QueryPerformanceCounter(&q);return static_cast<std::uint64_t>(static_cast<long double>(q.QuadPart)*10000000/fq.QuadPart);};
        const auto offset=now()+2000000-mic.front().time;
        struct Event{std::uint64_t release;const neural::Packet* packet;bool reference;};
        std::vector<Event> events;
        for(const auto& p:mic)events.push_back({p.time+offset+100000,&p,false}); // APO callback ~10 ms after capture
        for(const auto& r:ref)events.push_back({r.time+offset+150000,&r,true});  // loopback forwarded ~15 ms later
        std::stable_sort(events.begin(),events.end(),[](auto& a,auto& b){return a.release<b.release;});
        std::vector<float> out(raw.size()+4096);
        for(const auto& e:events){
            while(now()<e.release)Sleep(1);
            if(e.reference)stream.reference(e.packet->samples.data(),e.packet->frames,1,e.packet->time+offset,false);
            else stream.microphone(e.packet->samples.data(),out.data()+(e.packet->index-first),e.packet->frames,1,1,e.packet->time+offset,false,true);
        }
        std::vector<float> aligned(raw.size());for(size_t n=0;n<raw.size();++n)aligned[n]=out[n+NeuralStream::latencyFrames];
        writeWav(outDir+"\\host.wav",aligned);
        evaluate("host",0,aligned);
        std::puts("]}");
        return 0;
    }
    if(argc>4&&std::string(argv[4])=="delays"){
        // Echo delay estimator trace on the recording (reference shifted by whole 48 kHz samples).
        writeWav(outDir+"\\reference.wav",ref0);
        EchoDelayEstimator echo;Fir micFir,refFir;int evaluations=0;std::vector<float> lastMic,lastRef;bool dumped=false;
        for(size_t n=0;n<raw.size();++n){
            const auto shift48=static_cast<size_t>(echo.shift()*3);
            const float a=micFir.tick(raw[n]),b=refFir.tick(n>=shift48?ref0[n-shift48]:0.f);
            if(n%3)continue;
            lastMic.push_back(a);lastRef.push_back(b);
            if(lastMic.size()>EchoDelayEstimator::window){lastMic.erase(lastMic.begin());lastRef.erase(lastRef.begin());}
            echo.push(a,b);const bool changed=echo.update();
            if(echo.evaluations()!=evaluations){
                evaluations=echo.evaluations();
                if(!dumped&&echo.lastLag()==0&&echo.lastRatio()>20){
                    dumped=true;FILE* f=std::fopen((outDir+"\\lag0-window.bin").c_str(),"wb");
                    std::fwrite(lastMic.data(),4,lastMic.size(),f);std::fwrite(lastRef.data(),4,lastRef.size(),f);std::fclose(f);
                }
                std::fprintf(stderr,"%.2fs lag=%.2fms ratio=%.1f delay=%.2fms shift=%.2fms%s\n",n/48000.0,echo.lastLag()/16.0,echo.lastRatio(),echo.delay()/16.0,echo.shift()/16.0,changed?" CHANGED":"");
            }
        }
        std::puts("]}");
        return 0;
    }
    personaltools::win::DtlnNpu model;model.open(argv[1]);
    for(const double shift:shifts){
        model.reset();Fir micFir,refFir,outFir;PlaybackOnset onset;const auto reference=shift==0?ref0:renderReference(shift);
        std::vector<float> out(raw.size());std::array<float,128> m{},f{},o{};unsigned count=0;std::uint64_t processed=0;
        for(size_t n=0;n<raw.size();++n){
            const float a=micFir.tick(raw[n]),b=refFir.tick(reference[n]);
            if(n%3==0){m[count]=a;f[count]=b;++count;}
            if(count==128){
                if(onset.resumes(f.data()))model.resetState();
                model.process(m.data(),f.data(),o.data());
                onset.processed(m.data(),o.data());
                for(unsigned j=0;j<hop;++j){const float v=outFir.tick(j%3==0?o[j/3]*3.f:0.f);if(processed+j>=modelDelay)out[processed+j-modelDelay]=v;}
                processed+=hop;count=0;
            }
        }
        writeWav(outDir+"\\out-"+std::to_string(static_cast<int>(shift))+"ms.wav",out);
        evaluate("replay",shift,out);
    }
    std::puts("]}");
}
