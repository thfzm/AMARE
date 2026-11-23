# AMARE Architecture Decisions — Phase 0

- 문서 상태: **Phase 0 감사 결과 / 승인 대기**
- 작성일: 2026-11-23
- 기준 branch: `master`
- 제품/테스트 HEAD: `49d4962` (`AMARE 0.93 fixture request counters and readiness`)
- 문서 HEAD: `4ea8c82` (`Document AMARE 0.93.0 navigation evidence results [skip ci]`)
- 최신 제품 버전: **0.92.0** (0.93은 fixture/test 변경만 포함, 새 패키지 없음)
- 이 문서는 **결정과 경계를 기록**하며, 제품 코드·DB 스키마·패키지를 변경하지 않는다.

---

## 1. 목적과 범위

AMARE의 최종 목표는 **Windows 11 개인용 메인 브라우저** 완성이다. 이 문서는 그 목표에 맞는
기술 방향과 현재 저장소의 실제 구조를 분리해 기록한다.

이번 Phase 0의 범위:

- 실제 저장소 구조, 실행 진입점, 데이터 경로, C#↔Rust 경계 조사
- WinUI 3 + WebView2를 최종 방향으로 채택하기 위한 근거와 제약 기록
- GPUI 동결 원칙과 보존 범위 확정
- 후속 Phase에서 변경해도 되는 것과 안 되는 것의 기준 수립

이번 Phase 0에서 하지 않는 것:

- 제품 코드 수정, 프레임워크 마이그레이션
- DB 스키마 변경, 패키지 재생성
- GPUI 소스·문서·패키지·증거 삭제
- 공개 GitHub Release 생성, 인증서 생성/서명
- force push, history rewrite, stash 삭제

---

## 2. 판단 근거

이 문서의 결론은 다음 실제 증거에 근거한다.

| 증거 | 결과 | 위치 |
|---|---|---|
| WinUI 기준선 | UI smoke **48/48**, Windows **143 PASS / 0 FAIL / 9 UNVERIFIED / 1 SKIPPED**, interop **70/70**, Linux SUCCESS | CI `45042473578` |
| WinUI 로컬 WebView2 probe | title/url/back/forward/reload/stop/new-window/download PASS | `webview2:webview2_local_navigation` |
| 프로필 격리 | cookies/localStorage/IndexedDB/CookieManager 격리 PASS | `webview2:profile_isolation` |
| 30탭 메모리 | empty 1754MB, content 2074MB, close 후 0MB | `webview2:memory_30_tabs` |
| GPUI 0.93 반복 | **11/13**, **12/10**, **13/10** FAIL/UNVERIFIED | CI `45042412219`, `45042420684`, `45042427495` |
| GPUI reload 증거 | DirectReload **10/10**, Ctrl+R **10/10** (HTTP counter 포함) | CI `45041213160` |
| GPUI titlebar | full PASS **3/5**, crash **0/5** | CI titlebar 5회 |
| 패키지 | WinUI ZIP/MSIX, GPUI ZIP은 0.92.0 그대로 | `artifacts/windows/release/` |

중요 원칙: **소스 코드 존재는 검증이 아니다.** 아래 상태는 코드, 자동 테스트, CI 결과,
GUI 증거를 함께 본 결과다. CI와 실제 Windows 11 실기기 검증은 합산하지 않는다.

---

## 3. 저장소 실제 구조

