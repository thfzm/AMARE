# AMARE Session Schema Design — Phase 1.1

- 상태: **설계 확정 / 구현·검증 완료 (CI)**
- 작성일: 2026-11-23
- 대상: WinUI 3 세션 저장/복원, Rust `amare-store`/`amare-services`/`amare-ffi`
- 관련 이슈: Phase 0 P0-1 멀티윈도우 세션 last-writer-wins
- 원칙: 기존 단일 창 세션을 깨지 않는다. 원본 사용자 DB를 직접 migration하지 않는다.

---

## 1. 기존 스키마와 문제점

현재 프로필 DB schema v4:

```sql
CREATE TABLE sessions (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    name           TEXT NOT NULL,
    active_tab_id  INTEGER NULL,
    created_at_ms  INTEGER NOT NULL,
    updated_at_ms  INTEGER NOT NULL
);

CREATE TABLE session_tabs (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id          INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    url                 TEXT NOT NULL,
    title               TEXT NOT NULL,
    position            INTEGER NOT NULL,
    pinned              INTEGER NOT NULL DEFAULT 0 CHECK (pinned IN (0, 1)),
    group_id            TEXT NULL,
    last_accessed_at_ms INTEGER NOT NULL
);
```

문제:

1. `sessions` 1행 = 탭 목록 1개다. 창 개념이 없다.
2. WinUI 각 창은 독립적인 `_sessionId`를 가지고 `session.save`를 호출한다.
3. `session.save`에 `session_id`가 없으면 새 세션을 만들고, 있으면
   `replace_tabs`로 **해당 세션의 전체 탭 목록을 교체**한다.
4. `session.latest`는 `updated_at_ms DESC`로 세션 1개만 반환한다.
5. 따라서 A창 저장 후 B창 저장 시, 마지막으로 저장한 창의 탭만 남는다.
6. 창을 하나만 닫아도 다른 창의 세션이 삭제/교체될 수 있다.

이 문제는 UI 문제가 아니라 저장 모델의 표현력 부족이다. 탭을 창 단위로
분리하지 않으면 어떤 호출 순서에서도 데이터 손실을 막을 수 없다.

---

## 2. 새 스키마 제안 (profile schema v5)

### 2.1 개념

- `sessions`: 하나의 복원 그룹(앱 실행 단위)을 나타낸다.
- `session_windows`: 그룹 안의 창 1개를 나타낸다.
- `session_tabs`: 탭 1개. `window_id`로 창에 귀속된다.
- `window_id`: 창 생성 시 만든 GUID 문자열. 재시작 후에도 유지된다.
- `ordinal`: 창 복원 순서. 작을수록 먼저 복원된다.
- `active_tab_id`: 그 창의 활성 탭 ID.
- geometry: 창 위치/크기/최대화 상태. 복원 실패 시 무시 가능한 메타데이터다.

### 2.2 SQL

```sql
-- v5
ALTER TABLE session_tabs
    ADD COLUMN window_id TEXT NOT NULL DEFAULT 'main';

CREATE TABLE session_windows (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id      INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    window_id       TEXT NOT NULL,
    ordinal         INTEGER NOT NULL DEFAULT 0,
    active_tab_id   INTEGER NULL,
    bounds_x        REAL NULL,
    bounds_y        REAL NULL,
    bounds_width    REAL NULL,
    bounds_height   REAL NULL,
    maximized       INTEGER NOT NULL DEFAULT 0 CHECK (maximized IN (0, 1)),
    updated_at_ms   INTEGER NOT NULL,
    UNIQUE(session_id, window_id)
);

CREATE INDEX idx_session_windows_session
    ON session_windows(session_id, ordinal, id);

CREATE INDEX idx_session_tabs_window
    ON session_tabs(session_id, window_id, position, id);
```

### 2.3 기존 데이터 변환

기존 `sessions`/`session_tabs`는 모두 단일 창으로 간주한다.

```sql
INSERT INTO session_windows
    (session_id, window_id, ordinal, active_tab_id,
     bounds_x, bounds_y, bounds_width, bounds_height,
     maximized, updated_at_ms)
SELECT
    s.id,
    'main',
    0,
    s.active_tab_id,
    NULL, NULL, NULL, NULL,
    0,
    s.updated_at_ms
FROM sessions s
WHERE NOT EXISTS (
    SELECT 1 FROM session_windows w WHERE w.session_id = s.id
);
```

`session_tabs.window_id`는 DEFAULT `'main'`이므로 기존 행은 자동으로
`main` 창에 속한다. 기존 `sessions.active_tab_id`도 그대로 보존된다.

### 2.4 새 세션 생성 규칙

