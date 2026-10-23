// Offline scan of a live AecWatch recording (no NPU): where did echo get through, and where did
// the processed output jump? The echo in the raw microphone is modelled per 30 s chunk with a
// linear room response estimated from the reference, so near-end speech and room noise (not
// correlated with playback) do not count as leaks. Raw and live output are the same moment, so
// their projections on that echo estimate give the suppression without an AEC-off recording.
// Usage: PersonalToolsAecLiveScan <record prefix incl. -chN[-gG]> <out dir> [snippets=12]
// Near-end windows (room sound well above the noise, little echo) also get a pitch strength, so the
// summary can tell how much voiced speech survives.
// Writes <out dir>\windows.csv (every 100 ms window), events.csv (merged problem spans) and
// snip-NN-<kind>-<seconds>.wav (4 channels: raw, output, reference, echo estimate; 48 kHz float).
#include "NeuralIpc.hpp"
#include "AEC.hpp"
#include <algorithm>
#include <cmath>
#include <complex>
#include <cstdio>
#include <cstdlib>
#include <numbers>
#include <string>
#include <vector>

using namespace personaltools::win;
using personaltools::StereoReferenceTimeline;
namespace {
constexpr unsigned rate=48000,modelDelay=1152+62,window=4800,step=2400,chunk=rate*30,irTaps=rate/5; // 200 ms room response
using cd=std::complex<double>;
void fft(std::vector<cd>& a,bool inverse){
    const size_t n=a.size();
    for(size_t i=1,j=0;i<n;++i){size_t bit=n>>1;for(;j&bit;bit>>=1)j^=bit;j^=bit;if(i<j)std::swap(a[i],a[j]);}
    for(size_t len=2;len<=n;len<<=1){
        const double ang=2*std::numbers::pi/static_cast<double>(len)*(inverse?1:-1);const cd wl(std::cos(ang),std::sin(ang));
        for(size_t i=0;i<n;i+=len){cd w(1);for(size_t k=0;k<len/2;++k){const cd u=a[i+k],v=a[i+k+len/2]*w;a[i+k]=u+v;a[i+k+len/2]=u-v;w*=wl;}}
    }
    if(inverse)for(auto& x:a)x/=static_cast<double>(n);
}
std::vector<neural::Packet> load(const std::string& path){
    std::vector<neural::Packet> packets;FILE* f=std::fopen(path.c_str(),"rb");if(!f)return packets;
    neural::Packet p;while(std::fread(&p,sizeof(p),1,f)==1)packets.push_back(p);std::fclose(f);return packets;
}
void writeWav(const std::string& path,const std::vector<const std::vector<float>*>& channels,size_t from,size_t frames){
    FILE* f=std::fopen(path.c_str(),"wb");if(!f)return;
    const std::uint16_t format=3,count=static_cast<std::uint16_t>(channels.size()),align=static_cast<std::uint16_t>(4*count),bits=32;
    const std::uint32_t bytes=static_cast<std::uint32_t>(frames*align),riff=36+bytes,r=rate,byteRate=rate*align,fmtSize=16;
    std::fwrite("RIFF",1,4,f);std::fwrite(&riff,4,1,f);std::fwrite("WAVEfmt ",1,8,f);std::fwrite(&fmtSize,4,1,f);std::fwrite(&format,2,1,f);std::fwrite(&count,2,1,f);
    std::fwrite(&r,4,1,f);std::fwrite(&byteRate,4,1,f);std::fwrite(&align,2,1,f);std::fwrite(&bits,2,1,f);std::fwrite("data",1,4,f);std::fwrite(&bytes,4,1,f);
    for(size_t n=from;n<from+frames;++n)for(auto* c:channels){const float v=n<c->size()?(*c)[n]:0.f;std::fwrite(&v,4,1,f);}
    std::fclose(f);
}
double db(double power){return 10*std::log10(power+1e-20);}
struct Win{double at,rawDb,outDb,refDb,echoRawDb,echoOutDb,suppressionDb,fit,nearDb;bool hole;int lagMs;double voicing=0;};
struct Span{double from,to;std::string kind;double worst;};
}
int main(int argc,char** argv){
    if(argc<3){std::puts("usage: PersonalToolsAecLiveScan <record prefix> <out dir> [snippets]");return 2;}
    const std::string prefix=argv[1],outDir=argv[2];const int snippets=argc>3?std::atoi(argv[3]):12;
    auto mic=load(prefix+"-mic.bin");auto ref=load(prefix+"-ref.bin");
    if(mic.empty()){std::puts("{\"error\":\"no microphone packets\"}");return 3;}
    std::sort(mic.begin(),mic.end(),[](auto& a,auto& b){return a.index<b.index;});
    mic.erase(std::unique(mic.begin(),mic.end(),[](auto& a,auto& b){return a.index==b.index;}),mic.end());
    std::sort(ref.begin(),ref.end(),[](auto& a,auto& b){return a.time<b.time;});
    ref.erase(std::unique(ref.begin(),ref.end(),[](auto& a,auto& b){return a.time==b.time;}),ref.end());
    const auto first=mic.front().index;const size_t total=mic.back().index+mic.back().frames-first;
    std::vector<float> raw(total),out(total),reference(total);std::vector<char> missing(total,1);
    for(const auto& p:mic)std::copy_n(p.samples.data(),p.frames,raw.begin()+static_cast<std::ptrdiff_t>(p.index-first));
    // results[k] is the output for microphone sample k-modelDelay; 0 marks a result the APO never got.
    if(FILE* f=std::fopen((prefix+"-out.bin").c_str(),"rb")){
        std::uint64_t start=0;float v=0;
        if(std::fread(&start,sizeof(start),1,f)==1)for(std::uint64_t idx=start;std::fread(&v,sizeof(v),1,f)==1;++idx)
            if(idx>=first+modelDelay&&idx-modelDelay-first<total){out[idx-modelDelay-first]=v;missing[idx-modelDelay-first]=v==0.f;}
        std::fclose(f);
    }
    // Reference on the microphone timeline exactly as the host renders it (unshifted), silence-filled gaps.
    std::vector<double> refStarts; // seconds on the mic timeline where playback resumed after >= 1 s idle
    {
        StereoReferenceTimeline timeline;std::uint64_t end=0;size_t next=0;
        static constexpr std::array<float,neural::packetFrames> silence{};std::array<float,neural::packetFrames> l{},r{};
        for(const auto& p:mic){
            while(next<ref.size()&&ref[next].time<=p.time+300000){
                const auto& q=ref[next++];
                if(end&&q.time>end+50000){
                    const auto gap=std::min<std::uint64_t>((q.time-end)*48000/10000000,48000);
                    for(auto k=gap/neural::packetFrames*neural::packetFrames;k>0;k-=neural::packetFrames)
                        timeline.push(silence.data(),silence.data(),neural::packetFrames,48000,q.time-(k*10000000+24000)/48000,10000000);
                }
                timeline.push(q.samples.data(),q.samples.data(),q.frames,48000,q.time,10000000);end=q.time+(q.frames*10000000ull+24000)/48000;
            }
            if(timeline.render(p.time,10000000,l.data(),r.data(),p.frames))
                std::copy_n(l.data(),p.frames,reference.begin()+static_cast<std::ptrdiff_t>(p.index-first));
        }
        double quietSince=0;bool quiet=true;
        for(size_t w=0;w+step<=total;w+=step){
            double e=0;for(size_t n=w;n<w+step;++n)e+=reference[n]*static_cast<double>(reference[n]);
            const bool silent=e/step<1e-9; // -90 dBFS
            if(quiet&&!silent&&(w/static_cast<double>(rate)-quietSince)>=1.0)refStarts.push_back(w/static_cast<double>(rate));
            if(silent&&!quiet)quietSince=w/static_cast<double>(rate);
            quiet=silent;
        }
    }
    // Echo estimate per chunk: H = sum X R* / sum |R|^2 over Hann frames, truncated to irTaps.
    std::vector<float> echo(total);std::vector<int> chunkLag;
    for(size_t c0=0;c0<total;c0+=chunk){
        const size_t c1=std::min(total,c0+chunk);
        constexpr size_t N=32768,H=N/4;std::vector<cd> sxr(N),srr(N),a(N),b(N);int frames=0;
        for(size_t f0=c0;f0+N<=c1+(c1==total?0:rate);f0+=H){
            if(f0+N>total)break;
            double er=0;for(size_t n=0;n<N;++n)er+=reference[f0+n]*static_cast<double>(reference[f0+n]);
            if(er/N<1e-8)continue; // playback below -80 dBFS
            for(size_t n=0;n<N;++n){const double w=0.5-0.5*std::cos(2*std::numbers::pi*n/(N-1));a[n]=w*reference[f0+n];b[n]=w*raw[f0+n];}
            fft(a,false);fft(b,false);
            for(size_t k=0;k<N;++k){sxr[k]+=b[k]*std::conj(a[k]);srr[k]+=std::norm(a[k]);}
            ++frames;
        }
        if(frames<2){chunkLag.push_back(-1);continue;}
        double floor=0;for(auto& v:srr)floor=std::max(floor,v.real());floor*=1e-4;
        std::vector<cd> h(N);for(size_t k=0;k<N;++k)h[k]=sxr[k]/(srr[k].real()+floor);
        fft(h,true);
        std::vector<double> ir(irTaps);size_t peak=0;
        for(size_t t=0;t<irTaps;++t){ir[t]=h[t].real()*(t>irTaps*3/4?0.5+0.5*std::cos(std::numbers::pi*(t-irTaps*3/4.)/(irTaps/4.)):1.0);if(std::abs(ir[t])>std::abs(ir[peak]))peak=t;}
        chunkLag.push_back(static_cast<int>(peak));
        // FFT convolution of the chunk (plus the preceding response length) with the room response.
        const size_t from=c0>irTaps?c0-irTaps:0,len=c1-from;size_t n=1;while(n<len+irTaps)n<<=1;
        std::vector<cd> x(n),y(n);for(size_t i=0;i<len;++i)x[i]=reference[from+i];for(size_t t=0;t<irTaps;++t)y[t]=ir[t];
        fft(x,false);fft(y,false);for(size_t k=0;k<n;++k)x[k]*=y[k];fft(x,true);
        for(size_t i=c0;i<c1;++i)echo[i]=static_cast<float>(x[i-from].real());
    }
    // 100 ms windows.
    std::vector<Win> wins;
    for(size_t w=0;w+window<=total;w+=step){
        double rr=0,oo=0,ff=0,ee=0,re=0,oe=0;bool hole=false;
        for(size_t n=w;n<w+window;++n){rr+=raw[n]*static_cast<double>(raw[n]);oo+=out[n]*static_cast<double>(out[n]);ff+=reference[n]*static_cast<double>(reference[n]);
            ee+=echo[n]*static_cast<double>(echo[n]);re+=raw[n]*static_cast<double>(echo[n]);oe+=out[n]*static_cast<double>(echo[n]);hole=hole||missing[n];}
        const double echoRaw=ee>0?re*re/ee:0,echoOut=ee>0?oe*oe/ee:0;
        Win v{w/static_cast<double>(rate),db(rr/window),db(oo/window),db(ff/window),db(echoRaw/window),db(echoOut/window),
              db(echoRaw)-db(echoOut),rr>0?echoRaw/rr:0,db(std::max(0.0,rr-echoRaw)/window),hole,chunkLag[std::min(chunkLag.size()-1,w/chunk)]};
        // Near-end sound well above the room noise with little echo: pitch strength (normalized
        // autocorrelation peak at an 80-400 Hz period, 16 kHz) separates voiced speech from clicks and noise.
        if(v.nearDb>-40&&v.fit<0.2){
            std::vector<double> x(window/3);for(size_t i=0;i<x.size();++i)x[i]=raw[w+3*i]+raw[w+3*i+1]+static_cast<double>(raw[w+3*i+2]);
            for(size_t lag=40;lag<=200;++lag){double xy=0,xx=0,yy=0;for(size_t i=lag;i<x.size();++i){xy+=x[i]*x[i-lag];xx+=x[i]*x[i];yy+=x[i-lag]*x[i-lag];}v.voicing=std::max(v.voicing,xy/std::sqrt(xx*yy+1e-30));}
        }
        wins.push_back(v);
    }
    FILE* csv=std::fopen((outDir+"\\windows.csv").c_str(),"w");
    if(csv){std::fprintf(csv,"seconds,rawDb,outDb,refDb,echoRawDb,echoOutDb,suppressionDb,echoFraction,nearDb,hole,voicing\n");
        for(auto& v:wins)std::fprintf(csv,"%.2f,%.1f,%.1f,%.1f,%.1f,%.1f,%.1f,%.3f,%.1f,%d,%.2f\n",v.at,v.rawDb,v.outDb,v.refDb,v.echoRawDb,v.echoOutDb,v.suppressionDb,v.fit,v.nearDb,v.hole?1:0,v.voicing);std::fclose(csv);}
    // Problem windows: echo clearly present in the raw microphone yet suppressed < 10 dB (leak);
    // output louder than the raw microphone (the canceller added energy); output missing (hole).
    // Level jumps: 10 ms output frames 15 dB above both neighbours 30 ms away while raw is not.
    std::vector<Span> spans;
    auto add=[&](double from,double to,const std::string& kind,double worst){
        if(!spans.empty()&&spans.back().kind==kind&&from<=spans.back().to+0.15){spans.back().to=std::max(spans.back().to,to);spans.back().worst=kind=="leak"?std::min(spans.back().worst,worst):std::max(spans.back().worst,worst);}
        else spans.push_back({from,to,kind,worst});
    };
    double leakWindows=0,echoWindows=0;std::vector<double> suppression,nearGain,voicedGain;
    for(auto& v:wins){
        const bool echoPresent=v.echoRawDb>-60&&v.fit>0.25;
        if(echoPresent){++echoWindows;suppression.push_back(v.suppressionDb);}
        // How much of the near-end sound survives, all of it and voiced speech only.
        if(v.nearDb>-40&&v.fit<0.2&&!v.hole){nearGain.push_back(v.outDb-v.rawDb);if(v.voicing>0.6)voicedGain.push_back(v.outDb-v.rawDb);}
        if(echoPresent&&v.suppressionDb<10){++leakWindows;add(v.at,v.at+0.1,"leak",v.suppressionDb);}
        if(v.outDb>v.rawDb+3&&v.outDb>-60)add(v.at,v.at+0.1,"louder",v.outDb-v.rawDb);
        if(v.hole)add(v.at,v.at+0.1,"hole",0);
    }
    {
        constexpr size_t frame=480;std::vector<double> o,r;
        for(size_t n=0;n+frame<=total;n+=frame){double a=0,b=0;for(size_t i=n;i<n+frame;++i){a+=out[i]*static_cast<double>(out[i]);b+=raw[i]*static_cast<double>(raw[i]);}o.push_back(db(a/frame));r.push_back(db(b/frame));}
        for(size_t k=3;k+3<o.size();++k){
            const double jump=o[k]-std::max(o[k-3],o[k+3]),rawJump=r[k]-std::max(r[k-3],r[k+3]);
            if(jump>15&&rawJump<jump-10&&o[k]>-55)add(k*frame/static_cast<double>(rate),(k+1)*frame/static_cast<double>(rate),"jump",jump);
        }
    }
    std::sort(spans.begin(),spans.end(),[](auto& a,auto& b){return a.from<b.from;});
    std::sort(suppression.begin(),suppression.end());std::sort(nearGain.begin(),nearGain.end());std::sort(voicedGain.begin(),voicedGain.end());
    FILE* ev=std::fopen((outDir+"\\events.csv").c_str(),"w");
    if(ev){std::fprintf(ev,"from,to,kind,worst,sincePlaybackStart,refDb,echoRawDb,nearDb,chunkLagMs\n");
        for(auto& s:spans){
            double since=-1;for(double t:refStarts)if(t<=s.from+0.05)since=s.from-t;
            const auto& v=wins[std::min(wins.size()-1,static_cast<size_t>(s.from*rate/step))];
            std::fprintf(ev,"%.2f,%.2f,%s,%.1f,%.2f,%.1f,%.1f,%.1f,%.2f\n",s.from,s.to,s.kind.c_str(),s.worst,since,v.refDb,v.echoRawDb,v.nearDb,v.lagMs/48.0);
        }
        std::fclose(ev);}
    // Snippets around the worst leak spans and every other kind, 1 s before to 1 s after; or, with
    // AEC_SNIPPETS=from-to,from-to (seconds), the same spans in every recording that is compared.
    std::vector<Span> pick=spans;
    std::sort(pick.begin(),pick.end(),[](auto& a,auto& b){
        auto score=[](const Span& s){return s.kind=="leak"?(10-s.worst)+(s.to-s.from)*20:s.kind=="hole"?100.0:s.worst;};return score(a)>score(b);});
    if(const char* list=std::getenv("AEC_SNIPPETS")){
        pick.clear();
        for(const char* p=list;*p;){char* end=nullptr;const double a=std::strtod(p,&end);const double b=*end=='-'?std::strtod(end+1,&end):a;pick.push_back({a,b,"span",0});p=*end?end+1:end;}
    }
    for(int i=0;i<std::min<int>(snippets,static_cast<int>(pick.size()));++i){
        const auto& s=pick[i];const size_t from=static_cast<size_t>(std::max(0.0,s.from-1)*rate),to=std::min(total,static_cast<size_t>((s.to+1)*rate));
        char name[96];std::snprintf(name,sizeof(name),"\\snip-%02d-%s-%.1fs.wav",i,s.kind.c_str(),s.from);
        writeWav(outDir+name,{&raw,&out,&reference,&echo},from,to-from);
    }
    size_t counts[4]{};for(auto& s:spans)++counts[s.kind=="leak"?0:s.kind=="louder"?1:s.kind=="hole"?2:3];
    std::printf("{\"seconds\":%.1f,\"micPackets\":%zu,\"refPackets\":%zu,\"playbackStarts\":%zu,\"echoWindows\":%.0f,\"leakWindows\":%.0f,"
                "\"suppressionMedianDb\":%.1f,\"suppressionP10Db\":%.1f,\"nearWindows\":%zu,\"nearGainMedianDb\":%.1f,\"nearGainP10Db\":%.1f,\"voicedWindows\":%zu,\"voicedGainMedianDb\":%.1f,\"voicedGainP10Db\":%.1f,"
                "\"spans\":{\"leak\":%zu,\"louder\":%zu,\"hole\":%zu,\"jump\":%zu},\"chunkLagMs\":[",
        total/static_cast<double>(rate),mic.size(),ref.size(),refStarts.size(),echoWindows,leakWindows,
        suppression.empty()?0:suppression[suppression.size()/2],suppression.empty()?0:suppression[suppression.size()/10],
        nearGain.size(),nearGain.empty()?0:nearGain[nearGain.size()/2],nearGain.empty()?0:nearGain[nearGain.size()/10],
        voicedGain.size(),voicedGain.empty()?0:voicedGain[voicedGain.size()/2],voicedGain.empty()?0:voicedGain[voicedGain.size()/10],counts[0],counts[1],counts[2],counts[3]);
    for(size_t i=0;i<chunkLag.size();++i)std::printf("%s%.2f",i?",":"",chunkLag[i]<0?-1:chunkLag[i]/48.0);
    std::puts("]}");
}
