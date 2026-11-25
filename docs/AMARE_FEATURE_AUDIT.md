# AMARE Feature Audit — Phase 0

- 문서 상태: **Phase 0 감사 결과 / 승인 대기**
- 작성일: 2026-11-23
- 기준: `master`, 제품/테스트 `49d4962`, 문서 `4ea8c82`
- 최신 제품 버전: **0.92.0** (0.93은 test/fixture only)
- 원칙: **소스 존재 ≠ 검증**. WinUI와 GPUI 결과는 합산하지 않는다.

---

## 1. 판정 규칙

| 상태 | 의미 |
|---|---|
| `IMPLEMENTED_AND_VERIFIED` | 코드가 있고, 실제 실행/자동화 증거가 통과했다 |
| `IMPLEMENTED_BUT_UNVERIFIED` | 코드는 있으나 최신 증거가 실패/미실행/부분이다 |
| `PARTIALLY_IMPLEMENTED` | 일부 경로만 구현되었거나 기능 범위가 제한적이다 |
| `NOT_IMPLEMENTED` | 제품 경로에 구현이 없다 |
| `BLOCKED` | 외부 조건(서명/설치/실기기) 때문에 검증할 수 없다 |
| `REGRESSION` | 이전에 통과한 동작이 최신 기준에서 실패했다 |

증거 등급:

1. **E1**: 실제 Windows GUI/입력 자동화 + 결과 확인
2. **E2**: WebView2/HTTP/probe 수준의 실제 동작 확인
3. **E3**: 단위/통합 테스트 (mock 포함)
4. **E4**: 코드 경로만 존재

`IMPLEMENTED_AND_VERIFIED`는 최소 E2 이상, UI 기능은 E1을 요구한다.
E3만 있는 경우 `IMPLEMENTED_BUT_UNVERIFIED`로 둔다. 단, 계약/정책 테스트가
기능의 본질인 경우(예: checksum 검증)는 예외로 명시한다.

---

## 2. 최신 증거 인벤토리

| 증거 | 결과 |
|---|---|
| WinUI UI smoke | **49/49** (Phase 1.1) |
| Windows 통합 검증 | **143 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** (package 단계 skip) |
| C#↔Rust interop | **76/76** (Phase 1.1) |
| Linux CI | SUCCESS |
| WebView2 local navigation | back/forward/reload/stop/new-window/download PASS |
| WebView2 profile isolation | cookies/localStorage/IndexedDB 격리 PASS |
| 30탭 메모리 | empty 1754MB / content 2074MB / close 후 0MB |
| Windows performance | environment/local-page/tab-create/tab-close 측정 PASS |
| GPUI DirectReload | **10/10** (API + HTTP + source) |
| GPUI Ctrl+R | **10/10** (keybinding + HTTP + source) |
| GPUI 반복 3회 | **11/13**, **12/10**, **13/10** |
| GPUI titlebar | full **3/5**, crash **0/5** |
| GPUI A/B | A reload PASS, B load/reload/close/exit UNVERIFIED |
| 패키지 | 0.92.0 WinUI ZIP/MSIX + GPUI ZIP 유지 |

WinUI UI smoke 48개 항목:

`window_created`, `initial_ui`, `custom_title_bar`, `address_navigation`,
`new_tab`, `tab_switch_shortcut`, `tab_close_shortcut`, `websocket_tab_close`,
`tab_drag_reorder`, `tab_drag_last_to_first`, `tab_drag_first_to_middle`,
`tab_drag_middle_to_last`, `tab_drag_middle_to_first`,
`tab_drag_middle_to_other_middle`, `tab_drag_same_position`, `tab_drag_cancel`,
`tab_drag_after_close`, `certificate_error_self_signed_ui`,
`certificate_error_expired_ui`, `certificate_error_hostname_mismatch_ui`,
`certificate_error_retry`, `certificate_error_back`, `certificate_error_other_tab`,
`find_in_page`, `reader_mode`, `ai_panel`, `settings_dialog`,
`settings_shortcut_actions`, `api_ai_settings_dialog`, `api_ai_no_key`,
`update_check_dialog`, `diagnostics_dialog`, `history_dialog`, `bookmarks_menu`,
`downloads_menu`, `window_resize_maximize`, `new_window_open_close`,
`new_window_button`, `new_window_shortcut`, `last_tab_recovery`,
`webview_focus_shortcuts`, `cross_window_tab_move`, `tear_off_tab`, `tab_churn`,
`multi_tab_stress`, `session_restore`, `session_restore_forced`,
`websocket_browser_close`

---

## 3. 18개 기능 요약

