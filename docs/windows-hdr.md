# 게임 자동 HDR과 Windows 위젯

Windows 앱의 **자동 HDR** 메뉴에서 실제 게임 EXE를 등록합니다. 맨 위 **자동 HDR 사용**을 끄면
실행 중인 세션을 바로 되돌리고 새로 실행해도 바꾸지 않습니다. 프로그램마다 켜기/끄기와
⋯ 메뉴(편집·삭제)가 있고, **적용할 모니터** 카드에서 주 모니터 HDR을 직접 바꿀 수 있습니다.
프로세스가 시작되면 당시 주 모니터의 HDR을 켭니다. 게임 창 이동, 주 모니터 전환,
게임 자체의 HDR 렌더링 옵션 변경은 하지 않습니다. 실행 이벤트 감지 뒤 적용하므로
HDR을 시작할 때만 읽는 게임은 HDR을 먼저 수동으로 켜고 다시 실행해야 할 수 있습니다.
화면 시안: https://claude.ai/artifact/6HEp13fuWcEHtSj3sYAcNK

**프로그램 추가**는 창이 열린 실행 중 프로그램을 검색해 고르고, 목록에 없으면 **파일 찾아보기**로
실행 파일을 고릅니다. 이미 등록한 프로그램은 고를 수 없고, Steam·Epic·Battle.net 같은 런처에는
**런처** 표시가 붙습니다. 상시 실행되는 런처가 아닌 실제 게임 EXE를 등록하세요. 표시 이름은
실행 파일의 설명에서 가져오며 바꿀 수 있습니다. 전체 경로를 대소문자 구분 없이 비교합니다. 접근이 제한된
프로세스는 선택 목록에 없거나 자동 감지되지 않을 수 있습니다. 이름만으로 추측하지 않습니다.
같은 모델 모니터가 여러 대면 이름 뒤에 Windows 디스플레이 번호를 붙여 구분합니다.
실행 중인 프로세스 목록을 1초마다 확인하며 Personal Tools가 트레이에 상주해야 합니다.
WMI 이벤트 구독은 쓰지 않습니다. 프로세스 추적 이벤트는 관리자 권한이 필요하고, 일반 사용자용
`__InstanceCreationEvent WITHIN 1` 감시는 매초 전체 `Win32_Process`를 다시 읽어 이 PC에서
WmiPrvSE CPU를 코어 1개 기준 약 9%에서 49%로 올렸기 때문입니다(2026-10-21 측정).

같은 모니터에서 여러 등록 게임이 실행되면 마지막 게임 종료 후 첫 실행 직전 상태로
돌아갑니다. 게임 중 앱 또는 위젯에서 수동 변경하면 그 모니터는 해당 세션 동안 자동
재적용 및 복원 대상에서 제외됩니다. 종료 후 다음 게임 실행부터 새 세션이 시작됩니다.
Windows 설정에서 바꾼 상태도 관측 가능한 경우 유지하지만, 두 번의 상태 확인 사이에
변경 후 되돌린 경우는 감지할 수 없습니다.

정상 앱 종료 시 복원하며, 강제 종료나 전원 중단 시 즉시 복원할 수는 없습니다.
다음 앱 시작 시 복구 기록과 실행 프로세스를 대조합니다. 연결 해제된 모니터의 복원은
같은 장치 경로로 재연결될 때까지 보류하며 다른 모니터로 대체하지 않습니다.
연결 포트가 바뀌어 Windows 장치 경로가 달라지면 Windows 설정에서 직접 확인하세요.
설정(`settings.json`, `programs.json`)과 복구 기록(`sessions.json`)은 `%LOCALAPPDATA%\PersonalTools\Hdr`에 저장합니다.
읽을 수 없는 복구 기록은 덮어쓰지 않고 자동화를 중단해 오류를 표시합니다.

HDR 전용 DisplayConfig API를 우선 사용합니다. 구형 API만 있는 Windows에서는
모니터 EDID의 PQ HDR 지원 정보까지 확인하며 확인할 수 없는 화면은 수동 변경을
비활성화합니다. 이는 SDR의 자동 색상 관리를 HDR로 잘못 켜는 것을 방지합니다.

