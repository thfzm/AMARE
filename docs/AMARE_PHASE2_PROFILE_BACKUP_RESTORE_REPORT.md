# AMARE Phase 2.6 — Profile Backup & Restore 보고서

- 상태: **PASS (Rust/C#/Windows/Linux CI)**. 실제 Windows 11 실기기/전원 손실은 UNVERIFIED.
- 작업 branch: `feature/phase-2-profile-backup-restore`
- 기준 branch: `feature/phase-2-portable-updater`
- 기준 제품/테스트 HEAD: **`fcf1ded`**
- 기준 문서 HEAD: **`89fc451`**
- 최종 제품/테스트 HEAD: **`6ff6e4b`**
- 최종 문서 HEAD: 이 보고서를 포함한 문서 commit
- DB schema 변경: **없음** (profile schema v6 유지)
- MSIX/서명/공개 Release: **없음**

---

## 1. 최종 요약

| 완료 기준 | 판정 | 근거 |
|---|---|---|
| Settings에서 profile backup 생성 | **PASS (CI/UI)** | `backup_create_verify_dialog` |
| backup 무결성/호환성 검증 | **PASS (CI/Rust)** | sidecar SHA-256, manifest, path, schema, Vault 검증 |
| 새 profile 및 기존 profile 복원 | **PASS (Rust/helper)** | `RestoreMode::ProfilesMapped`, UI target selector |
| Vault 포함/제외 정책 | **PASS (구현/Rust)** | DPAPI key 미포함, portable export, 기존 Vault 보존 |
| Vault 다른 Windows 환경 복구 | **PASS (format), 실기기 UNVERIFIED** | Argon2id+XChaCha20 export, 새 DPAPI key 생성 후 import |
| 복원 실패 시 profile/Vault 보존 | **PASS (Rust)** | staging/recovery/journal, rollback 테스트 |
| multi-window/동시성 | **PASS (lock)**; 실제 Window race UNVERIFIED | `.backup.lock`, app 종료 후 helper |
| Unit/Integration/Failure Injection | **PASS (Rust 155 / Windows backup-ci 10/10)** | `amare-store`/`amare-services` tests |
| P0 데이터 손실/credential 노출 | **없음** | 아래 정책/테스트 |
| 문서 갱신 | **PASS** | report/design/audit/plan/Vault docs |

---

## 2. 기준점 / branch

```text
feature/phase-2-portable-updater
  fcf1ded  Select matching checksum entry from checksums.txt
  89fc451  Complete Phase 2.5 report commit map

feature/phase-2-profile-backup-restore (신규)
  8b13d29  Add profile backup/restore helper and Settings UI
  9a1926b  Fix backup UI profile list and window interop imports
  6ff6e4b  Remove unused Windows-only test import
  <docs>   Document Phase 2.6 profile backup and restore
```

기존 branch, GPUI 소스, Phase 2.5 패키지/증거는 보존했다.
force push/history rewrite를 하지 않았다.

---

## 3. 기존 구현 감사

확인된 기존 자산:

- `crates/amare-store/src/backup.rs`
  - `AMAREBAK` container, format v1
  - SQLite Backup API 기반 root/profile DB snapshot
  - gzip, password/key encryption, sidecar SHA-256
  - manifest file index, path traversal/duplicate/size limit
  - staging, integrity/schema 검증, recovery directory, rollback
  - `BackupOptions`, `RestoreMode::Full/Profiles`, `BackupPolicy`
- Vault `VaultService::{export_backup, validate_backup, import_backup}`
  - Argon2id + XChaCha20-Poly1305, DPAPI key file은 별도
- C# `AmareVaultClient` Vault backup export/import
- WinUI Settings/Vault UI, `ProfileClient`, profile switching

확인된 결손:

| # | 결손 | 영향 |
|---:|---|---|
| 1 | Store backup이 FFI/C#/UI에 연결되지 않음 | 사용자가 backup/restore 불가 |
| 2 | `BackupData.secrets` 기본 true, key file 제외 | 다른 PC에서 Vault 복호화 불가 |
| 3 | Vault export가 profile backup에 포함되지 않음 | portable Vault 복원 불가 |
| 4 | app-wide `downloads.json`/preferences 미포함 | 다운로드/앱 설정 유실 |
| 5 | backup create destination 선택/진행/취소 UI 없음 | Settings 기능 미완 |
| 6 | restore target 선택/새 profile 복원 없음 | 제한적 복원 |
| 7 | restore 도중 프로세스 종료 복구 journal 없음 | DB 유실 위험 |
| 8 | 동시 backup/restore lock 없음 | 경합 |
| 9 | backup format v1에 Vault/app file kind 없음 | 확장 필요 |

기존 `BackupManager`의 DB snapshot/rollback/path/size 검증은 재작성하지 않고 확장했다.

---

## 4. 백업 범위

### 기본 백업

- Bookmarks, History, Downloads metadata, Site Permissions, Profile settings
- Session/window state, Library, AI conversations, Workspaces/task memories
- custom search engine 등 profile DB 데이터
- app-wide:
  - `downloads.json`
  - `app-preferences.json`
  - `update-preferences.json`
  - `ai-api-settings.json` (API key 없음)

### 사용자 선택

- Password Vault (명시적 선택 + Vault password)
- Vault는 DPAPI ciphertext가 아니라 `export_backup` 암호화 payload로 포함

### 제외

- WebView2 cache/cookies/localStorage/IndexedDB
- 로그/진단, updater staging/backup
- 실행 파일/바이너리
- Windows Credential Manager AI API key
- Vault DPAPI key file

---

## 5. 포맷 / 암호화 / Vault 이식성

- container `AMAREBAK`, manifest **format v2** (v1 read 지원)
- `contains_vault`, `vault_profiles`, `BackupFileKind::VaultExport/AppFile` 추가
- general backup password → container Argon2id + XChaCha20-Poly1305
- Vault password → Vault export Argon2id + XChaCha20-Poly1305
- DPAPI key file은 backup에 포함하지 않는다.
- 복원 시 target profile에 새 DPAPI key를 생성하고 Vault export를 import한다.
- Vault를 포함하지 않은 backup은 기존 `secrets` table과 key file을 보존한다.
- Vault import 후 `lock()` 상태로 유지한다.
- SHA-256 sidecar는 helper verify/restore에서 필수다.

---

## 6. 흐름

### Create

```text
Settings → Backup & restore
  → path / target profile / include Vault / password
  → helper create (stdin password)
  → DB snapshot + app files + optional Vault export
  → temp → sync → rename
  → SHA-256 sidecar
  → result JSON (size/sha256/contains_vault)
```

### Verify

```text
helper verify
  → sidecar SHA-256
  → container magic/version/limits
  → manifest/schema/path/offset/file hash
  → Vault export password validation
  → Valid / ValidWithWarnings / UnsupportedVersion / IncompatibleSchema /
    InvalidPassword / UnsafeArchive / Corrupt
```

### Restore

```text
verify (no mutation)
  → explicit target profile / restore-as-new
  → user confirmation
  → helper starts, password on stdin
  → app closes
  → helper waits parent exit
  → journal
  → staging + integrity/schema
  → old DB → recovery
  → staged DB → target
  → app files → restore
  → Vault import / existing Vault preserve
  → journal succeeded, result JSON
  → app relaunch
```

복원 도중 종료된 경우 다음 app 시작 시 helper `recover`가 journal/recovery를 사용해
DB와 app files를 rollback한다.

---

## 7. Failure Injection 결과

| 항목 | 결과 |
|---|---|
| backup write 실패 | **PASS** (temp/final 없음) |
| backup 도중 종료 | temp만 남고 final 미노출 (journal/atomic) |
| 손상 archive | **PASS** (`Corrupt`) |
| sidecar 누락 | **PASS** (`Corrupt`) |
| checksum mismatch | **PASS** (`Corrupt`) |
| 잘못된 password | **PASS** (`InvalidPassword`) |
| Vault ciphertext 손상 | **PASS** (verify 거부) |
| staging/DB integrity 실패 | **PASS** (target 미변경) |
| restore 도중 종료 | **PASS** (journal recover test) |
| rollback 실패 | recovery/journal 보존 |
| 동시 backup/restore | **PASS** (exclusive lock) |
| profile window 종료 중 restore | app 전체 종료 후 helper (실제 race UNVERIFIED) |

---

## 8. 테스트 결과

### Local Rust

- `cargo test -p amare-store -p amare-services`: **155 tests PASS / 0 FAIL**
- `cargo fmt --check`, `cargo clippy -p amare-store -p amare-services --all-targets`: PASS
- C# core-only managed tests: **105 PASS / 0 FAIL**
  (Windows Credential/backup helper tests return UNVERIFIED detail on Linux)

### Windows / Linux CI

| Suite | 결과 |
|---|---|
| Windows interop | **127/127 PASS** |
| WinUI UI smoke | **85/85 PASS** |
| Windows 통합 검증 | **199 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** (215 total) |
| Portable updater E2E 회귀 | **11/11 PASS** |
| Backup CI cases | **10/10 PASS** |
| Linux CI | **SUCCESS** |

Windows run: [45880094114](https://github.com/thfzm/AMARE/actions/runs/45880094114) (`6ff6e4b`)
Linux run: [45880097942](https://github.com/thfzm/AMARE/actions/runs/45880097942) (`6ff6e4b`)

신규 Windows 검증:

```text
PASS backup_create_verify_dialog
PASS backup_helper_create_verify_windows
PASS backup_helper_password_rejection_windows
PASS windows 백업 helper Vault export/restore test
```


### 주요 테스트

- `crates/amare-store/tests/backup.rs`
  - full/profile backup/restore
  - data exclusion
  - password/wrong credential
  - path/offset/duplicate/size
  - future schema rejection
  - vault export + mapped restore
  - write failure no final file
- `crates/amare-services/tests/backup_helper.rs`
  - helper create/verify/restore
  - wrong password/sidecar/corrupt archive
  - invalid backup does not touch target
  - app files backup/restore
  - interrupted restore journal recovery
  - Windows-only Vault export/restore test
- C# `Phase35Tests`
  - BackupClient helper create/verify
  - encrypted wrong password rejection
- UI smoke
  - `backup_create_verify_dialog`

---

## 9. 최초 실패 및 수정

| # | 단계 | 실패 | 분류 | 수정 |
|---:|---|---|---|---|
| 1 | local Rust | backup helper app files read from wrong parent | TEST_AUTOMATION | explicit `--app-root`로 변경 |
| 2 | Windows build [45875811658](https://github.com/thfzm/AMARE/actions/runs/45875811658) | `ProfilePageDto.Items` 없음 | C# COMPILE | `Profiles` 사용 |
| 3 | Windows build 45875811658 | partial file에 `WinRT.Interop` using 없음 | C# COMPILE | `using WinRT.Interop;` 추가 |
| 4 | Windows run [45876862631](https://github.com/thfzm/AMARE/actions/runs/45876862631) | Windows-only test의 unused import로 clippy 실패 | RUST LINT | import 제거 |
| 5 | local Rust | restore journal test가 기존 profile registry와 충돌 | TEST_FIXTURE | `ProfileStore::open_at`로 partial DB 모사 |
| 6 | Windows run 45876862631 | UI 85/85, interop 127/0, E2E 11/11였으나 rust_clippy만 실패 | LINT | 수정 후 [45880094114](https://github.com/thfzm/AMARE/actions/runs/45880094114) clean |

제품 데이터 손실/credential 노출 P0는 발견되지 않았다.

---

## 10. 남은 UNVERIFIED / BLOCKED

**UNVERIFIED**

- 실제 Windows 11 실기기 backup/restore
- 실제 SSD 전원 손실/강제 종료와 동일한 조건
- 실제 multi-window restore race
- Vault import 중 key file crash window
- 다른 Windows 계정/PC에서 실제 Vault 복원
- full restore UI (helper API만 지원)
- C#/Rust string zeroization 한계

**BLOCKED**

- 없음.

---

## 11. P0/P1/P2

### P0

- 없음.

### P1

1. 실제 Windows 11 실기기 backup/restore 및 Vault cross-PC 복원.
2. 전원 손실 조건의 restore journal/key file durability.
3. multi-window restore race 실제 검증.
4. full restore UI/정책.
5. backup format v2 forward-compatibility 문서화/old app downgrade 정책.

### P2

1. backup 목록/보존 정책 UI.
2. restore 진행률/취소 UI (현재 create/verify 취소).
3. backup 크기/압축/증분 정책.
4. restore 결과 진단 bundle.

---

## 12. 변경 파일 / commit

제품/테스트:

- `crates/amare-store/src/backup.rs`
- `crates/amare-store/tests/backup.rs`
- `crates/amare-services/Cargo.toml`
- `crates/amare-services/src/vault.rs`
- `crates/amare-services/src/bin/amare-backup.rs` (신규)
- `crates/amare-services/tests/backup_helper.rs` (신규)
- `apps/windows/AMARE.Windows.Core/BackupClient.cs` (신규)
- `apps/windows/AMARE.Windows.Core/BackupModels.cs` (신규)
- `apps/windows/AMARE.Windows/MainWindow.Backup.cs` (신규)
- `apps/windows/AMARE.Windows/MainWindow.Settings.cs`
- `apps/windows/AMARE.Windows/MainWindow.xaml.cs`
- `apps/windows/AMARE.Windows.Core/Localization.cs`
- `apps/windows/AMARE.Interop.Tests/Phase35Tests.cs` (신규)
- `apps/windows/AMARE.Interop.Tests/Program.cs`

검증/CI:

- `tools/verify-windows.ps1`
- `tools/verify-portable-updater.ps1`
- `tools/windows-ui-smoke/Program.cs`

문서:

- `docs/AMARE_PHASE2_PROFILE_BACKUP_RESTORE_REPORT.md` (신규)
- `docs/PROFILE_BACKUP_RESTORE_DESIGN.md` (신규)
- `docs/AMARE_FEATURE_AUDIT.md`
- `docs/AMARE_FINALIZATION_PLAN.md`
- `docs/VAULT_SECURITY_DESIGN.md`
- `docs/VAULT_TEST_PLAN.md`

```text
8b13d29 Add profile backup/restore helper and Settings UI
9a1926b Fix backup UI profile list and window interop imports
6ff6e4b Remove unused Windows-only test import
<docs>  Document Phase 2.6 profile backup and restore
```

---

## 13. 결론

- 기존 Rust backup/restore와 Vault export/import를 재사용해 Settings backup/restore를
  완성했다.
- profile DB, app-wide downloads/preferences, optional portable Vault를 포함한다.
- restore는 검증/저널/recovery를 사용하며 실패 시 기존 DB와 Vault를 보존한다.
- 실제 Windows 11 실기기/전원 손실/다중 창 race는 UNVERIFIED로 남긴다.
- MSIX/서명/공개 Release는 수행하지 않았다.
