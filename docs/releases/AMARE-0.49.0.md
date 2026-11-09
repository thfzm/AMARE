# AMARE 0.49.0 — GPUI Popups, New Windows & Tab Drag

0.49.0은 GPUI 브라우저에 WebView2 NewWindowRequested 기반 팝업 처리, 독립 브라우저 창, 실제 마우스 탭 드래그 재정렬을 추가했다.

## Highlights

- target="_blank" 및 window.open() 요청을 새 탭으로 열기.
- Ctrl+N/새 창 버튼으로 독립 탭 목록을 가진 새 브라우저 창 생성.
- 새 창/탭별 WebView2 Controller를 유지하면서 Environment/Profile 공유.
- 실제 mouse drag로 탭 순서 변경 및 탭 표시줄 밖 drop 시 원래 순서 복구.
- 탭 드래그와 탭 클릭/닫기 이벤트 충돌 방지.
- 창 닫기 시 해당 창 탭/WebView2 정리, 다른 창 유지.
- 기존 10탭·Korean IME·탭별 상태/history 회귀 유지.

## Unverified

- 창 간 탭 이동/분리, 명시적 팝업 창.
- 페이지 우클릭 context menu, favicon/loading/error UI 시각 세부.
- 125/150/200% DPI, multi-monitor, 장시간 IME/Handle stress.
- 실제 Windows 11/local CA/signing.
- GPUI는 여전히 기본 배포 진입점이 아니며 WinUI 3를 유지한다.
