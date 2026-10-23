// Replays an AecWatch recording through the NeuralHost signal path with the recurrent-state rules
// switched on or off, and writes each variant as an AecWatch-style recording that
// PersonalToolsAecLiveScan can score. Runs on the OpenVINO CPU plugin unless AEC_DEVICE=NPU.
// The NPU computes in FP16 and at this microphone level behaves quite differently from the CPU's
// FP32 (2026-10-21: live output matched the NPU replay within ~1 dB, the CPU replay not at all),
// so judge a rule on the NPU. NPU replays are paced at 4x real time so that the live host sharing
// the NPU keeps its deadlines. AEC_GAIN_DB / AEC_REF_GAIN_DB scale the model inputs (the microphone
// gain is removed from the output again); +24 dB on the microphone makes the NPU match FP32.
// Usage: PersonalToolsAecVariantReplay <openvino bin dir> <model dir> <record prefix> <out dir> <shift ms> <variant>...
// variant: shipped (as NeuralHost: onset and jump resets, 0.995 state decay), before (no jump reset,
//          the 2026-10-20 host), nodecay, decay=<factor>, gate<N>[h<ms>] (decay only while the
//          reference is above -N dBFS, or within <ms> after it was).
#include "NeuralIpc.hpp"
#include "NeuralHost.hpp"
#include "SignalOps.hpp"
#include <algorithm>
#include <cstdio>
#include <filesystem>
#include <numbers>
#include <string>
#include <vector>

