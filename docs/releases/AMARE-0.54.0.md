# AMARE 0.54.0 — GPUI Browser Settings & Environment

0.54.0은 GPUI 브라우저 설정을 하나의 SQLite settings 경로로 통합하고, 변경 사항을 즉시 적용·재시작 유지하며, 0.53.0 세션 복원과 다중 창 복원 검증을 보강했다.

## Highlights

- 검색 가능한 설정 화면: 외관, 시작 및 세션, 검색, 다운로드, 개인정보 및 보안, 고급.
- 라이트/다크/시스템 테마, UI 밀도, 글꼴 패밀리/크기, 탭 너비 실제 적용.
- 시작 페이지, 새 탭 페이지, 세션 복원, 종료 시 저장, 기본 검색 엔진, 주소 표시줄 동작.
- 다운로드 경로와 다운로드 시작 시 패널 열기.
- 방문 기록 저장 여부, 인증서 경고 정책 안내.
- AMARE/WebView2 버전, 데이터 경로, GPU 정책, 상태 JSON 경로, 확인 후 전체 설정 초기화.
- 설정 검색, 오류 표시, 좁은 창 대응, 다중 창 설정 동기화.
- 실제 top-level 두 창 세션 복원과 `11-multi-window-session-restored.png` 캡처.
- 실제 GPUI 0.54.0 screenshots와 settings/library/download/tab/input CI.

## Unverified

- DOM/JavaScript/scroll/history/page state 복원; URL 재탐색과 내부 상태 복원 구분.
- 쿠키·사이트 데이터·사이트 권한 관리 진입점.
- 다운로드 저장 위치 확인/완료 알림/pause/resume/cancel UI 자동화.
- 다중 창 graceful exit session snapshot; 현재 다중 창 복원 검증은 forced termination 이후 확인.
- DPI 125/150/200%, multi-monitor, Windows 11, local CA/signing.
- 장시간 IME/Handle stress 및 일부 이전 tab/popup 자동화 flaky.
