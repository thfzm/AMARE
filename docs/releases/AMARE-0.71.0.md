# AMARE 0.71.0 — Window State & Session Persistence Integrity

0.71.0은 0.70에서 확인된 **GPUI titlebar restore**와 **다중 창 session
persistence race**를 수정하고, 2개 창의 실제 maximize/restore/minimize,
다중 창 session 저장·복원, active URL/scroll/geometry/maximized restore를
검증한 버전입니다. **GPUI를 기본 패키지로 전환하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- GPUI titlebar: 두 창 모두 maximize/restore/minimize **16/0/0 PASS**, 3회 완전 반복 모두 PASS
- GPUI multi-window session: `saved windows=2`, `restored windows=2` **PASS**
- GPUI session active URL / scroll / normal client geometry / maximized restore **PASS** (최고 반복)
- Corrupt metadata / empty / corrupt DB fallback **PASS**; corrupt JSON fallback **UNVERIFIED**
- GPUI 3회 완전 반복: **5 FAIL / 6 FAIL / 6 FAIL** — 3회 연속 0 FAIL **미달**
- 최고 반복: **5 FAIL / 2 UNVERIFIED**
- 남은 FAIL은 password reveal, vault form 진입, tab/library 첫 navigation,
  settings 첫 category/search 등 **자동화 click/focus/timing**에 집중
- AI Web Panel, updater, site permission UI는 **미구현**

## 0.71 제품 수정

### Titlebar maximize/restore

GPUI 0.2.2 Windows `zoom()`은 항상 `SW_MAXIMIZE`만 호출합니다. 0.71은
`window.is_maximized()`(내부적으로 Win32 `IsZoomed`)로 상태를 확인하고,
maximized이면 `ShowWindow(hwnd, SW_RESTORE)`, 아니면 `zoom_window()`를
호출합니다.

### Session persistence

- session payload에 단조 증가 `revision`과 `schema`를 추가
- live window snapshot과 최근 종료 window snapshot을 분리
- 마지막 window 종료 시 최종 revision이 이전 autosave revision보다 **항상
  크도록** 보정. 0.70의 "final ids=[1,2]"가 revision 거부로 저장되지 않던
  결함을 수정
- 10초 close grace 안의 다중 창 종료는 하나의 shutdown으로 저장
- 10초가 지난 뒤 계속 사용 중인 닫힌 창은 다음 복원에서 제외
- `window_bounds()`의 normal placement와 `maximized` flag를 저장해
  windowed geometry와 maximized 상태를 분리
- legacy 0.70 array payload도 계속 읽음

### Session fallback

- 잘못된 window ID, 중복 window ID, 빈 URL, 비정상 active index, 비정상
  scroll을 정규화
- window/tab 수 상한, 빈 tab → `about:blank`
- JSON parse 실패와 DB 손상은 안전한 새 세션 + 오류 배너로 fallback
- corrupt raw payload는 `gpui.session.corrupt`에 백업

### Re-entrant panic

paint 중 `GetWindowRect`/`GetClientRect`를 호출하던 0.71 초기 진단 코드가
`RefCell already borrowed`를 다시 유발할 수 있어 paint path에서 제거했습니다.
외부/클라이언트 rect는 테스트에서 Win32로 직접 측정합니다.

## 검증 결과

| 항목 | 결과 |
|---|---|
| 두 창 titlebar maximize/restore/minimize | **PASS (16/0/0, 3회 완전 반복 모두)** |
| 다중 창 session saved/restored | **PASS (2 → 2)** |
| active tab URL / WebView2 Source | **PASS** (`http://127.0.0.1:8772/fast`) |
| scroll restore | **PASS** (`scrollY=2181`) |
| windowed client geometry | **PASS** (`1028x779 → 1028x779`) |
| outer/client 구분 | **PASS** (outer 1044×788, client 1028×779, scale 1) |
| maximized save/restore | **PASS** (`IsZoomed=true`, `maximized=true`) |
| corrupt metadata fallback | **PASS** (정규화 + 오류 배너) |
| empty session | **PASS** (안전한 새 창) |
| corrupt DB fallback | **PASS** (앱 시작, 오류 배너) |
| corrupt JSON fallback | **UNVERIFIED** (최종 3회에서 재도달 실패) |
| WinUI 기준선 | **48/48, 143/0/9/1, interop 70/70** |
| 실제 Windows 11 사용자 검증 | **UNVERIFIED** |
| DPI 125/150/200%, Snap, Alt+Tab, 다중 모니터 | **UNVERIFIED** |

## 3회 완전 반복 (동일 코드 `7253c68`)

| 회차 | run | Aggregate |
|---|---|---|
| 1 (최고) | [#43774705732](https://github.com/thfzm/AMARE/actions/runs/43774705732) | **5 FAIL / 2 UNVERIFIED** |
| 2 | [#43774723774](https://github.com/thfzm/AMARE/actions/runs/43774723774) | **6 FAIL / 6 UNVERIFIED** |
| 3 | [#43778302308](https://github.com/thfzm/AMARE/actions/runs/43778302308) | **6 FAIL / 6 UNVERIFIED** |

별도 실행 [#43774716984](https://github.com/thfzm/AMARE/actions/runs/43774716984)는
titlebar step canceled로 session이 skipped되어 완전 반복으로 집계하지
않았습니다.

**3회 연속 0 FAIL 미달**이며, 달성했다고 보고하지 않습니다.

## 기능 격차

- **미구현**: AI Web Panel, updater, GPUI 사이트 권한 관리 UI
- 상세 설계/의존성/검증 기준: `docs/GPUI_MIGRATION_GAPS.md`
- **UNVERIFIED**: titlebar double-click, 실제 Windows 11, DPI, Snap,
  Alt+Tab, 다중 모니터, corrupt JSON fallback

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.71.0-win-x64.zip` (WinUI) | 126,906,518 bytes | `628647aba685903a0114d2d72d2dd0907803efc18ec9e93fb8aba995e7c62ac9` |
| `AMARE-0.71.0-win-x64-unsigned.msix` | 126,841,796 bytes | `0f3e92de1c6f37792abc519e14f18459fd2a5b107eb1c74c3d10f0bfcae64a14` |
| `AMARE-0.71.0-gpui-win-x64.zip` | 5,945,094 bytes | `964d87a4cf2c9aeb99b81a303c17b9d74c9b781c50a160961e7d5d15e7c4cae3` |

- **UNSIGNED**. MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**.
- 공개 GitHub Release 없음.

## 스크린샷

- WinUI: `artifacts/windows/screenshots/0.71.0/` — **7 PNG**
- GPUI: `artifacts/windows/screenshots/0.71.0/gpui/` — **56 PNG**
  (초기, 일반 페이지, 여러 탭, 두 창, titlebar max/restore, session restore,
  maximized restore, Settings, Vault)

## GPUI 전환 판단

**0.71.0에서도 GPUI를 기본값으로 전환하지 않습니다.**

Titlebar restore, 다중 창 session 저장/복원, active URL/scroll/geometry/
maximized restore는 실제 PASS했습니다. 그러나 3회 연속 0 FAIL이 아니고,
여러 suite의 첫 click/navigation 자동화 FAIL과 corrupt JSON fallback,
실제 Windows 11/DPI 검증이 남아 있습니다.
