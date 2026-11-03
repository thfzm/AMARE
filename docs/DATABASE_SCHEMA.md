# AMARE 데이터베이스 스키마

- 작성일: 2026-11-03
- 구현: `crates/amare-store`
- DB: SQLite 3 (rusqlite bundled)
- 상세 설계: [저장소 아키텍처](STORAGE_ARCHITECTURE.md)

---

## 1. 파일 레이아웃

```
<root>/amare.db
<root>/profiles/<profile-id>/amare.db
```

- 루트 DB: 프로필 레지스트리/기본 프로필.
- 프로필 DB: 설정/북마크/기록/세션/비밀 + Library/AI 대화/Workspace 기억 + FTS5 검색 인덱스.
- 프로필 ID는 ASCII letters/digits/`-`/`_`/`.`만 허용 (path traversal 방지).
- 루트 DB v2는 프로필 `icon` 참조를 추가합니다. v1 DB/백업은 자동 마이그레이션됩니다.

---

## 2. 공통

- Timestamp: Unix epoch milliseconds (`INTEGER`).
- Boolean: `INTEGER` 0/1 + `CHECK`.
- ID: `INTEGER PRIMARY KEY AUTOINCREMENT` (프로필은 TEXT).
- 문자열은 UTF-8 TEXT.
- 모든 FK는 `PRAGMA foreign_keys=ON`에서 동작.
- `schema_migrations(version INTEGER PRIMARY KEY, applied_at_ms INTEGER NOT NULL)`.

---

## 3. 루트 DB (v2)

### profiles

| 컬럼 | 타입 | 제약 |
|---|---|---|
| id | TEXT | PK, path-safe |
| name | TEXT | NOT NULL, 1..256 bytes |
| icon | TEXT | NULL, <=1024 bytes (이미지 파일 아님, 참조 문자열) |
| is_default | INTEGER | NOT NULL, 0/1, 기본 0 |
| created_at_ms | INTEGER | NOT NULL |
| updated_at_ms | INTEGER | NOT NULL |

인덱스:

- `idx_profiles_single_default` UNIQUE ON `is_default` WHERE `is_default=1`
- `idx_profiles_name` ON `name`

기본 프로필은 최대 1개입니다. `set_default`는 기존 기본을 해제한 뒤 대상을 설정합니다.

---

## 4. 프로필 DB v1

### settings

| 컬럼 | 타입 | 제약 |
|---|---|---|
| key | TEXT | PK, 1..256 bytes |
| group_name | TEXT | NOT NULL, 1..128 bytes |
| value_type | TEXT | NOT NULL, `text/integer/float/boolean/json` |
| value_text | TEXT | NOT NULL |
| updated_at_ms | INTEGER | NOT NULL |

인덱스: `idx_settings_group(group_name, key)`.

`SettingsRepository`는 키에 `password`, `passwd`, `secret`, `token`, `api_key`, `apikey`, `credential`, `private_key`가 포함되면 `InvalidInput`으로 거부합니다. 민감 값은 `SecretRepository`를 사용해야 합니다.

타입 보존:

| SettingValue | value_type | value_text |
|---|---|---|
| Text | text | 원문 |
| Integer | integer | 10진수 |
| Float | float | 유한한 10진수 |
| Boolean | boolean | `1`/`0` |
| Json | json | 유효한 JSON 문자열 |

### bookmark_folders

| 컬럼 | 타입 | 제약 |
|---|---|---|
| id | INTEGER | PK AUTOINCREMENT |
| parent_id | INTEGER | NULL, FK self `ON DELETE CASCADE` |
| name | TEXT | NOT NULL |
| position | INTEGER | NOT NULL |
| created_at_ms | INTEGER | NOT NULL |
| updated_at_ms | INTEGER | NOT NULL |

인덱스: `idx_bookmark_folders_parent(parent_id, position, id)`.

- 폴더 계층은 `parent_id` self-FK.
- cycle은 Repository에서 차단.
- 부모 삭제 시 하위 폴더 cascade.

### bookmarks

| 컬럼 | 타입 | 제약 |
|---|---|---|
| id | INTEGER | PK AUTOINCREMENT |
| title | TEXT | NOT NULL |
| url | TEXT | NOT NULL |
| folder_id | INTEGER | NULL, FK folder `ON DELETE SET NULL` |
| position | INTEGER | NOT NULL |
| created_at_ms | INTEGER | NOT NULL |
| updated_at_ms | INTEGER | NOT NULL |

