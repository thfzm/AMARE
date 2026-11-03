# AMARE 저장소 아키텍처

- 작성일: 2026-11-03
- 상태: Phase 1.7 구현 (공통 서비스 계층 + Library/AI/Workspace 데이터 계층)
- 범위: 플랫폼 독립 SQLite 로컬 저장소 (`amare-store`)
- 관련 문서: [DB 스키마](DATABASE_SCHEMA.md), [서비스](SERVICE_ARCHITECTURE.md), [Library](LIBRARY_ARCHITECTURE.md), [AI 데이터](AI_DATA_MODEL.md), [Workspace 기억](WORKSPACE_MEMORY.md), [보안 설계](SECURITY_DESIGN.md), [GitHub Actions](GITHUB_ACTIONS.md)

---

## 1. 결정: 프로필별 DB 분리 + 루트 레지스트리

AMARE는 **단일 DB에 `profile_id`를 넣는 방식**과 **프로필별 DB 파일**을 비교한 뒤, Phase 1에서는 다음 하이브리드를 채택합니다.

```
<root>/amare.db                 # 프로필 레지스트리만
<root>/profiles/<profile-id>/amare.db  # 설정/북마크/기록/세션/비밀/Library/AI/Workspace
```

### 비교

| 기준 | 단일 DB + profile_id | 프로필별 DB |
|---|---|---|
| 데이터 격리 | 쿼리/Repository 필터에 의존 | 파일/연결 단위 격리 |
| 실수로 인한 누출 | WHERE 누락 시 위험 | 다른 파일을 열지 않는 한 접근 불가 |
| 트랜잭션 | 프로필 간 원자적 변경 쉬움 | 프로필별 독립 |
| 백업/삭제 | 한 파일 | 프로필 단위 백업/삭제 쉬움 |
| 확장성 | 테이블 비대화 | 프로필별 크기/수명 관리 |
| 복잡도 | 낮음 | 루트 레지스트리/다중 연결 필요 |

### 선택 이유

- 방문 기록/북마크/세션은 프로필별로 완전히 분리되는 것이 제품 요구사항에 맞습니다.
- 파일 단위 격리는 Repository 버그가 다른 프로필 데이터에 닿을 가능성을 줄입니다.
- 프로필 삭제/백업/이전이 단순합니다.
- 단일 DB의 장점(프로필 간 원자적 작업)은 AMARE의 초기 요구사항이 아닙니다.

### 한계

- 프로필 간 데이터 이동/복사는 별도 기능으로 구현해야 합니다.
- 다중 프로필 동시 사용 시 연결 수가 늘어납니다.
- 루트 DB와 프로필 DB의 일관성(프로필 행 생성 후 DB 생성 실패 등)은 `Store::create_profile`에서 정리합니다.
- OS 사용자 권한이 같다면 파일 시스템 수준의 강한 격리는 아닙니다. 암호화/OS 권한은 별도 계층입니다.

---

## 2. 구성

| 계층 | 역할 |
|---|---|
| `Store` | 루트 DB, 프로필 레지스트리, 프로필 DB 생성/삭제 |
| `ProfileStore` | 단일 프로필 DB 연결, Repository 제공 |
| Repository | Settings/Bookmarks/History/Sessions/Profiles/Secrets/Library/Conversations/Workspaces/Tasks/TaskMemories/Search |
| Migration | 루트/프로필 스키마의 순차 버전 관리 |
| Domain (`amare-core`) | Profile/Bookmark/History/Session/SettingValue + Library/Conversation/Workspace 모델 |
| DB row (`amare-store::models`) | private row structs, domain으로 변환 |

`amare-core`는 SQLite/rusqlite 타입을 import하지 않습니다. Repository API도 도메인 타입만 노출합니다.

---

## 3. 연결/PRAGMA

`amare-store::db::open_connection`:

