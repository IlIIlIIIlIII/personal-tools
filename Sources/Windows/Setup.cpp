#include "NeuralIpc.hpp"
#include "MicTiming.hpp"
#include <windows.h>
#include <objbase.h>
#include <shellapi.h>
#include <algorithm>
#include <filesystem>

// The SYSTEM microphone recovery task's own look before it restarts the capture device: reads the
// APO <-> NPU host section for up to 8 s (as PersonalToolsAecWatch does) and runs MicTiming on
// every open microphone stream. Exit 0: all healthy, 3: no microphone stream, 4: no section,
// 10: a stream is stalled.
static int checkMicrophone() {
    using namespace personaltools::win;
    HANDLE mapping=OpenFileMappingW(FILE_MAP_READ,FALSE,neural::Mapping::defaultName().c_str());
    if(!mapping)return 4;
    const auto* section=static_cast<const neural::Section*>(MapViewOfFile(mapping,FILE_MAP_READ,0,0,sizeof(neural::Section)));
    if(!section||section->magic.load()!=neural::sectionMagic){if(section)UnmapViewOfFile(section);CloseHandle(mapping);return 4;}
    std::array<MicTiming,neural::channelCount> timing{};
    std::array<std::uint64_t,neural::channelCount> seen{};std::array<std::uint32_t,neural::channelCount> generation{};
    std::array<bool,neural::channelCount> watching{};
    bool any=false;int result=0;
    const auto start=GetTickCount64();
    while(result==0&&GetTickCount64()-start<8000) {
        for(unsigned ch=0;ch<neural::channelCount&&result==0;++ch) {
            const auto& c=section->channels[ch];
            if(c.owner.load()==0){watching[ch]=false;continue;}
            const auto g=c.generation.load();const auto w=c.microphone.w.load(std::memory_order_acquire);
            if(!watching[ch]||g!=generation[ch]){watching[ch]=true;generation[ch]=g;seen[ch]=w;timing[ch].reset();continue;}
            // The APO only writes the slot at w; the last 120 before it are complete.
            for(auto x=std::max(seen[ch],w>120?w-120:0);x<w;++x) {
                const neural::Packet p=c.microphone.packets[x%neural::queueSize];
                if(p.generation!=g)continue;
                any=true;
                if(timing[ch].add(p.index,p.time,p.frames)){result=10;break;}
            }
            seen[ch]=w;
        }
        Sleep(5);
    }
    UnmapViewOfFile(section);CloseHandle(mapping);
    return result?result:(any?0:3);
}

int WINAPI wWinMain(HINSTANCE,HINSTANCE,LPWSTR,int) {
    int count=0;auto args=CommandLineToArgvW(GetCommandLineW(),&count);
    if(args&&count==2&&wcscmp(args[1],L"--check-microphone")==0){LocalFree(args);return checkMicrophone();}
    const bool rebind=args&&count==5&&wcscmp(args[1],L"--rebind-render")==0;
    if(!rebind&&(!args||count!=4||wcscmp(args[1],L"--initialize")!=0)){LocalFree(args);return 2;}
    if(FAILED(CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED))){LocalFree(args);return 1;}
    wchar_t path[32768]{};GetModuleFileNameW(nullptr,path,32768);
    const auto library=std::filesystem::path(path).parent_path()/L"ui"/L"PersonalToolsBridge.dll";
    auto module=LoadLibraryExW(library.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
    using Initialize=int(__cdecl*)(const wchar_t*,const wchar_t*);
    auto initialize=module?reinterpret_cast<Initialize>(GetProcAddress(module,"MT_Initialize")):nullptr;
    using Rebind=int(__cdecl*)(const wchar_t*,const wchar_t*,const wchar_t*);
    auto repair=module?reinterpret_cast<Rebind>(GetProcAddress(module,"MT_RebindRender")):nullptr;
    const int result=rebind?(repair?repair(args[2],args[3],args[4]):1):(initialize?initialize(args[2],args[3]):1);
    if(module)FreeLibrary(module);CoUninitialize();LocalFree(args);return result;
}
