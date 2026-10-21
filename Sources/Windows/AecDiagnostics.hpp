#pragma once
#include <windows.h>
#include <array>
#include <atomic>
#include <filesystem>
#include <memory>
#include <thread>
#include <algorithm>
#include <cstdio>

namespace personaltools::win {
// Opt-in, bounded diagnostic capture. Producers never allocate, wait, or touch files.
// Each queue has exactly one producer: microphone callback, reference callback,
// or inference worker. Full queues drop diagnostics, never audio.
class AecDiagnostics {
public:
    static constexpr std::uint64_t segmentBytes=16*1024*1024, budgetBytes=14000000000ull;
    struct Header {
        std::uint32_t magic=0x31444341,type=0,frames=0,flags=0;
        std::uint64_t qpc=0,time=0,index=0,a=0,b=0,c=0,d=0;
    };
    static_assert(sizeof(Header)==72);
    struct Record { Header h;std::array<float,512> samples{}; };
    static std::uint64_t clock(){LARGE_INTEGER t{};QueryPerformanceCounter(&t);return t.QuadPart;}
    static std::unique_ptr<AecDiagnostics> open(const std::filesystem::path& root,std::uint64_t limit=budgetBytes) noexcept {
        try {
            if(root.empty()||!std::filesystem::exists(root/L"enabled.flag"))return {};
            return std::unique_ptr<AecDiagnostics>(new AecDiagnostics(root,std::min(limit,budgetBytes)));
        } catch(...) {return {};}
    }
    ~AecDiagnostics(){quit_=true;if(writer_.joinable())writer_.join();}
    void submit(unsigned lane,Record& r) noexcept {
        if(!active_.load(std::memory_order_relaxed))return;
        r.h.qpc=clock();if(!queues_[lane].push(r))drops_[lane].fetch_add(1,std::memory_order_relaxed);
    }
    void event(unsigned lane,unsigned type,std::uint64_t time,std::uint64_t index,
               std::uint64_t a=0,std::uint64_t b=0,std::uint64_t c=0,std::uint64_t d=0,unsigned flags=0){
        Record r;r.h.type=type;r.h.time=time;r.h.index=index;r.h.a=a;r.h.b=b;r.h.c=c;r.h.d=d;r.h.flags=flags;submit(lane,r);
    }
private:
    struct Queue {
        std::array<Record,256> data{};std::atomic<std::uint64_t>w{0},r{0};
        bool push(const Record& item){auto n=w.load(std::memory_order_relaxed);if(n-r.load(std::memory_order_acquire)>=data.size())return false;data[n%data.size()]=item;w.store(n+1,std::memory_order_release);return true;}
        bool pop(Record& item){auto n=r.load(std::memory_order_relaxed);if(n==w.load(std::memory_order_acquire))return false;item=data[n%data.size()];r.store(n+1,std::memory_order_release);return true;}
    };
    std::array<Queue,3>queues_{};std::array<std::atomic<std::uint64_t>,3>drops_{};
    std::atomic<bool>quit_{false},active_{true};std::filesystem::path root_;
    HANDLE file_=INVALID_HANDLE_VALUE;std::uint64_t offset_=0,session_=clock(),limit_;unsigned segment_=0;
    explicit AecDiagnostics(const std::filesystem::path& root,std::uint64_t limit):root_(root),limit_(limit),writer_([this]{run();}){}
    void close(){if(file_!=INVALID_HANDLE_VALUE){CloseHandle(file_);file_=INVALID_HANDLE_VALUE;}}
    bool next(){
        close();
        // File lock coordinates reservations across all APO instances/processes.
        HANDLE lock=INVALID_HANDLE_VALUE;
        for(unsigned attempt=0;attempt<100&&lock==INVALID_HANDLE_VALUE;++attempt){
            lock=CreateFileW((root_/L"budget.lock").c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
            if(lock==INVALID_HANDLE_VALUE){if(GetLastError()!=ERROR_SHARING_VIOLATION)break;Sleep(20);}
        }
        if(lock==INVALID_HANDLE_VALUE)return false;
        bool ok=false;
        try {
            std::uint64_t used=0;
            for(const auto& e:std::filesystem::recursive_directory_iterator(root_)){
                if(e.is_symlink())throw 1;
                if(e.is_regular_file()){auto size=e.file_size();if(size>budgetBytes||used>budgetBytes-size)throw 1;used+=size;}
            }
            ULARGE_INTEGER free{};
            if(used+segmentBytes<=limit_&&GetDiskFreeSpaceExW(root_.c_str(),&free,nullptr,nullptr)&&free.QuadPart>segmentBytes+2000000000ull){
                wchar_t name[128];swprintf(name,128,L"aec-%lu-%llu-%u.acd",GetCurrentProcessId(),static_cast<unsigned long long>(session_),segment_++);
                file_=CreateFileW((root_/name).c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);
                if(file_!=INVALID_HANDLE_VALUE){
                    LARGE_INTEGER size{};size.QuadPart=segmentBytes;
                    ok=SetFilePointerEx(file_,size,nullptr,FILE_BEGIN)&&SetEndOfFile(file_);
                    size.QuadPart=0;ok=ok&&SetFilePointerEx(file_,size,nullptr,FILE_BEGIN);offset_=0;
                }
            }
        }catch(...){ok=false;}
        CloseHandle(lock);if(!ok)close();return ok;
    }
    bool write(const Record& r){
        const DWORD bytes=sizeof(Header)+((r.h.type==1||r.h.type==2||r.h.type==12)?r.h.frames*2*sizeof(float):0);
        if(file_==INVALID_HANDLE_VALUE||offset_+bytes>segmentBytes){
            if(!next())return false;
            LARGE_INTEGER frequency{};QueryPerformanceFrequency(&frequency);FILETIME ft{};GetSystemTimeAsFileTime(&ft);
            Record start;start.h.type=6;start.h.qpc=clock();start.h.a=frequency.QuadPart;
            start.h.b=(std::uint64_t(ft.dwHighDateTime)<<32)|ft.dwLowDateTime;start.h.c=4096;start.h.d=48000;
            DWORD n=0;if(!WriteFile(file_,&start.h,sizeof(Header),&n,nullptr)||n!=sizeof(Header))return false;offset_+=n;
        }
        DWORD n=0;const bool ok=WriteFile(file_,&r,bytes,&n,nullptr)&&n==bytes;offset_+=n;return ok;
    }
    void run() noexcept {
        try {
            auto check=GetTickCount64();
            while(true){
                bool any=false;Record r;
                for(unsigned lane=0;lane<3;++lane)for(unsigned n=0;n<256&&queues_[lane].pop(r);++n){any=true;if(!write(r)){active_=false;close();return;}}
                if(GetTickCount64()-check>=1000){
                    if(!std::filesystem::exists(root_/L"enabled.flag")){active_=false;break;}
                    Record status;status.h.type=7;status.h.qpc=clock();status.h.a=drops_[0].load();status.h.b=drops_[1].load();status.h.c=drops_[2].load();
                    if(!write(status)){active_=false;break;}check=GetTickCount64();
                }
                if(quit_&&!any)break;
                if(!any)Sleep(10);
            }
        }catch(...){active_=false;}
        close();
    }
    std::thread writer_;
};
}
