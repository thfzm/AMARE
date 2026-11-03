# AMARE 개발 가이드

- 문서 상태: **Phase 1 스캐폴딩** — Rust/C#/Android 스캐폴드가 존재하며, 실제 브라우저 기능은 미구현입니다.
- 작성일: 2026-11-03
- 관련 문서: [아키텍처](ARCHITECTURE.md), [기술 스택](TECH_STACK.md), [로드맵](ROADMAP.md)

---

## 1. 현재 상태

- Phase 0: 기술 조사/설계 승인 완료.
- Phase 0.5: Linux 실행 가능 항목만 검증 통과. Windows/Android/DRM/성능은 미검증.
- Phase 1: Rust workspace, C ABI, C# interop test, WinUI 3/WebView2 스캐폴드, Android Compose 스캐폴드, engine contracts, CI가 존재합니다.
- **실제 브라우저 기능(탭/주소창/세션/다운로드 등)은 아직 구현하지 않았습니다.**
- 결과: [PHASE0_5_REPORT.md](PHASE0_5_REPORT.md), [PHASE1_SCAFFOLD.md](PHASE1_SCAFFOLD.md)

---

## 2. 저장소 구조

```
AMARE/
├─ README.md
├─ docs/
│  ├─ TECH_STACK.md
│  ├─ ENGINE_COMPARISON.md
│  ├─ ARCHITECTURE.md
│  ├─ SECURITY_DESIGN.md
│  ├─ ROADMAP.md
│  ├─ FEATURES.md
│  ├─ DEVELOPMENT.md
│  └─ spikes/
├─ apps/
│  ├─ windows/                 # C# WinUI 3
│  └─ android/                 # Kotlin/Compose
├─ crates/
│  ├─ amare-core/
│  ├─ amare-store/
│  ├─ amare-services/
│  ├─ amare-ai/
│  ├─ amare-agent/
│  ├─ amare-security/
│  ├─ amare-update/
│  ├─ amare-platform/
│  ├─ amare-ffi/
│  └─ amare-testkit/
├─ engine/
│  ├─ contracts/               # 공유 이벤트/액션 스키마
│  └─ README.md
├─ design/
│  ├─ tokens/
│  └─ icons/
├─ tools/
│  ├─ packaging/
│  ├─ signing/
│  └─ ci/
├─ tests/
│  ├─ fixtures/
│  ├─ extraction/
│  ├─ injection/
│  └─ performance/
└─ .github/workflows/
```

### 소유권 경계

| 경계 | 규칙 |
|---|---|
| `crates/` | UI/엔진 타입을 import하지 않는다. 플랫폼 의존은 trait 뒤로 숨긴다. |
| `engine/contracts` | 언어 중립 스키마. C#/Kotlin/Rust가 함께 사용한다. |
| `apps/windows` | WebView2/WinUI 타입은 여기서만 사용한다. |
| `apps/android` | GeckoView/Android 타입은 여기서만 사용한다. |
| `design/tokens` | UI 토큰의 단일 원천. 코드 생성으로 C#/Kotlin에 반영한다. |

---

## 3. 개발 환경

### 공통

- Git
- Rust stable (edition 2024), `rust-toolchain.toml`로 고정
- Python 3(빌드 스크립트/코드 생성용, 선택)
- GitHub CLI(릴리스/이슈)

### Windows

- Windows 11
- Visual Studio 2022/2026 Build Tools 또는 Visual Studio with:
  - .NET Desktop Development
  - Windows App SDK / WinUI 3
  - C++ Desktop Development(선택: 네이티브 interop)
- .NET LTS SDK
- Windows SDK
- WebView2 Runtime(Windows 11 기본)
- WiX Toolset(설치 프로그램, Phase 2)
- Authenticode 코드 서명 인증서(배포 시)

### Android

- Android Studio
- JDK 17+ (Gradle 요구 버전에 맞춤)
- Android SDK: `minSdk 29`, `targetSdk` 최신 Play 요구사항(2026년 기준 API 36)
- NDK(FFI/JNI 사용 시)
- 실제 기기(에뮬레이터는 GPU/DRM/성능 검증에 부적합)

### macOS/Linux

- 코어 개발/CI용. Windows/Android 제품 빌드는 해당 OS/CI 러너에서 수행.

---

## 4. 빌드/테스트 명령

> 현재 스캐폴딩에서 실행 가능한 명령입니다. Windows/Android 항목은 해당 환경이 필요합니다.

### Rust

```bash
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --workspace
cargo deny check          # 라이선스/취약점/중복 (설정 후)
```

### Windows

```bash
dotnet restore apps/windows/AMARE.Windows.sln
dotnet build apps/windows/AMARE.Windows.sln -c Debug
dotnet test apps/windows/AMARE.Tests/AMARE.Tests.csproj
```

### Android

```bash
./gradlew :app:assembleDebug
./gradlew :app:testDebugUnitTest
./gradlew :app:connectedDebugAndroidTest   # 실제 기기/에뮬레이터
```

### 전체