```text
AMARE/
├─ apps/
│  ├─ windows/
│  │  ├─ AMARE.Windows/            WinUI 3 실행 프로젝트 (WinExe)
│  │  ├─ AMARE.Windows.Core/       UI 비의존 C# 서비스/모델/정책
│  │  └─ AMARE.Interop.Tests/      C#↔Rust 및 Core 기능 테스트 (70 assertions)
│  └─ amare-gpui/                  GPUI 셸 (동결 대상)
├─ crates/
│  ├─ amare-core/                   공통 코어 타입
│  ├─ amare-store/                  SQLite 스키마/저장소/백업/FTS
│  ├─ amare-security/               키 유도/암호화/서명/업데이트 검증
│  ├─ amare-services/               프로필/설정/검색/세션/북마크/히스토리
│  ├─ amare-ai/                     AI 관련 Rust 타입
│  ├─ amare-agent/                  Agent 타입
│  ├─ amare-update/                 업데이트 트랜잭션/롤백
│  ├─ amare-platform/               Windows DPAPI 등 플랫폼 기능
│  ├─ amare-ffi/                    C ABI + JSON RPC 경계
│  └─ amare-testkit/                테스트 지원
├─ tools/                           Windows probe, UI smoke, fixture, CI 스크립트
├─ docs/                            설계/검증/릴리스 문서
└─ artifacts/                       CI 산출물, 패키지, 스크린샷, 보고서
```

규모:

- WinUI `MainWindow.xaml.cs` **3,248줄**, `MainWindow.xaml` **311줄**
- `AMARE.Windows.Core` **3,587줄**
- Rust `crates/` **12,632줄** (그중 `amare-store` 7,872줄)
- GPUI **10,502줄** (`main.rs` 6,892줄, `webview.rs` 1,492줄, `password_vault.rs` 884줄)

---

## 4. 현재 런타임 아키텍처

### 4.1 WinUI 시작 경로

```text
AMARE.Windows.exe
  → App.OnLaunched
  → new MainWindow()
  → MainWindow.InitializeAsync()
      → AppPaths.LocalRoot()                 데이터 루트 결정
      → AmareNative.Create(root/data)        Rust 서비스 핸들 생성
      → ProfileClient.InitializeAsync()      활성 프로필/세대(context) 확보
      → LoadSettingsAsync()                  theme/engine/restore_session
      → CreateEnvironmentAsync()             WebView2 환경 생성
      → RestoreSessionOrNewTabAsync()        세션 복원 또는 새 탭
      → ResumeProfileCleanupAsync()          삭제 대기 프로필 폴더 정리
      → PortableUpdateRecovery                 중단된 업데이트 복구 표시
```

전역 예외는 `App.xaml.cs`에서 `Application.UnhandledException`,
`AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`으로
`%LOCALAPPDATA%\AMARE\logs\startup-error.log`에 기록한다.

### 4.2 창·탭·WebView2

- 한 프로세스 안에 여러 `MainWindow`를 만들 수 있다.
- `MainWindow`는 static `WindowRegistry`에 `WeakReference`로 등록된다.
- 탭은 `TabManager`가 모델을 관리하고, 각 탭은 **개별 `WebView2` 컨트롤**을 가진다.
- 한 창의 탭들은 **하나의 `CoreWebView2Environment`**를 공유한다.
- 환경의 user data folder는 프로필별로 분리된다:
  `webview2/<profileId>`.
- 창 간 탭 이동/분리는 drag/drop + `TabTransferSnapshot`으로 처리한다.
- AI 패널은 **별도 `CoreWebView2Environment`**(`ai-webview2`)를 사용한다.
- Reader는 화면 오버레이의 **별도 `WebView2`**에서 생성된 HTML을 렌더링한다.
- 닫힌 탭은 `ClosedTabHistory`(LIFO)로 복원한다.
- 세션 저장은 500ms debounce + 종료 시 동기 저장 경로를 가진다.

### 4.3 화면 구조

`MainWindow.xaml`의 주요 요소:

- custom title bar: `AppTitleBar`, `CaptionSpacer`, `TitleBarTitle`
- 탭 스트립: `TabList`, `TabButtons`
- navigation toolbar: `BackButton`, `ForwardButton`, `ReloadButton`, `AddressBar`,
  `GoButton`, `SearchEngineBox`
- 패널/오버레이: `FindBar`, `AiPanelHost`/`AiWebView`, `ReaderHost`/`ReaderWebView`
- 상태: `StatusText`

UI 문자열은 현재 영어 중심이다. 한국어 UI/IME는 코드 구조가 아니라
Phase 3 실기기 검증 항목으로 분리한다.

---

## 5. C# ↔ Rust 경계

