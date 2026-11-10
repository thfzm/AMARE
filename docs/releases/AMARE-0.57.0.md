# AMARE 0.57.0 — GPUI Browser Security Boundary & Privacy

0.57.0은 비밀번호 자동 완성의 iframe·교차 출처 경계를 강화하고, 다중 창 vault merge/generation, 마지막 계정 상태, 개인정보 토글과 WebView2 사이트 데이터 삭제 진입점을 추가했다.

## Highlights

- top-level exact-origin guard와 iframe/nested iframe no-capture 검증.
- cross-origin form action 및 action-change credential capture 거부.
- 자동 완성은 사용자 선택 후에만 실행되고 자동 submit하지 않음.
- 실제 삭제 확인/취소/완료, 마지막 계정 삭제 empty-state, 재시작 유지.
- 손상 vault 원본 보존, 재시도/백업 안내, key 누락 시 덮어쓰기 방지.
- vault 저장 시 remote merge + deleted ID 제외, 다중 창 generation sync 경로.
- 비밀번호 자동 완성/저장 제안 활성화 설정.
- WebView2 ALL_SITE browsing data 삭제 entry point와 확인 UI. 사이트별 삭제는 공식 API 범위 밖이므로 UNVERIFIED.
- 실제 multi-tab/multi-window 및 security test screenshots.

## Unverified

- 다른 Windows 사용자/DPAPI 권한 실패, 손상 key/권한 부족의 실제 사용자 계정 재현.
- 다중 창 동시 add/edit/delete UI mutation과 충돌 UX.
- 페이지 이동/탭 전환 race, 실제 교차 출처 공격의 모든 브라우저 조합.
- 사이트별 쿠키/권한 조회·삭제·변경(공식 API 제약), clipboard copy/auto-clear.
- 기존 GPUI session/titlebar/library 자동화 flake, DPI/multi-monitor/Windows 11/signing.
