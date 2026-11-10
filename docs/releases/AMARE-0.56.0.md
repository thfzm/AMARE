# AMARE 0.56.0 — GPUI Password Security Hardening

0.56.0은 0.55.0 비밀번호 관리자의 실제 삭제 UI, 손상 vault 복구 안내, 다중 창 저장 일관성과 보안 회귀를 보강한 안정화 릴리스다.

## Highlights

- 실제 계정 삭제 확인/취소/확인 UI와 재시작 후 삭제 상태 유지 검증.
- 손상된 vault를 자동 삭제·덮어쓰기하지 않고 원본 보존, 재시도와 vault 백업 안내 제공.
- DPAPI key 누락 상태에서 새 key를 만들어 기존 vault를 덮어쓰는 경로 차단.
- vault 저장 시 기존 암호화 파일을 다시 읽어 삭제 ID를 제외하고 계정을 merge; 다중 창 generation sync 경로 추가.
- WebView2 built-in password autosave/general autofill 비활성화 유지.
- SQLite 평문 비밀번호 부재, data/status 비밀번호 scan, 잠금 시 메모리 목록 제거 유지.
- 실제 Windows 0.56 screenshots와 password unit/GUI 보안 검증.

## Unverified / blocked

- iframe·중첩 iframe·교차 출처 action 자동화 공격.
- 다른 Windows 사용자/DPAPI 권한 실패, 잘못된 key 참조의 실제 사용자 계정 재현.
- 다중 창 동시 UI mutation의 실제 자동화 및 충돌 UX.
- 마지막 계정 삭제 후 empty-state 자동 캡처.
- 쿠키/사이트 데이터/사이트 권한 제어, clipboard copy/auto-clear.
- 기존 session/titlebar/library 자동화 flake, WinUI new-window timeout, DPI/Windows 11/signing.
