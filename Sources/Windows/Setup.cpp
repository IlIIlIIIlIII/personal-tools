#include "NeuralIpc.hpp"
#include "MicTiming.hpp"
#include <windows.h>
#include <cfgmgr32.h>
#include <objbase.h>
#include <shellapi.h>
#include <winioctl.h>
#include <usbioctl.h>
#include <algorithm>
#include <filesystem>
#include <string>
#include <thread>

// The SYSTEM microphone recovery task's own look before it restarts the capture device: reads the
// APO <-> NPU host section for up to 8 s (as PersonalToolsAecWatch does) and runs MicTiming on
// every open microphone stream, starting with the packets already queued (~0.6 s). Exit 0: all
// healthy, 3: no microphone stream, 4: no section, 10: a stream is stalled.
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
            if(!watching[ch]||g!=generation[ch]){watching[ch]=true;generation[ch]=g;seen[ch]=0;timing[ch].reset();}
            // The APO only writes the slot at w; the last 120 before it are complete.
            for(auto x=std::max(seen[ch],w>120?w-120:0);x<w;++x) {
                const neural::Packet p=c.microphone.packets[x%neural::queueSize];
                // The section is writable by the signed-in user; this runs as SYSTEM.
                if(p.generation!=g||p.frames>neural::packetFrames)continue;
                any=true;
                if(timing[ch].add(p.index,p.time,p.frames,p.samples.data())){result=10;break;}
            }
            seen[ch]=w;
        }
        Sleep(5);
    }
    UnmapViewOfFile(section);CloseHandle(mapping);
    return result?result:(any?0:3);
}

