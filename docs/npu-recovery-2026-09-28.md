# NPU initialization failure recovery — 2026-09-28

## Observed failure

The configured Lewitt/MOTU capture and KALI/SMSL render endpoint IDs matched active endpoints. The installed NPU payload passed Assert-NpuPayload. Live capture callbacks continued at 48 kHz with neuralState=-1, error=21 (the APO maps any neural worker failure to ERROR_NOT_READY), and neuralBlocks=0. This error is not a specific Intel/OpenVINO error code.

Earlier diagnostic records showed initialization entering state 1 and terminating at -1. The installed DLL did not yet include exception records, so the original low-level cause cannot be reconstructed. Existing code exits the worker after an exception and stays failed for the lifetime of that stream. The same installed runtime/model package completed 125 synthetic NPU hops in the current user process (0.606 s); this alone did not verify audiodg.

## Applied change and recovery

DtlnNpu now reports the failed core/model/request operation and ov_get_last_err_msg(), and records GetLastError for runtime load failures. The already-prepared type-12 exception recorder was included in the rebuilt DLL. Reference: https://docs.openvino.ai/2026/api/c_cpp_api/group__ov__base__c__api.html

Backed up the installed DLL, replaced it and restarted Windows Audio through an elevated deployment. Existing EQ/AEC configuration, models, and diagnostic recording preference were preserved. Installed SHA256: A5DA996FF0204DAC36600D835D6C6810EFBF03A86A2D70B0922F55958A9BA4A8. Backup and deployment result: build/npu-error-repair-20260928-035620. Both Windows audio services returned to Running.

The code change improves diagnostics; the process restart recovered the failed NPU state. This does not establish the original failure's root cause or prove it cannot recur. No automatic restart/retry policy or audio algorithm was added.

## Live verification

All nine native CTest tests passed. Seven new audiodg diagnostic sessions recorded lifecycle 1 -> 2, with no exception records in their initial segments. A shared-mode microphone probe opened successfully and observed neuralState=2/error=0.

At 03:57:21–03:57:51 KST, 30 samples from the same capture instance all had 48 kHz, enabled=1, neuralState=2, reference=1, error=0, and fresh callbacks. Neural blocks increased 5251 -> 8911; missingFrames remained 434622 throughout. Evidence: measurements/aec-npu-recovery-2026-09-28.csv.

Reference availability fluctuated during earlier post-restart sampling and missingFrames rose then. The final 30-second window proves live NPU execution with reference and no additional missing output in that window, not long-term absence of dropouts or acoustic quality. No new audio export or remote upload was performed.
