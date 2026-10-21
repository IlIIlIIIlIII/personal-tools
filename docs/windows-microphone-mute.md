# Windows 마이크 음소거

Personal Tools 실행 중 **Win + Alt + M**을 누르면 현재 연결된 모든 Windows 녹음 장치의 음소거를 전환합니다. 하나라도 음소거가 해제되어 있으면 전체를 음소거하고, 모두 음소거 상태이면 전체를 해제합니다. 시스템 출력 음량은 변경하지 않습니다.

Windows Core Audio의 `IAudioEndpointVolume.SetMute`를 사용합니다. 앱별 녹음 설정과 별개인 장치 음소거입니다. 장치 목록은 매번 다시 조회합니다. 음소거 후 새로 연결한 장치는 자동 음소거되지 않습니다.

Game Bar가 같은 단축키를 예약하므로 별도의 메시지 스레드에서 키보드 훅으로 이 조합을 처리합니다. Personal Tools 실행 중에는 Game Bar의 같은 단축키 대신 동작하며, 앱 종료 시 훅을 해제합니다. 키 반복은 무시합니다. 전환 결과 또는 오류는 트레이 알림과 앱 메시지에 표시합니다.

알림에는 Windows 기본 마이크 아이콘(`%windir%\System32\mmres.dll,-3014`)을 사용합니다. 마지막 시작/전환 결과만 `%LOCALAPPDATA%\PersonalTools\microphone-shortcut-status.txt`에 덮어써 저장합니다.

실제 장치 검증: `Tests/Microphone`을 `--installed`로 실행하면 설치된 앱에 키 입력을 전달하고 두 번의 전환 및 키 반복 억제를 확인합니다. 종료 시 원래 장치별 음소거 상태를 복원합니다. `--read`는 상태만 읽습니다. 인자 없이 실행하면 자체 훅을 만들어 검증하므로 설치된 앱과 동시에 실행하지 않습니다.
