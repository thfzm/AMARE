# AMARE 0.67.0 — GPUI Stability & Windows Architecture Readiness

0.67.0은 0.66.0의 WinUI 기준선을 유지하면서 GPUI CI 실패 원인을 분리하고,
GPUI 다중 창 생성과 테스트 입력 경로를 수정한 버전입니다.

## 핵심 결론

- **WinUI는 계속 기본 패키지이자 기능 검증 기준입니다.**
- GPUI는 두 번째 창 생성과 WebView2 초기화 순서를 수정했지만,
  3회 반복에서 **3회 연속 0 FAIL을 달성하지 못했습니다.**
- 따라서 0.67.0에서 GPUI를 기본값으로 전환하지 않습니다.
- GPUI에는 AI Web Panel, updater, 사이트 권한 관리 UI가 아직 없습니다.

## WinUI 0.67.0

- Windows UI smoke **48/48 PASS**
- Windows assertions **143 PASS / 0 FAIL / 9 UNVERIFIED / 1 SKIPPED**
- C#/Rust interop **70/70 PASS**
- Linux CI **SUCCESS**
- 실제 Windows 11 사용자 재검증 **UNVERIFIED**

0.66.0에서 구현한 WinUI 단축키 라우터, Ctrl+N/New Window 동일 경로,
마지막 탭 controller 재사용, AMARE title strip은 그대로 유지했습니다.

## GPUI 0.67.0 수정

### 1. 두 번째 창 생성

- 새 창 생성 callback 안에서 첫 WebView2를 만들던 경로를 제거했습니다.
- 새 창이 먼저 render/status를 기록한 뒤, 400ms 후 WebView2를 attach합니다.
- 새 창을 `activate_window()`로 foreground에 올립니다.
- 이전에는 WebView2 controller 생성이 Win32 메시지를 pump하면서 두 번째
  창이 status를 쓰기 전에 UI 스레드를 막을 수 있었습니다.

### 2. 단축키 테스트 입력

- GPUI CI 도우미의 `Send-GpuiShortcut`이 `PostMessage`로 키를 보내
  modifier 상태를 갱신하지 않던 문제를 수정했습니다.
- Ctrl+T/N은 실제 `SendKeys` 입력으로 보냅니다.
- 제품 코드의 WebView2 → GPUI 동기 `SendMessageW` 전달은 유지합니다.

### 3. 테스트 fixture

- library/session/tab 테스트에 `AMARE_GPUI_WINDOW_SIZE=1044x788`를 적용해
  작은 runner 화면에서 창이 음수 좌표로 열려 screenshot이 비는 문제를 줄였습니다.
- 두 번째 창의 WebView2 준비 상태를 tab `loading`으로 기다립니다.
- password panel 열기와 entry 수정/삭제 클릭에 제한된 재시도를 추가했습니다.
- tear-off re-merge 전에 두 창을 화면 안에 나란히 배치합니다.

## GPUI 반복 결과

| 실행 | 코드 | password | multiwindow | input | tab | library |
|---|---|---:|---:|---:|---:|---:|
| #43588676092 | 0.67.0 | 51/0/1 | 5/1/0 | 24/0/5 | 10/1/2 | 0/1/0 |
| #43592111834 | 0.67.0 | 51/0/1 | 1/1/0 | 25/0/4 | 10/1/2 | 2/1/0 |
| #43592127672 | 0.67.0 | 51/0/1 | 1/1/0 | 25/0/4 | 10/1/2 | 2/1/0 |
| #43596729728 (final) | 0.67.0 | 51/0/1 | 5/1/0 | 25/0/4 | 10/1/2 | 2/1/0 |

- download **7/0/1**, settings **18~20/0~1/5~7**.
- 3회 연속 0 FAIL: **미달**.
- 두 번째 창 생성 자체는 trace에서 `open_new_window result=ok`로 확인했고,
  #43588676092에서는 두 창 Vault 동기화/독립 편집 **5 PASS**까지 진행했습니다.
- 남은 실패는 password entry edit 자동화 좌표, 4번째 탭 생성, local-server navigation,
  GPUI custom titlebar control click 등입니다.
- 제품 결함과 CI/자동화 제약을 완전히 분리하지 못한 항목은 **UNVERIFIED**입니다.

## WinUI / GPUI 기능 동등성

