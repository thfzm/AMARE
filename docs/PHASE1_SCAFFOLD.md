# Phase 1 스캐폴딩 현황

- 작성일: 2026-11-03
- 상태: **스캐폴딩 + SQLite/Library/AI 저장소 + 백업/키 보호 + Phase 1.7 공통 서비스 계층 구현 완료, WebView2 UI 통합 미구현**
- 관련 문서: [Phase 0.5 검증 결과](PHASE0_5_REPORT.md), [개발 가이드](DEVELOPMENT.md)

## 1. 생성된 구조

```text
AMARE/
├─ Cargo.toml                 # Rust workspace
├─ rust-toolchain.toml
├─ Cargo.lock
├─ crates/
│  ├─ amare-core/             # 도메인/버전/기본 타입
│  ├─ amare-ffi/              # C ABI + amare.h + C# interop용
│  ├─ amare-security/         # Argon2id + XChaCha20-Poly1305 + Ed25519 + 롤백
│  ├─ amare-update/           # 업데이트 트랜잭션 프로토타입(로직 테스트)
│  ├─ amare-store/            # SQLite(WAL, profiles/Library/AI/Workspace + FTS5 + backup)
│  ├─ amare-services/         # Profile/Settings/Search 공통 서비스
│  ├─ amare-ai/               # 웹 AI/API 연결 정책 타입
│  ├─ amare-agent/            # Agent 모드/권한/승인 기본값
│  ├─ amare-platform/         # Keychain/Clock trait
│  └─ amare-testkit/
├─ apps/
│  ├─ windows/
│  │  ├─ AMARE.Windows.sln
│  │  ├─ AMARE.Windows/       # WinUI 3 + WebView2 스캐폴드
│  │  └─ AMARE.Interop.Tests/ # C# ↔ Rust (Linux 실행 가능)
│  └─ android/                # Kotlin/Compose + GeckoView 스캐폴드(빌드 미검증)
├─ engine/contracts/          # EngineCommand/Event/Capabilities JSON Schema
├─ tools/
│  ├─ verify-linux.sh
│  ├─ verify-windows.ps1
│  └─ windows-verify/         # WebView2 Phase 0.5 프로브(WinForms, 미실행)
├─ tests/
└─ .github/workflows/         # ci-linux.yml, ci-windows.yml
```

## 2. 구현된 것

### Rust

- `amare-core`: 버전/`CoreInfo`/`ProfileId`/ping
- `amare-ffi`: C ABI
  - `amare_core_version`
  - `amare_core_create`/`amare_core_destroy`
  - `amare_core_ping`
  - `amare_core_last_error`
  - `amare_string_free`
  - `amare_core_add`
- `amare-security`: Argon2id, XChaCha20-Poly1305, Ed25519, SHA-256, 다운그레이드 거부
- `amare-update`: 서명/해시/버전 검증 + 설치 실패 복구/데이터 보존/명시적 롤백 로직
- `amare-store`: 루트 프로필 레지스트리 + 프로필별 SQLite DB, 순차 마이그레이션(root v2/profile v3), Settings/Bookmarks/History/Sessions/Profiles/Secrets/Library/Conversations/Workspaces/Tasks/TaskMemories/Search Repository, FTS5, 백업/복원, 실제 SQLite 테스트 및 benchmark
- `amare-services`: ProfileService/SettingsService/SearchService, ProfileContext 기반 격리/전환, 설정 atomic validation, typed search/pagination
- `amare-ai`: 웹 AI 기본/API 선택, Agent API 조건
- `amare-agent`: Agent 모드/권한/승인 기본값
- `amare-platform`: Keychain/Clock trait

### C# / Windows

- `AMARE.Interop.Tests`: P/Invoke로 Rust C ABI + service ABI 호출. Linux에서 13/13 통과.
- `AMARE.Windows`: WinUI 3 + `WebView2` 컨트롤을 `about:blank`로 초기화하는 **스캐폴드만**.
  - 탭/주소창/북마크/기록/다운로드/세션 등은 **미구현**.
  - Linux에서는 XAML 컴파일러가 Windows PE라 빌드 불가. Windows 빌드 필요.

