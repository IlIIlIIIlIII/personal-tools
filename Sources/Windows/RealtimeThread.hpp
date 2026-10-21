#pragma once
// Scheduling for the real-time helper threads that live outside audiodg (loopback
// reference in the app, NPU workers in the host). Both feed a deadline of a few tens of
// milliseconds, so a backgrounded or minimised app must not be slowed by EcoQoS power
// throttling or coarse timers. Link with avrt.
#include <windows.h>
#include <avrt.h>

namespace personaltools::win {
class RealtimeThread {
public:
    RealtimeThread() {
        THREAD_POWER_THROTTLING_STATE throttling{THREAD_POWER_THROTTLING_CURRENT_VERSION,THREAD_POWER_THROTTLING_EXECUTION_SPEED,0};
        SetThreadInformation(GetCurrentThread(),ThreadPowerThrottling,&throttling,sizeof(throttling));
        DWORD task=0;mmcss_=AvSetMmThreadCharacteristicsW(L"Audio",&task);
        if(!mmcss_)SetThreadPriority(GetCurrentThread(),THREAD_PRIORITY_TIME_CRITICAL);
    }
    ~RealtimeThread(){if(mmcss_)AvRevertMmThreadCharacteristics(mmcss_);}
    RealtimeThread(const RealtimeThread&)=delete;
    // High-resolution one-shot wait; independent of the process timer resolution.
    class Timer {
    public:
        Timer():handle_(CreateWaitableTimerExW(nullptr,nullptr,CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,TIMER_ALL_ACCESS)){}
        ~Timer(){if(handle_)CloseHandle(handle_);}
        Timer(const Timer&)=delete;
        void wait(unsigned microseconds) {
            LARGE_INTEGER due{};due.QuadPart=-static_cast<LONGLONG>(microseconds)*10;
            if(handle_&&SetWaitableTimer(handle_,&due,0,nullptr,nullptr,FALSE))WaitForSingleObject(handle_,50);
            else Sleep(1);
        }
    private:
        HANDLE handle_;
    };
private:
    HANDLE mmcss_=nullptr;
};
}
