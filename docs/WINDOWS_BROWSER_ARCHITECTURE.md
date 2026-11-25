# AMARE Windows 브라우저 아키텍처

- 작성일: 2026-11-03
- 단계: Phase 2 (Windows 브라우저 본체)
- 연결: C# WinUI 3 → C# Adapter → C ABI → Rust Services → SQLite
- 렌더링: WebView2 Evergreen

---

## 1. 계층

```text
AMARE.Windows (WinUI 3)
  MainWindow / tabs / toolbar / profile menu / new tab page
        │
AMARE.Windows.Core (net8.0, platform-independent C#)
  AmareNative, ProfileClient, SettingsClient, SearchClient,
  SessionClient, HistoryClient, TabManager, address bar,
  profile path, navigation/download policy, new-tab HTML,
  WebOrigin, SitePermissionCatalog, AmareVaultClient(permissions)
        │
amare_ffi (C ABI, JSON service envelope; ABI 1 + ABI 2)
        │
amare-services (ProfileService, SettingsService, SearchService, VaultService)
        │
amare-store (SQLite repositories, FTS5, sessions, site_permissions, backup)
```

- `AMARE.Windows.Core`는 WinUI/WebView2 타입을 포함하지 않아 Linux에서 테스트합니다.
- WinUI 프로젝트는 WebView2 control, window, dialog, 실제 UI만 담당합니다.
- Rust Core/Store/Services를 다시 작성하지 않고 FFI JSON envelope로 연결했습니다.

---

## 2. 실제 구현된 브라우저 흐름

1. `MainWindow` 로드 → `AmareNative` 생성 → `ProfileService.initialize`
2. 활성 프로필의 settings에서 `engine`, `restore_session`, `theme` 조회
3. 프로필별 WebView2 UserDataFolder로 `CoreWebView2Environment` 생성
4. 세션 복원 또는 새 탭 생성
5. 주소창 → `AddressBarResolver` → URL 또는 검색 URL
6. 활성 WebView2 → 실제 `CoreWebView2.Navigate`
7. 이벤트 → `BrowserTabModel` (URL/title/loading/back-forward)
8. 탭/세션 변경 → debounce 후 Rust SessionRepository 저장

---

## 3. C ABI

기존 `amare_core_*` API는 유지하고, `AmareServices` handle과 JSON 호출을 추가했습니다.

```c
uint32_t amare_services_abi_version(void);
int amare_services_create(const char* root_dir, AmareServices** out);
void amare_services_destroy(AmareServices*);
int amare_services_call(AmareServices*, const char* request_json, char** out_json);
```

Request:

```json
{"version":1,"method":"profile.switch","params":{"id":"work"}}
```

지원 method:

- Profile: `profile.initialize/list/active/current_context/exists/create/rename/set_icon/set_default/switch/delete/reset`
- Settings: `settings.get/list_group/list_all/set/set_many/reset_key/reset_group/reset_all`
- Search: `search.query`
- Session: `session.save/latest/delete`
- History: `history.top/list/record/clear`
- Bookmarks: `bookmark.list/create/delete`

ABI 2는 별도 symbol/handle을 사용하며 Vault와 사이트 권한을 제공한다.

- Vault: `vault.status/initialize/unlock/lock/list/get/get_for_fill/create/update/delete/export/import`
- Permissions: `permissions.list/get/set/delete/clear_origin/clear_all`

ABI 2도 `ProfileContext`의 `profile_id`/`generation`을 검증하며, stale context는
오류 코드로 거부한다. 사이트 권한 오류 코드는 40–44다.

Error code는 `amare.h`에 정의되어 있으며 C# `NativeErrorMapper`가 동일하게 매핑합니다.
문자열은 UTF-8이며 `amare_string_free`로 해제합니다. panic은 Rust FFI 경계에서 catch됩니다.
`AmareServices`는 multi-thread 호출을 직렬화하지만, destroy와 call을 동시에 호출하면 안 됩니다.

---

## 4. C# Adapter

