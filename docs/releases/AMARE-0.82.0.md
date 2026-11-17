# AMARE 0.82.0 — Restore Enter / IME Event Delivery & Focus Ownership

0.82.0은 restore-first Enter가 GPUI에 도달하지 않는 위치를 Win32 메시지와
IME/focus 상태로 추적한 버전입니다. **GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- 0.81 `0xC0000409` 수정 유지: titlebar 5회 **crash 0/5**
- Exit code classifier **7/7 PASS**
- Win32 메시지 추적 추가: `WM_KEYDOWN/UP`, `WM_CHAR`, `WM_IME_*`,
  `WM_SETFOCUS/KILLFOCUS`, `WM_ACTIVATE`
- restore-first Enter:
  - foreground **PASS**
  - external Win32 focus `Zed::Window` **PASS**
  - `window_active=true`, `address_focused=true` **PASS**
  - **WM_KEYDOWN VK_RETURN 미관측**
  - GPUI key event/keybinding/submit **미도달**
- new-window/corrupt Enter는 `WM_KEYDOWN vk=13` + `SubmitAddress` **PASS**
- IME: layout `1033` (en-US), `ime_context=true`, `ime_open=false`,
  `ime_conversion=1`, composition/marked 없음
- titlebar 5회: full PASS **2/5**, runner cancel 3회, crash **0/5**
- 동일 코드 3회: **13/20**, **13/23**, **13/25** FAIL/UNVERIFIED — 0 FAIL 미달

## 1. 0.81 crash 수정 / exit harness 회귀

- `start_status_poll` 없음, status는 Render에서 100ms throttle 기록
- `cx.defer` auto-capture 유지
- panic hook / lifecycle log 유지
- 0.82 titlebar 5회에서 `0xC0000409` **0회**
- exit classifier: `0` PASS, non-zero FAIL, `-1073740791`/`0xC0000409` FAIL,
  timeout/cancel/invalid handle UNVERIFIED — **7/7 PASS**

## 2. Restore-first Enter trace

`enter-precondition-restore-first` (3회 모두):

```text
force_foreground=True foreground=window
external_focus=window external_class=Zed::Window external_is_window=True
window_active=True address_focused=True
ime_context=True ime_open=False ime_conversion=1 layout=1033
```

그런데:

```text
enter-window-keydown-restore-first: no WM_KEYDOWN VK_RETURN
enter-ime-messages-restore-first: ime_count=0 keyup_count=0
enter-keybinding-restore-first: no GPUI keystroke
submit-callback-restore-first: FAIL
```

- `SendKeys`, retry, raw `keybd_event` 모두 앱 HWND에 keydown을 만들지 못함
- 같은 시점 new-window/corrupt fallback은 `WM_KEYDOWN vk=13`과
  `action=amare::SubmitAddress`가 정상
- Enter가 Windows 입력 큐에서 앱 스레드로 전달되지 않는 지점까지 확인
- 제품 keybinding/handler 문제가 아니라 **그 이전 단계**
- 원인 **UNRESOLVED** (입력 주입/세션/IME 환경 가능성)

금지 사항 준수:
- explicit Enter interceptor 없음
- `on_key_event` 우회 없음
- synthetic Enter 제품 코드 없음
- 강제 callback 없음
- 기존 `SubmitAddress` keybinding 유지

## 3. IME 상태

| 항목 | 값 |
|---|---|
| keyboard layout | `1033` (en-US) |
| IME context | true |
| IME open | false |
| IME conversion | 1 |
| AddressInput marked range | empty |
| WM_IME_START/COMPOSITION/END | restore Enter 구간 0 |
| WM_IME_SETCONTEXT | new-window/corrupt 9회 |

- IME composition이 Enter를 소비했다는 증거는 없음
- restore 실패 시 IME 메시지 자체가 없음
- 실제 Windows 11 한국어 IME 조합 시나리오는 **UNVERIFIED**

## 4. Focus ownership / WebView2

- Win32: `GetForegroundWindow`, `GetGUIThreadInfo`, focus HWND/class/PID
- GPUI: `window_active`, `address_focused`, selection, marked range
- restore precondition에서 foreground와 focus가 모두 `Zed::Window`로 확인됨
- 일부 run에서 `Chrome_WidgetWin_1` focus steal은 계속 관측
- `SetFocus(parent)` 재확인은 유지되지만, Enter 미전달 구간에서는
  focus가 이미 window로 확인되어 focus steal만으로 설명되지 않음

## 5. SendKeys / paste 비교

| run | SendKeys | key_delta | paste |
|---|---:|---:|---:|
| 44140459564 | 55자 | 54 | **74자 PASS** |
| 44140476371 | 67자 | 67 | **74자 PASS** |
| 44140491440 | 70자 | 70 | **74자 PASS** |

- `key_count_delta == actual_len` → 문자 손실은 GPUI 도달 전
- **TEST_AUTOMATION_BUG**으로 분류
- paste 성공을 keyboard 입력 성공으로 간주하지 않음
- 최초 SendKeys 실패는 `input-mismatch-attempt1`로 보존

## 6. 동일 코드 3회 (`a8b1939`)

| run | Aggregate | password | vault | tab | library | download | settings | session | corrupt | navigation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 44140459564 | **13/20** | 2/1 | 1/1 | 1/1 | 5/1 | 1/1 | 20/1/1 | 2/1 | 14/0 | 52/7/19 |
| 44140476371 | **13/23** | 2/1 | 1/1 | 1/1 | 4/1 | 1/1 | 20/1/1 | 2/1 | 14/0 | 49/7/22 |
| 44140491440 | **13/25** | 2/1 | 1/1 | 1/1 | 4/1 | 1/1 | 32/1/1 | 2/1 | 14/0 | 47/7/24 |

**3회 연속 0 FAIL 미달.**

## 7. Titlebar / multi-window

| run | stage | exit | 판정 |
|---|---|---|---|
| 44140410578 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |
| 44140423461 | 29/0/0 | cancel | ENVIRONMENT_LIMITATION |
| 44140440745 | 33/0/0 | 0 | **PASS** |
| 44140544330 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |
| 44140559072 | 33/0/0 | 0 | **PASS** |

- full PASS **2/5**
- crash `0xC0000409` **0/5**
- runner cancellation 3회, stage FAIL 없음

## 8. 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.82.0-win-x64.zip` | 126,906,487 bytes | `f5f76b713d14c6ae62809c74a15a5b7eb3883587deceb9c6906d364ed7df5fe4` |
| `AMARE-0.82.0-win-x64-unsigned.msix` | 126,841,822 bytes | `2b7795b943684ce77e0d56974614a3f407b261b4ad71d4227bd110eba3fa0df0` |
| `AMARE-0.82.0-gpui-win-x64.zip` | 5,958,776 bytes | `9fc893c55fee32b76b43acd8c19c9bbeae9928ec2242fab8b986286d535d0c3c` |

- WinUI manifest `git_commit=a8b1939`, **UNSIGNED**
- MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. 0.81 crash 수정과 exit classifier는
회귀 없이 유지됐고, restore Enter가 앱 HWND에 도달하지 않는 단계까지
좁혔습니다. 그러나 원인 확정·수정, restore submit, 3회 연속 0 FAIL,
실제 Windows 11/IME 검증이 남아 **GPUI 기본 전환은 보류**합니다.
