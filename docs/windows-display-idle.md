# 화면 자동 끄기 (DDC/CI)

키보드·마우스·터치·펜·XInput 게임패드 입력이 1분 동안 없으면 MO32U24 두 대의 패널을 끄고,
다음 입력에서 다시 켭니다. Windows의 "화면 끄기"를 쓰지 않고 모니터에 DDC/CI 전원 명령
(MCCS VCP `D6`)을 보내므로 Windows는 모니터가 계속 연결되고 켜진 것으로 봅니다.
창 배치, 렌더링, HDR 상태, 실행 중인 프로그램은 바뀌지 않습니다. 영상 재생이나 게임이
Windows에 "화면 켜 둠"을 요청해도 입력이 없으면 끕니다(2026-10-23 사용자 결정).

- 코드: `Sources/Windows/WinUI/DisplayIdle.cs`(타이머·상태·로그), `DisplayDdc.cs`(DDC/CI),
  `IdleInput.cs`(`GetLastInputInfo` + XInput 폴링). 켜진 동안 250 ms, 꺼진 동안 100 ms 간격으로 확인합니다.
- 끄기 직전 `D6`을 읽어 켜져 있는(1) 모니터만 끄고, 그 모니터만 다시 켭니다. 버튼으로 직접 끈 모니터는 켜지지 않습니다.
- 켤 때는 `D6=1`을 보낸 뒤 1이 읽힐 때까지 최대 3초씩 기다리며 두 번 더 보냅니다. 끈 뒤 2초에 다시 읽어 켜져 있으면 한 번 더 끕니다.
- 앱 종료, Windows 종료·로그아웃(숨은 최상위 창의 `WM_ENDSESSION`) 때 끈 모니터를 켭니다.
  강제 종료로 꺼진 채 남으면 `dark.json`에 남은 목록을 다음 실행 때 켭니다.
- 파일: `%LOCALAPPDATA%\PersonalTools\DisplayIdle\` — `settings.json`(없으면 기본값, 5초마다 변경 확인),
  `dark.json`, `log.txt`(256 KB 넘으면 앞 절반 삭제).

```json
{"Enabled": true, "IdleSeconds": 60, "Monitors": ["MO32U24"]}
```

`Monitors`에는 모델 이름(같은 이름 전부) 또는 장치 경로를 넣습니다. `IdleSeconds`는 최소 10초입니다.

## 화면 (2026-10-23, 시안 https://claude.ai/artifact/7dtoP5r9f84XYigQQUiJQE)

메뉴 **화면 자동 끄기**(자동 HDR 아래). 상태 표시(켜짐 · N초 뒤 끔 / N대 꺼짐 · M분째 / 사용 안 함),
사용 스위치, 기다릴 시간(30초·1분·2분·5분·10분·30분, 파일에 다른 값이 있으면 목록에 추가), 모니터별 스위치,
**지금 끄기**(누른 뒤 1.5초 동안의 입력은 무시), 최근 동작 3개(앱 실행 중 기록만)를 보여 줍니다.
모니터 하나의 스위치를 바꾸면 선택이 장치 경로 목록으로 바뀌고, 지금 연결되지 않은 모니터의 항목은 그대로 둡니다.
DDC/CI 응답 여부는 페이지를 열 때와 끌 때만 확인하며, 고른 모니터가 응답하지 않으면 경고를 띄웁니다.
내장 디스플레이는 끌 수 없어 스위치가 비활성입니다. 같은 모델은 자동 HDR 페이지처럼 Windows 디스플레이 번호로 구분합니다.

## 측정 (2026-10-23, SUNGGULAPTOP)

| 모니터 | 연결 | DDC/CI `D6` 허용 값 | 결과 |
| --- | --- | --- | --- |
| MO32U24 (UID8448, 주 모니터) | HDMI | 01 04 05 | 끄기·켜기 정상 |
| MO32U24 (UID8452, 위) | DisplayPort | 01 04 05 | 끄기·켜기 정상 |
| 32RTX950 | DisplayPort | 01 02 04 | 지원, 기본 대상 아님 |
| 내장 NE18NZ2 | eDP | — | DDC/CI 없음 |

- `D6=5`: 패널이 꺼지고 읽으면 4가 나오며 DDC/CI 응답은 계속됩니다. `D6=4`도 꺼지지만 읽으면 1이 나와 확인이 안 되므로 5를 씁니다.
- 꺼진 동안과 켠 뒤 `EnumDisplayMonitors`·`QueryDisplayConfig` 결과와 모니터 좌표가 같았고, 사용자 확인으로도 창 이동·깜빡임·OSD 안내가 없었습니다.
- 켜는 데 1.5–1.8초(두 대 동시). 켜지는 동안 `D6` 읽기가 가끔 실패해 확인을 반복합니다.
- 다른 프로세스의 `PowerSetRequest(DisplayRequired)`와 `SetThreadExecutionState(ES_DISPLAY_REQUIRED)`는
  `CallNtPowerInformation(SystemExecutionState)`로 관리자 권한 없이 보이지만, 사용자 결정에 따라 쓰지 않습니다.
- XInput 빈 슬롯 확인은 호출당 약 16 µs입니다.

## 검증

```powershell
dotnet run --project Tests\DisplayIdle\DisplayIdleTests.csproj -c Release            # 가짜 모니터로 상태 전환 14개
dotnet run --project Tests\DisplayIdle\DisplayIdleTests.csproj -c Release -- --probe  # 대상 모니터 D6 읽기만
dotnet run --project Tests\DisplayIdle\DisplayIdleTests.csproj -c Release -- --live 5 # 실제로 5초 끄고 켬, 사용자에게 먼저 알림
```

OLED 패널은 꺼질 때 자체 픽셀 리프레시를 돌릴 수 있고, 그동안은 입력이 있어도 모니터가 늦게 켜질 수 있습니다.
DualSense처럼 XInput이 아닌 패드는 Steam Input 등으로 XInput을 흉내 낼 때만 입력으로 셉니다.
