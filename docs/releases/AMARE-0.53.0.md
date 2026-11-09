# AMARE 0.53.0 — GPUI Session Restore & Downloads

0.53.0은 GPUI 브라우저의 탭·창 세션을 SQLite settings에 저장하고 재실행 시 복원하며, 다운로드 목록 연속성과 실제 재시작 화면 증거를 추가했다.

## Highlights

- SQLite `gpui.session` JSON에 창별 탭 URL/title/순서, 활성 탭, 창 위치/크기, 최대화 상태를 저장.
- GPUI 재실행 시 저장된 탭을 새 WebView2로 복원하고 주소/제목/활성 탭을 재구성.
- `AMARE_GPUI_RESTORE=0`과 self-test에서 복원을 비활성화하는 opt-out.
- 단일 창 실제 재시작에서 탭 수/순서/제목/URL/활성 탭/창 geometry **PASS**.
- `15-session-restored.png`, `16-downloads-after-restart.png` 실제 화면 캡처.
- 기존 WebView2 downloads, SQLite bookmark/history, custom titlebar/icon 회귀 유지.

## Unverified

- 다중 창 동시 복원과 창 ID/위치/최대화 race.
- DOM/JS/입력/scroll/history/page state 복원.
- 세션 복원 설정 화면, 진행/실패 UI, 손상 JSON 복구 UX.
- 다운로드 중 종료 후 재개 정책, pause/resume/cancel 자동화.
- 재시작 앱 종료 후 WebView2 cleanup, DPI/multi-monitor, Windows 11/local CA/signing.
