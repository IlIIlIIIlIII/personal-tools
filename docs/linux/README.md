# Personal Tools for Ubuntu

Ubuntu 26.04 · GNOME 50 · Wayland용 네이티브 구현입니다. 화면은 GJS/GTK4,
빠른 클립보드와 전역 단축키는 GNOME 확장, 보관함과 설정은 .NET 10 사용자
서비스, 오디오는 PipeWire C++ 엔진이 담당합니다. 승인 시안과 검수 현황은
저장소 루트의 `design-qa.md`에 기록합니다. 미통과 항목이 있는 동안 완성된
릴리스로 간주하지 않습니다.

## 창 빠른 전환

`1`, `2`, `3`, `4`, `Q`, `W`, `E`, `R`의 8개 슬롯을 사용합니다.

| 단축키 | 동작 |
| --- | --- |
| `Super+Shift+슬롯 키` | 현재 포커스된 창 등록 · 기존 슬롯 덮어쓰기 |
| `Super+슬롯 키` | 등록한 창으로 포커스 전환 |

메인 앱 왼쪽의 **창 빠른 전환**에서 8개 슬롯의 단축키, 등록된 창 제목,
앱 이름과 등록 개수를 볼 수 있습니다. 창 등록·덮어쓰기·제목 변경·종료 시
목록이 자동 갱신됩니다. 빈 슬롯과 확장 연결 실패는 구분하며, 이전 확장이
실행 중이면 재로그인 안내를 표시합니다. `personal-tools --page windows`로
해당 화면을 직접 열 수도 있습니다.

같은 앱의 여러 창도 각각 등록할 수 있습니다. 창 제목이 바뀌어도 같은 창을
찾으며, 최소화된 창은 복원하고 다른 작업공간의 창은 해당 작업공간으로
이동해 활성화합니다. 등록 결과와 빈 슬롯 안내는 짧은 화면 알림으로 표시합니다.
창이 닫히면 슬롯을 비우며, 같은 앱이나 같은 제목의 새 창을 대신 열지 않습니다.
등록은 현재 GNOME 세션 동안 유지됩니다. 잠금 중에는 단축키를 해제하고
잠금 해제 후 살아 있는 창의 등록을 복원하며, 로그아웃하면 다시 등록해야 합니다.

GNOME Shell·창 관리자·Ubuntu Dock에서 위 조합과 겹치는 단축키만 잠시
해제합니다. 다른 번호나 Ctrl 조합, 동일 기능의 다른 단축키는 유지합니다.
확장을 끄면 기존 설정을 복원하고, 사용 중 사용자가 바꾼 값은 덮어쓰지 않습니다.
복원 기록은 GSettings에 보관하므로 비정상 종료 후 다음 활성화 때도 복구합니다.
그 밖의 확장과 충돌하면 창 전환 단축키 전체 등록을 취소하고 원래 설정으로
돌린 뒤 오류를 알립니다. 클립보드·마이크 단축키에는 이 16개 조합을 지정할 수 없습니다.

검증 명령은 다음과 같습니다. 사용자의 데스크톱과 별도인 GNOME/Wayland 세션에서
동일 앱의 실제 창 8개와 가상 키보드의 물리 키 이벤트를 사용합니다.

```sh
python3 Tests/Linux/Shell/run-isolated.py --script window-shortcuts.js --output build/linux/window-shortcuts-qa
```

## 빌드와 패키지

```sh
Scripts/Linux/build.sh
python3 Scripts/Linux/package.py
sudo apt install ./build/linux/packages/personal-tools_0.7.0~ubuntu9_amd64.deb
```

빌드는 .NET 10 SDK, GJS, GTK4/libadwaita, GNOME/Mutter 18, PipeWire 개발
헤더, CMake/Ninja, Meson, GObject Introspection이 필요합니다. `build.sh`는
C++ 검사, 사용자 서비스 검사를 실행하고 .NET 런타임을 포함해 패키지용으로
게시합니다. GTK/Shell 스타일은 동일한 디자인 토큰에서 생성됩니다.