| # | 기능 | WinUI | GPUI | 우선순위 |
|---:|---|---|---|---|
| 1 | 탭 생성/닫기/재정렬/분리/병합 | `IMPLEMENTED_AND_VERIFIED` | `IMPLEMENTED_BUT_UNVERIFIED` | P1 |
| 2 | 주소창과 검색 | `IMPLEMENTED_AND_VERIFIED` | `IMPLEMENTED_BUT_UNVERIFIED` | P1 |
| 3 | 뒤로/앞으로/새로고침/취소 | `IMPLEMENTED_AND_VERIFIED` | `IMPLEMENTED_BUT_UNVERIFIED` | P1 |
| 4 | 북마크 | `IMPLEMENTED_AND_VERIFIED` | `IMPLEMENTED_BUT_UNVERIFIED` | P1 |
| 5 | 방문 기록 | `IMPLEMENTED_AND_VERIFIED` | `IMPLEMENTED_BUT_UNVERIFIED` | P1 |
| 6 | 다운로드 | `IMPLEMENTED_AND_VERIFIED` | `IMPLEMENTED_BUT_UNVERIFIED` | P1 |
| 7 | 세션 복원/충돌 복구 | `IMPLEMENTED_AND_VERIFIED` (멀티윈도우 P0 해결) | `IMPLEMENTED_BUT_UNVERIFIED` | P1 |
| 8 | 비밀번호 저장/자동완성 | `PARTIALLY_IMPLEMENTED` (저장/UI, 자동완성 미구현) | `IMPLEMENTED_BUT_UNVERIFIED` | **P0** |
| 9 | Vault 보안 | `IMPLEMENTED_AND_VERIFIED` (관리 UI/CI) | `IMPLEMENTED_BUT_UNVERIFIED` | **P0** |
| 10 | AI Web Panel | `IMPLEMENTED_AND_VERIFIED` | `NOT_IMPLEMENTED` | P1 |
| 11 | AI API 모드 | `IMPLEMENTED_AND_VERIFIED` | `NOT_IMPLEMENTED` | P1 |
| 12 | Reader | `IMPLEMENTED_AND_VERIFIED` | `NOT_IMPLEMENTED` | P2 |
| 13 | Find in Page | `IMPLEMENTED_AND_VERIFIED` | `NOT_IMPLEMENTED` | P2 |
| 14 | 사이트 권한 | **`NOT_IMPLEMENTED`** | `PARTIALLY_IMPLEMENTED` | P1 |
| 15 | 설정 | `IMPLEMENTED_AND_VERIFIED` | `IMPLEMENTED_BUT_UNVERIFIED` | P1 |
| 16 | 자동 업데이트 | `IMPLEMENTED_BUT_UNVERIFIED` | `NOT_IMPLEMENTED` | P1 |
| 17 | 성능/진단 | `IMPLEMENTED_AND_VERIFIED` | `PARTIALLY_IMPLEMENTED` | P2 |
| 18 | 창 관리/Windows 통합 | `PARTIALLY_IMPLEMENTED` | `IMPLEMENTED_BUT_UNVERIFIED` | P1 |

> WinUI에 `REGRESSION`으로 판정된 항목은 없다. GPUI는 0.92/0.93에서
> 11~13 FAIL이 지속되며, 이는 동결 대상 셸의 미해결 상태다.

---

## 4. 기능별 상세

### 4.1 탭 생성/닫기/재정렬/분리/병합

**WinUI — `IMPLEMENTED_AND_VERIFIED`**

- `TabManager`가 add/activate/move/close/snapshot을 담당한다.
- UI smoke에서 drag reorder 7종, cancel, close 후 drag, cross-window move,
  tear-off, tab churn, multi-tab stress가 모두 PASS했다.
- interop에서 30개 순차 탭과 last-tab replacement가 PASS했다.
- 닫힌 탭은 LIFO로 복원된다 (`ClosedTabHistory`).

**GPUI — `IMPLEMENTED_BUT_UNVERIFIED`**

- 탭 suite가 반복마다 FAIL을 포함했다 (0.93 run 기준 8/1/1, 1/1/0 등).
- 4번째 탭 상태 race가 문서화되어 있다.

**격차/결정**

- WinUI 탭 기능은 재개발하지 않는다.
- 멀티윈도우 **세션**은 별도 P0로 다룬다 (4.7).

---

### 4.2 주소창과 검색

**WinUI — `IMPLEMENTED_AND_VERIFIED`**

- `AddressBarResolver`: URL/검색/빈 입력 판정.
- `AddressSuggestionProvider`: 북마크/히스토리/열린 탭/검색 제안.
- `SearchEngineCatalog`: Google/Naver 등 쿼리 인코딩, 한글 `%ED%95%9C%EA%B8%80` 테스트 PASS.
- UI smoke `address_navigation`, `webview_focus_shortcuts` PASS.
- interop `address_bar_url_vs_search`, `windows_0_15_address_suggestions` PASS.

