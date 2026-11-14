# AMARE 0.69.0 — GPUI Session Recovery & Interaction Stability

0.69.0은 GPUI의 Session Restore, custom titlebar, multiwindow/탭/입력
lifecycle을 안정화한 버전입니다. **GPUI를 기본 패키지로 전환하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- GPUI 최고 반복: **1 FAIL / 15 UNVERIFIED**
- 동일 최종 코드 3회: **1 FAIL / 1 FAIL / 2 FAIL** — 3회 연속 0 FAIL **미달**
- Session Restore의 metadata/controller/창 복원은 **PASS**, 실제 active navigation
  source/scroll/corrupt fallback은 **UNVERIFIED**
- Titlebar 최소화/최대화는 실제 mouse **PASS**, 복원/닫기는 **UNVERIFIED**
- 기능 격차(AI/updater/사이트 권한)는 설계만 완료

## WinUI 0.69.0

- Windows UI smoke **48/48**
- Windows assertions **143/0/9/1**
- C#/Rust interop **70/70**
- Linux **SUCCESS**
- 실제 Windows 11 사용자 재검증 **UNVERIFIED**

0.66.0의 WinUI 단축키 라우터, Ctrl+N, 마지막 탭 controller 재사용,
AMARE title strip은 유지했습니다.

## GPUI 0.69.0 변경

### Session Restore

- 복원 창은 metadata/탭을 먼저 만들고, WebView2 controller를 **entity borrow
  밖**에서 생성한 뒤 attach하도록 변경했습니다.
- 활성 탭을 먼저 attach합니다.
- 복원 중 controller가 없다는 이유로 tab을 즉시 실패 처리하던 로직을 제거했습니다.
- `session-complete-restore`, `session-webview-attached`, `session-tab-failure`,
  settings의 multi-window session restore가 PASS했습니다.
- active navigation source/scroll/window geometry/corrupt fallback은
  UNVERIFIED입니다.

### Custom Titlebar

- min/max/close를 `window_control_area` OS hit-test에만 의존하지 않고
  GPUI `on_click` action(`minimize_window`/`zoom_window`/`remove_window`)으로
  처리했습니다.
- 실제 mouse minimize/maximize **PASS**, second-window maximize **PASS**.
- restore/close는 아직 **UNVERIFIED**입니다.

### 입력/탭/multiwindow

- WebView2 controller 생성에서 발생하던 GPUI RefCell 재진입 panic을 제거했습니다.
- 새 탭/새 창/복원 창 모두 controller를 entity borrow 밖에서 생성합니다.
- 탭 test **21/0/6**, 4번째 탭과 last-tab Ctrl+W recovery가 PASS했습니다.
- password **51/0/1**, input **25/0/4**, library **20/0/0**, download **7/0/1**,
  settings **21/0/4**, session **10/0/4**.
- multiwindow **12/1**: 두/세 번째 창, 탭 독립성, Vault 동기화/충돌, 편집은
  PASS; dave delete 자동화 1건이 FAIL입니다.

### 테스트 자동화 개선

- password edit/delete는 실제 `layout_bounds`를 사용하고 foreground를 재시도합니다.
- multiwindow 상태는 종료된 창의 status 파일을 제외하고 live window만 셉니다.
- last-tab은 Ctrl+W 제품 경로로 검증하고, 종료 실패는 UNVERIFIED로 분리했습니다.
- local server readiness를 PASS/FAIL로 기록합니다.

## GPUI 최종 반복

| run | aggregate | 비고 |
|---|---|---|
| [#43639966790](https://github.com/thfzm/AMARE/actions/runs/43639966790) | **1 FAIL / 15 UNVERIFIED** | dave delete 1건 |
| [#43642547300](https://github.com/thfzm/AMARE/actions/runs/43642547300) | **2 FAIL / 15 UNVERIFIED** | dave delete + tab exit |
| [#43642552642](https://github.com/thfzm/AMARE/actions/runs/43642552642) | **2 FAIL / 14 UNVERIFIED** | dave delete + tab exit |

3회 연속 0 FAIL은 **미달**입니다. assertion 삭제/약화 없이 FAIL/UNVERIFIED를
유지했습니다.

## 기능 격차

- **미구현**: AI Web Panel, updater, GPUI 사이트 권한 관리 UI
- **UNVERIFIED**: Session active navigation/scroll/corrupt fallback, titlebar
  restore/close, GPUI DPI/Snap/Alt+Tab
- **부분 검증**: multiwindow dave delete, password edge actions
- **검증됨**: WinUI 전체 기준선, GPUI password/input/tab/library/download/settings
  및 Session metadata/controller/window 복원

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.69.0-win-x64.zip` (WinUI) | 126,906,520 bytes | `facd013a5e9bcaeb609499afa008bd5f0099b7f6c248af661c79b7ab9965021a` |
| `AMARE-0.69.0-win-x64-unsigned.msix` | 126,841,853 bytes | `f6b30efd493c43f4f7cb61db27d262d35bbefb1b5d97d8c1ae018da06b0540c2` |
| `AMARE-0.69.0-gpui-win-x64.zip` | 5,912,101 bytes | `21c1d17f8c2f3ad38a2e769b24a153bf457b922d309d6f25f1d240ee9b4742fe` |

- UNSIGNED. MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**.
- 공개 GitHub Release 없음.

## 스크린샷

- WinUI: `artifacts/windows/screenshots/0.69.0/` — 7 PNG
- GPUI: `artifacts/windows/screenshots/0.69.0/gpui/` — 68 PNG
  - 초기/일반 페이지/여러 탭/Settings/다운로드/두 창 동기화/Session Restore/
    titlebar maximized·restored/실패 화면 포함

## 사용자 재검증

1. WinUI 0.66.0 체크리스트 재확인
2. GPUI WebView2 페이지/주소창/Settings에서 Ctrl+T/L/N
3. GPUI 두 번째·세 번째 창 생성/전환/종료, 창별 탭
4. GPUI Session Restore — 탭 순서/URL/활성 탭/창 상태, 두 창 복원
5. GPUI custom titlebar 최소화/최대화/복원/닫기, Snap/Alt+Tab/DPI
6. GPUI 마지막 탭 Ctrl+W recovery, tear-off/re-merge
7. GPUI Vault edit/delete/충돌

## 남은 문제

- GPUI 3회 연속 0 FAIL 미달
- multiwindow dave delete 자동화
- tab exit cleanup 자동화
- Session active navigation/scroll/corrupt fallback
- titlebar restore/close
- AI/updater/permission 미구현
- 실제 Windows 11 사용자 검증

## GPUI 전환 판단

**0.69.0에서 GPUI를 기본값으로 전환하지 않습니다.**

Session Restore의 핵심 metadata/controller/window 복원과 titlebar
최소화/최대화, 탭/라이브러리/Settings가 크게 안정화됐지만, 3회 연속 0 FAIL,
active navigation/scroll, titlebar restore/close, 기능 격차가 남아 있습니다.
