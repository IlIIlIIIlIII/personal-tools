# 조명 (Legion 9 18IAX10 키보드·라이트바·로고·전원 버튼)

이 노트북(Lenovo Legion 9 18IAX10, 83EY, BIOS RZCN50WW)의 조명을 **조명** 페이지에서 관리합니다.
키보드(키 101개), 앞쪽 U자 라이트바(10구간), 덮개 LEGION 로고는 노트북 펌웨어에 설정을 저장하므로
앱을 닫거나 Windows를 다시 시작해도 그대로입니다. 전원 버튼 표시등은 켜고 끄기만 되고 색은 전원 모드가 정합니다.
충전·카메라·Esc Fn 잠금 표시등은 노트북이 직접 정하므로 바꿀 수 없습니다. 후면·통풍구 조명은 이 모델에 없습니다.

- 코드: `Sources/Windows/WinUI/LightingSpectrum.cs`(HID 프로토콜과 장치), `Lighting.cs`(구역 모델, Windows 동적 조명 스위치,
  전원 버튼 도우미 클라이언트, 컨트롤러 스레드), `LightingPage.xaml(.cs)`.
- 스크립트: `Scripts/set-windows-power-led.ps1`(SYSTEM, WMI 호출), `Scripts/register-windows-power-led.ps1`(관리자, 작업 등록).
- 테스트: `Tests/Lighting`(가짜 펌웨어 25개, `--probe` 읽기만, `--live` 되돌리는 쓰기).
- 로그: `%LOCALAPPDATA%\PersonalTools\Lighting\log.txt`(쓰기와 오류, 128 KB 넘으면 앞 절반 삭제). 저장하는 설정 파일은 없습니다.

## 경로

| 조명 | 경로 | 권한 |
|---|---|---|
| 키보드·라이트바·로고 효과, 밝기 0–9, 프로필 1–6, 로고 켜기/끄기 | ITE `048D:C909` HID 컬렉션 `FF89:0007`, 960바이트 feature report 7 (Lenovo "Spectrum") | 일반 사용자 |
| Windows 동적 조명 끄기(이 노트북만) | `HKCU\Software\Microsoft\Lighting\Devices\HID#VID_048D&PID_C903&MI_01#…\AmbientLightingEnabled` = 0 | 일반 사용자 |
| 전원 버튼 표시등 켜기/끄기 | `LENOVO_OTHER_METHOD` Get/SetFeatureValue(`0x06040000`) | 관리자 → SYSTEM 작업 |

Lenovo WMI의 `LENOVO_LIGHTING_METHOD`, GameZone `SetKeyboardLight`/`SetLightControlOwner`는 이 BIOS에서 아무 일도 하지 않습니다
(DSDT의 WMA3는 Lighting_ID 5만 처리, WMA0에는 해당 분기 없음). `048D:C903`의 HID LampArray는 Windows 동적 조명이 쓰는 같은 LED의
다른 입구이며, 이 앱은 쓰지 않습니다(비패키지 앱은 창에 포커스가 있을 때만 반영되기 때문).

## Spectrum 프로토콜 (2026-10-30 이 PC에서 측정)

- 요청: `07 op C0 03 인자…`를 SetFeature로 보내고 GetFeature로 답을 읽습니다. 효과 쓰기(CB)만 바이트 2–3에 실제 길이(u16 LE, 프로필·`01 01`·효과 포함)를 씁니다.
- **답이 한 박자 늦습니다.** 요청 직후 GetFeature는 대개 직전 요청의 답을 돌려줍니다. 약 150 ms 뒤에는 LED 실제 색 보고(op `03`)로 돌아갑니다.
  그래서 클라이언트는 보고가 `03`으로 돌아온 뒤에 요청하고, 답의 op(그리고 CC는 바이트 4의 프로필)가 맞을 때까지 다시 읽으며, 300 ms 안에 안 오면 3번까지 다시 보냅니다.
- 쓰기(CE 밝기, A6 로고, C8 프로필, CB 효과, C9 공장 설정)는 답이 없습니다. CB 직후에는 다음 요청을 무시하므로 600 ms 기다립니다.
- 읽기: D1 호환(0), CD 밝기, CA 프로필, A5 로고, C4/C5 키 배열(9×22 + 로고 행), CC 프로필 효과, 요청 없는 GetFeature = 키마다 지금 켜진 색(밝기 반영).
- 키 코드의 상위 바이트가 구역입니다: `00xx` 키, `01F5–01FE` 라이트바, `05DD` 로고, `03E9–03FA` 통풍구(펌웨어 목록에만 있고 항상 검정).
- C9는 지정한 프로필만 공장값으로 되돌립니다(다른 프로필에 쓴 효과는 남음, 측정).
- 공장 프로필: 1 나선 무지개(키보드)+무지개 물결(라이트바·로고), 2 하늘색 #158DDD 고정, 3 물결 퍼짐+숨쉬기(무작위 색), 4 빗방울+부드럽게 바뀜,
  5 소리 반응, 6 소리 물결. 5·6은 Lenovo Legion Space가 소리를 보내 줄 때만 움직입니다. 백업: `build\led-2026-10-30\backup\profile-*.bin`(커밋 안 함).
