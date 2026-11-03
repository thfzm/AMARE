# Phase 0.5 검증 결과 보고서

- 작성일: 2026-11-03
- 상태: **부분 검증 완료** — Linux에서 가능한 항목만 실행, Windows/Android/실제 하드웨어 항목은 **미검증**
- Phase 1 저장소 구현 결과: [PHASE1_SCAFFOLD.md](PHASE1_SCAFFOLD.md) 6장
- 관련 문서: [Phase 0 수정 보고서](PHASE0_REPORT.md), [Phase 1 스캐폴딩](PHASE1_SCAFFOLD.md), [Windows 검증 가이드](WINDOWS_VERIFICATION.md), [보안 설계](SECURITY_DESIGN.md)

> 중요: 현재 개발 환경은 **Linux aarch64**이며 Windows/WebView2/Android SDK/i5-7500 하드웨어가 없습니다. Windows 전용 항목은 추측으로 통과 처리하지 않고 **UNVERIFIED**로 기록했습니다. Windows 검증 하네스는 `tools/windows-verify/`에 준비했습니다.

---

## 0. 한 줄 결론

**Rust 코어/암호/저장소/업데이트 로직/FFI/C#↔Rust 통신은 검증 통과.** 그러나 **WebView2 프로필 격리, CDP 화면 조작, 사용자 개입, 탭 메모리, DRM/VMP, i5-7500 성능, WinUI/Android 빌드**는 이 환경에서 실행할 수 없어 **미검증**입니다. 따라서 실제 브라우저 기능 구현은 **보류**하고 Windows 검증 결과를 기다립니다.

---

## 1. 테스트 환경 및 사양

| 항목 | 값 |
|---|---|
| OS | Ubuntu 24.04.4 LTS (Linux 7.0.0-1011-oracle) |
| CPU | 4 vCPU, Neoverse-N1 (aarch64) |
| RAM | 24,548,036 KiB (약 23GiB) |
| GPU | 해당 없음(서버 환경) |
| Rust | 1.99.1 (2026-10-13) |
| Cargo | 1.99.1 |
| .NET SDK | 8.0.133 |
| C 컴파일러 | gcc 13.3.0 |
| Windows | **없음** |
| WebView2 Runtime | **없음** |
| Android SDK | **없음** |
| 목표 하드웨어(i5-7500/16GB/GTX1050) | **없음** |

**목표 하드웨어 측정 불가.** 아래 성능 수치는 Linux 호스트 값이며 i5-7500/Windows 성능으로 해석하면 안 됩니다.

---

## 2. 테스트별 실행 방법

### 2.1 Linux에서 실행한 명령

```bash
export PATH="$HOME/.cargo/bin:$PATH"

# 정적 검사/테스트
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
cargo test -p amare-update
cargo test -p amare-security

# C ABI 빌드
cargo build -p amare-ffi

# C# ↔ Rust interop
export LD_LIBRARY_PATH="$PWD/target/debug:${LD_LIBRARY_PATH:-}"
dotnet run --project apps/windows/AMARE.Interop.Tests/AMARE.Interop.Tests.csproj

# C 헤더/링크 스모크
gcc -I crates/amare-ffi/include /tmp/amare_header_test.c \
  -L target/debug -lamare_ffi -Wl,-rpath,"$PWD/target/debug" \
  -o /tmp/amare_header_test
/tmp/amare_header_test

# 계약 JSON 검증
python3 - <<'PY'
import json, pathlib
for p in pathlib.Path('engine/contracts').glob('*.json'):
    json.loads(p.read_text())
    print('ok', p)
PY
```

### 2.2 Windows에서 실행해야 하는 명령

```powershell
# Windows 11 + WebView2 Runtime + .NET 8 SDK 필요
.\tools\verify-windows.ps1
```

`tools/verify-windows.ps1`는 Rust 테스트/FFI/Interop, WinUI 3 빌드, WebView2 프로브를 순서대로 실행합니다. WebView2 프로브는 `tools/windows-verify/`의 WinForms 하네스가 담당합니다.

### 2.3 Android

```bash
cd apps/android
gradle wrapper   # 현재 환경에는 gradle/Android SDK가 없음
./gradlew :app:assembleDebug
```

**미실행.** Android SDK가 없어 빌드/실행하지 못했습니다.

---

## 3. 성공/실패 결과

