# AMARE 0.85.0 — Native Focus Recovery & Input Delivery Verification

0.85.0은 foreground thread 기준 native focus를 정확히 기록하고,
NavigationCompleted 이후 WebView2 GotFocus가 주소창 focus를 빼앗는 문제에
대해 **조건부 1회 복구**를 실험한 버전입니다.
**GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- 0.81 `0xC0000409` 수정 유지: titlebar **crash 0/5**, exit classifier **7/7**
- Native focus telemetry 확장:
  - foreground thread 기준 `GetGUIThreadInfo`
  - keyboard focus HWND/class/TID/PID
  - GPUI thread focus와 별도 필드
  - status 작성 시각/stale 판단
- **GotFocus 1회 복구 실험**
  - 조건: navigation 시작 시 AddressInput focus
  - WebView2 GotFocus 발생 시 **1회만** `SetFocus(parent)`
  - `set_focus_ok=True`, `after_focus=Zed::Window` 확인
- Probe 10회 모두 **Ctrl+L / 주소 click / clear PASS**
  - 0.84에서는 navigation 이후 mouse_delta=0, clear FAIL
- 남은 probe 실패는 **SendKeys 문자 손실**뿐
  - 텍스트가 11/11인 round는 Enter → `SubmitAddress` → submit **PASS**
- 동일 코드 3회: **13/9**, **12/11**, **12/12** FAIL/UNVERIFIED
  - navigation PASS **64/69/72**, FAIL 7
- Titlebar 5회: full PASS **3/5**, runner cancel 2회, crash **0/5**

## 1. Native focus 관측 정확도

추가 필드:

- `foreground_gui_thread_info_ok`
- `keyboard_focus_hwnd/class/thread_id/pid`
- `keyboard_active_hwnd`
- `status_written_at_ms`
- webview focus trace의 `foreground_focus_hwnd/class/pid`

- app GUI thread 결과와 foreground thread 결과를 분리
- 조회 실패(`ok=False`)와 실제 focus 부재를 구분
- status age가 큰 경우 stale로 표시하고 현재 focus로 사용하지 않음

## 2. Focus 시간축

확인된 순서:

```text
navigation_starting   focus = Zed::Window, address_intent = True
navigation_completed  focus = Zed::Window
webview_got_focus     before = WebView2 child
                      planned_after_navigation = True
                      should_recover = True
                      set_focus_ok = True
                      after = Zed::Window
```

- 0.84: `webview_got_focus` 후 `Chrome_WidgetWin_1` 잔존
- 0.85: 조건 충족 시 1회 복구로 `after=Zed::Window`
- 복구는 navigation 시작 시 AddressInput이 focused였던 경우에만 실행
- 사용자가 페이지를 클릭한 일반 GotFocus에는 복구 조건을 적용하지 않음
- 무한 SetFocus 반복 없음

## 3. AddressInput focus 복구 결과

probe 10회:

| 항목 | 결과 |
|---|---|
| Ctrl+L 후 focus window | **10/10 PASS** |
| 주소 bounds click `mouse_delta=1` | **10/10 PASS** |
| address clear | **10/10 PASS** |
| recovery `set_focus_ok=True` | **PASS** |
| recovery 후 `Zed::Window` | **PASS** |
| 텍스트 11/11 | 2/10 (나머지 SendKeys 손실) |
| 텍스트 성공 후 Enter/submit | **2/2 PASS** |

- 0.84의 navigation 이후 mouse_delta=0/clear FAIL이 0.85에서 재현되지 않음
- focus 복구 효과 **PASS**
- 남은 문제는 자동화 문자 손실

## 4. Ctrl+L / accelerator

- 10회 모두 Ctrl+L 후 `selected=0,N`, focus window
- 실패 round의 accelerator callback 미관측 문제는 재현되지 않음
- Ctrl+T/N/W/R과의 직접 비교는 미완료
- 기존 shortcut을 가로채는 변경 없음

## 5. 주소창 mouse input

- 10회 모두 `mouse_delta=1`, `focus=Zed::Window`
- navigation 이후에도 주소 bounds click이 GPUI에 도달
- 0.84의 `mouse_delta=0` 문제 **재현 없음**
- Win32 `WM_LBUTTONDOWN`와 GPUI mouse의 완전한 동시 비교는 미완료

## 6. 동일 코드 3회 (`dbd3193`)

| run | Aggregate | navigation |
|---|---:|---:|
| 44247560173 | **13/9** | 64/7/8 |
| 44247570342 | **12/11** | 69/7/10 |
| 44247579066 | **12/12** | 72/7/11 |

- 0.84 대비 navigation PASS 증가, UNVERIFIED 감소
- 3회 연속 0 FAIL 미달

## 7. Titlebar / crash

| run | stage | exit | 판정 |
|---|---|---|---|
| 44247588283 | 32/0/1 | 0 | **PASS** |
| 44247596783 | 32/0/1 | 0 | **PASS** |
| 44247605281 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |
| 44247615479 | 32/0/1 | 0 | **PASS** |
| 44247625039 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |

- full PASS **3/5**
- `0xC0000409` **0/5**
- exit classifier **7/7 PASS**

## 8. First-click / 기능 회귀

| 영역 | run1 | run2 | run3 |
|---|---|---|---|
| Password | 2/1 | 2/1 | 2/1 |
| Vault | 1/1 | 1/1 | 1/1 |
| Tab | 1/1 | 1/1 | 1/1 |
| Library | 4/1 | 6/1 | 6/1 |
| Settings | 32/1/1 | 33/0/1 | 33/0/1 |
| Download | 1/1 | 1/1 | 1/1 |

- first-click 실패는 남아 있음
- 입력 precondition/제품 상태 분리는 일부만 완료
- **TEST_AUTOMATION_BUG / UNRESOLVED** 혼재

## 9. Session / Navigation / Corrupt

- Session: **2/1** 3회
- Corrupt matrix: **14/14 PASS** 3회
- Navigation: 64/7/8, 69/7/10, 72/7/11
- Titlebar 3 PASS / 2 runner cancel

## 10. WinUI 기준선 / UIA race

- `44247634130`: UI **48/48**, **143/0/9/1**, interop **70/70**
- 0.83 48/49 UIA race는 0.84/0.85에서 재현되지 않음
- 0.83 실패 selector 원인은 **UNRESOLVED**

## 11. 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.85.0-win-x64.zip` | 126,906,510 bytes | `2006eb0fe01ae3b2d8a1d8a93be6545d6f86dfa55e6e466b61a4130bac2dc887` |
| `AMARE-0.85.0-win-x64-unsigned.msix` | 126,841,813 bytes | `43da0a4346139e208f67aa923ace054eefac64037e488c1d1516fbf9c6e3e267` |
| `AMARE-0.85.0-gpui-win-x64.zip` | 5,970,408 bytes | `7adb45d7fa4c29d2a47781ddc2619f2ab13c9acd0e222c8a466d01405e02d0c2` |

- manifest `git_commit=dbd3193`, **UNSIGNED**
- MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. 조건부 1회 GotFocus 복구는 probe에서
focus를 `Zed::Window`로 되돌렸고 Ctrl+L/mouse/clear 10/10을 회복했습니다.
그러나 SendKeys 문자 손실, first-click, 3회 연속 0 FAIL, 실제 Windows 11/IME
검증이 남아 **GPUI 기본 전환은 보류**합니다.
