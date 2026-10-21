# NPU 반향 제거 검증 — 2026-10-20

이 노트북(Intel Core Ultra 9 275HX, Intel(R) AI Boost, 드라이버 32.0.100.4778)에서
`PersonalToolsNpuProbe`로 측정했습니다. OpenVINO 2026.3.1 Windows 런타임과
DTLN-AEC 256 모델(해시 고정)을 NPU 전용 패키지(`build\npu-package\npu`)로 묶어 사용했습니다.

## 방법

- 1단계: `DtlnNpu`로 128샘플(16 kHz, 8 ms) 블록 1,500개를 NPU에서 추론하고 시간을 잽니다.
- 2단계: APO와 같은 `NeuralStream` 경로(48 kHz, QPC 타임스탬프, 실시간 속도)에 16초 신호를 넣습니다.
  - 0–8초: 스피커 음성만(합성 유성음, 희소 반사 240개·RT60 300 ms, 약한 스피커 압축)
  - 8–12초: 근거리 음성만
  - 12–16초: 동시 발화
- 출력은 4,096프레임(85.33 ms) 지연을 맞춰 입력과 비교합니다. 첫 2초는 모델 예열로 제외합니다.

## 결과

| 항목 | 1차 실행 | CTest 실행 | 기준 |
| --- | --- | --- | --- |
| NPU 모델 열기 | 1.41 s | 0.19 s (컴파일 캐시) | — |
| 블록 추론 평균 / p99 / 최대 | 1.194 / 1.591 / 3.266 ms | 1.175 / 1.620 / 2.278 ms | 8 ms 미만 |
| 스피커 반향 감쇠 (ERLE) | 26.5 dB | 26.5 dB | 15 dB 이상 |
| 근거리 음성 레벨 변화 | −0.1 dB | −0.1 dB | −6 dB보다 큼 |
| 동시 발화 SDR | 16.2 dB | 16.2 dB | 참고값 |
| 놓친 출력 프레임 | 0.28% | 0.47% | 2% 이하 |
| NPU 작업자 재시작 | 0 | 0 | 0 |

## 범위

합성 신호와 실제 NPU·실제 처리 경로의 검증입니다. MOTU 마이크와 SMSL 스피커가 있는 방에서의
음향 품질, Windows 오디오 엔진(audiodg)에 설치된 APO의 동작은 설치 후 별도로 확인해야 합니다.
NPU 초기화·추론 실패 시 자동 재시도는 `PersonalToolsNeuralTests`가 런타임이 없는 경로로 검증합니다.

재현:

```powershell
$env:PERSONALTOOLS_NPU_DIR = (Resolve-Path build\npu-package\npu).Path
build\windows-native\Sources\Windows\PersonalToolsNpuProbe.exe
```
