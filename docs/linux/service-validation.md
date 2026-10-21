# Ubuntu 서비스·AI 실제 검증 기록

관측일: 2026-10-03. 합성 테스트 데이터만 사용했으며 이 문서와 진단 내보내기는 API 키·사용자 클립보드 원문·음성을 포함하지 않는다. GNOME이 실행 중인 실제 데스크톱에서 검증했다. 별도의 합성 데스크톱 그래픽 벤치마크는 수행하지 않았다.

| 범위 | 결과 | 근거 |
|---|---|---|
| 서비스·FD·클립보드·페이지네이션·진단 보호 | 37개 검사 통과 | [일반 서비스 suite](../../Tests/Linux/Service/Program.cs) |
| vLLM 포트 소유권·TIME_WAIT 재기동 | 2개 검사 통과 | [실제 소켓 회귀](../../Tests/Linux/Service/test_vllm_runtime.py) |
| 실제 Antigravity 로그인·HTTP/SSE·범위 검색·이미지 태깅 | 5개 검사 통과, 이미지 태그 24개 | [보고서](../../build/linux/antigravity-live.json) |
| 실제 systemd 자동 시작 상태와 UI 일치 | 5개 검사 통과, 당시 실행 상태 inactive 유지, 원래 disabled 복원 | [보고서](../../build/linux/autostart-live.json) |
| 실제 GNOME 및 logind inhibitor 생성·분리 조회·해제 | 6개 검사 통과 | [보고서](../../build/linux/desktop-qa/result.json) |
| Ubuntu3 설치 apphost 설정 지속·종료 응답·재기동 | 16개 검사 통과 | [보고서](../../build/linux/installed-service-smoke.json) |
| vLLM 8K·16K·32K·64K | 모든 프로필 통과 | [프로필·부하 보고서](../../build/linux/vllm-qa/joint-v2/result.json) |
| NPU + vLLM + 실행 중인 GNOME 데스크톱 공존 | 공통 1,800.001초 통과, 실제 요청 31회 | [상관 보고서](../../build/linux/audio-qa/joint-result-v2.json) |
| 실제 HDR 수동·자동 전환과 원래 상태 복원 | 23개 검사 통과 | [HDR 보고서](../../build/linux/desktop-qa/hdr-result.json) |
| HDR 이후 모델의 이전 실행 상태 복원 | 통과, 이전 active 상태와 실제 API 준비 복원 | [수명 복원 보고서](../../build/linux/desktop-qa/finish-result.json) |

## 고정 모델과 실제 실행 프로필

NVIDIA RTX 5090 Laptop GPU(24,463 MiB), 드라이버 610.57.04에서 RedHatAI/Qwen3.8-27B-INT4의 revision `91bd022d5b49442a868bc35008f6c21e1860edfa`를 사용했다. 독립 CPython 3.12 환경, vLLM 0.30.0, 일치하는 CUDA 13.0 헤더·컴파일러 13.0.88을 사용한다.

선택·저장된 최종 프로필은 `context=65536`, `maxSequences=1`, `cpuOffloadGiB=8`이다. 사용자 설정 `~/.config/personal-tools/vllm.json`과 실행 PID에 연결된 `running-config.json`을 대조했다. CPU 가중치 오프로드는 일부 가중치를 RAM에서 CUDA로 옮기는 설정이며 모델 추론은 CUDA에서 실행한다. 긴 입력의 응답 시간은 아래 실측값을 참고한다.

| 프로필 | 실제 근접 한도 입력 | 해당 요청 시간 | 한국어·JSON·도구 호출·SSE·앞부분 표식 회수 |
|---|---:|---:|---|
| 8K | 7,985 토큰 | 71.1초 | 통과 |
| 16K | 16,177 토큰 | 151.5초 | 통과 |
| 32K | 32,561 토큰 | 280.3초 | 통과 |
| 64K | 65,329 토큰 | 573.0초 | 통과 |

## 30분 공동 부하

실제 공통 측정 창은 2026-10-03 11:52:44.439914–12:22:44.440914 KST이며, 서로 다른 실행의 시간을 합산하지 않았다. vLLM은 총 1851.334초 동안 31회 응답을 완료했고, 1초 간격 GPU 관측 최소 여유는 **6,871 MiB**로 3 GiB 목표를 충족했다. 응답 직후 관측값이 아닌 연속 측정값으로 판정했다. 공통 창 내 요청 간 최대 간격은 0.016632초다.

NPU 오디오는 449,980회 실제 추론, 새 missing frame·dropped packet·비정상 PCM 모두 0, RSS 66,516 KiB 고정이었다. 물리 MOTU 마이크 입력과 무음 재생 참조를 사용해 장시간 처리·안정성을 검증했다. 소비한 PCM은 유한값·프레임 수만 확인하고 버렸으며, 실제 방에서의 반향 제거 음향 품질 평가는 별도다. 측정 후 vLLM의 실제 종료를 확인하고 62.14초에 재시작·워밍업·실제 응답까지 확인했다.

전체 시계열은 [GPU samples](../../build/linux/vllm-qa/joint-v2/gpu-samples.jsonl), [요청 기록](../../build/linux/vllm-qa/joint-v2/requests.jsonl), [오디오 결과](../../build/linux/audio-qa/soak-joint-v2/result.json)에 보존했다.

## 실제 HDR와 화면 꺼짐 요청

