# AMARE Phase 2.1 — Site Permissions 보고서

- 상태: **PASS** (Windows/Linux CI). 실제 Windows 11 실기기와 일부 비동기 UI 경합은 UNVERIFIED.
- 작업 branch: `feature/phase-2-site-permissions`
- 기준 branch: `feature/phase-1-vault-webview-v2`
- 기준 제품/테스트 HEAD: **`1cb4780`**
- 기준 문서 HEAD: **`75c5a5f`**
- 최종 제품/테스트 HEAD: **`f1d878d`**
- 문서 HEAD: 이 보고서를 포함한 문서 commit
- Working tree: **clean** (문서 commit 후)
- 패키지/MSIX/코드 서명/공개 Release: **없음**

---

## 1. 최종 요약

| 완료 기준 | 판정 | 근거 |
|---|---|---|
| WebView2 권한 API 실제 지원 범위 조사 | **PASS** | SDK 1.0.4314.35 IDL/XML, runtime 157.0.4341.46 |
| 지원 권한 종류 정확히 식별 | **PASS** | 13개 named kind + Unknown, Unknown은 deny |
| 사용자 승인 없는 민감 권한 허용 없음 | **PASS** | dialog 필수, 저장된 deny 즉시 deny, fail closed |
| exact origin / scheme / port / subdomain 분리 | **PASS** | C# `WebOrigin` + Rust `normalize_site_origin`, Store/FFI 테스트 |
| profile isolation | **PASS** | per-profile DB, FFI context generation, UI profile 전환 테스트 |
| 일회성/영구 권한 정책 | **PASS** | Allow once/Not now는 저장 안 함, Always allow/Block always만 저장 |
| 사이트 권한 설정 UI | **PASS** | 목록/필터/허용/차단/삭제/site reset/clear all, UI smoke |
| iframe / AI / Reader / popup 격리 | **PASS (iframe/popup) / AI·Reader 코드** | frame deny, AI/Reader deny-all, popup 자체 WebView/프로필 |
| 비동기 취소 및 창·탭 lifecycle | **PASS (코드) / 일부 UNVERIFIED** | epoch/재검증, navigation/tab close/profile switch/window close 취소 |
| DB migration 무결성 | **PASS** | v5→v6 additive migration, 기존 settings 보존, rollback 테스트 |
| Windows/Linux CI | **PASS (기록)** | Windows 168/0/8/8, UI 71/71, interop 100/100, Linux SUCCESS |
| 패키지/MSIX/서명/배포 | **없음** | package 단계 8개 SKIPPED |

**BLOCKED로 남긴 기능은 없다.** 실제 Windows 11 workstation, 실제 Windows
notification 표시, UI 수준의 permission cancel/store failure, 13개 kind 전체의
live WebView2 요청은 UNVERIFIED다.

---

## 2. Git 기준 / branch

```text
feature/phase-1-vault-webview-v2
  75c5a5f  Document Phase 1.3D WebView2 capture and autofill
  1cb4780  Cover cross-origin frames and multiple account selection

feature/phase-2-site-permissions (신규)
  f1d878d  Cover settings entry for site permissions
  96a1e86  Harden stale permission decisions and test profile isolation
  100ed7e  Cover allow-once and cross-origin permission frames
  7b4dcd2  Update service schema assertion for site permissions migration
  858dc3a  Use one permission kind for site permission smoke flow
  a87ece5  Trigger supported permission kinds in site permission smoke
  1cc7fa3  Fix permission event deferral and discard binding
  d5ed33f  Add per-profile site permissions with WebView2 consent
```

- `git merge-base --is-ancestor 1cb4780 HEAD`로 기준 제품/테스트 HEAD 포함을
  확인했다.
- 기존 Phase 1.3A~1.3D commit과 `feature/phase-1-vault-webview` branch는
  변경·삭제·rewrite하지 않았다.
- GPUI branch/source/package는 변경하지 않았다.
- force push, history rewrite를 하지 않았다.
- 문서는 제품/테스트 commit과 분리해 이 보고서를 포함한 docs commit으로
  기록한다.

---

## 3. WebView2 SDK / API 조사

사용 패키지와 CI runtime:

```text
Microsoft.Web.WebView2 1.0.4314.35
Windows CI WebView2 runtime 157.0.4341.46
CI OS: Windows Server 2025 (windows_11=false)
```