**GPUI — `IMPLEMENTED_BUT_UNVERIFIED`**

- navigation suite가 반복마다 6~7 FAIL을 포함했다.
- 실제 key 입력/포커스 경로는 별도 probe가 필요하다.

**격차**

- 한국어 IME 조합/Enter는 CI가 아니라 Phase 3 실기기 항목이다.
- 주소창 자동완성의 실제 키보드 선택 동작은 추가 feature test가 필요하다.

---

### 4.3 뒤로/앞으로/새로고침/탐색 취소

**WinUI — `IMPLEMENTED_AND_VERIFIED`**

- `webview2:webview2_local_navigation`이 title/url/back/forward/reload/stop/
  new-window/download를 실제 WebView2에서 PASS했다.
- HTTPS navigation과 certificate error 3종도 PASS했다.

**GPUI — `IMPLEMENTED_BUT_UNVERIFIED`**

- **reload는 예외적으로 강한 증거가 있다**: DirectReload 10/10, Ctrl+R 10/10,
  HTTP request counter +1, source 유지.
- back/forward/stop은 navigation suite FAIL과 함께 있어 전체 기능은 미검증이다.

**격차**

- WinUI reload/stop은 이미 검증되었으므로 재개발하지 않는다.
- GPUI reload 증거는 참고 자료로 보존하되, WinUI 결과와 합산하지 않는다.

---

### 4.4 북마크

**WinUI — `IMPLEMENTED_AND_VERIFIED`**

- `BookmarkClient` + Rust `bookmark.*` FFI.
- interop `native_services_bookmarks_and_history` PASS.
- UI smoke `bookmarks_menu` PASS.
- 북마크 버튼 토글/캐시 갱신 경로가 있다.

**GPUI — `IMPLEMENTED_BUT_UNVERIFIED`**

- library suite 8/1/0 (반복에 따라 변동).
- 패널/삭제/토글은 구현되어 있으나 최신 반복에서 FAIL이 있다.

**격차**

- 폴더 계층/검색 UI는 WinUI에 최소 수준이다.
- 개인용 완료 기준에는 “유지”만 필요하므로 P1 범위에서 안정성만 확인한다.

---

### 4.5 방문 기록

**WinUI — `IMPLEMENTED_AND_VERIFIED`**

- `HistoryClient` + `history.record/list/clear/top`.
- interop round-trip PASS, `history_dialog` UI smoke PASS.
- 실제 방문 기록은 http/https만 기록한다.

**GPUI — `IMPLEMENTED_BUT_UNVERIFIED`**

- library/history 패널은 구현되어 있으나 최신 suite FAIL이 있다.

**격차**

- 기록 검색/FTS는 Rust storage에서 PASS했지만, WinUI UI는 목록/삭제 중심이다.
- 장기 보존/대용량 정리는 P2.

---

### 4.6 다운로드

**WinUI — `IMPLEMENTED_AND_VERIFIED`**

- `CoreWebView2.DownloadStarting`에서 경로 할당, 진행/완료/중단 상태 기록.
- `DownloadPolicy`가 Windows 금지 문자/중복 이름을 처리한다.
- interop `download_sanitizes_windows_separators`,
  `windows_0_18_download_checksum_and_cancel` PASS.
- WebView2 probe의 download PASS.
- UI smoke `downloads_menu` PASS.

**GPUI — `IMPLEMENTED_BUT_UNVERIFIED`**

- download suite 1/1/0 (FAIL 포함).
- cancel/remove/DB 저장 경로는 구현되어 있다.

**격차**

- WinUI 다운로드 이력은 `downloads.json`이며 SQLite `downloads` 테이블을 쓰지 않는다.
- WinUI UI에는 취소/재시도/목록 삭제가 없다. P2에서 정리한다.

---

### 4.7 세션 복원 및 충돌 복구 — **P0 해결**

**WinUI — `IMPLEMENTED_AND_VERIFIED` (CI/데이터 계층)**

Phase 1.1에서 last-writer-wins P0를 수정했다.

- schema v5: `session_windows` + `session_tabs.window_id`
- `session.save_window`, `session.latest_windows`, `session.delete_window`
- 창별 탭/active/ordinal/geometry 저장·복원
- 기존 `session.save`/`session.latest`는 `main` 창으로 하위 호환
- 앱 시작 시 전체 창을 ordinal 순서로 복원
- 마지막 창은 스냅샷 유지, 다른 창이 남아 있으면 5초 grace 후 제거
- 저장 실패 시 기존 정상 데이터 보존

검증:

