# AMARE 0.68.0 — GPUI Stability & Migration Gap Closure

0.68.0은 0.67.0의 GPUI 실패를 더 세밀하게 분리하고, 창/복원 lifecycle을
수정한 버전입니다. **GPUI를 기본 패키지로 전환하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- GPUI 최종: **5 FAIL / 7 UNVERIFIED**, 동일 코드 3회 반복 모두 5 FAIL, 3회 연속 0 FAIL **미달**
- 두 번째·세 번째 창 생성과 창별 탭 독립성은 확인
- Session Restore는 코드를 보강했지만 **CI restart 검증 실패/UNVERIFIED**
- Password edit, 4번째 탭, local navigation, custom titlebar control은 미해결
- 실제 Windows 11 사용자 PASS는 **UNVERIFIED**

## GPUI 수정

- 새 창/복원 창의 WebView2 controller 생성을 **effect/paint flush 밖**으로
  이동했습니다. `defer_in` 안에서 controller를 만들면 GPUI RefCell 재진입
  panic이 발생했습니다.
- 복원 탭은 창이 먼저 render/status를 기록한 뒤 controller를 attach합니다.
  활성 탭을 먼저 attach합니다.
- multiwindow 테스트에 세 번째 창 생성, 창별 탭 독립성, 세 번째 창 종료를
  추가했습니다.
- status에 `platform`, `version`, `webview` attached를 추가했습니다.
- titlebar control 테스트가 실제 `layout_bounds`를 사용하도록 수정했습니다.

## GPUI 최종 결과

| 영역 | PASS | FAIL | UNVERIFIED |
|---|---:|---:|---:|
| password | 51 | 0 | 1 |
| multiwindow | 6 | 1 | 0 |
| input | 25 | 0 | 4 |
| tab | 10 | 1 | 2 |
| library | 2 | 1 | 0 |
| download | 7 | 0 | 1 |
| settings | 13 | 1 | 4 |
| session | 0 | 1 | 0 |

Aggregate: **5 FAIL / 7 UNVERIFIED** (3회 반복: 5/7, 5/5, 5/7).

### 실패 분류

- **Password edit**: TEST_AUTOMATION_BUG / TIMING. custom-rendered 수정 버튼의
  실제 hit-test와 foreground 전환이 불안정합니다. Vault 데이터 변경은 확인되지
  않았습니다.
- **4번째 탭**: TEST_AUTOMATION_BUG / TIMING. trace에 `new_tab id=5 tabs=3`이
  남아 탭 생성과 status count read 사이의 race로 판단했습니다.
- **local navigation**: UNRESOLVED. server readiness와 address-bar retry를
  추가했지만 `127.0.0.1:8765/8772` source에 도달하지 못했습니다.
- **custom titlebar control**: PRODUCT/TEST UNRESOLVED. layout bounds click도
  window state를 바꾸지 못해 OS fallback으로 기록됐습니다.
- **Session Restore restart**: PRODUCT/TIMING. 재시작 앱이 status를 쓰기 전에
  멈추는 현상이 남아 **FAIL/UNVERIFIED**입니다.
- **세 번째 창 종료**: TEST_AUTOMATION_BUG. 생성/탭 독립성은 PASS했고
  Alt+F4 close 자동화만 실패했습니다.

## WinUI 0.68.0

0.66.0에서 구현한 단축키 라우터, Ctrl+N/New Window 동일 경로, 마지막 탭
controller 재사용, AMARE title strip을 유지했습니다.

- Windows UI smoke **48/48**
- Windows assertions **143/0/9/1**
- C#/Rust interop **70/70**
- Linux CI **SUCCESS**
- 실제 Windows 11 사용자 재검증 **UNVERIFIED**

## 기능 격차

자세한 P0/P1/P2 계획은 [GPUI_MIGRATION_GAPS.md](../GPUI_MIGRATION_GAPS.md)에
기록했습니다.

- **미구현**: AI Web Panel, updater, GPUI 사이트 권한 관리 UI
- **부분 검증**: 탭, 주소/검색, Settings, 다중 창, tear-off
- **FAIL/UNVERIFIED**: Session Restore, custom titlebar control
- **검증됨**: WinUI 전체 기준선, GPUI password/input/download 일부

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.68.0-win-x64.zip` (WinUI) | 126,906,498 bytes | `9f90eadceea4a8f60def1c6a600e367be6f289a5cc8a0b82f84606247529381b` |
| `AMARE-0.68.0-win-x64-unsigned.msix` | 126,841,829 bytes | `b4d5c90b5479f24d20bb0796df2ff63e4fc887976030d52565ce50166c52a5e3` |
| `AMARE-0.68.0-gpui-win-x64.zip` | 5,901,641 bytes | `556e2154e64ffddd5715bf070e4bf63822052a825a037652f36870c86fa51b01` |

- UNSIGNED. MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**.
- 공개 GitHub Release 없음.

## 스크린샷

- WinUI: `artifacts/windows/screenshots/0.68.0/` — 7 PNG
- GPUI: `artifacts/windows/screenshots/0.68.0/gpui/` — 51 PNG
- 0.68 최종 GPUI run에서 세 번째 창 종료가 실패해 두 창 동기화 screenshot은
  확보하지 못했습니다. 실제 캡처한 파일만 기록합니다.

## 사용자 재검증

1. WinUI `AMARE.Windows.exe`에서 0.66.0 체크리스트 재확인
2. GPUI에서 WebView2 페이지 Ctrl+T/L/N, Settings, 주소창
3. GPUI 두 번째·세 번째 창 생성/전환/종료
4. GPUI 탭 tear-off/re-merge
5. GPUI 마지막 탭 종료 후 검색/URL/IME
6. GPUI Session Restore (탭/URL/활성 탭/창 크기)
7. GPUI custom titlebar 최소화/최대화/복원/닫기, Snap, Alt+Tab, DPI

## 남은 문제

- GPUI 3회 연속 0 FAIL 미달
- Password entry edit 자동화
- 4번째 탭 생성/status race
- local server navigation
- Session Restore restart
- custom titlebar control click
- AI/updater/permission 미구현
- 실제 Windows 11 사용자 검증

## GPUI 전환 판단

**0.68.0에서 GPUI를 기본값으로 전환하지 않습니다.**

P0 안정성, Session Restore, titlebar control, 기능 격차 해소가 완료되지
않았습니다. WinUI는 계속 기본 패키지이자 검증 기준입니다.