현재 연결된 모니터 4대에서 각각 HDR 켜기와 원래 상태 복원을 확인했다. 격리된 실제 실행 파일로 자동 HDR 시작, 중복 실행 중 일부 종료, 사용자 수동 변경 우선, 마지막 종료, 새 실행, durable journal을 읽는 컨트롤러 재시작 후 복원을 검증했다. 최종 HDR 상태뿐 아니라 해상도·주사율·주 모니터·위치·배율·회전도 모두 보존됐다. GNOME 화면 꺼짐 방지와 logind 절전 방지는 실제 생성·조회·해제하고 서로 다른 목록에 표시됨을 확인했다.

HDR 테스트의 설정과 규칙은 격리된 XDG 디렉터리에 기록했다. 실제 사용자의 HDR 규칙을 덮어쓰지 않았다. HDR 변경 중에는 독립 vLLM 서버만 잠시 중지하고 이전 실행 상태로 돌리는 절차를 사용했다. 주 서비스와 실제 오디오 경로는 이 테스트에서 활성화하지 않았다.

## 보존한 실패와 수정

첫 공동 창은 약 20분 후 GPU 여유 3 GiB 안전 하한에 도달해 런타임이 중지했다. 이 창을 30분 공동 통과로 사용하지 않는다. CPU 가중치 오프로드를 4에서 8 GiB로 늘리고 KV 캐시를 고정 모델 요구에 맞게 산정한 뒤 전체 프로필과 새 30분 창을 검증했다. [이전 실패](../../build/linux/vllm-qa/joint-final/result.json)와 연속 GPU 관측은 그대로 보존했다. 첫 [오디오 단독 30분](../../build/linux/audio-qa/soak-persistent/result.json)도 새 공동 창에 합산하지 않았다.

HDR harness는 격리 디렉터리 누락, Ubuntu multicall coreutils의 basename dispatch라는 테스트 준비 문제를 차례로 수정했다. 각 실패 뒤 저장된 HDR 상태 복구가 확인됐으며 앱 서비스 코드는 변경하지 않았다. 두 원본 실패는 [디렉터리 준비 실패](../../build/linux/desktop-qa/hdr-result-before-harness-fix.json), [multicall 실행 파일 실패](../../build/linux/desktop-qa/hdr-result-before-multicall-fix.json)에 보존했다. 최종 HDR 23개 검사는 수정한 fixture로 새로 수행했다.

설치 apphost 검사에서 발견한 종료 응답 경쟁은 성공 응답 후 동일 D-Bus 연결의 name-release 왕복을 기다린 다음 종료하도록 수정했다. Ubuntu3 실제 설치 파일로 설정 저장·종료·재기동·다시 종료까지 검증했다.

## 2026-10-03 오디오 엔진 연결 실패 회귀

사용자 화면의 `Cannot assign requested address`를 번들 .NET 10의 실제 Unix socket 연결로 재현했다. 존재하지 않는 `audio.sock`에 `Socket.ConnectAsync`를 호출하면 `SocketError.AddressNotAvailable`과 같은 영문 문구가 반환된다. 이는 IP 주소 설정 실패를 뜻하는 화면이 아니었다. `Type=simple` 오디오 유닛이 활성화된 뒤 소켓을 바인딩하기 전의 시작 구간과, 엔진이 정상 종료된 뒤 소켓이 사라진 구간에 발생할 수 있었다.

기존 `Restart=on-failure`에서는 UI 서비스가 여전히 실행 중일 때 엔진이 정상 종료하면 오디오만 inactive로 남았다. 이미 active인 오디오를 본 새 UI 서비스의 Wants 의존성이 종료 후 다시 시작해 주지 않는 상황도 실제 systemd의 독립적인 임시 sleep 유닛으로 재현했다. 오디오 유닛을 `Restart=always`, `StopWhenUnneeded=yes`로 바꿨다. 소유 서비스가 실행되는 동안 엔진을 복구하고, 명시적 종료 또는 소유 서비스의 자연 종료에서는 오디오도 정지한다.

서비스의 오디오 연결은 **요청 전 연결에만** 2초 동안 재시도한다. 이미 전송한 설정·필터 명령은 재전송하지 않는다. 연결이 불가능한 동안 저장된 좌우 EQ 곡선과 파일 이름은 유지하지만, 실제 처리 중이라고 표시하지 않는다. 연결 오류는 오디오 엔진의 연결 문제라는 한국어 메시지로 전달하고, 엔진 복구 후에는 실제 상태를 다시 사용한다.

- [Unix socket 서비스 회귀](../../Tests/Linux/Service/AudioConnectionChecks.cs): 실제 AF_UNIX의 없는 경로, 지연 시작, 연결 거부, 취소, 요청 전송 후 단절, 저장된 필터 유지, 복구 후 한 번만 복원. 전체 서비스 검사 **55/55 통과** ([로그](../../build/linux/audio-connection-tests.log)).
- [systemd 수명 회귀](../../Tests/Linux/Packaging/audio-service-lifecycle.py): 기존 정책에서 고아 상태 재현, 정상 종료 후 복구, 명시적 종료, 서비스 재시작, 빠른 재활성화, 자연 종료 후 정리 등 **8/8 통과** ([보고서](../../build/linux/audio-service-lifecycle.json)). 독립적인 이름의 임시 유닛과 sleep 프로세스만 실행했으며 실제 앱·오디오·vLLM 서비스와 로그인 target의 PID·실행 식별자가 전후 일치했다.

검사 전 실제 엔진과 D-Bus의 읽기 전용 상태에서는 저장된 좌우 필터, 실제 NPU 추론 횟수 증가, EQ 활성 상태를 확인했다. 사용자 캡처의 실패를 이 정상 관측값으로 대체하지 않고, 연결 실패와 수명 경쟁을 별도로 재현해 수정했다. 이 회귀 검사는 실제 입력·출력 장치, EQ·에코 제거 토글 또는 현재 사용자의 오디오 경로를 변경하지 않았다.
