#include "AecDiagnostics.hpp"
#include <fstream>
#include <vector>
#include <cstdio>
using personaltools::win::AecDiagnostics;
int main(){
    auto root=std::filesystem::temp_directory_path()/(L"aec-diagnostic-test-"+std::to_wstring(GetCurrentProcessId()));
    std::filesystem::create_directories(root);
    if(AecDiagnostics::open(root))return 1; // Explicit opt-in required.
    std::ofstream(root/L"enabled.flag")<<"test";
    {
        auto log=AecDiagnostics::open(root,AecDiagnostics::segmentBytes+1024);if(!log)return 2;
        AecDiagnostics::Record r;r.h.type=1;r.h.frames=256;r.h.index=123;r.samples[0]=.25f;r.samples[1]=-.5f;
        log->submit(0,r);log->event(2,5,17,123,1,2,3,4,7);
    }
    std::uint64_t bytes=0;bool audio=false,event=false;
    for(const auto& e:std::filesystem::directory_iterator(root))if(e.path().extension()==L".acd"){
        bytes+=e.file_size();std::ifstream f(e.path(),std::ios::binary);AecDiagnostics::Header h;
        while(f.read(reinterpret_cast<char*>(&h),sizeof(h))&&h.magic==0x31444341){
            if(h.type==1){std::array<float,512>x{};f.read(reinterpret_cast<char*>(x.data()),h.frames*8);audio=h.index==123&&x[0]==.25f&&x[1]==-.5f;}
            if(h.type==5)event=h.flags==7&&h.a==1&&h.d==4;
        }
    }
    if(bytes!=AecDiagnostics::segmentBytes||!audio||!event)return 3;
    {auto log=AecDiagnostics::open(root,AecDiagnostics::segmentBytes+1024);log->event(2,5,1,1);}
    unsigned files=0;for(const auto& e:std::filesystem::directory_iterator(root))if(e.path().extension()==L".acd")++files;
    if(files!=1)return 4; // Across sessions, reservation cannot exceed the cap.
    std::filesystem::remove_all(root);
    puts("Diagnostic opt-in, audio/event serialization, drain and cross-session budget passed");
}
