// Read-only live watcher for NPU echo cancellation. Maps the installed APO <-> NPU host
// section and reports where processed output was missing (the APO then fades to raw
// microphone audio, which is when echo and room noise leak), model resets, reference
// gaps and microphone timestamp jitter.
// Usage: PersonalToolsAecWatch [seconds] [events.csv] [record-prefix]
// With record-prefix, the raw microphone and reference packets the host receives are written
// to <prefix>-ch<N>-g<generation>-mic.bin / -ref.bin (neural::Packet records) for offline replay;
// every stream restart gets new files instead of truncating the previous ones.
#include "NeuralIpc.hpp"
#include <algorithm>
#include <bit>
#include <cmath>
#include <string>
#include <cstdio>
#include <cstdlib>
#include <vector>

using namespace personaltools::win;
namespace {
std::uint64_t now100ns(){LARGE_INTEGER q{},f{};QueryPerformanceCounter(&q);QueryPerformanceFrequency(&f);return static_cast<std::uint64_t>(static_cast<long double>(q.QuadPart)*10000000/f.QuadPart);}
struct Hole{double at;std::uint64_t index,length;int channel;};
struct Watch{
    std::uint32_t generation=0;std::uint64_t lastW=0,prevIndex=0,prevTime=0,checked=0,holeStart=0,holeLength=0;unsigned prevFrames=0;bool prev=false;
    std::uint64_t packets=0,jitterOver1ms=0,indexGaps=0,holeSamples=0,lostStart=0,completedStart=0;double maxJitterMs=0;bool started=false;
    std::vector<double> jitter;
    // Reference arrival: a gap over 50 ms ends a run; the next packet starts one.
    std::uint64_t refW=0;double refLastSeen=-1;bool refFlowing=false;
    // Results the APO needed in the callback that pushed the newest packet but that were not there yet.
    std::uint64_t dueChecked=0,lateStart=0,lateLength=0,lateSamples=0,lateEvents=0,lateWhileRef=0;
    // Counters published by the APO (raw samples played while the host ran) and the host (hops without reference).
    std::uint64_t apoMissStart=0,apoMissLast=0,unreferencedStart=0;
    FILE* micOut=nullptr;FILE* refOut=nullptr;FILE* outOut=nullptr; // out: first index (u64), then one float per index (0 = missing)
};
}
int main(int argc,char** argv){
    const double seconds=argc>1?std::atof(argv[1]):60;
    FILE* csv=argc>2?std::fopen(argv[2],"w"):nullptr;
    const std::string record=argc>3?argv[3]:"";
    if(csv)std::fprintf(csv,"seconds,channel,kind,index,samples,ms\n");
    HANDLE mapping=OpenFileMappingW(FILE_MAP_READ,FALSE,neural::Mapping::defaultName().c_str());
    if(!mapping){std::puts("{\"error\":\"shared section not found; open a microphone stream first\"}");return 2;}
    const auto* section=static_cast<const neural::Section*>(MapViewOfFile(mapping,FILE_MAP_READ,0,0,sizeof(neural::Section)));
    if(!section||section->magic.load()!=neural::sectionMagic){std::puts("{\"error\":\"cannot map section (different build?)\"}");return 3;}
    std::vector<Watch> watch(neural::channelCount);
    std::vector<Hole> holes;
    const std::uint64_t start=now100ns();
    auto at=[&]{return (now100ns()-start)/1e7;};
    std::printf("{\"event\":\"start\",\"qpc100ns\":%llu}\n",static_cast<unsigned long long>(start));std::fflush(stdout);
    HANDLE timer=CreateWaitableTimerExW(nullptr,nullptr,CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,TIMER_ALL_ACCESS);
    SetThreadPriority(GetCurrentThread(),THREAD_PRIORITY_HIGHEST);
    while(at()<seconds){
        for(unsigned ch=0;ch<neural::channelCount;++ch){
            const auto& c=section->channels[ch];auto& w=watch[ch];
            if(c.owner.load()==0){w.started=false;continue;}
            const auto generation=c.generation.load();
            if(!w.started||generation!=w.generation){
                for(FILE* f:{w.micOut,w.refOut,w.outOut})if(f)std::fclose(f);
                w=Watch{};w.started=true;w.generation=generation;w.lastW=c.microphone.w.load();w.refW=c.reference.w.load();
                if(!record.empty()){
                    const auto base=record+"-ch"+std::to_string(ch)+"-g"+std::to_string(generation);
                    w.micOut=std::fopen((base+"-mic.bin").c_str(),"wb");w.refOut=std::fopen((base+"-ref.bin").c_str(),"wb");
                    w.outOut=std::fopen((base+"-out.bin").c_str(),"wb");
                }
                w.lostStart=c.lost.load();w.completedStart=c.completed.load();
                w.apoMissStart=w.apoMissLast=c.apoMisses.load();w.unreferencedStart=c.unreferenced.load();
                std::printf("{\"event\":\"stream\",\"seconds\":%.2f,\"channel\":%u,\"generation\":%u}\n",at(),ch,generation);
            }
            const auto refNow=c.reference.w.load(std::memory_order_acquire);
            if(w.refOut)for(auto x=std::max(w.refW,refNow>120?refNow-120:0);x<refNow;++x){
                const auto& r=c.reference.packets[x%neural::queueSize];
                if(r.generation==generation)std::fwrite(&r,sizeof(r),1,w.refOut);
            }
            if(refNow!=w.refW){
                const auto& r=c.reference.packets[(refNow-1)%neural::queueSize];
                if(r.generation==generation){
                    if(!w.refFlowing&&csv)std::fprintf(csv,"%.3f,%u,ref-start,0,0,0\n",at(),ch);
                    w.refFlowing=true;w.refLastSeen=at();
                }
                w.refW=refNow;
            }else if(w.refFlowing&&at()-w.refLastSeen>0.05){
                w.refFlowing=false;if(csv)std::fprintf(csv,"%.3f,%u,ref-stop,0,0,0\n",w.refLastSeen,ch);
            }
            const auto misses=c.apoMisses.load();
            if(misses!=w.apoMissLast){if(csv)std::fprintf(csv,"%.3f,%u,apo-miss,0,%llu,%.2f\n",at(),ch,static_cast<unsigned long long>(misses-w.apoMissLast),(misses-w.apoMissLast)/48.0);w.apoMissLast=misses;}
            const auto wNow=c.microphone.w.load(std::memory_order_acquire);
            for(auto x=std::max(w.lastW,wNow>120?wNow-120:0);x<wNow;++x){
                const auto& p=c.microphone.packets[x%neural::queueSize];
                const auto index=p.index,time=p.time;const unsigned frames=p.frames;
                if(p.generation!=generation)continue;
                if(w.micOut)std::fwrite(&p,sizeof(p),1,w.micOut);
                ++w.packets;
                if(w.prev){
                    if(index==w.prevIndex+w.prevFrames){
                        const auto expected=w.prevTime+(w.prevFrames*10000000ull+24000)/48000;
                        const double ms=std::abs(static_cast<double>(static_cast<std::int64_t>(time-expected)))/1e4;
                        w.maxJitterMs=std::max(w.maxJitterMs,ms);w.jitter.push_back(ms);
                        if(ms>1.0){++w.jitterOver1ms;if(csv)std::fprintf(csv,"%.3f,%u,jitter,%llu,0,%.3f\n",at(),ch,static_cast<unsigned long long>(index),ms);}
                    }else{++w.indexGaps;if(csv)std::fprintf(csv,"%.3f,%u,index-gap,%llu,%lld,0\n",at(),ch,static_cast<unsigned long long>(index),static_cast<long long>(index-(w.prevIndex+w.prevFrames)));}
                }
                w.prev=true;w.prevIndex=index;w.prevTime=time;w.prevFrames=frames;
            }
            w.lastW=wNow;
            if(!w.prev)continue;
            const auto newest=w.prevIndex+w.prevFrames;
            // In the callback that pushed microphone index i the APO also read result i-4096+1214.
            if(newest>4096){
                const auto due=newest-4096+1214;
                if(w.dueChecked<1214)w.dueChecked=1214;
                if(due>w.dueChecked+30000)w.dueChecked=due-30000;
                for(auto idx=w.dueChecked;idx<due;++idx){
                    const bool ok=c.results[idx%neural::ringSize].stamp.load(std::memory_order_acquire)==neural::stamp(generation,idx);
                    if(!ok){if(!w.lateLength)w.lateStart=idx;++w.lateLength;++w.lateSamples;}
                    else if(w.lateLength){
                        ++w.lateEvents;if(w.refFlowing)++w.lateWhileRef;
                        if(csv)std::fprintf(csv,"%.3f,%u,%s,%llu,%llu,%.2f\n",at()-(newest-w.lateStart)/48000.0,ch,w.refFlowing?"late-ref":"late-idle",
                            static_cast<unsigned long long>(w.lateStart),static_cast<unsigned long long>(w.lateLength),w.lateLength/48.0);
                        w.lateLength=0;
                    }
                }
                w.dueChecked=due;
            }
            // Results are written ~40 ms after capture; check up to 125 ms behind the newest packet.
            if(newest<6000)continue;
            const auto limit=newest-6000;
            if(w.checked<1214)w.checked=1214; // first model-delay samples are never produced
            if(limit>w.checked+30000)w.checked=limit-30000;
            if(w.outOut&&std::ftell(w.outOut)==0)std::fwrite(&w.checked,sizeof(w.checked),1,w.outOut);
            for(auto idx=w.checked;idx<limit;++idx){
                const auto& slot=c.results[idx%neural::ringSize];
                const bool ok=slot.stamp.load(std::memory_order_acquire)==neural::stamp(generation,idx);
                if(w.outOut){const float v=ok?std::bit_cast<float>(slot.sample.load(std::memory_order_relaxed)):0.f;std::fwrite(&v,sizeof(v),1,w.outOut);}
                if(!ok){if(!w.holeLength)w.holeStart=idx;++w.holeLength;++w.holeSamples;}
                else if(w.holeLength){
                    const double behind=(newest-w.holeStart)/48000.0;
                    holes.push_back({at()-behind,w.holeStart,w.holeLength,static_cast<int>(ch)});
                    if(csv)std::fprintf(csv,"%.3f,%u,hole,%llu,%llu,%.2f\n",at()-behind,ch,static_cast<unsigned long long>(w.holeStart),static_cast<unsigned long long>(w.holeLength),w.holeLength/48.0);
                    w.holeLength=0;
                }
            }
            w.checked=limit;
        }
        // Poll every 2 ms so a result that arrives just after the APO read still counts as late.
        if(timer){LARGE_INTEGER due{};due.QuadPart=-20000;if(SetWaitableTimer(timer,&due,0,nullptr,nullptr,FALSE))WaitForSingleObject(timer,50);else Sleep(2);}else Sleep(2);
    }
    std::printf("{\"seconds\":%.1f,\"channels\":[",seconds);
    bool first=true;
    for(unsigned ch=0;ch<neural::channelCount;++ch){
        const auto& c=section->channels[ch];const auto& w=watch[ch];
        if(!w.started)continue;
        std::vector<double> j=w.jitter;std::sort(j.begin(),j.end());
        const double p99=j.empty()?0:j[j.size()*99/100];
        std::uint64_t resets=0,refHops=0,other=0;
        for(const auto& h:holes)if(h.channel==static_cast<int>(ch)){if(h.length==1214)++resets;else if(h.length%384==0)++refHops;else ++other;}
        std::printf("%s{\"channel\":%u,\"packets\":%llu,\"jitterP99Ms\":%.3f,\"jitterMaxMs\":%.3f,\"jitterOver1ms\":%llu,\"indexGaps\":%llu,\"lost\":%llu,\"blocks\":%llu,\"unreferencedBlocks\":%llu,"
            "\"holeSamples\":%llu,\"holes\":%zu,\"resetHoles\":%llu,\"referenceHoles\":%llu,\"otherHoles\":%llu,\"lateEvents\":%llu,\"lateWhileReference\":%llu,\"lateMs\":%.1f,\"apoMissMs\":%.1f,\"echoDelayUs\":%d,\"referenceShiftUs\":%d,\"state\":%d,\"restarts\":%d}",
            first?"":",",ch,static_cast<unsigned long long>(w.packets),p99,w.maxJitterMs,static_cast<unsigned long long>(w.jitterOver1ms),static_cast<unsigned long long>(w.indexGaps),
            static_cast<unsigned long long>(c.lost.load()-w.lostStart),static_cast<unsigned long long>(c.completed.load()-w.completedStart),static_cast<unsigned long long>(c.unreferenced.load()-w.unreferencedStart),
            static_cast<unsigned long long>(w.holeSamples),static_cast<size_t>(std::count_if(holes.begin(),holes.end(),[&](const Hole& h){return h.channel==static_cast<int>(ch);})),
            static_cast<unsigned long long>(resets),static_cast<unsigned long long>(refHops),static_cast<unsigned long long>(other),
            static_cast<unsigned long long>(w.lateEvents),static_cast<unsigned long long>(w.lateWhileRef),w.lateSamples/48.0,(c.apoMisses.load()-w.apoMissStart)/48.0,c.echoDelayUs.load(),c.referenceShiftUs.load(),c.state.load(),c.restarts.load());
        first=false;
    }
    std::puts("]}");
    if(csv)std::fclose(csv);
    for(auto& w:watch)for(FILE* f:{w.micOut,w.refOut,w.outOut})if(f)std::fclose(f);
}