- Rust `session_windows` **10/10 PASS**
  - 2창 3/2탭, 저장 순서 반전, 3창 6가지 순서, 한 창 삭제, 반복 저장,
    비정상 종료 reopen, v4 migration, 동시 writer, 실패 rollback
- C# interop **76/76 PASS** (신규 6개 포함)
- WinUI UI smoke **49/49 PASS**
  - `session_multiwindow_restore: restored_windows=2 tabs=[2,3]`
  - 기존 `session_restore`, `session_restore_forced` 유지
- Windows 통합 **143 PASS / 0 FAIL**
- migration dry-run: 원본 SHA-256 불변, 복사본 `integrity_check=ok`

남은 제약:

- 실제 Windows 11 실기기 2·3창 GUI/IME/DPI 검증은 **UNVERIFIED**
- 3창은 Rust 데이터 계층에서 검증, 3창 UI smoke는 없음
- 단일 창 종료 후 재시작은 Rust reopen 테스트로 검증, UI assertion은 없음

**GPUI — `IMPLEMENTED_BUT_UNVERIFIED`**

- session suite 2/1/0, corrupt matrix 14/14 PASS.
- A/B/close/exit는 UNVERIFIED. 동결 유지.

상세: `docs/AMARE_PHASE1_SESSION_REPORT.md`,
`docs/SESSION_SCHEMA_DESIGN.md`.

---

### 4.8 비밀번호 저장 및 자동완성 — **P0**

**WinUI — `NOT_IMPLEMENTED`**

- WinUI에서 `PasswordVault`는 `AiCredentialStore`의 **AI API 키 1개**에만 쓰인다.
- 브라우저 로그인 폼 저장/자동완성/목록/삭제 UI가 없다.
- WebView2 설정을 명시하지 않아 `IsGeneralAutofillEnabled` 기본값 **true**가
  적용될 수 있고, 기존에 저장된 password 데이터는 autofill될 수 있다.
- `WebMessageReceived`는 새 탭 검색에서 nonce만 확인하며 `args.Source` origin
  검증을 하지 않는다. Vault 메시지에는 source 검증이 필수다.

**Phase 1.2 감사 결과 (WinUI 구현 전)**

- 권장: Rust `amare_security` XChaCha20-Poly1305 + `KeyFile` +
  `WindowsDpapiKeyProtector` 재사용.
- DB schema 변경 없이 기존 `secrets` 테이블 1행에 Vault blob 저장 가능.
- key file은 DB 밖 `<root>\vault\<profile>.key`, DPAPI user scope.
- 자동완성은 exact origin + HTTPS + top-level + explicit Fill + no auto-submit.
- Phase 1.3A에서 Rust Vault Core를 구현/검증했다:
  - `VaultService` initialize/unlock/lock/list/get/create/update/delete/export/import
  - XChaCha20-Poly1305, profile AAD, strict format 1, CAS, key-file lock
  - Linux Vault **23/23**, Windows **24/24**, `windows_dpapi_vault_roundtrip` PASS
  - FFI **12/12**, C# interop **79/79**, Windows **146/0**
- Phase 1.3B에서 FFI ABI 2 + C# `AmareVaultClient`를 구현/검증했다:
  - ABI1 유지, ABI2 별도 symbol/handle
  - Windows CI 실제 Rust DLL + C# CRUD/backup/conflict PASS
  - Rust FFI 12/12, C# interop 79/79
- Phase 1.3C에서 WinUI Vault UI(lifecycle, CRUD, reveal/edit, backup, idle/session
  lock 코드, clipboard)를 구현하고 잔여 lock 경계를 UI로 검증했다.
  UI smoke **54/54**, Windows **147/0**.
- 아직 없음: WebView2 자동완성/로그인 감지, 실제 5분 idle·session lock,
  Windows 11 실기기(IME/DPI/접근성) 검증.
- 상세: `docs/VAULT_SECURITY_DESIGN.md`, `docs/VAULT_THREAT_MODEL.md`,
  `docs/VAULT_TEST_PLAN.md`, `docs/AMARE_PHASE1_VAULT_AUDIT.md`,
  `docs/AMARE_PHASE1_VAULT_CORE_REPORT.md`,
  `docs/AMARE_PHASE1_VAULT_FFI_REPORT.md`.

**GPUI — `IMPLEMENTED_BUT_UNVERIFIED`**

- `password_vault.rs`: XChaCha20-Poly1305, DPAPI 보호 키 파일, exact origin,
  revision/tombstone/merge, 전역 lock, atomic save.
- 0.93 password **2 PASS / 1 FAIL** (초기 normal page timeout), vault multiwindow
  **1 PASS / 1 FAIL** (unlock timeout). 0.81~0.83에서도 password 2/1, vault 1/1.
