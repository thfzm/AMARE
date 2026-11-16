# AMARE 0.73.0 — GPUI Automation Infrastructure & Multi-Window Stability

0.73.0은 새 브라우저 기능이 아니라 **GPUI 입력 전달/관측 신뢰도**, **navigation
회귀**, **titlebar multi-window hang**, **session/corrupt fixture 격리**를
다룬 버전입니다. **GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- Corrupt session 14-case: **14/14 PASS — 3회 모두**
- Session persistence: **29/0/0** (1회), **22/1/1** (2회)
- Vault: **29/0/0** (1회)
- Input delivery telemetry 추가: mouse/key count, foreground/active, click 좌표, navigation request
- Navigation regression suite 추가: immediate submit, rapid submit, new-window first nav, restore first nav, corrupt resave/restart
- Titlebar separate workflow: **FAIL** — window1 조작 후 window2 handle이 invalid
- GPUI 3회 완전 반복(`7de834c`): **8 FAIL / 5 UNVERIFIED**, **9/11**, **10/6**
- **3회 연속 0 FAIL 미달**

## 주요 변경

### 1. 입력 전달 관측

제품 상태에 다음을 추가했습니다.

- `mouse_down_count`, `key_down_count`
- `last_input_at_ms`, `last_click_x/y`, `last_key`
- `window_active`
- `last_action`, `navigation_requests`, `last_navigation_url`, `pending_navigation`

테스트는 클릭/키 입력 전후 counter와 foreground/active 상태를 기록해
**이벤트 전달 실패**와 **제품 상태 변경 실패**를 분리할 수 있습니다.

### 2. Navigation regression

새 suite `tools/gpui-navigation-regression.ps1`:

- new tab 생성 직후(controller attach 전) 주소 제출
- attach 직후/빠른 연속 제출
- 마지막 URL이 최종 목적지인지
- 새 창 첫 navigation
- session restore 직후 navigation
- sanitized corrupt session 재저장/재시작
- request → pending → attach → source → loading=false 증거

### 3. Restore banner 주소 좌표

session restore banner가 toolbar를 아래로 밀 때 y=70 클릭이 banner에 닿는
문제를 확인하고, 실패 재시도는 y=105를 사용하도록 보강했습니다.

### 4. Titlebar multi-window

blocking CI에서 분리해 `ci-windows-titlebar` workflow로 실행했습니다.
결과:

```text
TITLEBAR window=0 handle=...
PASS window1 maximize/restore/geometry
UNVERIFIED window1 minimize
TITLEBAR window=1 handle=...
FAIL window2-handle: window handle is no longer valid
```

window1 조작 후 window2 HWND가 사라지는 현상을 확인했습니다. 제품
multi-window lifecycle 문제인지 harness/foreground 문제인지 **UNRESOLVED**로
기록하며, window2 조작 PASS로 간주하지 않습니다.

### 5. Session/corrupt fixture

- session smoke의 중복 old corrupt fixture 제거
- corrupt JSON은 14-case matrix로 단일화
- profile DB 선택, raw backup count, resave/restart까지 검증

## 3회 완전 반복 (`7de834c`)

| run | Aggregate | corrupt | session | vault |
|---|---:|---:|---:|---:|
| [43831963871](https://github.com/thfzm/AMARE/actions/runs/43831963871) | **8 FAIL / 5 UNV** | 14/0/0 | 2/1/1 | 8/1/0 |
| [43831980338](https://github.com/thfzm/AMARE/actions/runs/43831980338) | **9 FAIL / 11 UNV** | 14/0/0 | 22/1/1 | 1/1/0 |
| [43831991718](https://github.com/thfzm/AMARE/actions/runs/43831991718) | **10 FAIL / 6 UNV** | 14/0/0 | 22/1/1 | 1/1/0 |

- session FAIL 1은 unreachable URL(`http://127.0.0.1:1/`) navigation request
  probe (test fixture)
- titlebar는 별도 workflow 결과
- 3회 연속 0 FAIL 미달

## 남은 실패

- password edit form 첫 click
- vault form field focus/submit
- tab/library 첫 navigation request
- settings 첫 screenshot/category/search
- navigation regression new-window/rapid-submit 상태
- titlebar window2 handle invalid
- 실제 Windows 11/DPI/Snap/Alt+Tab UNVERIFIED

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.73.0-win-x64.zip` | 126,906,507 bytes | `b8f9a3d3bc8ac328c65111c9638b9e4a950d9609ca21c00bb36d250b66a64bc4` |
| `AMARE-0.73.0-win-x64-unsigned.msix` | 126,841,835 bytes | `dd7918642ec4a5582092058057803622fec84312192233e66be56e5bbaff3e67` |
| `AMARE-0.73.0-gpui-win-x64.zip` | 5,944,055 bytes | `29d2a85a47bbe7c559275c986698f71e4511efab4fe2651df995910383990e7c` |

- **UNSIGNED**, MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 스크린샷

- WinUI: `artifacts/windows/screenshots/0.73.0/` — **7 PNG**
- GPUI: `gpui/` — **51 PNG**
  - New Tab, normal page, tabs, Settings, Vault, two windows,
    maximize/restore, session restore

## 최종 판정

WinUI를 기본 패키지로 유지합니다. 0.73은 입력 전달 telemetry, navigation
regression, corrupt 14/14, session persistence 개선을 제공했지만 3회 연속
0 FAIL과 titlebar window2 lifecycle 안정화에 실패했으므로 **GPUI 기본 전환은
계속 보류**합니다.