- `busy_timeout = 5s`
- `PRAGMA foreign_keys = ON`
- `PRAGMA journal_mode = WAL` (가능한 경우)
- `PRAGMA synchronous = NORMAL`
- Prepared statements (`prepare`/`query_map`/parameter binding)

WAL은 동시 읽기/쓰기와 비정상 종료 복구에 유리합니다. Windows/Android 모두 SQLite WAL을 지원합니다.
네트워크 파일 시스템은 지원 대상이 아닙니다.

---

## 4. 마이그레이션

- `schema_migrations(version, applied_at_ms)` 테이블로 버전을 기록합니다.
- 마이그레이션은 **버전 오름차순**으로 적용합니다.
- 각 마이그레이션은 `BEGIN IMMEDIATE` 트랜잭션에서 실행하고, 성공 시 버전 행을 삽입합니다.
- SQL 실패 시 트랜잭션을 롤백하고 `StoreError::Migration { version, source }`를 반환합니다.
- `PRAGMA user_version`도 함께 갱신합니다.
- 반복 실행해도 이미 적용된 버전은 건너뜁니다.
- 초기화 실패 시 DB는 이전 버전 상태로 남습니다. 호출자는 재시도/백업 복원/사용자 안내를 선택할 수 있습니다.

| DB | 현재 버전 | 내용 |
|---|---:|---|
| 루트 | 2 | profiles + icon + default unique index |
| 프로필 | 3 | v1: settings/bookmarks/folders/history, v2: sessions/tabs/secrets, v3: Library/AI/Workspace + FTS5 |

---

## 5. 트랜잭션/동시성

- 단일 INSERT/UPDATE/DELETE는 autocommit.
- 다중 행 변경(bookmark create_many, session replace, profile default 변경, migration)은 `BEGIN IMMEDIATE` 트랜잭션을 사용합니다.
- `BEGIN IMMEDIATE`는 쓰기 잠금을 먼저 얻어 DEFERRED 트랜잭션의 lock upgrade deadlock을 줄입니다.
- 동시 접근 테스트: 두 개의 `ProfileStore::open_at` 연결이 같은 파일에 40개 bookmark를 쓰고 모두 보존되는지 확인.
- 실패 시 트랜잭션 롤백으로 이전 상태를 보존합니다.

---

## 6. Repository API

| Repository | 주요 기능 |
|---|---|
| `ProfileRepository` | create/get/list/default/set_default/rename/delete/count |
| `SettingsRepository` | set/get/delete/list/search, SettingValue 타입 보존 |
| `BookmarkRepository` | bookmark CRUD/list/search/pagination, folder CRUD/hierarchy/cycle 방지 |
| `HistoryRepository` | visit 기록/URL 검색/기간 조회/방문 횟수/삭제 |
| `SessionRepository` | session 생성/목록/복원/tab 교체/active tab/삭제 |
| `SecretRepository` | 암호화된 secret set/get/delete/list (키는 DB 밖) |
| `LibraryRepository` | Library item/tag/collection CRUD, 상태/휴지통/검색 |
| `SourceRepository` | 출처 인용 생성/조회/삭제, LibraryItem 삭제 후 보존 |
| `ConversationRepository` | 대화/메시지/파트 CRUD, soft delete, 메시지 출처 연결, 검색 |
| `WorkspaceRepository` / `TaskRepository` | Workspace/Task CRUD, 보관/삭제, 검색 |
| `TaskMemoryRepository` | Task 기억 CRUD, 명시적 Task 간 공유, 검색 |
| `SearchRepository` | FTS5 통합 검색, 원본 type/ID가 포함된 `SearchHit` 반환 |
| `amare-services` | Profile/Settings/Search 서비스, context/정책/트랜잭션 조합 |

Pagination은 `Page<T> { items, total, limit, offset }`를 사용합니다. limit은 1..=500으로 clamp합니다.

---

## 7. 보안

