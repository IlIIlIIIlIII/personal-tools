# Ubuntu 오디오 검증 기록

검증 환경은 Ubuntu 26.04 · GNOME 50 · Wayland, 사용자 PipeWire/WirePlumber
세션이다. 물리 입력은 MOTU M2의 Mic1, 출력은 SMSL USB Audio를 사용했다.
마이크 프레임은 유한값과 프레임 수를 검사한 뒤 버리며 음성 파일을 저장하지
않았다. 장시간 검사 중 기존 기본 장치와 사용자의 재생 스트림은 변경하지 않았다.

## 실행 파일과 경로

최종 패키지와 두 번째 공동 부하 검사에 사용한 실행 파일은 같은 빌드다.

- 빌드: `build/linux/native/Sources/Linux/Audio/PersonalToolsAudio`
- 설치: `/usr/lib/personal-tools/audio/PersonalToolsAudio`
- 구성: `RelWithDebInfo`
- SHA256: `aafac2287071865f0fb618f7d0a98fc5a4c462343f21826ea35e31dd465a472b`
- 런타임 관측: `build/linux/npu-runtime/runtime.json`

NPU 실행은 실제 OpenVINO NPU 장치 추론으로 확인한다. 런타임 또는 모델 초기화가
실패하면 NPU 오류와 DSP 저하 상태를 보고하며, CPU 실행을 NPU 성공으로 표시하지
않는다. 일반 앱이 마이크를 소비하지 않는 idle 상태는 UI에서 대기로 구분한다.

## 지속 부하 결과

두 번째 공동 검사는 **통과했다.** 2026-10-03 11:52:44.439914부터
12:22:44.440914 KST까지 실제 오디오 측정과 vLLM 64K · CPU parameter
offload 8GiB의 연속 요청이 **1800.001초** 겹친다. 완료된 두 원본 보고서의
공통 시간 구간을 `Tests/Linux/Audio/summarize_joint.py`로 대조했으며 실패
목록은 비어 있다.

| 관측 항목 | 결과 |
|---|---:|
| 실제 NPU 추론 증가량 | 449,980회 |
| 오디오 상태 표본 | 361개, 모두 실제 NPU 정상 |
| 새 missing frame / dropped packet | 0 / 0 |
| 비정상 PCM 값 | 0 |
| 엔진 RSS | 66,516KiB 고정, 측정 전후 변화 0 |
| 추론 누적 최대 / RT 콜백 누적 최대 | 16.524477ms / 1.151715ms |
| 처리 지연 | 85.333333ms |
| 오디오와 겹치는 성공 모델 요청 | 31회 |
| 요청 사이 최대 간격 | 0.016632초 |
| 공통 구간의 최소 GPU 여유 | 6,871MiB |
| 기존 기본 장치 메타데이터 | 변경 없음 |

vLLM 연속 부하는 12:23:35 KST까지 1851.334초 유지됐다. 이어 실제 서비스
중지와 재기동·워밍업·응답을 확인했고, 12:24:41 KST에 최종 `passed=true`로
완료했다. 오디오 검사용 유닛과 엔진은 12:22:44 KST에 정상 종료했다.

원본 측정 경로:

- 오디오: `build/linux/audio-qa/soak-joint-v2/`
- vLLM: `build/linux/vllm-qa/joint-v2/`
- 최종 상관 보고서: `build/linux/audio-qa/joint-result-v2.json`

GPU 여유는 요청 완료 직후 값이 아니라 공통 구간의 **1초 간격 연속 측정**으로
검사한다. 1800초 이상의 겹치는 구간, 지속적인 성공 모델 요청, 실제 NPU 출력,
프레임 손실·비정상 샘플, 기존 기본 장치 복원을 함께 확인한다.

완료된 원본에서 상관 보고서를 다시 생성하는 명령은 다음과 같다.

