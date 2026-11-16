# AMARE 0.72.0 — GPUI Validation Reliability & Recovery Hardening

0.72.0은 새 기능 추가가 아니라 **GPUI 검증 신뢰도와 복구 경로**를 강화한
버전입니다. **GPUI 기본 전환은 승인하지 않았고 WinUI를 기본 패키지이자
기능 검증 기준으로 유지합니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- Corrupt session matrix: **14/14 PASS** — 3회 완전 반복 모두
- Session persistence regression: **30 PASS / 0 FAIL / 1 UNVERIFIED** (2회)
  - multi-window **2→2**, active URL, scroll, geometry, maximized, immediate close, no-resurrection PASS
- Titlebar 두 창 max/restore/minimize: 0.72 선행 run **12/0/0**, 0.71 **16/0/0**
- GPUI 동일 코드 3회 완전 반복(`30c4a62`):
  - **3 FAIL / 9 UNVERIFIED**
  - **4 FAIL / 5 UNVERIFIED**
  - **4 FAIL / 8 UNVERIFIED**
- **3회 연속 0 FAIL 미달**
- 남은 FAIL은 password/vault/library/settings의 첫 click·submit 자동화와
  session smoke의 중복 corrupt fixture
- AI Web Panel, updater, site permission UI는 **미구현**

## 주요 수정

### 1. First navigation 유실 (PRODUCT_BUG)

`navigate()`는 active tab의 WebView2 controller가 아직 없으면 아무 동작도
하지 않고 `loading=true`만 남겼습니다. 새 탭/복원 탭의 controller는 비동기
attach되므로 첫 주소 제출이 유실될 수 있었습니다.

- tab별 `pending_navigation` 큐 추가
- controller attach 후 `refresh_tabs`에서 pending navigation 실행
- 상태에 `navigation_requests`, `last_navigation_url`, `pending_navigation` 노출
- 테스트는 Enter 수신(`navigation_requests` 증가)과 실제 `source` 변경을 분리

### 2. Corrupt JSON 복구 (PRODUCT_BUG + 검증)

- invalid/empty/truncated JSON도 **raw 그대로 backup**하도록
  `gpui.session.corrupt`를 `Text`로 저장 (기존 Json 저장은 invalid JSON을
  저장할 수 없었음)
- missing field에 `serde(default)` 적용
- invalid URL, active index, negative/huge scroll 정규화 + 원인 로그
- mixed valid/invalid window에서 정상 window 최대 복원
- 14개 독립 시나리오 matrix 추가

### 3. Session persistence regression

기존 revision/final guard/10초 close grace를 유지하면서 다음을 추가 검증:

- 두 창 서로 다른 탭 종료/복원
- 한 창만 닫고 12초 후 나머지 종료 → **닫힌 창 부활 없음**
- autosave 직후/도중 즉시 종료 → 마지막 navigation URL 복원
- 연속 재시작
- saved/restored window count, active URL, scroll, geometry, maximized 비교

### 4. Titlebar double-click

drag 영역에 `ClickEvent.click_count()==2`일 때만 maximize/restore toggle을
실행하는 명시적 handler를 추가했습니다. 기존 `SW_RESTORE` 분기는 유지합니다.

### 5. 테스트 인프라

- foreground/HWND 확인과 주소 선택(`selected_range`) 대기 추가
- navigation request와 source/completion 분리
- password reveal/entry action은 실제 layout bounds 사용
- corrupt matrix는 profile DB를 명시적으로 선택하고 backup row 존재까지 확인
- titlebar multi-window CI step은 두 번째 창에서 hang해 **blocking sequence에서
  분리** (session/corrupt가 skip되지 않도록)

## 3회 완전 반복 (`30c4a62`)

| run | Aggregate | session | corrupt | 비고 |
|---|---:|---:|---:|---|
| [43814816281](https://github.com/thfzm/AMARE/actions/runs/43814816281) | **3 FAIL / 9 UNV** | 30/0/1 | 14/0/0 | best |
| [43814829207](https://github.com/thfzm/AMARE/actions/runs/43814829207) | **4 FAIL / 5 UNV** | 30/0/1 | 14/0/0 | |
| [43814837469](https://github.com/thfzm/AMARE/actions/runs/43814837469) | **4 FAIL / 8 UNV** | 2/1/1 | 14/0/0 | session 조기 실패 |

- session suite의 1 UNVERIFIED는 **중복된 구 corrupt-session-fallback fixture**
  (신규 14-case matrix는 PASS)
- titlebar multi-window step은 final 3회에서 CI hang 때문에 제외됨
  (0.72 `45365b4` run [#43803737758](https://github.com/thfzm/AMARE/actions/runs/43803737758) 12/0/0, 0.71 16/0/0)

## Corrupt session matrix (14/14 PASS)

empty-string, truncated-json, invalid-syntax, missing-fields, unknown-field,
duplicate-window-id, invalid-url, active-out-of-range, negative-scroll,
huge-scroll, legacy-array, mixed-valid-invalid, empty-window-list,
normal-payload.

각 case에서 앱 비정상 종료 없음, window/tab 수, error banner, corrupt raw
backup 존재/일치를 확인했습니다.

## Session persistence regression

최고 run에서 다음이 모두 PASS:

- `session-multiwindow-saved: saved windows=2 tabs=2`
- `multi-window-session-restore: restored windows=2`
- active tab URL/WebView2 Source
- scroll (`scrollY=2181`)
- windowed geometry (`1028x779`)
- maximized (`IsZoomed=true`, normal bounds 보존)
- `session-one-window-close`
- `session-no-resurrection: saved windows=1`
- `session-immediate-autosave-close`

## WinUI 기준선

- UI smoke **48/48**
- Windows assertions **143/0/9/1**
- interop **70/70**
- Linux **SUCCESS**
- WinUI/GPUI 결과 합산 없음

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.72.0-win-x64.zip` (WinUI) | 126,906,516 bytes | `386a080b28799da2d1a5c290e64f7c5b3835ca259c8b26c752d6f66543187ee5` |
| `AMARE-0.72.0-win-x64-unsigned.msix` | 126,841,819 bytes | `0f4772edc1cca6e3e47ddff825c499fd8b03ff43dcc41c559df6a3a85b3cec81` |
| `AMARE-0.72.0-gpui-win-x64.zip` | 5,943,145 bytes | `9d7c61e9caf3d58c4d32b999979ce1158dfb619232c28105cda2d7793223345d` |

- **UNSIGNED**. MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 스크린샷

- WinUI: `artifacts/windows/screenshots/0.72.0/` — **7 PNG**
- GPUI: `artifacts/windows/screenshots/0.72.0/gpui/` — **44 PNG**
  - initial/new tab, normal page, multiple tabs, two windows, titlebar
    max/restored, Settings, Vault accounts, password reveal, session restore,
    maximized restore, corrupt fallback

실제 캡처 파일만 기록했습니다.

## 미해결

- 3회 연속 0 FAIL
- password/vault/library/settings 첫 click/submit 자동화
- titlebar multi-window CI step의 두 번째 창 hang
- old duplicate corrupt-session-fallback fixture
- 실제 Windows 11 사용자, DPI 125/150/200%, Snap, Alt+Tab, 다중 모니터
- AI/updater/site permission

## 최종 판정

WinUI를 기본 패키지로 유지합니다. 0.72는 **corrupt 14/14**, **session
persistence 30/0/1**, **multi-window 2→2**, **immediate close/no-resurrection**
을 실제로 검증했지만, 3회 연속 0 FAIL과 첫 입력 자동화 안정화가 남아 있어
GPUI 기본 전환은 계속 보류합니다.
