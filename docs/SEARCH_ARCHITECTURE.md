# AMARE 검색 아키텍처

- 구현: `SearchService` (`crates/amare-services/src/search.rs`)
- 기반: `SearchRepository` + FTS5 + Bookmark/History Repository
- 관련 문서: [서비스 아키텍처](SERVICE_ARCHITECTURE.md), [DB 스키마](DATABASE_SCHEMA.md)

---

## 1. 검색 대상

| SearchKind | 원본 | 인덱스 |
|---|---|---|
| LibraryItem | library_items | library_fts |
| Tag | tags | tag_fts |
| Collection | collections | collection_fts |
| Conversation | conversations | conversation_fts |
| Message | messages | message_fts |
| Workspace | workspaces | workspace_fts |
| Task | tasks | task_fts |
| TaskMemory | task_memories | task_memory_fts |
| Bookmark | bookmarks | LIKE (Repository) |
| History | history_visits (URL 집계) | LIKE (Repository) |

결과는 `SearchHit { kind, id, title, snippet, updated_at_ms }`입니다. 동일한 숫자 ID가 여러 종류에 존재할 수 있으므로 **kind와 id를 반드시 함께** 사용합니다.

---

## 2. API

```rust
let request = SearchRequest::new("ownership")
    .with_kinds([SearchKind::LibraryItem, SearchKind::Bookmark])
    .with_page(20, 0)
    .with_sort(SearchSort::TitleAsc);
let page = SearchService::new(&profiles).search(&context, &request)?;
```

`SearchPage { profile_id, query, items, total, limit, offset, sort }`.

- `kinds`가 비어 있으면 전체 10종을 검색합니다.
- 결과 kind 필터와 원본 ID를 보존합니다.
- `total`은 선택된 source별 count 합계입니다.
- pagination은 최대 limit 100, offset 400으로 제한합니다.
- query는 trim 후 1..512 bytes입니다.
- 빈 query는 `EmptyQuery`, 너무 긴 query는 `QueryTooLong`.

---

## 3. 정렬

| 값 | 의미 |
|---|---|
| `Relevance` | FTS bm25/Repository 순서를 유지한 source 순서 |
| `TitleAsc` | 제목 대소문자 무시 오름차순, kind/id tie-break |
| `UpdatedDesc` | updated/visit 시각 내림차순, kind/id tie-break |

`Relevance`는 서로 다른 FTS 테이블의 bm25 점수를 직접 비교하지 않습니다. 전역 relevance 모델이 필요하면 후속 Phase에서 별도 점수 정규화/임베딩을 설계합니다.

---

## 4. 안전성/프로필 격리

- `ProfileContext` generation을 먼저 검증합니다. 오래된 context는 `ProfileSwitched`.
- 검색은 프로필별 DB 연결에서만 수행됩니다.
- FTS 입력은 token별 quote/escape + prefix AND로 변환해 `OR`, `NOT`, `NEAR(`, `"` 등이 연산자로 해석되지 않게 합니다.
- SQL은 모두 prepared statement parameter binding입니다.
- snippet은 원문 텍스트이므로 UI에서 HTML escape해야 합니다.
- 검색 결과는 원본 테이블과 JOIN되거나 소유 Repository에서 조회되므로 참조 유효성을 유지합니다.

---

## 5. soft delete/보관 정책

| 데이터 | 검색 |
|---|---|
| Library active/archived | 포함 |
| Library trashed | 제외 |
| Conversation active/archived | 포함 |
| Conversation soft-deleted | 제외 |
| Message visibility=user | 포함 |
| Message visibility=internal | 제외 |
| Workspace active | 포함 |
| Workspace archived | 제외 |
| Task active/in_progress/done | 포함 |
| Task archived | 제외 |
| TaskMemory의 Task archived | 제외 |
| Bookmark/History | 삭제된 row만 제외 |

이 정책은 Phase 1.6 재감사에서 서비스 검색 기준으로 명시했습니다.

---

## 6. 성능

- FTS5 MATCH와 `bm25` 정렬을 사용하며 전체 테이블을 앱으로 로딩하지 않습니다.
- pagination은 선택된 source별 `offset+limit`(최대 500)만 조회한 뒤 병합합니다.
- total은 COUNT 쿼리로 계산합니다.
- Bookmark/History는 소유 Repository의 LIKE 검색을 사용합니다.
- 현재 검색은 단일 프로필 내부에서만 동작하며 교차 프로필 검색은 지원하지 않습니다.

측정값은 [PHASE1_SCAFFOLD](PHASE1_SCAFFOLD.md)의 환경/실행 결과에 기록하며 목표치를 측정값처럼 사용하지 않습니다.