Ubuntu 26.04에 없는 이전 LTTng 라이브러리를 요구하는 선택적 .NET 추적
공급자는 패키지에서 제외합니다. 서비스 실행과 EventPipe 진단은 유지됩니다.

설치만으로 모델 다운로드, 계정 로그인, 오디오 장치 변경은 수행하지 않습니다.
사용자별 선택적 준비 명령은 다음과 같습니다.

```sh
personal-tools-setup --npu
personal-tools-setup --vllm --download-model
personal-tools-setup --enable
personal-tools
```

`--npu`는 해시를 확인한 OpenVINO/NPU 사용자 공간 라이브러리와 DTLN-AEC
모델을 개인 데이터 폴더에 설치하고 실제 NPU 추론을 검사합니다. 시스템
GPU/NPU 드라이버나 Python을 덮어쓰지 않습니다. `--vllm`은 독립 Python 3.12
환경과 사용자 유닛을 등록합니다. `--download-model`은 약 18GiB의 고정된
Qwen INT4 체크포인트를 추가로 받습니다. 이미 설치된 런타임과 모델은 재사용합니다.

처음 설치한 GNOME 확장은 Wayland 세션 재로그인 후 사용할 수 있습니다.
아직 등록되지 않았다는 안내가 나오면 로그아웃·로그인한 후 `--enable`을 다시
실행하세요. 기존 세션을 강제로 종료하는 설치 동작은 없습니다.
`--enable`은 기존 자동 시작 링크도 현재 패키지 규격으로 다시 연결합니다.
자동 시작은 매 그래픽 로그인에 연결되며, 설정 변경만으로 실행 중인 서비스를
시작하거나 재시작하지 않습니다.
Antigravity는 `agy` CLI에서 로그인한 계정을 사용합니다. 실제 연결 상태와
모델 목록은 AI 서비스 화면에서 확인합니다.

## 실행과 종료

- `Ctrl+Shift+V`: 빠른 클립보드 열기/닫기. 카드 이동은 좌우 화살표,
  Enter는 호출 전 활성 앱에 붙여넣기입니다.
- `Super+Alt+M`: 모니터 소스를 제외한 활성 마이크 음소거 전환.
- 메인 보관함 Enter: 선택 항목 복사. Ctrl+F 검색, Enter 일반 검색,
  Shift+Enter AI 검색.
- 창 닫기와 백그라운드 실행은 설정을 따릅니다. 앱 메뉴의 명시적 종료는
  소유 중인 오디오 경로와 HDR을 복원합니다. 사용자가 수동 변경한 값은 유지합니다.
- vLLM은 앱과 별도 서비스입니다. 앱을 닫아도 유지되고, AI 서비스 화면에서
  서버 중지를 선택해야 종료합니다.

처음 사운드를 설정할 때 실제 출력과 입력을 선택하세요. EQ를 켜면 Personal
Tools 출력 경로를 기본 출력으로 사용합니다. 처리된 마이크를 기본 입력으로
사용하는 동작은 별도 버튼으로 요청합니다. NPU 실패를 CPU 실행 성공으로
표시하지 않으며, 장치/신호/지연은 진단에서 확인할 수 있습니다.

## 데이터와 업데이트·제거

- 설정: `$XDG_CONFIG_HOME/personal-tools` (기본 `~/.config/personal-tools`)
- 보관함·모델·NPU: `$XDG_DATA_HOME/personal-tools`
- 로그·진단·복원 기록: `$XDG_STATE_HOME/personal-tools`
- 오디오 소켓: `$XDG_RUNTIME_DIR/personal-tools/audio.sock`

