# AMARE Phase 1.2 — Password Vault Security Audit

- 상태: **감사/설계 PASS (구현 전)**
- Branch: `feature/phase-1-vault-design`
- 시작 HEAD: `63aa2b1`
- 제품/테스트 HEAD: `bcd476d` (변경 없음)
- 문서 HEAD: 이 문서 commit 후 갱신
- 작성일: 2026-11-23
- 범위: 조사·위협 모델·설계·테스트 계획. **제품 코드/DB/암호화 구현 변경 없음.**

---

## 1. 조사한 실제 파일과 함수

### Rust

| 파일 | 확인한 내용 |
|---|---|
| `crates/amare-security/src/lib.rs` | `derive_key` (Argon2id m=19MiB/t=2/p=1), `generate_salt`, `encrypt`/`decrypt` (XChaCha20-Poly1305, 24-byte nonce, nonce‖ct+tag), `VaultKey` zeroize, crypto tests |
| `crates/amare-security/src/keyfile.rs` | `KeyFile::load_or_create/load/store`, magic `AMAREKEY1`, DPAPI-protected key payload, temp+sync+rename, plaintext-key-absence test |
| `crates/amare-security/src/key_protection.rs` | `KeyProtector` trait, test-only `InMemoryKeyProtector` |
| `crates/amare-platform/src/windows_dpapi.rs` | `WindowsDpapiKeyProtector::protect/unprotect`, user-scope DPAPI, `CRYPTPROTECT_UI_FORBIDDEN`, context entropy, tamper/context tests |
| `crates/amare-store/src/repositories/secrets.rs` | `SecretRepository::set/get/delete/list_keys`, AEAD AAD=secret key, SQLite `secrets` |
| `crates/amare-store/src/migrations.rs` | `secrets(key, group_name, ciphertext, updated_at_ms)` schema, profile schema v5 |
| `crates/amare-store/src/backup.rs` | `BackupData.secrets` default true; `contains_secrets`; backup encryption Argon2id+XChaCha |
| `crates/amare-ffi/src/services.rs` | **secrets/vault method 없음**; services ABI version 1 |
| `crates/amare-ffi/src/lib.rs` | ABI/문서상 “extend only with contract/version update” |

### WinUI

| 파일 | 확인한 내용 |
|---|---|
| `apps/windows/AMARE.Windows/MainWindow.xaml.cs` | `CreateEnvironmentAsync` (profile WebView2), `EnsureAiAsync` (AI 별도 env), `WebMessageReceived` (nonce만 검증), `AppLog.Error` 사용 |
| `apps/windows/AMARE.Windows/AppPaths.cs` | `%LOCALAPPDATA%\AMARE` / `AMARE-MSIX` |
| `apps/windows/AMARE.Windows.Core/ProfilePathPolicy.cs` | profile id/path traversal 방지 |
| `apps/windows/AMARE.Windows.Core/ProfileDataCleanup.cs` | WebView2 폴더만 정리, Vault key 정리 없음 |
| `apps/windows/AMARE.Windows/AiCredentialStore.cs` | AI API 키만 Windows `PasswordVault` 사용 |
| `apps/windows/AMARE.Windows.Core/AppLog.cs` | `exception.Message` 기록, Vault redaction 필요 |
| `apps/windows/AMARE.Windows/AMARE.Windows.csproj` | WebView2 `1.0.4314.35` |
| `Microsoft.Web.WebView2.Core.xml` | `IsPasswordAutosaveEnabled` 기본 false, `IsGeneralAutofillEnabled` 기본 true, `ClearBrowsingDataAsync`의 `PasswordAutosave`/`GeneralAutofill`, `WebMessageReceived.Source`, frame APIs |

### GPUI (참고, 수정 없음)

| 파일 | 확인한 내용 |
|---|---|
| `apps/amare-gpui/src/password_vault.rs` | XChaCha20-Poly1305, DPAPI KeyFile, magic `AMAREVLT1`, format 2, origin normalizer, revision/tombstone/merge, global lock, atomic save, zeroize, tests |
| `apps/amare-gpui/src/main.rs` | `fill_password_entry`, `save_pending_login`, `poll_login_captures`, `sync_vault_lock`, password settings |
| `apps/amare-gpui/src/webview.rs` | `LOGIN_HOOK_SCRIPT` (top-frame/action check), `fill_login` first-password-field JS, `SetIsPasswordAutosaveEnabled(false)`, `SetIsGeneralAutofillEnabled(false)` |
| `apps/amare-gpui/src/library.rs` | vault는 전역 `%APPDATA%\AMARE`, profile별 아님; autofill/save prompt default true |
| CI 0.93 | password **2 PASS / 1 FAIL** (initial normal page timeout); vault multiwindow **1/1** |