- 알려진 결함: format version 미검증, idle auto-lock 없음, 전역 Vault(프로필
  격리 없음), backup key 제외, fill의 form action/visibility 검증 부족.
- GPUI 코드를 그대로 복사하지 않고 Rust 보안 로직만 참고한다.

**판정**

- 개인용 완료 기준의 “비밀번호 저장과 자동완성의 보안 검증”은 아직 완전히
  충족되지 않는다. 저장/관리 UI는 Phase 1.3C에서 구현했지만 자동완성은
  Phase 1.3D 범위다.
- Phase 1.3C UI smoke 54/54, Windows 147/0을 유지한다. profile switch와
  multiwindow lock은 실제 WinUI로 PASS했고, idle override는 5분을 넘지 못한다.
  실제 5분 idle과 session lock은 UNVERIFIED다.

---

### 4.9 Vault 보안 — **P0**

**WinUI — `IMPLEMENTED_AND_VERIFIED` (Vault UI + WebView2 capture/autofill, CI)**

- Rust `amare-security`의 키 유도/암호화/서명과 `secrets` 테이블은 존재하고
  storage 테스트에서 PASS했다.
- `KeyFile` + `WindowsDpapiKeyProtector`의 DPAPI user-scope 테스트가 Windows
  CI에서 PASS했다 (`platform_key_tests`).
- Phase 1.3A Rust `VaultService` + Phase 1.3B ABI2 `vault.*`/C# `AmareVaultClient` +
  Phase 1.3C WinUI Vault UI가 Windows CI에서 PASS했다.
- Phase 1.3D에서 WebView2 login-form metadata capture와 explicit autofill을
  추가했다. Capture는 저장 승인 후에만 password를 읽고, fill은 사용자 menu
  선택에서만 동작하며 자동 submit/Enter/click이 없다.
- 최종 UI smoke **64/64**: init/lock/unlock, CRUD, reveal/edit,
  backup export/import/validate, profile switch isolation, multiwindow
  lock/unlock, idle timeout(30s 단축), capture approval/rejection,
  explicit fill(no submit), forged/iframe message rejection, locked capture.
- `webview2_autofill_policy` runtime probe PASS: 같은 profile의 두 WebView2에서
  password autosave/general autofill read-back false.
- `VaultService::status`가 영속 Vault를 감지하도록 수정해, background client
  재생성 후에도 기존 Vault를 `Locked`로 표시한다 (회귀 테스트 포함).
- 아직 없음/UNVERIFIED: 실제 5분 idle, Windows session lock, Windows 11
  실기기(IME/DPI/접근성), 기존 WebView2 저장 데이터 삭제 실행, 강제 종료 경합.
- Phase 1.3C.1에서 New Tab의 `NavigateToString` `data:` navigation 결함을
  수정했다. 1회성·정확한 payload provenance로 내부 문서만 허용하고, 일반
  `data:`/`javascript:`/`file:` 차단은 그대로다. UI 60/60, Windows 151/0,
  interop 84/84.
- AI 키는 Windows Credential Manager에 저장된다.

**GPUI — `IMPLEMENTED_BUT_UNVERIFIED`**

- Password vault가 사실상 Vault 역할을 한다.
- Windows Hello는 없고 DPAPI만 사용한다.
- Windows 계정/프로필 손실 복구 정책이 없다.

**판정**

- “Vault 백엔드”와 “사용자 Vault 기능”을 분리해야 한다.
- 백엔드는 재사용 가능, 제품 기능은 미완성이다.
- Phase 1.2는 **감사/설계 PASS**, 구현은 별도 승인 후 진행한다.

### 4.10 AI Web Panel

**WinUI — `IMPLEMENTED_AND_VERIFIED`**

- `AiWebView` + 별도 `ai-webview2` 환경.
- AI 서비스 선택, 열기/닫기, 상태 텍스트, 페이지 컨텍스트 복사.
- UI smoke `ai_panel` PASS.
- `NewWindowRequested`는 새 창 대신 패널 내 navigation으로 제한한다.

**GPUI — `NOT_IMPLEMENTED`**

**격차**

- 실제 외부 AI 서비스의 로그인/렌더링/쿠키 동작은 CI에서 검증되지 않았다.
- 메인 브라우징 프로필과의 origin/데이터 격리는 폴더 분리 수준이며,
  실제 서비스 세션 격리는 Phase 2에서 확인해야 한다.

---

### 4.11 AI API 모드

**WinUI — `IMPLEMENTED_AND_VERIFIED` (계약/모의)**

