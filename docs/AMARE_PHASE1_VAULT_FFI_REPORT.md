# AMARE Phase 1.3B — FFI ABI 2 + C# Vault Client Report

- 상태: **ABI 2 / C# Vault Client 구현·검증 완료**
- Branch: `feature/phase-1-vault-ffi`
- 시작 HEAD: `02e7823` (`feature/phase-1-vault-core`의 문서 HEAD)
- 제품/테스트 HEAD: **`7dd116b`**
- 문서 HEAD: 이 보고서를 포함한 문서 commit
- 작성일: 2026-11-24
- 범위: Rust FFI ABI 2 + C# client + integration tests.
- 범위 밖: WinUI Vault UI, WebView2 autofill/로그인 감지, 실제 비밀번호 입력, Windows Hello, 패키지.

---

## 1. Git / 무결성

| 항목 | 결과 |
|---|---|
| 시작 branch | `feature/phase-1-vault-core` |
| 시작 HEAD | `02e7823` |
| 작업 branch | **`feature/phase-1-vault-ffi`** |
| 최종 제품/테스트 HEAD | **`7dd116b`** |
| Working tree | clean (문서 commit 전) |
| Stash | `stash@{0}` 보존 |
| Phase 1.3A Rust Core | 보존 |
| GPUI 변경 | 없음 |
| DB schema | **v5 유지, migration 없음** |
| ABI 1 symbols | **변경/삭제 없음** |
| 패키지/MSIX/Release | 없음 |

Phase 1.3A의 `feature/phase-1-vault-core` remote HEAD는 `02e7823`으로 보존되어 있다.
Phase 1.3B commit은 별도 `feature/phase-1-vault-ffi` branch에만 있다.

---

## 2. 변경 파일

- `crates/amare-ffi/src/services_v2.rs` (신규)
- `crates/amare-ffi/src/lib.rs` (`mod services_v2`)
- `crates/amare-ffi/Cargo.toml` (`amare-platform`, `amare-security`, `hex`)
- `crates/amare-services/src/vault.rs` (`VaultStatus`/`VaultEntryView`/`VaultBackupSummary` Serialize, `VaultEntry::view` public)
- `apps/windows/AMARE.Windows.Core/VaultNativeMethods.cs`
- `apps/windows/AMARE.Windows.Core/VaultModels.cs`
- `apps/windows/AMARE.Windows.Core/VaultClient.cs`
- `apps/windows/AMARE.Interop.Tests/Phase26Tests.cs`
- `apps/windows/AMARE.Interop.Tests/Program.cs`
- `Cargo.lock`

문서:

- `docs/AMARE_PHASE1_VAULT_FFI_REPORT.md`
- `docs/VAULT_SECURITY_DESIGN.md`
- `docs/VAULT_TEST_PLAN.md`
- `docs/AMARE_FEATURE_AUDIT.md`
- `docs/AMARE_FINALIZATION_PLAN.md`

---

## 3. ABI 1 호환성

ABI 1은 **변경하지 않았다**.

- `amare_services_abi_version()` → **1**
- `amare_services_create/destroy/call`
- `amare_core_version/create/destroy/ping/last_error/add`
- `amare_string_free`
- 기존 `services.rs`/C# `AmareNative`/`NativeMethods` 변경 없음
- ABI 1 Rust tests: **7/7 PASS**
- C# 기존 interop tests: 76/76 유지 (신규 3개 포함 총 79/79)
- ABI 1/2 동시 로드 테스트: Windows CI에서 두 handle을 같은 root에 사용해 PASS

ABI 2는 별도 handle/symbol/JSON version=2를 사용한다. `amare_string_free`만
메모리 해제 규약으로 공유한다.

---

## 4. ABI 2 symbol / 호출 규약

실제 export (`nm`):

```text
amare_services_abi2_version
amare_services_abi2_create
amare_services_abi2_destroy
amare_services_abi2_call
amare_services_abi2_last_error
```

