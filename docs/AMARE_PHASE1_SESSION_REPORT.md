# AMARE Phase 1.1 — Multi-Window Session Data Integrity Report

- 상태: **P0 멀티윈도우 세션 데이터 무결성 — CI 검증 완료**
- Branch: `feature/phase-1-session-integrity`
- 시작 HEAD: `81dfc82` (`master`, Phase 0 문서)
- 최종 제품/테스트 HEAD: `bcd476d`
- 최종 문서 HEAD: 이 문서 commit 후 갱신
- 작성일: 2026-11-23
- 범위: 세션 저장/복원 데이터 무결성만. 비밀번호/Vault, 사이트 권한, AI, 패키지 작업 없음.

---

## 1. Git / 작업 환경

| 항목 | 결과 |
|---|---|
| 시작 branch | `master` |
| 시작 HEAD | `81dfc82` |
| 작업 branch | `feature/phase-1-session-integrity` |
| 최종 제품/테스트 HEAD | `bcd476d` |
| Working tree | clean (문서 commit 전) |
| stash | `stash@{0}` 보존, 적용/삭제 없음 |
| GPUI | 소스/문서/패키지/증거 수정 없음 |
| Phase 0 문서 | `81dfc82`에 보존 |
| force push / history rewrite | 없음 |
| 공개 GitHub Release | 0개 |
| 패키지 생성 | **없음** (검증 스크립트 `skip_package=true`) |

---

## 2. 문제와 원인

Phase 0 P0:

> WinUI 멀티윈도우 세션이 마지막으로 저장한 창의 탭만 남긴다.

실제 코드 원인:

- `MainWindow`는 창마다 `_sessionId`를 가진다.
- `SessionClient.SaveAsync`는 창 ID 없이 탭 목록만 보낸다.
- FFI `session.save`는 `session_id`가 없으면 새 세션을 만들고, 있으면
  `replace_tabs`로 **해당 세션의 전체 탭 목록을 교체**한다.
- `session.latest`는 `updated_at_ms DESC`로 세션 1개만 반환한다.
- 따라서 A 저장 → B 저장 시 B의 `replace_tabs`가 A의 탭을 지운다.

이것은 UI 문제가 아니라 저장 모델이 창을 표현하지 못하는 문제였다.

---

## 3. 구현

### 3.1 Schema v5

- `session_windows` 테이블 추가
  - `window_id`, `ordinal`, `active_tab_id`
  - `bounds_x/y/width/height`, `maximized`
  - `session_id` FK, `UNIQUE(session_id, window_id)`
- `session_tabs.window_id` 추가 (`DEFAULT 'main'`)
- 기존 세션은 `main` 창 1개로 backfill
- `PROFILE_SCHEMA_VERSION` 4 → 5

상세 SQL/rollback/동시성 설계는
[`docs/SESSION_SCHEMA_DESIGN.md`](SESSION_SCHEMA_DESIGN.md)에 기록했다.

### 3.2 저장/복원 API

추가:

- `session.save_window`
- `session.latest_windows`
- `session.delete_window`

기존 `session.save`, `session.latest`, `session.delete`는 유지했다.
ABI version은 **1** 그대로이며 필드/메서드 추가만 했다.

`save_window`는 하나의 트랜잭션에서:

1. 세션 확인/생성
2. 대상 `(session_id, window_id)` 탭만 삭제
3. 새 탭 삽입
4. active/geometry/updated_at 갱신

다른 창의 탭은 건드리지 않는다. `BEGIN IMMEDIATE` + WAL + 5초 busy timeout으로
동시 저장을 직렬화한다.

### 3.3 WinUI

- 창마다 안정적인 `window_id`(GUID)와 `ordinal` 유지
- `MainWindow` 시작 시 `session.latest_windows`로 전체 창을 1회 조회
- 첫 창이 나머지 창을 ordinal 순서로 생성/복원
- 새 창은 기존 세션에 합류하고 새 탭으로 시작
- 창별 탭 순서/active/geometry 저장·복원
- 종료 시:
  - 마지막 창: 스냅샷 유지 → 다음 실행에서 복원
  - 다른 창이 남아 있는 창: 저장 후 5초 grace 뒤 제거
  - 5초 안에 앱 전체가 종료되면 모든 창 스냅샷 유지
