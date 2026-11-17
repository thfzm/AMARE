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

## 5. 현재 실행 상태 (0.81.0)

- Windows CI [44099436225](https://github.com/thfzm/AMARE/actions/runs/44099436225): **143 PASS / 0 FAIL / 9 UNVERIFIED / 1 SKIPPED**, WinUI UI smoke **48/48**, interop **70/70**.
- Linux CI [44110507502](https://github.com/thfzm/AMARE/actions/runs/44110507502): **SUCCESS**.
- Exit code classifier: **7/7 PASS** (`0`, non-zero, `0xC0000409`, timeout, cancel, invalid handle).
- `0xC0000409` 원인: status poll의 paint 중 GPUI 재진입 → `RefCell already borrowed` panic → `ucrtbase` fail-fast. poll 제거로 수정.
- Titlebar 5회: crash **0/5**, full PASS **3/5**, 2회 runner cancel.
- Restore-first: text input 개선, Enter/submit 최종 3회 미관측 (**UNRESOLVED**).
- 동일 코드 3회: **13/14**, **13/18**, **13/28** — 3회 연속 0 FAIL **미달**.
- Corrupt **14/14** 3회.
- 실제 Windows 11/DPI/Snap/Alt+Tab **UNVERIFIED**.
- unsigned MSIX `0x80073D2C` **UNVERIFIED/BLOCKED**.