- `AmareNative`: handle 소유, ABI 버전 검사, `Task.Run`+SemaphoreSlim으로 UI 스레드 차단 방지, dispose.
- `ProfileClient`, `SettingsClient`, `SearchClient`, `SessionClient`, `HistoryClient`, `BookmarkClient`: method/params/result 변환.
- `AddressSuggestionProvider`, `ReaderPageBuilder`, `DownloadPolicy`, `TabManager`: Windows UI와 독립적인 로직.
- `INativeBridge`: 테스트에서 fake bridge로 교체 가능.
- JSON wire naming은 snake_case로 Rust serde와 일치합니다.
- 오류는 `NativeException(NativeError, message, method)`로 통일합니다.

---

## 5. 보안 경계

- WebView2 기본 보안 설정 유지. 인증서 오류 무시 옵션을 설정하지 않음.
- `javascript:`, `data:`, `vbscript:`, `file:`은 navigation block.
- `mailto:`, `tel:`, 사용자 정의 scheme은 사용자 확인 후 `Launcher.LaunchUriAsync`.
- 새 창은 http/https만 내부 새 탭으로 열고, 그 외는 외부 앱 확인.
- 다운로드 파일명/경로를 `DownloadPolicy`로 검증해 downloads 디렉터리 밖으로 나가지 않게 함.
- WebView2 UserDataFolder는 `ProfilePathPolicy`로 프로필 ID를 검증한 뒤 생성.
- 새 탭 페이지는 `NavigateToString` 로컬 HTML이며 외부 서버를 사용하지 않음.
- WebMessage는 새 탭 페이지 nonce와 일치할 때만 처리.

---

## 6. 사이트 권한 (Phase 2.1)

- **event**: `CoreWebView2.PermissionRequested`와
  `CoreWebView2Frame.PermissionRequested`를 사용한다. frame handler는
  `Handled=true` + `State=Deny`로 즉시 거부하고, top-level handler는
  `args.Uri`와 `core.Source`의 HTTPS origin을 exact 비교한다.
- **persistence**: 모든 결정에 `SavesInProfile=false`를 설정한다. WebView2
  profile 저장은 source of truth가 아니며, AMARE per-profile SQLite
  `site_permissions`(schema v6)가 authoritative다.
- **decision**: 저장된 allow/deny를 먼저 적용하고, 없으면 AMARE
  `ContentDialog`에서 `Allow once`/`Always allow`/`Not now`/`Block always`를
  받는다. once/Not now는 저장하지 않고, Always/Block만 revision CAS로
  저장한다.
- **origin**: HTTPS exact scheme/host/port, subdomain 비상속, IDN/punycode,
  default port 정규화. HTTP/data/file/javascript/New Tab은 거부한다.
- **isolation**: profile_id + generation, per-profile DB. iframe은 same/cross
  origin 모두 deny, AI/Reader WebView는 deny-all, popup/new window는 자체
  WebView/프로필 컨텍스트를 유지한다.
- **async lifecycle**: deferral을 잡고 dialog를 띄운다. lookup 후와 dialog
  후 epoch/tab/WebView/profile/generation/origin을 재검증하며, navigation/
  tab close/profile switch/window close/dialog cancel은 deny로 끝난다.
- **UI**: 메뉴 `Site permissions…`와 Settings의 `Manage site permissions…`.
  목록/filter/Allow/Block/Remove/Reset site/Clear all. 저장 실패 시 오류를
  표시하고 목록을 다시 읽으며 성공으로 표시하지 않는다.

상세: [`AMARE_PHASE2_SITE_PERMISSIONS_REPORT.md`](AMARE_PHASE2_SITE_PERMISSIONS_REPORT.md).

## 7. 확인 상태

- Linux에서 Rust service/FFI, C# adapter/core logic, fake bridge, native service round-trip 테스트를 실행했습니다.
- WinUI 3 XAML compiler와 WebView2 runtime은 Linux에서 실행할 수 없습니다.
- Windows Runner/실기기 검증은 별도 상태로 기록하며, 실행하지 않은 항목을 PASS로 표시하지 않습니다.
