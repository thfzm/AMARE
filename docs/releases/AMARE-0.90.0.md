# AMARE 0.90.0 — Reload Determinism & Multi-Window Lifecycle

0.90.0은 Ctrl+R reload를 local HTTP fixture에서 10회 추적하고, reload counter를
제품에 추가한 버전입니다. **GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- 0.81 `0xC0000409` 수정 유지: titlebar crash **0/5**, exit classifier **7/7**
- Reload counter 추가: `reload_count`, `last_reload_at_ms`
- local HTTP fixture 로드 **PASS**
- Ctrl+R 10회:
  - reload #1: `ReloadPage` callback **PASS**, `reload_count +1`
  - 그러나 WebView2 `navigation_started/completed`는 **변화 없음**
  - reload #2–#10: key event 미수신, `reload_delta=0`
  - **UNRESOLVED**
- A/B 독립 navigation/종료는 reload 실패로 **미도달 UNVERIFIED**
- First-click 6/6 및 multi-window 입력/종료는 0.89 결과 유지
- 동일 코드 3회: **12/12**, **13/11**, **11/10** FAIL/UNVERIFIED
- Titlebar 5회: full PASS **3/5**, startup hang 2회, crash **0/5**

## 1. Reload trace

| 단계 | 관측 |
|---|---|
| fixture HTTP | 200, 65 bytes |
| address/paste load | `http://127.0.0.1:8772/fixture-a.html` |
| reload #1 callback | `amare::ReloadPage`, `reload_count +1` |
| reload #1 NavigationStarting | **변화 없음** (2→2) |
| reload #1 NavigationCompleted | **변화 없음** (2→2) |
| reload #2–#10 | key_action 없음, reload_count 변화 없음 |

- callback과 실제 WebView2 lifecycle이 일치하지 않음
- 첫 reload 이후 key event가 GPUI/WebView2에 도달하지 않음
- 원인 **UNRESOLVED**
- callback만으로 PASS하지 않음

## 2. A/B 독립 navigation

- Ctrl+N이 reload 실패 이후 전달되지 않아 두 번째 창 생성 실패
- A/B 독립 URL/navigation ID 비교 **미수행**
- **UNVERIFIED**

## 3. 창 종료 및 재생성

- 0.89에서 B 종료는 `IsWindow`로 PASS
- 0.90 A/B lifecycle은 reload 실패로 미도달
- 마지막 창 종료 후 재생성 **UNVERIFIED**

## 4. First-click 6개

0.89 SendInput 결과 유지:

| control | 결과 |
|---|---|
| New Tab | **PASS** |
| Settings | **PASS** |
| Password | **PASS** |
| Downloads | **PASS** |
| Bookmarks | **PASS** |
| Address | **PASS** |

- 0.90 제품 변경은 reload counter뿐, mouse 처리 변경 없음
- 0.90 재실행은 **UNVERIFIED**

## 5. Shortcut regression

- Ctrl+L/T/W/N: 0.89 PASS 유지
- Ctrl+R: **UNRESOLVED** (reload #1 callback only, #2–#10 key 미전달)

## 6. Focus recovery

- 0.85 조건부 1회 GotFocus recovery 유지
- focus class `Zed::Window`
- 반복 SetFocus/sleep 없음

## 7. 기존 안정성

- exit classifier **7/7**
- titlebar crash **0/5**
- corrupt **14/14** 3회
- session **2/1** 3회
- `start_status_poll` 없음
- Render 100ms / `cx.defer` / panic hook / lifecycle log 유지

## 8. 동일 코드 3회 (`abdbacb`)

| run | Aggregate | navigation |
|---|---:|---:|
| 44526436784 | **12/12** | 72/7/11 |
| 44526449668 | **13/11** | 71/7/10 |
| 44526462159 | **11/10** | 71/6/9 |

**3회 연속 0 FAIL 미달.**

## 9. Titlebar

| run | stage | exit | 판정 |
|---|---|---|---|
| 44526475360 | 31/0/2 | 0 | **PASS** |
| 44526486051 | 32/0/1 | 0 | **PASS** |
| 44526498658 | 33/0/0 | 0 | **PASS** |
| 44526510614 | 0/1 | no first render | **ENVIRONMENT_LIMITATION/UNRESOLVED** |
| 44526522388 | 0/1 | no first render | **ENVIRONMENT_LIMITATION/UNRESOLVED** |

- full PASS **3/5**
- 두 실패는 `process_start`/`restore loaded` 후 status/window 미생성
- crash `0xC0000409` 아님

## 10. WinUI / 패키지

- WinUI `44526534753`: UI **48/48**, **143/0/9/1**, interop **70/70**
- Linux **SUCCESS**

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.90.0-win-x64.zip` | 126,908,316 bytes | `ac311f0af2801bdd856f614a942ab8cadf6f2a56a6fe137d3eee85fac6e22a6d` |
| `AMARE-0.90.0-win-x64-unsigned.msix` | 126,843,002 bytes | `0462038bcdc312b86b2a50c3edb5ac6ee47a644eac8cb73946e26f756bbfe302` |
| `AMARE-0.90.0-gpui-win-x64.zip` | 5,973,588 bytes | `b00b61d124524f65c86cfff35b7cd03494cd5125f0515d896eaddfce635987a9` |

- manifest `git_commit=abdbacb`, **UNSIGNED**
- MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. Reload callback과 실제 WebView2 lifecycle의
불일치, 첫 reload 이후 key 미전달, A/B 독립 navigation 미검증, 3회 연속
0 FAIL 미달로 **GPUI 기본 전환은 보류**합니다.