## 작업표시줄 HDR 버튼

모든 모니터의 작업표시줄 오른쪽(주 모니터는 트레이 아이콘 왼쪽, 나머지는 시계 왼쪽)에
그 모니터의 HDR만 켜고 끄는 **HDR** 배지가 붙습니다. 꺼짐은 테두리, 켜짐은 강조색으로 채우고,
전환 중에는 흐려지며 아래 막대가 움직입니다. 자동 HDR이 게임 때문에 켠 주 모니터에는 흰 점,
Windows가 HDR을 막고 있으면(배터리 등) 노란 점이 붙습니다. 마우스를 올리면 모니터 이름과 상태가,
오른쪽 클릭하면 HDR 켜기/끄기, Windows HDR 설정, 자동 HDR 열기, 작업표시줄 버튼 설정 메뉴가 나옵니다.
버튼으로 바꾸는 것은 앱의 수동 변경과 같아서 게임 중에 누르면 그 게임이 끝날 때까지 직접 고른 설정을 유지합니다.
**자동 HDR → 작업표시줄 버튼**에서 전체 표시와 모니터별 표시를 고릅니다(`settings.json`의
`TaskbarButtons`, `TaskbarHidden`). HDR을 지원하지 않는 모니터와 작업표시줄이 없는 모니터에는 나타나지 않습니다.
화면 시안: https://claude.ai/artifact/M8LZqXcTbkkArBSbxrbSPG (A안)

Windows 11에는 작업표시줄 버튼 API가 없어서 각 버튼은 Explorer의 `Shell_TrayWnd`/`Shell_SecondaryTrayWnd`
안에 넣은 레이어드 자식 창입니다. 작업표시줄과 함께 움직이고 숨으므로 전체 화면 게임 중에는 보이지 않습니다.
자식 창은 Explorer와 입력 큐를 공유하므로 전용 스레드에서 돌리고 HDR 변경과 UI Automation은 스레드 풀에서 합니다.
보조 작업표시줄의 시계는 XAML이라 창이 없어서 UI Automation(`AutomationId` `SystemTrayIcon`)으로 왼쪽 끝을 읽고,
읽기 전에는 오른쪽에서 79 DIP로 가정합니다. 트레이 크기 변화는 Explorer의 창 위치 이벤트로 바로 따라가고,
시계 폭은 1분마다 다시 읽습니다. Explorer가 다시 시작되면(`TaskbarCreated`) 버튼을 다시 붙입니다.
메뉴를 작업표시줄 테마에 맞추려고 uxtheme의 비공개 함수(`SetPreferredAppMode`, `FlushMenuThemes`)를 씁니다.
오류는 `%LOCALAPPDATA%\PersonalTools\Hdr\taskbar.log`에 남습니다.

```powershell
dotnet build Tests/TaskbarHdr/TaskbarHdrTests.csproj -c Release -p:Platform=x64
Tests/TaskbarHdr/bin/x64/Release/net8.0-windows10.0.19041.0/TaskbarHdrTests.exe
Tests/TaskbarHdr/bin/x64/Release/net8.0-windows10.0.19041.0/TaskbarHdrTests.exe --show --out <폴더>
```

인수 없이 실행하면 그리기·배치·설정 검사와 이 PC 작업표시줄의 읽기 전용 확인을 합니다. `--show`는 가짜 HDR
서비스로 실제 작업표시줄에 약 10초간 버튼을 띄우고 단계별 PNG를 남기며 실제 모니터 HDR은 바꾸지 않습니다.
2026-10-25 검증: 검사 9개 통과, 모니터 4대(150%·200% 혼합) 모두에 버튼이 붙고 꺼짐·켜짐·자동·전환 중·제한
상태, 툴팁, 다크 메뉴, 양방향 클릭 전환을 캡처로 확인했습니다. 설치된 앱에 배포한 뒤 4개 버튼과 설정 카드를 확인했습니다.
실제 모니터 HDR을 버튼으로 바꾸는 확인은 사용자가 직접 눌러 보는 것으로 남겨 두었습니다.