| # | 검증 항목 | 결과 | 근거/비고 |
|---|---|---|---|
| 1 | Rust workspace 테스트 | **PASS** | 32개 테스트 통과(agent 3, ai 2, core 4, ffi 4, security 9, store 3, testkit 1, update 6) |
| 2 | Rust fmt/clippy | **PASS** | `cargo fmt --check`, `clippy -D warnings` 통과 |
| 3 | C ABI 빌드/헤더 | **PASS** | `libamare_ffi.so`, C 헤더 컴파일/실행 성공 |
| 4 | C# ↔ Rust interop | **PASS** | 4/4 통과. version/create/ping/add/null error |
| 5 | FFI 왕복 지연 | **PASS(호스트)** | 10,000회 avg 약 0.9–1.1µs, p99 약 2.3–2.5µs (Linux arm64, run-to-run 변동 있음) |
| 6 | 로컬 데이터 암호화 | **PASS(로직)** | Argon2id + XChaCha20-Poly1305, wrong key/AAD/tamper 실패 확인 |
| 7 | 업데이트 서명/롤백 로직 | **PASS(로직)** | Ed25519 sign/verify/tamper, 다운그레이드 거부, 설치 실패 시 복구, 데이터 보존, 명시적 롤백 확인 |
| 8 | Engine 계약 JSON | **PASS** | 3개 스키마 JSON 파싱 성공 |
| 9 | WinUI 3 restore | **PASS** | `dotnet restore` 성공(Windows App SDK 2.6.5) |
| 10 | WinUI 3 build | **UNVERIFIED / 환경 실패** | Linux에서 `XamlCompiler.exe: Exec format error`; Windows 전용 |
| 11 | WebView2 프로필/세션 격리 | **UNVERIFIED** | Windows/WebView2 없음. 쿠키/localStorage/IndexedDB/CookieManager 하네스 준비 |
| 12 | CDP DOM 접근/화면 조작 | **UNVERIFIED** | 하네스에 `DOM.getDocument` + 클릭 + `Input.insertText` + 스크롤 구현, 미실행 |
| 13 | 사용자 직접 개입 감지 | **UNVERIFIED** | OS 입력 감지 + Agent 유사 루프 중단 하네스 준비, 수동 입력 필요 |
| 14 | 탭 종료/메모리 회수 | **UNVERIFIED** | 30탭 총량/renderer/GPU/utility/시점별 스냅샷 하네스 준비, 미실행 |
| 15 | DRM/코덱 | **UNVERIFIED** | Widevine/PlayReady robustness, 코덱/해상도/powerEfficient/HDR 매트릭스 준비, 실제 재생/유료 서비스 미검증 |
| 16 | 업데이트 패키지 서명/롤백(설치) | **UNVERIFIED** | Rust 트랜잭션 로직(설치 실패/데이터 보존/롤백) 통과, Windows 설치 프로그램은 미구현 |
| 17 | i5-7500/16GB 성능 | **UNVERIFIED** | 목표 하드웨어 없음 |
| 18 | Android 빌드/실행 | **UNVERIFIED** | Android SDK 없음 |
| 19 | WebView2/WinUI 라이선스 | **UNVERIFIED** | 법무 검토 필요 |
| 20 | GitHub workflow 구문 | **PASS** | actionlint 1.7.12 통과 (Linux/Windows/Android workflow) |
| 21 | PowerShell/C# 스크립트 구문 | **PASS** | PowerShell 7.6.9 parser, Roslyn syntax parse |
| 22 | GitHub Actions 실제 실행 | **UNVERIFIED** | 저장소 remote/Actions 접근 권한 없음 |

### 3.1 실제 실행 로그 요약

```text
cargo test --workspace
  32 passed; 0 failed

C# interop:
  PASS version: version=0.1.0
  PASS create_ping_add_destroy: reply=pong:1:pong:hello
  PASS ffi_ping_latency_10000: avg_us≈0.9–1.1 p99_us≈2.3–2.5 (run-to-run)
  PASS null_input_reports_error: error=null string pointer
  SUMMARY passed=4 failed=0

C header:
  version=0.1.0
  ping=pong:1:pong:c-header

WinUI:
  restore: success
  build: XamlCompiler.exe Exec format error (Windows PE on Linux)

Workflow:
  actionlint 1.7.12: PASS
  PowerShell parser 7.6.9: PASS
  C# Roslyn syntax parse: PASS
```

### 3.2 Windows 결과 JSON 정책

