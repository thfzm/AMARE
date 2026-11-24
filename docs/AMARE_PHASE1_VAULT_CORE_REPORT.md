# AMARE Phase 1.3A — Rust Password Vault Core Report

- 상태: **Rust Vault Core 구현/검증 완료**
- Branch: `feature/phase-1-vault-core`
- 시작 HEAD: `c4d8d17` (`feature/phase-1-vault-design`)
- 제품/테스트 HEAD: **`22e3e51`**
- 최종 문서 HEAD: `6b783b1` (`Document Phase 1.3A Rust vault core results`)
- 작성일: 2026-11-24
- 범위: Rust Vault Core + 테스트. FFI ABI 2, WinUI UI, WebView2 자동완성, 패키지는 범위 밖.

---

## 1. Git / 무결성

| 항목 | 결과 |
|---|---|
| 시작 branch | `feature/phase-1-vault-design` |
| 작업 branch | `feature/phase-1-vault-core` |
| 시작 HEAD | `c4d8d17` |
| 제품/테스트 HEAD | **`22e3e51`** |
| Working tree | clean (문서 commit 전) |
| Stash | `stash@{0}` 보존 |
| Phase 1.1 세션 구현/테스트 | 보존 |
| Phase 1.2 Vault 설계 문서 | 보존 |
| GPUI 소스/증거 | 수정 없음 |
| DB schema | **v5 유지, 변경 없음** |
| 패키지/MSIX/Release | 생성 없음 |
| 기존 ABI 1 | 깨뜨리지 않음 |

---

## 2. 구현 파일

| 파일 | 내용 |
|---|---|
| `crates/amare-services/src/vault.rs` | VaultService, VaultData/Entry/Tombstone, AES/키/CAS/backup |
| `crates/amare-services/src/lib.rs` | `pub mod vault` |
| `crates/amare-services/Cargo.toml` | `amare-security`, `serde`, `url`, `zeroize`, `fs2` |
| `crates/amare-services/tests/vault.rs` | Vault Core 23개 테스트 (Linux 22 + Windows DPAPI 1) |
| `crates/amare-store/src/repositories/secrets.rs` | AAD/version/CAS API |
| `crates/amare-store/src/error.rs` | `StoreError::Conflict` |
| `crates/amare-store/tests/storage.rs` | AAD/CAS/legacy secret 호환 테스트 |
| `Cargo.lock` | 신규 dependency lock |

DB migration/table 추가는 없습니다.

---

## 3. 구현 API

`VaultService`:

```rust
new(root, profile, protector) -> Result<Self>
initialize() -> Result<VaultStatus>
unlock() -> Result<VaultStatus>
lock()
status() -> Result<VaultStatus>
list_entries() -> Result<Vec<VaultEntryView>>
get_entry(id) -> Result<VaultEntry>
get_for_fill(origin, id) -> Result<VaultEntry>
create_entry(origin, username, password, title) -> Result<VaultEntry>
update_entry(id, expected_revision, origin, username, password, title) -> Result<VaultEntry>
delete_entry(id, expected_revision) -> Result<u64>
export_backup(password) -> Result<Vec<u8>>
validate_backup(payload, password) -> Result<VaultBackupSummary>
import_backup(payload, password) -> Result<VaultStatus>
key_path() -> &Path
```

동작:

- `initialize`: 기존 Vault가 있으면 key/ciphertext 검증 후 unlock, 없으면 key 생성 + empty format 1 저장.
- `unlock`: 항상 디스크에서 reload. stale state를 그대로 쓰지 않는다.
- `lock`: entries/key/version 제거. key는 `VaultKey` Drop으로 zeroize.
- `list_entries`: password 제외 metadata만.
- `get_entry`: unlock 상태에서만 password 포함 1건.
- `get_for_fill`: 요청 origin과 저장 origin을 exact match. 불일치 시 `OriginMismatch`.
- `create/update/delete`: revision 검사 + generation 증가 + CAS 저장.
- `export/validate/import`: Master Password 기반 암호화 backup.