---

## 2. 재사용 가능한 Rust 보안 모듈

### 그대로 재사용

- `amare_security::encrypt/decrypt`
  - 검증된 XChaCha20-Poly1305 AEAD
  - nonce 포함 출력, AAD 지원, 잘못된 키/AAD/tag 테스트 PASS
- `amare_security::VaultKey` / `zeroize`
- `amare_security::KeyFile`
  - DPAPI 등 OS protector와 조합
  - plaintext key를 디스크에 쓰지 않음
- `amare_platform::WindowsDpapiKeyProtector`
  - user-scope DPAPI, entropy context, 실패 시 generic error
- `amare_store::SecretRepository`
  - `secrets` 테이블 기반 ciphertext 저장
  - 기존 `get/set` 하위 호환 유지 가능
- `amare_store` backup의 Argon2id+XChaCha20-Poly1305 패키지 암호화

### 재사용 전 보완 필요

- `SecretRepository` AAD를 profile/version까지 결합하는 API
- CAS/충돌 처리 (`updated_at_ms`/generation)
- Vault 전용 `VaultService` (key/DPAPI/format/merge)
- FFI ABI 확장 (`vault.*`)
- error mapping (`VAULT_LOCKED/CORRUPT/AUTH/...`)
- profile delete 시 key file cleanup

### 기존 테스트 확인 (이번 감사에서 실행/인용)

| 항목 | 결과 | 비고 |
|---|---|---|
| `cargo test -p amare-security` | **11/11 PASS** | wrong key/AAD, tag tamper, KeyFile 등 |
| `amare-store` `encrypted_secret_round_trip_and_wrong_key` | PASS | Phase 1.1 storage suite 12/12에 포함 |
| Windows `platform_key_tests` | PASS | DPAPI/KeyFile, Phase 1.1 CI |
| GPUI password smoke 0.93 | **2 PASS / 1 FAIL** | normal page timeout; Vault 전체 미검증 |
| GPUI vault multiwindow 0.93 | **1 PASS / 1 FAIL** | unlock timeout |

GPUI 결과는 WinUI Vault 검증으로 합산하지 않는다.

### 재사용하지 않을 것

- GPUI `password_vault.rs`를 그대로 복사하는 것
- GPUI custom UI/입력/status 계층
- GPUI 전역 vault 경로/전역 lock 구현
- GPUI `fill_login` JS를 그대로 사용하는 것

---

## 3. GPUI Vault 장점

1. XChaCha20-Poly1305 + DPAPI user-scope key file 조합.
2. magic + format + JSON, 크기 상한 8MiB.
3. `normalize_origin`이 scheme/host/port, IDN punycode, default port,
   userinfo 거부를 처리.
4. `same_origin` exact match (subdomain/http/port 차이 거부).
5. revision + persistent tombstone + merge로 stale window의 resurrect/덮어쓰기 방지.
6. 같은 entry 동시 수정은 conflict로 거부 (last-writer-wins 아님).
7. manual/global lock epoch로 다중 창 잠금 전파.
8. temp file + `sync_all` + rename으로 원자적 저장.
9. plaintext/status 부재, missing key 보존, corrupt vault 보존 테스트.
10. 로그인 capture는 top-frame만, source/action origin을 검증.
11. 자동완성은 사용자가 entry를 클릭할 때만, 자동 submit 없음.
12. WebView2 내장 password/general autofill을 명시적으로 비활성화.

---

## 4. GPUI Vault 결함 / 미검증

