# AMARE 0.64.0 — Interactive Windows Validation & GPUI Accessibility (Incomplete)

0.64.0은 GPUI toolbar의 실제 layout bounds와 Windows input harness/UIA를 결합해 새 탭/새 창 입력 경로를 검증하려 한 버전이다. **CI 세션 입력 제약으로 실제 사용자 입력 E2E는 여전히 BLOCKED/UNVERIFIED다.**

## 결과

- Product code HEAD: `106c943`
- Windows CI: **143/0/9/1**, WinUI GUI **42/42**
- Linux CI: **SUCCESS**
- GPUI CI: **FAILURE**
- CPUI 3회 반복: **미달/미실행**
- Input harness: **SendInput 반환 0**
- GPUI UIA toolbar: **없음**
- 실제 layout bounds: **확보**

## Layout bounds 진단

`on_children_prepainted`로 실제 toolbar child bounds를 status에 기록했다.

- window: x=-10, y=-13, 1028×779, scale=1.0
- tab strip: x=12, y=5, 766×34
- **new tab: x=786, y=7, 30×30**
- **new window: x=824, y=7, 30×30**
- min/max/close: x=862/908/954, 46×45

이 값은 추측 좌표가 아닌 GPUI layout bounds다.

## 입력 환경

- WinForms harness: SendInput Ctrl+T/Ctrl+N 반환 **0**, ctrl counter 0
- PostMessage: keydown/up 도달, modifier 없음
- 실제 mouse button click: harness에서 동작
- AMARE 입력 run:
  - Ctrl+T action이 성공한 run도 있었지만 최종 run에서는 foreground가 다른 HWND로 바뀌며 action 없음
  - layout bounds center click 후에도 action 없음
  - button click 후 foreground HWND가 사라지거나 다른 창으로 이동
- **입력 자동화 환경 문제와 제품 button/hit-test 문제를 완전히 분리하지 못했다.**

## UIA / 접근성

- GPUI 0.2.2 소스에 공식 accessibility API가 없다.
- AMARE UIA tree: root + WebView2 pane **9 elements**, toolbar 버튼 없음.
- New Tab/New Window UIA element, AutomationId, BoundingRectangle, InvokePattern 없음.
- UIA Invoke **not-found**.
- 공식 API 없이 접근성 요소를 임의 구현하지 않았다.
- 대신 GPUI layout bounds 진단 경로를 제공했다.

## New Tab / New Window

- Ctrl+T: tab suite가 4 tabs까지 도달한 run이 있었으나 5 tabs에서 실패.
- 새 탭 버튼: layout bounds center click 후 action 없음.
- Ctrl+N: multiwindow 두 번째 창 생성 실패.
- 새 창 버튼: 두 번째 window 없음.
- 10회/5회 반복 기준 **미달**.

## 테스트

- Windows: 143/0/9/1, GUI 42/42
- Linux: SUCCESS
- GPUI run `43488605751`: FAIL
  - password 51/0/1
  - multiwindow 1/1/0
  - input 25/0/4
  - tab 10/1/3 (4 tabs까지)
  - library 18/0/1
  - settings 18/0/7
  - session 0/1/0
- 3회 연속 0 FAIL **미달/미실행**.

## 스크린샷 / 진단

- `artifacts/windows/screenshots/0.64.0/` — PNG **61개**
- `artifacts/windows/diagnostics/0.64.0/input-harness.json`
- `artifacts/windows/diagnostics/0.64.0/uia-amare.json`
- `artifacts/windows/diagnostics/0.64.0/input-diagnostics.json` (layout bounds 포함)
- 실제 layout bounds 진단 overlay는 만들지 않았고 status JSON으로 제공했다.
- 확보: initial, 새 탭, normal page, 설정, 다운로드, CapturePreview, maximize/restore, UIA screenshot
- 미확보: 두 번째 창/두 창 동시, conflict/stale, lock/unlock, multiwindow restart, 5 tabs, button click 결과

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.64.0-win-x64.zip` | 126,900,812 bytes | `bdd597d0afeb8a9bffcc3a75829d92fc8df3a23c22d7d0eb73691fbd7891b6f2` |
| `AMARE-0.64.0-win-x64-unsigned.msix` | 126,836,682 bytes | `9c5c6dc0707c8673de4140f747fbbba568dafa52644c244b96c468d1bad9241c` |

Unsigned. MSIX install `0x80073D2C` UNVERIFIED/BLOCKED. 공개 Release 없음.

## 판정

**0.64.0은 실제 layout bounds를 확보하고 입력 환경의 BLOCKED 원인을 더 구체화했지만, 실제 사용자 입력 E2E와 3회 반복 안정성은 완료하지 못했다.**