업데이트 전에 `personal-tools-setup --disable`로 앱 서비스를 정상 종료한 후
새 `.deb`를 설치합니다. 설치 후 `--enable`을 실행하세요. vLLM은 독립적으로
유지되므로 그 런타임을 업데이트할 때만 먼저 서버를 중지합니다. 새 vLLM
설치 경로를 등록하려면 `personal-tools-setup --vllm`을 실행합니다.

제거는 다음 순서입니다. 기록, 필터, 사용자 설정과 모델은 삭제하지 않습니다.

```sh
personal-tools-setup --disable
systemctl --user stop personal-tools-vllm.service
sudo apt remove personal-tools
systemctl --user daemon-reload
```

사용자별 `personal-tools-vllm.service`는 모델을 보존하기 위해 자동 삭제하지
않습니다. 완전히 제거하려면 해당 사용자 유닛을 비활성화한 뒤
`~/.config/systemd/user/personal-tools-vllm.service`를 별도로 지우세요.
보관함과 필터는 다시 설치하면 그대로 사용할 수 있습니다.

## 재현 가능한 검증

- 실제 Wayland 다중 MIME/파일 보관 및 붙여넣기: `Tests/Linux/Shell/daemon-wayland.js`
- 실제 GTK 화면 캡처: `Tests/Linux/UI/capture.js`, 격리 GNOME 실행기 `Tests/Linux/Shell/run-isolated.py`
- 시안 나란히 비교: `Scripts/Linux/compare-design.py`
- GTK 입력·오래된 응답·설정 저장 회귀: `Tests/Linux/UI/test-model.js`
- NPU/오디오 장시간 검사: `Tests/Linux/Audio/`, `Scripts/linux-audio-soak.py`
- 실제 Antigravity/vLLM/HDR/inhibitor: `Tests/Linux/Service/`

시각 검사용 예시 데이터는 `--fixture`에서만 활성화됩니다. 실제 서비스는
빈 보관함으로 시작하며 예시 장치·로그·진행률을 사용하지 않습니다. 진단
내보내기는 API 키, 클립보드 원문과 음성을 제외합니다.

## 이 PC의 설치·검수 기록

2026-10-03 기준 `0.7.0~ubuntu3` 패키지를 설치했습니다. 업데이트 → 제거 →
재설치 동안 설정·보관함·필터와 모델 등 136,074개 경로를 비교했고 삭제·추가·변경은
없었습니다. 큰 모델 파일은 경로·크기·inode·소유권·권한·수정 시각을 비교했으며,
이 수치를 전체 모델의 새 바이트 해시 검사로 해석하지 않습니다.

- 설치 파일·폰트·네이티브 라이브러리·디자인 토큰 검사: **22/22 통과**.
- 패키지와 현재 구현 소스 차이: **0개**.
- 설치된 사용자 서비스의 설정 지속·정상 종료·재시작: **16/16 통과**.
- 설치 경로의 `personal-tools-setup --npu`도 실제 NPU 추론 **250회 통과**.
  결과는 `build/linux/installed-npu-setup.json`입니다.
- 17개 화면의 실제 GTK/Shell 캡처, 배율·라이트·글자 확대 및 차이:
  [디자인 검수](../../design-qa.md).
- 실제 Wayland 붙여넣기·단축키·필터·포커스:
  [GNOME 확장 검증](../ubuntu-shell-validation.md).
- 실제 NPU·오디오 라우팅과 지속 부하:
  [오디오 검증](audio-validation.md).
- 로그인된 Antigravity, vLLM 컨텍스트 단계, 네 모니터의 HDR 23개 검사:
  [서비스·AI 검증](service-validation.md).
- NPU·64K vLLM·실행 중인 GNOME 데스크톱의 공통 부하: **1,800초 통과**.
  실제 NPU 추론 449,980회, 손실·드롭·비정상 PCM 0, 모델 요청 31회,
  연속 측정 GPU 여유 최소 6,871MiB입니다. 별도의 데스크톱 애니메이션 부하나
  방별 반향 제거 음향 품질까지 검증한 결과는 아닙니다.

