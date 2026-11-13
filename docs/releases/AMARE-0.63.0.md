# AMARE 0.63.0 — Windows Input Harness & GPUI Hit-Test Forensics (Incomplete)

0.63.0은 제품 테스트 전에 Windows 입력 자동화 환경과 UIA를 독립 검증하는 포렌식 버전이다. **입력 자동화 환경 자체가 CI 세션에서 정상 동작하지 않아 실제 사용자 입력 E2E를 완료하지 못했다.**

## 결과

- Product code HEAD: `b6d7c80`
- Windows CI: **143/0/9/1**, WinUI GUI **42/42**
- Linux CI: **SUCCESS**
- GPUI CI: **FAILURE**
- Input harness: **FAIL** (SendInput 반환 0)
- UIA AMARE: **GPUI toolbar 요소 없음**
- Ctrl+T E2E: 1회 action 성공 / 반복 실패
- Ctrl+N E2E: 이전 run에서 1회 성공 / 0.63 run 반복 실패
- 새 탭/새 창 버튼: 실제 mouse click은 root에 수신되지만 button action 없음
- 3회 반복: **미달/미실행**

## 독립 입력 harness

`tools/windows-input-harness.ps1` — Win32/WinForms 버튼, key count, mouse count, foreground/focus 기록.

결과:

- `SendInput` Ctrl+T/Ctrl+N 반환값 **0**
- `ctrl_t=0`, `ctrl_n=0`
- `key_down=1` (PostMessage 수신)
- `posted_key_messages=2`
- 실제 mouse button click `button_click=1`
- foreground/focus: start=end, form 유지

판정:

- **OS 수준 SendInput이 CI runner에서 이벤트를 주입하지 못했다.**
- PostMessage는 도달했지만 modifier state가 없어 제품 shortcut 검증으로 사용할 수 없다.
- 실제 mouse click은 harness에서 동작했다.

## UIA Tree

AMARE GPUI root (`Zed::Window`) 아래 **9개 요소**만 노출됐다.

- Window root: bounds `1044×788`, Window/Transform pattern
- `Chrome_WidgetWin_0/1` WebView2 pane
- `Intermediate D3D Window`
- `BrowserRootView`, `NonClientView`, `EmbeddedBrowser*`, `TopContainerOverlayView`

GPUI custom toolbar 요소는 없었다:

- 새 탭 버튼: **UIA element 없음**
- 새 창 버튼: **UIA element 없음**
- AutomationId/Name/BoundingRectangle/InvokePattern: **없음**
- UIA Invoke: **not-found**

따라서 UIA BoundingRectangle 기반 버튼 hit-test를 수행할 수 없었다. 가상 AutomationId를 만들지 않았다.

## GPUI 버튼 hit-test

- 스크린샷상 toolbar plus/new-window 위치는 대략 x 805/839, y 30으로 확인됐다.
- 실제 mouse click scan에서 root mouse_down trace는 수신됐다.
- 그러나 해당 좌표에서 `new_tab` action/callback은 실행되지 않았다.
- UIA bounds가 없어 실제 버튼 bounds와 click point를 독립 대조하지 못했다.
- **좌표 불일치 / GPUI hit-test 실패 / drag 영역 간섭을 분리하지 못했다.**

## 제품 코드 변경

- 0.63.0에서 제품 `NewTab`/`NewWindow` action, tab 생성, window creation 로직을 변경하지 않았다.
- 입력 자동화 환경이 정상 이벤트를 전달하지 못하고 UIA bounds도 없으므로, 원인 확인 전 제품 코드를 추측 수정하지 않았다.
- version bump와 diagnostics만 변경했다.

## E2E 결과

- Ctrl+T: 1회 `action=NewTab`, `new_tab tabs=2` 확인. 반복 run에서는 action trace 없음.
- 새 탭 버튼: root mouse event는 있으나 action 없음.
- Ctrl+N: 이전 run에서 `open_new_window result=ok` 1회. 0.63 run에서는 foreground가 다른 HWND로 바뀌며 action 없음.
- 새 창 버튼: 두 번째 window 없음.
- **10회 Ctrl+T, 5회 Ctrl+N 반복 기준 미달.**

## 테스트 결과

- Windows: 143/0/9/1, GUI 42/42
- Linux: SUCCESS
- GPUI run `43452497868`: FAIL
  - password 51/0/1
  - multiwindow 1/1/0
  - input 25/0/4
  - tab 10/1/2
  - library 2/1/0
  - settings 18/0/7
  - session 0/1/0
- GPUI 3회 연속 0 FAIL: **미달/미실행**

## Vault / pending / CapturePreview

- Vault v2 유지.
- multiwindow 두 번째 창 생성 실패로 **UNVERIFIED**.
- pending autofill 실제 생성/취소: **UNVERIFIED**
- CapturePreview normal/active/max/minimize-restore: **PASS**
- 개별 탭 2개: **UNVERIFIED**
- ALL_SITE: **read-only/BLOCKED**

## 스크린샷 / 진단 로그

- `artifacts/windows/screenshots/0.63.0/` — PNG **52개**
- `artifacts/windows/diagnostics/0.63.0/input-harness.json`
- `artifacts/windows/diagnostics/0.63.0/uia-amare.json`
- `artifacts/windows/diagnostics/0.63.0/input-diagnostics.json`
- 확보: initial, 새 탭, normal page, 설정, 다운로드, CapturePreview, maximize/restore, UIA screenshot
- 미확보: 두 번째 창/두 창 동시, conflict, stale, lock/unlock, multiwindow restart, 3/4 tabs, button click 결과

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.63.0-win-x64.zip` | 126,900,762 bytes | `be7ae4f266a37c7fc5d101b78b810b18e57e96febe1c4df52cd300e467b7137a` |
| `AMARE-0.63.0-win-x64-unsigned.msix` | 126,836,691 bytes | `ab56e6bc7940b168d9f05b29bbd610f717eddd0d184408a9d4bdc6f639c8e2da` |

Unsigned. MSIX install `0x80073D2C` UNVERIFIED/BLOCKED. 공개 Release 없음.

## 판정

**0.63.0은 입력 자동화 환경과 GPUI UIA/hit-test의 한계를 입증했지만 실제 사용자 입력 E2E를 완료하지 못했다.** CI runner의 SendInput 비활성, GPUI toolbar UIA 미노출, 버튼 mouse hit-test 미확인이 핵심 BLOCKED 사유다.
