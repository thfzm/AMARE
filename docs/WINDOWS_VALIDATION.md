# Windows 검증 가이드

- 작성일: 2026-11-03
- 단계: Phase 2.1 Windows 안정화/검증 자동화
- 스크립트: `tools/verify-windows.ps1`
- 보조 하네스: `tools/windows-verify/`

---

## 1. 전제

- Windows 10/11 x64
- .NET 8 SDK
- Rust stable + `x86_64-pc-windows-msvc`
- Visual Studio Build Tools/C++ 워크로드 + Windows SDK
- WebView2 Evergreen Runtime

GitHub `windows-latest`는 Windows Server일 수 있으며 Windows 11 실기기와 동일하지 않습니다. 스크립트는 Windows 11 build 22000+ 여부를 `windows_11` 필드로 기록하지만, Runner가 Windows 11이 아니어도 자동 빌드/테스트를 수행합니다.

---

## 2. 검증 순서

```text
preflight
→ Rust fmt/clippy/test
→ service/storage/backup/update tests
→ Rust Windows target build
→ FFI build
→ C ABI MSVC header compile
→ WebView2 Runtime 확인
→ C# Core build
→ C# Interop tests
→ WinUI 3 restore/build (required)
→ WindowsVerify probe build/run
→ JSON/log artifact
```

`tools/verify-windows.ps1 -CI`는 WebView2 Runtime을 확인하고, manual/memory/DRM probe를 환경변수로 UNVERIFIED 처리합니다.

---

## 3. 결과 구분

- **PASS**: 실제 실행 후 exit code 0
- **FAIL**: 실제 실행 후 non-zero
- **UNVERIFIED**: 실행하지 않았거나, Runner가 판정할 수 없음
- **SKIPPED**: 명시적으로 제외

`-SkipWinUI`를 사용하면 WinUI 단계는 `UNVERIFIED`로 기록됩니다.

---

## 4. 산출물

`artifacts/windows/`:

- `windows-verify-environment.json`
- `windows-verify-summary.json`
- `windows-verify-results.json`
- `windows-verification.json` (unified result schema)

Unified item shape:

```json
{
  "name": "webview2:webview2_local_navigation",
  "status": "PASS",
  "executed": true,
  "duration_ms": 1234,
  "environment": { "os": "...", "webview2": {}, "windows_11": true },
  "error": null,
  "log_path": "artifacts/windows/webview2_probe_run.log",
  "source": "windows-verify-probe",
  "detail": "..."
}
```
- `storage-ci.json`, `backup-ci.json`, `service-ci.json`
- `service-benchmark.json`, storage/backup benchmark JSON
- 단계별 `*.log`
- `interop-results.json`

---

## 5. 현재 실행 상태 (0.67.0)

- Windows CI [43575072487](https://github.com/thfzm/AMARE/actions/runs/43575072487): **143 PASS / 0 FAIL / 9 UNVERIFIED / 1 SKIPPED**, WinUI UI smoke **48/48**, interop **70/70**.
- Linux CI [43575072515](https://github.com/thfzm/AMARE/actions/runs/43575072515): **SUCCESS**.
- GPUI CI [43566952547](https://github.com/thfzm/AMARE/actions/runs/43566952547): **FAILURE** — password **51/0/1**, input **25/0/4**, aggregate **FAIL=4/UNVERIFIED=11**; **실제 Windows 11 사용자 검증 UNVERIFIED**.
- WinUI UI smoke **48/48**: custom title strip, WebView2 focus Ctrl+T/L/N, Ctrl+N/New Window, last-tab recovery, tab reorder/tear-off/cross-window, settings/history/downloads, session restore, maximize/restore **PASS**.
- GPUI 최종: password **51/0/1**, input **25/0/4**, multiwindow **5/1**, settings **20/0/5**; tab 4번째 생성, local navigation, titlebar control은 **FAIL/UNVERIFIED**다.
- 실제 Windows 11 사용자 키보드/창 chrome 검증, Snap, Alt+Tab, 작업 표시줄, Korean IME, DPI, 다중 모니터는 **UNVERIFIED**다.
- ALL_SITE 삭제는 **read-only/BLOCKED**, unsigned MSIX 설치는 `0x80073D2C`로 **UNVERIFIED**다.
- Windows 11 실기기, 다른 Windows 사용자 계정/DPAPI 권한 실패, DPI/multi-monitor는 **UNVERIFIED/BLOCKED**다.
