# GitHub Actions 기반 Windows 자동 검증

- 작성일: 2026-11-04
- 단계: Phase 2.2 Windows 검증 게이트/Release Candidate
- 목적: Ubuntu에서 개발 → GitHub Push → Windows Runner 빌드/테스트 → Artifacts 확인
- 원칙: **CI에서 검증하지 못한 항목은 PASS로 처리하지 않는다.**
- 현재 상태: Windows 0.56.0 WinUI Debug/Release build + GPUI password security/deletion/recovery/login autofill/browser settings/session restore/downloads/custom titlebar/icon/bookmarks/history/screenshots/Korean IME/exit validation, portable ZIP + updater helper, unsigned MSIX, Kestrel TLS handshake + 실제 self-signed/expired/hostname-mismatch event/dialog+retry/back/other-tab, native drag 9개 조합+cancel+after-close, cross-window 정/역방향, actual AMARE WebSocket tab/window cleanup, video/fullscreen/PiP, SQLite/WAL recovery, 5/10/20/30-tab memory, clean path launch, C#/Rust interop 70/70, GUI 42/42, release preflight **PASS** (Windows Server 2025 Runner). local trusted CA는 CurrentUser trust-store timeout으로 **UNVERIFIED**, CA chain/custom-trust simulation은 **PASS**, native tear-off+re-merge **PASS**, 30분 keyboard-only stress **SUCCESS**, handle type31/type8 증가 추세 **UNVERIFIED**, 빠른 연속 drag/tear-off/이동 중 창 종료/Windows 11 GUI도 **UNVERIFIED**입니다. Linux **PASS**. Android CI는 0.14.0 최종 코드 `9110ddc`에서 **SUCCESS**했으며 Windows-only 변경에서는 path filter로 실행되지 않습니다.
- 0.52.0 최종 코드 검증 run: [Windows #42656165390](https://github.com/thfzm/AMARE/actions/runs/42656165390) **success**, [Linux #42656165445](https://github.com/thfzm/AMARE/actions/runs/42656165445) **success**, [GPUI #42656165553](https://github.com/thfzm/AMARE/actions/runs/42656165553) **success**, Android [0.14.0 #41952767737](https://github.com/thfzm/AMARE/actions/runs/41952767737) **success**
- 0.52 GPUI Windows run: [#42654159337](https://github.com/thfzm/AMARE/actions/runs/42654159337) **success** (WebView2 download progress/complete/failure, actual 0.52 screenshots, download/ library, titlebar/desktop captures; download 5/0/1, library 13/0/1, input 26/0/3)
- 0.53.0 최종 코드 검증 run: [Windows #42720515760](https://github.com/thfzm/AMARE/actions/runs/42720515760) **success**, [Linux #42720516059](https://github.com/thfzm/AMARE/actions/runs/42720516059) **success**, [GPUI #42720515770](https://github.com/thfzm/AMARE/actions/runs/42720515770) **success** (single-window session restore, `15`/`16` screenshots, download restart `12`, library 18/0/0, download 7/0/1, input 26/0/3, tab 20/0/6)
- 0.54.0 최종 코드 검증 run: [Windows #42761911386](https://github.com/thfzm/AMARE/actions/runs/42761911386) **success**, [Linux #42761911384](https://github.com/thfzm/AMARE/actions/runs/42761911384) **success**, [GPUI #42761911210](https://github.com/thfzm/AMARE/actions/runs/42761911210) **success** (settings UI/search/reset/restart persistence, two-window restore, `11`/`12` screenshots; settings 18/0/2, library 18/0/0, download 7/0/1, input 25/0/4, tab 20/0/6)
- 0.55.0 최종 코드 검증 run: [Windows #42819548216](https://github.com/thfzm/AMARE/actions/runs/42819548216) **success**, [Linux #42819548309](https://github.com/thfzm/AMARE/actions/runs/42819548309) **success**, [GPUI #42822606900](https://github.com/thfzm/AMARE/actions/runs/42822606900) **success** (DPAPI vault/login autofill, password 18 PASS/0 FAIL/1 UNVERIFIED, unit 4/4, WinUI 142/0/10/1, actual 0.55 screenshots)
- 0.41 ETL/lifecycle: [Windows #42396103458](https://github.com/thfzm/AMARE/actions/runs/42396103458) **success**; ETL 606,780,794 bytes, 25,734,983 events, CSV 12.3GB
- 30분 keyboard-only stress run: [Windows #42352609389](https://github.com/thfzm/AMARE/actions/runs/42352609389) **success** (1,033 cycles, handles 965→6,736)
- 0.41 lifecycle: idle stable 855→858, tab close 후 E 995/G 1217, 5분 I 1186; ETL artifact ~1.44GB
- AMARE 0.54.0: WinUI GUI **42/42**, WinUI **143 PASS / 0 FAIL / 9 UNVERIFIED / 1 SKIPPED**, GPUI release build, SQLite settings UI/search/reset/restart persistence **PASS**, theme/density/font/tab width actual apply, search/engine/address/download/history/advanced settings, two-window session restore **PASS (forced-exit snapshot)**, actual 0.54 screenshots **31개**, settings **18/0/2**, library **18/0/0**, download **7/0/1**, input **25/0/4**, tab **20/0/6**; DOM/page-state restore, cookies/site permission UI, graceful multi-window exit, download pause/resume/cancel automation, DPI/multi-monitor, restart WebView2 cleanup, MSIX install **UNVERIFIED/BLOCKED**, GitHub Release 0개.
- 상세 분석: [CI 재검증 보고서](CI_REVALIDATION_REPORT.md)

---

## 1. Workflow 목록

| Workflow | 파일 | 실행 조건 |
|---|---|---|
| Linux CI | `.github/workflows/ci-linux.yml` | PR, main/master push, 수동 |
| Windows CI | `.github/workflows/ci-windows.yml` | PR, main/master push, 수동 |
| Android scaffold | `.github/workflows/ci-android.yml` | `apps/android/**`/workflow path push·PR, 수동 |

공통:

- `permissions: contents: read` (최소 권한)
- `concurrency`로 같은 PR/branch의 중복 실행 취소
- GitHub Secrets에 계정/유료 서비스 정보를 저장하지 않음
- 외부 Action은 공식/신뢰 버전 고정 (`actions/*`, `microsoft/setup-msbuild`, `dtolnay/rust-toolchain`)
- 캐시: Cargo registry/target, NuGet packages

---

## 2. Windows CI 구조

`windows-latest` (Windows Server)에서 실행합니다. **Windows 11 실기기와 동일하지 않습니다.** Windows 11 build 22000+ 여부는 `windows-verify-environment.json`의 `windows_11`에 기록하며, Runner가 Windows Server여도 빌드/테스트는 계속 수행합니다.

1. Checkout
2. .NET 8 SDK 설치/확인
3. Rust stable + `x86_64-pc-windows-msvc` target
4. MSBuild 설정
5. Cargo/NuGet 캐시
6. WebView2 Runtime 확인, 없으면 Evergreen Bootstrapper 설치
7. Visual Studio C++ Build Tools/Windows SDK/Rust 확인
8. `tools/verify-windows.ps1 -CI` 실행
   - Rust fmt/clippy/test (workspace + service layer)
   - update/security 로직 테스트
   - SQLite storage tests (`amare-store`, `rusqlite` bundled + MSVC C compiler)
   - Service layer tests (`amare-services`) + `service-ci.json` + `service-benchmark.json`
   - SQLite integration report (`storage-ci.json`, Phase 1.6 Library/AI/Workspace FTS5 포함)
   - SQLite benchmark (`storage-benchmark.json`)
   - Backup/restore tests + integration (`backup-ci.json`, Phase 1.6 데이터 카테고리 포함)
   - Backup/restore benchmark (`backup-benchmark.json`)
   - Windows DPAPI tests (`cargo test -p amare-platform`)
   - FFI build
   - C header compile (MSVC `cl /c`)
   - C# ↔ Rust interop (AMARE.Windows.Core + native service JSON ABI)
   - WinUI 3 restore + Release build (required; Windows only, Linux에서 XAML compiler 실행 불가)
   - Windows probe build
   - WebView2 probes: local HTTP navigation, HTTPS(optional), 10-tab lifecycle, profile isolation, CDP, memory(30탭)
   - Windows 11 실기기 결과는 별도 UNVERIFIED
   - `windows_11=false` Runner 결과를 Windows 11 실기기 PASS로 변환하지 않음
9. Artifacts 업로드 (`if: always()`)

### 통합 결과

`windows-verification.json`은 PowerShell step, WebView2 probe, C# interop, service/storage/backup CI case를 하나의 배열로 합칩니다. 각 항목은 `name/status/executed/duration_ms/environment/error/log_path`를 포함하고 status는 `PASS/FAIL/UNVERIFIED/SKIPPED`만 사용합니다. `windows-verify-summary.json`은 기존 요약 형식을 유지합니다.

### `verify-windows.ps1 -CI` 동작

- `AMARE_VERIFY_HEADLESS=1`: GUI를 숨기고 자동 실행 후 종료
- `AMARE_VERIFY_MANUAL=0`: 사용자 개입 테스트를 `UNVERIFIED`
- `AMARE_VERIFY_MEMORY=0`: 30탭 메모리 테스트를 `UNVERIFIED`
- `AMARE_VERIFY_DRM=0`: DRM 매트릭스를 `UNVERIFIED` (CI Runner DRM/VMP는 실기기 대표성 없음)
- `AMARE_VERIFY_OUTPUT=<repo>\artifacts\windows`: 결과/로그 저장
- 각 단계 exit code를 `windows-verify-summary.json`에 기록
- 필수 단계 실패 시 workflow 실패, 가능한 로그/결과는 Artifact로 보존

---

## 3. Ubuntu CI 구조

`ubuntu-24.04`에서 실행합니다.

1. Rust fmt
2. Clippy `-D warnings`
3. Rust workspace tests
4. `amare-services` 서비스 테스트 + `service-ci` + `service-benchmark`
5. `amare-security` 암호화 테스트
6. `amare-update` 서명/롤백 테스트
7. `amare-store` SQLite 테스트
8. Backup/restore tests/CI/benchmark
9. Key protection interface tests (Linux: Android stub, no DPAPI)
10. C ABI build
11. C header link/run smoke test (gcc)
12. C# ↔ Rust interop (Linux 가능 범위)
13. Engine JSON Schema 검증
14. 로그/Artifacts 업로드

Linux에서는 WinUI 3/WebView2/Windows DPAPI/설치 프로그램 테스트를 실행하지 않습니다.

---

## 4. 수동 실행

GitHub UI:

1. Repository → **Actions**
2. **ci-windows** 선택
3. **Run workflow**
4. Branch 선택 후 실행
5. 완료 후 Artifacts 다운로드

CLI:

```bash
gh workflow run ci-windows.yml
gh run list --workflow=ci-windows.yml
gh run watch <run-id>
gh run download <run-id>
```

로컬 Windows에서 동일한 검증:

```powershell
.\tools\verify-windows.ps1 -CI
```

수동 개입/메모리 테스트를 포함하려면:

```powershell
.\tools\verify-windows.ps1
```

---

## 5. Artifacts

Windows Artifact (`windows-verification-<run_id>`):

```text
artifacts/windows/
├─ windows-verify-environment.json
├─ windows-verify-summary.json
├─ windows-verification.json
├─ windows-verify-results.json
├─ interop-results.json
├─ rust_fmt.log
├─ rust_clippy.log
├─ rust_tests.log
├─ update_logic_tests.log
├─ ffi_build.log
├─ c_header.log
├─ rust_windows_target.log
├─ webview2_runtime.log
├─ csharp_core_build.log
├─ storage_tests.log
├─ storage_ci.log
├─ service_tests.log
├─ service_ci.log
├─ service-ci.json
├─ service_benchmark.log
├─ service-benchmark.json
├─ storage-ci.json
├─ storage_benchmark.log
├─ storage-benchmark.json
├─ backup_tests.log
├─ backup_ci.log
├─ backup-ci.json
├─ backup_benchmark.log
├─ backup-benchmark.json
├─ platform_key_tests.log
├─ csharp_rust_interop.log
├─ winui_restore.log
├─ winui_build.log
├─ windows_probe_build.log
└─ webview2_probe_run.log
```

Linux Artifact (`linux-phase0.5-<run_id>`):

```text
artifacts/linux/
├─ rust-fmt.log
├─ rust-clippy.log
├─ rust-tests.log
├─ security-tests.log
├─ update-tests.log
├─ ffi-build.log
├─ c-header.log
├─ storage-tests.log
├─ storage-ci.log
├─ service-tests.log
├─ service-ci.log
├─ service-ci.json
├─ service-benchmark.log
├─ service-benchmark.json
├─ storage-ci.json
├─ storage-benchmark.log
├─ storage-benchmark.json
├─ backup-tests.log
├─ backup-ci.log
├─ backup-ci.json
├─ backup-benchmark.log
├─ backup-benchmark.json
├─ key-protection-tests.log
├─ ci-summary.json
├─ csharp-interop.log
├─ header_link_smoke
└─ interop-results.json
```

---

## 6. 결과 상태

기존 상태 체계를 유지합니다.

- `PASS`: 실행 + 검증 성공
- `FAIL`: 실행 + 검증 실패
- `UNVERIFIED`: 미실행/수동 입력 없음/증거 부족
- `SKIPPED`: 정책상 의도적 미실행 (유료 계정 등)

`windows-verify-results.json`의 각 항목에는 다음이 포함됩니다.

- 테스트 ID/이름
- `executed`
- `status`
- 실행 환경 (`RunReport.Environment`)
- 실패 원인 (`Detail`)
- 로그 경로 (`windows-verify-summary.json` steps)
- 데이터/한계 (`Data`, `Detail`)

**CI에서 실행하지 않은 항목은 PASS가 아닙니다.**

---

## 7. CI vs 실기기 검증 범위

| 항목 | GitHub Actions Windows | Windows 11 실기기 |
|---|---|---|
| WinUI 3 build | 가능 | 가능 |
| WebView2 초기화 | 가능(환경 의존) | 가능 |
| Rust/C# FFI | 가능 | 가능 |
| C ABI header | 가능 | 가능 |
| CDP DOM/클릭/입력/스크롤 | 가능(가상 디스플레이/세션 의존) | 가능 |
| 프로필 격리 | 가능 | 가능 |
| 사용자 개입 감지 | **UNVERIFIED**(자동화 불가) | 수동 |
| 30탭 메모리 | **UNVERIFIED**(부하/정확도) | 목표 하드웨어 |
| i5-7500/16GB/GTX1050 성능 | **UNVERIFIED** | 필수 |
| Netflix/Disney+ | **SKIPPED** | 계정 동의 필요 |
| DRM robustness/VMP | **UNVERIFIED**(정책) | 수동 |
| HDR/특정 GPU HW 디코딩 | **UNVERIFIED** | 실기기 |
| 실제 설치/롤백 | **UNVERIFIED** | Phase 2 |

`windows-latest`는 **Windows Server**이며 GPU/DRM/HDR/사용자 세션이 실기기와 다릅니다.

또한 GitHub-hosted Runner의 세션/데스크톱 제약으로 WinForms/WebView2 headless 프로브가 실패하거나 지연될 수 있습니다. 이 경우 단계 로그를 확인하고, 필요하면 WebView2 windowless/offscreen 호스트 또는 전용 self-hosted Windows runner로 전환합니다. CI 실패를 PASS로 처리하지 않습니다.

---

## 8. 실패 디버깅

1. Actions run → job `windows-verify` 선택
2. 실패 step 로그 확인
3. Artifacts 다운로드
4. `windows-verify-summary.json`의 `steps`/`failures` 확인
5. `windows-verify-results.json`에서 `FAIL`/`UNVERIFIED` 구분
6. 로컬 Windows에서 `.\tools\verify-windows.ps1 -CI` 재현
7. Ubuntu에서 수정 후 다시 Push

자주 발생하는 실패:

| 증상 | 원인 | 조치 |
|---|---|---|
| WinUI build 실패 | Windows SDK/WinAppSDK/플랫폼 불일치 | `dotnet restore`, `-p:Platform=x64`, 패키지 버전 확인 |
| WebView2 probe hang | headless/세션 문제 | `webview2_probe_run.log` 확인, `AMARE_VERIFY_HEADLESS` 동작 확인 |
| DRM matrix FAIL | Runner에 EME/Widevine 없음 | CI 결과를 실기기 검증으로 대체하지 않음 |
| C header FAIL | MSVC/vcvars/헤더 경로 | `c_header.log`, VS Build Tools 확인 |
| interop FAIL | DLL 경로/PATH | `target\debug` PATH, `interop-results.json` 확인 |

---

## 9. 비용/보안

- 공개 저장소 기준 표준 runner 사용, 불필요한 반복 실행은 concurrency로 취소
- 캐시로 빌드 시간 절감
- Secrets 미사용, 유료 계정 로그인 없음
- PR from fork에서도 Secrets를 사용하지 않으므로 노출 위험 없음
- WebView2 설치 파일은 Microsoft 공식 Bootstrapper 링크 사용
- 외부 사이트는 테스트에 사용하지 않음 (DRM 실제 서비스 제외, 수동/SKIPPED)