- `AiApiClient`: 요청 구성, 응답 파싱, SSE, cancel.
- interop: 401/429/network 오류, redaction, provider 선택, cancel PASS.
- `api_ai_settings_dialog`, `api_ai_no_key` UI smoke PASS.
- API 키는 Windows Credential Manager에만 저장하고 로그/설정 파일에 쓰지 않는다.

**GPUI — `NOT_IMPLEMENTED`**

**격차**

- 실제 OpenAI/제공자 API 호출은 키가 없어 CI에서 UNVERIFIED다.
- 개인용 완료 전 1회 실제 호출과 오류/취소/키 삭제를 실기기에서 확인해야 한다.

---

### 4.12 Reader

**WinUI — `IMPLEMENTED_AND_VERIFIED`**

- `ReaderExtractionScript` + `ReaderPageBuilder` + Reader overlay WebView2.
- 폰트/테마/크기/너비 조절.
- UI smoke `reader_mode` PASS.
- interop `windows_0_15_reader_page_builder`, `reader_safe_image_urls` PASS.

**GPUI — `NOT_IMPLEMENTED`**

**격차**

- 복잡한 실제 사이트(SPA, lazy image, paywall)에서의 추출 품질은 별도 확인이 필요하다.
- P2 품질 항목으로 둔다.

---

### 4.13 Find in Page

**WinUI — `IMPLEMENTED_AND_VERIFIED`**

- `FindBar`, 다음/이전/닫기, 현재/총 개수 표시.
- UI smoke `find_in_page` PASS.

**GPUI — `NOT_IMPLEMENTED`**

**격차**

- IME 입력 중 Find 동작은 Phase 3에서 확인한다.

---

### 4.14 사이트 권한

**WinUI — `NOT_IMPLEMENTED`**

- `CoreWebView2.PermissionRequested` 처리 코드가 없다.
- 카메라/마이크/위치/알림/클립보드에 대한 AMARE 자체 저장/조회/초기화 UI가 없다.
- WebView2 기본 프롬프트가 그대로 노출될 수 있으나, 제품 기능으로 검증되지 않았다.

**GPUI — `PARTIALLY_IMPLEMENTED`**

- 설정에 “사이트 권한” 카테고리가 있으나 read-only 안내 문구뿐이다.
- 카메라/마이크는 WebView2 기본 프롬프트, 알림/위치는 UNVERIFIED로 표시한다.
- 쿠키/사이트 데이터 삭제는 BLOCKED/read-only다.

**Phase 2 조치**

- origin별 decision 저장, 허용/차단/초기화 UI, 로그/상태 노출.
- 절대 자동 허용하지 않는다.

---

### 4.15 설정

**WinUI — `IMPLEMENTED_AND_VERIFIED`**

- 테마(system/light/dark), 검색 엔진, 세션 복원, 업데이트 확인.
- UI smoke `settings_dialog`, `settings_shortcut_actions` PASS.
- Rust `settings.*` + profile별 저장, atomic validation 테스트 PASS.

**GPUI — `IMPLEMENTED_BUT_UNVERIFIED`**

- 7개 카테고리, 검색, 밀도, 다운로드 경로, 폰트 등 더 넓다.
- settings suite 33/0/1.

**격차**

- WinUI 설정은 최소 수준이다. 다운로드 경로/홈/개인정보/권한/언어 항목이 없다.
- 한국어 UI는 Phase 2/3에서 다룬다.

---

### 4.16 자동 업데이트

**WinUI — `IMPLEMENTED_BUT_UNVERIFIED`**

- `GitHubReleaseClient`, stable/pre-release 선택, SHA-256 필수.
- `PortableUpdateEngine`: apply, rollback, health marker, crash recovery.
- interop: apply preserves data, rollback, launch failure rollback, manifest/version/
  traversal 거부, concurrent helper 거부, health timeout, corrupt state PASS.
- UI smoke `update_check_dialog` PASS.
- **실제 Windows installer/설치 성공 경로는 없다.**
- `webview2:windows_installer_update_flow`는 UNVERIFIED다.
- MSIX는 unsigned라 설치가 BLOCKED다.

**GPUI — `NOT_IMPLEMENTED`**

**판정**

- “확인/다운로드/적용 엔진”은 강하지만, “설치/복구 end-to-end”는 아직 아니다.
- Portable ZIP 업데이트를 1차 경로로 확정하고 검증해야 한다.

---

### 4.17 성능 및 진단

**WinUI — `IMPLEMENTED_AND_VERIFIED` (CI 범위)**

- `webview2:windows_browser_performance` PASS.
- `webview2:memory_30_tabs`: empty 1754MB, content 2074MB, background 2078MB,
  suspend 2062MB, close 0MB, webview/browser 프로세스 수 기록.
- `Diagnostics` 다이얼로그, 앱 로그, startup error 로그, probe JSON.
- ETW/handle forensics 빌드/자체 테스트 PASS.

