# AMARE 0.58.0 — Stability and Privacy Verification

0.58.0은 신규 기능을 늘리기보다 0.57.0의 비밀번호 vault, 자동 완성 경계, 사이트 데이터 삭제, WebView2 캡처와 GPUI 자동화의 신뢰성을 재검증한 안정화 릴리스다. 검증되지 않은 항목은 PASS로 표시하지 않는다.

## Highlights

- GPUI password security suite **43 PASS / 0 FAIL / 0 UNVERIFIED**, vault unit **4/4**.
- 실제 WebView2 same-origin / cross-origin / nested iframe과 cross-origin action, action-change에서 credential capture **없음** (`pending=null`) 재확인.
- `NavigationStarting` 기반 navigation ID를 추가하고, 입력 시점에 source origin·form action origin·navigation ID를 다시 확인하는 경로 구현.
- 실제 삭제 확인/취소/완료, 마지막 계정 empty-state, 재시작 유지, 잠금 시 메모리 목록 제거 PASS.
- 손상 vault와 key 누락 상황에서 기존 `vault.bin` 원본 보존 PASS. 재시도/백업 안내 UI 유지.
- 다중 탭·다중 계정·DPAPI 오류·개인정보 설정 실제 Windows 캡처 확보. 다중 창 캡처는 시도했으나 한 창만 명확히 보여 **PARTIAL**이다.
- Windows CI **143 PASS / 0 FAIL / 9 UNVERIFIED / 1 SKIPPED**, WinUI GUI **42/42**.
- Windows ZIP과 unsigned MSIX, SHA-256 생성. 공개 GitHub Release는 만들지 않는다.

## 제한과 미검증

- 동일 프로세스 두 창에서 동시에 add/edit/delete하는 실제 UI mutation과 충돌 안내 UX는 **UNVERIFIED**다.
- 현재 같은 계정을 두 창에서 동시에 수정하면 마지막 저장이 이길 수 있다. 충돌을 조용히 덮어쓰지 않는 per-entry optimistic conflict UI는 아직 없다.
- 한 창에서 vault를 잠가도 다른 창의 잠금 상태를 전역으로 강제하는 경로는 **미구현/UNVERIFIED**다.
- ALL_SITE 삭제는 실제 UI lifecycle 완료를 안정적으로 확인하지 못해 0.58.0 UI에서 **read-only / BLOCKED**로 되돌렸다. 사이트별 삭제 UI는 제공하지 않는다.
- 자동 완성 승인 직후 navigation/tab/window 전환, 동적 form 교체, 처리 중 lock/delete, 빠른 연속 입력은 **UNVERIFIED**다. iframe/action-origin 자동화만 실제 WebView2로 PASS했다.
- WebView2 `CapturePreview`는 구현하지 않았다. CopyFromScreen 캡처는 여전히 페이지 DOM 내용이 blank이므로 실제 페이지 콘텐츠 캡처로 주장하지 않는다.
- GPUI tab/library/settings/session 자동화는 계속 FAIL했다. CI step은 `continue-on-error`로 password evidence 수집만 계속하며, 실패한 내부 test를 PASS로 바꾸지 않는다.
- 다른 Windows 사용자 계정/DPAPI 권한 실패, 저장 중 강제 종료, 파일 권한 부족은 환경상 **BLOCKED/UNVERIFIED**다.
- unsigned MSIX 설치는 `0x80073D2C`로 실패해 **UNVERIFIED**다. 코드 서명은 없다.

## Platform

- Windows: 0.58.0 ZIP / unsigned MSIX, x64.
- Linux: CI build/test PASS.
- GPUI: Windows 전용 release build와 실제 WebView2 GUI test.

전체 결과와 해시는 `artifacts/windows/release/AMARE-0.58.0-development-report.md`에 기록한다.