### 5.1 공통 호출 경로

```text
C# Core (ProfileClient, SettingsClient, ...)
  → INativeBridge.CallAsync(method, params)
  → AmareNative (SemaphoreSlim 직렬화, UI 밖 Task.Run)
  → amare_services_call(handle, JSON, out JSON)
  → amare-services / amare-store / amare-security
```

- ABI version: `1` (`ExpectedAbiVersion`)
- 요청 형식: `{"version":1,"method":"...","params":{...}}`
- 오류: `NativeError` + `amare_core_last_error`
- 문자열 수명: Rust → C#은 `amare_string_free`, C# → Rust는 `CoTaskMem`

노출된 주요 method:

- `profile.initialize/list/active/current_context/exists/create/rename/set_icon/set_default/switch/delete/reset`
- `settings.get/list_group/list_all/set/set_many/reset_key/reset_group/reset_all`
- `search.query`
- `session.save/latest/delete`
- `bookmark.list/create/delete`
- `history.record/list/clear/top`

### 5.2 공통 코드 경계

- **WinUI는 Rust를 FFI로만 호출**한다. C#이 Rust crate를 직접 링크하지 않는다.
- **GPUI는 Rust crate를 직접 호출**한다. FFI를 사용하지 않는다.
- 따라서 `amare-store`, `amare-services`, `amare-security`, `amare-update` 변경은
  양쪽에 영향을 줄 수 있다.
- GPUI 동결 후에도 공통 crate를 변경할 때는 **WinUI 빌드/테스트와 GPUI 빌드**를
  모두 확인한다. GPUI에 신규 기능을 추가하지 않는다는 뜻이지, 공통 crate를
  무검증으로 바꾸는 뜻이 아니다.

---

## 6. 데이터 저장 구조

### 6.1 WinUI 데이터 경로

| 데이터 | 경로 |
|---|---|
| portable 데이터 루트 | `%LOCALAPPDATA%\AMARE` |
| MSIX 데이터 루트 | `%LOCALAPPDATA%\AMARE-MSIX` |
| Rust 서비스/SQLite 루트 | `<root>\data` |
| 루트 DB | `<root>\data\amare.db` |
| 프로필 DB | `<root>\data\profiles\<profileId>\amare.db` |
| WebView2 프로필 | `<root>\webview2\<profileId>` |
| AI WebView2 | `<root>\ai-webview2` |
| 로그 | `<root>\logs` |
| 다운로드 파일 | `<root>\downloads` |
| 다운로드 이력 | `<root>\downloads.json` |
| 업데이트 설정 | `<root>\update-preferences.json` |
| AI API 설정 | `<root>\ai-api-settings.json` |
| AI API 키 | Windows Credential Manager (`PasswordVault`) |

`ProfilePathPolicy`는 `profileId`를 영숫자/`-`/`_`/`.`로 제한하고,
`webview2` 루트 밖으로 나가는 경로를 거부한다.

### 6.2 SQLite 스키마

- 루트 스키마: **v2** (`profiles` + `icon`)
- 프로필 스키마: **v4**
- WAL, `foreign_keys=ON`, `synchronous=NORMAL`
- 마이그레이션은 `schema_migrations`와 `PRAGMA user_version`을 함께 사용
- 주요 테이블:
  `settings`, `bookmark_folders`, `bookmarks`, `history_visits`, `sessions`,
  `session_tabs`, `secrets`, `library_items`, `tags`, `library_item_tags`,
  `collections`, `library_item_collections`, `source_references`, `workspaces`,
  `tasks`, `task_memories`, `task_memory_links`, `conversations`, `messages`,
  `message_parts`, `message_sources`, `downloads`, FTS5 테이블
- `secrets`는 암호화 blob 저장에 사용된다. Rust 테스트에서
  XChaCha20-Poly1305 reopen/오류 키 거부가 PASS했다.

### 6.3 GPUI 데이터 경로와 호환성

GPUI는 `%APPDATA%\AMARE`를 사용한다 (`AMARE_GPUI_DATA_DIR` override 가능).
WinUI는 `%LOCALAPPDATA%\AMARE`를 사용한다.