using namespace personaltools::win;
using personaltools::StereoReferenceTimeline;
namespace signal=personaltools::signal;
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
// DtlnNpu.hpp with the device, input gains and the state decay as parameters.
class Dtln {
    using Status=int;
    using Create=Status(__cdecl*)(void**);
    using Compile=Status(__cdecl*)(const void*,const char*,const char*,size_t,void**,...);
    using Request=Status(__cdecl*)(const void*,void**);
    using Tensor=Status(__cdecl*)(const void*,size_t,void**);
    using Data=Status(__cdecl*)(const void*,void**);
    using Infer=Status(__cdecl*)(void*);
    HMODULE dll_=nullptr;void* core_=nullptr;
    std::array<void*,2> models_{},requests_{};std::array<std::array<void*,5>,2> tensors_{};std::array<std::array<float*,5>,2> values_{};
    Infer infer_=nullptr;signal::FFTSetup fft_=nullptr;
    std::array<float,512> mic_{},ref_{},overlap_{},real_{},imag_{},mr_{},mi_{};
    unsigned sinceLoud_=1u<<30;
    template<class T> T get(const char* name){auto p=reinterpret_cast<T>(GetProcAddress(dll_,name));if(!p)throw std::runtime_error(name);return p;}
    static void check(Status s,const char* what){if(s)throw std::runtime_error(std::string(what)+" "+std::to_string(s));}
public:
    float gain=1.f,referenceGain=1.f,decay=.995f;
    float referenceFloor=1e-7f;unsigned holdHops=0; // decay gate: reference energy over the 512-sample window
    void open(const std::filesystem::path& bin,const std::filesystem::path& models,const char* device){
        dll_=LoadLibraryExW((bin/L"openvino_c.dll").c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if(!dll_)throw std::runtime_error("openvino_c.dll "+std::to_string(GetLastError()));
        auto compile=get<Compile>("ov_core_compile_model_from_file");auto request=get<Request>("ov_compiled_model_create_infer_request");
        auto input=get<Tensor>("ov_infer_request_get_input_tensor_by_index");auto output=get<Tensor>("ov_infer_request_get_output_tensor_by_index");
        auto data=get<Data>("ov_tensor_data");infer_=get<Infer>("ov_infer_request_infer");
        check(get<Create>("ov_core_create")(&core_),"core");
        for(int i=0;i<2;++i){
            auto path=(models/("dtln_aec_256_"+std::to_string(i+1)+".tflite")).string();
            check(compile(core_,path.c_str(),device,0,&models_[i]),"compile");check(request(models_[i],&requests_[i]),"request");
            for(size_t j=0;j<5;++j){check(j<3?input(requests_[i],j,&tensors_[i][j]):output(requests_[i],j-3,&tensors_[i][j]),"tensor");check(data(tensors_[i][j],reinterpret_cast<void**>(&values_[i][j])),"data");}
        }
        fft_=signal::createFFT(9,signal::radix2);reset();
    }
    void reset(){mic_.fill(0);ref_.fill(0);overlap_.fill(0);resetState();}
    void resetState(){for(auto& v:values_)if(v[1])std::fill_n(v[1],1024,0.f);}
    void process(const float* microphone,const float* reference,float* out){
        std::move(mic_.begin()+128,mic_.end(),mic_.begin());std::move(ref_.begin()+128,ref_.end(),ref_.begin());
        for(int k=0;k<128;++k){mic_[384+k]=microphone[k]*gain;ref_[384+k]=reference[k]*referenceGain;}
        signal::SplitComplex z{real_.data(),imag_.data()};
        real_=mic_;imag_.fill(0);signal::fft(fft_,&z,1,9,signal::forward);mr_=real_;mi_=imag_;
        for(int k=0;k<257;++k)values_[0][0][k]=std::hypot(real_[k],imag_[k]);
        real_=ref_;imag_.fill(0);signal::fft(fft_,&z,1,9,signal::forward);
        for(int k=0;k<257;++k)values_[0][2][k]=std::hypot(real_[k],imag_[k]);
        check(infer_(requests_[0]),"infer");std::copy_n(values_[0][4],1024,values_[0][1]);
        for(int k=0;k<512;++k){const auto bin=k<=256?k:512-k;real_[k]=mr_[k]*values_[0][3][bin];imag_[k]=mi_[k]*values_[0][3][bin];}
        signal::fft(fft_,&z,1,9,signal::inverse);
        for(int k=0;k<512;++k){values_[1][0][k]=real_[k]/512.f;values_[1][2][k]=ref_[k];}
        check(infer_(requests_[1]),"infer");std::copy_n(values_[1][4],1024,values_[1][1]);
        std::move(overlap_.begin()+128,overlap_.end(),overlap_.begin());std::fill(overlap_.begin()+384,overlap_.end(),0.f);
        for(int k=0;k<512;++k)overlap_[k]+=values_[1][3][k];
        float inputEnergy=0.f,outputEnergy=0.f,referenceEnergy=0.f;
        for(int k=0;k<128;++k){out[k]=overlap_[k]/gain;inputEnergy+=mic_[k]*mic_[k];outputEnergy+=overlap_[k]*overlap_[k];}
        for(float v:ref_)referenceEnergy+=v*v;
        referenceEnergy/=referenceGain*referenceGain;
        sinceLoud_=referenceEnergy>referenceFloor?0:sinceLoud_+1;
        if(decay<1.f&&referenceEnergy>1e-7f&&sinceLoud_<=holdHops&&outputEnergy<inputEnergy*.1f)for(auto& v:values_)for(int k=0;k<1024;++k)v[1][k]*=decay;
    }
};
// The scorer reads <prefix>-mic/-ref next to -out; link the recording instead of copying it.
void link(const std::string& from,const std::string& to){
    std::error_code error;std::filesystem::remove(to,error);
    std::filesystem::create_hard_link(from,to,error);
    if(error)std::filesystem::copy_file(from,to,std::filesystem::copy_options::overwrite_existing);
}
}
int main(int argc,char** argv){
    if(argc<7){std::puts("usage: PersonalToolsAecVariantReplay <openvino bin> <model dir> <prefix> <out dir> <shift ms> <variant>...");return 2;}
    const std::filesystem::path bin=argv[1],models=argv[2];const std::string prefix=argv[3],outDir=argv[4];const double shiftMs=std::atof(argv[5]);
    for(const auto& extra:{bin/".."/".."/".."/"3rdparty"/"tbb"/"bin",bin})AddDllDirectory(std::filesystem::absolute(extra).c_str());
    const char* env=std::getenv("AEC_DEVICE");const std::string device=env?env:"CPU";
    auto mic=load(prefix+"-mic.bin");auto ref=load(prefix+"-ref.bin");
    if(mic.empty()){std::puts("{\"error\":\"no microphone packets\"}");return 3;}
    std::sort(mic.begin(),mic.end(),[](auto& a,auto& b){return a.index<b.index;});
    mic.erase(std::unique(mic.begin(),mic.end(),[](auto& a,auto& b){return a.index==b.index;}),mic.end());
    std::sort(ref.begin(),ref.end(),[](auto& a,auto& b){return a.time<b.time;});
    ref.erase(std::unique(ref.begin(),ref.end(),[](auto& a,auto& b){return a.time==b.time;}),ref.end());
    const auto first=mic.front().index;const size_t total=mic.back().index+mic.back().frames-first;
    std::vector<float> raw(total),reference(total);
    for(const auto& p:mic)std::copy_n(p.samples.data(),p.frames,raw.begin()+static_cast<std::ptrdiff_t>(p.index-first));
    {   // As NeuralHost::step: reference rendered at p.time - shift, idle gaps up to 1 s filled with silence.
        StereoReferenceTimeline timeline;std::uint64_t end=0;size_t next=0;const auto shift=static_cast<std::uint64_t>(shiftMs*10000);
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
            if(timeline.render(p.time-shift,10000000,l.data(),r.data(),p.frames))
                std::copy_n(l.data(),p.frames,reference.begin()+static_cast<std::ptrdiff_t>(p.index-first));
        }
    }
    std::filesystem::create_directories(outDir);
    for(int a=6;a<argc;++a){
        const std::string variant=argv[a];
        Dtln model;model.open(bin,models,device.c_str());
        if(const char* g=std::getenv("AEC_GAIN_DB"))model.gain=static_cast<float>(std::pow(10.0,std::atof(g)/20));
        model.referenceGain=model.gain;
        if(const char* g=std::getenv("AEC_REF_GAIN_DB"))model.referenceGain=static_cast<float>(std::pow(10.0,std::atof(g)/20));
        const bool jumpReset=variant!="before";
        if(variant=="nodecay")model.decay=1.f;
        if(variant.rfind("decay=",0)==0)model.decay=static_cast<float>(std::atof(variant.c_str()+6));
        if(variant.rfind("gate",0)==0){
            model.referenceFloor=static_cast<float>(512*std::pow(10.0,-std::atof(variant.c_str()+4)/10));
            if(const auto h=variant.find('h');h!=std::string::npos)model.holdHops=static_cast<unsigned>(std::atof(variant.c_str()+h+1)/8);
        }
        LARGE_INTEGER qf{},qn{};QueryPerformanceFrequency(&qf);QueryPerformanceCounter(&qn);auto pace=qn.QuadPart;
        Fir micFir,refFir,outFir;PlaybackOnset onset;PlaybackJump jump;
        std::array<float,128> m{},f{},o{};unsigned count=0,onsetResets=0,jumpResets=0;std::uint64_t processed=0;
        std::vector<float> results(total+NeuralHost<>::modelDelay);
        for(size_t n=0;n<total;++n){
            const float x=micFir.tick(raw[n]),y=refFir.tick(reference[n]);
            if(n%3==0){m[count]=x;f[count]=y;++count;}
            if(count==128){
                const bool resumed=onset.resumes(f.data()),jumped=jump.jumps(f.data())&&jumpReset;
                if(resumed||jumped)model.resetState();
                onsetResets+=resumed;jumpResets+=jumped;
                if(device=="NPU"){pace+=qf.QuadPart/500;do{SwitchToThread();QueryPerformanceCounter(&qn);}while(qn.QuadPart<pace);}
                model.process(m.data(),f.data(),o.data());
                onset.processed(m.data(),o.data());jump.processed(m.data(),o.data());
                for(unsigned j=0;j<NeuralHost<>::hop;++j){const float v=outFir.tick(j%3==0?o[j/3]*3.f:0.f);if(processed+j>=NeuralHost<>::modelDelay)results[processed+j]=v;}
                processed+=NeuralHost<>::hop;count=0;
            }
        }
        // Same layout as AecWatch: -out holds the first result index, then one float per index (0 = missing).
        const auto base=outDir+"\\"+variant;
        link(prefix+"-mic.bin",base+"-mic.bin");link(prefix+"-ref.bin",base+"-ref.bin");
        if(FILE* fo=std::fopen((base+"-out.bin").c_str(),"wb")){
            const std::uint64_t start=first+NeuralHost<>::modelDelay;std::fwrite(&start,sizeof(start),1,fo);
            for(size_t k=NeuralHost<>::modelDelay;k<total;++k){const float v=results[k]==0.f?1e-9f:results[k];std::fwrite(&v,4,1,fo);}
            std::fclose(fo);
        }
        std::printf("{\"variant\":\"%s\",\"device\":\"%s\",\"decay\":%.4f,\"onsetResets\":%u,\"jumpResets\":%u}\n",variant.c_str(),device.c_str(),model.decay,onsetResets,jumpResets);std::fflush(stdout);
    }
}