설치 증거는 `build/linux/package-lifecycle-final.json`,
`build/linux/packaging-qa.json`, `build/linux/installed-service-smoke.json`에
보존했습니다. 신규 확장의 사용자 세션 재로그인 검증이 남아 있으므로 이 설치
검사만으로 전체 구현의 최종 완료를 선언하지 않습니다.

설치 후 메인 앱과 사용자 서비스·오디오 엔진을 실행하고 자동 시작을 켰습니다.
vLLM 사용자 유닛은 저장소 작업 경로에서 설치 경로
`/usr/share/personal-tools/Vllm/runtime.py`로 전환했습니다. 64K·8GiB CPU 가중치
오프로드 설정을 유지한 실제 모델 응답과 로그인된 Antigravity의 실제 응답도
다시 확인했습니다. 관측 결과는 `build/linux/installed-session.json`,
`installed-vllm-test.json`, `installed-antigravity-test.json`에 있습니다.
오디오 장치·EQ·처리 마이크는 첫 사용 시 사용자가 선택하며 예시 설정을 적용하지
않습니다. 현재 보관함은 비어 있고 시스템 테마 설정을 유지합니다.

### 재로그인 자동 시작 수정 (`0.7.0~ubuntu4`)

첫 실제 재로그인에서 메인·오디오 서비스가 정상 종료된 뒤 자동으로 다시
시작되지 않는 문제가 확인됐습니다. 독립 vLLM이 유지되는 동안 사용자 서비스
관리자와 `default.target`도 계속 활성 상태여서 기존 자동 시작 요청이 재실행되지
않았습니다. 메인 서비스를 `graphical-session.target`에 연결하고, `--enable`이
이전 `default.target` 링크를 제거한 뒤 새 링크를 생성하도록 고쳤습니다.
로그아웃 시 오디오·HDR 정리와 독립 vLLM 유지 동작은 그대로 적용됩니다.

격리된 설치 링크 이관·반복 실행·시작 순서 검사 **8/8 통과**를
`build/linux/session-startup-qa.json`에 기록했습니다. 이 검사는 실제 로그인
세션을 중지하지 않고 현재 메인·오디오·vLLM의 상태와 PID가 유지됨을 확인합니다.
실제 사용자 세션의 재로그인 결과는 이 오프라인 검사와 별도로 기록합니다.

### 요청한 디자인·단축키·EQ 변경 (`0.7.0~ubuntu5`)

- 빠른 클립보드의 기본 단축키는 `Ctrl+Shift+V`입니다. 설정·빈 보관함·빠른 패널은 실제 저장된 조합을 표시합니다.
- 전체·고정 탭을 유지하고 하단 단축키 안내와 메인 창 열기 영역만 제거했습니다. 패널 기준 높이는 488px에서 400px로 줄였습니다. 메인 창 열기는 상단 아이콘으로 사용할 수 있습니다.
- 기존 Windows의 `Resources/EQ/L.txt`·`R.txt`를 패키지에도 보관합니다. 이번 사용자 환경에는 서비스의 가져오기 기능으로 좌우 두 필터씩 불러왔으며 EQ 활성 상태와 장치·오디오 경로는 유지했습니다. 사운드 화면은 파일명과 전체 경로 툴팁을 표시합니다.
- 단축키 변경은 Shell에서 충돌 확인·등록 후 서비스가 저장합니다. 실패 시 이전 조합을 복원합니다. Shell이 서비스 설정 명령을 다시 호출하던 교착을 제거했습니다.
- 이미 실행 중인 GNOME Shell의 JavaScript 모듈은 로그아웃·로그인 후 교체됩니다. 새 패널과 카드 Enter 수정은 새 사용자 세션에서 최종 확인합니다.

### 탭 모서리 수정 (`0.7.0~ubuntu6`)