| 기능 | WinUI | GPUI |
|---|---|---|
| 탭 관리 | 구현/검증 완료 | 구현, CI 일부 실패 |
| 주소창/검색 | 구현/검증 완료 | 구현, input 25/0/4 |
| 뒤로/앞으로/새로고침 | 구현/검증 완료 | 구현, input PASS |
| 북마크 | 구현/검증 완료 | 구현, CI tab 의존 |
| 방문 기록 | 구현/검증 완료 | 구현, CI tab 의존 |
| 다운로드 | 구현/검증 완료 | 구현, 7/0/1 |
| Settings | 구현/검증 완료 | 구현, 18~19/0~1 |
| Session Restore | 구현/검증 완료 | 구현, CI **FAIL/UNVERIFIED** |
| AI Web Panel | 구현/검증 완료 | **미구현** |
| Vault/비밀번호 | 구현/검증 완료 | 구현, password 51/0/1 |
| 사이트 권한 | read-only UI | read-only 안내만, 관리 UI **미구현** |
| CapturePreview | 구현/검증 완료 | 구현, password capture PASS |
| 자체 창 상단 UI | 구현/검증 완료 | 구현, control click **UNVERIFIED** |
| 창 이동/리사이즈 | 구현, maximize/restore PASS | 구현, 실제 검증 **UNVERIFIED** |
| 다중 창 | 구현/검증 완료 | 구현, CI **불안정** |
| 탭 tear-off/re-merge | 구현/검증 완료 | 구현, CI **UNVERIFIED** |
| updater | 구현/검증 완료 | **미구현** |
| diagnostics | 구현/검증 완료 | 구현 |

## 마지막 탭 복구 비교

- **WinUI**: 마지막 탭에서 살아 있는 WebView2 controller를 재사용해 New Tab
  페이지로 재설정합니다. `last_tab_recovery` **PASS** (마지막 탭 종료 후
  example.com 탐색).
- **GPUI**: 마지막 탭 host를 drop하고 새 탭 + 새 controller를 만듭니다.
  코드는 구현되어 있지만 0.67 GPUI CI의 4-tab 단계 실패로 **UNVERIFIED**입니다.
- 두 구현 모두 종료된 탭의 이벤트가 새 탭 상태를 덮어쓰지 않도록
  controller/host를 분리합니다. GPUI의 실제 IME/자원 정리는 사용자 검증이 필요합니다.

## 창 상단 UI 비교

- **WinUI**: 32px AMARE title strip + 시스템 caption 버튼, 탭 스트립은 별도
  client 행. native titlebar 제거와 48/48 UI smoke를 확인했습니다.
- **GPUI**: 자체 titlebar와 최소화/최대화/닫기 control을 유지합니다.
  0.67 settings test에서 custom titlebar control click 4개가
  `UNVERIFIED`(“custom titlebar click did not change state; OS fallback used”)입니다.
- GPUI Snap, Alt+Tab, 작업 표시줄, DPI, 다중 모니터는 **UNVERIFIED**입니다.

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.67.0-win-x64.zip` (WinUI) | 126,906,500 bytes | `2d1307a67acae81772b72b0e1d1b5d843cff5bcc99a8c7d4c438bada7c4b1ea7` |
| `AMARE-0.67.0-win-x64-unsigned.msix` | 126,841,792 bytes | `f71a35dd3848b9d28fa1eeaeb4d203bdf3e126d71594bfb0cf3e252a41c5dc07` |
| `AMARE-0.67.0-gpui-win-x64.zip` | 5,891,544 bytes | `247f3306875bd5d91fa4a644a349a122d00182802fd72f21d5eb8d2ced970459` |

- UNSIGNED. MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**.
- 공개 GitHub Release 없음.

## 스크린샷

- WinUI: `artifacts/windows/screenshots/0.67.0/` — 7 PNG
  (1044×788 5개, desktop 1024×768, maximized 1040×736)
- GPUI: `artifacts/windows/screenshots/0.67.0/gpui/` — 52 PNG
- GPUI 두 창 동기화 screenshot은 0.67 최종 run에서 multiwindow가 조기
  실패해 확보하지 못했습니다. 실제 캡처한 파일만 기록합니다.

## 사용자 재검증

1. WinUI `AMARE.Windows.exe`에서 0.66.0 체크리스트 재확인
2. GPUI `amare-gpui.exe`에서 Ctrl+T/L/N, Settings, 두/세 창
3. GPUI 마지막 탭 종료 후 검색/URL/IME
4. GPUI 탭 tear-off/re-merge
5. GPUI titlebar 최소화/최대화/복원/닫기, Snap, Alt+Tab, DPI
6. 세션 복원/재실행

## 남은 문제

- GPUI 3회 연속 0 FAIL 미달
- GPUI 두 번째 창 준비/편집 단계의 테스트 불안정
- GPUI 4번째 탭 생성 및 local-server navigation 실패
- GPUI custom titlebar control click UNVERIFIED
- AI Web Panel, updater, 사이트 권한 관리 UI 미구현
- 실제 Windows 11 사용자 PASS UNVERIFIED

## GPUI 전환 판단

**0.67.0에서 GPUI를 기본값으로 전환하지 않습니다.**

GPUI는 핵심 브라우징·탭·Settings·다운로드·Vault·CapturePreview·자체 titlebar의
기반을 갖췄고 두 번째 창 생성 경로도 개선했습니다. 그러나 3회 연속 안정성,
다중 창/세션/tear-off 검증, titlebar control, AI/updater/permission 동등성이
남아 있습니다. 다음 버전은 GPUI 테스트 안정화와 미구현 기능의 제품 결정을
먼저 완료해야 합니다.
