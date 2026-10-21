# 2026-09-28 AEC timing and reference delivery repair

## Evidence

The user confirmed the audible incidents matched 22:17:26 and 22:18:13. Mic callbacks continued at approximately 10.67 ms during a 64 ms gap in reference delivery to the APO. When delivery resumed, consecutive reference samples arrived together; their source timestamps remained contiguous. This localizes the delay to the reference collection/delivery path rather than lost source samples or an NPU inference stall. Historical recordings did not include producer wake/read/publish timestamps, so they cannot distinguish producer scheduling delay from WASAPI buffer availability with certainty.

A separate 1.0157 ms mic timestamp correction reset the model and introduced a 1214-frame output gap (25.292 ms). The 1.1311/1.1080 ms corrections caused another reset burst. No queue loss accompanied these events.

## Changes applied

- Kept 4096-frame / 85.33 ms microphone latency and original 30 ms reference wait. A trial 40 ms wait was reverted; the user rejected adding latency without investigating its origin.
- Keep recurrent/FIR state across contiguous, loss-free mic packets whose timestamp correction is <=5 ms. Index gaps, queue loss, or larger timestamp discontinuities still reset. This is a bounded tolerance for observed jitter, not proof that every small timestamp jump is harmless.
- Replace the loopback worker's normal-priority Sleep(3) polling with WASAPI event callbacks and MMCSS Audio registration. A 20 ms bounded wait also checks stop/configuration and drains packets if an event is delayed. Existing stream buffer duration is unchanged.
- Producer diagnostics type 13: source timestamp and device position, frames, WASAPI flags, wake QPC and publish QPC. Type 14 records MMCSS registration success. Type 15 records wait start/end and wait result. This makes remaining producer-versus-consumer delays observable.

API references: https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording and https://learn.microsoft.com/en-us/windows/win32/coreaudio/audclnt-streamflags-xxx-constants .

## Validation/deployment

Nine native test suites passed, including recorded timestamp correction regressions and reset preservation for genuine index/queue/large timestamp discontinuities. Offline reference-timeline replay showed that just adding waiting did not solve all late packets within the original budget; this supported correcting delivery instead of increasing latency. This replay checks reference availability, not acoustic quality.

APO installed SHA256: 7C9DC2C3153DCA754E839B8991021AEA8163F6C27AFC59BD1D24031DC5E9CA60.
UI native bridge installed SHA256: 7DE0617FD8646C38A1E3BC8BC40299AE31E64F5DB71679F707644272798E4849.
Backup: build/aec-timing-backup-20260928-222848 and build/aec-reference-backup-*.
Audio service and normal-user UI restarted; configuration/models preserved. NPU state 2 and reference active verified on a reopened capture stream. Startup and bridge restart caused expected temporary reference gaps; exclude those from continuity assessment.

At 22:29:59, producer diagnostics verified MMCSS registration successful, 5558 packets, maximum delivery gap 10.893 ms. This short observation does not prove no long-term stalls or residual acoustic echo. Raw diagnostic capture remains enabled within its existing cap.
