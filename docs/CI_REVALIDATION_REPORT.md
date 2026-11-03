# AMARE CI 재검증 보고서

- 작성일: 2026-11-03
- 기준 commit: `9b4ac03db9acde2dea252434902d7ebf8b12820a` (CI fix + diagnostics)
- 저장소: `https://github.com/thfzm/AMARE`
- branch: `master`
- 이번 문서 갱신 시점: 추가 Windows 진단 수정은 local commit 예정, **push는 인증 없음으로 미실행**

---

## A. 전체 요약

| 항목 | 결과 |
|---|---|
| 실제 GitHub Actions 실행 확인 | **PASS (조회)** |
| Linux CI | **PASS** — 재실행 run confirmed |
| Windows CI | **FAIL** — 재실행 run still failure |
| Android CI | **UNVERIFIED** — 수동 전용 workflow, 실행 없음 |
| Linux 로컬 재검증 | **PASS** |
| Windows 로컬 재검증 | **UNVERIFIED** (Ubuntu ARM64에서 WinUI/WebView2 실행 불가) |
| 발견한 실제 CI 문제 | **5** |
| 수정한 문제 | **5** |
| 수정 후 GitHub Actions 재실행 | Linux **PASS**, Windows **FAIL** |
| Windows 내부 step 로그 | 인증 없음으로 Artifact/full log 확인 불가 |

실제 실행:

- 초기 Linux run: `41665695890` — failure
- 초기 Windows run: `41665695751` — failure
- 1차 수정 후 Linux run `41669251118` — **success**
- 1차 수정 후 Windows run `41669251131` — **failure**
- 2차 수정 후 Linux run `41678971283` — **success**
- 2차 수정 후 Windows run `41678971378` — **failure** (원인 확인)
- Android run: **없음**

---

## B. GitHub Actions 분석

### B-1. Linux CI

최신 결과: **PASS** (`41669251118`)

초기 실패 분석:

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
- GitHub Actions 재실행: **PASS** (`41669251118`)
- backup tests/CI/benchmark도 수정 workflow에 포함되어 실행됨

### B-2. Windows CI

최신 결과: **FAIL** (`41678971378`) — full log/artifact 인증 확보 후 내부 원인 확인

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

확인된 내부 실패:

1. `rust_clippy` — Windows DPAPI `CryptProtectData`/`CryptUnprotectData`에 불필요한 `&mut input` 사용
   - Windows target에서만 clippy `unnecessary_mut_passed` 발생
2. `webview2_local_navigation`, `profile_isolation`, `cdp_dom_click_input_scroll`
   - `WebViewHelpers.EvalAsync`가 CDP `Runtime.evaluate` 응답 구조를 잘못 가정
   - `JsonElement.GetProperty` KeyNotFoundException
3. PowerShell 최종 aggregation
   - `[switch]$CI` 파라미터와 `$ci = Get-Content ...` 변수 이름이 충돌
   - SwitchParameter 변환 오류로 trap 진입

추가 확인:

- `winui_restore` / `winui_build`: **PASS** (0 warnings, 0 errors)
- `windows_probe_build`: **PASS**
- C# interop: **46/46 PASS**
- WebView2 HTTPS: PASS
- 10-tab lifecycle: PASS
- Windows Runner caption: **Microsoft Windows Server 2025 Datacenter** → Windows 11 실기기가 아님
- `windows_11` 판정을 build만이 아니라 Server caption 제외로 수정

수정:

- DPAPI `&mut input` → `&input`
- `EvalAsync`를 CDP 응답의 envelope/직접 result 양쪽을 처리하도록 수정
- PowerShell `$ci` → `$ciData`로 변경
- Windows Server를 Windows 11로 오인하지 않도록 `windows_11` 판정 수정

재검증:

- Windows cross-target clippy: **PASS**
- Windows compile/runtime 실제 재검증: **미실행** (다음 push 후)
- Windows 11 실기기: **UNVERIFIED**

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
| Windows target clippy (`amare-platform`) | **PASS** | cross-target |
| C# Core build | **PASS** | Linux |
| C# Interop | **PASS** | **46/46** |
| Storage | **PASS** | `storage-ci` 10/10 |
| Backup | **PASS** | `backup-ci` 10/10 |
| Services | **PASS** | `service-ci` 4/4 |
| C ABI/header | **PASS** | local link smoke |
| actionlint | **PASS** | 3 workflows |
| shellcheck | **PASS** | `tools/verify-linux.sh` |
| PowerShell parser | **PASS** | `tools/verify-windows.ps1` |
| Linux CI | **PASS** | `41678971283` |
| Windows CI | **FAIL** | `41678971378`, 원인 확인 및 수정 완료 |
| Android CI | **UNVERIFIED** | workflow_dispatch only, run 없음 |

---

## D. 수정 파일 목록

| 파일 | 이유 | 내용 |
|---|---|---|
| `.gitignore` | Rust `src/bin` source가 Git에서 제외됨 | `bin/`/`obj/`를 .NET output 경로로 한정 |
| `tools/windows-verify/WebViewHelpers.cs` | WinForms WebView2 API compile error | `CreateAsync` 사용, `CoreWebView2.Close` 제거 |
| `.github/workflows/ci-linux.yml` | backup CI 단계 누락 | backup tests/CI/benchmark 추가 |
| `docs/CI_REVALIDATION_REPORT.md` | 결과 보고 | 이 문서 |
| `crates/amare-platform/src/windows_dpapi.rs` | Windows clippy 실패 | 불필요한 mutable reference 제거 |
| `tools/windows-verify/WebViewHelpers.cs` | CDP 응답 파싱 실패 | envelope/direct result tolerant parser |
| `tools/verify-windows.ps1` | PowerShell 변수 충돌/Server 판정 | `$ciData`, Server 제외 windows_11, annotation |
| `tools/windows-verify/Phase21Probes.cs` | popup probe 안정화 | trusted click 사용 |

---

## E. 미해결 문제

1. 수정 사항은 아직 commit/push 전입니다. 다음 Windows run에서 probe/aggregation 수정 결과를 확인해야 합니다.
2. Android는 manual-only라 실제 빌드가 없습니다.
3. WindowsAppSDK/WebView2 Runtime 패키징·고지(R-03)는 별도입니다.
4. crate 배포 path dependency version(R-05)은 별도입니다.
5. Windows 11 실기기 검증은 별도입니다.

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

Linux CI는 `src/bin` 누락 수정 후 실제 run에서 **PASS**했습니다. Windows CI는 compile/WinUI build까지 통과했고, 남은 실패 원인을 full log/artifact로 확인했습니다: Windows DPAPI clippy, CDP 응답 파싱, PowerShell `$CI` 변수 충돌입니다. 세 원인을 수정했으나 아직 push 후 재실행하지 않았습니다.

Windows CI 최신 결과는 **FAIL(수정 전)**, Android CI는 **UNVERIFIED**입니다.
