# AMARE Phase 2.7 — Backup & Restore UI Completion 보고서

- 상태: **PASS (Rust/C#/Windows/Linux CI)**. 실제 Windows 11 실기기/전원 손실은 UNVERIFIED.
- 작업 branch: `feature/phase-2-backup-restore-ui`
- 기준 branch: `feature/phase-2-profile-backup-restore`
- 기준 제품/테스트 HEAD: **`6ff6e4b`**
- 기준 문서 HEAD: **`0b63fbe`**
- 최종 제품/테스트 HEAD: **`3fff704`**
- 문서 내용 HEAD: **`9780e8b`** (이 보고서, design, audit/plan/testing 포함)
- DB schema 변경: **없음** (profile schema v6 유지)
- 백업 포맷/암호화 방식 변경: **없음** (기존 v2/Argon2id/XChaCha20 유지)
- MSIX/서명/공개 Release: **없음**

---

## 1. 최종 요약

| 완료 기준 | 판정 | 근거 |
|---|---|---|
| Full Profile Restore Settings UI | **PASS (CI)** | full manifest, full restore button, confirmation, `--mode full` |
| 기존/새 profile restore | **PASS (Phase 2.6 + UI 확장)** | source/target profile selector, restore-as-new |
| 지원 Partial Restore UI 연결 | **PASS (profile 단위)** | full backup에서 단일 source profile 선택 복원 |
| 복원 진행 상태/안전 취소 | **PASS (구현/Rust)** | helper staged restore + progress file; apply 이후 취소 불가 |
| 실패 시 기존 데이터/recovery 보존 | **PASS (Phase 2.6 + journal)** | prepared/applying journal, recovery |
| 다중 창/동시성 | **PASS (lock/종료 정책)**; 실제 race UNVERIFIED | `.backup.lock`, app 전체 종료 후 apply |
| Phase 2.6/updater 회귀 | **PASS (CI)** | backup-ci 10/10, portable E2E 11/11 |
| P0 데이터 손실/credential 노출 | **없음** | credential은 helper stdin/메모리만 |
| 실기기 절차 문서화 | **PASS** | report section 9 |
| 문서/감사/계획 갱신 | **PASS** | report/design/audit/plan/testing |
| branch clean/remote sync | **PASS** | 최종 확인 |

---

## 2. 기준점 / branch

```text
feature/phase-2-profile-backup-restore
  6ff6e4b  Remove unused Windows-only test import
  0b63fbe  Record Phase 2.6 docs HEAD

feature/phase-2-backup-restore-ui (신규)
  11fc123  Add full profile restore UI and staged restore progress
  45bf86b  Separate Vault password and fix full restore UI test
  bec5d37  Fix Windows-only Vault test and expose full restore button
  3fff704  Fix full restore dialog flow and nested dialog handling
  <docs>   Document Phase 2.7 backup/restore UI completion
```

기존 Phase 2.6 구현/증거, GPUI 소스는 보존했다. force push/history rewrite를 하지 않았다.

---

## 3. Phase 2.6 감사 결과

기존 Phase 2.6:

- Rust `amare-backup` helper: create/verify/restore/recover, full/profile/mapped mode
- Vault portable export/import, DPAPI key 미포함
- journal + startup recover, `.backup.lock`
- C# `BackupClient`, Settings Backup & Restore dialog
- UI는 **단일 profile backup** create/verify와 single-profile restore만 노출
- Full restore는 helper `--mode full`만 지원
- 복원 helper는 app 종료를 먼저 기다린 뒤 staging/apply → UI에서 진행 상태 표시 불가
- backup 목록 UI 없음
- Vault와 container password가 같은 field

Phase 2.7에서 보완한 결손:

1. Full restore UI 부재
2. multi-profile backup에서 source profile 선택 불가
3. staging/apply progress 미표시
4. full restore confirmation/manifest summary 부재
5. backup 목록 부재
6. Vault/container password 분리 부재
7. full restore 후 target profile open 미지원

---

## 4. Full Profile Restore UI

Backup dialog에서 verify 후:

- manifest summary:
  - format version / AMARE version
  - scope(full/profiles)
  - 생성 시각
  - profile id/name/schema
  - 포함 data kinds
  - Vault 포함 여부/entry 수
  - encryption
  - file count
  - 전체 SHA-256
- `Source profile in backup` ComboBox
- `Target profile` ComboBox
- `Profile to open after full restore` ComboBox
- `Restore entire backup (all profiles)` button (full backup만 활성)
- `Restore to this profile`, `Restore as new profile`

Full restore:

```text
verify (no mutation)
  → full manifest 확인
  → 모든 profile/root registry 교체 경고 확인
  → helper --mode full
  → staging/integrity 검증
  → app 종료 대기
  → recovery로 기존 root/profiles 이동
  → backup root/profiles 적용
  → app files/Vault
  → restart + 선택 profile open
```

- 기존 root registry와 모든 profile이 교체된다.
- 기존 데이터는 recovery directory에 보존된다.
- 검증되지 않은 backup은 apply하지 않는다.
- full restore 후 `open-profile.json`으로 선택 profile을 연다.

---

## 5. Partial Restore 정책

### UI에 노출하는 partial restore

- **Profile 단위 partial restore**: full backup에서 source profile 하나를 선택해
  기존 target profile에 복원하거나 새 profile로 복원한다.
- 선택하지 않은 profile은 변경하지 않는다.
- Vault 포함/제외는 별도 선택이며, Vault 미포함 backup은 기존 Vault를 보존한다.

### UI에 노출하지 않는 partial restore

- Bookmarks만, History만, Settings만 등 **category 단위 복원**은 노출하지 않는다.
- 이유: 현재 엔진의 profile restore는 profile DB 전체를 atomic swap한다.
  category 단위 merge는 테이블 의존성/FK/세션 상태를 안전하게 보존한다고
  검증되지 않았다.
- category 단위 복원을 안전하게 구현하려면 별도 merge 설계와 실기기 검증이 필요하다.

---

## 6. Progress / Cancellation

helper restore를 prepare/apply로 분리했다.

```text
verify / decode / vault validation
  → backup app files
  → journal "preparing"
  → staging + DB integrity/schema check
  → journal "prepared", progress "staged"
  → app 종료 대기
  → journal "applying", progress "applying"
  → atomic apply
  → app files / Vault
  → journal "succeeded", progress "completed"
```

UI:

- helper progress file을 polling해 `staging`, `staged`, `applying` 등을 표시한다.
- `staged`에 도달하면 cancel을 비활성화하고 app을 종료한다.
- atomic apply 이후에는 취소하지 않는다.
- 적용 전 cancel 시 helper를 종료하고 `recover`로 staging/journal을 정리한다.
- 취소 요청과 완료를 status로 구분한다.
- app/window가 먼저 종료되어도 helper는 journal/recovery를 유지하고 apply를 계속한다.

진행률은 임의 percentage를 만들지 않고 실제 stage와 indeterminate 상태만 표시한다.

---

## 7. Backup 목록 / 보존 정책

- 기본 backup folder(`data/backups`)의 `.amarebak` 목록
- 파일명, size, 생성 시각, SHA-256(sidecar), 검증 상태
- 목록 선택 시 path field에 반영
- 선택 후 Verify로 실제 검증 상태 표시
- 자동 백업/자동 삭제 없음
- 기존 backup을 UI가 임의로 삭제하지 않음

---

## 8. 동시성 / 오류

- `.backup.lock` exclusive lock 유지
- restore 중복 요청은 두 번째 helper가 lock에서 거부
- restore는 app 전체를 종료한 뒤 apply하므로 대상 profile WebView2/SQLite와 경합하지 않는다.
- 다른 profile도 app 종료 시 함께 닫힌다. 실제 multi-window race는 UNVERIFIED.
- 오류 구분:
  - Invalid backup / Unsupported version / Incompatible schema
  - Invalid password / Corrupt Vault payload
  - Insufficient disk space
  - Target profile in use
  - Staging failed / Apply failed / Rollback failed
  - Recovery pending / Restart failed
- UI는 credential/Vault password/API key를 status/log에 남기지 않는다.
- recovery/staging/journal은 실패 시 자동 삭제하지 않는다.

---

## 9. 실기기 검증 절차

1. 새 profile 생성 후 bookmark/history/settings/site permission 입력
2. full backup 생성
3. backup SHA-256/sidecar 및 Verify 결과 확인
4. full backup에서 새 profile로 partial restore
5. Bookmarks/History/Settings/Site Permissions 확인
6. Vault 포함 backup 생성 (별도 Vault password)
7. 다른 Windows 사용자 또는 PC에서 Vault restore
8. 잘못된 Vault password 입력 후 기존 Vault 보존 확인
9. 기존 profile 덮어쓰기 full/partial restore
10. 복원 후 app restart 및 선택 profile open 확인
11. 복원 staging/apply 중 강제 종료 후 journal recovery 확인

실기기에서 수행하지 않은 항목은 PASS로 표시하지 않는다.

---

## 10. 테스트 결과

### Local Rust

- `cargo test -p amare-store -p amare-services`: **157 PASS / 0 FAIL**
- `cargo fmt --check`, `cargo clippy --all-targets`: **PASS**
- C# core-only managed: **105 PASS / 0 FAIL**
  (Windows helper tests는 Linux에서 UNVERIFIED detail)

### Windows / Linux CI

| Suite | 결과 |
|---|---|
| Windows interop | **127/127 PASS** |
| WinUI UI smoke | **86/86 PASS** |
| Windows 통합 검증 | **199 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** (215 total) |
| Backup CI | **10/10 PASS** |
| Portable updater E2E | **11/11 PASS** |
| Linux CI | **SUCCESS** |

Windows run: [45950978626](https://github.com/thfzm/AMARE/actions/runs/45950978626) (`3fff704`)
Linux run: [45946891881](https://github.com/thfzm/AMARE/actions/runs/45946891881) (`3fff704`)

신규/확장 UI 검증:

```text
PASS backup_create_verify_dialog
PASS backup_full_restore_dialog
```

Rust helper 확장 검증:

```text
PASS helper_full_backup_and_full_restore_restores_all_profiles
PASS helper_partial_profile_restore_preserves_other_profiles
PASS helper_create_verify_restore_profile (progress completed)
PASS helper_recover_rolls_back_interrupted_restore_journal
PASS Windows Vault separate-password export/restore
```

---

## 11. 최초 실패 및 수정

| # | 단계 | 실패 | 분류 | 수정 |
|---:|---|---|---|---|
| 1 | Windows UI run [45940269078](https://github.com/thfzm/AMARE/actions/runs/45940269078) | full restore UI test `StandardIn has not been redirected` | TEST_AUTOMATION | `RedirectStandardInput=true` |
| 2 | Windows run [45942758652](https://github.com/thfzm/AMARE/actions/runs/45942758652) | Windows-only Vault test가 `Vec<&str>`를 `Option<&str>`로 전달 | RUST COMPILE | `run_with_passwords` 사용 |
| 3 | Windows run 45942758652 | `BackupRestoreFullButton missing` | UI LAYOUT | full restore button을 restoreRow에 추가 |
| 4 | Windows run [45944702065](https://github.com/thfzm/AMARE/actions/runs/45944702065) | 중첩 ContentDialog로 full restore confirmation timeout | UI DIALOG | backup dialog hide → confirmation → progress dialog |
| 5 | Windows run 45944702065 | `websocket_browser_close` NewTabButton flake | PRE-EXISTING UI FLAKE | 동일 HEAD 재실행 |
| 6 | Windows run [45946888903](https://github.com/thfzm/AMARE/actions/runs/45946888903) | `tear_off_tab` flake | PRE-EXISTING UI FLAKE | 동일 HEAD 재실행 |
| 7 | Windows run [45948920625](https://github.com/thfzm/AMARE/actions/runs/45948920625) | `new_window_shortcut` flake | PRE-EXISTING UI FLAKE | 동일 HEAD 재실행 |
| 8 | 최종 [45950978626](https://github.com/thfzm/AMARE/actions/runs/45950978626) | 199/0/8/8, UI 86/86, interop 127/127, E2E 11/11 | — | clean final |

제품 데이터 손실/credential 노출 P0는 발견되지 않았다.

---

## 12. 남은 UNVERIFIED / BLOCKED

**UNVERIFIED**

- 실제 Windows 11 실기기 full/partial restore
- 실제 multi-window restore race
- 전원 손실/강제 종료 조건의 durability
- 다른 Windows 계정/PC에서 Vault restore
- category 단위 partial restore (구현하지 않음)
- full restore 후 실제 app restart/open profile의 실기기 확인
- C#/Rust string zeroization 한계

**BLOCKED**

- 없음.

---

## 13. P0/P1/P2

### P0

- 없음.

### P1

1. 실제 Windows 11 실기기 full/partial restore 및 cross-PC Vault.
2. 전원 손실 조건의 staging/journal/key durability.
3. multi-window race 실제 검증.
4. category 단위 partial restore 설계/검증.
5. backup format downgrade/forward compatibility 정책.

### P2

1. backup 목록 삭제/보존 정책 UI.
2. apply 단계의 사용자 progress(재시작 전 표시 한계).
3. backup 크기/압축/증분 정책.
4. restore 결과 진단 bundle.

---

## 14. 변경 파일 / commit

제품/테스트:

- `crates/amare-store/src/backup.rs`
- `crates/amare-services/src/bin/amare-backup.rs`
- `crates/amare-services/tests/backup_helper.rs`
- `apps/windows/AMARE.Windows.Core/BackupClient.cs`
- `apps/windows/AMARE.Windows.Core/BackupModels.cs`
- `apps/windows/AMARE.Windows.Core/Localization.cs`
- `apps/windows/AMARE.Windows/MainWindow.Backup.cs`
- `apps/windows/AMARE.Windows/MainWindow.xaml.cs`
- `tools/windows-ui-smoke/Program.cs`

문서:

- `docs/AMARE_PHASE2_BACKUP_RESTORE_UI_REPORT.md` (신규)
- `docs/PROFILE_BACKUP_RESTORE_DESIGN.md`
- `docs/AMARE_FEATURE_AUDIT.md`
- `docs/AMARE_FINALIZATION_PLAN.md`
- `docs/WINDOWS_TESTING.md`

```text
11fc123 Add full profile restore UI and staged restore progress
45bf86b Separate Vault password and fix full restore UI test
bec5d37 Fix Windows-only Vault test and expose full restore button
3fff704 Fix full restore dialog flow and nested dialog handling
<docs>  Document Phase 2.7 backup/restore UI completion
```

---

## 15. 결론

- Full Profile Restore가 Settings UI에서 verify/confirm/progress/restart 흐름으로 연결되었다.
- full backup의 profile 선택 partial restore와 full restore를 모두 지원한다.
- Vault password를 container password와 분리했다.
- restore staging/apply journal과 progress를 실제 단계에 연결했다.
- 실제 Windows 11 실기기/전원 손실/multi-window race는 UNVERIFIED로 남긴다.
- MSIX/서명/공개 Release는 수행하지 않았다.
