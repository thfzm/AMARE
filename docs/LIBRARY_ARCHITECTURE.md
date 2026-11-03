# AMARE Library 아키텍처

- 작성일: 2026-11-03
- 구현: `amare-store` profile DB v3
- 관련 문서: [DB 스키마](DATABASE_SCHEMA.md), [AI 데이터 모델](AI_DATA_MODEL.md), [백업/복원](BACKUP_RESTORE.md)

---

## 1. 범위

Phase 1.6 Library는 **플랫폼 독립 로컬 데이터 계층**입니다. 브라우저 UI, WebView2 캡처, AI 요약/임베딩, 외부 전송은 포함하지 않습니다.

포함:

- `LibraryItem`: 웹 페이지, 링크, 발췌, 노트, 이미지, PDF, 문서, 리서치, 요약
- `Tag`, `Collection` 및 다대다 연결
- `SourceReference`: 원문 URL, 페이지 제목, 사이트명, 인용문, 위치 정보
- FTS5 검색
- 보관/삭제(휴지통)/영구 삭제 정책

제외:

- 실제 페이지 본문/이미지 파일 저장
- 자동 크롤링/스크래핑
- AI 요약 생성
- 외부 클라우드 동기화

---

## 2. 데이터 모델

### library_items

| 필드 | 의미 |
|---|---|
| `title` | 사용자에게 표시할 제목 |
| `description` | 요약/설명 |
| `kind` | `web_page/link/excerpt/note/image/pdf/document/research/summary/other` |
| `original_url` | 원문 URL (선택) |
| `source_name` | 사이트/출처 이름 (선택) |
| `image_ref` | 썸네일/이미지 참조 (선택, 파일 자체가 아님) |
| `user_note` | 사용자 메모 |
| `favorite` | 즐겨찾기 |
| `status` | `active/archived/trashed` |
| `sort_order` | 사용자 정렬 |
| timestamps | 생성/수정/마지막 접근 |

### tags / collections

- Tag 이름은 앞뒤/중복 공백을 정리해 표시하고, Unicode lowercase 정규화 값으로 유일성을 보장합니다.
- Tag/Collection 삭제는 연결만 cascade되고 LibraryItem은 유지됩니다.
- Collection 삭제는 항목을 삭제하지 않습니다.

### source_references

- URL/페이지 제목/사이트명/인용문/위치/수집 시각을 저장합니다.
- `library_item_id`는 `ON DELETE SET NULL`입니다.
- LibraryItem을 영구 삭제해도 출처 인용은 보존됩니다.
- AI 메시지 인용은 `message_sources`로 별도 연결합니다. 출처 자체는 대화 삭제 후에도 남습니다.

---

## 3. 삭제 정책

| 동작 | 결과 |
|---|---|
| `archive` (`set_status(Archived)`) | 목록/search에서 기본 제외, 데이터 유지 |
| `trash` | 휴지통 상태, search 제외 |
| `purge` | LibraryItem 영구 삭제, tag/collection 연결 cascade |
| Tag/Collection 삭제 | 연결만 삭제, 항목 유지 |
| LibraryItem purge | SourceReference 유지, `library_item_id=NULL` |

삭제는 명시적 사용자 동작으로만 수행합니다. AI 계층이 Library를 자동 삭제해서는 안 됩니다.

---

## 4. 검색

FTS5 external-content 인덱스:

- `library_fts(title, description, user_note)` → `library_items`
- `tag_fts(name)` → `tags`
- `collection_fts(name, description)` → `collections`

`LibraryRepository::search`는 제목/설명/메모를 검색하며 `trashed` 항목을 제외합니다. 검색어는 각 token을 quote/escape한 prefix AND query로 변환해 FTS5 연산자 주입을 막습니다.

통합 검색(`SearchRepository`)은 `SearchHit { kind, id, title, snippet }`를 반환합니다. UI는 반드시 원본 `kind/id`로 Repository에서 다시 조회해야 합니다. `snippet`은 원문 텍스트이므로 UI에서 HTML escape해야 합니다.

---

## 5. 보안/프라이버시

- Library는 프로필 DB 파일 단위로 격리됩니다.
- AI/웹 AI에 자동 전송하지 않습니다. 사용자가 명시적으로 선택한 항목만 AI 입력으로 전달할 수 있습니다.
- `SourceReference`는 원문 보존을 위해 AI 답변 삭제 후에도 유지됩니다.
- 비밀번호/API 키/토큰은 Library/Message에 저장하지 않습니다. `SecretRepository`를 사용합니다.