인덱스:

- `idx_bookmarks_folder(folder_id, position, id)`
- `idx_bookmarks_url(url)`
- `idx_bookmarks_title(title)`

### history_visits

| 컬럼 | 타입 | 제약 |
|---|---|---|
| id | INTEGER | PK AUTOINCREMENT |
| url | TEXT | NOT NULL |
| title | TEXT | NOT NULL |
| visited_at_ms | INTEGER | NOT NULL |

인덱스:

- `idx_history_url_visited(url, visited_at_ms DESC)`
- `idx_history_visited(visited_at_ms DESC)`

방문은 row-per-visit로 저장합니다. 조회는 URL별로 집계합니다.

```sql
SELECT MAX(id), url, latest_title, MIN(visited_at_ms), MAX(visited_at_ms), COUNT(*)
FROM history_visits
GROUP BY url
```

`HistoryEntry` 필드:

- `id`: 최신 방문 row id
- `first_visited_at_ms`, `last_visited_at_ms`
- `visit_count`
- `title`: 최신 방문 제목

기간 필터는 `visited_at_ms BETWEEN ? AND ?`로 적용합니다.

---

## 5. 프로필 DB v2

### sessions

| 컬럼 | 타입 | 제약 |
|---|---|---|
| id | INTEGER | PK AUTOINCREMENT |
| name | TEXT | NOT NULL |
| active_tab_id | INTEGER | NULL (session_tabs.id, FK 아님) |
| created_at_ms | INTEGER | NOT NULL |
| updated_at_ms | INTEGER | NOT NULL |

인덱스: `idx_sessions_updated(updated_at_ms DESC)`.

`active_tab_id`는 tab 교체 시 일시적으로 무효할 수 있어 FK를 두지 않습니다. `set_active_tab`에서 소속을 검증합니다.

### session_tabs

| 컬럼 | 타입 | 제약 |
|---|---|---|
| id | INTEGER | PK AUTOINCREMENT |
| session_id | INTEGER | NOT NULL, FK sessions `ON DELETE CASCADE` |
| url | TEXT | NOT NULL |
| title | TEXT | NOT NULL |
| position | INTEGER | NOT NULL |
| pinned | INTEGER | NOT NULL, 0/1 |
| group_id | TEXT | NULL, 1..128 bytes |
| last_accessed_at_ms | INTEGER | NOT NULL |

인덱스: `idx_session_tabs_session(session_id, position, id)`.

### secrets

| 컬럼 | 타입 | 제약 |
|---|---|---|
| key | TEXT | PK, 1..256 bytes |
| group_name | TEXT | NOT NULL |
| ciphertext | BLOB | NOT NULL (XChaCha20-Poly1305 nonce+ciphertext) |
| updated_at_ms | INTEGER | NOT NULL |

인덱스: `idx_secrets_group(group_name, key)`.

- 평문/암호화 키는 DB에 저장하지 않습니다.
- AAD = secret key.
- 키는 `amare-security` 계층/OS 키체인에서 호출자가 제공합니다.

---

## 6. 프로필 DB v3 — Library / AI / Workspace

### library_items

| 컬럼 | 타입 | 제약 |
|---|---|---|
| id | INTEGER | PK AUTOINCREMENT |
| title | TEXT | NOT NULL |
| description | TEXT | NOT NULL, 기본 `''` |
| kind | TEXT | `web_page/link/excerpt/note/image/pdf/document/research/summary/other` |
| original_url | TEXT | NULL |
| source_name | TEXT | NULL |
| image_ref | TEXT | NULL |
| user_note | TEXT | NOT NULL, 기본 `''` |
| created_at_ms, updated_at_ms, last_accessed_at_ms | INTEGER | NOT NULL |
| favorite | INTEGER | 0/1 |
| status | TEXT | `active/archived/trashed` |
| sort_order | INTEGER | NOT NULL |

인덱스: `(status, updated_at_ms DESC, id DESC)`, `(favorite, updated_at_ms DESC, id DESC)`, `(original_url)`.

### tags / collections / 연결

| 테이블 | 주요 컬럼 | 정책 |
|---|---|---|
| `tags` | `name`, `name_norm UNIQUE`, `created_at_ms` | Unicode lowercase 정규화로 중복 방지 |
| `library_item_tags` | `item_id`, `tag_id` PK | 양쪽 삭제 시 cascade |
| `collections` | `name`, `description`, timestamps | 이름 중복 허용 |
| `library_item_collections` | `item_id`, `collection_id` PK | 항목/컬렉션 삭제 시 cascade |

