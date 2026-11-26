# AMARE Phase 2.4 — Password Vault WebView2 Integration & Security 보고서

- 상태: **PASS (WinUI/WebView2 security tests)**. 실제 Windows 11 실기기/실제 OS lock은 UNVERIFIED.
- 작업 branch: `feature/phase-2-vault-webview-integration`
- 기준 브랜치: `feature/phase-2-settings-localization`
- 기준 제품/테스트 HEAD: **`e548a1f`**
- 기준 문서 HEAD: **`1030e75`**
- 최종 제품/테스트 HEAD: **`e1b9cbf`**
- 문서 HEAD: 이 보고서를 포함한 문서 commit
- Working tree: **clean** (문서 commit 후)
- DB schema 변경: **없음** (v6 유지)
- 패키지/MSIX/코드 서명/공개 Release: **없음**

---

## 1. 최종 요약

| 완료 기준 | 판정 | 근거 |
|---|---|---|
| HTTPS top-level login 저장 후보 감지 | **PASS** | 기존 Phase 1.3D + Phase 2.4 UI tests |
| 사용자 승인 후 Vault 저장 | **PASS** | Save dialog, approval-before-read, entry count |
| 사용자 거부 시 저장 없음 | **PASS** | `vault_capture_requires_approval` |
| `login_save_prompt_enabled` OFF 차단 | **PASS** | `vault_capture_settings_off` |
| Vault locked capture 차단 | **PASS** | `vault_capture_locked_rejected` |
| exact-origin explicit Fill | **PASS** | `vault_fill_explicit_and_no_submit` |
| 다른 origin / hidden field Fill 거부 | **PASS** | `vault_fill_other_origin_rejected`, `vault_fill_hidden_field_rejected` |
| `login_autofill_enabled` OFF 차단 | **PASS** | `vault_fill_settings_off` |
| Vault lock epoch / async revalidation | **PASS (code/UI)** | lock epoch + settings rechecks |
| cross-origin iframe/action 차단 | **PASS** | forged/iframe/cross-action/cross-frame UI tests |
| 자동 제출/클릭/Enter 없음 | **PASS** | script + page status/URL assertions |
| AI/Reader Vault 경로 없음 | **PASS (code)** | Vault bridge는 일반 tab에만 attach |
| existing New Tab / Site Permissions / AI 회귀 | **PASS** | UI 84/84, interop 113/113 |
| Windows/Linux CI | **PASS (기록)** | Windows 182/0/8/8, UI 84/84, interop 113/113, Linux SUCCESS |
| 실제 Windows 11 실기기 | **UNVERIFIED** | CI는 Windows Server 2025 |

**P0 보안 결함은 발견되지 않았다.** 실제 Windows 11 hardware/session lock/
force-kill race는 UNVERIFIED다.

---

## 2. Git 기준 / branch

```text
feature/phase-2-settings-localization
  e548a1f  Keep Settings UI consistent when app-wide saves fail
  1030e75  Document Phase 2.3 settings audit and localization

feature/phase-2-vault-webview-integration (신규)
  e1b9cbf  Make Vault fill security tests behavior-based
  8396819  Add Vault WebView settings/lock diagnostics and security tests
  0ba49bc  Harden Vault WebView settings, lock epoch and add security tests
```

- `git merge-base --is-ancestor e548a1f HEAD`로 기준 제품/테스트 HEAD 포함을
  확인했다.
- 기존 Phase 1.3D branch `feature/phase-1-vault-webview` (`c0138ab`)와
  `feature/phase-1-vault-webview-v2` (`75c5a5f`)는 보존했다.
- Phase 1.3D 구현을 최신 코드와 비교해 **필요한 부분만** hardening했다.
- GPUI, Vault 암호화/DPAPI/backup/import, DB schema, New Tab provenance,
  Site Permissions 보안 정책, AI WebView 격리는 변경하지 않았다.
- force push/history rewrite를 하지 않았다.

---

## 3. 기존 Phase 1.3D와 Phase 2.4의 관계

Phase 1.3D는 이미 다음을 구현/검증했다.

- top-level WebMessage + per-document token/generation + exact URL/origin
- approval-before-read capture
- explicit Fill, no auto-submit/Enter/click
- exact-origin entry, visible/enabled/form-associated field checks
- built-in WebView2 autofill disable
- forged top-level/iframe rejection
- Vault locked capture rejection
- UI 64/64, interop 99/99, Windows 167/0