| # | 결함/한계 | 영향 | WinUI 설계 반영 |
|---:|---|---|---|
| G1 | `read_vault`가 `format` 값을 검증하지 않고 `normalise()`가 2로 강제 | 미래/손상 format을 조용히 재작성 | format 정확히 일치만 허용, 불일치 시 원본 보존 |
| G2 | idle auto-lock 없음, manual/global lock만 | 사용자가 잠그지 않으면 평문 유지 | 기본 5분 idle + session lock |
| G3 | Vault 경로가 전역 `%APPDATA%\AMARE\password-vault` | 프로필 간 격리 없음 | 프로필별 Vault/key |
| G4 | backup은 `vault.bin`만 복사, key는 제외 | 다른 계정/기기에서 복구 불가 | Master Password export 또는 backup key 정책 |
| G5 | 파일 저장 lock이 프로세스 내부만 | 다중 앱 인스턴스 lost update 가능 | SQLite CAS/transaction |
| G6 | `fill_login`이 첫 password field를 무조건 채움 | hidden/검색/결제 field 오입력 가능 | visible/form/type/action 검증 |
| G7 | `fill_login`이 form action origin을 확인하지 않음 | cross-origin POST로 비밀번호 전송 가능 | action origin 검증/경고 |
| G8 | 클립보드 복사/자동 정리 없음 | 해당 없음(기능 부재) | explicit copy + 30s clear |
| G9 | Windows Hello 없음 | UX gate 없음 | UserConsentVerifier는 gate로만 검토 |
| G10 | password String/Clone 복사본 zeroize 한계 | 메모리 잔존 | byte buffer/최소 복사/폐기 정책 |
| G11 | password smoke 2/1, vault multiwindow 1/1 | 전체 Vault 시나리오 미검증 | WinUI 독립 테스트 필수 |
| G12 | version/nonce 재사용/중간 byte tamper 테스트 부족 | 회귀 위험 | 테스트 계획 T02/T03/T04/T05 |
| G13 | key file ACL을 명시적으로 조이지 않음 | 사용자 프로필 ACL 의존 | ACL/DPAPI 경계 문서화 |
| G14 | `PasswordEntry` Drop만 zeroize, pending/UI 복사본은 별도 | 평문 잔존 | lock 시 모든 참조 폐기 |

**GPUI를 WinUI Vault의 “검증된 구현”으로 사용할 수 없다.** 디자인 참고만 한다.

---

## 5. 최종 권장 암호화/키 구조

```text
profile DB (existing secrets table)
  key='password-vault', group='security'
  ciphertext = XChaCha20-Poly1305(key, JSON, AAD)
  AAD = amare.winui.password-vault.v1:<profile-id>

<appDataRoot>\vault\<profile-id>.key
  = DPAPI(user, context='amare.winui.password-vault.key.v1:<profile-id>')
    (KeyFile: magic + protected 32-byte key)
```

- **DB schema 변경 없음** (기존 `secrets` 사용).
- key와 ciphertext 분리.
- 24-byte random nonce 매 저장.
- format 1만 허용, `generation`/revision/tombstone.
- 기본 DPAPI-only, 복구는 Master Password export.
- Windows Hello는 UX gate로만; `KeyCredentialManager`는 v1 필수 아님.
- key file missing/corrupt 시 새 key 생성 금지, 원본 보존.

---

## 6. 자동완성 보안 설계 요약

- exact origin (scheme/host/port), HTTPS-only 기본.
- subdomain 자동 포함 금지.
- IDN punycode 정규화 + Unicode/punycode 표시.
- top-level document만; `window.top===window`; `args.Source` 검증.
- cross-origin iframe/action 차단.
- visible/enabled/form 소속 password field만.
- 사용자가 Fill을 클릭할 때만; **자동 주입/자동 submit 금지**.
- 로그인 submit capture는 top-frame + action origin 일치 + 사용자 저장 승인.
- WebView2 내장 `IsPasswordAutosaveEnabled=false`,
  `IsGeneralAutofillEnabled=false`; 기존 데이터는 동의 후 clear.
- clipboard explicit copy + 30초 clear (한계 명시).
- idle/session lock.

---

## 7. 위협 모델 요약

상세: `docs/VAULT_THREAT_MODEL.md`.

| 위협 | 판정 |
|---|---|
| 로컬 DB 탈취 | 부분 보호 |
| 다른 Windows 계정 | 보호 가능(조건부) |
| 같은 사용자 다른 프로세스 | 부분/보호 불가능 |
| 프로세스 메모리 평문 | 부분 보호 |
| Vault 변조 | 보호 가능(AEAD), rollback 부분 |
| 암호화 데이터 손상 | 탐지 가능, 가용성 부분 |
| 입력/자동완성 노출 | 부분 보호 |
| 악성 웹사이트 | 부분 보호 |
| 피싱/유사 도메인 | 부분 보호 |
| 비정상 종료 | 보호 가능(조건부) |
| DPAPI/계정 손실 | 보호 불가능/추가 설계 |
| 암호 분실/복구 | 추가 설계 |

**DPAPI는 Windows 사용자 계정/시스템 보안 경계에 의존한다.** 같은 사용자 악성코드,
관리자, 메모리 덤프, 로그인된 세션 공격을 막지 못한다.

---

## 8. 실패 테스트 계획