## Windows 위젯 설치

날씨가 있는 Windows 11 위젯 보드용입니다. 트레이 팝업이나 바탕화면 창이 아닙니다.
기존 앱과 별도의 `PersonalTools.HdrWidget` MSIX를 사용합니다. Windows Web Experience
Pack과 위젯 보드가 사용 가능해야 합니다. 조직 정책으로 위젯이 꺼져 있으면 표시되지 않습니다.

### 개인 PC에서 서명 없이 사용

Windows 개발자 모드가 켜져 있으면 빌드 결과 폴더를 개발용으로 등록할 수 있습니다.
빌드가 출력한 `stage-...` 폴더 경로를 지정하세요.

```powershell
./Scripts/install-hdr-widget.ps1 -DevelopmentDirectory ./build/hdr-widget/stage-...
```

설치 스크립트가 파일을 `%LOCALAPPDATA%\Programs\PersonalTools\HdrWidget\dev-...`로 복사하고
등록하므로 이후 저장소의 build 폴더를 지워도 사용할 수 있습니다. 설치된 `dev-...` 폴더는
유지하세요. 인증서 생성이나 신뢰 저장소 변경은 필요하지 않습니다.

### 서명된 MSIX로 설치

```powershell
./Scripts/build-hdr-widget.ps1 -DotNet dotnet
```

빌드는 서명되지 않은 패키지를 `build/hdr-widget/PersonalTools.HdrWidget.msix`에 만듭니다.
일반 배포에서는 패키지 Publisher와 일치하는 신뢰된 코드 서명 인증서를 사용하세요.
이 저장소의 개발용 Publisher는 `CN=PersonalTools Local`입니다.
로컬 테스트 인증서가 필요하면 다음처럼 만들 수 있습니다. 개인 키는 내보내지 않습니다.

```powershell
$hdrCert = New-SelfSignedCertificate -Type Custom -Subject 'CN=PersonalTools Local' `
  -KeyUsage DigitalSignature -FriendlyName 'Personal Tools HDR development' `
  -CertStoreLocation Cert:\CurrentUser\My `
  -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
Export-Certificate -Cert $hdrCert -FilePath ./build/hdr-widget/PersonalTools.HdrWidget.cer
./Scripts/build-hdr-widget.ps1 -CertificateThumbprint $hdrCert.Thumbprint
```

로컬 인증서는 설치 PC가 명시적으로 신뢰해야 합니다. 본인이 만든 인증서인지 확인한 뒤,
관리자 PowerShell에서 공개 인증서를 `LocalMachine\TrustedPeople`에 가져옵니다.
빌드·설치 스크립트는 인증서 신뢰를 자동으로 변경하지 않습니다.

```powershell
Import-Certificate -FilePath ./build/hdr-widget/PersonalTools.HdrWidget.cer `
  -CertStoreLocation Cert:\LocalMachine\TrustedPeople
./Scripts/install-hdr-widget.ps1
```

업데이트한 Personal Tools를 한 번 실행한 다음 **Win+W → 위젯 추가 → Personal Tools HDR**을
선택하세요. 모니터 이름·주 모니터 여부·HDR 상태와 켜기/끄기 버튼이 표시됩니다.
모니터 이름·상태·즉시 적용되는 스위치형 토글을 한 행에 표시합니다.
작음은 2대, 보통은 4대, 큼은 8대까지 표시하며 초과할 때만 이전/다음 버튼이 나타납니다.
행이 위젯의 세로 공간을 나눠 사용하므로 큰 위젯에서도 하단이 비고 다른 모니터가 다음 페이지로 숨는 현상이 없습니다.
보드가 활성화된 동안 2초마다 갱신하고, 조작 후 실제 상태를 다시 읽습니다.
앱이 꺼져 있으면 마지막으로 실행한 경로에서 `--startup`으로 시작합니다.
실행 파일을 이동했다면 새 위치에서 앱을 다시 실행하세요.