**GPUI — `PARTIALLY_IMPLEMENTED`**

- status JSON, titlebar/lifecycle 로그, panic hook이 있다.
- 장시간/실기기 성능은 검증되지 않았다.

**격차**

- 30탭 2GB 수준은 개인용으로 허용 가능하지만, 장시간 누수/복원 비용은
  Phase 3에서 측정해야 한다.

---

### 4.18 창 관리 및 Windows 통합

**WinUI — `PARTIALLY_IMPLEMENTED`**

검증된 부분:

- custom title bar, 창 resize/maximize.
- 새 창 open/close (버튼/단축키), cross-window tab move, tear-off.
- UI smoke `new_window_*`, `cross_window_tab_move`, `tear_off_tab` PASS.
- 다중 창은 같은 프로세스의 `MainWindow` registry로 관리된다.

미검증/미구현:

- 실제 Windows 11 DPI 125/150%, 멀티모니터 이동.
- Snap Layouts, Alt+Tab 동작, 작업 표시줄 그룹화.
- 한국어 IME, 포커스 전환.
- 기본 브라우저 등록.
- 창별 세션 복원 (4.7 P0).
- 장시간 다중 창 메모리/핸들.

**GPUI — `IMPLEMENTED_BUT_UNVERIFIED`**

- titlebar full 3/5, crash 0/5, A/B/close/exit UNVERIFIED.
- 다중 창 자체는 구현되어 있으나 신뢰 가능한 PASS 기준에 미달한다.

**판정**

- WinUI의 “멀티윈도우 기본 동작”은 검증되었다.
- “Windows 11 실사용 통합”은 Phase 3 게이트다.

---

## 5. P0 / P1 / P2 결함 및 격차

### P0 — 개인용 완료를 막는 항목

| ID | 항목 | 상태 | 근거/조치 |
|---|---|---|---|
| P0-1 | 멀티윈도우 세션 last-writer-wins | **RESOLVED (Phase 1.1)** | schema v5 + window API, Rust 10/10·UI 49/49·interop 76/76 |
| P0-2 | WinUI 비밀번호 관리자/Vault 부재 | **OPEN — capture/autofill 구현·CI 검증, 실기기/5분 idle/session lock UNVERIFIED (1.3D)** | Windows 11 실기기와 실제 session lock 검증 필요 |
| P0-3 | 실제 Windows 11 핵심 입력 미검증 | **OPEN** | CI는 Windows Server일 수 있음, IME/DPI/Snap/Alt+Tab UNVERIFIED |

### P1 — 핵심 기능/데이터/배포

| ID | 항목 | 근거 | 조치 |
|---|---|---|---|
| P1-1 | 사이트 권한 UI 없음 | `PermissionRequested` 처리 없음 | Phase 2 구현 |
| P1-2 | AI 패널 실제 서비스/세션 격리 미검증 | UI smoke는 패널 open만 확인 | 실제 서비스 로그인/쿠키/격리 확인 |
| P1-3 | 업데이트 설치 end-to-end 없음 | installer flow UNVERIFIED, MSIX BLOCKED | Portable ZIP 업데이트 E2E |
| P1-4 | WinUI/GPUI 데이터 루트 불일치 | `%LOCALAPPDATA%` vs `%APPDATA%` | 자동 공유 금지, 필요 시 import |
| P1-5 | WinUI 멀티윈도우 독립 navigation 미검증 | 0.93 A/B는 GPUI 대상 | WinUI A/B fixture test 추가 |
| P1-6 | GPUI 11~13 FAIL | 3회 반복 | 동결 대상, 결과만 보존 |
| P1-7 | First-click 6/6 최신 재실행 없음 | 0.89 결과만 유지 | WinUI first-click 재실행 |
| P1-8 | Backup/Restore UI 없음 | Rust backup 테스트만 PASS | 사용자 백업/복구 UI 검토 |
| P1-9 | 기본 브라우저 등록 없음 | 코드 없음 | Phase 3 |
| P1-10 | 한국어 UI/IME | UI 영어 중심, IME UNVERIFIED | Phase 2/3 |
| P1-11 | WebView2 내장 autofill 데이터 | `IsGeneralAutofillEnabled` 기본 true, 기존 password autofill 가능 | 명시적 비활성 + 동의 후 clear |
| P1-12 | Vault 백업/복구 키 정책 부재 | DB backup에 ciphertext만 포함, key file 제외 | Master Password export 또는 key 포함 백업 |
| P1-13 | Windows 계정/DPAPI 손실 복구 | DPAPI-only는 복구 불가 | 복구 정책/UX 승인 |

### P2 — 품질/자동화

