# AMARE CI 재검증 보고서

- 작성일: 2026-11-03
- 기준 commit: `a7a2875af749e29e49391c7fb1b21f3c610c6e4f`
- 저장소: `https://github.com/thfzm/AMARE`
- branch: `master`
- 이번 단계에서 commit/push/remote 변경: **수행하지 않음**

---

## A. 전체 요약

| 항목 | 결과 |
|---|---|
| 실제 GitHub Actions 실행 확인 | **PASS (조회)** |
| Linux CI | **FAIL** — 실제 실행 |
| Windows CI | **FAIL** — 실제 실행 |
| Android CI | **UNVERIFIED** — 수동 전용 workflow, 실행 없음 |
| Linux 로컬 재검증 | **PASS** |
| Windows 로컬 재검증 | **UNVERIFIED** (Ubuntu ARM64에서 WinUI/WebView2 실행 불가) |
| 발견한 실제 CI 문제 | **3** |
| 수정한 문제 | **3** |
| 수정 후 GitHub Actions 재실행 | **미실행** (commit/push 금지) |

실제 실행:

- Linux run: `41665695890` — failure
- Windows run: `41665695751` — failure
- Android run: **없음**

---

## B. GitHub Actions 분석

### B-1. Linux CI

기존 설정:

- `ubuntu-24.04`
- Rust fmt/clippy/test
- service tests + service-ci
- storage tests + storage-ci + benchmark
- C ABI / C header / C# interop
- **backup tests/CI/benchmark 단계 없음**

실패 위치:

- `Service layer integration report` (step 12)
- exit code **101**
- 이후 storage/backup/C ABI/C# interop 단계는 skipped

근본 원인:

`.gitignore`의 광범위한 `bin/` 규칙이 Rust source 디렉터리까지 제외했습니다.

초기 commit에 다음 source가 빠졌습니다.

```text
crates/amare-services/src/bin/service-ci.rs
crates/amare-services/src/bin/service-benchmark.rs
crates/amare-store/src/bin/storage-ci.rs
crates/amare-store/src/bin/storage-benchmark.rs
crates/amare-store/src/bin/backup-ci.rs
crates/amare-store/src/bin/backup-benchmark.rs
```

GitHub checkout에서 `cargo run -p amare-services --bin service-ci`를 실행하면:

```text
error: no bin target named `service-ci` in `amare-services` package
```

로 실패합니다. 로컬에는 파일이 남아 있어 통과했지만, GitHub clone에는 없었습니다.

수정:

- `.gitignore`의 `bin/`, `obj/`를 .NET output 디렉터리로 한정
  - `apps/**/bin/`, `apps/**/obj/`
  - `tools/**/bin/`, `tools/**/obj/`
- `crates/*/src/bin`은 Git 포함 대상으로 복원
- Linux CI에 누락되어 있던 backup tests/CI/benchmark 단계 추가

재검증:

- 깨끗한 archive + source bin을 포함한 임시 checkout에서 `service-ci`, `storage-ci`, `backup-ci` 모두 **PASS**
- 로컬 `verify-linux.sh`: **EXIT 0**
- GitHub Actions 재실행: **미실행** (commit/push 금지)

### B-2. Windows CI

기존 설정:

- `windows-latest`
- .NET 8, Rust MSVC target, MSBuild
- WebView2 Runtime install
- `verify-windows.ps1 -CI`
- WinUI/WindowsVerify/WebView2 probes/Artifact upload

실패 위치:

- `Run Windows verification` — exit 1

실제 compile 오류:

```text
tools/windows-verify/WebViewHelpers.cs:13
'CoreWebView2Environment' does not contain a definition for 'CreateWithOptionsAsync'

tools/windows-verify/WebViewHelpers.cs:130
'CoreWebView2' does not contain a definition for 'Close'
```

원인:

- WinForms `Microsoft.Web.WebView2` package의 `CoreWebView2Environment` API는 `CreateAsync(string, string, CoreWebView2EnvironmentOptions)`를 사용
- WinForms `CoreWebView2`에는 `Close()`가 없음
- `tools/windows-verify`는 WinUI 프로젝트가 아니라 .NET WinForms/net462 WebView2 표면을 사용

수정:

- `CreateWithOptionsAsync` → `CreateAsync(null, userDataFolder, null)`
- `CoreWebView2.Close()` 제거
- `WebView2.Dispose()`로 정리

추가 영향:

