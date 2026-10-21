# Residual echo recovery adjustment — 2026-09-28

The 22:32:20–22:34:20 incident had no missing results, reference underruns, resets or diagnostic drops. Speaker-correlated content remained in model output. This change targets neural state recovery; it does not replace the earlier reference-delivery repair.

## Implementation

With an active speaker reference, retain the existing 0.995 recurrent-state multiplier when output energy is below 3% of aligned mic energy (over 15.2 dB reduction). Between 3% and 50%, use 0.98 to recover from weak suppression. Leave state unchanged for reference silence, invalid energy or output at least 50% of mic energy (speech-like/less than 3 dB reduction). Output samples are not gated or scaled by this policy. Both model stages' recurrent states receive the same multiplier after producing the hop. This is an empirical modification to the pretrained model, not proof of the internal cause of every leakage event.

Microphone latency stays 4096 frames / 85.33 ms. Reference wait stays 30 ms. Model weights, NPU device selection, EQ, speaker volume and microphone gain are unchanged. Type-11 diagnostics now include the actual decay float bits in field d and set flags according to whether decay was applied.

## Controlled local replay

Input: the same recorded raw microphone and speaker reference from the reported interval, 121.992 seconds including context. Both signals were filtered with the production 63-tap input FIR and decimated to 16 kHz. Native C++ DTLN inference ran on the installed Intel NPU. The 384-frame model delay was removed for scoring. Timelines were reconstructed from recorded sample indices/source timestamps; this is model replay, not a new simultaneous acoustic A/B test or an exact rerun of every live ASRC interpolation.

| Reported window | Previous output RMS | Updated output RMS | Difference |
|---|---:|---:|---:|
| 22:32:52.009–22:32:55.009 | -41.049 dBFS | -45.058 dBFS | 4.009 dB lower |
| 22:33:50.009–22:33:53.009 | -47.907 dBFS | -47.879 dBFS | 0.028 dB higher |

The first window's loudest 100 ms fell from -28.191 to -32.903 dBFS (4.713 dB). Its 95th-percentile 100 ms energy fell 3.841 dB. These are output-energy comparisons in user-reported, speaker-correlated windows, not isolated echo ERLE: the recording may contain near speech or unrelated noise.

Pure near speech was taken from the same capture with speaker reference below 1e-8. A separate 40-second double-talk fixture used a different background segment (original seconds 80–120), injecting that known voice at seconds 10–14. Compare output with and without that voice under each implementation, projecting the difference onto the known voice. This is a controlled digital mixture, not ground-truth live acoustic double talk.

| Injected voice | Previous differential gain | Updated differential gain | Previous / updated differential SDR |
|---|---:|---:|---:|
| Quiet (0.1x) | -7.280 dB | -7.480 dB | -1.090 / -1.375 dB |
| Normal (1x) | -0.293 dB | -0.283 dB | 15.002 / 14.933 dB |

Quiet-voice preservation remains a known weakness; the update adds about 0.20 dB attenuation on that harder mixture. Normal speech gain is effectively unchanged. More aggressive unconditional decay was rejected because it worsened the second leakage window by about 4.36 dB. Reference time shifts and gain scaling were also rejected. The selected change is a measured partial improvement, not complete echo inaudibility across all content.

Private originals/replays remain in C:\ProgramData\SoundControl\aec-diagnostics\analysis-residual-20260928 under the existing total disk budget. Relevant input files: input.f32, fixture.npz, doubletalk-*.f32. Baseline output: delay-0.f32. Selected output: bounded-1.f32. Double-talk outputs: doubletalk-*-baseline-1.f32 and doubletalk-*-candidate-1.f32. Existing PersonalToolsNeuralReplay can reproduce the selected model output using the native build, runtime directory and input.f32. No audio was uploaded.

## Verification and deployment

Nine native CTest suites passed, including reference-silent/speech/weak-echo state policy checks and existing timing, format, bypass, channel and latency regressions.

Installed APO SHA256: C8338A904B80EB1AD52A4580BDAAB098C441FBDCFB981D51EBA6F79DCC95EFB7. Previous installed APO saved in build/aec-residual-backup-20260928-224943. Windows Audio restarted once; installed hash matches tested artifact. Initial reopened capture showed neuralState=2, reference=1, error=0 with no new missing output after preparation. Diagnostic recording remains enabled. Acoustic success after installation still needs real-use confirmation; do not infer it from active callbacks alone.
