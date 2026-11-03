# AMARE 공통 서비스 계층 아키텍처

- 작성일: 2026-11-03
- 구현: `crates/amare-services`
- 상태: Phase 1.7 (플랫폼 독립 서비스 계층)
- 관련 문서: [프로필 관리](PROFILE_MANAGEMENT.md), [설정](SETTINGS_ARCHITECTURE.md), [검색](SEARCH_ARCHITECTURE.md), [저장소](STORAGE_ARCHITECTURE.md)

---

## 1. 위치와 의존성

```text
amare-core          도메인 타입
    ↑
amare-store         SQLite Repository / FTS5 / Backup
    ↑
amare-services      ProfileService / SettingsService / SearchService
    ↑
Windows C# / Android Kotlin / FFI adapter (후속)
```

- `amare-services`는 UI, WebView2, GeckoView, AI 전송 코드를 포함하지 않습니다.
- Rust workspace에 `crates/amare-services`로 추가했습니다.
- Windows/Android는 동일한 서비스 API를 사용할 수 있습니다.
- Phase 2에서 필요한 서비스 명령만 JSON C ABI(`amare_services_call`)로 노출했습니다. UI는 C# Adapter를 통해 호출합니다.

---

## 2. 서비스 범위

| 서비스 | 책임 | Repository와의 관계 |
|---|---|---|
| `ProfileService` | 프로필 registry, 활성 프로필, tombstone 차단, 초기화/삭제 정책 | `Store`/`ProfileRepository`를 소유하고 수명주기를 관리 |
| `SettingsService` | 기본값, 타입/범위 검증, 원자적 변경, reset, 변경 결과 | `SettingsRepository`에 위임 |
| `SearchService` | 통합 검색, kind 필터, pagination, 정렬, 프로필 context 검증 | `SearchRepository` + Bookmark/History Repository 조합 |

Library/Conversation/Workspace/Task/Backup 전용 서비스는 이번 단계에서 **만들지 않았습니다**. 현재 Repository가 이미 도메인 단위 응집도를 갖고 있고, 교차 정책은 `ProfileContext`와 `SearchService`에서 처리합니다. 단순 위임 서비스를 추가하면 API 표면만 늘어나기 때문입니다. Backup은 기존 `BackupManager`를 그대로 사용합니다.

---

## 3. 프로필 컨텍스트

```rust
let context = profiles.switch_profile(&profile_id)?; // ProfileContext
let settings = SettingsService::new(&profiles).get(&context, "theme")?;
```

`ProfileContext { profile_id, generation }`:

- 서비스 작업은 항상 활성 프로필과 세대(generation)를 함께 검증합니다.
- 프로필 전환, 활성 프로필 삭제, 외부 삭제 감지 시 generation이 증가합니다.
- 오래된 context로 접근하면 `ServiceError::ProfileSwitched`를 반환합니다.
- 이미 열린 `ProfileStore` 연결은 원래 프로필에만 연결되므로, 전환 도중 새 프로필로 섞이지 않습니다.
- 서비스는 장기 DB 연결을 캐시하지 않고 호출마다 짧은 `ProfileStore` 연결을 엽니다.

---

## 4. 오류 정책

`ServiceError`는 서비스 공통 오류입니다.

| 오류 | 의미 |
|---|---|
| `Store` | Repository/DB/마이그레이션 오류. 내부 `StoreError` 보존 |
| `NoActiveProfile` | 활성 프로필 없음 |
| `ProfileSwitched` | context가 오래됨 |
| `LastProfile` | 마지막 프로필 삭제 거부 |
| `UnknownSetting` | 등록되지 않은 설정 |
| `InvalidSettingType` / `SettingOutOfRange` | 타입/범위 위반 |
| `SensitiveSetting` | 민감 키를 일반 설정에 저장 시도 |
| `DuplicateSetting` | 한 번의 원자적 변경에 중복 키 |
| `EmptyQuery` / `QueryTooLong` / `InvalidLimit` / `OffsetTooLarge` | 검색 입력 제한 |

원칙:

- 서비스는 Repository 오류를 임의로 숨기지 않습니다.
- 입력 검증 오류는 DB 쓰기 전에 반환합니다.
- 다중 쓰기는 Repository 트랜잭션에서 all-or-nothing으로 처리합니다.
- 프로필 전환/삭제로 인한 오류는 데이터 오류와 구분합니다.

---

## 5. 트랜잭션 경계

| 작업 | 트랜잭션 |
|---|---|
| 설정 다중 변경 | `SettingsRepository::set_many` 한 트랜잭션 |
| 설정 초기화 | group/all delete (단일 SQL 또는 트랜잭션 delete_many) |
| 프로필 DB 초기화 | 모든 사용자 테이블 DELETE 한 트랜잭션 |
| 프로필 생성 | registry INSERT + profile DB 생성/정리 |
| 프로필 삭제 | 디렉터리 tombstone 이동 + registry 삭제 + tombstone 정리 |
| 검색 | 읽기 전용, 여러 SELECT를 하나의 스냅샷 트랜잭션으로 묶지 않음 |

검색은 프로필 전환과 동시에 실행될 수 있습니다. 이때 검색은 시작 시 검증한 프로필 DB 연결을 끝까지 사용하며, 새 프로필 데이터와 섞이지 않습니다.

---

## 6. Phase 1.6 재감사 결과

| 항목 | 결과 |
|---|---|
| FTS5/원본 일관성 | trigger 검증 유지. Task archived/Workspace archived 검색 제외 추가 |
| soft delete 검색 노출 | Library trashed, Conversation deleted, internal Message 제외 유지 |
| 영구 삭제/인덱스 | cascade + FTS trigger 테스트 유지 |
| SET NULL/CASCADE | source reference, workspace/task/conversation link 테스트 유지 |
| 프로필 간 검색 혼합 | 파일 단위 격리 + context generation + 서비스 테스트 |
| 공유 링크 접근/만료 | 외부 공유 URL은 아직 없음. 내부 `task_memory_links`는 명시적 공유이며 unshare/cascade 전까지 유지. 외부 링크 도입 시 만료/토큰 정책 필요 |
| Message/Part 수정·삭제 | message update FTS, delete cascade, private key 거부 테스트 유지 |
| TaskMemory/Conversation 분리 | 별도 테이블/Repository/검색 종류 유지 |

백업 파서는 기존 제한(container/manifest/file/gzip/offset/path/duplicate/checksum)을 유지하고, path traversal·중복·overflow·크기 제한 단위 테스트를 추가했습니다. SHA-256은 무결성 검증이며 전자서명이 아닙니다.

---

## 7. 테스트/CI

- `crates/amare-services/tests/services.rs`: 서비스 테스트 38개
- `service-ci`: Profile/Settings/Search/Isolation JSON 보고서
- `service-benchmark`: 검색/설정 원자 변경/프로필 전환 Linux 실측 JSON
- Linux CI: `cargo test -p amare-services`, `service-ci`
- Windows CI: `verify-windows.ps1 -CI`가 동일 테스트와 JSON을 실행
- 결과 구분: PASS / FAIL / UNVERIFIED / SKIPPED
- GitHub Actions 실제 실행 여부는 [GitHub Actions 문서](GITHUB_ACTIONS.md)에 별도 기록