---

## 4. 암호화 / 키 보호

- AEAD: **XChaCha20-Poly1305** (`amare_security::encrypt/decrypt`)
- nonce: 24-byte CSPRNG, 저장마다 새로 생성 (`nonce || ciphertext+tag`)
- AAD: `amare.winui.password-vault.v1:<profile-id>`
- Vault key: 32-byte random, `KeyFile` + `KeyProtector`
- Windows: `WindowsDpapiKeyProtector` (user scope, context entropy)
- key file: `<root>/vault/<profile>.key` (DB와 분리)
- key file lock: `<key>.lock` + `fs2` exclusive lock
- key file missing/corrupt: 새 key 자동 생성 금지, `KeyMissing`/`KeyUnavailable`
- key race: key 생성/읽기/저장 시 lock + key file 재확인. 불일치 시 저장 거부.
- plaintext buffer: `zeroize` 적용 (key, JSON, backup plaintext)
- DB에는 ciphertext만 저장. origin/username/password 평문 없음.

Backup:

```text
AMAREVB1 | version(4) | salt(16) | nonce+ciphertext+tag
KDF: Argon2id (amare_security::derive_key, 기존 파라미터)
AAD: amare.winui.password-vault.backup.v1
```

- export는 현재 unlock 상태 필요.
- import/validate 실패 시 현재 Vault를 변경하지 않음.
- 다른 profile로 backup import 가능 (backup AAD는 profile 비종속).
- DPAPI key file은 backup에 포함하지 않음. 계정/PC 이동은 Master Password export로만 가능.

---

## 5. CAS / 동시성

- DB: `secrets.updated_at_ms`를 monotonic version으로 사용.
- `SecretRepository::set_with_aad_cas`:
  - `BEGIN IMMEDIATE`
  - expected version 비교
  - `next = max(now_ms, current + 1)`
  - 불일치 시 `StoreError::Conflict`
- VaultService는 generation/revision을 별도로 단조 증가.
- stale writer는 DB transaction에서 거부되며 state도 변경되지 않음.
- 다중 VaultService 인스턴스/스레드 테스트:
  - simultaneous first initialize: key lock으로 하나의 key file
  - concurrent create: 한 writer 성공, 다른 writer conflict
  - reload 후 재시도 성공

**주의:** CAS는 같은 profile DB의 다중 connection을 전제로 한다. 여러 앱 인스턴스가
같은 key file을 동시에 **회전**하는 기능은 이번 단계에 없다.

---

## 6. 테스트 결과 (실제 실행)

### 6.1 Linux local

| Suite | 결과 |
|---|---|
| `amare-security` | **11/11 PASS** |
| `amare-store` lib | 9/9 PASS |
| `amare-store` backup | 13/13 PASS |
| `amare-store` library/AI | 30/30 PASS |
| `amare-store` recovery | 4/4 PASS |
| `amare-store` session_windows | 10/10 PASS |
| `amare-store` storage (AAD/CAS 포함) | **13/13 PASS** |
| **`amare-store` 합계** | **79/79 PASS** |
| `amare-services` 기존 | **38/38 PASS** |
| **`amare-services` Vault** | **23/23 PASS** (Linux; Windows 24/24) |
| `amare-ffi` | **7/7 PASS** |
| 전체 workspace | FAIL 0 |

### 6.2 Windows CI

