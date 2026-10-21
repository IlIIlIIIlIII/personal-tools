#pragma once
// Shared-memory contract between the capture APO (audiodg) and the NPU host
// process. OpenVINO never loads into audiodg, where OEM effects may already have
// loaded a different OpenVINO/TBB under the same DLL names.
// Producers and consumers are single-threaded per queue and lock-free; the
// audio thread only copies packets in and reads results out.
#include "Shared.hpp"
#include <aclapi.h>
#include <array>
#include <atomic>
#include <cstdint>
#include <string>

namespace personaltools::win::neural {
inline constexpr unsigned packetFrames=256, queueSize=128, ringSize=32768, channelCount=4;
inline constexpr std::uint32_t sectionMagic=0x3350494E; // "NIP3"
// Host states published per channel. -2 is reported by the APO side when the host is silent.
enum : std::int32_t { hostMissing=-2, hostFailed=-1, hostIdle=0, hostOpening=1, hostRunning=2 };

struct Packet { std::uint64_t time=0,index=0; std::uint32_t generation=0,frames=0; std::array<float,packetFrames> samples{}; };
struct Queue {
    std::array<Packet,queueSize> packets{};
    std::atomic<std::uint64_t> w{0},r{0};
    bool push(const Packet& item) {
        const auto x=w.load(std::memory_order_relaxed);
        if(x-r.load(std::memory_order_acquire)>=queueSize)return false;
        packets[x%queueSize]=item; w.store(x+1,std::memory_order_release); return true;
    }
    bool peek(Packet& item) {
        const auto x=r.load(std::memory_order_relaxed);
        if(x==w.load(std::memory_order_acquire))return false;
        item=packets[x%queueSize]; return true;
    }
    void pop() { r.fetch_add(1,std::memory_order_release); }
};
struct Result { std::atomic<std::uint64_t> stamp{0}; std::atomic<std::uint32_t> sample{0}; };
// Stamps carry the claim generation so a new stream never reads a previous stream's output.
inline std::uint64_t stamp(std::uint32_t generation,std::uint64_t index) {
    return (static_cast<std::uint64_t>(generation&0xFFFFFFu)<<40)|((index+1)&((1ull<<40)-1));
}
struct Channel {
    std::atomic<std::uint64_t> owner{0};          // APO claim token; 0 = free
    std::atomic<std::uint32_t> generation{0};     // bumped by every claim
    std::atomic<std::uint32_t> hostGeneration{0}; // generation the host has reset for
    std::atomic<std::uint64_t> apoTick{0},hostTick{0}; // GetTickCount64 heartbeats
    std::atomic<std::int32_t> state{hostIdle},failure{0},restarts{0};
    std::atomic<std::uint64_t> lost{0},completed{0};
    // Diagnostics: hops processed against silence because no loopback reference covered
    // them (speaker idle, or reference late), and samples the APO had to play unprocessed.
    std::atomic<std::uint64_t> unreferenced{0},apoMisses{0};
    // Measured echo delay behind the reference timestamps (-1 = not yet known) and how much
    // later the host renders the reference to keep it just ahead of the echo.
    std::atomic<std::int32_t> echoDelayUs{-1},referenceShiftUs{0};
    Queue reference,microphone;
    std::array<Result,ringSize> results{};
};
struct Section {
    std::atomic<std::uint32_t> magic{0},hostPid{0};
    std::array<Channel,channelCount> channels{};
};
static_assert(std::atomic<std::uint64_t>::is_always_lock_free && std::atomic<std::uint32_t>::is_always_lock_free);

// Pagefile-backed section. In audiodg it is created in the Global namespace with
// the installed settings DACL (the signed-in user may read/write); the host opens it.
class Mapping {
public:
    Mapping()=default;
    Mapping(const Mapping&)=delete;
    ~Mapping() { close(); }
    static std::wstring defaultName() {
#ifdef PERSONALTOOLS_TESTING
        return L"Local\\PersonalToolsNeural-test-"+std::to_wstring(GetCurrentProcessId());
#else
        // Versioned with the layout: an APO and host from different builds never share a section.
        return L"Global\\PersonalToolsNeural-v3";
#endif
    }
    bool open(const std::wstring& name,bool create) {
        if(section_)return true;
        mapping_=OpenFileMappingW(FILE_MAP_READ|FILE_MAP_WRITE,FALSE,name.c_str());
        if(!mapping_&&create) {
            PSECURITY_DESCRIPTOR descriptor=nullptr;
            SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES),nullptr,FALSE};
            if(name.rfind(L"Global\\",0)==0) {
                PACL acl=nullptr;
                const auto settings=(dataDirectory()/L"state-v1.bin").wstring();
                if(GetNamedSecurityInfoW(settings.c_str(),SE_FILE_OBJECT,DACL_SECURITY_INFORMATION,nullptr,nullptr,&acl,nullptr,&descriptor)==ERROR_SUCCESS)
                    security.lpSecurityDescriptor=descriptor;
            }
            mapping_=CreateFileMappingW(INVALID_HANDLE_VALUE,&security,PAGE_READWRITE,0,static_cast<DWORD>(sizeof(Section)),name.c_str());
            if(descriptor)LocalFree(descriptor);
        }
        if(mapping_)section_=static_cast<Section*>(MapViewOfFile(mapping_,FILE_MAP_READ|FILE_MAP_WRITE,0,0,sizeof(Section)));
        if(!section_){close();return false;}
        if(create){std::uint32_t empty=0;section_->magic.compare_exchange_strong(empty,sectionMagic);}
        if(section_->magic.load(std::memory_order_acquire)!=sectionMagic){close();return false;}
        return true;
    }
    Section* get() const { return section_; }
    void close() {
        if(section_)UnmapViewOfFile(section_);
        if(mapping_)CloseHandle(mapping_);
        section_=nullptr; mapping_=nullptr;
    }
private:
    HANDLE mapping_=nullptr;
    Section* section_=nullptr;
};
}