통신은 사용자 SID와 로그인 세션별 named pipe를 사용합니다. 서버와 클라이언트 모두
`CurrentUserOnly`를 적용하며 크기 제한, 연결 시간 제한을 둡니다. 네트워크 포트는 열지 않습니다.
앱이 종료 중이거나 연결에 실패하면 위젯의 새로 고침으로 재시도할 수 있습니다.

```powershell
./Scripts/uninstall-hdr-widget.ps1
```

위젯 제거는 오디오 설정과 게임 목록을 지우지 않습니다. 필요 없는 개발 인증서는
발급 시 기록한 지문으로 확인해 개인 인증서 및 신뢰 저장소에서 별도로 제거하세요.

## 검증

```powershell
dotnet run --project Tests/Hdr/HdrTests.csproj -c Release
dotnet run --project Tests/Hdr/HdrTests.csproj -c Release -- --integration
dotnet run --project Tests/Hdr/HdrTests.csproj -c Release -- --probe
dotnet build Sources/Windows/WinUI/PersonalToolsWindows.csproj -c Release -p:Platform=x64
./Scripts/build-hdr-widget.ps1
```

`--probe`는 연결 모니터와 지원 상태를 조회만 합니다. 자동화 테스트는 가짜 디스플레이와
복구 저장소로 다중 게임, 실패, 복원 및 수동 우선권을 검증합니다.
`--integration`은 실제 자식 프로세스와 WMI, 사용자 전용 named pipe를 사용하지만 디스플레이와
설정 저장소는 테스트용이므로 실제 HDR이나 사용자 설정은 변경하지 않습니다.

2026-09-22 검증: 상태 머신·ABI·위젯 카드·메시지 크기 제한 18개 테스트와 프로세스/IPC 통합
검사가 통과했습니다. WinUI 빌드, MSIX 생성/검증, 개발자 모드 패키지 등록 및 COM 제공자
활성화도 통과했습니다. 연결된 실제 모니터 4대의 HDR 지원 상태와 주 모니터를 조회했습니다.
추가 설치 검증: 서명 없는 개발용 패키지를 영구 설치 폴더에 등록하고 기존 앱을 업데이트했습니다.
`dotnet run --project Tests/Hdr/HdrTests.csproj -c Release -- --live-app`으로 실제 모니터 HDR
전환·복원, 테스트 프로세스 실행 시 주 모니터 자동 적용과 종료 후 복원, 수동 설정 유지까지
통과했습니다. 이 옵션은 설치된 앱을 재시작하고 화면 HDR을 실제로 변경하므로 게임을 종료한
상태에서 실행하세요. 테스트 후 임시 목록을 제거하고 원래 모니터 상태를 복원합니다.
게임별 HDR 렌더링 및 위젯 보드에서 실제 버튼을 누르는 검증은 별도로 남아 있습니다.

2026-10-21 검증(자동 HDR 화면 복귀): 단위 테스트 23개, `--integration`(실제 프로세스 폴링 감지,
named pipe 수동 변경, 전체 스위치 끄기·차단·다시 켜기)과 설치된 앱 대상 `--live-app` 4개 항목이
통과했습니다(32RTX950 수동 전환·복원, 주 모니터 MO32U24 자동 켜기·종료 후 복원, 수동 설정 유지,
원래 상태 복원). 화면은 설치된 앱에서 빈 목록·샘플 목록·추가 대화상자를 캡처해 시안과 비교했습니다.
WMI 감시를 없앤 뒤 WmiPrvSE CPU는 교체 전과 같은 약 8%였습니다.

API 참고: [Widget providers](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/widget-providers),
[C# 제공자 구현](https://learn.microsoft.com/en-us/windows/apps/develop/widgets/implement-widget-provider-cs).

화면 꺼짐을 방지하는 프로세스·서비스·드라이버 조회는 [화면 꺼짐 방지 요청 조회](windows-display-power.md)를 참고하세요.
