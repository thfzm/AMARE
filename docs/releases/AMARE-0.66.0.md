# AMARE 0.66.0 — Shortcuts, Last Tab Recovery & Native Window Chrome

0.66.0은 0.65.0 실제 Windows 11 사용자 테스트에서 확인된 문제를 수정한 버전입니다.

## 먼저 확인할 점: 0.65.0에서 실행한 빌드

0.65.0 사용자 보고의 `New Tab 6`은 WinUI `TabManager`의 탭 이름 규칙이며, 기본 패키지
`AMARE-0.65.0-win-x64.zip`의 `AMARE.Windows.exe`에서만 나타납니다. GPUI 셸은 탭 제목이
`about:blank` 또는 페이지 제목이고, 0.65.0에도 이미 자체 창 chrome이 있었습니다.
따라서 0.65.0 사용자 테스트는 **WinUI 기본 패키지**에서 수행된 것으로 판단합니다.

0.66.0은 실제 사용자가 실행한 WinUI 앱을 우선 수정하고, GPUI 셸도 별도로 개선했습니다.

## 변경 사항

### WinUI — 키보드 단축키

- UI 스레드에 `WH_GETMESSAGE` 라우터를 설치해, WebView2 자식 창이 키보드 포커스를
  가지고 있어도 `Ctrl+T/L/N/W/R`, `Ctrl+Tab`, `Alt+Left/Right`를 앱 명령으로 변환합니다.
- XAML `KeyboardAccelerator`는 fallback으로 유지합니다.
- `Ctrl+N`을 `BrowserShortcutPolicy`에 추가하고, New Window 버튼과 **동일한
  `OpenNewWindow` 경로**를 사용합니다.
- Settings/ContentDialog가 열려 있어도 같은 라우터가 동작합니다.
- 처리한 키 메시지는 `WM_NULL`로 바꿔 WebView2가 같은 단축키를 두 번 처리하지 않습니다.

### WinUI — 마지막 탭 복구

- 마지막 탭을 닫으면 탭을 제거한 뒤 대체 WebView2를 새로 만드는 대신, **살아 있는
  WebView2 controller를 재사용해 New Tab 페이지로 재설정**합니다.
- 기존에는 `TabManager.Close`가 만든 `New Tab N` placeholder와 실제 hosted tab이
  경합했고, WebView2 초기화가 실패하면 controller 없는 탭이 남아 주소창 탐색이
  불가능했습니다. 이제 그런 상태가 생기지 않습니다.
- UI smoke에서 마지막 탭을 닫은 뒤 `https://example.com/` 탐색까지 확인했습니다.

### WinUI — AMARE 자체 창 상단 UI

- Windows 기본 제목 표시줄을 제거했습니다 (`ExtendsContentIntoTitleBar`).
- 32px AMARE title strip(파란 점 + `AMARE · 탭 제목`)을 창 상단에 두고, 이 strip 전체를
  창 드래그 영역으로 사용합니다.
- 최소화/최대화/복원/닫기는 Windows 시스템 caption 버튼을 사용하되, 배경을 투명하게
  하고 전경색을 테마에 맞췄습니다.
- 탭 스트립은 title strip **아래의 일반 client 행**으로 분리해, 기존 탭 reorder와
  tear-off 포인터 동작을 그대로 유지했습니다.
- 탭 스트립 오른쪽에 New Window 버튼을 추가했습니다.

### GPUI 셸

- WebView2 `AcceleratorKeyPressed`에서 GPUI로 단축키를 **동기적으로** 전달합니다
  (`SetFocus` + `SendMessageW`). 이전의 비동기 post는 `Ctrl+L` 직후 입력한 문자가
  주소창이 아니라 페이지로 가는 경합이 있었습니다.
- 마지막 탭을 닫으면 `cx.quit()` 대신 새 탭을 생성합니다.
- GPUI 셸은 0.65.0부터 이미 자체 titlebar를 사용하고 있었으며, 0.66.0에서도 유지합니다.

## 검증 결과

