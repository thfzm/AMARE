# AMARE 데이터베이스 복구 정책

- 작성일: 2026-11-03
- 구현: `amare-store::maintenance`, `amare-store::backup`
- 관련 문서: [DB 스키마](DATABASE_SCHEMA.md), [백업/복원](BACKUP_RESTORE.md)

---

## 1. 무결성 검사

`amare-store`는 다음을 검사합니다.

- `PRAGMA integrity_check`
- `PRAGMA foreign_key_check`
- 파일 존재 여부
- `schema_migrations` 최대 버전
- 지원 스키마 버전 범위
- WAL/SHM 파일은 SQLite가 자동 처리

API:

```rust
let report = amare_store::check_database(path);
let reports = amare_store::check_store(root);
```

`IntegrityReport`:

```text
path, exists, ok, schema_version, supported_schema_version,
integrity_messages, foreign_key_violations, error
```

**손상된 DB를 자동으로 삭제하거나 초기화하지 않습니다.** 원본을 그대로 보존하고 오류를 반환합니다.

---

## 2. 손상 감지 시 절차

1. `check_database`/`check_store`로 상태 확인
2. 손상 파일을 그대로 복사/보존
3. `backups/`의 정상 백업 확인
4. `BackupManager::verify`로 백업 무결성 확인
5. staging 복원/검증 후 교체
6. 복원 실패 시 recovery 디렉터리에서 기존 데이터 복구

자동 복구가 불가능하면:
- 원본 파일을 유지
- 사용자에게 백업 복원 안내
- 임의 삭제/DB 재생성 금지

---

## 3. 마이그레이션 정책

- 기본은 **순방향 마이그레이션**입니다.
- v1 → v2 업그레이드는 지원합니다.
- 지원하지 않는 **미래 버전** DB는 열거나 변경하지 않고 거부합니다.
- 자동 다운그레이드는 금지합니다.
- 다운그레이드가 필요하면 이전 버전 백업에서 복원합니다.
- 마이그레이션 전에 백업/복구 지점을 확보하는 것을 권장합니다.

`Store::open`/`ProfileStore::open_at`은 현재 지원 버전까지만 마이그레이션합니다.
백업 복원 시 staging에서 `schema_version > supported`이면 target을 건드리지 않고 거부합니다.

---

## 4. WAL 관련

- `PRAGMA journal_mode=WAL`을 사용합니다.
- 백업은 SQLite Online Backup API를 사용하므로 WAL 내용을 포함한 일관 스냅샷을 만듭니다.
- 백업 복사 시 `.db`만 복사하지 않습니다.
- 프로필 DB 백업 시 WAL/SHM을 직접 복사하지 않고 Backup API를 사용합니다.
- 복원 후 target DB를 다시 열어 무결성 검사를 수행합니다.

---

## 5. Foreign Key 위반

- `PRAGMA foreign_keys=ON`으로 연결을 구성합니다.
- `foreign_key_check` 결과가 1개 이상이면 `IntegrityReport.ok=false`.
- 백업/복원 staging에서 FK 위반이 있으면 복원 거부.
- 기존 DB의 FK 위반을 자동 수정하지 않습니다.

---

## 6. 스키마 버전

| DB | 지원 버전 |
|---|---:|
| 루트 | 1 |
| 프로필 | 2 |

- `schema_migrations` + `PRAGMA user_version`.
- 미래 버전은 거부.
- 이전 버전은 staging에서 마이그레이션 후 재검증.

---

## 7. 복원 실패 시 데이터 보호

- 기존 데이터를 먼저 삭제하지 않습니다.
- staging에서 검증/마이그레이션을 먼저 수행합니다.
- 교체 전 기존 데이터를 `*.restore-recovery-*`로 이동합니다.
- 교체 후 재검증 실패 시 recovery를 되돌립니다.
- 복원 함수는 성공/실패를 명확히 반환하고, recovery 경로를 `RestoreReport`에 포함합니다.

---

## 8. 자동 복구 금지 원칙

다음은 자동으로 수행하지 않습니다.

- 손상 DB 삭제
- DB 파일 재생성
- 스키마 다운그레이드
- 사용자 동의 없는 백업 덮어쓰기
- 키 분실 시 Secret 데이터 초기화

대신 명확한 오류와 복구 안내를 제공합니다.

---

## 9. 테스트 상태

- Linux: 정상 DB integrity, FK 위반, 손상 DB 감지, 원본 보존, backup/restore staging 검증 PASS
- Windows Runner: workflow에 포함했지만 실제 실행 미확인
- 실제 Windows 파일 잠금/계정 환경: 실기기 검증 필요