### source_references

| 컬럼 | 타입 | 제약 |
|---|---|---|
| id | INTEGER | PK AUTOINCREMENT |
| url | TEXT | NOT NULL |
| page_title, site_name, quote_text, location_info | TEXT | NOT NULL, 기본 `''` |
| collected_at_ms | INTEGER | NOT NULL |
| library_item_id | INTEGER | NULL, FK library_items `ON DELETE SET NULL` |

LibraryItem purge 후에도 출처는 보존됩니다.

### workspaces / tasks / task_memories

| 테이블 | 주요 컬럼 | FK/정책 |
|---|---|---|
| `workspaces` | `name`, `description`, `icon`, `archived`, timestamps | 삭제 시 Task cascade |
| `tasks` | `workspace_id`, `title`, `description`, `status(open/in_progress/done/archived)`, timestamps | Workspace 삭제 시 cascade |
| `task_memories` | `task_id`, `title`, `content`, `memory_type`, `source_reference_id`, `importance(0..5)`, `user_confirmed`, timestamps | Task 삭제 시 cascade; 출처 삭제 시 SET NULL |
| `task_memory_links` | `memory_id`, `target_task_id`, `created_at_ms` | 명시적 Task 간 공유; 양쪽 삭제 시 cascade |

### conversations / messages / message_parts

| 테이블 | 주요 컬럼 | FK/정책 |
|---|---|---|
| `conversations` | `title`, `workspace_id`, `task_id`, `archived`, `deleted_at_ms`, timestamps | Workspace/Task 삭제 시 SET NULL; soft delete 지원 |
| `messages` | `conversation_id`, `role(system/user/assistant/tool)`, `body`, `status`, `visibility(user/internal)`, `position`, timestamps | Conversation purge 시 cascade |
| `message_parts` | `message_id`, `kind(text/code/image/source/citation/tool_result/error/attachment)`, `text`, `reference`, `metadata_json`, `position` | Message 삭제 시 cascade |
| `message_sources` | `message_id`, `source_reference_id` PK | Message/Source 삭제 시 cascade; Source row는 대화 삭제 후 유지 |

### FTS5

External-content FTS5 테이블과 INSERT/UPDATE/DELETE trigger:

- `library_fts(title, description, user_note)`
- `tag_fts(name)`
- `collection_fts(name, description)`
- `conversation_fts(title)`
- `message_fts(body)`
- `workspace_fts(name, description)`
- `task_fts(title, description)`
- `task_memory_fts(title, content)`

검색 입력은 quote/escape한 prefix AND query로 변환합니다. 결과는 원본 type/ID를 포함하며 UI에서 재조회해야 합니다.

---

## 7. 마이그레이션 이력

| DB | version | 내용 |
|---|---:|---|
| root | 1 | profiles/default index |
| root | 2 | profiles.icon (nullable) |
| profile | 1 | settings, bookmark_folders, bookmarks, history_visits |
| profile | 2 | sessions, session_tabs, secrets |
| profile | 3 | Library, tags/collections/sources, conversations/messages, workspaces/tasks/memories, FTS5 |

- `schema_migrations`에 적용 시각을 기록.
- `PRAGMA user_version` 동기화.
- 실패 시 트랜잭션 롤백, 이전 버전 유지.
- 이전 v1/v2 profile DB는 v3로 자동 업그레이드.
- 이전 v1 root DB는 v2로 자동 업그레이드(icon=NULL).

---

## 8. 쿼리/인덱스 원칙

- 모든 값은 prepared statement parameter binding.
- 일반 LIKE 검색은 `%`/`_`를 escape하고 `ESCAPE '\'` 사용.
- Library/AI/Workspace 검색은 FTS5 MATCH + parameter binding을 사용.
- 페이지네이션은 `LIMIT ? OFFSET ?` (limit 1..=500).
- 정렬은 명시적(`position, id`, `updated_at_ms DESC` 등).
- 프로필 DB는 파일 단위로 분리되어 교차 프로필 쿼리가 없습니다.

---

## 9. 백업 시 주의

프로필 DB를 복사할 때 다음을 함께 고려합니다.

- `amare.db`
- `amare.db-wal`
- `amare.db-shm`

정상 종료/체크포인트 후 백업하는 것이 가장 안전합니다.