| 항목 | 결과 |
|---|---|
| Windows CI [#43571194047](https://github.com/thfzm/AMARE/actions/runs/43571194047) | **SUCCESS** |
| Windows verification assertions | **143 PASS / 0 FAIL / 9 UNVERIFIED / 1 SKIPPED** |
| WinUI UI smoke | **47 / 47 PASS** |
| C#/Rust interop | **70 / 70 PASS** |
| Linux CI [#43571194018](https://github.com/thfzm/AMARE/actions/runs/43571194018) | **SUCCESS** |
| GPUI CI [#43566952547](https://github.com/thfzm/AMARE/actions/runs/43566952547) | **FAILURE** |
| 실제 Windows 11 사용자 재검증 | **UNVERIFIED** |

WinUI UI smoke에서 확인한 항목:

- 자체 title strip 표시, native titlebar 제거
- WebView2 페이지 포커스에서 `Ctrl+T` / `Ctrl+L` / `Ctrl+N`
- New Window 버튼과 `Ctrl+N` 새 창 생성
- 마지막 탭 종료 후 New Tab 탐색
- 탭 drag reorder, 창 간 이동, tear-off + re-merge
- Settings/History/Diagnostics/Downloads/Update check, 세션 복원, maximize/restore

GPUI CI 상세:

- password **51 / 0 / 1**
- multiwindow **1 / 1 / 0**
- input **25 / 0 / 4**
- tab **8 / 1 / 4**
- library **2 / 1 / 0**
- download **7 / 0 / 1**
- settings **19 / 0 / 6**
- session **0 / 1 / 0**
- aggregate **FAIL=4 / UNVERIFIED=11**

GPUI password smoke의 `Ctrl+L` 직후 입력이 51/0/1로 통과한 것은 동기 forwarding 수정의
CI 근거입니다. 나머지 GPUI suite 실패는 CI foreground/SendInput 제약과 multiwindow
두 번째 창 생성 문제이며, 제품 PASS로 계산하지 않습니다.

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.66.0-win-x64.zip` (WinUI, 권장) | 126,906,505 bytes | `732b890881219d9b1033aff0ddddc407116ee75ce64b5d0fe3fc889639e3dcc9` |
| `AMARE-0.66.0-win-x64-unsigned.msix` | 126,841,852 bytes | `92f613fc960d1b5d61bcdf96873d1bd59990516b911f0267e7946e72db414a49` |
| `AMARE-0.66.0-gpui-win-x64.zip` (GPUI 대체 셸) | 5,885,885 bytes | `d89697fc87525e1bbc6f907f5e331d45ab0c696a79ced5eca63da595ca69d7aa` |

- UNSIGNED. MSIX 설치는 `0x80073D2C`로 **UNVERIFIED/BLOCKED**입니다.
- 공개 GitHub Release는 만들지 않았습니다.

## 사용자가 직접 확인할 항목

기본 패키지는 **WinUI**입니다. `AMARE.Windows.exe`를 실행하세요. GPUI 셸은
`AMARE-0.66.0-gpui-win-x64.zip`의 `amare-gpui.exe`로 별도 확인할 수 있습니다.

1. WebView2 페이지에서 `Ctrl+T` 10회
2. WebView2 페이지에서 `Ctrl+L` 10회, 주소창에 즉시 입력 및 한글 IME
3. `Ctrl+N` 10회
4. 주소창 포커스에서 `Ctrl+T` / `Ctrl+N`
5. Settings에서 `Ctrl+T` / `Ctrl+N`
6. 두 창 간 전환 후 `Ctrl+T/L/N`
7. `Ctrl+W`로 마지막 탭까지 닫은 뒤 검색/URL 탐색
8. 탭 reorder 및 tear-off 회귀
9. title strip 드래그 이동, 더블클릭 최대화/복원, 가장자리 리사이즈
10. Snap, 작업 표시줄, Alt+Tab, DPI 100/125/150/200%, 다중 모니터

사용자가 직접 수행하지 않은 항목은 PASS가 아니라 **UNVERIFIED**입니다.

## 스크린샷

- WinUI: `artifacts/windows/screenshots/0.66.0/` — 7 PNG
  - `01-initial-custom-titlebar.png` 1044×788
  - `02-settings.png` 1044×788
  - `03-multiple-tabs.png` 1044×788
  - `04-two-windows.png` 1024×768 (desktop)
  - `05-last-tab-recovered.png` 1044×788
  - `06-maximized.png` 1040×736
  - `07-restored.png` 1044×788
- GPUI: `artifacts/windows/screenshots/0.66.0/gpui/` — 52 PNG

## 남은 문제

- 실제 Windows 11 사용자 키보드/창 검증은 아직 완료되지 않았습니다.
- GPUI CI는 multiwindow 두 번째 창 생성과 입력 자동화 제약으로 실패합니다.
- Snap, DPI, 다중 모니터, Alt+Tab, 작업 표시줄 미리보기는 CI runner에서 검증할 수
  없어 UNVERIFIED입니다.
- unsigned MSIX 설치는 BLOCKED입니다.
