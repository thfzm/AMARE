# AMARE 0.83.0 — GPUI Input Delivery Forensics & Regression Isolation

0.83.0은 restore-first Enter가 사라지는 위치를 격리된 probe로 추적한 버전입니다.
**GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- 0.81 `0xC0000409` 수정 유지: titlebar **crash 0/5**
- Exit classifier **7/7 PASS**
- **Enter 자체는 정상**: 주소 텍스트가 실제 입력된 경우 normal/restore 모두
  `WM_KEYDOWN`(getmsg) + LL hook + GPUI keystroke + `SubmitAddress` + submit **PASS**
- **최초 손실 지점**: navigation 이후 **주소창 focus ownership**
  - external focus `Chrome_WidgetWin_1` (WebView2)
  - GPUI `address_focused=true`는 오래된 값
  - 이 상태에서 Ctrl+L/주소 click/typing이 AddressInput에 도달하지 않음
- Enter 수정/우회 코드 추가 없음
- Titlebar 5회: full PASS **2/5**, runner cancel 3회, crash **0/5**
- 동일 코드 3회: **12/20**, **13/20**, **13/19** FAIL/UNVERIFIED — 0 FAIL 미달

## 1. 격리 probe

동일 앱에서 normal 5회 + restore 5회, `about:blank`만 사용해 외부 네트워크를
배제했습니다.

성공한 round:

```text
address clear=0
text=11/11 (key_delta=11)
foreground=window, external focus=Zed::Window
callwnd=False, getmsg=True, ll=True, ll_injected=True
GPUI keystroke=enter action=amare::SubmitAddress
submit_delta=1
```

실패한 round:

```text
address clear=11 (변화 없음)
text key_delta=0
status_age_ms=15,000~31,000 (stale)
external focus=Chrome_WidgetWin_1
getmsg=False, ll=False
GPUI keystroke/submit 없음
```

- `status_age`가 오래된 것은 0.83의 Render-only status 특성상 이벤트가 없으면
  갱신되지 않기 때문
- process `Responding=True`
- 실패는 Enter 이전에 주소창 focus를 되찾지 못한 상태
- WebView2가 focus를 소유한 뒤 **Ctrl+L accelerator/Ctrl+A/typing이 GPUI에
  도달하지 않음**

## 2. Enter keybinding / submit

- 텍스트가 정상 입력된 모든 probe round에서 Enter → `SubmitAddress` **PASS**
- `SubmitAddress` handler/keybinding 변경 없음
- explicit interceptor/synthetic Enter/강제 callback 없음
- restore-first Enter 실패는 **keybinding 이전의 focus/입력 단계**로 분류

## 3. Focus ownership

- Win32 `GetForegroundWindow`, `GetGUIThreadInfo`, LL keyboard hook 추가
- GPUI `window_active`, `address_focused`, focus history 유지
- 실패 구간 external focus는 `Chrome_WidgetWin_1`
- GPUI `address_focused=true`는 마지막 render 시점 값으로 실제 OS focus와 불일치
- **WebView2 focus steal이 최초 손실 지점**으로 확인됨
- 주소창 클릭/Ctrl+L로 focus를 회복하지 못한 원인은 **UNRESOLVED**
  (자동화 입력 vs WebView2 accelerator 경합)

## 4. SendKeys / paste

- SendKeys 손실은 계속 재현
- paste는 별도 fallback이며 keyboard 성공으로 집계하지 않음
- 첫 실패는 `input-mismatch-attempt1`로 보존
- probe에서는 텍스트 미입력 시 Enter를 판정하지 않고
  `enter-skipped / TEST_AUTOMATION_BUG`로 분리

## 5. 동일 코드 3회 (`2d8bda6`)

| run | Aggregate | password | vault | tab | library | download | settings | session | corrupt | navigation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 44172759302 | **12/20** | 2/1 | 1/1 | 1/1 | 4/1 | 1/1 | 33/0/1 | 2/1 | 14/0 | 52/7/19 |
| 44172771903 | **13/20** | 2/1 | 1/1 | 1/1 | 5/1 | 1/1 | 20/1/1 | 2/1 | 14/0 | 59/7/19 |
| 44172786665 | **13/19** | 2/1 | 1/1 | 1/1 | 4/1 | 1/1 | 32/1/1 | 2/1 | 14/0 | 51/7/18 |

**3회 연속 0 FAIL 미달.**

## 6. Titlebar / crash

| run | stage | exit | 판정 |
|---|---|---|---|
| 44172803708 | 29/0/0 | cancel | ENVIRONMENT_LIMITATION |
| 44172816205 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |
| 44172830891 | 33/0/0 | 0 | **PASS** |
| 44172843324 | 33/0/0 | 0 | **PASS** |
| 44172858433 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |

- full PASS **2/5**
- `0xC0000409` **0/5**
- exit classifier **7/7 PASS**

## 7. WinUI 기준선

- 1차 `44172872191`: UI 48/49, **142/0/10/1** (UIA 1건)
- 재실행 `44178954839`: **48/48**, **143/0/9/1**, interop **70/70**
- Linux `44170498340`: **SUCCESS**
- 최초 실패와 재실행 성공을 모두 기록

## 8. 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.83.0-win-x64.zip` | 126,906,460 bytes | `1fb82b8498f63955bdf6fdc061c48a127cbd05e2e8432ccef09830a8ca8ffef1` |
| `AMARE-0.83.0-win-x64-unsigned.msix` | 126,841,808 bytes | `4e7f91661d91716f3033fa99f5d63464f60f10a81f8f86307d27c8d58d06a8e2` |
| `AMARE-0.83.0-gpui-win-x64.zip` | 5,960,754 bytes | `117f5d6322dc4a0bd7d6a250978ad93ff0bc96ddab7c137c1a92ce7a87fa04eb` |

- manifest `git_commit=2d8bda6`, **UNSIGNED**
- MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. Enter 자체와 submit keybinding이 정상임을
격리 probe로 확인했고, 최초 손실 지점을 **navigation 이후 WebView2 focus
ownership**으로 좁혔습니다. 그러나 focus 회복 원인, restore submit, 3회 연속
0 FAIL, 실제 Windows 11/IME 검증이 남아 **GPUI 기본 전환은 보류**합니다.