전체·고정과 사용자 모음의 실제 첫·마지막 탭에 8px 외곽 모서리를 대칭 적용합니다. 가운데 경계는 직각으로 이어지며 선택 상태가 모서리 모양을 바꾸지 않습니다. 모음 추가 버튼은 탭 끝 계산에서 제외합니다.

### 빠른 메뉴 정보 확장 (`0.7.0~ubuntu7`)

상단 상태 버튼은 주변 Ubuntu compact indicator와 같은 좌우 여백을 사용합니다. HDR은 연결된 모니터 전부를 표시하고 긴 이름을 줄바꿈하며, 단자와 주 모니터 여부를 별도 줄에 표시합니다. 화면 꺼짐 방지 앱은 같은 앱의 요청을 묶어 최대 3개를 보여주고, 전체 개수와 상세 화면 이동을 제공합니다. 요청 개수가 같아도 앱·이유 변경이 반영되며 조회 실패와 요청 없음을 구분합니다. 메뉴가 작업 영역보다 크면 스크롤할 수 있습니다.

`display-power` 링크는 새 창과 기존 창 모두 화면 꺼짐 방지 탭을 엽니다. 닫힌 클립보드 패널의 표시 상태를 GNOME 전체 화면 추적이 덮어쓰던 문제도 수정했습니다. 이미 실행 중인 Shell에는 다음 로그인부터 적용됩니다.

### 오디오 재연결·키보드 복구 (`0.7.0~ubuntu8`)

- 오디오 엔진의 소켓이 생성되기 전에는 연결 단계만 짧게 재시도합니다. 이미 전달한 토글·가져오기 명령은 재실행하지 않습니다. 연결 실패 중에도 저장된 좌우 EQ 필터와 파일명은 유지하며 실제 엔진 상태와 구분합니다.
- 메인 서비스가 살아 있는 동안 오디오 프로세스가 종료 코드 0으로 끝나도 다시 시작합니다. 메인 서비스의 명시적 종료·로그아웃에서는 엔진을 계속 재생성하지 않고 함께 종료합니다.
- EQ 숫자를 편집하는 동안에도 엔진 연결 오류·NPU 상태·처리 지연·입력 레벨은 새 상태로 갱신합니다.
- 앱과 빠른 패널의 Ctrl+F를 입력 위젯보다 먼저 처리합니다. 입력창·카드·팝업에서 검색으로 이동하고, Esc로 입력 점유를 해제합니다. GNOME 개요 화면으로 전환할 때 빠른 패널을 닫고 표시 상태와 내부 열림 상태를 일치시킵니다.
- Shell `GetStatus`는 실제 로드된 `buildVersion`, `panelOpen`, `panelVisible`을 제공합니다. 패키지 설치 버전과 실행 중인 확장 버전을 별도로 확인합니다. 실행 중인 GTK 앱은 다시 열고, GNOME 확장은 새 로그인 세션에서 변경 코드를 사용합니다.

### 빠른 클립보드의 초기 키보드 동작 (`0.7.0~ubuntu9`)

패널을 열면 선택된 카드에 키보드 포커스를 줍니다. 추가 클릭이나 아래 화살표 없이 바로 좌우 화살표로 선택하고 Enter로 호출 전 앱에 붙여넣을 수 있습니다. 검색은 Ctrl+F 또는 검색창 클릭으로 시작하며, 검색창 안의 Enter·Shift+Enter는 일반·AI 검색 동작을 유지합니다.

조회 결과로 카드 위젯을 다시 만들 때는 카드 선택과 포커스를 복원합니다. 사용자가 이미 검색창·팝업·다른 조작부로 이동했다면 포커스를 가져오지 않습니다. 오류·빈 상태에서는 숨겨진 이전 기록을 Enter로 붙여넣지 않습니다.

### 터미널 블록 Enter 붙여넣기 수정 (2026-10-04)