상세: `docs/VAULT_TEST_PLAN.md`.

필수 T01–T16:

- wrong key, ciphertext 1-byte, tag tamper, nonce reuse
- wrong format/version, wrong DB schema
- kill during save, concurrent save
- DPAPI failure, different Windows account
- lock 후 평문 차단
- wrong origin, iframe, lookalike
- delete 후 잔존, log/status 평문

추가 T17–T35: AAD/profile, missing key, export/import, built-in autofill clear,
HTTP/HTTPS, form action, hidden fields, clipboard, idle/session lock, multiwindow
lock/conflict, crash dump, redaction, ABI mismatch.

---

## 9. DB 변경 필요 여부

- **v1: DB schema 변경 없음.**
  - 기존 `secrets` 테이블 1행에 Vault blob.
  - profile DB가 이미 프로필 격리.
  - 암호문만 저장 → origin/username metadata 평문 없음.
- 코드 변경 필요:
  - `SecretRepository` AAD/CAS API
  - Rust `VaultService`
  - FFI `vault.*` (ABI 2 권장)
  - key file path/cleanup
  - WinUI Vault UI/autofill
- GPUI `vault.bin` 자동 import 없음. 필요 시 명시적 import.
- 미래 per-entry 검색/인덱스가 필요하면 schema v6 `vault_entries` 검토.

---

## 10. 구현 난이도 / 예상 작업량

| 단계 | 예상 |
|---|---:|
| Rust VaultService (format/crypto/key/CAS) | 5–8일 |
| FFI ABI 2 + C# client/error mapping | 3–5일 |
| WinUI Vault UI/lock/auto-lock | 5–7일 |
| WebView2 autofill/capture/origin/frame/builtin disable | 5–8일 |
| Backup/export/recovery/clipboard/log hardening | 4–6일 |
| Failure tests/security review/Win11 | 5–8일 |
| **합계** | **27–42 person-days** |

난이도: **높음**. 위험:

- Windows Hello/DPAPI 정책/계정 변경
- WebView2 frame/source/autofill API 동작
- Master Password/백업 복구 UX
- 실제 Windows 11 IME/DPI/자동완성
- 같은 사용자 악성코드/메모리 덤프 잔여 위험

---

## 11. Windows 11 실기기 검증 필요 항목

1. DPAPI user scope: 정상 로그인/로그아웃/재로그인 후 key 사용
2. Windows 계정 변경/프로필 재생성 시 오류/복구
3. Windows Hello `UserConsentVerifier` 지원/취소/미지원
4. Windows session lock 시 Vault 잠금
5. idle auto-lock 5분
6. 실제 사이트 로그인 폼 자동완성 (HTTPS)
7. cross-origin iframe/action 차단
8. IDN/punycode/유사 도메인 표시/차단
9. 클립보드 30초 정리 + Clipboard History
10. 다중 창 lock/동시 편집
11. 크래시/강제 종료 후 Vault 보존
12. WebView2 내장 autofill 데이터 clear
13. WinUI 49/49, Windows 143/0, interop 76/76 유지
14. 패키지/설치 없이 portable 실행

---

## 12. Git / 무결성

| 항목 | 결과 |
|---|---|
| Branch | `feature/phase-1-vault-design` |
| 시작 HEAD | `63aa2b1` |
| 제품/테스트 HEAD | **`bcd476d` 변경 없음** |
| Working tree | clean (문서 commit 전) |
| Stash | `stash@{0}` 보존 |
| GPUI | 수정/삭제 없음 |
| Phase 1.1 증거 | 보존 |
| 패키지 | 생성 없음 |
| MSIX/서명/Release | 없음 |
| 제품 코드/DB/암호화 구현 변경 | **없음** |

문서 commit hash는 최종 commit 후 이 표에 기록한다.

---

## 13. 감사 판정

**PASS (감사/설계 범위)**

- 기존 Rust 암호화/DPAPI/저장 구조를 실제 코드로 확인했다.
- GPUI Vault의 장점과 결함을 분리했다.
- WinUI의 현재 부재/WebView2 경계/내장 autofill 위험을 확인했다.
- DB schema 변경 없이 가능한 v1 설계를 제시했다.
- 12개 위협과 35개 테스트 항목을 정의했다.
- 제품 코드/DB/실제 비밀번호/패키지는 변경하지 않았다.

**구현은 별도 승인 후 시작한다.** 특히 Master Password/백업/자동 잠금/
WebView2 내장 autofill 정리 정책을 먼저 승인해야 한다.
