# AMARE 0.89.0 — Native Mouse Release Forensics & Interaction Reliability

0.89.0은 WM_LBUTTONUP 미수신 원인을 입력 생성 방식에서 분리하고, SendInput
기반 first-click과 multi-window 입력/종료를 검증한 버전입니다.
**GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- 0.81 `0xC0000409` 수정 유지: titlebar **crash 0/5**, exit classifier **7/7**
- **SendInput으로 first-click 6/6 PASS**
  - New Tab, Settings, Password, Downloads, Bookmarks, Address
  - `sendinput=2`, `WM_LBUTTONDOWN=1`, `WM_LBUTTONUP=1`
- Address: selection `0,11 → 11,11` 실제 변경 확인
- Ctrl+L/T/W/N **PASS**, Ctrl+R **PASS 1회 / UNVERIFIED 1회**
- Multi-window 독립 입력 `alpha`/`beta` **PASS**
- B 종료: `a_valid=True`, `b_valid=False` **PASS**
- 동일 코드 3회: **13/11**, **11/10**, **11/12** FAIL/UNVERIFIED
- Titlebar 5회: full PASS **3/5**, runner cancel 2회, crash **0/5**

## 1. Mouse DOWN/UP 원인

0.88의 `mouse_event` 경로에서 Settings/Downloads/Bookmarks의 UP이 누락됐습니다.

0.89 비교:

| 방식 | down/up | 반환 | 결과 |
|---|---|---|---|
| `mouse_event` (0.88) | 1/0 (일부) | void | UP 미수신 |
| `SendInput` (0.89) | 1/1 | **2** | **PASS** |

- `SendInput`은 2개 이벤트를 실제로 삽입했고 모두 HWND에 도달
- UP 누락은 제품 callback 이전 **입력 생성/전달 방식** 문제
- PostMessage로 UP만 주입하지 않음
- 제품 mouse 처리 코드 변경 없음

## 2. Control별 first-click

| control | bounds | down/up | 상태 변화 | 결과 |
|---|---|---|---|---|
| New Tab | 786,7,30,30 | 1/1 | tab 증가 | **PASS** |
| Settings | 948,48,30,30 | 1/1 | none→settings | **PASS** |
| Password | 88,48,30,30 | 1/1 | settings→passwords | **PASS** |
| Downloads | 796,48,30,30 | 1/1 | passwords→downloads | **PASS** |
| Bookmarks | 872,48,30,30 | 1/1 | downloads→bookmarks | **PASS** |
| Address | 126,48,662,30 | 1/1 | selection 0,11→11,11 | **PASS** |

- correlation ID로 native message와 제품 상태를 연결
- 클릭 생성만으로 PASS하지 않음

## 3. Address 실제 클릭

- 클릭 전 selection `0,11`
- 클릭 후 selection `11,11`
- mouse down/up 모두 수신
- focus 유지와 별개로 caret/selection 변경 확인
- **PASS**

## 4. Ctrl+R reload

- local test-page fixture 사용
- PASS run: `navigation_started 1→2`, `navigation_completed 1→2`,
  `key_action=amare::ReloadPage`
- UNVERIFIED run: `navigation_started 0→0`, `source` empty
- callback/keybinding과 실제 navigation lifecycle을 분리
- **변동성 UNRESOLVED**

## 5. Multi-window

| 항목 | 결과 |
|---|---|
| A/B/A focus | **PASS** |
| A 입력 `alpha` | **PASS** |
| B 입력 `beta` | **PASS** (성공 run) |
| A after B | `alpha` 유지 **PASS** |
| B 종료 | `a_valid=True`, `b_valid=False` **PASS** |

- status 개수가 아니라 실제 `IsWindow`로 종료 확인
- 일부 run에서 B `bet` 손실 → **UNVERIFIED**

## 6. Shortcut regression

| 단축키 | 결과 |
|---|---|
| Ctrl+L | **PASS** |
| Ctrl+T | **PASS** |
| Ctrl+W | **PASS** |
| Ctrl+N | **PASS** |
| Ctrl+R | **PASS/UNVERIFIED 변동** |

## 7. Focus recovery / 안정성

- 0.85 조건부 1회 GotFocus recovery 유지
- 0.86 input path 결과 유지
- `start_status_poll` 없음, Render 100ms status, `cx.defer` 유지
- panic hook/lifecycle log 유지
- exit classifier **7/7**, crash **0/5**
- Corrupt **14/14** 3회, Session **2/1** 3회

## 8. 동일 코드 3회 (`0bed421`)

| run | Aggregate | navigation |
|---|---:|---:|
| 44335491907 | **13/11** | 71/7/10 |
| 44335507203 | **11/10** | 71/6/9 |
| 44335522715 | **11/12** | 72/7/11 |

**3회 연속 0 FAIL 미달.**

## 9. Titlebar

| run | stage | exit | 판정 |
|---|---|---|---|
| 44335540395 | 32/0/1 | 0 | **PASS** |
| 44335555330 | 32/0/1 | 0 | **PASS** |
| 44335570162 | 32/0/1 | 0 | **PASS** |
| 44335586565 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |
| 44335601495 | 29/0/0 | cancel | ENVIRONMENT_LIMITATION |

- full PASS **3/5**
- crash **0/5**

## 10. WinUI / 패키지

- WinUI `44335618176`: UI **48/48**, **143/0/9/1**, interop **70/70**
- Linux **SUCCESS**

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.89.0-win-x64.zip` | 126,906,525 bytes | `128dc0a45705c078489bba275320ffaa2c83c059cb965aa35399df46784e91e1` |
| `AMARE-0.89.0-win-x64-unsigned.msix` | 126,841,818 bytes | `fdb93dbaf68594dc243646006875e7ff27098abc67eb094b9611b77cfa6145ae` |
| `AMARE-0.89.0-gpui-win-x64.zip` | 5,973,528 bytes | `19ef7f23e1d19995be35025428773c8bd52fe9c2dae28c2c37e7f48fae24e23a` |

- manifest `git_commit=0bed421`, **UNSIGNED**
- MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. SendInput으로 mouse UP 누락을 해소하고
first-click 6/6, multi-window 입력/종료를 검증했습니다. Ctrl+R 변동,
multi-window 일부 run, 3회 연속 0 FAIL, 실제 Windows 11/IME 검증이 남아
**GPUI 기본 전환은 보류**합니다.