- 최초 실행: [45329528761](https://github.com/thfzm/AMARE/actions/runs/45329528761)
  - Windows **142 PASS / 0 FAIL / 9 UNVERIFIED / 8 SKIPPED**
  - UI smoke **49개 중 2 FAIL** → `winui_ui_smoke UNVERIFIED`
  - Rust `windows_dpapi_vault_roundtrip` **PASS**, service vault **23/23**
- 동일 HEAD(407f94a) 재실행: [45331281561](https://github.com/thfzm/AMARE/actions/runs/45331281561)
  - Windows **143 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED**
  - UI smoke **49/49 PASS**
  - service vault **23/23 PASS**
- 최종 실행: [45333470001](https://github.com/thfzm/AMARE/actions/runs/45333470001)
  - Windows **143 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED**
  - UI smoke **49/49 PASS**
  - Interop **76/76 PASS**
  - Rust `windows_dpapi_vault_roundtrip` **PASS**
  - service vault **24/24 PASS**

Windows 첫 실행의 실패:

```text
FAIL session_multiwindow_restore: target element ... no longer available
FAIL websocket_browser_close: NewTabButton missing
```

분류: **TEST_AUTOMATION_BUG / ENVIRONMENT_LIMITATION**  
제품 Vault Core와 무관한 기존 WinUI multiwindow UIA harness flake. 동일 HEAD(407f94a)
재실행에서 49/49 PASS. 최초 실패와 재시도 결과를 모두 보존한다.

`SKIPPED` 8개는 package 생성 금지에 따른 7개 단계 + Netflix/Disney 수동 테스트 1개.
**패키지/MSIX는 생성·설치하지 않았다.**

---

## 7. 최초 실패와 분류

| # | 최초 실패 | 분류 | 조치 |
|---:|---|---|---|
| 1 | Vault CAS 테스트에서 `second.unlock()`이 state를 reload하지 않아 재시도 create가 Conflict | **PRODUCT_DEFECT (초기 구현)** | `unlock`이 항상 디스크에서 reload하도록 수정 |
| 2 | Clippy `derivable_impls` | `TEST_AUTOMATION_BUG`/lint | `#[derive(Default)]` |
| 3 | Clippy bool assert/useless format | `TEST_AUTOMATION_BUG` | assertion/코드 정리 |
| 4 | Windows UI smoke multiwindow stale UIA + websocket NewTabButton missing | **TEST_AUTOMATION_BUG / ENVIRONMENT_LIMITATION** | 동일 HEAD 재실행 49/49 PASS |

최초 Vault Core 테스트 실행에서 15 PASS / 1 FAIL (CAS reload)이었고,
수정 후 23/23 (Windows 24/24)이다.

---

## 8. 보안 검토

| 항목 | 결과 |
|---|---|
| nonce 재사용 | 24-byte CSPRNG, 저장마다 새 nonce; 테스트 T04 PASS |
| 키 파생 파라미터 | 기존 Argon2id m=19MiB/t=2/p=1 사용; export에 적용 |
| AAD profile binding | wrong AAD/profile 테스트 PASS |
| format downgrade | format != 1 거부, 자동 변환 없음 |
| generation rollback | generation 단조 증가, CAS; 외부 anti-rollback은 미구현 |
| CAS 우회 | `set_with_aad_cas`만 Vault 저장에 사용, stale writer 거부 |
| 키 파일 race | `fs2` exclusive lock + key 재확인; simultaneous initialize 테스트 |
| DB atomicity | `BEGIN IMMEDIATE` transaction, 실패 시 rollback |
| plaintext logging | VaultService logging 없음; error Display redaction 테스트 |
| error leakage | `VaultError`는 password/key/ciphertext를 포함하지 않음 |
| panic 경로 | Mutex poison → `Corrupt`; secret을 panic message에 넣지 않음 |
| zeroization | key/backup/JSON buffer zeroize; `VaultEntry` password Drop zeroize |

### 미해결/한계

1. **Windows 계정/DPAPI 손실 복구**: Master Password export는 구현했지만 복구 UI/정책은
   다음 단계.
2. **generation rollback 완전 방지**: AEAD/CAS는 변조/동시성만 방어. 이전 정상 blob으로
   되돌리는 공격은 외부 monotonic anchor 없이는 완전 방어 불가.
3. **메모리**: Rust는 zeroize하지만 C#/WebView2/JS DOM 복사본은 다음 단계에서 최소화.
4. **키 회전**: 일반 entry 삭제/전체 삭제 시 key rotation은 아직 없음.
5. **다중 앱 인스턴스 key 회전**: key lock으로 생성/읽기/저장은 보호하지만,
   key rotation 프로토콜은 미구현.
6. **Windows Hello**: 미구현.
7. **실제 Windows 11 사용자 계정 변경**: UNVERIFIED.
8. **FFI ABI 2 / WinUI UI / WebView2 autofill**: 범위 밖, 미구현.

---

## 9. FFI 경계 문서 (ABI 2 준비, 구현 전)

이번 단계에는 FFI ABI 2를 구현하지 않았고 기존 ABI 1도 그대로다.

권장 ABI 2 계약:

- 입력: `version=2`, `method`, `params` JSON. 기존 ABI 1 method는 유지.
- 문자열: UTF-8, NUL 종료 C string, Rust→C#은 `amare_string_free`.
- 오류: `AMARE_ERR_VAULT_LOCKED`, `VAULT_CORRUPT`, `VAULT_AUTH`,
  `VAULT_CONFLICT`, `VAULT_UNSUPPORTED`, `VAULT_KEY_UNAVAILABLE`.
- `vault.get_for_fill`만 password를 포함한 응답을 허용하고, C#은 fill 직후 폐기.
- `vault.status/list`는 password를 포함하지 않는다.
- lock/unlock 상태는 서비스 인스턴스/프로필별로 관리.
- plaintext buffer: Rust가 소유한 Vec을 응답 직후 free; C# 문자열 복사 최소화.
- ABI 1 호출자 깨뜨리지 않기: method 추가 + version 2 병행.
- 실제 구현은 Rust Core 안정화 후 다음 단계.

---

## 10. DB / Migration

- schema **v5 유지**.
- 새 table 없음. 기존 `secrets` 1행 (`key='password-vault'`, `group='security'`).
- AAD/CAS 코드만 추가.
- 기존 `secrets` 사용처(AI 등)와 `set/get` 하위 호환 테스트 PASS.
- 실제 사용자 DB 직접 migration 없음.
- GPUI `vault.bin` 자동 import 없음.

---

## 11. 완료 조건 체크

- [x] VaultService 핵심 기능
- [x] XChaCha20-Poly1305 검증
- [x] DPAPI key lifecycle (Windows CI `windows_dpapi_vault_roundtrip` PASS)
- [x] profile AAD 격리
- [x] format/version 검증
- [x] CAS 및 다중 인스턴스 충돌 방지
- [x] 저장 실패 rollback (CAS/transaction; process-kill은 UNVERIFIED)
- [x] lock 상태 평문 접근 차단
- [x] export/import 검증
- [x] 기존 schema v5 호환성
- [x] 기존 Rust 테스트 회귀 없음
- [x] 민감정보 로그 노출 없음 (VaultService 무로그)
- [x] 구현/테스트 증거 보존

미완료/UNVERIFIED:

- process kill 도중 저장 (T07)
- 다른 Windows 계정에서 DPAPI 실패/복구 (T10)
- WebView2 iframe/autofill (T13/T14, T22–T30)
- crash dump redaction (T33)
- FFI ABI 2/UI

따라서 **Rust Vault Core 범위는 PASS**, **전체 Vault 기능 완료는 아님**.

---

## 12. 다음 단계

1. FFI ABI 2 (`vault.*`) + C# client/error mapping
2. WinUI Vault UI (unlock/list/add/edit/delete/lock/auto-lock)
3. WebView2 autofill/capture + exact origin/top-level/no auto-submit
4. Master Password export/import UI + backup policy
5. process-kill/Windows account/DPAPI failure real-device tests
6. 전체 Vault 보안 검증 후에만 P0-2 완료 판정

패키지는 별도 승인 후에만 생성한다.