| API | 실제 확인 결과 / AMARE 사용 |
|---|---|
| `CoreWebView2.PermissionRequested` | top-level WebView 요청 event. **사용**. |
| `CoreWebView2Frame.PermissionRequested` | iframe(frame) 요청 event. **사용하되 무조건 deny**. |
| `CoreWebView2PermissionRequestedEventArgs.Uri` | 요청 origin. **`core.Source`와 exact origin 비교**. |
| `...PermissionKind` | `COREWEBVIEW2_PERMISSION_KIND`. **13개 named kind + Unknown**. |
| `...IsUserInitiated` | user gesture 여부. **로그에 bool만 기록**, 허용 판단의 유일 근거로 쓰지 않음. |
| `...State` | `Default/Allow/Deny`. **Default로 두지 않고 Allow/Deny를 명시**. |
| `...GetDeferral()` | 현재 요청 1건에만 적용되는 deferral. **dialog 비동기 처리에 사용**, 반드시 dispose. |
| `ICoreWebView2PermissionRequestedEventArgs2.Handled` | frame handler에서 `true`로 설정해 Core handler 중복 호출을 막음. **사용**. |
| `ICoreWebView2PermissionRequestedEventArgs3.SavesInProfile` | get/set. **모든 결정에서 `false`**. WebView2 profile 저장을 source of truth로 쓰지 않음. |
| `CoreWebView2Profile.GetNonDefaultPermissionSettingsAsync` | SDK에 존재하지만 **결정 저장/조회에 사용하지 않음**. |
| `CoreWebView2Profile.SetPermissionStateAsync` | SDK에 존재하지만 **AMARE store를 대신하지 않음**. |
| `CoreWebView2.NewWindowRequested` | popup/new window. 기존 새 탭 경로를 거쳐 **같은 프로필의 새 WebView**로 열림. |
| `CoreWebView2.NotificationReceived` | 구현하지 않음. 알림 권한 결정만 관리하며 toast 표시 pipeline은 범위 밖. |

중요한 SDK 동작:

- `PermissionRequested`는 **deferral을 잡지 않으면 handler가 반환될 때** 결정이
  적용된다. AMARE는 dialog 동안 deferral을 잡고, 완료 후 `State`를 설정한다.
- `SavesInProfile=false`이면 그 결정은 현재 요청에만 적용되고, 같은
  origin/kind의 future request에서 event가 다시 발생한다. 따라서 **AMARE
  store lookup이 매 요청의 authoritative decision**이 된다.
- `Handled=true`는 frame handler에서 Core handler로의 전파를 막는다. deferral을
  잡는 경우에는 deferral 전에 동기적으로 설정해야 한다. AMARE frame handler는
  deferral을 잡지 않고 즉시 deny한다.
- `CoreWebView2Frame`에는 신뢰 가능한 frame origin/source 속성이 없다. 따라서
  frame 요청은 "검증해서 허용"하지 않고 **수신 즉시 deny**한다.
- `PermissionKind.UnknownPermission`은 AMARE가 저장하지 않는다. dialog 없이
  deny한다.
- `PermissionState.Default`는 브라우저 기본 동작(보통 prompt)으로 돌아가므로
  AMARE는 Default를 사용하지 않는다. 항상 Allow 또는 Deny를 명시한다.

API에 존재하지 않거나 이 runtime에서 지원되지 않는 기능을 추측으로 구현하지
않았다. `SavesInProfile`은 SDK/runtime 157에서 지원됨을 확인하고 사용했다.

---

## 4. 지원 권한 종류

WebView2 `COREWEBVIEW2_PERMISSION_KIND`의 **named kind 13개**를 그대로
지원한다. `UnknownPermission`은 지원 목록에 넣지 않고 deny한다.

| # | WebView2 kind | AMARE 이름 | UI 표시 |
|---:|---|---|---|
| 1 | Microphone | `microphone` | Microphone |
| 2 | Camera | `camera` | Camera |
| 3 | Geolocation | `geolocation` | Location |
| 4 | Notifications | `notifications` | Notifications |
| 5 | OtherSensors | `other_sensors` | Motion and light sensors |
| 6 | ClipboardRead | `clipboard_read` | Clipboard read |
| 7 | MultipleAutomaticDownloads | `multiple_automatic_downloads` | Multiple automatic downloads |
| 8 | FileReadWrite | `file_read_write` | File access |
| 9 | Autoplay | `autoplay` | Autoplay |
| 10 | LocalFonts | `local_fonts` | Local fonts |
| 11 | MidiSystemExclusiveMessages | `midi_system_exclusive_messages` | MIDI devices |
| 12 | WindowManagement | `window_management` | Window management |
| 13 | PersistentStorage | `persistent_storage` | Persistent storage |
| — | UnknownPermission | 저장 안 함 | dialog 없이 deny |

