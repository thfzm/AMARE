# AMARE 키 보호

- 작성일: 2026-11-03
- 구현: `amare-security` (trait/KeyFile), `amare-platform` (Windows DPAPI, Android stub)
- 관련 문서: [백업/복원](BACKUP_RESTORE.md), [보안 설계](SECURITY_DESIGN.md)

---

## 1. 현재 SecretRepository 키 전달 방식

기존 `SecretRepository::set/get`은 **호출자가 32바이트 키를 전달**합니다.

```rust
secrets.set(key, group, plaintext, &encryption_key)?;
secrets.get(key, &encryption_key)?;
```

- 키는 DB에 저장되지 않습니다.
- 키 생성/보관은 호출자 책임입니다.
- 이 API는 그대로 유지합니다. 기존 데이터와 호환됩니다.

이번 단계에서 추가한 것:

- `KeyProtector` trait
- `KeyFile` — 보호된 키 blob을 파일로 저장/로드
- Windows `WindowsDpapiKeyProtector`
- Android Keystore stub
- 백업 암호화에 `BackupEncryption::Key`로 연결

---

## 2. Windows DPAPI

### 범위

- **사용자 계정 범위 DPAPI**를 사용합니다. `CRYPTPROTECT_LOCAL_MACHINE`을 사용하지 않습니다.
- 같은 Windows 사용자 + 같은 기기에서만 복호화됩니다.
- 다른 Windows 계정/다른 기기로 KeyFile을 복사하면 복호화에 실패합니다.
- Windows Runner의 계정 범위 테스트는 동일 계정 내 round-trip/변조 거부만 검증합니다. 실제 다른 계정 간 실패는 실기기/별도 계정 환경에서 검증해야 합니다.

### 구현

`amare-platform::WindowsDpapiKeyProtector`:

- `CryptProtectData` / `CryptUnprotectData`
- 사용자 scope (LOCAL_MACHINE 없음)
- context 문자열을 DPAPI description으로 사용
- 출력 blob은 `LocalFree`로 해제
- 실패 시 Windows error code를 노출하지 않고 `KeyProtectionError`로 변환
- 키/평문을 로그에 출력하지 않음

### KeyFile

```text
AMAREKEY1 (9 bytes magic)
protected_blob (DPAPI output)
```

- 32바이트 키를 생성해 DPAPI로 보호한 뒤 파일에 기록합니다.
- 평문 키는 파일에 기록하지 않습니다.
- Unix 테스트에서는 파일 mode 0600을 사용합니다.
- 쓰기는 temp + rename으로 수행합니다.

```rust
let protector = WindowsDpapiKeyProtector;
let key_file = KeyFile::new(path);
let key = key_file.load_or_create(&protector, "amare.backup.key")?;
```

---

## 3. Android Keystore

현재는 **인터페이스/스텁만** 구현했습니다.

- `AndroidKeystoreKeyProtector`는 `KeyProtectionError::Unsupported`를 반환합니다.
- 실제 JNI/Keystore 연동은 Android 빌드/실기기 단계에서 구현/검증해야 합니다.
- Linux 테스트에서 Android Keystore를 PASS로 처리하지 않습니다.

---

## 4. 키 손실/이동 정책

| 대상 | 키 손실 시 | 다른 기기/계정 이동 |
|---|---|---|
| SecretRepository | 복호화 불가 | 기존 키를 안전하게 이전해야 함 |
| DPAPI KeyFile | 같은 계정/기기 복구 불가 | DPAPI 실패, 휴대 불가 |
| 비밀번호 백업 | 비밀번호 분실 시 복원 불가 | 비밀번호로 이동 가능 |
| backup key(Key) | 백업 복원 불가 | 키를 안전하게 이전해야 함 |

- **키를 잃으면 복구할 수 없는 데이터**를 사용자에게 명확히 고지해야 합니다.
- 자동 백업을 다른 기기로 옮길 때는 비밀번호 기반 백업을 사용합니다.
- DPAPI KeyFile은 "이 기기/이 사용자" 범위의 자동 백업에만 사용합니다.

---

## 5. 키 회전

- XChaCha20-Poly1305 키 회전을 위한 필드는 아직 없습니다.
- Secret row에 key version을 추가하는 후속 마이그레이션이 필요합니다.
- 회전 시 기존 키로 복호화 → 새 키로 재암호화 → 원자적 갱신 절차를 설계해야 합니다.
- 현재는 키를 교체하면 기존 Secret 복호화가 실패합니다.

---

## 6. DB 전체 암호화와의 구분

- 현재 암호화는 **SecretRepository row 단위**입니다.
- SQLite DB 전체 암호화(SQLCipher 등)는 **별도 보안 결정**이며 아직 적용하지 않았습니다.
- DB 전체 암호화를 적용하면:
  - 모든 프로필 DB가 암호화됨
  - 키 관리/백업/복구 정책이 더 강하게 결합됨
  - WAL/백업/복원 절차 재검증 필요
- Secret 암호화가 있다고 DB 전체 암호화가 된 것으로 간주하지 않습니다.

---

## 7. 테스트 상태

| 항목 | 환경 | 상태 |
|---|---|---|
| InMemoryKeyProtector/KeyFile round-trip | Linux | PASS |
| KeyFile 평문 키 비노출 | Linux | PASS |
| 잘못된 보호자/context 거부 | Linux | PASS |
| Android Keystore stub Unsupported | Linux | PASS |
| Windows DPAPI round-trip | Windows Runner | **미실행** (workflow에 포함, 실제 실행 미확인) |
| Windows DPAPI 변조 거부 | Windows Runner | **미실행** |
| 다른 Windows 계정 접근 실패 | 실기기 | **미검증** |

Windows DPAPI 테스트를 Linux 결과로 PASS 처리하지 않습니다.

Linux 개발 환경에서는 `cargo check -p amare-platform --target x86_64-pc-windows-msvc`로
**컴파일 가능성만** 확인했습니다. Windows API 실제 호출/계정 범위 테스트는 Windows Runner에서만 유효합니다.