- 새 세션을 만들면 `session_windows`에 최소 1개 창을 만든다.
- 창 저장은 `(session_id, window_id)`에 대해 **해당 창의 탭만** 교체한다.
- 세션의 `updated_at_ms`는 창 저장 시 갱신한다.
- `sessions.active_tab_id`는 legacy 호환용으로 유지한다.
  - `main` 창이 있으면 `main`의 active tab을 반영한다.
  - windowed 세션에서는 첫 창(ordinal 최소)의 active tab을 반영한다.

---

## 3. API 계약

ABI version은 **1을 유지**한다. 필드/메서드 추가는 하위 호환이다.

### 3.1 새 메서드

```text
session.save_window
  params:
    context
    session_id: number | null
    name: string
    window_id: string (1..128, GUID 권장)
    ordinal: integer
    tabs: [{url,title,pinned,group_id,position}]
    active_position: integer | null
    bounds_x/bounds_y/bounds_width/bounds_height: number | null
    maximized: boolean
  result: { session: {...}, windows: [ {window_id, ordinal, active_tab_id,
            bounds_*, maximized, updated_at_ms, tabs:[...]}, ... ] }

session.latest_windows
  params: context
  result: { session, windows:[...] } | null

session.delete_window
  params: context, session_id, window_id
  result: { deleted: boolean }
```

### 3.2 기존 메서드

- `session.save`, `session.latest`, `session.delete`는 그대로 유지한다.
- `session.save`는 `window_id='main'`인 단일 창으로 동작한다.
- `session.latest`는 기존과 같이 세션 1개를 반환한다. 새 클라이언트는
  `session.latest_windows`를 사용한다.
- 새 DB를 구버전 앱이 읽으면 모든 창의 탭을 평면 목록으로 볼 수 있다.
  (창 그룹은 잃지만 데이터는 잃지 않는다.)

### 3.3 C# DTO

```csharp
SessionWindowDto {
  WindowId, Ordinal, ActiveTabId,
  BoundsX, BoundsY, BoundsWidth, BoundsHeight,
  Maximized, UpdatedAtMs, Tabs
}
SessionWindowsStateDto { Session, Windows }
```

`SessionRestorePlanner`는 기존 `SessionStateDto` 경로를 유지하고,
창 단위 복원은 `SessionWindowDto`를 `SessionStateDto`로 변환해 재사용한다.
복원 규칙(순서, active, pinned, 빈 URL, 상한)은 변경하지 않는다.

---

## 4. 동시 저장 처리

- SQLite WAL, `busy_timeout=5s`, `BEGIN IMMEDIATE`.
- `save_window`는 다음을 **하나의 트랜잭션**으로 수행한다.
  1. 세션 존재 확인/생성
  2. `session_windows` upsert
  3. 해당 `(session_id, window_id)`의 탭만 삭제
  4. 새 탭 삽입
  5. `active_tab_id`, geometry, `updated_at_ms` 갱신
- 다른 창의 탭 행은 건드리지 않는다.
- 같은 창의 동시 저장은 마지막 트랜잭션이 이긴다. 창 내부 저장은
  C# `_sessionSaveGate`로 직렬화되어 있다.
- 여러 창의 동시 저장은 SQLite write lock으로 직렬화된다.
- 프로세스 내 첫 세션 생성 경합은 C#의 process-wide 저장 gate로 직렬화한다.
- 저장 실패 시 트랜잭션 rollback으로 이전 정상 데이터가 남는다.

---

## 5. 창 종료/복원 정책

### 5.1 창 종료

- 창이 닫힐 때:
  1. 현재 창 상태를 저장한다.
  2. 같은 프로세스에 다른 창이 살아 있으면 이 창의 `session_windows` 행을 삭제한다.
  3. 마지막 창이면 행을 유지한다. 다음 실행에서 복원한다.
- 이 정책으로 A만 닫고 B가 남았을 때 A가 재시작에서 되살아나지 않는다.
- 강제 종료/크래시 시에는 `Closed`가 호출되지 않으므로 마지막 저장된
  A/B 창이 모두 남는다.

### 5.2 앱 시작

- 첫 `MainWindow`가 `session.latest_windows`를 1회 조회한다.
- windows가 있으면 ordinal 순서로 창을 만든다.
- 각 창은 자신의 `window_id`/탭/active/geometry를 복원한다.
- 창이 1개면 기존 단일 창 동작과 동일하다.
- `restore_session=false`이면 새 탭 1개만 만든다.

### 5.3 Geometry

- `AppWindow.Position/Size`와 `OverlappedPresenter.State`를 저장한다.
- 복원 시 유효 범위를 확인하고, 화면 밖이면 복원을 건너뛴다.
- geometry 실패는 세션 복원 실패로 취급하지 않는다.

---

## 6. Migration / rollback

### 6.1 Migration

