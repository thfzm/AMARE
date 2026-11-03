# AMARE 백업 및 복원

- 작성일: 2026-11-03
- 구현: `amare-store::backup`
- 관련 문서: [저장소 아키텍처](STORAGE_ARCHITECTURE.md), [DB 스키마](DATABASE_SCHEMA.md), [키 보호](KEY_PROTECTION.md), [DB 복구](DATABASE_RECOVERY.md)

---

## 1. 원칙

1. **복원 실패 시 기존 데이터를 잃지 않는다.** 기존 데이터를 먼저 삭제하지 않는다.
2. 백업은 검증을 통과한 뒤에만 정상 백업으로 표시한다.
3. 손상/체크섬 불일치/미래 스키마 백업은 복원 전에 거부한다.
4. 민감 데이터 포함 여부를 메타데이터에 명시한다.
5. 백업 파일에는 평문 암호화 키를 넣지 않는다.
6. 실제 사용자 PC 성능은 Runner 측정으로 대체하지 않는다.

---

## 2. 백업 대상

- 루트 DB (`amare.db`) — 프로필 레지스트리
- 프로필별 DB (`profiles/<id>/amare.db`)
  - settings
  - bookmarks + folders
  - history
  - sessions + tabs
  - secrets (암호문)
  - Library items/tags/collections/source references
  - AI conversations/messages/message parts/message-source links
  - Workspaces/tasks/task memories/share links
  - FTS5 인덱스 (DB 파일에 포함)
- 새 테이블은 프로필 DB 스냅샷에 자동 포함됩니다. `BackupData`의 카테고리 제외 옵션과 `data_kinds`도 함께 확장했습니다.

프로필 선택 및 데이터 종류 선택:

```rust
BackupOptions {
    include_root: true,
    profiles: None,                 // None = 전체
    data: BackupData {
        settings: true,
        bookmarks: true,
        history: false,             // 제외 가능
        sessions: true,
        secrets: false,             // 제외 가능
        library: true,
        conversations: true,
        workspaces: true,
    },
    encryption: BackupEncryption::None,
    compression: true,
}
```

- `contains_secrets`가 `true`이면 암호화된 Secret row가 포함됩니다. 암호화 키는 포함되지 않습니다.
- 제외된 데이터 종류는 백업/복원 후 존재하지 않습니다.
- `library=false`: Library item/tag/collection 연결을 제외합니다.
- `conversations=false`: conversation/message/part/source link를 제외합니다.
- `SourceReference`는 Library 또는 Conversation 중 하나라도 포함되면 유지하고, 둘 다 제외하면 함께 삭제합니다(AI 인용 보존과 프라이버시 선택의 균형).
- `workspaces=false`: workspace/task/memory/share link를 제외합니다. 연결된 Conversation은 유지되고 link만 NULL이 됩니다.

---

## 3. SQLite 일관성

- SQLite **Online Backup API**(`rusqlite::backup::Backup`)를 사용합니다.
- `.db` 파일을 단순 복사하지 않습니다.
- WAL 모드에서도 스냅샷이 일관되도록 SQLite가 페이지 단위로 복사합니다.
- 루트 DB와 각 프로필 DB는 **순차적으로** 스냅샷합니다. 하나의 트랜잭션으로 묶을 수 없으므로, 백업 메타데이터에 다음 한계를 기록합니다.

```text
consistency: "SQLite Backup API per database; root and profile snapshots are sequential, not a cross-database transaction."
```

즉, 백업 도중 서로 다른 DB에 쓰기가 발생하면 시점 차이가 있을 수 있습니다. 데이터 손상은 아니지만 "완전한 동일 시점"은 보장하지 않습니다.

- 백업 중에도 원본 DB를 장시간 잠그지 않습니다(페이지 단위 백업).
- 백업 실패 시 `.tmp-*`와 미완성 최종 파일을 삭제하고, 기존 백업은 건드리지 않습니다.

---

## 4. 백업 파일 포맷

### 4.1 컨테이너

```text
MAGIC(8) "AMAREBAK"
version(u16 LE) = 1
flags(u8)        bit0=encrypted, bit1=compressed
salt_len(u8)
payload_len(u64 LE)
salt[salt_len]
payload[payload_len]
```

