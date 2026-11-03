# Windows 테스트 전략

- 원칙: Linux에서 가능한 로직은 Linux에서, Windows 전용은 Windows Runner/실기기에서 검증합니다.
- 실행하지 않은 테스트는 PASS로 표시하지 않습니다.

---

## 1. Linux에서 실행 가능

- Rust workspace: fmt, clippy, tests, service/store/backup
- C ABI: `amare-ffi` Rust tests, C header link smoke, C# P/Invoke interop
- C# `AMARE.Windows.Core`: 주소창/검색엔진/navigation policy/profile path/tab manager/download/new-tab HTML/adapter fake bridge
- C# native service interop: profile/settings/session/search + stale context error
- 기존 `verify-linux.sh`

## 2. Windows Runner

`tools/verify-windows.ps1 -CI`:

- Rust workspace + service tests + JSON reports
- C ABI build + MSVC header compile
- C# interop tests (core logic + native service)
- WinUI 3 restore/build (required; 실패 시 FAIL)
- WebView2 Runtime 존재 확인
- 기존 WebView2 headless probes

Windows Runner는 Windows 11 실기기와 동일하지 않습니다.

## 3. Windows 11 실기기에서 반드시 확인할 것

- 실제 WebView2 페이지 로딩/렌더링
- 탭 5/10/20개 메모리와 전환
- WebView2 UserDataFolder 프로필 분리(쿠키/로그인/캐시)
- IME/DPI/멀티 모니터/keyboard shortcut
- 다운로드 완료/파일 열기
- 외부 protocol 확인 dialog
- 비정상 종료 후 세션 복구
- 인증서 오류/HTTPS 동작
- DRM/미디어(이번 Phase 범위 밖)

## 4. 결과 구분

- PASS: 실제 실행해 성공
- FAIL: 실제 실행해 실패
- UNVERIFIED: 실행하지 않았거나 Runner 환경에서 판정하지 않음
- SKIPPED: 의도적으로 제외

## 5. 현재 상태 (2026-11-03, Linux)

- Windows 자동 검증 하네스: `tools/verify-windows.ps1` + `tools/windows-verify/` (local HTTP/HTTPS/tab/profile probes)

- Rust tests: 138개 (Phase 2 FFI 추가 후)
- C# interop/core tests: **46/46** (Phase 2.1/2.2 신규 33 포함)
- MainWindow C# semantic type-check: WindowsAppSDK reference로 PASS (임시 검증, 공식 빌드 아님)
- WinUI XAML compile/MakePri: Linux에서 불가 (`XamlCompiler.exe`/`MakePri.exe` Windows-only)
- Windows Runner: **실행하지 않음**
- Windows 11 실기기: **미검증**