- `PROFILE_SCHEMA_VERSION`을 4 → 5로 올린다.
- 위 SQL을 하나의 migration으로 적용한다.
- `schema_migrations`와 `PRAGMA user_version`은 기존 메커니즘을 그대로 사용한다.
- 실패 시 SQLite transaction rollback으로 v4가 유지된다.

### 6.2 Rollback

- 자동 down migration은 제공하지 않는다. v5는 additive이므로 v4 앱은
  새 테이블/컬럼을 무시하고 읽을 수 있다.
- v4 앱의 `replace_tabs`는 기존처럼 전체 탭을 교체할 수 있으므로,
  다운그레이드 시 창 그룹이 평면화될 수 있다.
- 심각한 손상 시 `PROFILE_SCHEMA_VERSION` 이전의 백업에서 복구한다.
  기존 `BackupManager`가 schema version을 검사한다.

### 6.3 Dry-run

- 실제 사용자 DB는 변경하지 않는다.
- v4 스키마와 대표 데이터(2개 세션, 5개 탭, active tab)를 가진 fixture DB를
  temp 디렉터리에 만든다.
- fixture 원본 SHA-256을 기록한다.
- fixture를 복사한 뒤 복사본에만 v5 migration을 적용한다.
- 원본 SHA-256이 변하지 않았는지, 복사본의 행 수/active/URL이 보존됐는지 확인한다.
- `PRAGMA integrity_check`와 `foreign_key_check`를 기록한다.

---

## 7. 테스트 시나리오와 예상 결과

| ID | 시나리오 | 예상 |
|---|---|---|
| A | A 3탭/B 2탭 저장 후 재시작 | A 3 + B 2, 순서/active 유지 |
| B | B 저장 후 A 저장 | 두 창 모두 유지 |
| C | A/B 중 A만 닫기 | B 유지, A는 다음 복원에서 제외 |
| D | A/B/C, 저장 순서 6가지 | 모든 창 유지 |
| E | 같은 창 반복 저장 | 마지막 상태만 반영, 다른 창 불변 |
| F | 강제 종료 | 마지막 commit된 A/B 유지 |
| G | v4 단일 창 DB migration | 기존 탭/active 보존, `main` 창으로 복원 |
| H | 동시 저장 | lost update 없음 |
| I | 저장 실패 rollback | 이전 정상 데이터 유지 |
| J | 새 창이 기존 세션에 합류 | 별도 세션 생성 없음 |

추가 검증:

- `cargo test -p amare-store`
- `cargo test -p amare-ffi`
- C# interop `session.*_window*`
- WinUI UI smoke 48 + 멀티윈도우 세션 시나리오
- 기존 `session_restore`, `session_restore_forced` 유지

---

## 8. 구현 결과

- 단일 창: 기존과 동일하게 1개 창/탭 복원.
- 2·3창: 각 창의 탭/순서/active가 독립적으로 복원.
- 저장 순서와 무관하게 데이터 손실 없음.
- 한 창 닫기: 닫은 창만 제거, 나머지 유지.
- 강제 종료: 마지막 정상 저장 상태 복원.
- v4 DB: migration 후 기존 세션이 `main` 창으로 복원.
- 저장 실패: 이전 정상 데이터 유지.

### 8.1 실제 검증 결과

| 검증 | 결과 |
|---|---|
| Rust `session_windows` | **10/10 PASS** |
| Rust store 전체 | **78/78 PASS** |
| `amare-services` | **38/38 PASS** |
| `amare-ffi` | **7/7 PASS** |
| C# interop | **76/76 PASS** |
| WinUI UI smoke | **49/49 PASS** |
| Windows 통합 | **143 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** |
| Linux CI | SUCCESS |

`SKIPPED` 8개는 package 생성 금지에 따른 7개 단계와 수동 Netflix 테스트 1개다.
package/MSIX는 생성·설치하지 않았다.

### 8.2 Migration dry-run 실제 기록

v4 fixture(1 session/2 tabs, active tab/pinned 포함)를 원본으로 사용했다.

```text
original_sha256=f5ec529f07ec5027976f202ec9c7f0e693106ff0c2783683492d04248487966a
original_bytes=28672
original_unchanged=true
copy_sha256=<run-dependent; SQLite page allocation/timestamps>
integrity_check=ok
foreign_key_violations=0
session_windows=1
session_tabs.window_id=main
```

실제 Windows 사용자 DB는 이 환경에서 접근하지 않았고 변경하지 않았다.

---

## 9. 하지 않는 것

- 세션 UI/창 그룹 기능 추가
- 비밀번호/Vault, 사이트 권한, AI 변경
- GPUI 수정
- 실제 사용자 DB 직접 migration
- 패키지 생성/서명/설치
- assertion 완화
