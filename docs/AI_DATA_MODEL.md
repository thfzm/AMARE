# AMARE AI 대화 데이터 모델

- 작성일: 2026-11-03
- 구현: `amare-store` profile DB v3
- 관련 문서: [DB 스키마](DATABASE_SCHEMA.md), [Library 아키텍처](LIBRARY_ARCHITECTURE.md), [보안 설계](SECURITY_DESIGN.md)

---

## 1. 범위

Phase 1.6은 대화/메시지/파트/출처를 로컬에 저장하는 **데이터 계층**만 구현합니다. 실제 AI API 호출, 웹 AI 자동화, 스트리밍 UI, 프롬프트 전송은 포함하지 않습니다.

---

## 2. conversations

| 필드 | 의미 |
|---|---|
| `title` | 대화 제목 |
| `workspace_id`, `task_id` | 선택적 Workspace/Task 연결 |
| `archived` | 보관 여부 |
| `deleted_at_ms` | soft delete 시각 (`NULL`이면 활성) |
| `last_message_at_ms` | 목록 정렬 기준 |

정책:

- 삭제는 기본적으로 **soft delete**입니다. `deleted_at_ms`가 설정되면 목록/search에서 제외됩니다.
- `restore`로 복구할 수 있습니다.
- `purge`만 메시지/파트/출처 링크를 cascade 삭제합니다.
- Workspace/Task 삭제 시 대화는 삭제되지 않고 연결만 `NULL`이 됩니다.

---

## 3. messages

| 필드 | 의미 |
|---|---|
| `role` | `system/user/assistant/tool` |
| `body` | 검색/표시용 평문 본문 |
| `status` | `pending/complete/error/cancelled` |
| `visibility` | `user/internal` |
| `position` | 대화 내 순서 |

- `visibility=internal`은 정책/도구 내부 메시지입니다. 기본 UI/transcript 조회에서는 제외됩니다.
- `visibility=user`만 일반 대화로 표시합니다.
- `MessagePart`는 `text/code/image/source/citation/tool_result/error/attachment`를 지원합니다.
- 메시지/파트 저장 전 길이 제한을 적용합니다.
- 명백한 private key PEM 블록은 `InvalidInput`으로 거부합니다.
- API 키/비밀번호/토큰은 메시지에 저장하지 않습니다. AI 계층은 `SecretRepository`의 키 핸들을 사용하고, 평문 secret을 프롬프트/로그/메시지에 넣지 않아야 합니다.

---

## 4. 출처/citation

- `source_references`는 URL/페이지 제목/사이트명/인용문/위치를 저장합니다.
- `message_sources(message_id, source_reference_id)`로 답변과 출처를 연결합니다.
- 메시지/대화 purge 시 링크는 삭제되지만 `SourceReference` 자체는 유지됩니다.
- 기존 AI 답변의 출처 정보는 원문 페이지가 변경/삭제되어도 저장된 인용문/URL을 유지합니다.

---

## 5. 검색

FTS5:

- `conversation_fts(title)`
- `message_fts(body)`

`ConversationRepository::search`는 삭제되지 않은 대화 제목을 검색합니다. 통합 검색은 사용자에게 보이는 메시지(`visibility=user`)만 검색하고 삭제된 대화를 제외합니다.

검색 결과는 `SearchKind::Conversation`/`SearchKind::Message`와 원본 row ID를 포함합니다. UI는 원본 ID로 다시 조회해야 합니다.

---

## 6. 보안/프라이버시

- 대화는 프로필 DB 파일 단위로 격리됩니다.
- AI 제공자에게 전송할 내용은 호출 계층에서 명시적으로 선택해야 합니다. 저장소가 자동 전송하지 않습니다.
- 내부 정책 메시지는 사용자 transcript와 분리합니다.
- 비밀 값은 `secrets` 테이블에만 암호문으로 저장합니다.
- 검색 snippet은 원문 텍스트이며 UI에서 escape해야 합니다.
