// Real NPU echo-cancellation probe. Runs the capture APO's NeuralStream here and
// PersonalToolsNpuHost.exe as a separate process on the local Intel NPU, with
// synthetic far-end speech, a sparse room response, near-end speech and double
// talk, paced in real time.
// Usage: PersonalToolsNpuProbe [payload-directory]  (or PERSONALTOOLS_NPU_DIR).
// Exit 0 pass, 1 fail, 77 when no NPU payload is configured (CTest skip).
#include "NeuralStream.hpp"
#include "DtlnNpu.hpp"
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <random>
#include <vector>

using namespace personaltools::win;
namespace {
constexpr unsigned rate=48000;
constexpr double pi=3.14159265358979323846;

// Voiced syllables with drifting pitch, two formants and pauses. Not real
// speech, but it exercises the model's harmonic and onset behaviour.
std::vector<float> speech(unsigned frames,unsigned seed,double basePitch){
    std::mt19937 random(seed);
    std::uniform_real_distribution<double> uniform(0.,1.);
    std::normal_distribution<double> noise(0.,1.);
    std::vector<float> out(frames,0.f);
    unsigned pos=0;
    while(pos<frames){
        const unsigned length=static_cast<unsigned>(rate*(0.15+0.18*uniform(random)));
        const unsigned gap=static_cast<unsigned>(rate*(0.05+0.12*uniform(random)));
        const double f1=400+500*uniform(random),f2=1100+1300*uniform(random);
        const double pitch=basePitch*(0.85+0.3*uniform(random)),glide=(uniform(random)-0.5)*0.4;
        double phase=0;
        for(unsigned i=0;i<length&&pos+i<frames;++i){
            const double t=static_cast<double>(i)/length;
            const double envelope=std::sin(pi*t)*std::sin(pi*t);
            const double f0=pitch*(1+glide*t);
            phase+=2*pi*f0/rate;
            double sample=0;
            for(int h=1;h*f0<4000;++h){
                const double f=h*f0;
                const double weight=1/(1+std::pow((f-f1)/180,2))+0.6/(1+std::pow((f-f2)/260,2))+0.05;
                sample+=weight*std::sin(h*phase)/h;
            }
            sample+=0.02*noise(random);
            out[pos+i]=static_cast<float>(0.12*envelope*sample);
        }
        pos+=length+gap;
    }
    return out;
}
double energy(const std::vector<float>& x,unsigned from,unsigned to,int shift=0){
    double sum=0;
    for(unsigned i=from;i<to;++i){const double v=x[i+shift];sum+=v*v;}
    return sum/(to-from);
}
double db(double ratio){return 10*std::log10(std::max(ratio,1e-12));}
std::uint64_t now100ns(){
    LARGE_INTEGER qpc{},frequency{};QueryPerformanceCounter(&qpc);QueryPerformanceFrequency(&frequency);
    return static_cast<std::uint64_t>(static_cast<long double>(qpc.QuadPart)*10000000/frequency.QuadPart);
}
}

