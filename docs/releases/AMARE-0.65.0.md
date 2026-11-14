# AMARE 0.65.0 — Keyboard Focus & Shortcut Reliability

0.65.0은 실제 Windows 11 사용자 테스트에서 확인된 Ctrl+T/Ctrl+L 포커스 의존성과 Ctrl+N 실패를 수정하기 위한 버전이다. CI 입력 자동화의 환경 제약과 별개로, 제품 단축키 전달 경로를 수정했다.

## 변경

- WebView2 `AcceleratorKeyPressed`에서 GPUI로 shortcut을 전달할 때 **Ctrl/Shift/Alt modifier를 명시적으로 keydown/keyup 전송**.
- 기존에는 letter key만 PostMessage하고 modifier는 `GetKeyState`에 의존해, 포커스 HWND에 따라 Ctrl+T/L/N이 누락될 수 있었다.
- Ctrl+T/L/N과 Ctrl+W/R, Ctrl+Tab, Alt+Left/Right가 동일한 forwarding 경로를 사용한다.
- `NewWindow` 버튼과 Ctrl+N은 동일한 `open_new_window` action을 사용한다.
- New Tab/New Window 버튼, 탭 재정렬, tear-off, WebView2/Vault 구조는 변경하지 않았다.

## 사용자 테스트 기준선

0.63.0 실제 Windows 11 사용자 테스트:

- Ctrl+T: top bar를 마지막으로 클릭한 경우만 작동
- Ctrl+L: 동일
- Ctrl+N: 작동하지 않음
- Ctrl+W/R: 정상
- New Tab/New Window 버튼, 탭 전환/닫기/드래그/tear-off: 정상

## CI 결과

- Windows: **143/0/9/1**, WinUI GUI **42/42**
- Linux: **SUCCESS**
- GPUI: **FAILURE** — CI 입력 자동화 제약(SendInput 0, foreground 변경)으로 제품 단축키를 검증하지 못함
- 0.65 GPUI run: password 51/0/1, multiwindow 1/1/0, tab 10/1/2, library 2/1/0, settings 18/0/7, session 8/0/2
- 3회 연속 0 FAIL: **미달/미실행**

CI 성공을 실제 사용자 키보드 검증으로 간주하지 않는다.

## 실제 사용자 검증 빌드

GPUI 검증용 ZIP:

`artifacts/windows/release/AMARE-0.65.0-gpui-win-x64.zip`

내용:

- `amare-gpui.exe`
- `VERSION`
- `GPUI-USER-TEST.md`

기본 패키지:

- `artifacts/windows/release/AMARE-0.65.0-win-x64.zip`
- `artifacts/windows/release/AMARE-0.65.0-win-x64-unsigned.msix`

### 사용자 체크리스트

1. WebView2 페이지 클릭 후 Ctrl+T 10회
2. WebView2 페이지 클릭 후 Ctrl+L 10회 + 한글 IME 입력
3. Ctrl+N 10회
4. 주소창 포커스 상태에서 Ctrl+T/Ctrl+N
5. Settings 패널에서 Ctrl+T/Ctrl+N
6. 두 창 간 포커스 전환 후 Ctrl+T/Ctrl+L/Ctrl+N
7. Ctrl+W/Ctrl+R 회귀
8. New Tab/New Window 버튼, 탭 재정렬, tear-off 회귀

사용자가 직접 수행하지 않은 항목은 PASS가 아니라 **UNVERIFIED**다.

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.65.0-gpui-win-x64.zip` | 5,885,095 bytes | `bb9301424f09d74c07a5f15abca8fe8e832b135f4824daf0e464f549babdb9bf` |
| `AMARE-0.65.0-win-x64.zip` | 126,900,779 bytes | `474a0ddfa95ad726af68e001a99468962e2f8ecd5f6e562297c0113631bcc456` |
| `AMARE-0.65.0-win-x64-unsigned.msix` | 126,836,733 bytes | `417100d6a8f8d1272c9439ef6bbfb7f0bbac3a9677493cd797659024d6cb554b` |

Unsigned. MSIX install `0x80073D2C` UNVERIFIED/BLOCKED. 공개 Release 없음.

## 판정

**제품 수정은 적용했지만 실제 Windows 11 사용자 검증은 아직 완료되지 않았다.** Ctrl+T/L/N은 실제 사용자 테스트에서 PASS/FAIL을 새로 확인해야 하며, CI 결과만으로 완료를 주장하지 않는다.