`SitePermissionCatalog`(C#)와 `SitePermissionKind`(Rust)가 동일한 canonical
이름을 사용한다. Store CHECK 제약도 같은 13개만 허용한다.

---

## 5. 요청 처리 / 보안 정책

### 5.1 요청 처리 순서

```text
PermissionRequested
  → SavesInProfile = false
  → deferral 획득
  → 현재 탭/WebView/profile/New Tab/dialog 중복 확인
  → kind 매핑 (Unknown → deny)
  → args.Uri와 core.Source를 HTTPS origin으로 정규화
  → 두 origin이 exact 일치하지 않으면 deny (cross-origin frame/third-party)
  → AMARE per-profile store에서 (origin, kind) 조회
  → 저장된 allow/deny가 있으면 그 결정 적용
  → 없으면 AMARE consent dialog
      Allow once   : 현재 요청만 Allow (저장 안 함)
      Always allow : store에 allow 저장 (성공 시 Allow)
      Not now/X    : 현재 요청만 Deny (저장 안 함)
      Block always : store에 deny 저장 시도 후 현재 요청은 무조건 Deny
  → lookup/dialog 이후 epoch/탭/WebView/profile/generation/origin 재검증
  → State = Allow 또는 Deny
  → deferral dispose
```

- 저장된 deny는 dialog 없이 즉시 deny한다.
- 저장된 allow는 dialog 없이 allow한다. 단, lookup 이후에도 요청 컨텍스트가
  유효한지 다시 검증한다.
- 사용자가 선택하지 않은 상태( dialog X, 앱 종료, navigation, tab close,
  profile switch)는 모두 deny로 끝난다.
- 저장 실패, FFI 오류, profile context 불일치는 allow로 fallback하지 않는다.
- `Block always`는 저장이 conflict로 실패하더라도 **현재 요청을 allow로 바꾸지
  않는다**. 다른 창의 기존 allow가 남아 있을 수는 있지만, 현재 사용자의
  명시적 거부를 현재 요청에 적용한다.

### 5.2 exact origin

`WebOrigin`(C#)과 `normalize_site_origin`(Rust)의 공통 정책:

- **HTTPS만** 허용한다. HTTP, `data:`, `file:`, `javascript:`, opaque origin은
  거부한다.
- scheme, hostname, port를 **exact** 비교한다.
- default port 443은 정규화하고, 명시 port는 그대로 비교한다.
- subdomain에 권한을 상속하지 않는다.
  `https://example.com`과 `https://www.example.com`은 별개다.
- IDN은 punycode lowercase로 정규화한다.
  `https://bücher.example` → `https://xn--bcher-kva.example`
- lookalike/Unicode host는 정규화 후 exact 비교로 분리한다.
- userinfo(`user:pass@`), trailing dot, 앞뒤 공백, 제어문자, 잘못된 URL을
  거부한다.
- URL prefix 문자열 비교를 하지 않는다.

Rust store는 `url` crate의 `origin().ascii_serialization()`을 사용하고,
C#은 `IdnMapping`/`Uri` 기반 `WebOrigin`을 사용한다. FFI 경계에서도 store가
다시 정규화하므로 C# 문자열을 그대로 저장하지 않는다.

### 5.3 frame / AI / Reader / popup

- **iframe / third-party frame**: `CoreWebView2Frame.PermissionRequested`에서
  `Handled=true`, `SavesInProfile=false`, `State=Deny`로 즉시 거부한다.
  same-origin/cross-origin 모두 포함한다. frame origin을 신뢰할 수 있는 API가
  없으므로 예외를 두지 않는다.
- **top-level cross-origin**: `args.Uri` origin과 `core.Source` origin이 다르면
  deny한다. top-level handler가 frame 요청을 받는 경우에도 이 비교로 막힌다.
- **AI WebView**: 별도 environment를 사용하며 `AttachDenyAllPermissions`로
  모든 요청을 deny한다. 일반 프로필 권한을 상속하지 않는다.
- **Reader WebView**: 같은 deny-all 정책을 적용한다.
- **popup / 새 창**: `NewWindowRequested`가 기존 새 탭 경로를 거쳐 새 WebView를
  만들고, 그 WebView에도 동일한 `AttachPermissionPolicy`가 적용된다. 요청
  WebView/탭/프로필 컨텍스트를 유지하며, UI test에서 popup 자체 tab의
  dialog/origin을 확인했다.
- **New Tab 내부 문서**: `host.Model.IsNewTabPage`/`host.ShowingNewTab`이면
  deny한다. `data:`/`file:`/`javascript:` 일반 navigation 정책은 변경하지
  않았다.

### 5.4 profile isolation

- 권한 row는 **프로필별 SQLite DB**에 저장한다.
- 테이블 PK에 `profile_id`를 포함해 같은 DB 연결에서도 다른 프로필 row를
  조회/변경할 수 없게 한다.
- FFI는 `ProfileContext`의 `profile_id`와 `generation`을 `ProfileService`에서
  검증한다. stale context는 `AMARE_ERR_PERMISSION_PROFILE`로 거부한다.
- 프로필 전환 시 pending permission dialog와 site permissions dialog를
  닫고, epoch를 증가시켜 이전 요청의 결정을 무효화한다.
- UI test `site_permissions_profile_isolation`:
  default profile에서 Always allow → 새 프로필에서는 목록 0건 → default로
  돌아오면 allow row가 그대로 존재.

### 5.5 중복 / stale request

- `_permissionRequestActive`: 한 번에 하나의 요청만 store lookup/dialog를
  진행한다. 추가 요청은 deny한다.
- `_permissionDialogOpen`: dialog가 열려 있으면 새 dialog를 만들지 않는다.
- `_permissionRequestEpoch`: navigation/tab close/profile switch/window close/
  dialog cancel 시 증가한다.
- lookup 후와 dialog 후 **두 번** `PermissionRequestIsCurrent`로 다음을
  재검증한다.
  - epoch
  - tab closed
  - `host.View.CoreWebView2` identity
  - `_context` identity / `_profileId`
  - `_vaultClient` identity
  - Vault/document generation
  - New Tab 여부
  - `core.Source`의 현재 top-level origin == 요청 origin
- 하나라도 바뀌면 deny한다.

### 5.6 취소/lifecycle

다음 경로에서 pending request를 무효화하고 dialog를 닫는다.

- `NavigationStarting`
- 탭 닫기 (`CloseHost`)
- 프로필 전환 (`ResetVaultForProfileChangeAsync`)
- 메인 창 닫기 (`MainWindow_Closed`)
- 앱 종료로 이어지는 창 close
- dialog 취소/X

dialog가 닫힌 뒤 `ShowAsync` 결과가 `None`이면 deny한다. deferral은
`using`으로 dispose하며, 요청이 이미 무효화된 경우에도 `State=Deny`를
설정하려고 시도하고 실패는 무시한다(fail closed).

---

## 6. 저장 구조 / schema v6

### 6.1 DB schema

프로필 DB schema를 **v5 → v6**으로 올리고 `site_permissions` 테이블을
추가했다. migration은 additive이며 기존 테이블/데이터를 변경하지 않는다.

```sql
CREATE TABLE site_permissions (
    profile_id       TEXT NOT NULL,
    origin           TEXT NOT NULL,
    permission_kind  TEXT NOT NULL CHECK (permission_kind IN (
        'microphone','camera','geolocation','notifications',
        'other_sensors','clipboard_read','multiple_automatic_downloads',
        'file_read_write','autoplay','local_fonts',
        'midi_system_exclusive_messages','window_management',
        'persistent_storage')),
    decision         TEXT NOT NULL CHECK (decision IN ('allow','deny')),
    scope            TEXT NOT NULL CHECK (scope IN ('persistent')),
    revision         INTEGER NOT NULL CHECK (revision > 0),
    created_at_ms    INTEGER NOT NULL,
    updated_at_ms    INTEGER NOT NULL,
    PRIMARY KEY (profile_id, origin, permission_kind)
);
```

인덱스:

- `idx_site_permissions_profile_updated(profile_id, updated_at_ms DESC, origin, permission_kind)`
- `idx_site_permissions_origin(profile_id, origin, permission_kind)`

저장하지 않는 것:

- password, Vault secret, 인증정보, username
- session-only decision (`scope='session'`은 store가 거부)
- WebView2 profile permission 자체

### 6.2 revision / CAS

`SitePermissionRepository`는 optimistic concurrency를 사용한다.

- `expected_revision == 0`: **insert only**. 기존 row가 있으면 conflict.
- `expected_revision > 0`: 기존 revision과 정확히 일치해야 update.
- `delete`: 기존 revision과 일치해야 delete.
- row가 없거나 revision이 다르면 `StoreError::Conflict`.
- `clear_origin`/`clear_all`은 명시적 reset이므로 revision 없이 삭제한다.

FFI/C# client도 이 규약을 그대로 노출한다. Settings UI는 conflict 시 목록을
다시 읽고 오류를 표시하며, 성공으로 표시하지 않는다.

### 6.3 migration / 기존 데이터

- `PROFILE_SCHEMA_VERSION`을 6으로 갱신했다.
- v5 DB에 v6 migration을 적용하고 기존 `settings` row가 보존되는 테스트를
  추가했다 (`upgrades_v5_database_to_v6_preserves_data`).
- migration 실패 시 기존 transaction rollback 테스트가 유지된다.
- `ProfileStore::reset_user_data`는 `site_permissions`도 삭제하지만 다른
  프로필 DB나 Vault secret은 건드리지 않는다.
- 기존 `secrets`/Vault blob, settings, bookmarks, history, sessions, downloads는
  변경하지 않았다.

### 6.4 FFI ABI 2

ABI 1은 그대로 두고 ABI 2에 `permissions.*` method를 추가했다.

| method | params | result |
|---|---|---|
| `permissions.list` | context | `{permissions: [...]}` |
| `permissions.get` | context, origin, kind | `{permission: ...|null}` |
| `permissions.set` | context, origin, kind, decision, scope, expected_revision | `{permission: ...}` |
| `permissions.delete` | context, origin, kind, expected_revision | `{deleted: true}` |
| `permissions.clear_origin` | context, origin | `{removed: n}` |
| `permissions.clear_all` | context | `{removed: n}` |

추가 error code:

| code | 의미 |
|---:|---|
| 40 | permission invalid input/kind/decision/scope |
| 41 | permission conflict (stale revision) |
| 42 | permission not found |
| 43 | profile context invalid/stale |
| 44 | permission storage/internal error |

C# `AmareVaultClient`에 `ListPermissionsAsync`, `GetPermissionAsync`,
`SetPermissionAsync`, `DeletePermissionAsync`, `ClearOriginPermissionsAsync`,
`ClearAllPermissionsAsync`를 추가했다. 별도 JSON 파일/DB를 만들지 않는다.

---

## 7. UI / 설정

### 7.1 권한 요청 dialog

- 제목: `Permission request`
- 표시: 정확한 origin, 권한 종류, 안내 문구
- 버튼:
  - `Allow once` (Primary): 현재 요청만 allow, 저장 안 함
  - `Always allow` (Secondary): store에 allow 저장
  - `Not now` (Close): 현재 요청 deny, 저장 안 함
  - `Block always` (custom): store에 deny 저장 시도, 현재 요청은 무조건 deny
- 기본 버튼은 `Close`다. Enter로 자동 허용되지 않는다.
- automation id: `PermissionRequestOrigin`, `PermissionRequestKind`,
  `PermissionBlockAlwaysButton`.

### 7.2 설정 화면

진입 경로 2개:

- 메뉴 → `Site permissions…`
- Settings → `Manage site permissions…`

목록/기능:

- 현재 프로필의 저장된 권한만 표시한다.
- `origin — 권한 종류 — Allow/Block` 형식.
- 권한 종류별 filter ComboBox (`All permissions` + 13 kinds).
- `Allow`, `Block`, `Remove`: 선택 row를 revision CAS로 변경/삭제.
- `Reset site`: 선택 origin의 모든 kind 삭제. 2-click confirm.
- `Clear all`: 현재 프로필의 모든 site permission 삭제. 2-click confirm.
- 저장된 권한이 없으면 `No saved site permissions for this profile.` 표시.
- 목록 로드/저장/삭제 실패 시 error text를 표시하고 목록을 reload한다.
  실패를 성공 상태로 표시하지 않는다.
- 목록에는 password/Vault secret/인증정보가 포함되지 않는다.

프로필 전환 시 `CancelSitePermissionsDialog`로 설정 dialog를 닫고, 새
프로필의 `_context`로만 목록을 읽는다.

---

## 8. 테스트 결과

### 8.1 Windows / Linux

| Suite | 결과 |
|---|---|
| WinUI UI smoke | **71/71 PASS** (기존 64 + site permissions 7) |
| C# ↔ Rust interop | **100/100 PASS** |
| Rust `site_permissions` | **5/5 PASS** |
| Rust `storage` | **13/13 PASS** |
| Rust `session_windows` | **10/10 PASS** |
| Rust `services` | **38/38 PASS** |
| Rust vault (Windows) | **25/25 PASS** |
| Rust FFI lib | **14/14 PASS** |
| Windows 통합 검증 | **168 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** (184 total) |
| Linux CI | **SUCCESS** (interop 100/100, Linux vault 24/24) |
| Package/MSIX | **SKIPPED (8)**, 생성 없음 |

최종 Windows run: [45685894741](https://github.com/thfzm/AMARE/actions/runs/45685894741)
(`f1d878d`)

최종 Linux run: [45682507161](https://github.com/thfzm/AMARE/actions/runs/45682507161)
(`f1d878d`)

### 8.2 신규 UI test

```text
PASS site_permissions_one_time_decisions
  - 최초 요청 dialog 표시
  - Not now: 저장 없음
  - Allow once: 저장 없음
PASS site_permissions_always_allow_and_settings
  - Always allow → 설정 목록 Allow
  - 재방문 시 dialog 없음
  - 설정에서 Block 변경 → 재방문 시 dialog 없음
  - Reset site → 다시 dialog 표시
PASS site_permissions_block_always
  - Block always → 설정 목록 Block
  - 재방문 시 dialog 없음
  - Clear all → row 제거
PASS site_permissions_iframe_denied
  - same-origin + cross-origin iframe 요청 모두 dialog/저장 없음
PASS site_permissions_popup
  - popup 자체 tab에서 dialog 표시, origin `https://amare-vault.test`
PASS site_permissions_settings_entry
  - Settings → Manage site permissions… → 목록 dialog
PASS site_permissions_profile_isolation
  - default profile allow → 새 profile 목록 0건
  - default 복귀 후 allow row 유지
```

### 8.3 Core / Store / FFI / interop

```text
PASS permission_crud_and_origin_normalization
PASS permission_origin_validation_is_exact
PASS permission_profile_isolation
PASS permission_scope_kind_and_reset
PASS permission_conflict_for_missing_or_stale_rows
PASS upgrades_v5_database_to_v6_preserves_data
PASS abi2_permissions_crud_conflict_and_validation
PASS permissions_client_crud_conflict_and_origin
```

- exact origin: scheme/port/subdomain/IDN/punycode/lookalike/userinfo/trailing
  dot/invalid URL.
- profile isolation: profile A allow ↔ profile B deny.
- CAS: insert-only, stale update, stale delete, missing row.
- session scope 거부, invalid kind/decision/scope/JSON.
- migration v5→v6: `site_permissions` 생성 + 기존 settings 보존.
- reset_user_data가 permission만 삭제.

### 8.4 PASS / UNVERIFIED / BLOCKED / SKIPPED

**PASS**

- WebView2 13 named permission kinds 식별, Unknown deny
- 사용자 consent 없는 allow 없음
- exact HTTPS origin / port / subdomain / IDN / lookalike
- per-profile store isolation (Store/FFI/UI)
- Allow once/Not now 비저장, Always allow/Block always 저장
- 설정 목록/filter/Allow/Block/Remove/Reset site/Clear all
- 설정 저장 실패를 성공으로 표시하지 않는 코드 경로
- iframe same/cross-origin deny
- popup 자체 WebView/프로필 컨텍스트
- AI/Reader deny-all (코드)
- navigation/tab close/profile switch/window close 시 epoch 무효화 (코드)
- v5→v6 migration, 기존 데이터 보존, rollback
- Vault/New Tab/navigation/session 기존 UI 회귀
- Windows/Linux CI

**UNVERIFIED**

- 실제 Windows 11 workstation (CI는 Windows Server 2025)
- 실제 Windows notification toast 표시 (`NotificationReceived`/OS integration
  미구현)
- UI 수준 navigation/tab close/profile switch 중 pending request 취소 경합
  (코드 경로만, 직접 UI fixture 없음)
- 권한 저장 실패를 강제로 주입한 UI 일관성 테스트
- AI/Reader WebView의 실제 permission 요청 event (deny-all 코드만)
- Notifications 외 12개 kind의 live WebView2 request/dialog
  (store/FFI에서는 13개 모두 CRUD PASS)
- UnknownPermission event fixture (코드상 deny)
- 동시 두 창에서 같은 origin/kind를 동시에 변경하는 UI 수준 conflict
  (Store/FFI CAS는 PASS)
- 저장된 allow/deny가 실제 page API 결과에 미치는 영향
  (dialog 미표시만 확인)
- 이전 버전/WebView2 기본 prompt로 profile에 남아 있을 수 있는 non-default
  permission setting의 열거/삭제 (아래 제한 참조)

**BLOCKED**

- 없음.

**SKIPPED**

- 패키지 8단계 (MSIX/portable/release metadata/preflight)
- Netflix/Disney 유료 서비스 probe 1단계

---

## 9. 최초 실패 및 수정 이력

| # | run | 실패 | 분류 | 수정 |
|---:|---|---|---|---|
| 1 | [45663612997](https://github.com/thfzm/AMARE/actions/runs/45663612997) | `MainWindow.Permissions.cs` compile: lambda discard/type binding, `CoreWebView2Deferral`/`Dispose` | PRODUCT_BUILD_DEFECT | event lambda `(sender, args)`, `using var deferral = args.GetDeferral()` |
| 2 | [45665113354](https://github.com/thfzm/AMARE/actions/runs/45665113354) | permission dialog 미표시 (`getUserMedia`), `new_window_button` timeout | TEST_AUTOMATION_BUG / PRE-EXISTING FLAKE | CI에서 실제 event가 확인된 Notifications fixture 사용, 단일 kind 흐름 |
| 3 | [45668353377](https://github.com/thfzm/AMARE/actions/runs/45668353377) | notification/geolocation/microphone 자동 요청이 겹쳐 kind mismatch/예상 밖 dialog | TEST_AUTOMATION_BUG | fixture를 한 kind(notification)만 요청하도록 단순화 |
| 4 | [45671716447](https://github.com/thfzm/AMARE/actions/runs/45671716447) | UI 69/69였지만 `services.rs` schema assertion 5 vs 6 | TEST_ASSERTION_DEFECT | 모든 schema assertion을 v6으로 갱신 |
| 5 | [45675021166](https://github.com/thfzm/AMARE/actions/runs/45675021166) | pre-hardening green: 168/0/8/8, UI 69/69, interop 100/100 | — | 증거 보존 |
| 6 | [45678645657](https://github.com/thfzm/AMARE/actions/runs/45678645657) | Allow once/cross-origin 추가 후 green | — | 증거 보존 |
| 7 | [45682502376](https://github.com/thfzm/AMARE/actions/runs/45682502376) | 동일 HEAD에서 `new_tab_address_search`, `session_multiwindow_restore` 2개 flake. site permissions 7/7 PASS | PRE-EXISTING UI FLAKE | assertion 약화 없이 동일 HEAD 재실행 |
| 8 | [45685894741](https://github.com/thfzm/AMARE/actions/runs/45685894741) | 최종 재실행: 168/0/8/8, UI 71/71, interop 100/100 | — | 최종 증거 |

- 1번 compile failure log: `artifacts/windows/phase2.1-site-permissions/compile-failure-45663612997.log`
- 최종/중간 UI 결과와 amare log: `artifacts/windows/phase2.1-site-permissions/`
- 실패한 최초 결과를 삭제하거나 PASS로 바꾸지 않았다.
- flake 2개는 site permissions 변경과 무관한 기존 UI test이며, 동일 HEAD
  재실행에서 71/71 PASS를 확인했다.

---

## 10. 보안 검토 / 남은 제한

1. **기존 WebView2 profile permission setting**: AMARE는 새 결정에 대해
   `SavesInProfile=false`를 설정하고 store만 source of truth로 사용한다.
   그러나 Phase 2.1 이전에 WebView2 기본 prompt/외부 도구로 profile에 저장된
   non-default permission setting을 **열거·삭제하지 않는다**. 이런 setting이
   있으면 WebView2가 event를 발생시키지 않아 AMARE 목록과 실제 상태가 다를 수
   있다. 향후 `GetNonDefaultPermissionSettingsAsync` +
   `SetPermissionStateAsync(Default)`로 startup audit/reset을 검토해야 한다.
2. **실제 Windows 11**: CI는 Windows Server 2025다. Windows 11의 알림 센터,
   실제 device(camera/mic), IME/DPI/접근성은 UNVERIFIED다.
3. **notification 표시**: 권한 결정만 관리하고 `NotificationReceived`/OS toast
   pipeline은 구현하지 않았다. 권한을 allow해도 실제 알림 표시는 별도 기능이다.
4. **13개 kind live 검증**: Store/FFI는 13개 모두 CRUD PASS지만, WebView2
   live request/dialog는 CI에서 Notifications만 안정적으로 재현했다. 나머지
   kind는 코드 mapping과 deny 정책으로만 검증했다.
5. **비동기 취소 경합**: navigation/tab close/profile switch/window close에서
   epoch/재검증 코드가 있으나, UI 자동화로 모든 race를 재현하지는 못했다.
6. **store failure UI**: FFI 오류를 강제로 주입해 설정 UI의 실패 표시를
   검증하지 못했다. 코드는 실패 시 error text + reload만 수행하고 성공
   상태를 표시하지 않는다.
7. **AI/Reader**: deny-all handler를 attach하지만 실제 permission 요청 event를
   UI로 발생시켜 검증하지 않았다.
8. **Block always conflict**: 현재 요청은 무조건 deny하지만, 다른 창이 이미
   allow를 저장한 경우 store row는 allow로 남을 수 있다. 이는 현재 요청의
   fail-closed를 우선한 결과이며, UI 목록은 store 상태를 그대로 보여준다.
9. **Frame 호환성**: frame origin을 신뢰할 수 없어 same-origin iframe도
   deny한다. 정당한 embedded permission flow는 동작하지 않는다
   (security over compatibility).
10. **Session decision**: Allow once/Not now는 현재 request에만 적용하고
    메모리 cache도 남기지 않는다. 같은 문서에서 재요청하면 다시 dialog가
    나타날 수 있다. 이는 저장하지 않는 정책의 의도된 결과다.
11. **WebView2 runtime**: `SavesInProfile`은 args3/runtime 157에서 지원된다.
    더 오래된 Evergreen runtime에서는 속성 지원 여부를 재확인해야 한다.
12. **Vault/New Tab 무변경**: Vault 암호화/capture/autofill, New Tab
    provenance, 일반 `data:` navigation 정책은 변경하지 않았다.

---

## 11. 변경 파일 / 커밋

### 제품

- `apps/windows/AMARE.Windows/MainWindow.Permissions.cs` (신규)
- `apps/windows/AMARE.Windows/MainWindow.xaml.cs`
- `apps/windows/AMARE.Windows/MainWindow.Vault.cs`
- `apps/windows/AMARE.Windows.Core/SitePermissionCatalog.cs` (신규)
- `apps/windows/AMARE.Windows.Core/VaultClient.cs`
- `apps/windows/AMARE.Windows.Core/VaultModels.cs`
- `apps/windows/AMARE.Windows.Core/VaultNativeMethods.cs`
- `crates/amare-core/src/domain.rs`
- `crates/amare-store/src/migrations.rs`
- `crates/amare-store/src/lib.rs`
- `crates/amare-store/src/repositories/mod.rs`
- `crates/amare-store/src/repositories/site_permissions.rs` (신규)
- `crates/amare-store/Cargo.toml` (`url`)
- `crates/amare-ffi/src/services_v2.rs`

### 테스트 / 검증

- `crates/amare-store/tests/site_permissions.rs` (신규, 5 tests)
- `crates/amare-store/tests/storage.rs`
- `crates/amare-store/tests/session_windows.rs`
- `crates/amare-services/tests/vault.rs`
- `crates/amare-services/tests/services.rs`
- `apps/windows/AMARE.Interop.Tests/Phase30Tests.cs` (신규)
- `apps/windows/AMARE.Interop.Tests/Program.cs`
- `tools/windows-ui-smoke/Program.cs` (+7 site permission UI tests)
- `tools/windows-ui-smoke/assets/vault/permissions*.html` (신규, test-only)
- `Cargo.lock`

### 주요 commit

```text
d5ed33f Add per-profile site permissions with WebView2 consent
1cc7fa3 Fix permission event deferral and discard binding
a87ece5 Trigger supported permission kinds in site permission smoke
858dc3a Use one permission kind for site permission smoke flow
7b4dcd2 Update service schema assertion for site permissions migration
100ed7e Cover allow-once and cross-origin permission frames
96a1e86 Harden stale permission decisions and test profile isolation
f1d878d Cover settings entry for site permissions
```

---

## 12. 결론 및 다음 단계

- Phase 2.1의 핵심 기능은 **PASS**다.
  - WebView2 `PermissionRequested`/frame event → AMARE consent dialog
  - exact HTTPS origin + per-profile SQLite store(schema v6)
  - Allow once/Not now 비저장, Always allow/Block always 저장
  - 설정 목록/filter/변경/삭제/site reset/clear all
  - iframe/AI/Reader deny, popup 컨텍스트 유지
  - migration/rollback 및 기존 데이터 보존
- Windows **168/0/8/8**, UI **71/71**, interop **100/100**, Linux **SUCCESS**.
- 실제 Windows 11, notification 표시, 전체 kind live request, 비동기 UI
  race, store failure UI는 UNVERIFIED로 남긴다.
- 가장 우선할 후속 보안 작업은 **기존 WebView2 non-default permission
  setting의 startup audit/reset**이다.

다음 단계는 이 보고서를 확인한 뒤 결정한다. 패키지/MSIX/서명/공개 배포는
수행하지 않았다.
