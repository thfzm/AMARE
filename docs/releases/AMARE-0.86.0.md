# AMARE 0.86.0 — Input Delivery Forensics & First-Click Reliability

0.86.0은 동일 문자열을 네 가지 입력 경로로 비교해 **문자 손실이 제품이 아니라
입력 생성/전달 단계**임을 분리한 버전입니다. 0.85의 조건부 1회 native focus
복구는 유지했습니다. **GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지(재실행): UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- 0.81 `0xC0000409` 수정 유지: titlebar **crash 0/5**, exit classifier **7/7**
- 0.85 focus recovery 회귀 없음: input path probe 전 trial focus `Zed::Window`
- 입력 경로 비교 (동일 문자열 `example`, 3회):

| 경로 | 일치 | 관측 |
|---|---:|---|
| SendKeys | **2/3** | 5자 trial: keydown 10, char 5 — 문자열 생성 단계 손실 |
| Scan-code | **3/3** | keydown 14, char 7 |
| Unicode | **1/3** | 6자 trial: keydown 12, char 6 |
| Clipboard paste | **3/3** | keydown 4, char 0 |

- 문자열이 일치한 모든 trial에서 Enter → `SubmitAddress` → submit **PASS**
- First-click suite는 기존 실패를 보존하며 완전 분리는 미완료
- 동일 코드 3회: **13/18**, **12/21**, **12/11** FAIL/UNVERIFIED
- Titlebar 5회: full PASS **4/5**, runner cancel 1회, crash **0/5**

## 1. SendKeys / scan-code / Unicode / paste

동일 앱, 동일 address bounds, `example` 7자, 각 3회.

- **SendKeys**: 5/7자 trial에서 keydown 10/char 5. 즉 GPUI 이전에 2문자
  이벤트 자체가 생성되지 않음 → **TEST_AUTOMATION_BUG**
- **Scan-code**: 3/3, 14 keydown / 7 char → **PASS**
- **Unicode**: 1/3, 6/7자 trial에서 12 keydown / 6 char → **UNVERIFIED**
- **Paste**: 3/3, Ctrl+V 4 keydown / 0 char → **PASS (별도 경로)**
- paste 성공을 keyboard 성공으로 합산하지 않음
- 모든 trial focus class `Zed::Window`

**결론**: SendKeys/Unicode 손실은 GPUI/AddressInput이 아니라 입력 생성 단계다.
Scan-code가 가장 안정적이며, 기존 keyboard 입력 제품 코드는 변경하지 않았다.

## 2. Enter / submit

- SendKeys 2/3, scan-code 3/3, Unicode 1/3, paste 3/3에서 문자열 일치
- 일치한 **모든 trial**에서 Enter → `SubmitAddress` → submit PASS
- 손실 trial은 Enter 판정을 skip하고 `UNVERIFIED`로 기록

## 3. 0.85 focus recovery 회귀

- navigation 시작 시 AddressInput focus intent 기록
- GotFocus 1회 복구 조건 유지
- input path probe 12 trial 모두 focus `Zed::Window`
- 0.85의 Ctrl+L/mouse/clear 10/10 기준을 깨는 증거 없음
- 무조건 SetFocus/반복 복구/sleep 추가 없음

## 4. First-click suite

| 영역 | run1 | run2 | run3 |
|---|---|---|---|
| Password | 2/1 | 2/1 | 2/1 |
| Vault | 1/1 | 1/1 | 2/1 |
| Tab | 1/1 | 1/1 | 1/1 |
| Library | 4/1 | 20/0 | 8/1 |
| Settings | 20/1/1 | 32/1/1 | 33/0/1 |
| Download | 1/1 | 1/1 | 1/1 |

- 최초 실패 보존
- INPUT_NOT_GENERATED / INPUT_NOT_DELIVERED / PRODUCT_STATE_NOT_CHANGED
  완전 분리는 미완료 → **UNRESOLVED**
- 제품 코드는 변경하지 않음

## 5. Ctrl+L / Ctrl+T / Ctrl+N / Ctrl+W / Ctrl+R

- 기존 입력 smoke/navigation 결과는 유지
- 5개 단축키의 accelerator/GPUI keybinding 동시 timestamp 비교는 **미완료**
- Ctrl+L은 0.85에서 focus recovery 후 정상
- 무분별한 shortcut 가로채기 없음
- **UNVERIFIED**

## 6. Multi-window focus independence

- Titlebar multi-window 5회 중 4 PASS, 1 runner cancel
- 각 창의 focus ownership 독립성 전용 matrix는 **미완료**
- 한 창의 NavigationCompleted가 다른 창 focus를 바꾸는지 **UNVERIFIED**

## 7. WinUI UIA race / 기준선

- 0.86 1차 `44287345461`: UI 48/17, 142/0/10/1
  - 실패 17건 모두 `Example Domain` navigation timeout
  - **UIA selector 실패가 아니라 외부 네트워크/환경**
- 재실행 `44292349214`: UI **48/48**, **143/0/9/1**, interop **70/70**
- 0.83의 48/49 selector 원인은 여전히 **UNRESOLVED**
- 기준선은 재실행에서 유지

## 8. 동일 코드 3회 (`7bedff1`)

| run | Aggregate | navigation |
|---|---:|---:|
| 44287246248 | **13/18** | 68/7/17 |
| 44287258633 | **12/21** | 58/7/20 |
| 44287270127 | **12/11** | 71/7/10 |

**3회 연속 0 FAIL 미달.**

## 9. Titlebar / crash

| run | stage | exit | 판정 |
|---|---|---|---|
| 44287282037 | 29/0/0 | cancel | ENVIRONMENT_LIMITATION |
| 44287294823 | 32/0/1 | 0 | **PASS** |
| 44287306366 | 32/0/1 | 0 | **PASS** |
| 44287318939 | 32/0/1 | 0 | **PASS** |
| 44287332287 | 32/0/1 | 0 | **PASS** |

- full PASS **4/5**
- `0xC0000409` **0/5**
- exit classifier **7/7 PASS**

## 10. Session / Corrupt

- Session: **2/1** 3회
- Corrupt matrix: **14/14** 3회
- Navigation: 68/7/17, 58/7/20, 71/7/10

## 11. 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.86.0-win-x64.zip` | 126,906,481 bytes | `222e6e162c605af1f59d339aae4649594bb3f9d76ddbad77e26b133558a44d68` |
| `AMARE-0.86.0-win-x64-unsigned.msix` | 126,841,787 bytes | `ca1fe005b778240b968e8e8f7d77d46dfa0a71b6eeb142f8d54e6b4c0e645b65` |
| `AMARE-0.86.0-gpui-win-x64.zip` | 5,970,349 bytes | `6dddeb22656f62ca4583a9e61023049809a13349de5c00b7c2640d13d70e7825` |

- manifest `git_commit=7bedff1`, **UNSIGNED**
- MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. 입력 손실을 자동화 계층으로 분리하고
scan-code/paste 안정성을 확인했으며 0.85 focus recovery도 유지했습니다.
그러나 first-click 완전 분리, shortcut matrix, multi-window focus matrix,
3회 연속 0 FAIL, 실제 Windows 11/IME 검증이 남아 **GPUI 기본 전환은 보류**합니다.
