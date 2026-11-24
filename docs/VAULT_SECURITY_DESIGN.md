# AMARE Vault Security Design — Phase 1.2

- 상태: **WinUI Vault UI + 잔여 lock 경계 검증 완료 / WebView2 autofill 미구현, 실기기 항목 UNVERIFIED**
- 작성일: 2026-11-23
- 기준: WinUI 3 + WebView2 + C#, Rust `amare-security`/`amare-platform`/`amare-store`
- 관련 문서: `VAULT_THREAT_MODEL.md`, `VAULT_TEST_PLAN.md`, `AMARE_PHASE1_VAULT_AUDIT.md`

---

## 1. 현재 확인된 사실

### 1.1 재사용 가능한 Rust 보안

`crates/amare-security/src/lib.rs`:

- `derive_key(password, salt)` — Argon2id, `m=19*1024 KiB`, `t=2`, `p=1`, 32-byte key
- `generate_salt()` — 16-byte OS RNG
- `encrypt(key, plaintext, aad)` — XChaCha20-Poly1305, 24-byte random nonce,
  출력 `nonce(24) || ciphertext+tag`
- `decrypt(key, blob, aad)` — nonce 분리 후 AEAD 인증/복호화
- `VaultKey` — `Zeroize + ZeroizeOnDrop`
- 테스트: round-trip, wrong key, wrong AAD, tag 1-byte tamper, signature,
  update downgrade, SHA-256

`crates/amare-security/src/keyfile.rs`:

- `KeyFile::load_or_create(protector, context)`
- magic `AMAREKEY1`, 32-byte key, 보호된 payload만 디스크 저장
- temp file + `sync_all` + rename
- 테스트: plaintext key 부재, wrong protector/context 실패

`crates/amare-platform/src/windows_dpapi.rs`:

- `WindowsDpapiKeyProtector` — user scope `CryptProtectData`/`CryptUnprotectData`
- `CRYPTPROTECT_UI_FORBIDDEN`
- context를 DPAPI entropy로 사용
- 테스트: round-trip, context mismatch, tamper 거부, KeyFile round-trip
- CI `platform_key_tests` PASS (Windows)

`crates/amare-store/src/repositories/secrets.rs`:

- `secrets(key PRIMARY KEY, group_name, ciphertext, updated_at_ms)`
- `set/get/delete/list_keys`
- `encrypt(key=credential key, plaintext, aad=secret key name)`
- storage test `encrypted_secret_round_trip_and_wrong_key` PASS
- **FFI에는 secrets/vault method가 없다.**

### 1.2 현재 WinUI

- 비밀번호 관리자/Vault UI 없음.
- `AiCredentialStore`만 Windows `PasswordVault`로 AI API 키를 저장한다.
- WebView2는 프로필별 user data folder를 사용한다.
- WebView2 settings에서 `IsPasswordAutosaveEnabled`/`IsGeneralAutofillEnabled`를
  설정하지 않는다.
  - 문서상 password autosave 기본값은 **false**
  - general autofill 기본값은 **true**
  - 기존에 저장된 password 데이터는 비활성화 후에도 autofill될 수 있음
- `WebMessageReceived`는 새 탭 검색에서 nonce만 확인하고 `args.Source` origin
  검증은 하지 않는다. Vault 메시지에는 반드시 source 검증이 필요하다.
- `AppLog.Error(message, exception)`는 `exception.Message`를 기록한다.
  Vault 예외는 평문을 포함하지 않도록 별도 매핑이 필요하다.
- `ProfileDataCleanup`은 WebView2 폴더만 정리한다. Vault 키 파일 정리 정책이 없다.
- `ProfilePathPolicy`는 profile id를 영숫자/`-`/`_`/`.`로 제한한다.

### 1.3 Rust Vault Core 구현 상태 (Phase 1.3A)

- `crates/amare-services/src/vault.rs`:
  - `VaultService::{initialize,unlock,lock,status,list_entries,get_entry,get_for_fill,create_entry,update_entry,delete_entry,export_backup,validate_backup,import_backup}`
  - format 1 strict, generation/revision/tombstone, exact origin
  - XChaCha20-Poly1305 + per-profile AAD
  - `KeyFile` + injected `KeyProtector`, per-profile key path
  - `fs2` key-file exclusive lock + key revalidation
  - `SecretRepository::set_with_aad_cas` (DB schema v5 유지)
  - Argon2id Master Password backup export/import