Windows 하네스 결과는 `PASS`/`FAIL`/`UNVERIFIED`/`SKIPPED`로 구분하고 `executed` 필드를 함께 기록합니다.

- 실행 실패(exit code ≠ 0)는 **PASS로 변환하지 않습니다.**
- 수동 입력이 없거나 전제조건이 없으면 `UNVERIFIED`입니다.
- 유료 계정/명시적 동의가 필요한 Netflix/Disney+ 테스트는 `SKIPPED`입니다.
- 프로필 격리는 쿠키/document.cookie/localStorage/IndexedDB/CookieManager를 모두 확인합니다.
- CDP는 연결이 아니라 실제 클릭/텍스트 입력/스크롤 결과를 확인합니다.
- 30탭 메모리는 전체 WebView2 프로세스, browser/renderer/GPU/utility, 생성/절전/종료 시점을 기록합니다.
- DRM은 재생 성공/해상도/코덱/하드웨어 가속/HDR을 분리하고, 실제 서비스 재생은 수동/미검증으로 둡니다.

---

## 4. 메모리 및 성능 측정치

### 4.1 측정된 값(Linux 호스트, 목표 하드웨어 아님)

| 항목 | 값 | 비고 |
|---|---:|---|
| `cargo test --workspace` wall | 2.851s | 32 tests, warm build |
| C# interop process wall | 4.295s | dotnet startup 포함 |
| FFI 10k ping avg | 약 0.9–1.1µs | create/ping/free 포함, run-to-run 변동 |
| FFI 10k ping p99 | 약 2.3–2.5µs | Linux arm64 |
| FFI 10k ping max | 0.4–1.3ms | 스케줄링 outlier |
| amare-security 테스트 | 9/9 pass | Argon2id 19MiB/2iterations 포함 |

### 4.2 측정하지 못한 값

- WebView2 첫 인스턴스/탭당 메모리
- 30탭 메모리/전환 p95
- 절전/폐기 후 메모리 회수
- i5-7500 콜드 스타트
- GTX 1050 GPU 가속/DRM 재생
- Android 20탭 메모리

**예상치(공개 자료 기반, 실측 아님):** WebView2 첫 인스턴스 150–250MB, 활성 탭 80–150MB, 30탭 3–5GB. `docs/PHASE0_REPORT.md` 8.1 참고.

---

## 5. 발견된 보안 문제

### 테스트 범위 내

- **발견된 치명적 문제 없음.**
- C ABI는 null 포인터/UTF-8 오류를 거부하고, Rust panic을 경계에서 차단합니다.
- 로컬 암호화는 wrong key/AAD/tamper를 거부합니다.
- 업데이트 서명은 tamper/서명 불일치를 거부하고, 다운그레이드를 거부합니다.
- C# interop는 문자열 소유권을 `amare_string_free`로 해제합니다.

### 설계 보완

- **공식 웹 AI 사이트와 Agent 권한 분리**를 [보안 설계 6.5](SECURITY_DESIGN.md)에 명시했습니다.
  - 웹 AI 사이트는 AMARE 북마크/비밀번호/Library/다른 탭/Agent 도구에 접근할 수 없습니다.
  - AI 전송 데이터는 사용자가 허용한 범위/미리보기/리댁션을 거칩니다.
  - Agent는 capability token/승인/원장으로 별도 관리합니다.

### 미검증 보안 표면

- WebView2 샌드박스/사이트 격리 실제 동작
- CDP 입력과 사용자 입력 구분
- Agent 프롬프트 인젝션 레드팀
- Windows 코드 서명/업데이트 설치
- Android GeckoView 격리/Fission

---

## 6. 미검증 항목

1. WebView2 프로필/세션 격리 (쿠키/UDF/InPrivate)
2. CDP DOM 접근과 실제 클릭/입력/스크롤
3. 사용자 직접 개입 감지와 Agent 일시정지
4. 탭 종료/절전/메모리 회수
5. DRM(PlayReady/Widevine/VMP)와 Netflix/Disney+
6. H.264/AAC/VP9/AV1/HEVC 실제 재생
7. Authenticode/Ed25519 설치/롤백 전체 흐름
8. i5-7500/16GB/GTX1050 성능/메모리
9. WinUI 3 Windows 빌드/실행
10. Android GeckoView 빌드/실행/호환성
11. WebView2/WinUI 라이선스/재배포 법무
12. Web AI 사이트 실제 로그인/세션/약관

---

## 7. 기술 스택 변경 필요성