static bool started(DEVINST node) {ULONG status=0,problem=0;return CM_Get_DevNode_Status(&status,&problem,node,0)==CR_SUCCESS&&(status&DN_STARTED)&&!problem;}
// Audio endpoints (SWD\MMDEVAPI\{0.0.x...}) started anywhere below a node. The MIDI nodes of
// Windows MIDI Services also hang there but may come back later; they do not count.
static int startedEndpoints(DEVINST parent) {
    int count=0;DEVINST child=0;
    for(auto found=CM_Get_Child(&child,parent,0);found==CR_SUCCESS;found=CM_Get_Sibling(&child,child,0)) {
        wchar_t id[MAX_DEVICE_ID_LEN]{};
        if(CM_Get_Device_IDW(child,id,MAX_DEVICE_ID_LEN,0)==CR_SUCCESS&&_wcsnicmp(id,L"SWD\\MMDEVAPI\\{0.0.",18)==0&&started(child))++count;
        count+=startedEndpoints(child);
    }
    return count;
}
// The USB device's started interfaces (MI_00 audio, MI_03 MIDI, ...) and audio endpoints, to tell when it is back.
static int startedFunctions(DEVINST device) {
    int count=0;DEVINST child=0;
    for(auto found=CM_Get_Child(&child,device,0);found==CR_SUCCESS;found=CM_Get_Sibling(&child,child,0))if(started(child))++count;
    return count+startedEndpoints(device);
}
static FILETIME arrivedAt(DEVINST device) {
    static const DEVPROPKEY lastArrival{{0x83da6326,0x97a6,0x4088,{0x94,0x53,0xa1,0x92,0x3f,0x57,0x3b,0x29}},102}; // DEVPKEY_Device_LastArrivalDate
    FILETIME at{};DEVPROPTYPE type=0;ULONG size=sizeof(at);
    if(CM_Get_DevNode_PropertyW(device,&lastArrival,&type,reinterpret_cast<PBYTE>(&at),&size,0)!=CR_SUCCESS||type!=DEVPROP_TYPE_FILETIME)return {};
    return at;
}
// Power-cycles the hub port of a USB device, like pulling its cable and plugging it back in, and
// waits until it, its interfaces and its audio endpoints have started again. Since 2026-10-27 the
// microphone recovery tries this first: unlike a device restart it needs neither Windows Audio
// stopped nor the vendor helpers closed, so playback and the other apps carry on. The port is
// looked up now, so a device moved to another port is still found. Needs admin rights.
// Exit 0: back; 20: device not found; 21: hub not found; 22: the hub refused; 23: not back in
// 30 s; 24: back and started, but fewer interfaces/endpoints running than before after 30 s.
static int cyclePort(const wchar_t* instance) {
    // CfgMgr calls do not wait for the device's driver, but nothing here may hang the SYSTEM task.
    std::thread([]{Sleep(45000);ExitProcess(23);}).detach();
    DEVINST device=0,hub=0;ULONG port=0,size=sizeof(port);
    if(CM_Locate_DevNodeW(&device,const_cast<DEVINSTID_W>(instance),CM_LOCATE_DEVNODE_NORMAL)!=CR_SUCCESS)return 20;
    // A USB device's address is its port number on the parent hub.
    if(CM_Get_Parent(&hub,device,0)!=CR_SUCCESS||CM_Get_DevNode_Registry_PropertyW(device,CM_DRP_ADDRESS,nullptr,&port,&size,0)!=CR_SUCCESS||port==0)return 20;
    wchar_t hubId[MAX_DEVICE_ID_LEN]{};ULONG length=0;
    static GUID hubInterface{0xf18a0e88,0xc30c,0x11d0,{0x88,0x15,0x00,0xa0,0xc9,0x06,0xbe,0xd8}}; // GUID_DEVINTERFACE_USB_HUB
    if(CM_Get_Device_IDW(hub,hubId,MAX_DEVICE_ID_LEN,0)!=CR_SUCCESS||
       CM_Get_Device_Interface_List_SizeW(&length,&hubInterface,hubId,CM_GET_DEVICE_INTERFACE_LIST_PRESENT)!=CR_SUCCESS||length<2)return 21;
    std::wstring interfaces(length,L'\0');
    if(CM_Get_Device_Interface_ListW(&hubInterface,hubId,interfaces.data(),length,CM_GET_DEVICE_INTERFACE_LIST_PRESENT)!=CR_SUCCESS)return 21;
    const int before=startedFunctions(device);const FILETIME arrived=arrivedAt(device);
    HANDLE handle=CreateFileW(interfaces.c_str(),GENERIC_WRITE,FILE_SHARE_WRITE,nullptr,OPEN_EXISTING,0,nullptr);
    if(handle==INVALID_HANDLE_VALUE)return 21;
    USB_CYCLE_PORT_PARAMS params{};params.ConnectionIndex=port;DWORD returned=0;
    const BOOL cycled=DeviceIoControl(handle,IOCTL_USB_HUB_CYCLE_PORT,&params,sizeof(params),&params,sizeof(params),&returned,nullptr);
    CloseHandle(handle);
    if(!cycled)return 22;
    // Back = arrived again (a new arrival time), started, with as many functions running as before.
    bool back=false;
    for(const auto start=GetTickCount64();GetTickCount64()-start<30000;Sleep(250)) {
        DEVINST again=0;
        if(CM_Locate_DevNodeW(&again,const_cast<DEVINSTID_W>(instance),CM_LOCATE_DEVNODE_NORMAL)!=CR_SUCCESS)continue;
        const FILETIME now=arrivedAt(again);
        if(CompareFileTime(&now,&arrived)<=0||!started(again))continue;
        back=true;
        if(startedFunctions(again)>=before)return 0;
    }
    return back?24:23;
}

int WINAPI wWinMain(HINSTANCE,HINSTANCE,LPWSTR,int) {
    int count=0;auto args=CommandLineToArgvW(GetCommandLineW(),&count);
    if(args&&count==2&&wcscmp(args[1],L"--check-microphone")==0){LocalFree(args);return checkMicrophone();}
    if(args&&count==3&&wcscmp(args[1],L"--cycle-port")==0){const int result=cyclePort(args[2]);LocalFree(args);return result;}
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
