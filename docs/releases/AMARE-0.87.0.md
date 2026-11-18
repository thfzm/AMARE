# AMARE 0.87.0 — First-Click Attribution & Multi-Window Input Verification

0.87.0은 first-click native input 계측을 추가하고, Ctrl+L/T/N/W/R의 실제 실행
결과와 두 창 focus 독립성을 검증한 버전입니다.
**GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- 0.81 `0xC0000409` 수정 유지: titlebar **crash 0/5**, exit classifier **7/7**
- Mouse trace 추가 (`WM_NCHITTEST`, `WM_MOUSEMOVE`, `WM_LBUTTONDOWN/UP`, 좌표)
- **Shortcut matrix 실제 효과 검증 PASS**:
  - Ctrl+L: 주소창 focus/selection
  - Ctrl+T: 탭 1→2
  - Ctrl+R: navigation_started 1→2
  - Ctrl+W: 탭 2→1
  - Ctrl+N: window 1→2, 새 HWND
- Multi-window A→B→A focus **PASS**
- First-click attribution은 toolbar bounds 미노출로 **UNVERIFIED**
- 동일 코드 3회: **12/16**, **12/11**, **14/16** FAIL/UNVERIFIED
- Titlebar 5회: full PASS **1/5**, runner cancel 4회, crash **0/5**

## 1. First-click attribution

추가한 native mouse trace:

- `WM_NCHITTEST`
- `WM_MOUSEMOVE`
- `WM_LBUTTONDOWN`
- `WM_LBUTTONUP`
- client x/y, foreground/focus class

Probe 결과:

```text
click-new-tab    bounds missing
click-settings   bounds missing
click-password   bounds missing
```

- `layout_bounds.toolbar_children`에 new_tab/settings/password bounds가 없음
- mouse event를 생성했다고 PASS 처리하지 않음
- **UNVERIFIED**
- 이벤트 단계 분류(`INPUT_NOT_GENERATED` 등)는 계측 구조만 추가, 전체 suite 적용 미완료

## 2. Shortcut matrix

| 단축키 | 실제 검증 | 결과 |
|---|---|---|
| Ctrl+L | address_focused, selected 11,11 | **PASS** |
| Ctrl+T | tabs 1→2, active 변경 | **PASS** |
| Ctrl+R | navigation_started 1→2 | **PASS** |
| Ctrl+W | tabs 2→1 | **PASS** |
| Ctrl+N | windows 1→2, HWND 2개 | **PASS** |

- callback 호출만으로 PASS 처리하지 않고 실제 상태 변화를 확인
- 기존 shortcut router 변경 없음
- 다른 조건(페이지 focus, NavigationCompleted 직후, restore 직후 등)의
  전체 matrix는 **UNVERIFIED**

## 3. Multi-window focus independence

| 항목 | 결과 |
|---|---|
| A 주소창 focus | **PASS** |
| B 주소창 focus | **PASS** |
| A 복귀 focus | **PASS** |
| A/B/A focus class | `Zed::Window` |
| A/B 독립 문자열 입력 | **UNVERIFIED** (A `about:blankalpha`, B `bet`) |
| B 종료 후 A 유지 | **UNVERIFIED** (status 2개 유지) |

- focus 전환 자체는 PASS
- 입력 문자열/persistence/종료는 자동화 precondition 미완료로 **UNVERIFIED**
- 한 창 상태를 다른 창 상태로 추정하지 않음

## 4. 0.85 focus recovery 회귀

- 0.85 조건부 1회 SetFocus 복구 유지
- shortcut/multi-window probe의 focus class `Zed::Window`
- 반복 SetFocus/sleep 추가 없음
- **회귀 없음**

## 5. Input path regression

0.86 결과(동일 문자열):

| 경로 | 결과 |
|---|---|
| SendKeys | 2/3 |
| Scan-code | 3/3 |
| Unicode | 1/3 |
| Paste | 3/3 |

- 0.87은 input path 코드를 변경하지 않음
- 0.87 재실행은 **UNVERIFIED**, 0.86 결과를 회귀 기준으로 유지

## 6. WinUI UIA / 네트워크 분리

- 0.87 `44302023665`: UI **48/48**, **143/0/9/1**, interop **70/70**
- 0.86 1차 48/17은 전부 `Example Domain` navigation timeout으로 확인
- 로컬 fixture 전환은 미완료
- 0.83 48/49 selector 원인 **UNRESOLVED**

## 7. 동일 코드 3회 (`c9d523c`)

| run | Aggregate | navigation |
|---|---:|---:|
| 44301918565 | **12/16** | 69/6/15 |
| 44301931689 | **12/11** | 71/7/10 |
| 44301944895 | **14/16** | 69/8/15 |

**3회 연속 0 FAIL 미달.**

## 8. Titlebar / crash

| run | stage | exit | 판정 |
|---|---|---|---|
| 44301957736 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |
| 44301970488 | 32/0/1 | 0 | **PASS** |
| 44301984866 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |
| 44301997038 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |
| 44302011183 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |

- full PASS **1/5**
- `0xC0000409` **0/5**
- exit classifier **7/7 PASS**

## 9. Session / Corrupt

- Session: **2/1** 3회
- Corrupt matrix: **14/14** 3회
- Navigation: 69/6/15, 71/7/10, 69/8/15

## 10. 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.87.0-win-x64.zip` | 126,906,468 bytes | `77d41ee85cebaa6c344c6d52eea740b476369b3ab076d8016a0bfe87ff2a9a8e` |
| `AMARE-0.87.0-win-x64-unsigned.msix` | 126,841,793 bytes | `0645254d77374f7cf857a7299f16c386cd847c1b7e68f4066a396f0b32b7e3cd` |
| `AMARE-0.87.0-gpui-win-x64.zip` | 5,971,865 bytes | `c38cfa70656a6a69a0af3b412284a84c17d5da9406db6e2fd1fe00b032323d5e` |

- manifest `git_commit=c9d523c`, **UNSIGNED**
- MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. Ctrl+L/T/N/W/R의 실제 실행 결과를
검증했고 multi-window focus 전환도 PASS했습니다. 그러나 first-click
attribution bounds 미노출, multi-window 입력/종료, 3회 연속 0 FAIL,
실제 Windows 11/IME 검증이 남아 **GPUI 기본 전환은 보류**합니다.