- 초기 commit에서 누락된 `src/bin` 때문에 Windows 검증 스크립트의 service/storage/backup CI 단계도 실패할 상태였습니다.
- `.gitignore` 수정으로 함께 해결했습니다.

재검증:

- Windows compile/runtime: **UNVERIFIED** (Ubuntu ARM64에서 WinUI/WindowsVerify 실행 불가)
- 실제 Windows 11/Windows Runner 재실행: **미실행**

### B-3. Android CI

확인:

- `.github/workflows/ci-android.yml`은 remote default branch에 존재
- `actionlint`: PASS
- trigger: `workflow_dispatch` only

원인:

- push/PR 자동 실행 조건이 없음
- 따라서 이번 push에서 Android run이 생성되지 않음
- Android는 Phase 5 manual scaffold이며 SDK/GeckoView 버전 고정 전 자동화하지 않는 기존 설계

수정:

- 없음. 수동 전용 정책 유지.
- 실제 Android 빌드: **UNVERIFIED**

---

## C. 테스트 결과

| 검증 항목 | 결과 | 비고 |
|---|---|---|
| Rust fmt | **PASS** | 로컬 |
| Rust clippy | **PASS** | `-D warnings` |
| Rust check | **PASS** | workspace |
| Rust tests | **PASS** | **138 tests** |
| C# Core build | **PASS** | Linux |
| C# Interop | **PASS** | **46/46** |
| Storage | **PASS** | `storage-ci` 10/10 |
| Backup | **PASS** | `backup-ci` 10/10 |
| Services | **PASS** | `service-ci` 4/4 |
| C ABI/header | **PASS** | local link smoke |
| actionlint | **PASS** | 3 workflows |
| shellcheck | **PASS** | `tools/verify-linux.sh` |
| PowerShell parser | **PASS** | `tools/verify-windows.ps1` |
| Linux CI | **FAIL (수정 전 실제 run)** / 재실행 UNVERIFIED | 빈 source bin 누락 |
| Windows CI | **FAIL (수정 전 실제 run)** / 재실행 UNVERIFIED | WebViewHelpers compile error |
| Android CI | **UNVERIFIED** | workflow_dispatch only, run 없음 |

---

## D. 수정 파일 목록

| 파일 | 이유 | 내용 |
|---|---|---|
| `.gitignore` | Rust `src/bin` source가 Git에서 제외됨 | `bin/`/`obj/`를 .NET output 경로로 한정 |
| `tools/windows-verify/WebViewHelpers.cs` | WinForms WebView2 API compile error | `CreateAsync` 사용, `CoreWebView2.Close` 제거 |
| `.github/workflows/ci-linux.yml` | backup CI 단계 누락 | backup tests/CI/benchmark 추가 |
| `docs/CI_REVALIDATION_REPORT.md` | 결과 보고 | 이 문서 |

---

## E. 미해결 문제

1. 수정 사항이 아직 commit/push되지 않아 GitHub Actions 재실행 전입니다.
2. Windows compile/runtime 결과는 Windows Runner에서 재검증해야 합니다.
3. Android는 manual-only라 실제 빌드가 없습니다.
4. 초기 commit 자체에는 `src/bin` 6개가 없습니다. 새 commit에 반드시 포함해야 합니다.
5. WindowsAppSDK/WebView2 Runtime 패키징·고지(R-03)는 별도입니다.
6. crate 배포 path dependency version(R-05)은 별도입니다.

---

## F. 실기기 검증 필요 항목

### Windows 11

- WinUI 3 Release build/실행
- WebView2 Environment 생성 및 실제 페이지 로딩
- back/forward/reload/stop/new-window/download
- 프로필별 쿠키/localStorage/IndexedDB 분리
- 프로필 전환/삭제와 WebView2 process 정리
- 세션 복원
- IME/DPI/멀티모니터/키보드
- 5/10/20탭 메모리

### Android

- Android SDK/JDK/Gradle build
- GeckoView 초기화
- 에뮬레이터/ARM64 기기 실행
- Android Keystore

---

## 결론

Linux CI 실패의 직접 원인은 **초기 commit에서 `src/bin` source가 `.gitignore`의 `bin/` 규칙으로 누락된 것**입니다. Windows CI는 여기에 더해 **WinForms WebView2 API 사용 오류**가 있었습니다. 두 문제를 최소 수정했고 Linux 로컬 검증은 통과했습니다.

Windows CI와 Android CI는 실제 수정 후 재실행 결과가 없으므로 **UNVERIFIED**입니다.