- 효과별 설정(펌웨어 C6 목록): 속도 1–3은 고정·소리·화면 효과 말고 모두, 색(목록/무작위)은 고정·색 바뀜·숨쉬기·색 물결·부드럽게·빗방울·물결 퍼짐·타자 반응,
  방향은 무지개 물결·색 물결, 회전은 나선 무지개. 페이지는 방향과 회전을 고르지 않고 원래 값(없으면 공장값)을 유지합니다.

## Windows 동적 조명

Windows 설정 > 개인 설정 > 동적 조명이 이 노트북을 쓰는 동안(기본값, 강조색 단색)에는 펌웨어 효과가 보이지 않습니다.
설정의 "이 디바이스에서 동적 조명 사용"은 위 장치 키의 `AmbientLightingEnabled` 값이고 Windows가 이 값을 지켜봅니다.
일반 프로세스가 0을 쓰면 그 즉시 펌웨어 효과가 나타나고, 1을 쓰면 다시 강조색이 됩니다(측정). 페이지의 **이 노트북에서 동적 조명 끄기**는
이 값만 0으로 바꾸고, 전역 스위치와 다른 장치(로지텍 등)는 건드리지 않습니다. 다시 켜려면 Windows 설정에서 켭니다.

Claude 데스크톱 앱이 띄운 프로세스는 HKCU가 가상화되어 이 값을 써도 Windows에 닿지 않습니다. 시험은 WMI로 띄운 프로세스에서 해야 합니다
(`build\led-2026-10-30\real-reg-*.ps1`).

## 전원 버튼 표시등

`LENOVO_*` WMI 인스턴스는 관리자만 읽을 수 있습니다. 그래서 마이크 복구처럼 고정 입력만 받는 SYSTEM 작업을 씁니다.

- 앱이 Application 로그에 `PersonalTools` 원본으로 이벤트 7101(끄기), 7102(켜기), 7103(읽기)을 남기면
  작업 `PersonalTools Power LED Off/On/Read`가 `C:\Program Files\PersonalTools\set-windows-power-led.ps1 -State …`를 SYSTEM으로 실행합니다.
  읽기 작업은 Windows 시작 때도 실행됩니다.
- 결과는 `power-led-status.txt`(Program Files, 관리자만 쓰기)에 `value=N before=M state=S at=…` 또는 `error=… at=…`로 남고, 페이지가 이 파일을 읽습니다.
- 펌웨어는 0·1·2만 받고 다른 값은 0으로 저장합니다(DSDT). 끄기는 0을 쓰고, 켜기는 끄기 전 값(`power-led-on-value.txt`, 없으면 펌웨어 기본값 1)을 돌려놓습니다.
- 처음 한 번 `register-windows-power-led.ps1`을 관리자 권한으로 실행해야 합니다(페이지의 **설정** 버튼, 또는 설치·업데이트 스크립트가 자동 실행).
  Lenovo WMI가 없는 PC에서는 건너뜁니다.

## 화면 (2026-10-30, 시안 https://claude.ai/artifact/6EANCaKQMaEjWsb5MN7Gz7)

메뉴 **조명**(화면 자동 끄기 아래). 상태 표시(노트북 효과 · 프로필 N / Windows 동적 조명이 제어 중 / 꺼짐 · 밝기 0 / 조명 장치 없음),
동적 조명 경고와 끄기 버튼, 실시간 미리 보기(펌웨어의 키 배열과 실제 색, 초당 4번), 밝기, 프로필, 구역 3개(효과·색·속도, 로고는 켜기 스위치),
**프로필 N 공장 설정으로**(확인 창), 전원 버튼 스위치(도우미가 없으면 **설정**)를 보여 줍니다. 동적 조명이 제어하는 동안 펌웨어 조작은 비활성이고
구역에는 "저장됨: …"으로 저장된 효과만 보입니다. 구역을 바꾸면 지금 프로필을 키보드·라이트바·로고 효과 3개로 다시 씁니다.
Lenovo의 소리·화면 효과는 모든 조명을 한꺼번에 쓰므로 나눌 수 없어, 다른 구역을 바꾸면 그 구역은 흰색 고정이 됩니다.

장치는 페이지가 보일 때만 읽습니다(밝기·프로필·로고 1초마다, 색 250 ms마다). 창이 트레이로 숨으면 멈춥니다. Fn+Space(프로필)와 Fn+↑/↓(밝기)는
다음 읽기에서 반영됩니다.

## 검증

```powershell
dotnet run --project Tests\Lighting\LightingTests.csproj -c Release            # 가짜 펌웨어 25개
dotnet run --project Tests\Lighting\LightingTests.csproj -c Release -- --probe # 실제 장치 읽기만
dotnet run --project Tests\Lighting\LightingTests.csproj -c Release -- --live  # 밝기·로고·프로필·프로필 2 키보드를 바꿨다가 되돌림
```
