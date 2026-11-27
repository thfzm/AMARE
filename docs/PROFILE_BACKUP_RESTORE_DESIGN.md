# AMARE Profile Backup & Restore Design — Phase 2.6

- 상태: 구현/CI 검증, 실제 Windows 11 실기기 UNVERIFIED
- 대상: Windows WinUI 3 profile backup/restore
- 백업 형식: `AMAREBAK` container, format version 2 (v1 read 지원)
- DB schema 변경: 없음 (profile schema v6 유지)
- SHA-256: 파일 무결성만 의미하며 배포자 인증/기밀성이 아니다.

---

## 1. 구성 요소

| 구성 요소 | 역할 |
|---|---|
| `amare-store::backup` | container/manifest/file hash, DB snapshot, staging/restore |
| `amare-backup` Rust helper | create/verify/restore/recover CLI, Vault export/import |
| `BackupClient` (C#) | helper 실행, stdin password, result JSON |
| `MainWindow.Backup` | Settings backup/restore UI, validation, confirm |
| `BackupManager` | `%LOCALAPPDATA%\AMARE\data` 아래 profile DB 백업 |
| Vault `export_backup` | Argon2id + XChaCha20-Poly1305 portable Vault export |

기존 `BackupManager`/`BackupOptions`/`BackupManifest`/`RestoreOptions`를 재사용하고
필요한 필드만 additive로 확장했다.

---

## 2. 백업 범위

### 기본 백업

- Profile DB: bookmarks, history, settings, site permissions, sessions/window state,
  downloads metadata rows, Library, conversations, workspaces/task memories
- Application files:
  - `downloads.json`
  - `app-preferences.json`
  - `update-preferences.json`
  - `ai-api-settings.json` (key 없음)

### 사용자 선택

- Password Vault: 명시적으로 선택하고 별도 Vault password를 입력한 경우에만 포함
- Vault 데이터는 DB의 DPAPI ciphertext가 아니라 **Vault export**로 포함

### 백업 제외

- WebView2 cache/cookies/localStorage/IndexedDB
- 로그/진단 파일
- updater staging/backup
- 실행 파일/패키지 바이너리
- Windows Credential Manager AI API key
- DPAPI Vault key file (`vault/<profile>.key`)

---

## 3. 파일 형식

```text
MAGIC(8) | container_version(u16) | flags(u8) | salt_len(u8) |
payload_len(u64) | salt | payload

payload = manifest_len(u32) | manifest_json | file_bytes...
```

- MAGIC: `AMAREBAK`
- manifest `format_version`: **2**
- v1 backup은 계속 읽는다.
- `BackupManifest`:
  - app version, created_at_ms, scope, data_kinds
  - profiles(id/name/schema), root schema
  - compression/encryption
  - `contains_secrets`, `contains_vault`, `vault_profiles`
  - files(path/kind/offset/size/sha256)
- 파일 종류: `root_db`, `profile_db`, `vault_export`, `app_file`

### 제한

- container 2 GiB, uncompressed 2 GiB
- manifest 4 MiB
- file 1 GiB, total 2 GiB
- file count 512
- path는 `amare.db`, `profiles/<id>/amare.db`, `vault/<id>.amarevault`,
  `app/<known>.json`만 허용
- traversal/absolute path/duplicate/symlink/예약 이름 거부

### 원자성

1. temp directory에서 DB snapshot/SQLite backup
2. temp container 작성 + `sync_all`
3. SHA-256 sidecar 작성
4. 최종 파일로 `rename`
5. 실패 시 temp/final/sidecar 정리
6. 다음 create 시작 시 `.tmp-*` 정리

---

## 4. 암호화 / Vault

### 일반 백업

- password를 입력하면 container 전체를 `Argon2id + XChaCha20-Poly1305`로 암호화
- password가 없으면 unencrypted backup이며 UI가 명시적으로 경고
- SHA-256 sidecar는 항상 요구 (helper verify/restore)

### Vault

- Vault는 `VaultService::export_backup(password)`의 Argon2id + XChaCha20-Poly1305
  포맷을 그대로 사용
- DPAPI key file은 절대 백업하지 않는다
- Vault를 포함하면 DB snapshot에서 `secrets` table을 제거하고,
  `vault/<profile>.amarevault`만 포함
- 복원 시:
  1. Vault password로 export를 먼저 검증
  2. DB restore
  3. 대상 profile의 VaultService를 initialize (없으면 새 DPAPI key 생성)
  4. `import_backup` 후 `lock()`
- 실패 시 DB recovery와 이전 key file을 복원
- Vault를 포함하지 않은 백업은 기존 `secrets` table과 key file을 보존

---

## 5. 복원 흐름

```text
verify (no mutation)
  → user confirms target profile
  → helper starts with password on stdin
  → app closes
  → helper waits parent exit
  → write restore journal
  → decode + validate (again)
  → staging DB + integrity/schema check
  → move old DB/profile dirs to recovery
  → move staged DB into target
  → restore app files
  → import Vault (if included) / preserve existing Vault (if not)
  → journal succeeded
  → result JSON written
  → app relaunched
```

- 복원 전 현재 DB를 recovery directory에 보존한다.
- 실패 시 `revert_restore`로 이전 DB를 되돌리고 recovery를 삭제하지 않는다.
- journal이 `applying`인 상태로 프로세스가 종료되면 다음 app 시작 시 helper
  `recover`가 DB와 app files를 rollback한다.
- 복원 후 profile을 다시 초기화하고 Vault는 locked 상태로 시작한다.

### 복원 모드

- `profile`: 기존 target profile에 source profile DB를 매핑
- `mapped`: source → target mapping (새 프로필 복원)
- `full`: root DB + 모든 profile 교체. Phase 2.7부터 Settings UI에서 지원한다.
- full backup에서 source profile 하나만 선택해 기존/새 profile로 복원하는
  profile 단위 partial restore를 지원한다.
- category 단위 partial restore(bookmarks만, settings만 등)는 엔진이 profile DB
  전체를 교체하므로 UI에 노출하지 않는다.

---

## 5.1 Prepare / Apply progress

- restore helper는 verify/decode/Vault validation 후 staging을 만들고
  integrity/schema를 검증한다.
- 이 단계는 target을 변경하지 않으며 `backups/restore-progress.json`에
  `staging`, `staged`를 기록한다.
- UI는 progress file을 polling해 실제 stage를 표시한다.
- `staged` 이후 app 종료를 기다린 뒤 `applying`으로 전환하고 atomic apply한다.
- atomic apply 이후에는 취소하지 않는다.
- journal status:
  - `preparing`: staging 중. recover는 staging만 정리
  - `prepared`: apply 전. recover는 staging만 정리
  - `applying`: target 변경 중. recover는 recovery로 rollback
  - `succeeded`: 결과 표시 후 정리

## 5.2 Password separation

- Backup encryption password: container 전체 암호화(선택)
- Vault password: Vault export/import 전용(필수, Vault 포함 시)
- 두 password는 별도 UI field와 별도 stdin line/flag로 전달한다.
- 하위 호환을 위해 `--password-stdin`만 제공된 경우 Vault password로도 사용한다.

## 5.3 Full restore / open profile

- full restore는 root DB와 모든 profile DB를 교체한다.
- 기존 root/profiles는 recovery directory에 보존한다.
- helper는 성공 후 `backups/open-profile.json`을 기록하고 app을 재시작한다.
- app은 시작 시 이 파일을 읽어 target profile로 switch하고 파일을 삭제한다.

## 5.4 Backup 목록

- 기본 folder의 `.amarebak` 목록을 filename/size/time/sidecar hash/status와 함께 표시한다.
- 목록 선택은 path field만 채우며 자동 복원하지 않는다.
- 자동 삭제/자동 백업은 하지 않는다.

## 6. 동시성 / multi-window

- `data/backups/.backup.lock` exclusive lock으로 create/restore 동시 실행을 막는다.
- create는 SQLite Backup API로 DB별 일관 snapshot을 만든다.
  root/profile 간 cross-database transaction은 아니다.
- restore는 app 전체를 종료하고 helper가 실행되므로 target profile의 WebView2/
  SQLite connection과 경합하지 않는다.
- 다른 profile도 app 종료 시 함께 닫힌다. restore는 앱 단위 작업이다.

---

## 7. 실패 처리

| 상황 | 처리 |
|---|---|
| backup write 실패 | temp/final/sidecar 정리, 기존 backup 유지 |
| backup 도중 종료 | `.tmp-*`만 남고 최종 파일 없음 |
| 손상 archive | verify/restore 전에 거부 |
| manifest 조작 | path/offset/size/hash 검증 |
| checksum mismatch | sidecar/파일 hash 불일치 거부 |
| 잘못된 Vault password | verify 단계에서 거부, DB 변경 없음 |
| Vault ciphertext 손상 | verify/restore 전에 거부 |
| DB integrity failure | staging에서 거부, target 변경 없음 |
| restore 도중 종료 | journal + recovery로 다음 시작 시 rollback |
| rollback 실패 | recovery/staging/journal 보존 |
| 동시 요청 | helper lock 거부 |

---

## 8. 남은 한계

- 실제 Windows 11 실기기 검증 UNVERIFIED
- 실제 SSD 전원 손실 조건 UNVERIFIED
- full restore UI는 노출하지 않음
- Vault import 도중 프로세스 종료와 key file race는 journal로 DB는 복구하지만
  key file crash window는 별도 실기기 검증 필요
- C#/Rust `string` zeroization은 best-effort
- app-wide preferences/downloads는 profile backup에 포함되므로 복원 시 app 전체에 영향
