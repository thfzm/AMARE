# AMARE Workspace / Task 기억 모델

- 작성일: 2026-11-03
- 구현: `amare-store` profile DB v3
- 관련 문서: [DB 스키마](DATABASE_SCHEMA.md), [AI 데이터 모델](AI_DATA_MODEL.md)

---

## 1. 범위

Workspace는 장기 작업 공간, Task는 실행 단위, TaskMemory는 조사 결과/결정/사용자 규칙/사실/진행/미해결 질문을 보존하는 로컬 기억입니다. Phase 1.6은 저장/검색/명시적 공유만 구현하며 AI 자동 기억 생성은 하지 않습니다.

---

## 2. workspaces

| 필드 | 의미 |
|---|---|
| `name`, `description`, `icon` | 표시 정보 |
| `archived` | 보관 여부 |

- Workspace 삭제는 소속 Task/TaskMemory/공유 링크를 cascade 삭제합니다.
- 연결된 Conversation은 삭제하지 않고 `workspace_id/task_id`만 `NULL`로 만듭니다.

## 3. tasks

| 필드 | 의미 |
|---|---|
| `workspace_id` | 소속 Workspace (필수) |
| `title`, `description` | 작업 내용 |
| `status` | `open/in_progress/done/archived` |

Task 삭제는 소속 TaskMemory와 공유 링크를 cascade 삭제합니다. 연결된 Conversation은 유지되고 `task_id`만 `NULL`이 됩니다.

## 4. task_memories

| 필드 | 의미 |
|---|---|
| `task_id` | 소유 Task |
| `title`, `content` | 기억 내용 |
| `memory_type` | `research_result/decision/user_rule/fact/progress/open_question` |
| `source_reference_id` | 선택적 출처 (삭제 시 `NULL`) |
| `importance` | 0..5 |
| `user_confirmed` | 사용자 확인 여부 |

### 명시적 공유

`task_memory_links(memory_id, target_task_id)`는 Task 간 기억 공유를 나타냅니다.

- 소유 Task 외에는 자동으로 조회되지 않습니다.
- `share_with_task`를 호출해야만 다른 Task에서 `include_shared=true`로 볼 수 있습니다.
- 공유 해제는 `unshare_from_task`로 수행합니다.
- 원본 TaskMemory를 삭제하면 공유 링크도 cascade 삭제됩니다.

---

## 5. 검색

FTS5 인덱스:

- `workspace_fts(name, description)`
- `task_fts(title, description)`
- `task_memory_fts(title, content)`

통합 검색은 `SearchKind::Workspace/Task/TaskMemory`와 원본 ID를 반환합니다. 검색어는 FTS5 연산자로 해석되지 않도록 quote/escape합니다.

---

## 6. 프라이버시

- Workspace/Task/Memory는 프로필 DB 파일 단위로 격리됩니다.
- 기억은 자동으로 AI에 전송되지 않습니다.
- 사용자 확인(`user_confirmed`)과 출처를 함께 저장해 신뢰 근거를 남깁니다.
- 비밀 값은 `SecretRepository`만 사용합니다.
