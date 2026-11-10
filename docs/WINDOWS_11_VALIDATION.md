# AMARE Windows 11 실기기 검증 절차 (0.57.0)

목표: Windows CI(Windows Server 2025 Runner)에서 확인할 수 없는 항목을 Windows 11 실기기에서 확인한다.
실제 기기에서 확인하지 않은 항목은 PASS로 기록하지 않는다.

## 준비

1. `AMARE-0.57.0-win-x64.zip`을 한글·공백이 포함된 일반 폴더에 압축 해제한다.
2. `AMARE.Windows.exe`를 실행한다. 설치 없이 동작해야 한다.
3. `메뉴 → Diagnostics…`에서 AMARE 버전, Windows 버전, WebView2 Runtime, DPI, display 수, 창 크기, update state를 복사한다.
4. 문제 발생 시 `Diagnostics → Open logs`로 로그 파일 위치를 확인한다. 로그에는 API key, cookie, token, 비밀번호, 페이지 본문을 포함하지 않는다.

## DPI / 디스플레이

| ID | 절차 | 예상 결과 |
|---|---|---|
| DPI-100 | Windows 설정에서 배율 100% | 주소창/탭/dialog가 잘리지 않음 |
| DPI-125 | 배율 125%, 앱 재시작 | 동일 |
| DPI-150 | 배율 150%, 앱 재시작 | 동일 |
| DPI-200 | 배율 200%, 앱 재시작 | 동일 |
| MON-1 | 모니터 2개에서 창 이동 | 최대화/복원/탭 drag 정상 |
| MON-2 | 모니터별 다른 DPI | 창 이동 후 UI 깨짐 없음 |

## 키보드 / IME

- 주소창 클릭 → 한글 입력 → 자동완성 → Enter.
- Ctrl+T, Ctrl+W, Ctrl+Tab, Ctrl+F, Ctrl+L 확인.
- 한글 조합 중 Enter/Backspace로 페이지가 잘못 이동하지 않음.

## 탭 / 창

- 탭 드래그: first→last, last→first, middle 이동, same-position, 취소.
- 새 창을 열고 A→B, B→A 이동 후 양쪽 탭 수가 정확한지 확인.
- 창 밖으로 탭을 drag했을 때의 실제 동작을 기록한다. 0.57.0은 tear-off를 **UNVERIFIED/미구현**으로 기록한다.
- 오류 dialog가 열린 상태에서 닫기/뒤로/다시 시도/다른 탭 사용을 확인한다.

## 인증서

- 자체 서명/만료/hostname mismatch 테스트 서버는 CI probe 전용이다.
- 실기기에서는 실제 오류 사이트를 사용해 한국어 dialog, URL, 취소, 뒤로, 다시 시도를 확인한다.
- 인증서 경고를 자동으로 우회하지 않아야 한다.
- Local trusted CA는 Windows CI trust store 제한으로 **UNVERIFIED**다. 실기기에서 별도 격리 계정으로만 검증한다.

## 다운로드 / AI / 미디어

- 파일 다운로드 저장 대화상자와 저장 경로 확인.
- Reader Mode, Find in Page, AI Web Panel 로그인/OAuth를 실제 계정으로 확인한다.
- 영상/오디오 재생, 전체 화면, PiP, 탭 전환 후 오디오 상태 확인.

## 종료 / 복구

- 7개 이상 탭과 2개 이상 창을 연 뒤 정상 종료 → 재실행 → 탭/창 복구 확인.
- 강제 종료 후 재실행 → 복구 확인.
- 복구 직후 새 탭 생성/종료, 탭 이동 확인.
- 북마크/방문 기록/다운로드/설정/AI 설정이 유지되는지 확인.

## 기록 형식

각 항목을 `PASS`, `FAIL`, `UNVERIFIED`, `BLOCKED`로 기록하고, 실제 Windows 버전, WebView2 Runtime, DPI, 모니터 구성을 함께 남긴다.