### Android

- Compose `MainActivity` + GeckoView 의존성 선언만.
- Android SDK가 없어 빌드/실행 미검증.

### 계약/CI

- `engine/contracts` 3개 JSON Schema.
- Linux CI: fmt/clippy/test/security/update/FFI/C header/C# interop/schema, Artifacts.
- Windows CI: Rust/FFI/C header/interop/WinUI Release build/WebView2 probes, Artifacts.
- Windows 검증 하네스: 프로필 격리(쿠키/localStorage/IndexedDB), CDP 클릭/입력/스크롤, 사용자 개입 감지, 30탭 메모리, DRM 매트릭스, 결과 JSON 상태 구분.
- Storage 자동 검증: `cargo test -p amare-store`, `storage-ci` 통합 JSON, `storage-benchmark` JSON.
- 저장소 설계: [저장소 아키텍처](STORAGE_ARCHITECTURE.md), [DB 스키마](DATABASE_SCHEMA.md).
- CI 사용법/Artifacts/디버깅: [GitHub Actions 문서](GITHUB_ACTIONS.md).

## 3. 실행 방법

```bash
# Linux
./tools/verify-linux.sh

# Windows 11
.\tools\verify-windows.ps1
```

## 4. 아직 구현하지 않은 것

- 탭/주소창/검색/북마크/기록/다운로드/세션 복원 UI (저장소 API는 구현됨)
- 실제 WebView2 탭 수명주기/프로필 연결
- CDP Agent 실행기
- AI 채팅/페이지 추출
- Personal Library/Research/작업 기억 UI (데이터/검색 API는 구현됨)
- Android GeckoView 연결
- 설치 프로그램/업데이트 서명 (백업/복원 로직은 구현됨)
- 자동 업데이트

## 5. Phase 1 실제 구현 전 조건

자세한 실행 절차는 [Windows 검증 가이드](WINDOWS_VERIFICATION.md)를 참고하세요.

1. Windows 11 + WebView2에서 `tools/verify-windows.ps1` 통과
2. 프로필/세션 격리, CDP 조작, 사용자 개입 감지 결과 확인
3. i5-7500/16GB에서 30탭 메모리/전환 측정
4. DRM/VMP 실제 서비스 테스트
5. WebView2/WinUI 라이선스 검토

이 조건 전에는 브라우저 기능 본 구현을 시작하지 않습니다.

## 6. Storage 구현/검증 결과 (2026-11-03, Linux)

- `amare-store`: 루트 프로필 레지스트리 + 프로필별 SQLite DB, WAL, FK, busy timeout, sequential migrations
- Repository: Settings/Bookmarks/History/Sessions/Profiles/Secrets + Library/Conversations/Workspaces/Tasks/TaskMemories/Search
- 실제 SQLite 테스트: **storage 12 + Phase 1.6 30 + migration unit 5 통과**
- `storage-ci`: **10/10 PASS** (`/tmp/storage-ci.json` 로컬 실행)
- `storage-benchmark`: 1,000 bookmarks, 10,000 history, 10 sessions/300 tabs 측정
  - Linux arm64 release: bookmarks insert ~95ms, history insert ~70ms, history search ~7ms, sessions ~3ms, total ~200ms (run-to-run 변동)
  - Runner 측정치는 목표 i5-7500/16GB 성능의 대체물이 아님
- 전체 workspace: Phase 1.7 시점 135, Phase 2 FFI 추가 후 **138**
- Windows Runner 실행: **미확인** (remote/권한 없음)
- 실기기 검증: 미실시

상세: [저장소 아키텍처](STORAGE_ARCHITECTURE.md), [DB 스키마](DATABASE_SCHEMA.md)

## 7. Phase 1.5 데이터 보호 결과 (2026-11-03, Linux)