```sh
python3 Tests/Linux/Audio/summarize_joint.py \
  --audio build/linux/audio-qa/soak-joint-v2 \
  --vllm build/linux/vllm-qa/joint-v2 \
  --output build/linux/audio-qa/joint-result-v2.json
```

이전 결과도 보존했다. `soak-persistent/result.json`의 오디오 단독 30분은
449,980회 NPU 추론, 새 missing frame과 dropped packet 0, 비정상 PCM 0,
RSS 66,648KiB 고정으로 통과했다. 그러나 첫 vLLM 공동 실행은 GPU 여유가
3GiB 아래로 내려가 보호 중지됐으므로 **공동 검사 실패**다.
`joint-result-v1.json`은 이 실패와 부분 공통 구간을 그대로 기록한다.
두 실행은 서로 다른 빌드이므로 첫 실행을 최종 패키지 검증으로 대체하지 않는다.

## 실제 장치 라우팅과 회귀

EQ를 켜면 서비스가 기존 기본 출력을 저널에 먼저 기록한 뒤
`personal-tools-output`을 기본 출력으로 선택한다. 처리된 마이크의 기본 입력
사용은 별도 사용자 동작으로 요청한다. 수동 장치 변경은 보존하며, 비활성화나
종료 시 아직 서비스가 소유한 경로만 복원한다. 가상 출력과 마이크는 연결할
물리 장치가 있을 때만 존재하므로, 장치 분리 중에는 조용한 가상 장치가
기본값으로 남지 않는다. 새로 연결된 블루투스 출력은 기본 출력이 된다.

출력 복원에 실패해도 마이크 복원은 독립적으로 시도한다. 실패한 경로의 저널은
남겨 다음 복원에서 재시도한다. 원래 사용자가 명시한 기본값이 없었다면 특정
물리 장치를 고정하지 않고 자동 선택 상태로 돌려놓는다.

- `AudioRoutes.Tests.cs`: 소유권, 수동 변경, 재시작, 분리/재연결, 중간 실패와
  독립 복원 회귀 **18개 통과**.
- 격리 PipeWire/WirePlumber 정책 세션: 실제 메타데이터와 스트림 이동 **8개 통과**.
- 실제 데스크톱 세션: 기존 재생 스트림의 가상 출력 이동, 마이크 선택, 원래 출력
  복귀와 기본 메타데이터의 정확한 복원 **8개 통과**.
  원본은 `build/linux/audio-qa/live-routes.json`이다.
- 패키지와 같은 native 실행 파일의 프로토콜 **15개**, 장치 수명 **10개** 통과.
- 최신 서비스 회귀 **37개**, 실제 GTK 위젯/페이지 회귀 **15개** 통과.

실제 라우팅 검사는 첫 지속 검사 종료 후, 두 번째 지속 검사 시작 전에 수행했다.
검사 스크립트는 활성 상태의 `personal-tools-audio-acceptance*` 유닛이 있으면
기본 장치 변경을 거절한다. GTK 회귀는 창을 띄우지 않고 가짜 서비스 응답을
사용하며 사용자의 클립보드나 설정을 변경하지 않는다.

## 남은 검증 범위

이 결과는 처리 경로, 실제 NPU 실행과 지속 안정성을 검증한다. 원거리/근거리
발화·동시 발화, 스피커 반향, 여러 방과 마이크 배치에서의 AEC 음향 품질은
검증하지 않았다. 음향 품질 지표나 청취 평가가 통과했다고 주장하지 않는다.

측정되는 최대 추론 시간과 콜백 시간은 엔진 수명의 누적 최대값이므로 워밍업이
포함될 수 있다. 손실 프레임, dropped packet, 추론 횟수의 변화량은 별도로 저장한
측정 시작 baseline을 기준으로 산출한다.

## 2026-10-04 실사용 중 짧은 출력 끊김 조사