즉:

- **스키마는 공유**한다 (같은 `amare-store` 사용).
- **기본 데이터 루트는 공유하지 않는다.**
- GPUI에서 쓰던 프로필/세션/북마크가 WinUI에 자동으로 나타나지 않는다.
- 반대 방향도 마찬가지다.

이것은 현재 결함이라기보다 **마이그레이션 정책 부재**다. 개인용 최종 패키지에서
GPUI 데이터를 가져올 필요가 있는지 결정해야 한다. 기본 방향은
“GPUI는 동결·보존, WinUI는 새 데이터 루트”이며, 필요 시 **명시적 import 도구**를
별도로 만든다. 자동 복사/덮어쓰기는 금지한다.

---

## 7. 보안 경계

| 영역 | 현재 구현 | 판정 |
|---|---|---|
| AI API 키 | Windows Credential Manager (`PasswordVault`) | 구현됨, 실패 시 저장 거부 |
| 일반 비밀번호 저장/자동완성 | WinUI에 없음 | **NOT_IMPLEMENTED** |
| Vault UI | WinUI에 없음 | **NOT_IMPLEMENTED** |
| Rust 암호화 저장 | `amare-security` + `secrets` 테이블, 테스트 PASS | 라이브러리 수준 검증 |
| GPUI password vault | XChaCha20-Poly1305 + DPAPI key file + 전역 lock | GPUI에만 존재, 0.93 테스트 1 FAIL |
| WebView2 프로필 격리 | cookies/localStorage/IndexedDB 격리 PASS | 검증됨 |
| MSIX | unsigned, `runFullTrust` | 설치 BLOCKED |
| 코드 서명 | 인증서 없음 | 미구현 |

중요: WinUI의 `PasswordVault` 사용은 **AI API 키 1개**에 한정된다.
브라우저 비밀번호 관리자나 일반 Vault로 오인하면 안 된다.

---

## 8. 업데이트와 배포

- 배포 우선순위: **Portable ZIP first**, MSIX는 보조.
- 현재 WinUI 패키지: `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`,
  `x64;ARM64`.
- 업데이트 확인: GitHub Releases API (`thfzm/AMARE`), stable/pre-release 채널.
- 다운로드: SHA-256 필수. checksum이 없으면 자동 다운로드 차단.
- 적용: `PortableUpdateEngine`이 backup → replace → health marker → rollback을 수행.
- 테스트: apply preserves data, rollback, crash recovery, launch failure rollback,
  manifest/version/traversal 거부, health marker timeout, corrupt state PASS.
- 아직 없는 것: 실제 Windows installer, 서명, MSIX 설치 성공 경로.
- `winui_msix_install`은 `0x80073D2C`로 **UNVERIFIED/BLOCKED**.
- 0.93은 제품 코드 변경이 없어 **새 패키지를 만들지 않았다.**

---

## 9. 최종 아키텍처 결정

| ID | 결정 | 근거 | 결과 |
|---|---|---|---|
| ADR-001 | Windows UI는 **WinUI 3** | 48/48 UI smoke, 143/0/9/1, 70/70 interop, 실제 기능 범위가 가장 넓음 | 최종 방향 |
| ADR-002 | 웹 렌더링은 **WebView2 Evergreen** | Chromium 호환성, OS 업데이트, 프로필 격리 검증 | 유지 |
| ADR-003 | 주 언어는 **C#** | WinUI 생산성, 기존 Core 3,587줄 재사용 | 유지 |
| ADR-004 | Rust는 **공통 데이터/보안/서비스/업데이트**에 제한 | FFI ABI v1, 스키마/백업/암호화 테스트 자산 | 유지 |
| ADR-005 | 저장소는 **SQLite(WAL, FTS5)** | v2/v4 마이그레이션, 백업/복구/FTS 테스트 | 유지 |
| ADR-006 | 보안 저장은 **Windows OS 기능 우선** | Credential Manager, DPAPI, 추후 Windows Hello | 유지 |
| ADR-007 | 배포는 **Portable ZIP 우선** | unsigned MSIX 설치 차단, 개인용 배포 단순화 | 유지 |
| ADR-008 | Android는 **보류** | Windows 개인용 완성이 우선 | 유지 |
| ADR-009 | GPUI는 **동결·보존** | WinUI 기능/검증 우위, GPUI 11~13 FAIL | 신규 개발 중단 |
| ADR-010 | GPUI와 WinUI 결과 **합산 금지** | 테스트/제품 경로가 다름 | 유지 |
| ADR-011 | 데이터 루트는 **자동 공유하지 않음** | 경로/수명/잠금 차이, 데이터 손실 방지 | 명시적 import만 허용 |
| ADR-012 | 멀티윈도우 세션은 **창 단위 모델**로 재설계 | 현재 WinUI는 단일 탭 목록/last-writer-wins 위험 | Phase 1 P0 |