| ID | 항목 | 근거 | 조치 |
|---|---|---|---|
| P2-1 | 다운로드 이력 JSON/취소 UI 없음 | `downloads.json`, 메뉴 open만 | SQLite + cancel/remove UI |
| P2-2 | 설정 범위 축소 | theme/engine/restore/update만 | 카테고리 확장 |
| P2-3 | UIA/titlebar 자동화 불안정 | WinUI 0.83 UIA race 이력, GPUI titlebar 2/5 cancel | 재시도/격리, FAIL과 환경 실패 분리 |
| P2-4 | MSIX 서명 없음 | unsigned | 서명 결정 |
| P2-5 | Reader 복잡 사이트 품질 | 단순 페이지 위주 | 실제 사이트 표본 |
| P2-6 | 30탭 2GB 수준 | memory probe | 장시간/누수 측정 |
| P2-7 | GPUI 패키지/테스트 유지보수 | 동결 | 동결 정책 준수 |
| P2-8 | GPUI Vault format version 무검증 | `normalise()`가 format 2로 강제 | WinUI에서 정확한 version 거부 |

---

## 6. 재사용 가능한 기존 코드

### 그대로 재사용

- `AmareNative` / `INativeBridge` / FFI ABI v1
- `ProfileClient`, `SettingsClient`, `SearchClient`, `SessionClient`,
  `BookmarkClient`, `HistoryClient`
- `TabManager`, `ClosedTabHistory`, `SessionRestorePlanner`
- `AddressBarResolver`, `AddressSuggestionProvider`, `SearchEngineCatalog`
- `NavigationPolicy`, `BrowserShortcutPolicy`, `KeyboardShortcutRouter`
- `DownloadPolicy`, `ProfilePathPolicy`, `ProfileDataCleanup`
- `ReaderExtractionScript`, `ReaderPageBuilder`
- `AiApiClient`, `AiCredentialStore`
- `UpdateService`, `PortableUpdateEngine`, `PortableUpdatePackage`,
  `PortableUpdateState`, `PortableUpdateLauncher`
- Rust `amare-store` (schema/backup/FTS/maintenance), `amare-security`,
  `amare-update`, `amare-services`

### 참고 후 재설계

- GPUI `SessionWindowSnapshot` 모델 → WinUI 창 단위 세션
- GPUI `password_vault.rs`의 키 보호/전역 lock/tombstone/merge 설계
- GPUI `SettingsCategory`/한국어 문자열 → WinUI 설정/로컬라이제이션
- GPUI download manager → WinUI 다운로드 UI

### 이관하지 않음

- GPUI custom rendering/input/text_input 계층
- GPUI 전용 UIA/status probe
- GPUI library/workspaces/tasks (현재 개인용 브라우저 요구 범위 밖)
- GPUI 패키지/기본 전환

---

## 7. GPUI에서 가져올 가치가 있는 기능

| 순위 | 기능 | 가치 | 이관 방식 |
|---:|---|---|---|
| 1 | 창 단위 세션 스냅샷 | 데이터 손실 방지, 멀티윈도우 복원 | 모델/알고리즘만 참고해 WinUI/Rust schema 설계 |
| 2 | Password vault | P0 보안 요구 | Rust 암호화/키 보호 로직 재사용, UI는 WinUI |
| 3 | 다운로드 관리 | 취소/삭제/이력 | SQLite `downloads` + WinUI 목록 UI |
| 4 | 설정 카테고리/검색 | 사용성 | WinUI 설정 다이얼로그 확장 |
| 5 | 한국어 UI 문자열 | 개인용 사용성 | WinUI 리소스化 |
| 6 | 라이브러리/워크스페이스 | 장기 자료 관리 | **보류** (요구 범위 밖) |

---

## 8. 감사 결론

- WinUI는 이미 **브라우저 핵심 기능의 대부분을 CI 수준에서 검증**했다.
- 따라서 “WinUI로 다시 만들기”가 아니라 **검증된 것을 유지하고 결손만 메우는**
  전략이 맞다.
- 개인용 완료를 막는 진짜 결손은 다음 두 가지다.
  1. 비밀번호 관리자/Vault (Rust Core + ABI2/C# 완료, WinUI UI/자동완성 미구현)
  2. 실제 Windows 11 실기기 입력/통합 검증
- 멀티윈도우 세션 데이터 모델(P0-1)은 Phase 1.1에서 해결·검증했다.
  상세: `docs/AMARE_PHASE1_SESSION_REPORT.md`.
- GPUI는 기능적으로 일부 앞선 부분(창 세션, vault)이 있으나, 최신 반복에서
  11~13 FAIL이고 기본 전환 조건을 충족하지 못했다. **동결·보존**한다.