- Calling convention: C ABI (`extern "C"`, C# `CallingConvention.Cdecl`)
- ABI version: **2**
- 요청/응답: UTF-8 C string JSON
- 요청 envelope:

```json
{"version":2,"method":"vault.status","params":{"context":{"profile_id":"default","generation":2}}}
```

- 성공: `amare_services_abi2_call` returns 0, `out_json` non-null;
  `amare_string_free`로 해제.
- 오류: return non-zero, `out_json` null, `amare_services_abi2_last_error`로
  **일반화된** 메시지 조회 후 `amare_string_free`.
- 요청 상한: 32 MiB.
- null handle/pointer, invalid UTF-8, invalid JSON, unknown method 방어.
- panic은 `guard`로 차단하고 ABI 2 panic code(2)로 반환.
- key material/DPAPI 전 키/내부 key는 ABI 2로 반환하지 않는다.

---

## 5. API 목록 / JSON 예시

| method | 기능 |
|---|---|
| `vault.initialize` | Vault 초기화 |
| `vault.unlock` | 잠금 해제 |
| `vault.lock` | 잠금 |
| `vault.status` | 잠금/메타데이터 |
| `vault.list` | password 제외 목록 |
| `vault.get` | ID로 1건 (password 포함) |
| `vault.get_for_fill` | exact origin 1건 (password 포함) |
| `vault.create` | 생성 |
| `vault.update` | 수정 (expected_revision) |
| `vault.delete` | 삭제 (expected_revision) |
| `vault.export_backup` | 암호화 backup |
| `vault.validate_backup` | backup 검증 |
| `vault.import_backup` | backup import |

### 예시

initialize:

```json
{"version":2,"method":"vault.initialize","params":{
  "context":{"profile_id":"default","generation":2}}}
```

create:

```json
{"version":2,"method":"vault.create","params":{
  "context":{"profile_id":"default","generation":2},
  "origin":"https://example.com","username":"alice",
  "password":"secret","title":"Example"}}
```

response:

```json
{"entry":{"id":123,"origin":"https://example.com","username":"alice",
          "title":"Example","created_at_ms":1,"updated_at_ms":1,"revision":1}}
```

get (유일하게 password 포함):

```json
{"version":2,"method":"vault.get","params":{
  "context":{"profile_id":"default","generation":2},"id":123}}
```

list response는 `entries`만, password 필드는 없다.

backup export/validate:

```json
{"format":1,"payload_hex":"414d415245564231..."}
```

`payload_hex`는 **암호화된** backup payload이며 key material은 포함하지 않는다.

---

## 6. 오류 코드 매핑

| code | Rust constant | C# `VaultErrorCode` | 의미 |
|---:|---|---|---|
| 0 | `AMARE_OK` | `Ok` | 성공 |
| 2 | panic | `Panic` | FFI panic 방어 |
| 20 | `VAULT_LOCKED` | `Locked` | 잠금 상태 |
| 21 | `VAULT_CORRUPT` | `Corrupt` | Vault/backup 손상 |
| 22 | `VAULT_AUTH` | `Auth` | key/auth 실패 |
| 23 | `VAULT_CONFLICT` | `Conflict` | CAS/revision 충돌 |
| 24 | `VAULT_UNSUPPORTED` | `Unsupported` | ABI/format/플랫폼 미지원 |
| 25 | `VAULT_KEY_UNAVAILABLE` | `KeyUnavailable` | key file missing/corrupt/DPAPI 실패 |
| 26 | `VAULT_INVALID_JSON` | `InvalidJson` | JSON 파싱 실패 |
| 27 | `VAULT_INTERNAL` | `Internal` | 내부 오류 |
| 28 | `VAULT_INVALID_ARGUMENT` | `InvalidArgument` | 입력 오류 |
| 29 | `VAULT_PROFILE` | `Profile` | profile/generation 불일치 |
| 30 | `VAULT_NOT_FOUND` | `NotFound` | entry 없음 |
| 31 | `VAULT_ORIGIN_MISMATCH` | `OriginMismatch` | fill origin 불일치 |

오류 메시지는 일반화되어 있으며 password, key, ciphertext, request body를 포함하지 않는다.

---

## 7. C# Client 구조

- `VaultNativeMethods` — ABI 2 P/Invoke만 담당 (ABI 1 `NativeMethods`와 분리)
- `VaultErrorCode` / `VaultErrorMapper` / `VaultException`
- `VaultStatusDto`, `VaultEntryViewDto`, `VaultEntryDto`, backup DTO
- `AmareVaultClient`:
  - `Create(root)`: ABI2 version/entrypoint 확인
  - `SemaphoreSlim`으로 native 호출 직렬화
  - `Task.Run`으로 UI thread 밖 호출
  - `Dispose`에서 ABI2 handle destroy
  - `DllNotFoundException`/`EntryPointNotFoundException`/`BadImageFormatException`을
    `Unsupported`로 매핑
  - `amare_services_abi2_last_error` + `amare_string_free`
- `VaultEntryDto.Password`는 `get`/`get_for_fill`에서만 생성.
- `status`/`list`/`create`/`update` 응답에는 password가 없다.
- A C# UI client can call `AmareVaultClient` after ABI 1 profile initialization.
  This phase does not connect it to WinUI.

### 평문 수명 정책

- password는 native JSON → C# `string`으로 1회 역직렬화된다.
- `VaultEntryDto.ClearPassword()`는 best-effort이며 원본 immutable string
  할당을 지우지 못한다. 이 한계를 문서화했다.
- finalizer에 의존하지 않는다. `AmareVaultClient`는 `IDisposable`이며 native
  handle만 해제한다.
- error/exception/log/debug 출력에 password를 넣지 않는다.
- future UI는 `get_for_fill` 결과를 즉시 사용하고 보관하지 않아야 한다.
- key material은 C#에 전달하지 않는다.

---

## 8. 테스트 결과 (실제 실행)

| Suite | 결과 |
|---|---|
| Rust ABI 1 FFI tests | **7/7 PASS** |
| Rust ABI 2 FFI tests | **5/5 PASS** |
| Rust FFI 총계 | **12/12 PASS** |
| Linux Vault Core | 23/23 PASS |
| Windows Vault Core | 24/24 PASS |
| `amare-store` | 79/79 PASS |
| `amare-services` 기존 | 38/38 PASS |
| C# interop 전체 | **79/79 PASS** (ABI1 76 + ABI2 3) |
| WinUI UI smoke | **49/49 PASS** (최종 재실행) |
| Windows CI 최종 | **146 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** |
| Linux CI | **SUCCESS** |

Windows 최종: [45354337439](https://github.com/thfzm/AMARE/actions/runs/45354337439)  
Linux 최종: [45352515604](https://github.com/thfzm/AMARE/actions/runs/45352515604)

ABI 2 실제 C# 결과:

```text
PASS vault_abi2_version_is_2: abi=2
PASS vault_abi2_error_mapping: ABI 2 codes mapped
PASS vault_abi2_platform_contract:
  ABI 2 Rust DLL + C# UTF-8/CRUD/backup/conflict PASS
```

Windows 146 PASS는 기존 143 + ABI2 C# 3개다.  
8 SKIPPED = package 7 + Netflix/Disney 1. **패키지는 생성하지 않았다.**

---

## 9. 최초 실패와 수정

| # | 최초 실패 | 분류 | 수정 |
|---:|---|---|---|
| 1 | ABI2 C# `vault_abi2_platform_contract` → `profile context is not active` | **PRODUCT_DEFECT** | ABI2가 자체 ProfileService를 init하고 generation을 ABI1 초기화 흐름과 맞추도록 수정 |
| 2 | ABI2 context cache가 generation 검증 전에 캐시를 반환할 수 있음 | **PRODUCT_DEFECT (보안)** | context 검증을 cache 조회보다 먼저 수행, generation 변경 시 cached vault lock |
| 3 | ABI2 테스트 코드의 handle/request 이름 충돌 | TEST_AUTOMATION_BUG | 테스트 helper 정리 |
| 4 | Windows UI smoke `session_multiwindow_restore` stale UIA | TEST_AUTOMATION_BUG / ENVIRONMENT_LIMITATION | 동일 HEAD 재실행 49/49 PASS |

ABI2 최초 Windows run 45348278554: C# 78/79 (ABI2 profile context 실패).  
수정 후 final run 45354337439: C# 79/79, ABI2 PASS.  
최초 실패 증거는 `artifacts/windows/phase1.3b/first-failure/`에 보존했다.

---

## 10. 보안 검토 / 미해결 위험

검증:

- ABI1/ABI2 별도 symbol, ABI1 7/7
- null/invalid UTF-8/invalid JSON/unknown method/panic guard
- `status`/`list`에 password 없음
- key material FFI 반환 없음
- backup은 encrypted payload(hex)만
- error message redaction
- output string free / no leak
- C# `IDisposable`, no finalizer
- Windows DPAPI + ABI2 C# 실제 호출 PASS
- ABI2 profile existence/generation validation
- cached vault generation change 시 lock

미해결:

1. **ABI2 profile switch**: ABI2 handle이 자체 ProfileService를 초기화하므로,
   profile switch 후에는 ABI2 handle을 재생성해야 한다. shared profile context
   API는 다음 단계.
2. **평문 수명**: C# immutable string은 zeroize 보장 없음. `ClearPassword`는 best-effort.
3. Windows 계정 변경/DPAPI 손실 복구: UNVERIFIED.
4. process kill 중 ABI2 호출/저장: UNVERIFIED.
5. WinUI UI/WebView2 autofill/로그인 감지: 이번 범위 밖.
6. Windows Hello: 미구현.
7. 실제 Windows 11 사용자 세션에서의 UI 검증: UNVERIFIED.
8. `vault.get`은 password를 JSON으로 반환하므로 future UI에서 즉시 폐기해야 한다.

---

## 11. 다음 단계 — Phase 1.3C

WinUI Vault UI 구현 범위:

1. `AmareVaultClient`를 WinUI 서비스에 연결
   - ABI1 profile initialize 후 ABI2 client 생성
   - profile switch 시 ABI2 client 재생성/lock 정책
2. Vault panel:
   - status/locked/unlocked
   - unlock/lock
   - list (password 미표시)
   - add/edit/delete
   - export/import (Master Password)
   - copy는 별도 정책
3. Auto-lock:
   - idle 5분
   - manual lock
   - Windows session lock 연동
4. UI 보안:
   - password reveal 제한
   - clipboard 30초 clear
   - error/status redaction
   - no password in logs/screenshots
5. WebView2 autofill은 **Phase 1.3D**로 분리:
   - exact origin, top-level, no auto-submit
   - built-in autofill 비활성/clear
   - form detection/사용자 승인
6. Tests:
   - WinUI Vault UI smoke
   - lock/unlock/auto-lock
   - CRUD/conflict
   - export/import
   - no plaintext in logs/status
   - Windows 11 real-device

OpenCode prompt에 포함할 핵심:

- `AmareVaultClient` API와 profile context
- ABI2 handle lifetime/profile switch
- UI에 password를 표시하지 않는 원칙
- `get`/`get_for_fill`의 평문 수명
- WinUI 49/49, Windows 146/0, interop 79/79 기준선
- WebView2 autofill 금지 범위

---

## 12. 결론

**Phase 1.3B PASS (ABI 2 / C# client / integration)**  
- ABI1 유지
- ABI2 독립 entrypoint
- 필수 Vault API 연결
- 오류 코드/메모리 해제/UTF-8 검증
- Windows 실제 Rust DLL + C# 통합 PASS
- 민감정보 로그 노출 없음

WinUI UI와 WebView2 autofill은 **미구현/UNVERIFIED**이며, Phase 1.3C에서 진행한다.