Phase 2.4는 이를 다시 구현하지 않고 다음을 보완했다.

1. Phase 2.3의 `login_save_prompt_enabled`/`login_autofill_enabled`를 실제
   capture/fill 경로에서 **async 중간 지점마다 재검사**.
2. Vault `lock epoch`를 추가해 dialog/detect/list/secret retrieval 중 lock이
   발생하면 in-flight 작업을 무효화.
3. 저장/입력 직전 context/settings/lock을 재검증.
4. cross-origin action, cross-origin iframe login, other-origin fill, hidden/
   disabled field, duplicate account, settings OFF, locked Fill UI tests 추가.
5. 기존 status 기반 flaky assertion을 **관찰 가능한 동작**(dialog 부재, page
   field 값, Vault entry count) 기준으로 보강.

---

## 4. 저장 제안 (login capture)

### 4.1 흐름

```text
top-level HTTPS login form submit
  → content script: form metadata만 전송
      type/version/generation/document token/msgId
      origin/actionOrigin/formToken/username/ambiguous
      (password 없음)
  → native strict schema + source + token + generation + exact origin/action
  → Ready/Vault context/profile/frame 확인
  → Vault status (Unlocked) 확인
  → Save password? dialog (기본 Not now)
  → 사용자가 Save 선택
  → context/profile/generation/origin/action/Vault lock 재검증
  → native `amare_read_secret` command (one-use opId)
  → script: form field가 여전히 visible/enabled/form-associated인지 확인 후
    username/password 반환
  → native strict schema, username 일치, 길이/제어문자, context 재검증
  → exact origin+username duplicate 확인
  → `vault.create(canonical origin, username, password, title)`
  → secret.Clear() (best-effort)
```

- **password는 Save 승인 전 native로 전송되지 않는다.**
- 사용자가 Not now/닫기/거부하면 저장하지 않는다.
- form action origin이 document origin과 exact 일치하지 않으면 후보를 만들지
  않는다.
- cross-origin iframe은 content script가 `window.top !== window`에서 즉시
  종료하고, native는 frame WebMessage를 구독하지 않는다.
- 저장 직전 Vault status가 Locked이면 저장하지 않는다.
- `login_save_prompt_enabled=false`이면 candidate 처리 자체를 중단하고
  dialog를 표시하지 않는다.
- 저장 중 `login_save_prompt_enabled`가 OFF로 바뀌면 최종 저장을 중단한다.

### 4.2 중복 정책

- 동일 **exact origin + exact username**이 이미 있으면 새로 저장하지 않고
  기존 entry를 덮어쓰지 않는다.
- 같은 origin의 다른 username은 별도 entry로 저장한다.
- 같은 username의 다른 origin은 별도 entry다.
- update/overwrite UI는 이번 범위에 포함하지 않았다. 기존 credential을
  자동으로 덮어쓰지 않는 것이 기본 정책이다.

---

## 5. 사용자 요청 기반 Fill

### 5.1 흐름

```text
사용자가 메뉴 “Fill password for this site…” 선택
  → login_autofill_enabled 확인 (OFF면 즉시 중단)
  → active tab/WebView/profile/Vault context 확인
  → Vault Unlocked 확인 + vault lock epoch 기록
  → native `amare_detect` (one-use opId)
  → script: top-level, HTTPS, exact form action, single visible form,
    visible/enabled/non-readonly/form-associated password only
  → native strict schema/origin/generation/token/opId
  → exact-origin Vault entries만 list (자동 선택 없음)
  → 사용자가 account 선택 + Fill
  → context/profile/generation/origin/lock/settings 재검증
  → `vault.get_for_fill(context, exact origin, entryId)`
  → entry origin exact 재검증
  → native `amare_fill` (one-use opId, formToken, username, password)
  → script: field reference/form/visibility/action 재검증
  → input/change event만 발생
  → 결과 표시: “Password filled. Press Sign in when you are ready.”
```