```bash
./tools/ci/check.sh        # fmt/lint/test (플랫폼별 CI에서 분리 실행)
```

---

## 5. 코딩 규칙

### Rust

- `rustfmt` + `clippy -D warnings`.
- `unsafe`는 FFI/플랫폼 경계에 한정하고, SAFETY 주석 필수.
- 에러는 `thiserror` 계열의 명시적 enum. FFI 경계에서는 에러 코드로 변환.
- `unwrap`/`expect`는 테스트/불변식이 증명된 곳에만. 프로덕션 경로 금지.
- 시간/난수/파일/네트워크/키체인은 trait로 주입해 테스트 가능하게.
- SQL은 마이그레이션 파일로만 변경. 런타임 문자열 조립 금지.
- 비밀/토큰은 `secrecy`/`zeroize`를 사용하고 로그에 남기지 않는다.

### C# (Windows)

- nullable enable, warnings as errors.
- UI 스레드에서 블로킹 호출 금지. `async`/`await` + `CancellationToken`.
- WebView2 이벤트는 UI 스레드로 마샬링. 코어 콜백은 큐로 전달.
- 예외는 사용자 메시지 + 진단 코드로 변환.
- XAML 리소스/토큰은 `design/tokens` 생성물을 사용.

### Kotlin (Android)

- `ktlint`/`detekt`.
- Compose 상태는 단방향 데이터 흐름. ViewModel에서 코어 이벤트를 구독.
- GeckoView 세션은 생명주기와 함께 정리. `GeckoRuntime`은 싱글턴.
- 메인 스레드 블로킹 금지. 코어 호출은 IO 디스패처.

### 공통

- 주석은 "왜"를 설명. 코드가 "무엇"을 하는지는 이름으로.
- 기능 플래그는 설정/빌드 상수로 관리하고, 죽은 코드를 남기지 않는다.
- 사용자에게 보이는 문자열은 리소스 파일로 분리(한국어/영어).
- 접근성 레이블/포커스 순서를 코드 리뷰 체크리스트에 포함.

---

## 6. Rust/네이티브 경계와 FFI 규칙

### 6.1 책임 분리

- **Rust:** 도메인/유스케이스, SQLite/FTS5/blob, AI 오케스트레이션, Agent 정책/원장, 암호/vault, 추출/검색, 업데이트/백업 검증.
- **C#/Kotlin:** UI/창/탭, 엔진 어댑터, 플랫폼 서비스, 다운로드/권한/설정 UI, 탭 절전/입력 주입, 설치 실행.
- 간단한 UI 상태/애니메이션/폼 검증은 Rust로 보내지 않습니다. FFI는 "데이터/정책/장시간 작업"에만 사용합니다.

### 6.2 FFI 구조

- Rust → Windows: C ABI(`amare-ffi`) + C# P/Invoke(ClangSharp/수동 래퍼).
- Rust → Android: UniFFI Kotlin 바인딩(공식 Kotlin 지원) 우선.
- 계약은 `engine/contracts`와 별도로 `amare-ffi`의 버전 고정 C 헤더/UDL로 관리합니다.
- 문자열은 UTF-8 + 길이, 소유권/해제 규칙 명시. 큰 payload는 blob handle/공유 메모리.
- 콜백은 UI 스레드로 직접 호출하지 않고 큐에 넣습니다.

### 6.3 비동기/취소/오류

```text
UI → core: command { request_id, kind, payload }
core → UI: event { request_id, kind: progress|result|error, payload }
```

- 모든 장시간 작업(AI/추출/인덱싱/검색)은 `request_id` + 취소 토큰 + 타임아웃을 가집니다.
- 이벤트 순서는 같은 `tab_id`/`request_id` 내에서 보장합니다.
- 오류: `AmareError { code, message, retryable, details, trace_id }` → C# 예외/Kotlin Result로 매핑.
- Rust panic은 `catch_unwind`로 차단하고 에러 코드로 변환합니다.
- 백프레셔: UI가 처리하지 못한 이벤트는 큐에 쌓지 않고 최신 상태로 병합/드롭 정책을 명시합니다.
- ABI/계약 버전을 명시하고, 앱/엔진 업데이트 후 호환성 테스트를 수행합니다.

예시(개념):

```rust
#[repr(C)]
pub struct AmareString { pub ptr: *mut u8, pub len: usize }

#[no_mangle]
pub extern "C" fn amare_core_create(config_json: *const u8, len: usize, out: *mut *mut AmareCore) -> i32 {
    // validate, catch_unwind, return error code
}
```

> 실제 시그니처는 Phase 1에서 확정합니다. 위 코드는 구현이 아닙니다.

---

## 7. 테스트 전략

