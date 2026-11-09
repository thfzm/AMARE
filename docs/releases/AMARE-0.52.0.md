# AMARE 0.52.0 — GPUI Downloads, Native Titlebar & Icon

0.52.0은 WebView2 공식 다운로드 API 기반 다운로드 관리자, Windows custom titlebar 탭 통합, AMARE 아이콘 리소스를 추가했다.

## Highlights

- WebView2 DownloadStarting, 진행률/완료/실패 상태, 저장 경로/중복 파일명/취소/열기/폴더 열기.
- GPUI 다운로드 패널과 SQLite ProfileStore downloads 영속화.
- Windows custom titlebar: 탭 바를 창 최상단에 통합하고 최소화/최대화/복원/닫기 시스템 hit area 배치.
- 기존 AMARE.ico를 GPUI exe resource로 포함하고 실제 작업 표시줄/Alt+Tab 화면 캡처.
- 실제 GPUI screenshots 0.52.0 artifact와 download/library 테스트.
- 기존 북마크/방문 기록/다중 탭/IME/WebView2 정리 회귀 유지.

## Unverified

- 다운로드 pause/resume/cancel UI 자동화, 대용량/동시 다운로드 stress, 재시작 진행 중 다운로드 정책.
- Windows window controls 전체 동작/DPI/멀티모니터, Alt+Tab 아이콘 세부.
- 창 간 탭 이동/분리, context menu, 일부 이전 회귀.
- Windows 11/local CA/signing.