- 페이지 로드/포커스/입력만으로 자동 Fill하지 않는다.
- `login_autofill_enabled=false`이면 메뉴 Fill도 거부한다.
- subdomain/다른 port/다른 scheme은 exact match 실패로 목록에 나오지 않는다.
- hidden/disabled/readonly/ambiguous field는 script에서 거부한다.
- `form.submit()`, `requestSubmit()`, Enter, click, 자동 제출을 실행하지 않는다.
- Fill 결과는 **필드에 값이 전달되었는지**만 나타내며 로그인 성공으로
  표현하지 않는다.

---

## 6. WebView2 보안 경계

사용한 공개 API:

- `CoreWebView2.WebMessageReceived` — top-level 메시지만 수신
- `CoreWebView2WebMessageReceivedEventArgs.Source` / `WebMessageAsJson`
- `CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync`
- `CoreWebView2.PostWebMessageAsJson`
- `CoreWebView2.ExecuteScriptAsync` (Fill)
- `CoreWebView2.NavigationStarting` / `NavigationCompleted`
- `CoreWebView2Settings/Profile.IsPasswordAutosaveEnabled`
- `CoreWebView2Settings/Profile.IsGeneralAutofillEnabled`
- `CoreWebView2.Profile.ClearBrowsingDataAsync` (사용자 확인 후 기존 WebView2
  autofill 데이터 삭제; 버튼은 기존 Phase 2.3 Settings에 연결)

사용하지 않은 것:

- undocumented/private API
- frame WebMessage를 top-level로 승격하는 우회
- 임의 credential 조회 Web API
- 자동 submit/click/Enter

경계:

- content script는 `window.top !== window`에서 종료한다.
- `CoreWebView2Frame.WebMessageReceived`는 Vault에 사용하지 않는다.
- top-level `WebMessageReceived` + exact `Source` + native token/generation +
  exact document URL/origin + one-use opId + msgId replay 방지를 모두
  통과해야 한다.
- AI/Reader WebView에는 `ConfigureWebViewVaultAsync`를 호출하지 않으므로
  Vault bridge/secret 경로가 없다. AI WebView deny-all permission은 유지.
- New Tab internal document는 HTTPS가 아니므로 Vault capture/fill 대상이
  아니다. `InternalDocumentNavigation`/`NewTabDocumentPolicy`는 변경하지 않았다.
- Site Permissions는 `PermissionRequested` 별도 경로이며 Vault 메시지와
  상호 간섭하지 않는다.

---

## 7. 설정 / Vault 상태 / 생명주기

### 7.1 설정

- `login_save_prompt_enabled=false`
  - login candidate 수신 시 즉시 중단
  - Save dialog 없음
  - Vault write 없음
- `login_autofill_enabled=false`
  - explicit Fill 즉시 중단
  - Fill dialog 없음
  - page field 변경 없음
- 두 설정 모두 Vault 잠금/정확한 origin/승인 정책을 우회하지 않는다.
- 설정은 Phase 2.3의 profile SQLite `login_*` key를 그대로 사용한다.
  기본값 true, scope/profile 변경 없음.

### 7.2 Vault lock epoch

- `LockVaultAsync` 시작 시 `_vaultLockEpoch`를 증가시킨다.
- capture/fill 시작 시 epoch를 기록하고, dialog/detect/list/secret 이후
  `VaultContextStillValid`에서 epoch/Unlocked 상태를 재검증한다.
- lock이 dialog 도중 발생하면 `InvalidateWebViewVaultOperations`가 pending
  command를 취소하고, epoch 불일치로 저장/입력이 중단된다.
- Profile switch, tab close, window close, navigation, Vault idle/session/manual
  lock, WebView dispose에서 pending command/secret/UI가 정리된다.

### 7.3 메모리 / 로그

- C# `string`은 zeroize를 보장할 수 없다. `WebViewVaultSecret.Clear`와
  `VaultEntryDto.ClearPassword`는 best-effort이며 실제 zeroization을
  주장하지 않는다.
- password를 status/log/exception message/WebMessage 일반 payload에 남기지
  않는다.
- Fill은 clipboard를 사용하지 않는다. Clipboard copy는 별도 명시적 Vault
  동작이다.
- Vault encryption/DPAPI/backup/import/CAS는 변경하지 않았다.

---

## 8. 테스트 결과

### 8.1 Windows / Linux