- `payload`는 선택적으로 gzip 압축된 뒤 암호화됩니다.
- 암호화는 XChaCha20-Poly1305입니다. nonce는 `amare-security::encrypt` 출력에 포함됩니다.
- 비밀번호 모드: Argon2id로 salt에서 키 유도.
- 키 모드: 호출자가 제공한 32바이트 키(예: DPAPI로 보호된 KeyFile) 사용.

### 4.2 Payload

```text
manifest_len(u32 LE)
manifest_json(UTF-8)
file_bytes...
```

Manifest의 `files[].offset`은 `file_bytes` 시작 기준입니다.

### 4.3 Manifest

```json
{
  "format_version": 1,
  "app_version": "0.1.0",
  "created_at_ms": 1790000000000,
  "scope": "full",
  "data_kinds": ["root_profile_registry", "settings", "bookmarks", "history", "sessions", "secrets", "library", "conversations", "workspaces"],
  "profiles": [{"id": "p1", "name": "Personal", "schema_version": 3}],
  "root_schema_version": 2,
  "compression": "gzip",
  "encryption": "none",
  "contains_secrets": true,
  "consistency": "SQLite Backup API per database; ...",
  "files": [
    {"path": "amare.db", "kind": "root_db", "offset": 0, "size": 49152, "sha256": "..."},
    {"path": "profiles/p1/amare.db", "kind": "profile_db", "offset": 49152, "size": 98304, "sha256": "..."}
  ]
}
```

- 파일별 SHA-256 + 컨테이너 전체 SHA-256 sidecar(`.sha256`).
- SHA-256은 손상 감지용 무결성 값이며 전자 서명/진위 증명이 아닙니다.
- `format_version`이 지원 범위를 벗어나면 복원 거부.
- 경로는 `amare.db` 또는 `profiles/<safe-id>/amare.db`만 허용합니다.
- 경로에 `..`, 절대 경로, NUL, 중복 파일명이 있으면 거부합니다.

### 4.4 파서 자원 제한

악성/손상 백업이 메모리를 고갈시키지 않도록 다음 제한을 적용합니다.

| 제한 | 값 |
|---|---:|
| 컨테이너 파일 | 2 GiB |
| 컨테이너 payload | 2 GiB |
| gzip 해제 결과 | 2 GiB |
| manifest JSON | 4 MiB |
| 파일 개수 | 512 |
| 파일 1개 | 1 GiB |
| 파일 전체 합 | 2 GiB |

길이/offset/size는 checked add로 검사하고, gzip은 제한+1까지만 읽어 초과 시 거부합니다. 현재 payload는 메모리에서 처리하므로 한도를 넘는 대용량 백업은 지원하지 않습니다.

---

## 5. 백업 관리

`BackupManager`:

- `create_backup(store, options)` — 전체/프로필/데이터 선택 백업
- `list()` — `.amarebak` + `.meta.json` 목록
- `verify(path, credential)` — 컨테이너/체크섬/manifest 검증
- `restore(path, target_root, options)` — 안전 복원
- `delete(path)` — 백업 + sidecar 삭제
- `prune(keep)` — 최신 N개 유지
- `auto_backup_if_due(store, options, policy)` — 주기/보관 개수

### 기본 정책

```rust
BackupPolicy {
    interval_ms: 24 * 60 * 60 * 1000, // 24h
    keep: 7,
}
```

- 자동 백업 실패 시 `state.json`의 마지막 성공 시각을 갱신하지 않습니다.
- 디스크 부족/쓰기 실패 시 미완성 파일을 남기지 않습니다.
- 중복 백업은 아직 content-hash 기반으로 생략하지 않습니다(향후).

---

## 6. 복원 순서

1. 컨테이너 magic/version/길이 확인
2. sidecar SHA-256 + 파일별 SHA-256 검증
3. 암호화 자격 증명 확인 (없으면 `CredentialRequired`, 틀리면 `WrongCredential`)
4. gzip 해제 + manifest 파싱
5. 지원 백업 버전/경로/프로필 검사
6. 임시 staging 디렉터리에 추출
7. `PRAGMA integrity_check` + `foreign_key_check`
8. 스키마 버전 확인:
   - 현재 지원 버전보다 **미래**면 거부
   - 이전 버전이면 staging에서만 마이그레이션
9. 복원 대상/프로필 충돌 확인
10. 기존 데이터를 `*.restore-recovery-*`로 이동
11. staging 데이터를 원자적 rename으로 교체
12. 복원 결과 재검증
13. 실패 시 recovery를 되돌리고 오류 반환

### Full 복원