이전의 30분 검증과 별개로, 실제 사용 중 간헐적인 출력 오류를 확인했다.
사용자 증상은 약 3분 이내에 한 번씩 아주 짧게 튀는 소리다.
Ubuntu 26.04.1, kernel 7.0.0-38, PipeWire 1.6.2에서 Spotify와
Discord의 출력이 Personal Tools EQ를 거쳐 SMSL로 재생되고 있었다.

- 그래프 드라이버는 MOTU 입력(`alsa_input.hw_M2_0`, 노드 87)이며
  SMSL 출력(노드 80), EQ와 마이크 처리가 같은 그래프에 연결돼 있었다.
  처리 단위는 256 frames / 48 kHz = 약 5.33ms다. 노드 ID는 이번 세션 값이다.
- SMSL의 `front:2p ... resync` 경고가 반복됐다. 23:09:43 KST까지
  이번 사용자 세션에서 경고 25줄을 확인했으며, 별도로 억제된 경고도 있다.
- 조사 중 엔진의 마이크 콜백 누적 최대는 6.246309ms였다.
  처리 주기 초과 이력이지만, 누적 최대에는 발생 시각이 없으므로
  이를 특정 끊김의 원인으로 단정할 수 없다.
- 새 수집기로 **23:10:20.903~23:10:21.334 KST** 구간에 SMSL resync와
  `pw-top` ERR 증가를 함께 포착했다. MOTU 드라이버 +2,
  `personal-tools-mic-process` +10, `personal-tools-output` +2였다.
  첫 약 38초간 NPU discontinuities +5, missingFrames +4,348도 기록됐다.
  NPU 손실 수치는 마이크 처리용이며 그 자체가 스피커 출력 손실량은 아니다.
- 같은 구간에 SMSL/MOTU USB 분리나 오디오 서비스 재시작은 관측되지 않았다.
  23:04:51의 Thunderbolt/USB3 허브 재연결은 별도 버스의 사건으로,
  반복되는 모든 출력 튐을 설명하는 증거로 취급하지 않는다.

현재 결론은 **공유 오디오 그래프의 처리 시한 초과와 SMSL 재동기화가
실제로 발생한다**는 것이다. 사용자가 들은 순간과의 직접 대조, 최초 지연이
콜백 실행·스케줄링·USB 장치 타이밍 중 어디서 시작되는지는 추가 관측이 필요하다.
EQ 계산 오류, NPU 자체 오류, USB 케이블 불량을 확정하지 않았다.
오디오 서비스 재시작, 라우팅 변경, 버퍼 조정은 이번 조사에서 하지 않았다.

### 지속 진단 로깅

`Scripts/linux-audio-diagnostics.py`는 별도 저우선순위 프로세스에서
`pw-top`을 지속 구독하며 ERR 증가를 기록한다. 5초 간격 노드 표본,
2초 간격 기존 엔진 상태·시스템 부하/pressure, 30초 간격 오디오 노드
구성, 오디오 서비스 저널과 USB 관련 커널 이벤트를 UTC 및 monotonic
시각과 함께 JSONL로 저장한다. 원본 journal 시각도 보존한다.
`pw-top` 표본 사이에 발생한 오류의 정확한 실행 순서는 판별하지 못한다.
첫 누적 카운터는 새 오류로 계산하지 않는다.

음성/재생 PCM, 곡명, 창 제목, EQ 파일 경로를 수집하지 않는다.
앱·장치 이름, 상태 및 관련 서비스 로그 메시지는 저장된다.
오디오를 열거나 장치 설정을 변경하지 않는다. 로그는 8MiB × 최대 5개로
순환하며 권한은 디렉터리 0700, 파일 0600이다.

현재 사용자에게 설치하고 활성화한 유닛은
`personal-tools-audio-diagnostics.service`다. 다음 로그인에서도 시작한다.
로그는 `~/.local/state/personal-tools/audio-diagnostics/events.jsonl`에 있다.
초기 조사 저널은 같은 디렉터리의 `initial-*.txt`에 별도 보존했다.

수집기를 수동 설치하거나 업데이트하는 명령(저장소 루트):

