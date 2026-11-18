# AMARE 0.84.0 — WebView2 Focus Ownership & Address Bar Recovery

0.84.0은 navigation 이후 WebView2가 keyboard focus를 가져가는 경로와 주소창
focus 복구 실패를 추적한 버전입니다. **GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- 0.81 `0xC0000409` 수정 유지: titlebar **crash 0/5**, exit classifier **7/7**
- WebView2 focus callback 계측 추가:
  - `GotFocus`, `LostFocus`, `MoveFocusRequested`, `AcceleratorKeyPressed`
  - NavigationStarting/Completed focus snapshot
- **focus 획득 경로 확인**:
  - `navigation_completed` 시 focus `Zed::Window`
  - 직후 **`webview_got_focus` → `Chrome_WidgetWin_1`**
- 텍스트가 실제 입력된 경우 Enter → `SubmitAddress` → submit **PASS**
- 실패 시 주소창 mouse click `mouse_delta=0`, Ctrl+L 후에도 실제 focus는 WebView2
- 동일 코드 3회: **12/16**, **12/21**, **12/15** FAIL/UNVERIFIED — 0 FAIL 미달
- Titlebar 5회: full PASS **2/5**, runner cancel 3회, crash **0/5**

## 1. Focus 시간축

probe에서 확인된 순서:

```text
navigation_starting   focus = Zed::Window
navigation_completed  focus = Zed::Window
webview_got_focus     focus = Chrome_WidgetWin_1
```

- NavigationCompleted 직후 WebView2 `GotFocus`가 실제 focus를 가져감
- GPUI `address_focused=true`는 마지막 render 값으로 stale
- status age가 15~66초까지 벌어져 현재 focus로 사용할 수 없음
- `GetGUIThreadInfo`를 app GUI thread로 조회하면 `Zed::Window`로 보이지만,
  실제 keyboard focus는 WebView2 child가 소유

## 2. AddressInput focus 복구

| 동작 | 결과 |
|---|---|
| 정상 round Ctrl+L | focus window, selected 0,N **PASS** |
| navigation 이후 Ctrl+L | selected N,N, 실제 focus WebView2 **UNVERIFIED** |
| 정상 round 주소 click | `mouse_delta=1`, focus window **PASS** |
| navigation 이후 주소 click | `mouse_delta=0`, focus 변화 없음 **UNVERIFIED** |
| restore 직후 | Ctrl+L/click/clear 대부분 PASS |
| restore 후 navigation | 동일하게 WebView2 focus 잔존 |

- `focus_address_input`의 `SetFocus(parent)`만으로는 복구되지 않음
- mouse click이 GPUI에 도달하지 않는 round가 있어 `WM_LBUTTONDOWN` 단계 손실 의심
- 정확한 회복 조건 **UNRESOLVED**

## 3. Ctrl+L / AcceleratorKeyPressed

- 정상 round: Ctrl+L 후 `selected=0,N` **PASS**
- 실패 round: `selected=N,N`, 실제 focus WebView2
- 실패 round의 focus trace에서 **AcceleratorKeyPressed callback이 관측되지 않음**
- 따라서 Ctrl+L이 WebView2 accelerator로 전달됐는지, GPUI shortcut으로
  전달됐는지 확정 불가
- Ctrl+T/N/W/R과 비교한 modifier 손실 증거는 아직 없음
- **UNRESOLVED**

## 4. 주소창 mouse input

- 정상: `mouse_delta=1`, focus `Zed::Window`, clear/typing PASS
- 실패: `mouse_delta=0`, focus 변화 없음
- `address_bounds` 좌표는 유효했지만 click이 GPUI에 도달하지 않음
- 창 비활성화/자동화 좌표 오류/WebView2 child hit 가능성 분리 필요
- **UNRESOLVED**

## 5. First-click / 기능 회귀

| 영역 | run1 | run2 | run3 |
|---|---|---|---|
| Password | 2/1 | 2/1 | 2/1 |
| Vault | 2/1 | 1/1 | 1/1 |
| Tab | 1/1 | 1/1 | 1/1 |
| Library | 4/1 | 6/1 | 5/1 |
| Settings | 20/1/1 | 33/0/1 | 33/0/1 |
| Download | 1/1 | 1/1 | 1/1 |

- first-click 실패는 입력 precondition과 분리되지 않은 항목이 남음
- 최초 실패 보존
- **TEST_AUTOMATION_BUG / UNRESOLVED** 혼재

## 6. Session / Navigation / Multi-window

- Corrupt matrix: **14/14 — 3회 모두**
- Session: **2/1** 3회
- Navigation: **62/6/15**, **51/7/20**, **60/7/14**
- Titlebar:
  - `44236136696`, `44236162999`: **PASS 32/0/1**
  - 나머지 3회: runner cancel, stage FAIL 없음
- `0xC0000409` **0/5**

## 7. WinUI 기준선 / UIA race

- 0.84 run `44236171959`: UI **48/48**, **143/0/9/1**, interop **70/70**
- 0.83의 48/49 UIA race는 0.84에서 재현되지 않음
- 0.83 실패 selector는 **UNRESOLVED** (재현되지 않아 원인 미확정)

## 8. 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.84.0-win-x64.zip` | 126,906,490 bytes | `d6d8ee5be8cd652c4aadffe36ab8446f4b8700693d679687cc162b356a502bc1` |
| `AMARE-0.84.0-win-x64-unsigned.msix` | 126,841,828 bytes | `7224e9343ce8fdf9e341252bb45da8c548a6412b767bf55a39bd0879f6fc71df` |
| `AMARE-0.84.0-gpui-win-x64.zip` | 5,967,767 bytes | `60b0c0a882878057a564b62e1b78996fabd0f31dcbbae1b1689aecd3522c5e9e` |

- manifest `git_commit=ab05c1f`, **UNSIGNED**
- MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. WebView2 `GotFocus`가 NavigationCompleted
직후 focus를 가져가는 경로를 확인했지만, AddressInput focus 복구와 Ctrl+L/mouse
전달 손실은 아직 해결되지 않았습니다. 3회 연속 0 FAIL도 미달하여 **GPUI 기본
전환은 보류**합니다.
