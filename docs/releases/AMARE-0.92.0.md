# AMARE 0.92.0 — WebView2 Reload Verification & Multi-Window Independence

0.92.0은 Direct WebView2 Reload를 Ctrl+R과 분리해 10회 검증하고, A/B 창
종료·마지막 창 종료·재생성을 추적한 버전입니다.
**GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- 0.81 `0xC0000409` 수정 유지: titlebar crash **0/5**, exit classifier **7/7**
- Direct WebView2 Reload (hidden F8 diagnostic): 호출/성공 **10/10**
  - `last_direct_reload_ok=True`
  - navigation counter 증가는 **5/10**
  - source 유지
- Ctrl+R: `ReloadPage` callback **10/10**
  - navigation counter 증가는 **4/10**
  - callback만으로 PASS하지 않음
- A/B 창 종료·마지막 창 종료·프로세스 exit **PASS**
- A/B 독립 navigation은 fixture load/telemetry 혼입으로 **UNVERIFIED**
- 새 창 C 재생성 **UNVERIFIED**
- 동일 코드 3회: **11/17**, **11/15**, **13/14** FAIL/UNVERIFIED
- Titlebar 5회: full PASS **2/5**, runner cancel 3회, crash **0/5**

## 1. Direct Reload vs Ctrl+R

| 경로 | 호출/성공 | nav counter 증가 | 판정 |
|---|---:|---:|---|
| Direct API (F8) | **10/10** | 5/10 | **PASS (API)** |
| Ctrl+R | **10/10** | 4/10 | **UNVERIFIED** |

- Direct API는 `Reload()` HRESULT/is_ok True
- Ctrl+R은 `amare::ReloadPage` keybinding 실행
- 두 경로 모두 source URL 유지
- navigation counter는 callback보다 늦게/누락되어 독립 evidence 부족
- **fixture request counter 미구현** → UNVERIFIED

## 2. A/B 독립 입력

- 0.89 A/B `alpha`/`beta`, B 종료 `IsWindow` PASS 유지
- 0.92 probe에서는 B load가 example.com을 거쳐 fixture-b로 늦게 전환
- 입력 독립성 재검증은 **UNVERIFIED**

## 3. A/B 독립 navigation

- A fixture-a, B fixture-b 로드
- A reload: B source가 로드 중 변경되어 **UNVERIFIED**
- B reload: B nav counter 2→2, A source 유지
- 한 창 reload가 다른 창을 변경했다는 증거는 없음
- **UNVERIFIED**

## 4. 창 종료 및 재생성

| 단계 | 결과 |
|---|---|
| B 종료 후 A valid | **PASS** |
| A reload after B close | **PASS** |
| A 마지막 창 종료/프로세스 exit | **PASS** |
| 새 창 C 생성 | source fixture-a 확인, validity **UNVERIFIED** |

## 5. First-click 6개

0.89 SendInput 결과 유지:

| control | 결과 |
|---|---|
| New Tab | **PASS** |
| Settings | **PASS** |
| Password | **PASS** |
| Downloads | **PASS** |
| Bookmarks | **PASS** |
| Address | **PASS** |

- 0.92 재실행 **UNVERIFIED**
- 제품 mouse 처리 변경 없음

## 6. Keyboard / Shortcut

- fixture 로드 후 Ctrl+L/T/W/N **PASS** (0.91)
- Ctrl+R callback **10/10**, navigation evidence **4/10**
- general text input PASS
- fixture 미로드 keydown=0은 제품 결함으로 계산하지 않음

## 7. 동일 코드 3회 (`0ddc834`)

| run | Aggregate | navigation |
|---|---:|---:|
| 44564257181 | **11/17** | 70/6/16 |
| 44564270284 | **11/15** | 67/7/14 |
| 44564286698 | **13/14** | 72/7/13 |

**3회 연속 0 FAIL 미달.**

## 8. Titlebar / crash

| run | stage | exit | 판정 |
|---|---|---|---|
| 44564301561 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |
| 44564317330 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |
| 44564334087 | 32/0/1 | 0 | **PASS** |
| 44564349700 | 29/0/0 | cancel | ENVIRONMENT_LIMITATION |
| 44564365235 | 32/0/1 | 0 | **PASS** |

- full PASS **2/5**
- crash **0/5**
- exit classifier **7/7**

## 9. WinUI / 패키지

- WinUI `44564381997`: UI **48/48**, **143/0/9/1**, interop **70/70**
- Linux **SUCCESS**

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.92.0-win-x64.zip` | 126,906,544 bytes | `1012168d99b276b8dd7fea450bbfe011a3c777f4af877540d8c1aacb85e5ed04` |
| `AMARE-0.92.0-win-x64-unsigned.msix` | 126,841,838 bytes | `39fea08ce0f41654ea256a136f66ec3635df0b06e4099b9ff07c3b5b28bcdff3` |
| `AMARE-0.92.0-gpui-win-x64.zip` | 5,976,012 bytes | `79afdfbd50b67dd6b94943fbf3efe330d2257381d67dbf5d47cb12909a5c1353` |

- manifest `git_commit=0ddc834`, **UNSIGNED**
- MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. Direct WebView2 Reload API는 10/10
호출·성공했지만 navigation evidence와 fixture request counter가 부족하고,
Ctrl+R navigation, A/B 독립 navigation, 새 창 재검증, 3회 연속 0 FAIL,
실제 Windows 11/IME 검증이 남아 **GPUI 기본 전환은 보류**합니다.