**현재까지: 변경 필요 없음.**

- Rust 코어/암호/저장소/FFI/C# 통신은 검증 범위에서 정상입니다.
- WinUI/WebView2는 Linux에서 빌드/실행할 수 없어 **판단 보류**입니다.
- Windows 검증에서 다음이 실패하면 설계 변경을 검토합니다.
  - 30탭 메모리/전환 목표 미달 → 탭 폐기 정책 강화 또는 CEF 검토
  - CDP 입력/사용자 개입 감지 실패 → Agent 범위 축소/호스팅 방식 변경
  - DRM/VMP 실패 → 외부 앱 안내, DRM 보장 범위 축소
  - 확장/광고 차단 오버헤드 → 내장 기능/엔진 재검토
- Android는 Phase 5에서 GeckoView vs Android WebView 비교 후 결정합니다.

---

## 8. Phase 1 진행 가능 여부

### 가능한 것(완료)

- 저장소/디렉터리 구조
- Rust workspace/코어/암호/저장소/FFI
- C# ↔ Rust interop
- WinUI 3 프로젝트 초기화(빌드는 Windows 필요)
- WebView2 연결 기본 인터페이스(스캐폴드)
- Android Compose 스캐폴드(빌드 미검증)
- Engine 계약 스키마
- Linux/Windows CI
- Windows 검증 하네스

### GitHub Actions 자동 검증(작성 완료, 실행 미확인)

- `.github/workflows/ci-windows.yml`: Windows Runner에서 WinUI 3/WebView2/FFI/C ABI/프로브 실행, Artifacts 업로드
- `.github/workflows/ci-linux.yml`: Rust/보안/업데이트/C ABI/C# interop/스키마 검증
- `tools/verify-windows.ps1 -CI`: headless 프로브, 수동 개입/메모리/DRM 매트릭스는 `UNVERIFIED`
- 결과: `windows-verify-results.json` 및 단계별 로그 Artifact

**중요:** 현재 저장소에는 GitHub remote/실행 권한이 없어 workflow를 실제 실행하지 못했습니다.
따라서 GitHub Actions 성공을 주장하지 않으며, Windows 결과는 여전히 **미검증**입니다.
자세한 사용법/Artifacts/디버깅은 [GitHub Actions 문서](GITHUB_ACTIONS.md)를 참고하세요.

### Windows 검증 준비(실행 전)

- 전제조건/SDK/WebView2/환경변수/결과 해석: [Windows 검증 가이드](WINDOWS_VERIFICATION.md)
- `tools/verify-windows.ps1`: OS/.NET/Rust/WebView2 확인, 환경 JSON 생성, 각 단계 exit code 기록
- `tools/windows-verify`: 프로필 격리(쿠키/localStorage/IndexedDB), CDP 클릭/입력/스크롤, 사용자 개입 감지, 30탭 메모리, DRM 매트릭스
- 결과 상태: `PASS`/`FAIL`/`UNVERIFIED`/`SKIPPED` + `executed`
- 유료 계정 로그인은 명시적 동의 없이 수행하지 않음

**아직 실행하지 않았습니다.** Windows 결과가 나오기 전까지 이 항목들은 모두 미검증입니다.

### 보류할 것

**실제 브라우저 기능 구현(탭/주소창/세션/다운로드 등)은 보류합니다.** Phase 0.5의 Windows 핵심 검증(프로필 격리, CDP, 사용자 개입, 메모리, DRM, 성능)이 완료되기 전에는 본 구현을 시작하지 않습니다.

### 다음 승인 요청

1. Windows 11 + WebView2 환경에서 `tools/verify-windows.ps1` 실행
2. 결과(`windows-verify-results.json`, 빌드 로그)를 Phase 0.5 보고서에 반영
3. i5-7500/16GB에서 30탭 메모리/전환 측정
4. Netflix/Disney+ 등 DRM/VMP 실제 계정 테스트
5. 결과 검토 후 **Phase 1 실제 브라우저 기능 구현 승인**

---

## 9. 참고

- [Windows 검증 가이드](WINDOWS_VERIFICATION.md)
- [Phase 0 수정 보고서](PHASE0_REPORT.md)
- [Phase 1 스캐폴딩](PHASE1_SCAFFOLD.md)
- [보안 설계](SECURITY_DESIGN.md)
- [개발 가이드](DEVELOPMENT.md)
- [로드맵](ROADMAP.md)
