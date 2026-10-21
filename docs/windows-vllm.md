# vLLM 시작 메뉴와 위젯

Personal Tools의 **vLLM** 메뉴에서 **vLLM 시작**을 누릅니다. 이미 실행 중이면
모델 ID와 실제 컨텍스트 크기를 표시하고 중복 시작하지 않습니다. API 주소 복사로
OpenAI 호환 클라이언트의 주소를 가져올 수 있습니다.

실행 중인 서버는 메뉴와 위젯의 **중지**로 내릴 수 있습니다. `중지 중 → 중지됨`을
표시하며 서비스의 inactive / MainPID=0 및 API 연결 종료를 확인해야 완료 처리합니다.
시작·중지 작업 중에는 중복 명령을 차단합니다. 시작 중 취소는 제공하지 않습니다.

Windows 위젯 보드의 **Personal Tools vLLM**은 HDR·화면 꺼짐 방지와 별개의 위젯입니다.
기존 `PersonalTools.HdrWidget` 패키지 1.0.4.0부터 함께 제공하므로
`Scripts/build-hdr-widget.ps1` 및 `Scripts/install-hdr-widget.ps1`로 업데이트한 뒤
Win+W → 위젯 추가에서 선택합니다. 작은 크기부터 큰 크기까지 지원합니다.

## 실행 계약

- WSL 배포판 `Ubuntu-26.04`, 사용자 `sunggu`
- 프로젝트 `/home/sunggu/Projects/Personal/llm`
- 실행은 기존 `./start.sh`로 위임합니다. `serve.sh`, 모델, `.venv`, 최적화 설정은 수정하지 않습니다.
- 기존 스크립트가 `qwen38.service`를 시작하고 readiness 및 `warmup_chat.py`를 완료해야 시작 완료입니다.
- 모델 `qwen3.8-27b`, API `http://127.0.0.1:8000/v1`
- 참조 채팅의 선택 프로필: 64K / MTP2 / BF16 SSM / native NVFP4 / KV4. 화면의 컨텍스트 값은 API 응답을 사용합니다.
- 시작 또는 중지 명령마다 WSL 프로세스를 한 번 실행합니다. Linux `flock`으로 앱 재실행 사이의 시작 스크립트도 직렬화합니다.
- 중지는 기존 `./stop.sh`에 위임하며 해당 qwen38.service만 내립니다. 다른 WSL 서비스는 건드리지 않습니다.
- 조회는 Windows HTTP 요청이며 5초 캐시를 공유합니다. 메뉴가 숨겨지고 위젯이 비활성 상태이면 주기적인 vLLM 조회는 하지 않습니다.
- 시작 명령은 사용자·세션별 named pipe로 즉시 접수하고 백그라운드에서 준비합니다. 앱·위젯의 중복 클릭은 한 번만 실행합니다.
- API가 먼저 열려도 시작 스크립트의 워밍업 완료 전에는 `시작 중` 상태입니다.
- 다른 모델이 포트를 사용하면 시작을 차단합니다. 연결 실패만으로 서버가 종료되었다고 단정하지 않습니다.
- 앱 종료는 systemd 모델 서비스를 중지하지 않습니다. 시작 확인이 중단되었다면 다시 열어 연결 상태를 확인합니다.
- 시작 실패 로그: `%LOCALAPPDATA%\PersonalTools\Vllm\startup.log`. 서버 로그는 WSL 프로젝트의 `server.log`입니다.

## 검증

```powershell
dotnet run --project Tests/Vllm/VllmTests.csproj -c Release -r win-x64 --self-contained
./Tests/Vllm/run-progress-tests.ps1
# 설치된 앱과 모델 서버가 실행 중일 때: 모델 및 65,536 컨텍스트, 중복 시작 방지 확인
dotnet run --project Tests/Vllm/VllmTests.csproj -c Release -r win-x64 --self-contained -- --live-app
# 실제 서버를 중지하고 재시작/워밍업까지 검증 (진행 중 요청은 끊깁니다)
dotnet run --project Tests/Vllm/VllmTests.csproj -c Release -r win-x64 --self-contained -- --live-cycle
```

자동 테스트는 동시 시작, 워밍업 대기, 실패 보존, 포트의 다른 모델 감지,
조회 캐시, 실제 named pipe 통신 및 위젯 버튼 상태를 검증합니다.

2026-09-28 검증: vLLM 검사 13개와 기존 HDR 검사 23개 통과, WinUI publish 및
위젯 MSIX 검증/등록 완료. 설치본의 DLL 해시 일치를 확인했습니다. 실제 앱 메뉴와
named pipe에서 `qwen3.8-27b`, 컨텍스트 65,536 및 이미 실행 중인 서버의 중복 시작 방지를
확인했습니다. 서비스 PID는 4122225로 유지됐습니다. 서버가 꺼진 상태에서의 최초 기동과
Windows 위젯 보드 내 실제 버튼 클릭·렌더링은 이번 검증에 포함하지 않았습니다.

## 단계별 진행 표시

1. WSL 연결
2. 서비스 시작
3. 모델 가중치 로딩 — 로그의 실제 파일 수와 백분율
4. 추론 엔진 준비 — 커널 컴파일, KV 캐시, CUDA 그래프 캡처
5. API 준비 확인
6. 대화 워밍업 — 1/2 및 2/2
7. Windows에서 최종 연결 확인