- DB는 사용자 기기 내부 경로에만 저장합니다.
- 방문 기록/북마크를 외부로 전송하지 않습니다.
- 비밀번호/API 키/토큰은 `settings`에 평문 저장하지 않습니다.
- `SecretRepository`는 `amare-security`의 XChaCha20-Poly1305를 사용합니다.
  - AAD = secret key (다른 key로 암호문을 옮겨도 복호화 실패)
  - 암호화 키는 DB에 저장하지 않음
  - 키는 OS 키체인/마스터 키에서 파생해 호출자가 전달
- 오류 로그에 평문 secret/URL/검색어를 자동 출력하지 않습니다.
- 프로필 DB 파일은 프로필 단위로 분리합니다.
- Library/대화/Workspace 기억은 자동으로 AI/외부에 전송하지 않습니다.
- Message에는 private key PEM을 저장하지 않으며, secret은 `SecretRepository`만 사용합니다.
- FTS5 검색어는 연산자 주입을 막기 위해 quote/escape합니다. snippet은 원문이므로 UI에서 escape합니다.

### 남은 보안 과제

- DB 파일 전체 암호화(SQLCipher 등)는 아직 미적용.
- OS 키체인/DPAPI/Keystore 연동은 Phase 1 후속.
- 백업 암호화/복원은 별도 모듈.

---

## 8. 복구/백업/키 보호

- 백업: `amare-store::backup`, SQLite Online Backup API, 체크섬/암호화, staging 복원
- 무결성: `amare-store::maintenance`의 `check_database`/`check_store`
- 키 보호: `amare-security::KeyFile` + `KeyProtector`, Windows DPAPI는 `amare-platform`
- SecretRepository 기존 호출자-키 API는 유지
- 상세: [백업/복원](BACKUP_RESTORE.md), [키 보호](KEY_PROTECTION.md), [DB 복구](DATABASE_RECOVERY.md)

- WAL/SHM 파일을 포함한 백업은 SQLite Backup API로 수행합니다(단순 파일 복사 금지).
- 정상 종료 시 WAL checkpoint를 수행하는 것이 좋습니다.
- 손상 감지 시 원본을 보존하고 백업 복원을 안내합니다.
- 마이그레이션 실패 시 자동 삭제하지 않고 이전 버전을 유지합니다.
- 프로필 삭제는 `Store::delete_profile`에서 레지스트리 행과 디렉터리를 함께 정리합니다.

---

## 9. 테스트/벤치마크

- 실제 SQLite 파일 DB 사용, 테스트별 임시 디렉터리로 상태 공유 방지.
- 단위/통합: 최초 생성, 재접속, 마이그레이션 중복/실패/v1·v2→v3 업그레이드, CRUD, 폴더 계층/사이클, FK, 검색, 기간, 세션 복원/롤백, 프로필 격리, SQL injection, 동시 접근, 암호화 secret.
- Phase 1.6: Library/tag/collection/source, 대화/메시지/파트/soft delete, Workspace/Task/Memory/명시적 공유, FTS5 통합 검색, cascade/SET NULL, profile isolation, private key 거부.
- Phase 1.7: ProfileService/SettingsService/SearchService, root v2 icon, tombstone 차단, atomic settings, typed search filters/pagination. Linux service tests 38 + service-ci 4/4.
- CI 보고서: `cargo run -p amare-store --bin storage-ci --release -- --output ...`
- 벤치마크: `cargo run -p amare-store --bin storage-benchmark --release -- --output ...`
- Runner 벤치마크는 목표 PC 성능의 대체물이 아닙니다.

---

## 10. 향후

1. 프로필 DB 전체 암호화 옵션
2. 비밀/토큰의 OS 키체인 통합
3. 백업/복원과 WAL checkpoint 정책
4. Agent 실행 원장/승인 기록 테이블
5. 마이그레이션 버전 롤백 정책(다운그레이드 금지/명시적 복원)
6. Android/iOS 파일 경로 adapter
7. 대용량 Library/AI 백업 스트리밍
