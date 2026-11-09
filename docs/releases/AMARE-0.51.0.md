# AMARE 0.51.0 — GPUI Bookmarks, History & Library UI

0.51.0은 GPUI 브라우저에 SQLite 기반 북마크와 방문 기록, 검색·삭제 패널, 실제 Windows 스크린샷 검증을 추가했다.

## Highlights

- 현재 페이지 북마크 추가/제거, 중복 URL 토글, 북마크 목록/이동/삭제.
- 방문 기록 자동 저장, 최근 방문순 목록, 검색, 개별/전체 삭제 확인 UI.
- 기존 amare-store/amare-services SQLite·WAL·트랜잭션 구조 재사용.
- GPUI 우측 북마크/방문 기록 패널, 검색 입력, 빈 상태, 닫기/삭제 UI.
- 데이터 디렉터리 재시작 후 북마크/방문 기록 복원 검증.
- 실제 GPUI 화면 8종을 1044×788 기준으로 캡처(최대화 포함).
- 기존 다중 탭, 팝업, 새 창, 탭 드래그, Korean IME, WebView2 cleanup 회귀 유지.

## Unverified

- 창 간 탭 이동/분리, 명시적 팝업 창.
- context menu, favicon/loading/error UI 세부.
- 북마크 제목 수정 UI, 폴더/정렬, 여러 창 동시 데이터 변경 stress.
- 125/150/200% DPI, multi-monitor, 장시간 IME/Handle stress.
- 실제 Windows 11/local CA/signing.
- GPUI는 아직 기본 배포 진입점이 아니며 WinUI 3를 유지한다.