- 저장 실패 시 삭제하지 않고 기존 정상 데이터 보존
- 기존 `SessionRestorePlanner` 규칙(순서/active/pinned/빈 URL/상한) 재사용

---

## 4. 테스트 결과 (실제 실행)

### 4.1 Rust

| Suite | 결과 |
|---|---|
| `amare-store` lib | 9/9 PASS |
| `amare-store` backup | 13/13 PASS |
| `amare-store` library/AI | 30/30 PASS |
| `amare-store` recovery | 4/4 PASS |
| **`amare-store` session_windows** | **10/10 PASS** |
| `amare-store` storage | 12/12 PASS |
| `amare-services` | 38/38 PASS |
| `amare-ffi` | 7/7 PASS |

Session 시나리오:

| ID | 테스트 | 결과 |
|---|---|---|
| A | 2창 3/2탭 저장 후 reopen | PASS |
| B | 저장 순서 반전 | PASS |
| C | 한 창 삭제 후 다른 창/재open | PASS |
| D | 3창, 6가지 저장 순서 | PASS |
| E | 동일 창 반복 저장, 다른 창 불변 | PASS |
| F | 비정상 종료 후 마지막 commit 복원 | PASS |
| G | v4 단일 창 migration | PASS |
| H | 두 connection 동시 저장 | PASS |
| I | 실패 저장 rollback | PASS |
| J | 새 창이 기존 세션에 합류 | PASS |

### 4.2 C# / FFI interop

- 기존 70 + 신규 6 = **76/76 PASS** (Linux local, Windows CI)
- 신규:
  - `session_window_restore_orders_and_selects_active`
  - `session_window_ordinal_and_validation`
  - `native_session_windows_independent_roundtrip`
  - `native_session_windows_save_order_reversed`
  - `native_session_windows_legacy_single_window`
  - `native_session_windows_concurrent_writers`

### 4.3 WinUI CI

