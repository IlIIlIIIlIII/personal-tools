// PersonalToolsNpuHost.exe: runs DTLN-AEC on the NPU for the capture APO, outside
// audiodg. Started and supervised by the Personal Tools app; exits with its parent.
// Arguments: [--npu <payload dir>] [--section <name>] [--parent <pid>]
#include "NeuralHost.hpp"
#include <shellapi.h>
#include <shlobj.h>
#include <fstream>
#include <mutex>

using namespace personaltools::win;
namespace {
std::mutex logGate;
std::filesystem::path logPath() {
    PWSTR root=nullptr;
    if(FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppData,0,nullptr,&root)))return {};
    auto path=std::filesystem::path(root)/L"PersonalTools";CoTaskMemFree(root);
    std::error_code error;std::filesystem::create_directories(path,error);
    return path/L"npu-host.log";
}
// Bounded text log: failures carry the OpenVINO/Windows error detail that the UI only summarises.
void writeLog(const std::string& message) {
    std::lock_guard lock(logGate);
    static const auto path=logPath();
    if(path.empty())return;
    std::error_code error;
    if(std::filesystem::exists(path,error)&&std::filesystem::file_size(path,error)>256*1024)
        std::filesystem::rename(path,std::filesystem::path(path).replace_extension(L".old.log"),error);
    SYSTEMTIME t{};GetLocalTime(&t);char stamp[32]{};
    snprintf(stamp,sizeof(stamp),"%04u-%02u-%02u %02u:%02u:%02u ",t.wYear,t.wMonth,t.wDay,t.wHour,t.wMinute,t.wSecond);
    std::ofstream out(path,std::ios::app|std::ios::binary);
    out<<stamp<<"pid="<<GetCurrentProcessId()<<' '<<message<<"\r\n";
}
// The echo delay belongs to the output device: remembered per render endpoint so the next
// start is aligned from the first sample instead of moving the reference mid-playback.
std::wstring renderDevice(){SharedFile settings;Configuration config;LONG revision=0;return settings.open()&&settings.read(config,revision)?std::wstring(config.renderId):std::wstring();}
std::filesystem::path delayPath(){const auto log=logPath();return log.empty()?log:log.parent_path()/L"echo-delay.txt";}
int loadDelay(){
    const auto device=renderDevice();std::wifstream in(delayPath());std::wstring id;int us=-1;
    // A delay beyond what the estimator searches was never a real echo (see EchoDelay.hpp).
    return !device.empty()&&std::getline(in,id)&&id==device&&(in>>us)&&us>=0&&static_cast<long long>(us)*16/1000<=EchoDelayEstimator::maxDelay?us:-1;
}
void saveDelay(int us){
    const auto device=renderDevice();if(device.empty()||delayPath().empty())return;
    std::wofstream out(delayPath(),std::ios::trunc);out<<device<<L"\n"<<us<<L"\n";
}
}

int WINAPI wWinMain(HINSTANCE,HINSTANCE,LPWSTR,int) {
    int count=0;auto args=CommandLineToArgvW(GetCommandLineW(),&count);
    wchar_t self[32768]{};GetModuleFileNameW(nullptr,self,32768);
    std::filesystem::path folder=std::filesystem::path(self).parent_path()/L"npu";
    std::wstring section=neural::Mapping::defaultName();
    DWORD parent=0;
    for(int i=1;args&&i+1<count;++i){
        if(wcscmp(args[i],L"--npu")==0)folder=args[++i];
        else if(wcscmp(args[i],L"--section")==0)section=args[++i];
        else if(wcscmp(args[i],L"--parent")==0)parent=static_cast<DWORD>(_wtoi(args[++i]));
    }
    if(args)LocalFree(args);
    // One host per section; a second launch exits quietly.
    std::uint64_t hash=14695981039346656037ull;for(wchar_t c:section){hash^=c;hash*=1099511628211ull;}
    HANDLE single=CreateMutexW(nullptr,TRUE,(L"Local\\PersonalToolsNpuHost-"+std::to_wstring(hash)).c_str());
    if(!single||GetLastError()==ERROR_ALREADY_EXISTS){if(single)CloseHandle(single);return 0;}
    HANDLE parentProcess=parent?OpenProcess(SYNCHRONIZE,FALSE,parent):nullptr;
    if(parent&&!parentProcess){CloseHandle(single);return 3;}
    SetPriorityClass(GetCurrentProcess(),ABOVE_NORMAL_PRIORITY_CLASS);
    // A windowless background process is an EcoQoS candidate; results have a ~60 ms deadline.
    PROCESS_POWER_THROTTLING_STATE throttling{PROCESS_POWER_THROTTLING_CURRENT_VERSION,
        PROCESS_POWER_THROTTLING_EXECUTION_SPEED|PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,0};
    SetProcessInformation(GetCurrentProcess(),ProcessPowerThrottling,&throttling,sizeof(throttling));
    // Probes and tests run their own section with synthetic echo; keep their delay out of the device file.
    const bool installed=section==neural::Mapping::defaultName();
    const int delayUs=installed?loadDelay():-1;
    writeLog("start npu="+folder.string()+" echo delay "+std::to_string(delayUs)+" us");
    {
        NeuralHost<> host(folder,section,std::chrono::milliseconds(1000),writeLog,delayUs,installed?NeuralHost<>::DelayChanged(saveDelay):NeuralHost<>::DelayChanged());
        if(parentProcess)WaitForSingleObject(parentProcess,INFINITE);
        else WaitForSingleObject(GetCurrentProcess(),INFINITE);
    }
    writeLog("stop");
    if(parentProcess)CloseHandle(parentProcess);
    CloseHandle(single);
    return 0;
}