- `scope=full` + 루트 DB가 있어야 합니다.
- 기존 `amare.db`와 `profiles/`를 recovery로 이동한 뒤 교체합니다.
- `backups/`, 키 파일 등 다른 데이터는 건드리지 않습니다.

### 프로필 복원

- 대상 루트에 프로필이 이미 존재해야 합니다.
- 선택한 프로필 디렉터리만 recovery로 이동/교체합니다.
- 다른 프로필과 루트 DB는 유지됩니다.

### 실패 시

- 검증/추출/마이그레이션 단계 실패: target을 전혀 수정하지 않습니다.
- 교체 후 재검증 실패: recovery에서 기존 데이터를 복원합니다.
- recovery 디렉터리는 성공 후에도 남겨 수동 복구 지점으로 사용할 수 있습니다.

### Windows 파일 잠금

- DB 연결이 열려 있으면 rename이 실패할 수 있습니다. 복원 전 모든 `Store`/`ProfileStore` 연결을 닫아야 합니다.
- rename 실패 시 target을 변경하지 않고 오류를 반환합니다.

---

## 7. 암호화/키

- `BackupEncryption::None` — 백업 파일 자체는 암호화되지 않음. Secret row는 기존 Secret 암호문으로 남음.
- `BackupEncryption::Password(password)` — Argon2id + XChaCha20-Poly1305.
- `BackupEncryption::Key(key)` — 호출자가 제공한 32바이트 키. Windows에서는 `KeyFile` + DPAPI 사용.
- **암호화 키는 백업 파일/manifest에 포함되지 않습니다.**
- 키를 잃으면:
  - SecretRepository 데이터: 복호화 불가
  - 키로 암호화한 백업: 복원 불가
  - DPAPI로 보호한 KeyFile: 동일 Windows 사용자/기기에서만 복호화 가능
- 휴대/다른 계정 이전에는 **비밀번호 기반 백업**을 사용해야 합니다.

자세한 내용: [키 보호](KEY_PROTECTION.md)

---

## 8. 테스트

`crates/amare-store/tests/backup.rs`:

- 루트/전체/단일 프로필 백업
- WAL 모드 + 백업 중 쓰기
- 체크섬/손상 백업 거부
- 정상/부분/전체 복원
- 복원 실패 시 target 보존
- 데이터 제외 (history/secrets/Library/AI/Workspace)
- Phase 1.6 데이터 전체 백업/복원 및 manifest `data_kinds`
- 비밀번호/키 암호화, 잘못된 자격 증명
- KeyFile DPAPI/InMemory 보호
- integrity/foreign key
- 목록/삭제/prune/auto policy
- 미래 버전 거부
- 비정상적으로 큰 container/manifest 길이 거부

`backup-ci` 통합 JSON과 `backup-benchmark`는 CI Artifact에 포함됩니다.

---

## 9. 알려진 한계

- 루트/프로필 DB의 **완전한 동일 시점** 백업은 보장하지 않습니다(순차 스냅샷).
- 백업 payload는 현재 메모리에서 압축/암호화합니다. 대용량 Library/AI 자료가 추가되면 스트리밍 포맷으로 확장해야 합니다.
- Windows 설치 프로그램/롤백은 별도 Phase 2 작업입니다.
- `backups/` 디렉터리 자체는 백업에 포함하지 않습니다.
- 백업 파일의 자동 무결성 재검사는 아직 스케줄링되지 않았습니다.

---

## 10. 현재 검증 상태 (2026-11-03, Linux)

- `crates/amare-store/tests/backup.rs`: **13/13 PASS** (+ backup parser unit 3)
- `backup-ci`: **10/10 PASS**
- `backup-benchmark` (Linux arm64 release):
  - 5 profiles / 1,000 bookmarks / 10,000 history / 100 tabs per profile
  - full backup ~500ms, full restore ~170ms, single profile backup ~100ms
  - full backup ~1.5MB, single profile ~0.3MB, checksum verify ~55ms
  - memory: `not_measured`
- Windows Runner/DPAPI: workflow에 포함했지만 **실제 실행 미확인**
- 실제 Windows 11 파일 잠금/복원: **실기기 검증 필요**
- Phase 1.6 profile schema v3 + root schema v2 백업/복원은 Linux 로컬 SQLite에서 검증했습니다.
- Phase 1.7 서비스 생성 데이터(설정/Library/프로필 아이콘) full backup/restore를 검증했습니다.
