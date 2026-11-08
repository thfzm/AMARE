# AMARE 0.47.0 — GPUI 입력·포커스·한글 IME 안정화

0.47.0은 GPUI 주소창을 실제 텍스트 입력 요소로 전환하고, Windows Korean IME 조합을 주소창과 WebView2 페이지 내부 input/textarea에서 검증했다.

## Highlights

- GPUI 주소창: 실제 mouse/keyboard 입력, Ctrl+L focus/select-all, Ctrl+R, Alt+Left/Right, Escape, Enter.
- GPUI 주소창 Korean IME PASS: 조합 중 marked range와 최종 `한글` 표시, Enter 검색.
- WebView2 로컬 테스트 페이지: input/password/textarea/한글 IME, Tab focus, Ctrl+A/C/V, 텍스트 선택, mouse drag, link click, wheel scroll, Enter/Escape.
- 주소창↔WebView2 focus 전환, URL/title 동기화, minimize/restore, 정상 종료/WebView2 cleanup PASS.
- 실제 Windows input automation 결과와 screenshot을 CI artifact로 보존.

## Unverified

- 페이지 우클릭 context menu.
- 125/150/200% DPI 및 multi-monitor.
- IME 빠른 연속/영문 혼합/조합 중 focus 이동의 세부 stress.
- GPUI는 여전히 기본 배포 진입점이 아니며 WinUI 3를 유지한다.