```sh
install -d -m 700 ~/.local/lib/personal-tools ~/.local/state/personal-tools/audio-diagnostics
mkdir -p ~/.config/systemd/user
install -m 600 Scripts/linux-audio-diagnostics.py ~/.local/lib/personal-tools/
install -m 644 Scripts/Linux/personal-tools-audio-diagnostics.service ~/.config/systemd/user/
systemctl --user daemon-reload
systemctl --user enable --now personal-tools-audio-diagnostics.service
# 기존 설치를 업데이트한 경우 수집기만 재시작
systemctl --user restart personal-tools-audio-diagnostics.service
```

중지 및 자동 시작 해제:

```sh
systemctl --user disable --now personal-tools-audio-diagnostics.service
```

검증: `python3 Tests/Linux/Audio/test_diagnostics.py` 5개 통과,
6초 실제 수집/정상 종료 확인, 설치된 서비스의 실제 engine/graph/node
수집 및 resync/ERR 증가 포착 확인. 로그 수집 개선 검증이며 끊김 해결이나
음향 품질 검증으로 간주하지 않는다.

ERR 해석과 ALSA 타이밍 참고:
[PipeWire pw-top](https://docs.pipewire.org/page_man_pw-top_1.html),
[WirePlumber ALSA buffer properties](https://pipewire.pages.freedesktop.org/wireplumber/daemon/configuration/alsa.html#alsa-buffer-properties).

## 2026-10-19 장치 분리·블루투스 연결 시 기본 출력

증상: SMSL을 분리해도 기본 출력이 `personal-tools-output`에 남아 내장
스피커로 넘어가지 않았고, WF-1000XM6를 연결해도 기본 출력이 바뀌지 않았다.

원인:

- WirePlumber 0.5는 `default.configured.audio.sink`의 노드가 존재하는 동안
  그 노드를 고른다. 엔진의 가상 출력은 물리 장치와 무관하게 항상 존재했고,
  라우트 저널이 `overridden`이면 서비스도 분리 시 기본값을 해제하지 않았다.
- WirePlumber에는 연결 시 전환 정책이 없다. 기억된 기본값의 대체 선택은
  `priority.session`과 이력 순서로 정해지는데, 가상 출력의 우선순위가 0이라
  이어폰이 끊기면 EQ를 거치지 않은 SMSL(1109)이 선택됐다.
- 엔진은 id 0의 모든 core 오류를 연결 끊김으로 처리했다. 장치 재연결 중
  사라진 포트에 대한 링크 실패 뒤의 `unknown resource` 오류만으로도 종료했다.

변경:

- 엔진은 가상 출력/마이크 루프백을 대상 물리 장치가 있을 때만 게시하고,
  우선순위를 물리 장치보다 50 높게 둔다. 연결 끊김은 core의 `-EPIPE`만으로
  판단한다.
- 서비스는 새로 나타난 블루투스 출력(WirePlumber 루프백 싱크 또는 내부가
  아닌 bluez5 노드)을 기본 출력으로 한 번 선택하고, 이를 수동 변경처럼 다룬다.
  EQ를 명시적으로 다시 켜면 처리 출력을 다시 선택한다.

검증:

- `Tests/Linux/Audio/test_endpoint_fallback.py`: 격리 PipeWire/WirePlumber에서
  실제 엔진으로 분리 시 대체, 재연결 시 복귀, 이어폰 해제 시 처리 출력 복귀,
  마이크 분리/재연결 **8개 통과**. 기존 설치 엔진은 분리 시 대체에서 실패했다.
- `AudioRoutes.Tests.cs` **25개**, 격리 WirePlumber 라우팅 **10개**,
  프로토콜 **15개**, 장치 수명 **10개**, 서비스 회귀 **59개**
  (`dbus-run-session` 안에서 실행) 통과.
- 실제 SMSL 분리와 WF-1000XM6 연결은 이 검증에 포함되지 않는다.