| 계층 | 테스트 |
|---|---|
| Rust 코어 | 단위/통합, 정책 property test, DB 마이그레이션, 암호화 |
| 엔진 어댑터 | 로컬 HTTP fixture, 탐색/이벤트/권한/다운로드 |
| Windows UI | 빌드 스모크, WebView2 통합, 접근성/키보드 |
| Android | unit + instrumentation, 실제 기기 스모크 |
| AI | 추출 골든, 인용 매핑, 토큰 예산, 제공자 모킹 |
| Agent | 도구/권한/승인/중지/개입, 인젝션 코퍼스 |
| 성능 | 콜드 스타트, 탭 전환, 30탭 메모리, 광고 차단 오버헤드 |
| 보안 | 업데이트 서명, 백업 암호화, 권한 우회, 내부 페이지 XSS |
| 프로필 격리 | 프로필 간 쿠키/기록/설정/다운로드 누출, UDF 삭제/복구 |
| 라이선스/배포 | WebView2/WinUI 재배포, 고지, 설치/부트스트랩 |

### 테스트 픽스처

- `tests/fixtures/`: 로그인/폼/표/iframe/동영상/다운로드/인증서 오류/리다이렉트.
- `tests/extraction/`: 한국어/영어 문서, 광고/메뉴/푸터, PDF 텍스트.
- `tests/injection/`: 직접/간접/숨김 텍스트/다국어 프롬프트 인젝션.
- `tests/performance/`: 30탭 시나리오, 필터 목록, 긴 문서.

### 수동 매트릭스

- 상위 50개 사이트 + 국내 20개 사이트.
- 로그인/2FA/결제/파일 업로드/다운로드.
- YouTube/Spotify/Netflix/Disney+ (가능 범위).
- 은행/공공기관(ActiveX/보안 프로그램 요구 사이트는 한계 명시).

---

## 8. 브랜치/커밋/리뷰

- `main`은 항상 빌드 가능.
- 기능 브랜치: `feat/<scope>`, 수정: `fix/<scope>`, 문서: `docs/<scope>`.
- 커밋은 논리 단위로 작게. 메시지는 "무엇을/왜"를 포함.
- PR 체크리스트:
  - [ ] 테스트/린트 통과
  - [ ] 보안 영향 검토(권한/데이터/로그)
  - [ ] 성능 영향(탭/메모리/시작)
  - [ ] 접근성/키보드
  - [ ] 문서 갱신
  - [ ] 비밀/키/민감 데이터 미포함

---

## 9. 릴리스/서명

### Windows

1. 버전/체인지로그 확정.
2. Rust/C# 릴리스 빌드.
3. 설치 프로그램(WiX/Burn) 생성.
4. Authenticode 서명 + SHA-256.
5. 업데이트 매니페스트 Ed25519/minisign 서명.
6. GitHub Release 업로드.
7. 클린 Windows 11 VM에서 설치/업데이트/롤백 리허설.

### Android

1. `versionCode`/`versionName` 증가.
2. release keystore로 서명(키는 오프라인/CI 시크릿).
3. APK/AAB + SHA-256.
4. GitHub Release 업로드.
5. 실제 기기에서 서명/업데이트/권한 리허설.

### 채널

- `stable`, `beta`, `nightly`(개발자용). 매니페스트에 채널 명시.
- 정식 릴리스와 테스트 빌드를 혼동하지 않도록 UI에 표시.

---

## 10. 문서 규칙

- 설계 변경 시 해당 문서와 ADR을 갱신.
- 스파이크 결과는 `docs/spikes/YYYY-MM-DD-<topic>.md`에 기록.
- "구현됨"과 "계획"을 명확히 구분.
- 보안/개인정보 변경은 `SECURITY_DESIGN.md`에 반영.
- 사용자에게 보이는 한계는 `FEATURES.md`에 반영.

---

## 11. Phase 1 시작 시 첫 작업

### 승인 직후(제품 코드 전 검증)

- WebView2/WinUI 3 라이선스·재배포 조건 검토
- DRM/VMP 실제 서비스 테스트
- 업데이트 서명/설치 리허설
- 프로필/data 격리 테스트 설계
- i5-7500/16GB 메모리 측정 계획
- 각 기능 테스트/실패 대안 표 확정

### MVP Core 구현 순서

1. 저장소 스캐폴딩(Rust workspace, WinUI solution, contracts).
2. `amare-ffi` 최소 ABI(버전/에러/이벤트/취소) + C# 스모크.
3. WebView2 호스트/탭/프로필 스파이크:
   - 30탭 메모리/전환(S1)
   - 프로필/쿠키/캐시/다운로드/권한/기록(S2)
   - DRM/VMP(S3)
   - 업데이트/서명(S4)
4. SQLite 스키마 v1 + 세션 저널.
5. 탭/주소창/검색/뒤로·앞으로·새로고침 최소 UI.
6. 북마크/방문 기록/다운로드/설정/세션 복원.
7. 기본 보안/권한/HTTPS/트래킹.
8. 설치/업데이트 확인/서명 검증.
9. (선택) 읽기 전용 AI 패널.
10. CI(fmt/lint/test/build) + 로컬 fixture 서버.

---

## 12. 참고

- [기술 스택](TECH_STACK.md)
- [엔진 비교](ENGINE_COMPARISON.md)
- [아키텍처](ARCHITECTURE.md)
- [보안 설계](SECURITY_DESIGN.md)
- [로드맵](ROADMAP.md)
