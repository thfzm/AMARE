# AMARE 0.48.0 — GPUI Multi-Tab & Per-Tab WebView2

0.48.0은 GPUI 브라우저의 단일 WebView2 공유 구조를 탭별 독립 WebView2 Controller 구조로 전환하고, 탭 생성·전환·닫기와 탭별 페이지 상태 보존을 검증했다.

## Highlights

- 탭 ID 기반 데이터 모델과 탭별 WebView2 Controller/WebView2.
- 하나의 WebView2 Environment/Profile을 공유하고 Controller만 탭별로 분리.
- Ctrl+T, 새 탭 버튼, 탭 클릭, Ctrl+Tab/Ctrl+Shift+Tab, Ctrl+W, 탭 닫기 버튼.
- 1/5/10탭 생성, 반복 전환, 일부 닫기, 마지막 탭 닫기 → 브라우저 종료.
- 탭별 DOM, 입력값, 스크롤 위치, JavaScript 상태, 뒤로/앞으로 history 독립성.
- 비활성 탭 WebView2 숨김, 활성 탭 Bounds/visible/focus 갱신.
- 탭 전환 후 Korean IME 주소창 입력 PASS.
- 탭별 event handler token 제거와 Controller Close, 종료 후 WebView2 0→0.

## Limits

- favicon 표시, loading/error UI의 시각적 세부 검증은 UNVERIFIED.
- page context menu, 125/150/200% DPI, multi-monitor, IME rapid/mixed/focus-move stress는 UNVERIFIED.
- 실제 Windows 11/local CA/signing은 UNVERIFIED/BLOCKED.
- GPUI는 아직 기본 배포 진입점이 아니며 WinUI 3를 유지한다.
