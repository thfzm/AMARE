# AMARE 0.50.0 — GPUI Cross-Window Tab Transfer Investigation

0.50.0은 창 간 탭 이동/분리에 필요한 WebView2 Controller 이전 경로를 조사하고, 공식 `SetParentWindow` 기반 이전을 시도했다.

## Implemented / Verified

- 0.49의 target=_blank/window.open 팝업 탭, 새 브라우저 창, 마우스 탭 드래그 재정렬 유지.
- 탭 detach용 seed window/`SetParentWindow`/shared Environment 구조 추가.
- 10탭, popup, new window, 탭 드래그, Korean IME, WebView2 0→0 회귀 PASS.
- cross-window drag 이동은 자동화/안전성 입증 전까지 UNVERIFIED.

## Unverified

- 기존 창 A→B 탭 이동 및 drag-outside detach의 실제 상태 보존.
- 명시적 팝업 창, context menu, favicon/loading/error UI 세부.
- DPI/multi-monitor, 장시간 IME/Handle stress, Windows 11/local CA/signing.
- GPUI는 아직 기본 배포 진입점이 아니며 WinUI 3를 유지한다.