메뉴에는 단계별 완료·진행·대기와 경과 시간이 표시됩니다. 위젯은 현재 단계와 세부 진행,
경과 시간을 보여 주며 큰 위젯에는 전체 단계 목록도 표시합니다. 실제 백분율이 없는 단계는
진행 중 표시를 사용하고 전체 완료율이나 예상 시간을 추정하지 않습니다.

`VllmStart.py`를 WSL의 기존 `.venv/bin/python` 표준입력으로 전달합니다. Linux에서
기존 `start.sh`를 실행하며 시작 이후에 추가된 `server.log`와 스크립트 출력을 관찰합니다.
로그 파일의 회전·축소 및 CR 기반 진행 출력을 처리하고, 오래된 완료 로그는 재사용하지 않습니다.
늦게 도착한 서버 로그가 워밍업 단계를 되돌리지 않으며 최종 API 확인 후에만 완료 표시합니다.
이미 실행 중인 서버를 발견한 경우 관찰하지 않은 시작 과정을 완료 목록으로 꾸며 표시하지 않습니다.

진행 파싱을 포함한 C# 검사 25개와 Linux 로그 관찰기 검사 2개 통과.
실제 기존 서비스에서 `start.sh`를 실행해 readiness와 워밍업 1/2·2/2 출력이 실시간 전달되는
것을 확인했습니다. 이 검증은 서비스 재시작 없이 수행했으며 냉간 모델 로딩 실측은 아닙니다.

## 2026-09-28 재배포와 시각화 보완

- 위젯 묶음 이름을 `Personal Tools`로 정리하고 1.0.6.0을 등록했습니다. 선택기에서 해당 묶음을 고른 후 좌우 화살표로 HDR / 화면 꺼짐 방지 / vLLM을 선택합니다.
- 설치 스크립트는 기본적으로 기존 제공자와 Widgets/WidgetBoard/WidgetService를 중지한 다음 등록합니다. 위젯 설정이나 고정 목록을 삭제하지 않습니다. 필요 시 `-RefreshWidgetHost:$false`로 생략할 수 있습니다.
- 빌드 시 vLLM 정의 및 모든 아이콘/미리보기 파일 존재를 검사합니다.
- 등록 후 `dotnet run --project Tests/WidgetCatalog/WidgetCatalogTests.csproj -c Release`로 Windows AppExtensionCatalog가 읽는 실제 세 정의와 설치 자산을 확인합니다. 이 검사는 선택기 UI 확인과 별개입니다.
- 본 메뉴는 상태 카드와 7단계 연결선으로 구성합니다. 좁은 창에서는 세로 단계로 전환합니다. 이미 실행 중이지만 시작 이력이 없는 경우 이전 단계는 기록 없음으로, 실제 확인한 최종 API 연결만 연결 확인으로 표시합니다.
- 위젯도 7단계 표시와 현재 단계 세부 정보를 사용하며 큰 크기에는 단계별 상태 목록을 제공합니다.
- 설치된 앱의 새 단계 화면과 API 연결, Windows 확장 목록의 세 정의를 확인했습니다. 위젯 선택기 표시 여부는 사용자 확인 대기 중입니다.

## 2026-09-28 중지 기능 검증

- 앱과 위젯에 중지 명령을 추가하고 위젯 패키지 1.0.7.0을 설치했습니다.
- C# 검사 34개 통과: 중복 중지, 시작과 중지의 충돌 방지, 종료 상태 유지, 실패 표시, 위젯 버튼 상태를 포함합니다.
- 설치된 앱의 실제 IPC를 통해 서비스와 API 중지를 확인하고 재시작했습니다. 가중치 파일 0~100%, 엔진 준비, 워밍업 1/2 및 2/2, 최종 Windows API 연결까지 통과했습니다. 서버는 실행 상태로 복원했습니다.
- Windows AppExtensionCatalog에서 1.0.7.0의 세 위젯 정의와 설치 자산을 확인했습니다. 위젯 보드 내 실제 버튼 클릭은 검증하지 않았습니다.

## 현재 시작 설정 (2026-09-28)

앱과 위젯은 동일한 start.sh → qwen38.service → serve.sh를 사용합니다. 선택 프로필은 64k-mtp2-fp8draft-b512-reserve3g입니다. 컨텍스트 65,536, MTP 2토큰, draft FP8, target native NVFP4, INT4 KV 1.375 GiB, batch 512, CPU offload 0, Torch 한도 20.5 GiB입니다. vLLM 외 GPU 여유 3 GiB는 검증된 목표이며 모든 입력에 대한 고정 보장은 아닙니다. 시작 옵션은 WSL에서만 관리하고 앱과 위젯은 시작 설정을 표시합니다.

이번 반영: 앱/위젯의 시작 설정 안내를 동일한 상수로 추가하고 설치본 DLL/PRI를 백업 후 교체했습니다. vLLM 검사 34개, WinUI/위젯 publish, 설치 파일 해시, Windows 위젯 확장 목록 검사가 통과했습니다. 설치된 앱의 IPC start로 새 서버 프로세스에서 위 옵션들을 확인했습니다. 위젯 보드의 실제 버튼 클릭은 이번 검증에 포함하지 않았습니다.

설치본의 실제 시작 검증은 1분 32초에 워밍업과 Windows 최종 API 확인까지 완료됐고 ready / context 65536 / Error null로 종료됐습니다. 서버는 실행 상태입니다.
