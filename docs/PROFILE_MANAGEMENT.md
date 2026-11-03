# AMARE 프로필 관리 정책

- 구현: `ProfileService` (`crates/amare-services/src/profile.rs`)
- 관련 문서: [서비스 아키텍처](SERVICE_ARCHITECTURE.md), [저장소](STORAGE_ARCHITECTURE.md), [백업](BACKUP_RESTORE.md)

---

## 1. 데이터 구조

루트 DB `profiles` 테이블:

| 컬럼 | 의미 |
|---|---|
| `id` | path-safe TEXT ID |
| `name` | 표시 이름 (1..256 bytes) |
| `icon` | 선택적 아이콘 참조 (nullable, <=1024 bytes) |
| `is_default` | 기본 프로필 (최대 1개) |
| `created_at_ms`, `updated_at_ms` | timestamps |

프로필 DB는 `profiles/<id>/amare.db`에 분리 저장됩니다. 아이콘은 이미지 파일 자체가 아니라 `asset:`/URL/식별자 참조입니다.

루트 DB는 **v2**이며, v1 백업/DB는 `icon=NULL`로 자동 마이그레이션됩니다.

---

## 2. API

| 기능 | 메서드 |
|---|---|
| 목록 | `list(limit, offset)` |
| 존재/조회 | `exists`, `get`, `active_profile` |
| 초기화(기본 프로필 준비) | `initialize()` |
| 생성 | `create_profile(id, name, icon, is_default)` |
| 이름/아이콘 변경 | `rename_profile`, `set_profile_icon` |
| 기본 지정 | `set_default_profile` |
| 활성 전환 | `switch_profile(id) -> ProfileContext` |
| 데이터 접근 | `profile_store(&ProfileContext)` |
| 프로필 데이터 초기화 | `reset_profile(&ProfileContext)` |
| 삭제 | `delete_profile(id)` |

---

## 3. 활성 프로필과 전환

- 활성 프로필은 `ProfileService` 내부 상태입니다.
- 모든 데이터 접근은 `ProfileContext`를 요구합니다.
- `switch_profile`은 generation을 증가시키고 새 context를 반환합니다.
- 이전 context는 `ProfileSwitched`로 거부됩니다.
- `profile_store`는 context 검증 후 새 SQLite 연결을 열고 반환합니다.
- 장기 연결을 캐시하지 않으므로 전환 후 새 작업이 이전 연결로 흘러가지 않습니다.

---

## 4. 삭제 정책

### 마지막 프로필

- 마지막 프로필 삭제는 **거부**합니다 (`LastProfile`).
- 앱은 항상 최소 1개의 프로필을 유지합니다.
- `initialize()`는 프로필이 없으면 `default`/`Personal`을 생성합니다.

### 기본 프로필

- 기본 프로필은 다른 프로필이 있으면 삭제할 수 있습니다.
- 활성 프로필을 삭제하면 기본 프로필(없으면 가장 오래된 프로필)이 활성화됩니다.
- 기본 프로필 삭제 시 `ProfileRepository`가 다음 프로필을 기본으로 승격합니다.
- 단일 기본 unique index 때문에 새 기본 지정은 기존 기본 해제 **후** 설정합니다.

### tombstone

삭제 순서:

1. `profiles/<id>`를 `profiles/<id>.deleting`으로 rename
2. registry row 삭제
3. tombstone 디렉터리 삭제

- tombstone이 존재하면 `open_profile`, `switch_profile`, metadata 변경, 같은 ID 재생성을 차단합니다.
- `ProfileService::list/get`은 tombstone 프로필을 숨깁니다.
- 중단된 삭제는 `delete_profile` 재호출로 정리할 수 있습니다.
- 기존 Phase 1.5 tombstone/복구 정책과 충돌하지 않습니다.

---

## 5. 프로필 초기화

`reset_profile(context)`:

- 활성 context를 검증한 뒤 해당 프로필 DB에서 사용자 데이터 전체를 한 트랜잭션으로 삭제합니다.
- settings, secrets, bookmarks, history, sessions, Library, AI 대화, Workspace/Task 기억, FTS 인덱스를 함께 정리합니다.
- 프로필 ID/이름/아이콘/기본 여부/DB 파일/schema version은 유지합니다.
- 실패 시 트랜잭션 롤백으로 이전 데이터를 보존합니다.

전체 프로필 삭제와 다릅니다. 초기화는 registry/profile identity를 유지합니다.

---

## 6. 보안/동시성

- 프로필 DB 파일 단위 격리.
- 활성 context generation으로 stale 작업 차단.
- `ProfileService`는 내부 `Mutex`로 registry와 active state를 보호합니다.
- `ProfileStore`는 호출 단위로 열고 닫으므로 전환/삭제 중 연결 혼입 위험을 줄입니다.
- tombstone은 삭제 중 접근을 차단합니다.
- Windows에서 열린 DB 파일은 rename을 방해할 수 있으므로 삭제/복원 전 연결을 닫아야 합니다.