터미널의 자동 붙여넣기 `Ctrl+Shift+V`가 클립보드 패널의 기본 전역
단축키와 충돌했습니다. 가상 키를 보내는 동안 확장의 전역 키 등록을
잠시 해제하고, 이벤트 처리 뒤 복원합니다. 설정 갱신·중첩 요청·확장
종료 중에도 등록 상태를 유지하며, 앱 식별자가 임시 `window:*`인 경우
터미널 WM class로도 판별합니다.

회귀 검증:

```sh
python3 Tests/Linux/Shell/run-isolated.py --script terminal-paste.js --output build/linux/terminal-paste-after
python3 Tests/Linux/Shell/run-isolated.py --script clipboard-browse.js --output build/linux/editor-paste-after
python3 Tests/Linux/Shell/run-isolated.py --script keyboard-regression.js --output build/linux/paste-keyboard-regression
```

터미널 테스트는 전용 Wayland 세션에서 카드 Enter와 연속 사용을 검증하고,
실제 Ptyxis PTY에 도착한 바이트를 선택한 블록과 비교합니다. 입력 수집기는
명령을 실행하지 않습니다. 테스트 세션에 systemd 사용자 관리자가 없으므로
해당 테스트 프로세스의 PATH에서만 scope 생성을 직접 실행으로 대체합니다.

현재 데스크톱에는 시스템 설치본을 기반으로 수정한 파일들을 담은 사용자
확장을 `~/.local/share/gnome-shell/extensions/personal-tools@griplabs.io`에
설치했습니다. GNOME 50은 ReloadExtension을 지원하지 않으므로 **다음 로그인에
적용**됩니다. 이 사용자 확장은 시스템 패키지보다 우선합니다. 수정이 포함된
시스템 패키지로 갱신한 뒤에는 사용자 확장을 제거하고 다시 로그인해야 시스템
패키지의 후속 업데이트를 사용할 수 있습니다. 배포 파일 체크섬과 위치는
`build/linux/terminal-paste-install.json`에 기록했습니다.

검색창에서 Enter(일반 검색) 또는 Shift+Enter(AI 검색)를 누르면 검색 완료 후
첫 결과 블록에 포커스를 둡니다. 바로 좌우 화살표로 선택하고 Enter로 붙여넣을
수 있습니다. 입력 중 자동 검색은 검색창 포커스를 유지하며, 결과 없음·오류·
취소된 응답은 블록으로 포커스를 옮기지 않습니다. 검색 도중 Ctrl+F로 다시
검색창을 선택하면 뒤늦은 결과가 포커스를 가져가지 않습니다.

### 빠른 클립보드 열기·카드 배치·전체 기록 (2026-10-13)

빠른 패널을 열 때마다 검색어·AI 결과·모음·필터와 가로 스크롤을 초기화하고
가장 최근 블록(첫 블록)을 선택합니다. 패널을 연 뒤의 새 복사·고정·삭제
갱신에서는 기존처럼 선택한 블록을 유지합니다.

긴 텍스트 미리보기는 카드 높이에 맞춰 마지막 줄을 말줄임하므로 출처·시간
줄이 카드 밖으로 밀려나지 않습니다. 제목·출처 줄에 최소 높이를 두어 한글·
기호(①, ✅)가 CJK 글꼴로 대체되어도 블록 내용이 아래로 내려가지 않습니다.

기록은 삭제되지 않았지만 패널이 서비스 기본값인 최근 60개만 조회했습니다.
이제 마지막 블록 근처로 이동하거나 스크롤하면 60개씩 이어서 불러와 모든 기록을
볼 수 있습니다. 같은 조회의 갱신은 이미 불러온 범위(최대 500개)를 유지합니다.

회귀 검증:

```sh
python3 Tests/Linux/Shell/run-isolated.py --script clipboard-popup.js --output build/linux/clipboard-popup
```

사용자 확장의 이전 파일은 `build/linux/shell-install-backup-20261013/`에
보관했습니다. 다음 로그인부터 적용됩니다.