int main(int argc,char** argv){
    std::filesystem::path folder;
    if(argc>1)folder=argv[1];
    else if(const char* configured=std::getenv("PERSONALTOOLS_NPU_DIR"))folder=configured;
    if(folder.empty()){puts("SKIP: set PERSONALTOOLS_NPU_DIR to an NPU payload directory");return 77;}
    if(!std::filesystem::exists(folder/"openvino_c.dll")){printf("SKIP: no OpenVINO runtime in %s\n",folder.string().c_str());return 77;}
    folder=std::filesystem::absolute(folder);

    // 1. Raw model timing on the NPU: each 128-sample hop is 8 ms of audio at 16 kHz.
    double meanMs=0,worstMs=0;
    try{
        DtlnNpu model;
        const auto opened=std::chrono::steady_clock::now();
        model.open(folder);
        printf("npu_open_seconds=%.2f\n",std::chrono::duration<double>(std::chrono::steady_clock::now()-opened).count());
        std::array<float,128> mic{},ref{},out{};
        std::vector<double> times;
        for(int hop=0;hop<1500;++hop){
            for(int k=0;k<128;++k){ref[k]=0.1f*std::sin(0.07f*(hop*128+k));mic[k]=0.5f*ref[k];}
            const auto start=std::chrono::steady_clock::now();
            model.process(mic.data(),ref.data(),out.data());
            if(hop>=100)times.push_back(std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count());
        }
        for(double t:times){meanMs+=t;worstMs=std::max(worstMs,t);}
        meanMs/=times.size();
        std::sort(times.begin(),times.end());
        printf("npu_hop_ms mean=%.3f p99=%.3f max=%.3f budget=8.000\n",meanMs,times[times.size()*99/100],worstMs);
    }catch(const NpuFailure& e){printf("FAIL: NPU open/inference stage=%d %s\n",static_cast<int>(e.stage),e.what());return 1;}
    catch(const std::exception& e){printf("FAIL: %s\n",e.what());return 1;}

    // 2. The APO pipeline: 48 kHz packets with QPC timestamps, real-time pacing.
    constexpr unsigned seconds=16,frames=rate*seconds,chunk=480;
    const auto farSpeech=speech(frames,11,128),nearSpeech=speech(frames,29,205);
    std::vector<float> echo(frames,0.f),mic(frames,0.f),out(frames,0.f),nearTrack(frames,0.f);
    {
        std::mt19937 random(7);std::uniform_real_distribution<double> uniform(0.,1.);
        std::vector<std::pair<unsigned,float>> taps{{72,0.55f}};
        for(int i=0;i<240;++i){
            const unsigned delay=96+static_cast<unsigned>(uniform(random)*rate*0.28);
            const double decay=std::exp(-6.9*delay/(rate*0.3)); // RT60 300 ms
            taps.push_back({delay,static_cast<float>((uniform(random)-0.5)*0.5*decay)});
        }
        for(const auto& [delay,gain]:taps)for(unsigned n=delay;n<frames;++n)echo[n]+=gain*farSpeech[n-delay];
        for(auto& v:echo)v=std::tanh(1.4f*v)/1.4f; // mild loudspeaker compression
    }
    std::mt19937 hiss(3);std::normal_distribution<float> floor(0.f,0.0005f);
    const unsigned farOnlyEnd=rate*8,nearOnlyEnd=rate*12;
    std::vector<float> farPlayed(frames,0.f);
    for(unsigned n=0;n<frames;++n){
        const bool farOn=n<farOnlyEnd||n>=nearOnlyEnd,nearOn=n>=farOnlyEnd;
        farPlayed[n]=farOn?farSpeech[n]:0.f;
        nearTrack[n]=nearOn?nearSpeech[n]:0.f;
        mic[n]=(farOn?echo[n]:0.f)+nearTrack[n]+floor(hiss);
    }
    // The APO side runs here; inference runs in the real PersonalToolsNpuHost.exe
    // child process, exactly as in the installed system (private section name).
    const std::wstring section=L"Local\\PersonalToolsNeural-probe-"+std::to_wstring(GetCurrentProcessId());
    NeuralStream stream(section);
    if(!stream.connected()){puts("FAIL: could not create the shared section");return 1;}
    wchar_t self[32768]{};GetModuleFileNameW(nullptr,self,32768);
    const auto hostExe=std::filesystem::path(self).parent_path()/L"PersonalToolsNpuHost.exe";
    std::wstring command=L"\""+hostExe.wstring()+L"\" --npu \""+folder.wstring()+L"\" --section "+section+L" --parent "+std::to_wstring(GetCurrentProcessId());
    STARTUPINFOW startup{sizeof(startup)};PROCESS_INFORMATION host{};
    if(!CreateProcessW(hostExe.c_str(),command.data(),nullptr,nullptr,FALSE,0,nullptr,nullptr,&startup,&host)){printf("FAIL: cannot start %s (%lu)\n",hostExe.string().c_str(),GetLastError());return 1;}
    struct Child{PROCESS_INFORMATION p;~Child(){TerminateProcess(p.hProcess,0);WaitForSingleObject(p.hProcess,5000);CloseHandle(p.hProcess);CloseHandle(p.hThread);}} child{host};
    for(int i=0;i<600&&stream.state()!=2;++i)Sleep(100);
    if(stream.state()!=2){printf("FAIL: NPU host state=%d failure=%d restarts=%ld\n",stream.state(),stream.failure(),stream.restarts());return 1;}
    puts("npu_host=separate process");
    std::vector<float> stereo(chunk*2);
    const auto start=now100ns();
    for(unsigned pos=0;pos<frames;pos+=chunk){
        const auto time=start+static_cast<std::uint64_t>(pos)*10000000ull/rate;
        while(now100ns()<time)Sleep(1);
        for(unsigned i=0;i<chunk;++i)stereo[i*2]=stereo[i*2+1]=farPlayed[pos+i];
        stream.reference(stereo.data(),chunk,2,time,false);
        stream.microphone(mic.data()+pos,out.data()+pos,chunk,1,1,time,false,true);
    }
    const int latency=static_cast<int>(NeuralStream::latencyFrames);
    // Output n carries input n-latency. Skip the model's first 2 s of warm-up.
    const unsigned farFrom=rate*2,farTo=farOnlyEnd-latency,nearFrom=farOnlyEnd+rate/2,nearTo=nearOnlyEnd-latency;
    const double erle=db(energy(mic,farFrom,farTo)/energy(out,farFrom,farTo,latency));
    const double nearKept=db(energy(out,nearFrom,nearTo,latency)/energy(mic,nearFrom,nearTo));
    double doubleTalkError=0,doubleTalkNear=0;
    for(unsigned n=nearOnlyEnd+rate/2;n<frames-latency;++n){
        const double residual=out[n+latency]-nearTrack[n];doubleTalkError+=residual*residual;doubleTalkNear+=nearTrack[n]*nearTrack[n];
    }
    const double doubleTalkSdr=db(doubleTalkNear/doubleTalkError);
    const double missRatio=static_cast<double>(stream.misses())/frames;
    printf("pipeline state=%d restarts=%ld completed_hops=%llu missed_frames=%llu (%.2f%%)\n",stream.state(),stream.restarts(),
        static_cast<unsigned long long>(stream.completed()),static_cast<unsigned long long>(stream.misses()),missRatio*100);
    printf("erle_far_only_db=%.1f near_end_level_change_db=%.1f double_talk_sdr_db=%.1f\n",erle,nearKept,doubleTalkSdr);
    bool pass=true;
    if(meanMs>=8.0){puts("FAIL: NPU inference is slower than real time");pass=false;}
    if(stream.state()!=2||stream.restarts()!=0){puts("FAIL: NPU worker did not stay running");pass=false;}
    if(missRatio>0.02){puts("FAIL: too many output frames missed the NPU deadline");pass=false;}
    if(erle<15){puts("FAIL: far-end echo reduction below 15 dB");pass=false;}
    if(nearKept<-6){puts("FAIL: near-end speech attenuated by more than 6 dB");pass=false;}
    puts(pass?"PASS: NPU echo cancellation":"NPU echo cancellation probe failed");
    return pass?0:1;
}
