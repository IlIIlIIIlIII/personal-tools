# AEC 상세 진단 녹음

사용자가 승인한 진단 세션에서만 `Scripts/set-aec-diagnostics.ps1 -Action Enable`로 켠다. 마이크 스트림을 다시 열면 새 NPU 인스턴스가 기록을 시작한다. `-Action Disable`은 약 1초 안에 기록을 중단하고 기존 자료를 보존한다. `-Action Status`로 저장 위치와 마지막 기록 시각을 확인한다. 활성화 표시는 기록 성공을 보장하지 않으며 파일 증가도 확인해야 한다.

기존 설치 위치는 `C:\ProgramData\SoundControl\aec-diagnostics`, 새 설치는 `C:\ProgramData\PersonalTools\aec-diagnostics`다. 마이크를 사용하는 동안 마이크 입력 1 원음, 실제 처리 출력 모노, 선택한 스피커의 WASAPI 참조 좌우를 48 kHz float32로 로컬 저장한다. 실제 스피커의 아날로그 출력 녹음이 아니라 제거기에 전달된 디지털 참조다. 프로그램이 마이크를 새로 열거나 외부로 전송하지 않는다. 활성화는 재부팅 후에도 유지되며 중단 명령 또는 용량 한도 도달 시까지 기록한다.

오디오 콜백, 참조 콜백, NPU 작업 각각의 고정 크기 큐에서 별도 저장 스레드로 전달한다. 콜백에서 파일 접근·잠금 대기·메모리 할당은 하지 않는다. 저장이 느리면 진단 자료만 누락하고 그 횟수를 남긴다. 오디오 처리 알고리즘과 버퍼 지연은 바꾸지 않는다.

전체 진단 폴더의 사용량을 확인하고 16 MiB 세그먼트를 미리 예약한다. 세그먼트 예약은 파일 잠금으로 여러 APO 인스턴스 사이에서 직렬화한다. **기록은 14,000,000,000 bytes 이내에서 중단**하며 기존 파일을 삭제하거나 덮어쓰지 않는다. SSD 여유가 약 2 GB 아래일 때도 중단한다. 단일 활성 스트림 기준 대략 시간당 3 GB이므로 수 시간 기록이 가능하며, 여러 인스턴스는 한도를 공유한다. 재생 앱이 마이크를 계속 열어두면 대화가 없는 시간도 기록된다.

`Scripts/read-aec-diagnostics.py <폴더>`는 최신 세션의 요약을 출력한다. `--session aec-PID-SESSION`으로 세션을 선택한다. `--wav-seconds 60`은 마지막 60초를 4채널 float WAV로 내보낸다(최대 120초). 채널은 원음, 처리 결과, 스피커 L, 스피커 R이다. 처리 결과는 4096프레임 지연을 보정한다. 마지막 4096프레임 처리 결과와 누락된 진단 패킷은 0으로 남으므로 무음으로 오해하면 안 된다. 원본 ACD에는 시각·인덱스가 남는다. WAV 포함 전체 15 GB 한도를 검사한다.

## ACD v1

파일은 `aec-PID-QPCSESSION-SEGMENT.acd`. 작은 엔디안 헤더 `<4I7Q` (72 bytes): magic=0x31444341, type, frames, flags, QPC, hostTime(100ns), sampleIndex, a,b,c,d. type 1/2 뒤에 frames × 2개 float32가 이어진다. 그 외 payload는 없다. magic=0부터는 예약된 빈 공간이다. 중단 중이거나 쓰기 중인 마지막 레코드는 미완성일 수 있다.

| type | 의미 | a,b,c,d / flags |
|---|---|---|
| 1 | 마이크 원음 / 출력 | 누적 결과 누락, mic 큐 손실, ref 큐 손실, NPU 상태 / silent=1, enabled=2 |
| 2 | 스피커 참조 L/R | flags: silent=1, NPU 참조 큐 거절=2 |
| 3 | NPU가 소비한 mic 패킷 | 현재 hostTime, frames, 참조 가용 여부, mic 큐 깊이(상위32)/ref 깊이(하위32) |
| 4 | 추론·출력 게시 완료 | 추론 시작 QPC, 종료 QPC, 참조 유효 여부, 마지막 게시 인덱스+1 |
| 5 | 모델 초기화 | 기대 mic index, timestamp drift(100ns), 새 큐 손실 수, 기대 시각 / index jump=1, loss=2, drift>1ms=4 |
| 6 | 세그먼트 시작 | QPC frequency, UTC FILETIME, latency=4096, rate=48000 |
| 7 | 진단 자체 손실 | mic, ref, worker 큐별 누락 레코드 수 |
| 8 | 출력 결과 누락 | 누락 frames, 최초 target index, 관측 result stamp, 마지막 게시 인덱스+1 |
| 9 | NPU 생명주기 | 상태 1=준비, 2=실행, UINT64_MAX=예외 |
| 11 | 모델 에너지 | aligned mic, output, reference 에너지의 float32 비트 / flags=상태 완화 적용 여부 |
| 12 | NPU 예외 메시지 | frames × 8 bytes payload에 NUL로 끝나는 오류 문자열. 오디오가 아님 |

type 8의 target을 type 4의 게시 시각 및 type 3의 참조 가용 상태, type 5의 초기화와 비교해 지연/참조 누락/초기화를 구분한다. type 11과 동시 녹음은 처리 결과가 있어도 반향이 새는 구간을 분석하기 위한 자료다. 총 에너지 비율 자체는 사용자 발화가 섞이면 반향 제거율이 아니다.

2026-09-28 추가 이벤트: type 13은 루프백 수집/게시 시각(hostTime=원래 스피커 시각, index=device position, a=frames, b=WASAPI flags, c=wake QPC, d=publish QPC), type 14는 a=MMCSS 등록 성공 여부, type 15는 a=wait 시작 QPC, b=종료 QPC, c=대기 결과다. UI 프로세스의 별도 세션으로 저장되며 동일한 저장 한도를 공유한다.

2026-09-28 잔여 반향 보정판부터 type 11의 d에는 실제 recurrent-state 감쇠 계수의 float32 비트를 기록하며, flags는 실제 감쇠 적용 여부다. 0.98/0.995/1.0을 구분할 수 있다.
