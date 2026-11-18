# AMARE 0.88.0 — GPUI Layout Introspection & First-Click Verification

0.88.0은 toolbar 컨트롤의 실제 bounds를 status에 노출하고, first-click을
native mouse message부터 제품 상태 변경까지 검증한 버전입니다.
**GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- 0.81 `0xC0000409` 수정 유지: titlebar **crash 0/5**, exit classifier **7/7**
- **Stable toolbar bounds 구현**
  - `toolbar.new_tab`, `toolbar.settings`, `toolbar.password`,
    `toolbar.downloads`, `toolbar.bookmarks`, `toolbar.address`
- First-click attribution:
  - New Tab **PASS**
  - Password **PASS**
  - Address **PASS**
  - Settings / Downloads / Bookmarks **UNVERIFIED**
    - `WM_LBUTTONDOWN=1`, `WM_LBUTTONUP=0`, panel 변화 없음
- Ctrl+L/T/W/N 실제 효과 **PASS**, Ctrl+R **UNVERIFIED**
- Multi-window A/B/A focus **PASS**, 독립 입력/종료 **UNVERIFIED**
- 동일 코드 3회: **11/11**, **12/14**, **12/13** FAIL/UNVERIFIED
- Titlebar 5회: full PASS **2/5**, runner cancel 3회, crash **0/5**

## 1. Layout bounds

추가한 stable control identity와 실제 bounds:

| control_id | bounds (x,y,w,h) |
|---|---|
| `toolbar.new_tab` | 786,7,30,30 |
| `toolbar.settings` | 948,48,30,30 |
| `toolbar.password` | 88,48,30,30 |
| `toolbar.downloads` | 796,48,30,30 |
| `toolbar.bookmarks` | 872,48,30,30 |
| `toolbar.address` | 126,48,662,30 |

- 두 번째 toolbar row에 `on_children_prepainted`를 추가해 실제 GPUI bounds 수집
- window-local 좌표, scale_factor 1.0
- 고정 좌표/추정 좌표 사용 없음
- 기존 UI 배치/스타일 변경 없음

## 2. First-click attribution

| control | native down/up | mouse_delta | panel 변화 | 결과 |
|---|---|---:|---|---|
| New Tab | 1/1 | 1 | tab 증가 | **PASS** |
| Settings | 1/0 | 1 | none→none | **UNVERIFIED** |
| Password | 1/1 | 1 | settings→passwords | **PASS** |
| Downloads | 1/0 | 1 | passwords→passwords | **UNVERIFIED** |
| Bookmarks | 1/0 | 1 | downloads→downloads | **UNVERIFIED** |
| Address | 1/0 | 1 | focus 유지 | **PASS** |

- Settings/Downloads/Bookmarks는 **WM_LBUTTONDOWN은 도달, WM_LBUTTONUP 미수신**
- 패널 상태 변화 없음
- 분류 후보: `NATIVE_MESSAGE_NOT_RECEIVED` / `TEST_AUTOMATION_BUG` / **UNRESOLVED**
- 클릭 생성만으로 PASS 처리하지 않음
- 최초 실패 보존

## 3. Shortcut regression

| 단축키 | 실제 검증 | 결과 |
|---|---|---|
| Ctrl+L | address_focused, selected | **PASS** |
| Ctrl+T | tabs 1→3 (기존 tab 포함) | **PASS** |
| Ctrl+R | navigation_started 증가 없음 | **UNVERIFIED** |
| Ctrl+W | tabs 3→2 | **PASS** |
| Ctrl+N | windows 1→2, 새 HWND | **PASS** |

- Ctrl+R은 0.87 PASS, 0.88 UNVERIFIED로 변동
- shortcut router 변경 없음

## 4. Multi-window focus / input

| 항목 | 결과 |
|---|---|
| A focus | **PASS** |
| B focus | **PASS** |
| A 복귀 | **PASS** |
| A 입력 | `alph` (1자 손실) |
| B 입력 | `beta` |
| A after B | `alpha` (stale) |
| 독립 입력 | **UNVERIFIED** |
| B 종료 후 A 유지 | **UNVERIFIED** (status 2개) |

- focus 전환은 PASS
- 입력/종료는 자동화 precondition 미완료

## 5. 0.85 focus recovery / 0.86 input path

- 0.85 조건부 1회 GotFocus recovery 유지
- probe focus class `Zed::Window`
- 0.86 input path 결과 유지: scan-code 3/3, paste 3/3, SendKeys 2/3, Unicode 1/3
- 0.88 재실행은 **UNVERIFIED**

## 6. WinUI UIA / 네트워크

- 0.88 `44318872178`: UI **48/48**, **143/0/9/1**, interop **70/70**
- Linux `44318751962`? (별도 CI): **SUCCESS**
- 0.86 Example Domain timeout은 네트워크로 분류
- 0.83 48/49 selector **UNRESOLVED**
- 로컬 fixture 전환 미완료

## 7. 동일 코드 3회 (`91d7e07`)

| run | Aggregate | navigation |
|---|---:|---:|
| 44318751962 | **11/11** | 72/6/10 |
| 44318765923 | **12/14** | 61/7/13 |
| 44318779373 | **12/13** | 74/6/12 |

**3회 연속 0 FAIL 미달.**

## 8. Titlebar / crash

| run | stage | exit | 판정 |
|---|---|---|---|
| 44318794089 | 29/0/0 | cancel | ENVIRONMENT_LIMITATION |
| 44318808271 | 29/0/0 | cancel | ENVIRONMENT_LIMITATION |
| 44318823686 | 32/0/1 | 0 | **PASS** |
| 44318838420 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |
| 44318856833 | 33/0/0 | 0 | **PASS** |

- full PASS **2/5**
- `0xC0000409` **0/5**
- exit classifier **7/7 PASS**

## 9. Session / Corrupt

- Session: **2/1** 3회
- Corrupt matrix: **14/14** 3회
- Navigation: 72/6/10, 61/7/13, 74/6/12

## 10. 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.88.0-win-x64.zip` | 126,906,477 bytes | `0f34f12933c0dfbc6031e3f6d047a6acf568420c835c0e779a47dcebc31046d5` |
| `AMARE-0.88.0-win-x64-unsigned.msix` | 126,841,789 bytes | `87989f70df07d04f30ade4997c89ad6331db2d1d4815c573945e6de94ea5e285` |
| `AMARE-0.88.0-gpui-win-x64.zip` | 5,974,071 bytes | `2d8f68e3c1544786332715ce2bb7993aa10ae967aed398027101718af3b0f1af` |

- manifest `git_commit=91d7e07`, **UNSIGNED**
- MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. 실제 toolbar bounds와 first-click
native 단계를 연결했고 New Tab/Password/Address는 PASS했습니다. Settings/
Downloads/Bookmarks의 mouse up 미수신, multi-window 입력/종료, 3회 연속
0 FAIL, 실제 Windows 11/IME 검증이 남아 **GPUI 기본 전환은 보류**합니다.