---

## 10. GPUI 동결 원칙

### 허용

- 기존 소스·문서·패키지·증거 보존
- 보안/빌드에 필요한 공통 crate 수정 (WinUI 영향 검증 필수)
- GPUI 전용 유용 기능의 **목록화와 설계 이관**
- 동결 상태를 설명하는 문서 추가

### 금지

- 신규 GPUI 기능 개발
- 신규 GPUI 전용 테스트 확장
- GPUI 기본 패키지 전환
- GPUI 소스/패키지/증거 삭제
- GPUI 테스트 FAIL을 이유로 assertion 완화
- WinUI와 GPUI 결과 합산

### 동결 후 공통 코드 변경 규칙

1. 변경 전 `git status`, HEAD, stash 확인
2. `cargo test`로 공통 crate 검증
3. WinUI `verify-windows.ps1 -CI`와 interop 검증
4. GPUI가 빌드 가능한지 확인 (신규 기능 추가 없이)
5. 스키마 변경 시 마이그레이션/백업/복구 테스트 추가
6. 증거와 실패 로그 보존

---

## 11. 확인된 구조적 위험

| 위험 | 내용 | 대응 |
|---|---|---|
| R-01 | CI runner가 Windows 11 실기기가 아님 | Phase 3 실기기 체크리스트 분리 |
| R-02 | WinUI 멀티윈도우 세션이 단일 탭 목록 | 창 단위 session 모델 도입 |
| R-03 | WinUI/GPUI 데이터 루트 불일치 | 자동 공유 금지, 필요 시 import |
| R-04 | 비밀번호 관리자/Vault 부재 | Phase 1 P0 |
| R-05 | 사이트 권한 UI 부재 | Phase 2 P1 |
| R-06 | unsigned MSIX 설치 불가 | Portable ZIP 우선, 서명은 별도 결정 |
| R-07 | AI 웹 패널의 외부 서비스 의존 | 실제 서비스/세션 격리 검증 |
| R-08 | WebView2 Evergreen 버전 변화 | CI에서 runtime 버전 기록, 회귀 시 재현 |
| R-09 | GPUI 테스트 불안정 | 동결, 결과는 참고만 하고 합산 금지 |
| R-10 | 한국어 IME/DPI/Snap 미검증 | Phase 3 게이트 |

---

## 12. 승인이 필요한 결정

1. Phase 1에서 **멀티윈도우 세션 모델**을 P0로 먼저 수정하는 것
2. WinUI **비밀번호 관리자/Vault**를 Rust 암호화 저장 + Windows Credential/DPAPI로
   구현하는 것
3. GPUI 데이터 **자동 이전을 하지 않고**, 필요 시 명시적 import 도구만 두는 것
4. MSIX 서명/설치를 Phase 4 이후로 미루고 **Portable ZIP을 1차 배포**로 확정하는 것
5. 실제 Windows 11 실기기 검증을 Phase 3의 **필수 게이트**로 두는 것

이 결정들은 Phase 0 문서 승인 후 Phase 1에서 적용한다. 승인 전에는 제품 코드와
기본 패키지를 변경하지 않는다.