- Backup format v1: manifest + per-file SHA-256 + whole-file sidecar + optional gzip + optional password/key encryption
- SQLite Online Backup API 사용 (WAL 일관 스냅샷)
- Full/프로필 백업, 전체/프로필 복원, 데이터 종류 선택
- 복원 staging 검증, 기존 데이터 recovery 이동, 실패 시 rollback
- `check_database`/`check_store` 무결성 검사, 손상 DB 원본 보존
- `KeyFile` + `KeyProtector`, Windows DPAPI adapter, Android Keystore stub
- Linux: backup 13 tests + parser unit 3, storage 12 + Phase 1.6 30 + migration 5, security 11, platform 1, service 38, full workspace 135 (Phase 1.7 시점)
- Linux backup benchmark: 5 profiles × 1,000 bookmarks / 10,000 history / 100 tabs
  - full backup ~500ms, full restore ~170ms, single profile backup ~100ms
  - full backup ~1.5MB, single profile ~0.3MB, checksum verify ~55ms
  - 메모리 사용량은 미측정(`not_measured`)
- Windows DPAPI/Windows Runner: **미실행** (compile check만)

상세: [백업/복원](BACKUP_RESTORE.md), [키 보호](KEY_PROTECTION.md), [DB 복구](DATABASE_RECOVERY.md)

## 8. Phase 1.6 Library/AI/Workspace 데이터 계층 (2026-11-03, Linux)

- Profile DB v3: LibraryItem/Tag/Collection/SourceReference, Conversation/Message/MessagePart, Workspace/Task/TaskMemory + FTS5
- `crates/amare-store/tests/library_ai.rs`: **30/30 PASS**
- `storage-ci`: Phase 1.6 FTS5/통합 검색 case 포함 **10/10 PASS**
- `backup-ci`: Phase 1.6 데이터 제외 case 포함 **10/10 PASS**
- 백업: profile schema v3 전체/카테고리 제외 복원 검증, parser 크기/길이 제한 추가
- 실제 AI 호출/웹 AI 자동화/WebView2 UI는 **미구현**
- Windows Runner: **미실행** (remote/권한 없음)

상세: [Library](LIBRARY_ARCHITECTURE.md), [AI 데이터](AI_DATA_MODEL.md), [Workspace 기억](WORKSPACE_MEMORY.md)

## 9. Phase 1.7 공통 서비스 계층 (2026-11-03, Linux)

- `crates/amare-services`: `ProfileService`, `SettingsService`, `SearchService`
- Root DB v2: profile `icon` (기존 v1은 자동 마이그레이션)
- `ProfileContext { profile_id, generation }`으로 전환 중 stale 작업 차단
- tombstone 프로필 접근/재생성 차단, 마지막 프로필 삭제 거부, 기본 프로필 승격
- Settings: 12개 그룹, 기본값/사용자값 구분, 타입/범위/민감키 검증, atomic multi-set, reset
- Search: 10개 SearchKind, kind 필터, pagination, 정렬, profile 격리, soft delete/archived 정책
- 서비스 테스트: **38/38 PASS**
- `service-ci`: **4/4 PASS** (profile/settings/search/isolation)
- `service-benchmark` (Linux aarch64, 2 profiles / 500 Library / 500 bookmarks / 500 history):
  - search all kinds (50회): avg ~5.7ms
  - typed Library+Bookmark search (50회): avg ~3.3ms
  - atomic two-key settings update (100회): avg ~9.3ms
  - profile switch (100회): avg ~1.5ms
  - run-to-run 변동 있음, Windows/Android 실기기 성능 아님
- 기존 데이터/백업 호환성: root v2 + profile v3, full/profile backup 테스트 유지
- 실제 UI/WebView2/GeckoView/AI 호출: **미구현**
- Windows Runner/실제 GitHub Actions: **미실행** (remote/권한 없음)

상세: [서비스 아키텍처](SERVICE_ARCHITECTURE.md), [프로필](PROFILE_MANAGEMENT.md), [설정](SETTINGS_ARCHITECTURE.md), [검색](SEARCH_ARCHITECTURE.md)