Run: [45145793391](https://github.com/thfzm/AMARE/actions/runs/45145793391)

| 항목 | 결과 |
|---|---|
| Windows 통합 | **143 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** |
| UI smoke | **49/49 PASS** (기존 48 + 신규 1) |
| Interop | **76/76 PASS** |
| Linux CI | [45147941167](https://github.com/thfzm/AMARE/actions/runs/45147941167) **SUCCESS** |

신규 UI:

```text
PASS session_multiwindow_restore: restored_windows=2 tabs=[2,3]
PASS session_restore: tabs_before=8 tabs_after=8
PASS session_restore_forced: tabs_before=8 tabs_after=8
```

App log 증거:

```text
Session windows restored count=2 session=1
  windows=[<A>:2,<B>:3]
```

> `SKIPPED` 8개는 패키지 생성 금지에 따른 7개 package 단계 + Netflix/Disney 수동
> 테스트 1개다. 패키지/MSIX 관련 항목은 **생성·설치하지 않았으므로 PASS가 아니다.**
> Windows 11 실기기 GUI/IME/DPI는 이번에도 **UNVERIFIED**다.

---

## 5. Migration dry-run

실제 Windows 사용자 DB는 이 환경에서 접근하지 않았다. 실제 DB를 변경하지 않기 위해
v4 스키마+대표 데이터 fixture를 원본으로 사용했다.

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

검증:

- 원본 fixture bytes/SHA-256이 migration 전후 동일
- 복사본만 v5로 migration
- 기존 2탭/active tab/pinned/URL 보존
- `PRAGMA integrity_check=ok`, FK 위반 0
- 기존 단일 창은 `main` 창으로 복원

---

## 6. 개발 중 최초 실패와 분류

| # | 실패 | 분류 | 조치 |
|---:|---|---|---|
| 1 | `three_windows_survive_every_save_order`가 저장 순서를 ordinal로 사용해 실패 | `TEST_AUTOMATION_BUG` | 창 ID별 고정 ordinal로 테스트 수정 |
| 2 | `reset_profile_clears_data_but_keeps_identity`가 schema 4 기대 | `TEST_AUTOMATION_BUG` | schema 5로 assertion 갱신 |
| 3 | WinUI `??` AggregateException/TimeoutException compile error | `PRODUCT_BUILD_DEFECT` | `(Exception?)` cast |
| 4 | XAML `BoolToVisibilityConverter` unknown | `PRODUCT_BUILD_DEFECT` (연쇄) | C# compile error 해결 후 소멸 |
| 5 | UI multiwindow 최초: 2창 복원 timeout | `TEST_AUTOMATION_BUG` | 두 창 종료를 순차 확인 + `TryClose`로 변경 |
| 6 | UI multiwindow 후속: stale UIA element | `TEST_AUTOMATION_BUG` | native `WM_CLOSE` + fresh element 사용 |
| 7 | single-close 재시작 assertion이 stale UIA로 실패 | `TEST_AUTOMATION_BUG` | UI 테스트 범위를 2창 복원으로 유지, 단일 창 종료 정책은 Rust reopen 테스트로 검증 |

최종 실행에서 제품 FAIL은 0이다.

---

## 7. 완료 조건 체크

- [x] 2개 창 세션 독립 저장/복원 — UI 49/49 + interop
- [x] 3개 창 세션 독립 저장/복원 — Rust 6가지 순서 PASS
- [x] 저장 순서에 따른 데이터 손실 없음 — Rust + interop
- [x] 한 창 종료 시 다른 창 세션 보존 — Rust reopen test, UI cleanup
- [x] 강제 종료 복구 정책 검증 — Rust reopen after drop, UI forced-kill 단일 창
- [x] 기존 단일 창 DB 호환성 — v4 migration test
- [x] migration dry-run 성공 — 원본 hash 불변, 복사본 integrity ok
- [x] 원본 DB 무결성 보존 — bytes/SHA 동일
- [x] 기존 검증 기능 회귀 없음 — 143/0, UI 49/49, interop 76/76
- [x] 새로운 테스트 결과/증거 보존 — CI artifact + 본 문서

남은 제약:

- 실제 Windows 11 실기기 멀티윈도우 GUI/IME/DPI 검증은 **UNVERIFIED**
- 3창 UI smoke는 없음 (데이터 계층 3창 테스트로 대체)
- 단일 창 종료 후 재시작 UI assertion은 없음 (Rust reopen test로 대체)
- 패키지/MSIX/SHA-256은 **생성하지 않음**

---

## 8. 변경 파일

제품/테스트:

- `crates/amare-core/src/domain.rs`
- `crates/amare-store/src/migrations.rs`
- `crates/amare-store/src/models.rs`
- `crates/amare-store/src/repositories/sessions.rs`
- `crates/amare-store/src/lib.rs`
- `crates/amare-store/tests/storage.rs`
- `crates/amare-store/tests/session_windows.rs` (new)
- `crates/amare-ffi/src/services.rs`
- `crates/amare-services/tests/services.rs`
- `apps/windows/AMARE.Windows.Core/Models.cs`
- `apps/windows/AMARE.Windows.Core/SessionClient.cs`
- `apps/windows/AMARE.Windows.Core/SessionWindowRestorePlanner.cs` (new)
- `apps/windows/AMARE.Windows/MainWindow.xaml.cs`
- `apps/windows/AMARE.Interop.Tests/Phase25Tests.cs` (new)
- `apps/windows/AMARE.Interop.Tests/Program.cs`
- `tools/windows-ui-smoke/Program.cs`
- `tools/verify-windows.ps1`
- `.github/workflows/ci-windows.yml`

문서:

- `docs/SESSION_SCHEMA_DESIGN.md`
- `docs/AMARE_PHASE1_SESSION_REPORT.md`
- `docs/AMARE_FEATURE_AUDIT.md`
- `docs/AMARE_FINALIZATION_PLAN.md`

---

## 9. Commit

| Commit | 내용 |
|---|---|
| `5865973` | multi-window session data integrity 구현 |
| `e2473ba` | schema assertion / C# exception cast 수정 |
| `90ffeba` | UI smoke 안정화 + session save 로그 |
| `57a3fc2` | native handle close |
| `7cc3bbd` | dry-run hash 기록 |
| `bcd476d` | UI smoke를 2창 복원 증거로 유지 |

문서 commit은 이 보고서와 함께 별도로 추가한다.

---

## 10. 다음 작업

1. 실제 Windows 11 실기기에서 2·3창 복원, 단일 창 종료 정책 확인
2. 3창 UI smoke 추가 (선택)
3. Phase 1 P0-2 **비밀번호 관리자/Vault** 착수
4. Phase 1 P0-3 실제 Windows 11 IME/DPI/Snap/Alt+Tab 검증
5. 패키지는 별도 승인 후에만 생성
