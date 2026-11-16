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

## 5. 현재 실행 상태 (0.74.0)

- Windows CI [43858573835](https://github.com/thfzm/AMARE/actions/runs/43858573835): **143 PASS / 0 FAIL / 9 UNVERIFIED / 1 SKIPPED**, WinUI UI smoke **48/48**, interop **70/70**.
- Linux CI [43853054122](https://github.com/thfzm/AMARE/actions/runs/43853054122): **SUCCESS**.
- GPUI 3회 완전 반복(`80c2329`): **10 FAIL/6 UNVERIFIED**, **8/2**, **8/2** — 3회 연속 0 FAIL **미달**.
- AddressInput telemetry: enter/submit/focus/dispatch/result/source 단계 분리. navigation regression submit callback **전 case PASS**.
- Corrupt session: **14/14 PASS — 3회 모두**.
- Session persistence core: 2회 PASS — multi-window 2→2, active URL, scroll, geometry, maximized, immediate close, no-resurrection.
- Navigation regression: 10/4, 12/2, 11/3; 남은 실패는 second-window state count/restore fixture.
- Titlebar 별도 workflow: 16/0/0 PASS 2회, window2 hang 1회.
- 실제 Windows 11, DPI 125/150/200%, Snap, Alt+Tab은 **UNVERIFIED**.
- unsigned MSIX 설치는 `0x80073D2C`로 **UNVERIFIED**, ALL_SITE 삭제는 **read-only/BLOCKED**입니다.
