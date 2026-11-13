# AMARE 0.62.0 — GPUI Input Pipeline & Window Lifecycle Forensics (Incomplete)

0.62.0은 실제 Ctrl+T/새 탭 버튼과 Ctrl+N/새 창 버튼의 입력→action→상태→render→WebView2 경로를 계측하고 근본 원인을 찾는 버전이다. **입력 자동화의 foreground/focus 불안정과 버튼 hit-test 실패로 핵심 목표를 완료하지 못했다.**

## 결과

- Product code HEAD: `599057b`
- Windows CI: **143/0/9/1**, WinUI GUI **42/42**
- Linux CI: **SUCCESS**
- GPUI CI: **FAILURE**
- 실제 입력 E2E: **FAIL/부분**
- Vault multiwindow: **11 PASS / 1 FAIL / 1 UNVERIFIED**
- CapturePreview normal/active/max/restore: **PASS**

## 진단 결과

### 계측 추가

- `AMARE_GPUI_DIAGNOSTICS=1`일 때 key/mouse/action/new-tab/open-window trace를 status JSON에 기록
- root mouse event trace
- `NewTab`/`NewWindow` action trace
- 새 탭 생성 id, tab count, active index trace
- `open_new_window` 요청 URL과 결과 trace

### Ctrl+T

- 실제 mouse activation 후 OS Ctrl+T로 **tab 1→2 생성 및 action trace PASS**한 실행이 있었다.
- 다음 동일 run에서는 foreground가 top-level HWND에서 다른 HWND로 바뀌며 action trace가 전혀 없었다.
- tab suite는 `Could not reach 2 tabs (current=1)` / `Could not reach 4 tabs (current=2)`로 실패했다.
- **입력 자동화가 foreground/focus를 안정적으로 확보하지 못하는 문제**와 GPUI action 자체 문제를 분리하지 못했다.

### 새 탭 버튼

- 실제 mouse click 좌표를 735~785 × 22~46으로 scan했으나 tab count가 증가하지 않았다.
- root mouse event trace도 기록되지 않아 실제 hit-test 대상/callback 실행을 확인하지 못했다.
- **새 탭 버튼 hit-test 실패 원인 미확정 / FAIL**

### Ctrl+N

- 실제 mouse activation 후 Ctrl+N에서 `action=NewWindow`, `open_new_window result=ok`, status window 2개가 확인된 실행이 있었다.
- 다음 run에서는 Ctrl+N action trace가 없고 windows=1이었다.
- **반복 재현 실패. 실제 입력 E2E FAIL/UNVERIFIED**

### 새 창 버튼

- 795~835 × 22~46 실제 click scan에서 두 번째 window가 생성되지 않았다.
- root mouse trace도 없어 hit-test/callback 미확인.
- **FAIL/UNVERIFIED**

## 입력 자동화 vs 제품 문제

- 진단용 실제 mouse activation으로 Ctrl+T/Ctrl+N이 **한 번씩 제품 action까지 도달**했다.
- 따라서 제품 `NewTab`/`NewWindow` handler와 GPUI window creation은 최소 1회 동작한다.
- 그러나 반복 실행에서 foreground가 child/다른 HWND로 바뀌고 input trace가 사라져 **자동화 신뢰성 문제**가 확인됐다.
- 버튼 click은 실제 hit-test trace가 없어 제품 버튼 문제인지 자동화 좌표 문제인지 **미분리**다.
- 제품 수정은 새 탭 WebView2 controller의 deferred attach만 적용했으며, 입력/버튼 문제를 추정 수정하지 않았다.

## 테스트 결과

- Windows: 143/0/9/1, GUI 42/42
- Linux: SUCCESS
- GPUI run `43414011192`: FAIL
  - password 51/0/1
  - multiwindow 11/1/1
  - input 24/0/5
  - tab 3/1/0
  - library 2/1/0
  - settings 18/0/7
  - session 8/0/2
- **3회 연속 0 FAIL: 미달/미완료** — 핵심 입력 경로 실패로 반복 안정성 단계에 진입하지 못했다.

## Vault / Pending / CapturePreview

- Vault v2는 유지했다.
- multiwindow는 두 번째 창/독립 수정/conflict/stale까지 11 PASS 후 lock 단계에서 실패했다.
- pending autofill 실제 생성/취소: **UNVERIFIED**
- CapturePreview normal/active/max/minimize-restore: **PASS**
- 개별 탭 2개 캡처: **UNVERIFIED**

## 스크린샷 / 진단 로그

- `artifacts/windows/screenshots/0.62.0/` — PNG **59개**
- `artifacts/windows/diagnostics/0.62.0/input-diagnostics.json` — key/mouse/action trace, window states
- 확보: initial, 새 탭, multi-tab(일부), normal page, 두 창 sync, conflict, stale rejection, 설정, 다운로드, CapturePreview, maximize/restore
- 미확보: 3/4 tabs, 새 탭 버튼 결과, 새 창 버튼 결과, lock/unlock, multiwindow restart
- 미확보 이미지를 PASS로 기록하지 않았다.

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.62.0-win-x64.zip` | 126,900,800 bytes | `f56e0fac24765be8a022d04ab4fe2f221a57327a92a3dd09eedf4cc96801e4c6` |
| `AMARE-0.62.0-win-x64-unsigned.msix` | 126,836,704 bytes | `d1c040bf426ae9631959da8bee531a612b4e3cc74c07c77cd260fa8d7633631a` |

Unsigned. MSIX 설치 `0x80073D2C` UNVERIFIED/BLOCKED. 공개 Release 없음.

## 판정

**0.62.0은 새 탭/새 창 실제 입력 경로 문제를 해결하지 못했다.** 진단 결과는 제품 action이 일부 실행됨을 보여주지만, 반복 가능한 foreground/focus와 버튼 hit-test를 확보하지 못했다. 다음 단계는 Windows foreground/child focus 구조와 GPUI hit-test를 실제 debug trace로 더 깊게 분석하는 것이다.