| Suite | 결과 |
|---|---|
| WinUI UI smoke | **84/84 PASS** (기존 76 + Phase 2.4 8) |
| C# ↔ Rust interop | **113/113 PASS** |
| Windows 통합 검증 | **182 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** (198 total) |
| Linux CI | **SUCCESS** (interop 113/113, vault 24/24) |
| Built-in autofill policy probe | **PASS** |
| Package/MSIX | **SKIPPED (8)**, 생성 없음 |

최종 Windows run: [45836063700](https://github.com/thfzm/AMARE/actions/runs/45836063700)
(`e1b9cbf`)

최종 Linux run: [45839402834](https://github.com/thfzm/AMARE/actions/runs/45839402834)
(`e1b9cbf`)

### 8.2 신규 Phase 2.4 UI tests

```text
PASS vault_capture_settings_off
  login_save_prompt_enabled OFF → no prompt/no entry
PASS vault_fill_settings_off
  login_autofill_enabled OFF → no Fill dialog/no field mutation
PASS vault_fill_locked_rejected
  Vault locked → no Fill dialog/no field mutation
PASS vault_capture_cross_action_rejected
  cross-origin form action → no prompt/no entry
PASS vault_capture_cross_frame_rejected
  cross-origin iframe login → no prompt/no entry
PASS vault_fill_other_origin_rejected
  same path/other origin → no Fill list/no mutation
PASS vault_fill_hidden_field_rejected
  hidden/disabled password fields → no Fill dialog/no mutation
PASS vault_duplicate_not_overwritten
  exact origin+username duplicate → entry count unchanged
```

기존 Phase 1.3D/2.3 Vault tests:

```text
PASS vault_ui_initialize_lock_unlock
PASS vault_ui_crud_reveal_backup
PASS vault_ui_profile_switch_isolation
PASS vault_ui_multiwindow_lock
PASS vault_capture_requires_approval
PASS vault_fill_explicit_and_no_submit
PASS vault_forged_message_rejected
PASS vault_capture_locked_rejected
PASS vault_ui_idle_lock
```

### 8.3 Core / Policy tests

기존 `Phase28Tests` 15개가 계속 PASS했다.

- exact HTTPS origin/default port
- scheme/port/host/subdomain reject
- IDN/punycode/lookalike
- userinfo/trailing dot reject
- action origin exact
- same document URL exact
- ready token/generation
- candidate source/token/generation binding
- schema/duplicate reject
- cross-origin/URL reject
- detect/read/fill command one-use
- replay/wrong op reject
- ready/context requirement
- payload/field limits
- script no auto-submit/secret candidate

### 8.4 PASS / UNVERIFIED / BLOCKED / SKIPPED

**PASS**

- exact origin/action/frame/source/token/generation validation
- HTTPS top-level only
- approval-before-read capture
- duplicate no-overwrite
- explicit Fill exact-origin
- no auto-submit/Enter/click
- settings OFF capture/fill
- Vault locked capture/fill
- cross-origin iframe/action/frame and other-origin Fill rejection
- hidden/disabled password field rejection
- AI/Reader no Vault bridge (code + regression)
- New Tab/Site Permissions/AI/Settings/Multiwindow regression
- Windows/Linux CI

**UNVERIFIED**

- 실제 Windows 11 실기기에서 실제 로그인 페이지 capture/Fill
- 실제 기본 5분 Vault idle lock
- 실제 Windows session lock (`SystemEvents.SessionSwitch`)
- 강제 프로세스 종료/전원 손실 중 secret/UI 안전성
- async window close/cancel race의 실제 OS timing
- multiwindow Vault lock 일관성의 실제 Windows 11 조건 (자동화 UI는 PASS)
- 실제 WebView2 renderer/DOM의 password 평문 제거 불가 한계
- 사용자 페이지가 Fill 후 값을 변조/자동 제출하는 악성 시나리오의 일반적 방어
  (AMARE는 submit을 실행하지 않지만 페이지 자체 동작은 통제 불가)
- 실제 Windows 11 Korean IME/DPI/접근성 (Phase 2.3 한계)

**BLOCKED**

- 없음.

**SKIPPED**

- 패키지 8단계
- Netflix/Disney 유료 서비스 probe

---

## 9. 최초 실패 및 수정 이력

| # | run | 실패 | 분류 | 수정 |
|---:|---|---|---|---|
| 1 | [45826017782](https://github.com/thfzm/AMARE/actions/runs/45826017782) | 새 Fill 테스트 5개가 status text timeout, 후속 profile test까지 영향 | TEST_AUTOMATION (status UIA) | dialog 부재/page field/Vault count 기반 assertion으로 변경 |
| 2 | [45829904890](https://github.com/thfzm/AMARE/actions/runs/45829904890) | diagnostics run: autofill=False/dialogOpen=False였지만 StatusText UIA name empty | TEST_AUTOMATION | 보안 동작은 PASS, status assertion 제거 |
| 3 | [45833226695](https://github.com/thfzm/AMARE/actions/runs/45833226695) | `webview_focus_shortcuts` pre-existing flake | PRE-EXISTING UI FLAKE | 동일 HEAD 재실행 |
| 4 | [45836063700](https://github.com/thfzm/AMARE/actions/runs/45836063700) | 최종 clean: 182/0/8/8, UI 84/84, interop 113/113 | — | 최종 증거 |

- 최초 실패에서 **product security failure는 확인되지 않았다.** log상
  `autofill=False`, `dialogOpen=False`였고 Fill dialog/field mutation이 없었다.
- status text automation name이 비어 있는 UIA 문제로 테스트가 실패했으므로
  status 문자열 대신 사용자 관찰 가능한 보안 결과를 assertion했다.
- assertion을 약화한 것이 아니라 **검증 대상을 status에서 실제 동작으로
  변경**했다.

증거: `artifacts/windows/phase2.4-vault-webview/`

---

## 10. 변경 파일 / 커밋

### 제품

- `apps/windows/AMARE.Windows/MainWindow.Vault.cs`
  - `_vaultLockEpoch`, lock 시 증가
- `apps/windows/AMARE.Windows/MainWindow.WebViewVault.cs`
  - settings 재검사, lock epoch 재검증, diagnostics
- `apps/windows/AMARE.Windows/MainWindow.Settings.cs`
  - `login_*` settings 진단 log
- `apps/windows/AMARE.Windows.Core/Localization.cs`
  - `vault.autofillDisabled`, `vault.savePromptsDisabled`

### 테스트

- `tools/windows-ui-smoke/Program.cs` (+8 Vault tests)
- `tools/windows-ui-smoke/assets/vault/login-cross-action.html` (신규)
- `tools/windows-ui-smoke/assets/vault/login-cross-frame.html` (신규)
- `tools/windows-ui-smoke/assets/vault/login-hidden.html` (신규)

### 주요 commit

```text
0ba49bc Harden Vault WebView settings, lock epoch and add security tests
8396819 Add Vault WebView settings/lock diagnostics and security tests
e1b9cbf Make Vault fill security tests behavior-based
```

---

## 11. 남은 P0/P1/P2 및 최우선 후속 작업

### P0

- **없음** (이번 범위에서 발견된 P0 보안 결함 없음).

### P1

1. 실제 Windows 11 실기기에서 실제 로그인 페이지 capture/Fill 검증.
2. 실제 5분 idle lock / Windows session lock / 강제 종료 race.
3. multiwindow lock의 실제 Windows 11 조건 검증.
4. 기존 WebView2 saved autofill data 삭제(`ClearBrowsingDataAsync`)의 실제
   실행 검증 (사용자 동의 후).

### P2

1. 실제 사이트별 field 변형(hidden username, multi-step login, SPA) 품질.
2. Fill 후 page가 값을 변조/자동 제출하는 악성 동작에 대한 관찰/경고.
3. Vault capture dialog의 Phase 2.3 localization 잔여 문자열.
4. 장시간/반복 capture/fill soak 및 memory 측정.

---

## 12. 결론

- Phase 2.4의 핵심 보안 통합은 **PASS**다.
  - top-level/exact-origin/action/frame 검증 유지
  - approval-before-read capture
  - explicit exact-origin Fill, no auto-submit
  - settings OFF / Vault Lock / cross-origin / hidden field / duplicate 차단
  - AI/Reader no Vault bridge, New Tab/Site Permissions 무변경
- Windows **182/0/8/8**, UI **84/84**, interop **113/113**, Linux **SUCCESS**.
- 실제 Windows 11 실기기/실제 OS lock/force-kill race는 UNVERIFIED로 남긴다.

다음 단계는 이 보고서를 확인한 뒤 결정한다. 패키지/MSIX/서명/공개 배포는
수행하지 않았다.