- 테스트: Linux Vault 23/23, Windows 24/24 (`windows_dpapi_vault_roundtrip` 포함)
- **아직 없음**: WinUI UI, WebView2 자동완성, Master Password UI, Windows Hello,
  자동 잠금/session lock, key rotation. (FFI ABI 2/C# client는 Phase 1.3B 완료)

### 1.3.1 ABI 2 + C# client (Phase 1.3B)

- ABI 1은 변경 없음, 별도 symbol:
  `amare_services_abi2_create/destroy/call/version/last_error`.
- `vault.*` API 전체 연결, JSON version 2, 오류 코드 20–31.
- C# `AmareVaultClient`:
  `VaultNativeMethods`, `VaultErrorCode`, `VaultException`, DTOs.
- Windows CI에서 실제 Rust DLL + C# UTF-8/CRUD/backup/conflict PASS.
- C# `status/list`에는 password 없음. `get/get_for_fill`만 password 반환.
- `VaultEntryDto.Password`는 immutable string이며 `ClearPassword()`는 best-effort.
- **아직 없음**: WinUI UI, WebView2 autofill, auto-lock, export/import UI.

### 1.3.2 WinUI Vault UI (Phase 1.3C)

- `MainWindow.Vault.cs` + `VaultHost` overlay:
  - initialize, unlock/lock, status, list, detail, add, edit, delete
  - reveal/copy, encrypted backup export/validate/import
  - explicit import confirmation, failed import preserves current vault
- ABI2 client is created after ABI1 profile initialization; profile switch locks
  and disposes/recreates the handle.
- `VaultIdleMonitor` (5-minute default), `VaultSessionMonitor`
  (`SystemEvents.SessionSwitch`), `VaultWindowCoordinator` lock/unlock broadcasts.
- `VaultClipboard`: explicit copy + best-effort 30-second cleanup.
- `VaultService::status` checks the persisted vault secret when in-memory state
  is empty, so a fresh ABI 2 client reports `Locked` (not `Uninitialized`) for an
  existing vault. Service/ABI 2 regression tests cover handle recreation.
- UI smoke: **54/54 PASS**. Verified in real WinUI windows:
  - profile switch isolation (new profile not initialized / 0 entries; returning
    to the default profile requires unlock and preserves the account count)
  - multiwindow lock/unlock broadcast (both windows change state)
  - idle timeout using `AMARE_VAULT_IDLE_SECONDS=30` → native lock → UI clear
  - CRUD/reveal/edit/backup export/import/validate and failed-import rollback
- **UNVERIFIED**: real 5-minute idle, actual Windows session lock, real Windows
  11 session/IME/DPI/accessibility.
- **미구현**: WebView2 autofill/login capture/`get_for_fill`, Windows Hello.
- 기존 결함: 이 WebView2 버전의 `NavigateToString`이 만드는 `data:text/html`
  NavigationStarting을 `NavigationPolicy`가 차단해 New Tab 페이지가 비고 status에
  전체 data URI가 기록된다. Phase 1.3C에서는 브라우저 영역을 변경하지 않았고,
  별도 승인 후 수정해야 한다 (`docs/AMARE_PHASE1_VAULT_UI_REPORT.md` 13절).

### 1.4 GPUI Vault (참고, 복사 금지)

`apps/amare-gpui/src/password_vault.rs`:

- XChaCha20-Poly1305 + DPAPI user-scope key file
- magic `AMAREVLT1`, JSON format version 2
- origin 정규화 (url crate, punycode, default port, userinfo 거부)
- exact origin 비교 (scheme/host/port)
- revision + tombstone + merge (같은 entry 충돌 거부)
- 전역 lock epoch, manual lock
- atomic temp+rename, 8MB 상한
- `PasswordEntry` Drop에서 password `zeroize`
- UI 테스트: plaintext 부재, corrupt vault 보존, key missing 보존, restart DPAPI
- 0.93 password smoke **2 PASS / 1 FAIL** — 초기 normal page navigation timeout.
  Vault 전체 시나리오가 실행되지 않았으므로 “검증됨”이 아니다.
- GPUI vault multiwindow 0.93 **1 PASS / 1 FAIL** — window unlock timeout.
- 0.81~0.83에서도 password 2/1, vault 1/1이 반복되었다.

---

## 2. 최종 권장 구조 (요약)

```text
WinUI Vault UI / autofill panel
        │  vault.* FFI (plaintext only for explicit fill)
Rust VaultService (amare-services 또는 amare-vault)
        │  SecretRepository (SQLite, profile DB)
        │  amare_security::encrypt/decrypt
        │  KeyFile + WindowsDpapiKeyProtector
        ▼
%LOCALAPPDATA%\AMARE\
  data\profiles\<profile>\amare.db   ← ciphertext only
  vault\<profile>.key                ← DPAPI-protected 32-byte key
  logs\...                           ← no secrets
```

- **DB schema 변경 없음**: 기존 `secrets` 테이블 1행에 Vault blob 저장.
- **키와 암호문 분리**: key file은 DB 밖, DPAPI user scope.
- **AAD에 profile/version 결합**: blob을 다른 프로필로 복사해도 복호화 실패.
- **기본 모드**: DPAPI-only (편리, 계정 손실 시 복구 불가).
- **복구 모드**: Master Password 기반 암호화 export/import를 별도 제공.
- **자동완성**: exact origin + top-level + explicit user gesture + no auto-submit.

---

## 3. 암호화 설계

### 3.1 알고리즘

| 항목 | 선택 |
|---|---|
| AEAD | XChaCha20-Poly1305 (기존 `amare_security`) |
| Nonce | 24-byte CSPRNG, 저장 시마다 새로 생성 |
| Tag | Poly1305 128-bit, AEAD 내장 |
| Key | 32-byte CSPRNG, DPAPI로 보호 |
| AAD | `amare.winui.password-vault.v1:<profile-id>` |
| KDF (선택) | Argon2id, Master Password 모드에서 사용 |

XChaCha20-Poly1305는 192-bit nonce로 random nonce collision 위험이 매우 낮다.
그래도 **저장마다 nonce가 달라야 하며 재사용하지 않는다.** nonce는 암호문 앞에
저장하고, 복호화 시 분리한다. nonce 재사용을 막기 위한 단일 카운터 방식은
다중 프로세스/복구에서 안전하지 않으므로 random nonce를 사용한다.

### 3.2 Vault blob format (version 1)

`secrets.ciphertext`에는 `nonce || ciphertext+tag`만 저장한다.
평문 JSON은 다음 구조를 가진다.

```json
{
  "format": 1,
  "generation": 12,
  "entries": [
    {
      "id": "uuid",
      "origin": "https://example.com",
      "username": "alice",
      "password": "secret",
      "title": "Example",
      "created_at_ms": 0,
      "updated_at_ms": 0,
      "revision": 3
    }
  ],
  "tombstones": [
    { "id": "uuid", "revision": 4, "deleted_at_ms": 0 }
  ]
}
```

- `format`은 **정확히 1만 허용**한다. 미래/알 수 없는 format은 거부하고 원본을 보존한다.
- `generation`은 저장마다 증가한다. rollback 탐지 보조에 사용하되, 완전한
  anti-rollback 보장은 아니다.
- entry id는 UUID 문자열. origin은 canonical ASCII origin.
- username/password/title을 포함한 전체 JSON을 하나의 AEAD blob으로 암호화한다.
  → DB에는 origin/username metadata조차 평문으로 남지 않는다.
- 크기 상한(예: 8 MiB)과 entry 수 상한(예: 10,000)을 둔다.

### 3.3 키 보호

- 32-byte random key를 `KeyFile`로 저장.
- `WindowsDpapiKeyProtector`, context:
  `amare.winui.password-vault.key.v1:<profile-id>`.
- key file path:
  `<appDataRoot>\vault\<profile-id>.key`.
- key file은 DB와 분리한다. DB만 복사해서는 복호화할 수 없다.
- key file에는 plaintext key를 쓰지 않는다 (`KeyFile` 구현 재사용).
- key file이 없고 ciphertext가 있으면 **복구 불가 오류**로 처리하고,
  새 key를 만들어 덮어쓰지 않는다.

### 3.4 Master Password (선택, 복구용)

기본 DPAPI-only는 계정/프로필 손실 시 복구할 수 없다. 다음 모드를 설계한다.

- 사용자가 Master Password를 설정하면:
  - Argon2id로 KEK를 유도 (salt 16-byte, m=64 MiB, t=3, p=1을 목표로 벤치마크)
  - Vault key를 KEK로 wrap한 recovery blob을 생성
  - 잠금 해제는 DPAPI key 또는 Master Password 중 하나로 가능
- Master Password를 잊으면 복구 불가. 힌트/백도어를 두지 않는다.
- Master Password는 메모리에 최소 시간만 보관하고 `char[]`/byte buffer를 zeroize.
- v1에서 Master Password를 넣을지, “암호화 export만” 제공할지는 승인 필요.

### 3.5 Windows Hello

- `Windows.Security.Credentials.UI.UserConsentVerifier`는 **사용자 존재 확인**을
  제공하지만, Vault key를 암호학적으로 release하지 않는다.
- 따라서 Windows Hello는 **unlock 전 UX gate**로만 사용하는 것을 권장한다.
- `KeyCredentialManager`로 Hello 보호 키 쌍을 만들 수 있으나, Vault key를
  안전하게 wrap하는 프로토콜/정책(TPM 유무, 도메인, PIN 변경)이 복잡하다.
  v1의 필수 요소로 채택하지 않는다.
- `PasswordVault`(Credential Locker)는 key 저장 대안이 될 수 있으나,
  DPAPI KeyFile보다 통제/백업/이식성이 불명확하므로 기본안으로 쓰지 않는다.

### 3.6 Rollback / key rotation

- AEAD는 변조를 막지만, 공격자가 이전의 정상 blob으로 되돌리는 rollback은 막지 못한다.
- `generation`을 DPAPI-protected metadata에 저장해 탐지할 수 있으나,
  같은 사용자 공격자는 둘 다 rollback할 수 있다. 완전 방어는 TPM/Hello 등
  외부 monotonic anchor가 필요하며 v1 범위 밖이다.
- key rotation이 필요하면 “새 key 파일 작성 → 새 blob 저장 → 이전 key 삭제” 순서로
  하고, 중간 실패 시 이전 key/blob으로 복구한다. v1은 “Vault 초기화”에서만
  rotation하고, 일반 저장에서는 key를 유지한다.

---

## 4. DB / 데이터 설계

### 4.1 DB 변경 필요 여부

**v1은 DB schema 변경이 필요 없다.**

- 기존 `secrets` 테이블:
  `key TEXT PRIMARY KEY, group_name TEXT, ciphertext BLOB, updated_at_ms INTEGER`
- Vault 1개를 `key='password-vault'`, `group_name='security'`로 저장한다.
- 프로필 DB가 이미 프로필별로 분리되어 있으므로 Vault도 프로필별로 격리된다.
- 암호문만 저장하므로 origin/username metadata가 DB에 평문으로 남지 않는다.
- backup의 `BackupData.secrets`가 기본 true이므로 ciphertext는 기존 백업에 포함된다.
  단, **key file은 포함되지 않는다.** (7장 참고)

필요한 코드 변경(스키마 아님):

- `SecretRepository`에 AAD를 명시하는 `set_with_aad/get_with_aad` 추가
  (기존 `set/get`은 하위 호환 유지).
- CAS(compare-and-swap) 저장:
  `updated_at_ms`/`generation`이 예상값과 다르면 충돌로 처리.
- Vault 전용 `VaultService` (Rust) + FFI `vault.*`.
- key file path/cleanup 정책.

### 4.2 Schema version / migration

- DB schema는 **v5 유지**. Vault format은 blob 내부 `format=1`.
- 기존 사용자 DB migration 없음. 기존 `secrets` 사용처(AI 등)와 key 이름이
  충돌하지 않도록 `password-vault` 예약.
- GPUI `vault.bin`은 자동 import하지 않는다. 필요 시 별도 명시적 import 도구.
- 미래에 entry별 검색/인덱스가 필요하면 schema v6에서 `vault_entries`를 추가한다.
  그 전까지는 whole-blob 방식으로 충분하다.

### 4.3 저장/동시성

- 모든 저장은 하나의 SQLite transaction.
- 같은 프로세스: Vault 전용 `SemaphoreSlim`/Rust mutex로 load-merge-save 직렬화.
- 다중 창: 저장 전 generation을 읽고, 저장 시 `WHERE updated_at_ms = expected`
  CAS를 사용한다. 불일치 시 다시 읽어 merge하거나 conflict를 반환한다.
- 다른 앱 인스턴스: SQLite `BEGIN IMMEDIATE` + busy timeout + CAS.
  완전한 multi-process merge가 필요하면 file lock/DB advisory lock을 추가한다.
- 저장 실패 시 transaction rollback, 기존 blob/key 유지.

### 4.4 삭제 / 정리

- entry 삭제는 tombstone + revision 증가 후 새 blob 저장.
- “모두 삭제”/Vault 초기화 시 key rotation + 새 빈 blob 저장.
- SQLite `DELETE`는 논리 삭제다. `secure_delete`, WAL checkpoint, VACUUM을
  적용해도 SSD wear-leveling/백업/스냅샷 때문에 물리적 완전 삭제는 보장하지 않는다.
- 프로필 삭제 시:
  - profile DB 삭제
  - `<root>\vault\<profile>.key` 삭제
  - WebView2 폴더 정리
  - 실패 시 pending cleanup 기록
- `ProfileDataCleanup`을 vault key까지 확장해야 한다.

---

## 5. 자동완성 보안 설계

### 5.1 기본 정책

- **자동 입력은 사용자가 Vault 항목의 “Fill”을 명시적으로 클릭할 때만** 실행한다.
- 페이지 로드/포커스/입력 이벤트에서 자동으로 채우지 않는다.
- **자동 submit 금지.** Enter/버튼 클릭은 사용자가 한다.
- Vault가 잠겨 있으면 목록/평문을 표시하지 않는다.
- HTTP에서는 기본적으로 자동완성하지 않는다. HTTPS만 허용하고, HTTP는
  수동 복사/경고로 제한한다. 테스트 fixture는 별도 test-only 정책으로만 허용.

### 5.2 Origin 매칭

- canonical origin = `scheme://host:port` (default port 생략).
- **exact match**만 허용:
  - scheme 동일 (http ≠ https)
  - host 동일 (subdomain 자동 포함 금지)
  - port 동일
- IDN은 `Uri.IdnHost`/`IdnMapping`으로 punycode 정규화해 저장/비교한다.
  표시할 때 Unicode와 punycode가 다르면 둘 다 보여준다.
- `userinfo`가 포함된 URL, 비 HTTP(S) scheme은 저장/자동완성 금지.
- 유사 도메인(`example.com.evil.test`)은 exact match 실패로 차단.
- homograph/혼합 script 경고는 추가 설계(선택).

### 5.3 iframe / frame 정책

- `ExecuteScriptAsync`는 top-level document에서만 실행한다.
- 주입 스크립트는 `window.top === window`를 확인한다.
- cross-origin iframe에는 자동완성하지 않는다.
- 로그인 capture 스크립트도 top-level만, `args.Source`와 `location.href`의
  origin이 일치할 때만 host로 전달한다.
- form action이 현재 origin과 다르면 자동완성하지 않거나 명시적 경고를 표시한다.
  (cross-origin POST로 비밀번호가 전송되는 것을 막기 위함)

### 5.4 필드 선택

자동완성 대상:

- `input[type="password"]`
- visible, enabled, readonly 아님
- form에 속함
- password field가 1개 이상인 login form

username 후보:

- `autocomplete="username"` 우선
- `type="email"`
- `name/id`에 user/email/login 포함
- visible/enabled
- **첫 번째 `type=text`를 무조건 채우지 않는다.**

금지:

- hidden/disabled/readonly
- `autocomplete="one-time-code"`
- contenteditable
- 검색/주소/결제 필드
- cross-origin iframe

### 5.5 WebView2 API 제약

확인된 SDK (`Microsoft.Web.WebView2.Core` 1.0.4314.35):

- `CoreWebView2Settings.IsPasswordAutosaveEnabled` / `CoreWebView2Profile.IsPasswordAutosaveEnabled`
  - 기본 false
  - 기존 저장 데이터는 비활성화 후에도 autofill될 수 있음
- `CoreWebView2Settings.IsGeneralAutofillEnabled` / `CoreWebView2Profile.IsGeneralAutofillEnabled`
  - 기본 true
- `CoreWebView2BrowsingDataKinds.PasswordAutosave` / `GeneralAutofill`
  - `ClearBrowsingDataAsync`로 기존 데이터 삭제 가능
- `AddScriptToExecuteOnDocumentCreatedAsync`는 top-level + child frame에 적용됨
- `ExecuteScriptAsync`는 top-level document
- `CoreWebView2WebMessageReceivedEventArgs.Source`로 메시지 문서 URI 확인 가능
- `CoreWebView2Frame`/`CoreWebView2FrameInfo.Source`로 frame 단위 확인 가능
- `CoreWebView2PermissionRequestedEventArgs`는 별도 사이트 권한 기능

설계:

1. WebView2 생성 직후 `IsPasswordAutosaveEnabled=false`,
   `IsGeneralAutofillEnabled=false`를 명시적으로 설정한다.
2. 기존 프로필에 password/autofill 데이터가 있으면 **사용자 동의 후**
   `ClearBrowsingDataAsync(PasswordAutosave | GeneralAutofill)`을 실행한다.
3. AMARE Vault와 WebView2 내장 autofill을 동시에 활성화하지 않는다.
4. 자동완성/캡처 메시지는 `args.Source`를 tab origin과 exact 비교한다.

### 5.6 저장 제안 (login capture)

- top-level login form submit만 감지.
- `source`와 `form.action`이 같은 origin일 때만 후보 생성.
- 비밀번호는 즉시 native로 전달하되, 자동 저장하지 않는다.
- native UI에서 “저장/업데이트/건너뛰기”를 사용자가 선택한다.
- 저장 후 원본 메시지/문자열은 가능한 한 빨리 폐기한다.
- status JSON/log에는 username/origin도 기본적으로 넣지 않는다.

### 5.7 클립보드

- 명시적 “Copy password”만 제공.
- 30초 후 클립보드가 여전히 같은 값이면 지운다. (값이 바뀌었으면 건드리지 않음)
- Windows Clipboard History/Cloud Clipboard/다른 앱은 완전 차단 불가.
  UI에 이 한계를 표시한다.
- 클립보드 복사는 Vault unlock 상태에서만.

### 5.8 잠금

- 수동 잠금 버튼.
- idle auto-lock 기본 5분 (1/5/15/never 설정).
- Windows session lock 시 잠금 (구현 API는 별도 검증 필요:
  `Microsoft.Win32.SystemEvents.SessionSwitch` 또는 Win32 WTS).
- 잠금 시 entries/평문/선택/클립보드 참조를 폐기하고 UI를 즉시 비운다.
- 여러 창이 있으면 global lock epoch로 전파한다.

---

## 6. 메모리 / 로그 / 오류

### 메모리

- Rust: key는 `VaultKey`/`Zeroizing`, plaintext JSON은 `Zeroizing<Vec<u8>>`,
  가능한 한 복사하지 않는다.
- C#: 요청한 credential 1개만 잠시 받고, fill 직후 참조를 버린다.
  .NET `string`은 zeroize 보장이 없으므로 `char[]`/`byte[]` 사용을 검토한다.
- JS/DOM/WebView2 renderer의 평문 복사본은 제거 불가. `input.value`를 지우는
  “Clear filled fields” 동작을 제공하되, 사이트 동작을 깨지 않도록 사용자 선택.
- 크래시 덤프/WER에 평문이 포함될 수 있음을 문서화하고, Vault 사용 중
  덤프 수집 정책을 별도 검토한다.

### 로그 / status

- Vault 관련 로그는 `window/profile/operation/result code`만 기록.
- password, key, origin+username 조합, vault blob, DPAPI payload 금지.
- 예외는 내부 코드로 매핑하고 `exception.Message`를 그대로 쓰지 않는다.
- status JSON에는 `locked`, `entry_count`, `error_code`만. 목록/사용자명 금지.
- AI/세션 로그와 동일한 redaction 정책을 적용한다.

### 오류 코드

권장 FFI 오류:

- `AMARE_ERR_VAULT_LOCKED`
- `AMARE_ERR_VAULT_CORRUPT`
- `AMARE_ERR_VAULT_AUTH`
- `AMARE_ERR_VAULT_UNSUPPORTED`
- `AMARE_ERR_VAULT_CONFLICT`
- `AMARE_ERR_VAULT_IO`

메시지는 일반화하고, secret/DB row 내용을 포함하지 않는다.

---

## 7. 백업 / 복구

현재:

- `BackupData.secrets` 기본 true → profile DB의 `secrets`(Vault ciphertext) 포함
- key file은 backup 대상이 아님
- 따라서 “DB backup만 복원”하면 Vault는 복호화 불가

권장:

1. **일반 자동 백업에서는 Vault를 제외**하거나, key를 포함한 별도 export만 허용.
2. Vault export:
   - 사용자가 지정한 Master Password로 Argon2id KEK 유도
   - `vault blob + metadata`를 XChaCha20-Poly1305로 암호화
   - 파일에 format/version/KDF parameter/salt/nonce 포함
   - key file은 포함하지 않음 (export 자체가 key를 wrap)
3. 복구:
   - Master Password로 export를 복호화
   - 새 key file 생성 후 Vault blob 재암호화
   - 실패 시 기존 Vault 보존
4. DPAPI-only 사용자는 “Windows 계정/프로필을 잃으면 복구 불가”를 명시적으로 동의.
5. 백업 파일에도 secret이 포함될 수 있음을 manifest에 표시 (`contains_secrets`).

v1 최소안:

- 자동 백업에서 Vault를 **명시적으로 제외**하고, “Vault 내보내기”를 별도 기능으로 제공.
- 이 결정은 구현 전 승인 필요.

---

## 8. FFI / C# 경계

**구현 상태 (Phase 1.3B): ABI 2 + C# client 완료. WinUI UI는 다음 단계.**

- ABI 1 유지, ABI 2 별도 symbol/handle.
- `vault.*` method, JSON version 2, UTF-8, `amare_string_free`.
- 오류 코드 20–31, C# `VaultErrorCode`.
- `status/list` password 없음, `get/get_for_fill`만 password.
- key material은 FFI로 반환하지 않음.
- backup은 encrypted payload hex.
- C# `AmareVaultClient`는 `IDisposable`, finalizer 없음.
- profile switch는 ABI2 handle 재생성 필요 (shared context API는 다음 단계).

설계:

- Rust `VaultService`가 key/DPAPI/암호화/저장을 모두 소유.
- C#에는 다음만 노출:
  - `vault.status`
  - `vault.unlock`
  - `vault.lock`
  - `vault.list` (origin/username/title/id, no password)
  - `vault.get_for_fill` (explicit fill 시에만 password 1개)
  - `vault.upsert`
  - `vault.delete`
  - `vault.export` / `vault.import`
  - `vault.clear_webview_autofill` (동의 후)
- key/DPAPI는 C#에 노출하지 않는다.
- 기존 `amare-ffi`는 “contract/version update”를 요구하므로
  **ABI version 2**로 올리고 C# `ExpectedAbiVersion`을 함께 갱신하는 것을 권장.
- `vault.get_for_fill`은 `origin`/`entry_id`/`expected_generation`을 받아
  Rust에서 exact origin을 재검증한다. C#이 보낸 origin만 믿지 않는다.
- plaintext는 FFI 응답에 1회 포함되고, C#은 fill 후 즉시 폐기한다.

---

## 9. 구현 단계 / 예상 작업량

| 단계 | 내용 | 예상 |
|---|---|---:|
| 1 | Rust VaultService (format, crypto, key file, CAS, profile path) | **DONE (Phase 1.3A)** |
| 2 | FFI ABI 2 + C# Session/Vault client + error mapping | **DONE (Phase 1.3B)** |
| 3 | WinUI Vault UI (unlock/list/add/edit/delete/lock/auto-lock) | **DONE (Phase 1.3C)** |
| 4 | WebView2 autofill/capture + origin/frame validation + builtin disable | **다음 단계 (1.3D)** |
| 5 | Backup/export/recovery + clipboard + logs/memory hardening | **Rust export/import DONE; UI 미구현** |
| 6 | Failure tests, security review, Windows 11 real-device | **Core PASS; 전체 미완료** |
| **합계** | | **27–42 person-days** |

기존 계획의 5–8일은 “최소 저장/조회” 기준이며, 자동완성·복구·보안 검증을
포함하면 위 범위가 현실적이다. 불확실성:

- Windows Hello/DPAPI 정책: 중~높음
- WebView2 frame/source 동작: 중간
- Master Password UX/복구: 중간
- 실제 Windows 11 IME/DPI/autofill: 높음
- 백업 키 포함 정책: 중간

---

## 10. 구현 전 승인 필요 결정

1. 기본 DPAPI-only + 별도 Master Password export vs 기본 Master Password
2. Vault를 프로필별로 둘지 (권장: 프로필별)
3. 자동 백업에서 Vault 제외 여부
4. 자동 잠금 기본값 (5분) 및 Windows session lock API
5. WebView2 내장 password/autofill 데이터 정리 시점/동의 UX
6. FFI ABI 2 채택
7. `secrets` 단일 blob vs future `vault_entries` schema v6
8. Windows Hello를 UX gate로만 사용하는 것

이 문서는 설계 제안이며, 실제 제품 코드/DB/암호화 구현은 별도 승인 후 진행한다.
