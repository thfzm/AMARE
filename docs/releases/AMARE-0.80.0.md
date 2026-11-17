# AMARE 0.80.0 — Restore Submit Root Cause & GPUI Window Activation Trace

0.80.0은 restore submit 이벤트와 Win32/GPUI activation을 추적한 버전입니다.
**GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- 0.79 explicit Enter interceptor 잔존 없음 확인
- 40ms submit dedupe 제거 (restore submit에 도움이 된다는 증거 없음)
- restore text input: run1 **30자**, run2/3 **0자**
- restore submit callback: run1 **FAIL**, run2/3는 text 입력 실패로 submit 미실행
- corrupt-resave: submit **1 PASS / 2 FAIL**
- Titlebar Win32 activation: **foreground HWND가 실제로 window2로 변경** (도달한 4회 모두)
- GPUI `window_active=true`: **3/4 PASS**, 1회 UNVERIFIED
- Titlebar 5회: **full PASS 2/5** — 2회 runner cancel, 1회 app crash
- Corrupt **14/14 — 3회 모두**
- GPUI 3회: **10/10**, **10/7**, **10/8** — 3회 연속 0 FAIL 미달

## 0.79 변경 영향

- explicit Enter interceptor: 최종 코드에 없음 (`5e0ea65`는 crash 증거로만 보존)
- dedupe: 실제 restore submit을 돕지 않았고 정상 연속 입력을 차단할 수 있어 제거
- `submit_address`는 기존 `SubmitAddress` keybinding 단일 경로로 복귀
- restore attach 후 AddressInput 재focus 유지

## Restore-first trace

| run | input delivery | text input | submit callback | navigation |
|---|---|---|---|---|
| 44035390439 | PASS focused=True foreground=True | **PASS 30자** | **FAIL** | 미도달 |
| 44035408395 | PASS focused=True foreground=False | **FAIL 0자** selected=47,47 | 미실행 | 미도달 |
| 44035419170 | PASS focused=True foreground=True | **FAIL 0자** selected=47,47 | 미실행 | 미도달 |

- run1: 문자는 AddressInput에 도달했지만 Enter가 root keydown에서 관측되지 않았고
  submit callback도 실행되지 않음 → **Enter dispatch/keybinding 단계 실패**
- run2/3: `address_focused=True`인데 문자 이벤트가 0자. selection 47,47이 그대로
  유지되어 키보드 포커스가 AddressInput에 실제로 있지 않았음을 시사
  → **focus ownership 단계 실패**
- 두 실패 지점을 분리해 기록했으며 원인은 **UNRESOLVED**

## Titlebar Win32/GPUI activation

```text
show=True setForeground=True
beforeFg=window1 afterFg=window2
afterActive=0 afterFocus=0
```

- `SetForegroundWindow` 반환 true, `GetForegroundWindow`가 실제 window2로 변경
- `SetActiveWindow`/`SetFocus` 반환 0은 cross-thread 호출의 정상 결과이며
  성공 판정에 사용하지 않음
- GPUI `window_active=true` 3/4 확인
- 0.79의 "window2 activation 실패"는 0.80에서 재현되지 않음
- 남은 실패는 activation이 아니라 **runner cancel 2회**와
  **window1 minimize 중 app crash 1회**

| run | Win32 activation | GPUI active | 이후 단계 | 분류 |
|---|---|---|---|---|
| 44035430362 | PASS fg=window2 | PASS true | window2 geometry 후 cancel | ENVIRONMENT_LIMITATION |
| 44035438385 | PASS fg=window2 | UNVERIFIED false | window2 geometry 후 cancel | ENVIRONMENT_LIMITATION |
| 44035446666 | PASS fg=window2 | PASS true | exit=0, webview2 cleanup | **PASS** |
| 44035455951 | PASS fg=window2 | PASS true | exit=0, webview2 cleanup | **PASS** |
| 44035465816 | window2 미도달 | 미도달 | exit `0xC0000409`, window2 handle invalid | **PRODUCT_BUG/UNRESOLVED** |

- 추가 harness 결함: exit code `-1073740791`을 `titlebar-multiwindow-exit` PASS로
  기록 → **TEST_AUTOMATION_BUG** (0.80 검증에는 미수정, 0.81 과제)

## 3회 완전 반복 (`1a23e2e`)

| run | Aggregate | corrupt | session | vault | navigation |
|---|---:|---:|---:|---:|---:|
| 44035390439 | **10/10** | 14/0/0 | 2/1/0 | 13/1/0 | 15/5/5 |
| 44035408395 | **10/7** | 14/0/0 | 23/1/0 | 6/1/0 | 18/5/6 |
| 44035419170 | **10/8** | 14/0/0 | 1/1/1 | 16/1/0 | 18/5/6 |

## 개별 영역

- Password 3/1, 9/1, 9/1
- Vault 13/1, 6/1, 16/1
- Tab 16/1/4, 6/1/0, 6/1/0
- Library **20/0/0**, 17/1, 17/1
- Settings 25/1/1, **33/0/1**, **33/0/1**

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.80.0-win-x64.zip` | 126,906,478 bytes | `09bf02cdd4fa98b7fe770c90b501d5e488ae90d51bb404b2812ea4f2182652bb` |
| `AMARE-0.80.0-win-x64-unsigned.msix` | 126,841,809 bytes | `2cd0ad72371fe7e657cf87696af2bab8bee648be0b6db38e434845a79932cf32` |
| `AMARE-0.80.0-gpui-win-x64.zip` | 5,960,627 bytes | `930e1badaa133f5ea438604e4e914464dbe0ac74f0a1a1c4a693f70955b1d600` |

- 내부 검증: WinUI ZIP/MSIX `AMARE.Windows.exe`·`AppxManifest.xml` 포함,
  GPUI ZIP `amare-gpui.exe`·`VERSION`=0.80.0 포함
- **UNSIGNED**, MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. Titlebar는 Win32 foreground 전환과 GPUI
active state를 증거로 확인했지만, restore submit 미해결, full titlebar 2/5,
3회 연속 0 FAIL 미달, app crash `0xC0000409` 잔존으로 **GPUI 기본 전환은
보류**합니다.
