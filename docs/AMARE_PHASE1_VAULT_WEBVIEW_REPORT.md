# AMARE Phase 1.3D — WebView2 Password Capture & Autofill Security Report

- 상태: **PASS** (Windows/Linux CI). 단, 실제 Windows 11·세션 잠금·실기기 항목은 UNVERIFIED.
- 작업 branch: `feature/phase-1-vault-webview-v2`
- 기준 branch: `fix/new-tab-internal-navigation` (`1dd5671`, 제품/테스트 `7f6a1d8`)
- 최종 제품/테스트 HEAD: **`1cb4780`**
- 문서 HEAD: 이 보고서를 포함한 문서 commit
- Working tree: clean (문서 commit 전)
- 보존한 기존 Phase 1.3D branch: `feature/phase-1-vault-webview` = **`c0138ab`**
- 패키지/MSIX/서명/공개 Release: **없음**

---

## 1. 최종 요약

| 완료 기준 | 판정 | 근거 |
|---|---|---|
| API/frame trust 검증 | **PASS** | top-level WebMessage API 계약 + per-document token/generation; frame 이벤트 미구독; forged/iframe UI test |
| WebMessage origin/generation 검증 | **PASS** | exact HTTPS origin, exact document URL, token, generation, msgId, profile/WebView/context |
| Capture 사용자 승인 | **PASS** | Save dialog 전 password 미수집; reject/approve UI test |
| 정확한 origin 저장 | **PASS** | canonical origin + exact action origin; entry count UI test |
| 사용자 요청 기반 Autofill | **PASS** | 메뉴 → 계정 선택 → Fill UI test |
| 자동 제출 방지 | **PASS** | script 정적 검사 + fill 후 `submitted` 부재 |
| 기본 WebView2 Autofill 비활성화 | **PASS** | `webview2_autofill_policy` probe (2개 instance) |
| Vault 잠금/프로필 격리 회귀 | **PASS** | 1.3C UI tests + locked capture test |
| 기존 New Tab/navigation 회귀 | **PASS** | 1.3C.1 UI tests + 전체 64/64 |
| Windows/Linux CI | **PASS (기록)** | Windows 167/0/8/8, UI 64/64, interop 99/99, Linux SUCCESS |

**BLOCKED로 남긴 보안 기능은 없다.** 다만 실제 Windows 11 workstation, 실제
5분 idle, Windows session lock, 강제 종료/비동기 경합은 UNVERIFIED다.

---

## 2. Git 기준 / 기존 Phase 1.3D branch 관계

```text
merge-base(feature/phase-1-vault-webview, fix/new-tab-internal-navigation)
  = 475bfc5

feature/phase-1-vault-webview (c0138ab, 로컬 보존)
  b2c8162 feat: add WebView2 Vault capture and explicit autofill
  7194b9b docs: record Phase 1.3D WebView2 Vault implementation
  e4d07ef 1.3C 잔여 lock UI test (이미 1.3C에 cherry-pick됨)
  c0138ab docs: record Phase 1.3E Windows/WebView2 validation block

feature/phase-1-vault-webview-v2 (신규)
  1dd5671 (1.3C.1 New Tab 수정 문서 포함)
  7f6a1d8 (1.3C.1 제품/테스트 HEAD, ancestor 확인)
  ...
  1cb4780 최종 제품/테스트 HEAD
```

- 기존 branch는 **rewrite·삭제·force push하지 않았다**.
- 기존 Phase 1.3D 구현(`WebOrigin`, `WebViewVaultTabState`, `WebViewVaultScript`,
  `MainWindow.WebViewVault`, Phase28)을 검토한 뒤, 최신 기준 위에 **재구현**했다.
  - 이유 1: 기존 branch의 base(475bfc5)에는 1.3C 잔여 lock 수정과 New Tab
    provenance 수정이 없다.
  - 이유 2: 새 요구사항(native per-document token, 명시적 generation, strict
    schema, cross-origin/iframe 검증, close-hide, autofill runtime probe)을
    기존 구현이 충족하지 않는다.
  - 충돌을 추측으로 해결한 파일은 없다.
- `fix/new-tab-internal-navigation`의 New Tab 수정은 새 branch에 포함되어 있다
  (`git merge-base --is-ancestor 7f6a1d8 HEAD` 확인).

---

## 3. WebView2 SDK / API 조사

사용 패키지/런타임:

```text
Microsoft.Web.WebView2 1.0.4314.35
Microsoft.WindowsAppSDK 2.6.5
Windows CI WebView2 runtime 157.0.4341.46
CI OS: Windows Server 2025 (windows_11=false)
```

| API | 확인 결과 / 사용 |
|---|---|
| `CoreWebView2.WebMessageReceived` | **top-level document 전용**. iframe은 `CoreWebView2Frame.WebMessageReceived`로 별도 전달. |
| `CoreWebView2WebMessageReceivedEventArgs.Source` | 메시지를 보낸 document URI. **단독 신뢰하지 않는다.** |
| `CoreWebView2Frame` | frame별 event/script API는 있으나 frame의 origin/source 속성은 없다. **frame WebMessage는 구독하지 않는다.** |
| `NavigationStarting` / `NavigationCompleted` | generation 무효화 / native token 발급·init |
| `SourceChanged.IsNewDocument` | same-document URL 변경 처리 |
| `IsPasswordAutosaveEnabled` / `IsGeneralAutofillEnabled` | Settings와 Profile 양쪽에 존재. profile-wide. probe에서 false read-back PASS |
| `ExecuteScriptAsync` | JSON 결과. **password read에는 사용하지 않음** (page-overwritable global 회피); `PostWebMessageAsJson` 명령/응답 사용 |
| `ClearBrowsingDataAsync` | `PasswordAutosave | GeneralAutofill`만, 사용자 동의 후. CI에서는 실행하지 않음 |
| `AddScriptToExecuteOnDocumentCreatedAsync` | top-level guard가 있는 dormant script 주입 |
| `CoreWebView2HostResourceAccessKind.Deny` | test-only virtual host에 사용 |

`IsWebMessageEnabled`는 top-level 문서 통신을 설명하며, iframe은 별도 frame
event를 사용한다(WebView2 IDL). 따라서 top-level event만 구독하는 것이
frame 경계의 1차 방어선이다. 여기에 native token/generation/exact URL을
추가해 `args.Source` 문자열만 신뢰하는 구현을 피했다.

---

## 4. Frame / source trust 모델

1. **top-level WebMessage event만 구독**한다. frame WebMessage event는
   구독하지 않으므로 iframe이 보낸 메시지는 host handler에 도달하지 않는다.
2. content script는 `window.top !== window`에서 즉시 종료한다.
3. top-level 문서마다 native가 **16-byte random document token**(32 hex)과
   **generation**을 발급해 `amare_init`으로 전달한다. token은 closure에만
   보관하고 모든 메시지에 포함한다.
4. host는 메시지마다 다음을 검증한다.
   - 현재 WebView 인스턴스의 상태인지 (`ReferenceEquals`, `WebViewId`)
   - Profile ID / Vault generation context 일치
   - `args.Source`의 HTTPS origin이 현재 document origin과 exact 일치
   - `args.Source`의 **전체 document URL**이 `core.Source`와 `Uri.Compare`로 일치
   - message token / generation이 현재 값과 일치
   - `msgId`가 16~64 hex이고 document당 중복이 아님
   - 메시지 type별 **정확한 property set** (예상 밖 field 거부)
   - form action origin이 document origin과 exact 일치
5. 메시지 검증 실패는 모두 fail closed다. 로그에는 type/state만 남기고
   password, username, origin, payload는 남기지 않는다.
6. UI test `vault_forged_message_rejected`:
   - top-level page가 잘못된 token으로 candidate를 보냄 → prompt/저장 없음
   - same-origin iframe과 cross-origin(`amare-vault-alt.test`) iframe의
     child가 candidate를 보냄 → prompt/저장 없음

> `CoreWebView2Frame`에는 신뢰 가능한 frame origin/source 속성이 없으므로
> frame 메시지를 "검증해서 처리"하지 않고 **수신 자체를 하지 않는** 정책을
> 택했다. 향후 WebView2가 top-level event에 frame 메시지를 섞는 계약으로
> 바뀌면 이 경계를 재평가해야 한다(SDK pinned).

---

## 5. Origin / document generation 검증

`WebOrigin`:

- HTTPS만 허용, userinfo/trailing dot/공백/제어문자 거부
- IDN → punycode lowercase 정규화
- default port 443 정규화, 명시 port는 exact 비교
- **subdomain 상속 없음**
- lookalike/Unicode host는 exact 비교로 차단
- `SameDocumentUrl`은 fragment/query/path를 포함한 전체 URL을 `Uri.Compare`로 비교

`WebViewVaultTabState`:

- `OnNavigationStarting` → generation++, origin/URL/token/ready/pending/candidate
  /seen-message 초기화
- `TryBeginDocument` → NavigationCompleted에서만 token 발급
- `amare_ready` → Ready
- candidate/detect/secret/fill 결과 → token/generation/source/opId/schema 검증
- pending command는 **한 번에 하나**, opId는 one-use
- candidate는 `origin|formToken|username`으로 document당 1회만 prompt
- invalid/old/duplicate는 모두 거부

---

## 6. 로그인 폼 감지 / Capture 흐름

### 6.1 감지

- top-level HTTPS에서만 동작
- form action origin == document origin
- form-associated, visible, enabled, non-readonly password field
- password field 1개 + username field 후보 1개일 때만 unambiguous
- script는 **password 값을 읽지 않는다.** detect/candidate 메시지는
  존재 여부, form token, username, action origin, generation만 전달한다.
- `detect` 결과도 native strict schema/token/generation/source를 통과해야 한다.

### 6.2 저장

```text
submit
  → script: metadata candidate (no password)
  → native: source/URL/origin/action/token/generation/schema/WebView/profile 검증
  → Vault status 확인 (Unlocked 필수)
  → ContentDialog "Save password?" (기본 Not now)
  → 사용자가 Save
  → context/WebView/Vault instance/status/generation/origin/form 재검증
  → native "amare_read_secret" command (token/generation/opId/formToken)
  → script: 저장된 field reference가 현재 form에 있고
            visible/enabled/non-readonly/form-associated,
            password field 1개, action origin 일치 확인 후 값 반환
  → native: strict schema, username 일치, 길이/제어문자, 중복 확인
  → vault.create(canonical origin, username, password)
  → secret.Clear() (best-effort)
```

- 승인 전 password read 없음
- 페이지가 승인 전에 이동/교체되면 generation/token이 무효화되어 저장 취소
- class form submit이 승인 전에 navigation을 일으키면 저장하지 않는다
  (fail closed; SPA/async login에서만 저장 가능). 이 제한은 의도된 보안 선택이다.
- 동일 origin+username은 덮어쓰지 않는다.
- password/username/origin은 로그에 남기지 않는다.

UI 증거:

```text
PASS vault_capture_requires_approval
  - reject: Vault entry count unchanged
  - approve(vault-user): exact origin entry saved
  - approve(second-user): multiple accounts saved
PASS vault_capture_locked_rejected
  - Vault locked → no dialog, entry count unchanged
```

---

## 7. Autofill 흐름

메뉴 **“Fill password for this site…”** 사용자 명시 동작에서만 실행한다.

```text
사용자 메뉴 선택
  → active tab / WebView / profile / Vault generation / Ready / exact HTTPS origin 확인
  → Vault Unlocked 확인
  → native "amare_detect" (token/generation/opId)
  → strict detect result; ambiguous/action/URL/generation 검증
  → exact origin entry만 ListView에 표시 (자동 선택 없음)
  → 사용자가 계정 선택 + Fill
  → context/token/generation/origin/URL 재검증
  → vault.get_for_fill(context, canonical origin, entryId)
  → 반환 entry origin 재검증
  → native "amare_fill" (token/generation/opId/formToken/username/password)
  → script: stored field reference, visible/enabled/non-readonly,
            one password, action origin 확인
  → input/change event만 발생 (submit/Enter/click/requestSubmit 없음)
  → secret.ClearPassword() (best-effort)
```

- hidden/disabled/readonly/ambiguous field는 script에서 거부
- cross-origin iframe field 사용 없음
- Vault locked/entry 없음/문서 변경 시 취소
- 여러 계정이 있으면 사용자가 선택 (UI test에서 2개 계정 중 선택)

UI 증거:

```text
PASS vault_fill_explicit_and_no_submit
  - dialog에서 vault-user 선택
  - page status "filled:vault-user"
  - "submitted" 부재, URL 유지
```

---

## 8. WebView2 기본 Autofill 정책

적용 대상:

- 일반 탭 WebView (`CreateTabAsync` → `ConfigureWebViewVaultAsync`)
- 새 창/새 탭 (동일 코드)
- AI WebView (`ApplyBuiltInAutofillPolicy`)
- Reader WebView

적용 값:

```csharp
Settings.IsPasswordAutosaveEnabled = false;
Settings.IsGeneralAutofillEnabled = false;
Settings.AreHostObjectsAllowed = false;
Profile.IsPasswordAutosaveEnabled = false;
Profile.IsGeneralAutofillEnabled = false;
```

실패 시 해당 탭의 Vault bridge를 비활성화하고 fail closed한다.

Runtime probe:

```text
PASS webview2:webview2_autofill_policy
  - 같은 profile의 두 WebView2에서 read-back false
  - AreHostObjectsAllowed=false
```

기존 저장 데이터 삭제는 **자동으로 하지 않는다.** 메뉴
“Clear WebView saved passwords…”에서 영향 범위를 고지하고 기본 Cancel로
동의한 경우에만 `ClearBrowsingDataAsync(PasswordAutosave | GeneralAutofill)`을
호출한다. 쿠키/기록/Vault entry는 삭제하지 않는다. 이 clear 동작 자체는
CI에서 실행하지 않았으므로 **UNVERIFIED**다.

---

## 9. Vault 수명 / close 동작 변경

Autofill/capture는 사용자가 browsing 중 Vault가 unlocked 상태여야 한다.
Phase 1.3C의 “Vault panel close → lock” 동작을 다음과 같이 변경했다.

- **X(숨기기)**: panel만 hide, native Vault는 unlocked 유지 (idle timer 계속)
- **Lock 버튼**: 즉시 native lock
- **idle 5분(테스트 30초) / Windows session lock / profile switch / app 종료**:
  native lock 유지

이 변경으로 Vault panel을 닫아도 자동완성 메뉴를 사용할 수 있다. 보안 경계는
manual lock, idle, session, profile, shutdown에서 그대로 유지된다. 기존
1.3C lock/profile/multiwindow/idle UI test는 모두 PASS했다.

---

## 10. 테스트 결과

### 10.1 Windows / Linux

| Suite | 결과 |
|---|---|
| WinUI UI smoke | **64/64 PASS** (기존 60 + 신규 4) |
| C# <-> Rust interop | **99/99 PASS** (기존 84 + Phase28 15) |
| `webview2_autofill_policy` | **PASS** |
| Windows CI | **167 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** (183 total) |
| Rust ABI 1 / ABI 2 | 7/7 / 6/6 |
| Windows/Linux vault core | 25/25 / 24/24 |
| Linux CI | **SUCCESS** (interop 99/99) |
| Package/MSIX | **SKIPPED (8)**, 생성 없음 |

- Windows 최종: [45588348239](https://github.com/thfzm/AMARE/actions/runs/45588348239)
- Linux 최종: [45590841382](https://github.com/thfzm/AMARE/actions/runs/45590841382)

신규 UI test:

```text
PASS vault_capture_requires_approval
PASS vault_fill_explicit_and_no_submit
PASS vault_forged_message_rejected
PASS vault_capture_locked_rejected
```

### 10.2 PASS / UNVERIFIED / BLOCKED / SKIPPED

**PASS**

- exact HTTPS origin / scheme / port / subdomain / IDN / punycode / lookalike /
  userinfo / invalid URL (Core)
- action origin exact (Core + script)
- top-level document token/generation/source/profile/WebView binding (Core + UI)
- strict schema/unexpected field/duplicate/replay (Core)
- same-origin/cross-origin iframe forged message rejection (UI)
- capture user approval / rejection / multiple accounts / exact origin (UI)
- capture while locked rejected (UI)
- explicit fill / no auto-submit / exact-origin entry / multiple accounts (UI)
- built-in autofill disable (probe)
- Vault lock/profile/multiwindow/idle/backup regressions (UI)
- New Tab internal page, address search, external data block, popup, iframe,
  redirect (1.3C.1 UI)
- Windows/Linux CI

**UNVERIFIED**

- 실제 기본 5분 idle lock (UI는 `AMARE_VAULT_IDLE_SECONDS=30` 단축만)
- 실제 Windows session lock (`SystemEvents.SessionSwitch`)
- 실제 Windows 11 IME/DPI/접근성 (CI는 Windows Server 2025)
- 비동기 창 닫힘/취소 경합
- 강제 종료 중 Vault UI 상태
- UI 수준의 `javascript:`/`file:` 차단 (Core unit만)
- hidden/disabled/readonly/ambiguous field 개별 UI fixture (script/Core static만)
- 기존 WebView2 저장 데이터 삭제(`ClearBrowsingDataAsync`) 실행
- AI/Reader WebView의 실제 form autofill (정책 적용/probe만)
- 실제 top-level page가 malicious일 때의 고유 한계 (페이지는 자기 password를 읽을 수 있음)

**BLOCKED**

- 없음. SDK 1.0.4314.35 / runtime 157.0.4341.46에서 top-level WebMessage
  계약과 token/generation 검증으로 frame 경계를 구현·검증했다.

**SKIPPED**

- 패키지 8단계, Netflix/Disney 유료 서비스 1단계

---

## 11. 최초 실패 및 수정 이력

| # | 실패 | 분류 | 수정 |
|---:|---|---|---|
| 1 | `unsigned long -> long` compile error (`ProfileContextDto.Generation`) | PRODUCT_BUILD_DEFECT | explicit cast |
| 2 | `webview2_autofill_policy` FAIL (second instance enabled) | TEST_AUTOMATION_BUG | probe가 두 instance 모두 설정/read-back |
| 3 | capture test가 status bar timeout | TEST_AUTOMATION_BUG | status 대신 Vault entry count로 기능 검증 |
| 4 | fill dialog 미표시 (empty username = ambiguous) | PRODUCT_DEFECT (UX/logic) | username field 존재만 요구, 값은 optional (fill) |
| 5 | locked capture test가 status bar empty | TEST_AUTOMATION_BUG | dialog 부재 + entry count로 검증 |
| 6 | `certificate_error_hostname_mismatch_ui` / `tear_off_tab` / `session_multiwindow_restore` 간헐 실패 | PRE-EXISTING UI FLAKE | 동일 HEAD 재실행 최종 64/64 PASS, 로그 보존 |

최초 Windows run [45575552631](https://github.com/thfzm/AMARE/actions/runs/45575552631)은
compile failure, [45577412754](https://github.com/thfzm/AMARE/actions/runs/45577412754)은
probe/UI 첫 실패였다. 증거는 `artifacts/windows/phase1.3d/`에 보존했다.

---

## 12. Phase 1.3C 잔여 검증

| 항목 | 결과 |
|---|---|
| 30초 idle override → native lock → UI clear | **PASS** (UI) |
| 실제 5분 idle | **UNVERIFIED** |
| Windows session lock event | **UNVERIFIED** (코드만) |
| 비동기 창 닫힘/취소 경합 | **UNVERIFIED** |
| 강제 종료 중 Vault UI | **UNVERIFIED** |
| profile switch / multiwindow / backup / reveal | **PASS** (1.3C UI) |

idle override는 기존처럼 **5분 이하 단축만** 허용한다.

---

## 13. 알려진 보안 제한

1. **C# immutable string**: `VaultEntryDto.Password`, `WebViewVaultSecret.Password`,
   `PostWebMessageAsJson`/`ExecuteScriptAsync`의 JSON 문자열은 원본 allocation을
   완전히 zeroize할 수 없다. `Clear`/`ClearPassword`는 best-effort이며 실제
   zeroization을 주장하지 않는다.
2. **페이지는 자기 origin의 password를 읽을 수 있다.** top-level page가
   악성/변조되면 저장 값을 조작할 수 있다. 사용자는 dialog에서 origin/username을
   보고 승인하며, AMARE는 페이지가 반환한 값을 그 origin의 credential로 저장한다.
3. **classic navigation login**: submit 직후 페이지가 이동하면 승인 전 document가
   사라져 저장하지 않는다(security over convenience). SPA/async login에서만
   안정적으로 동작한다.
4. **top-level WebMessage API 계약**: WebView2가 장래에 frame 메시지를 top-level
   event로 전달하는 방향으로 바뀌면 token/URL 검증을 재검토해야 한다. SDK는
   pinned이며, 현재 runtime에서 forged/iframe test PASS.
5. **기본 autofill 기존 데이터**: 설정 비활성화는 기존 저장 데이터를 삭제하지
   않는다. 삭제는 사용자 동의가 필요하며 CI에서 실행하지 않았다.
6. **test-only virtual host**: `AMARE_TEST_VAULT_ROOT`가 설정된 경우에만
   `amare-vault.test`/`amare-vault-alt.test`를 로컬 folder에 매핑한다
   (`Deny`, `.test` TLD). 일반 실행에서는 비활성이다.
7. **Windows 11 실기기**: CI는 Windows Server 2025다. IME/DPI/접근성과 실제
   session lock은 UNVERIFIED다.
8. **Vault panel close-hide**: close는 hide이며 unlocked 상태를 유지한다.
   Manual lock/idle/session/profile switch/app 종료는 lock한다.

---

## 14. 변경 파일 / 커밋

### 제품

- `apps/windows/AMARE.Windows.Core/WebOrigin.cs` (신규)
- `apps/windows/AMARE.Windows.Core/WebViewVaultTabState.cs` (신규)
- `apps/windows/AMARE.Windows.Core/WebViewVaultScript.cs` (신규)
- `apps/windows/AMARE.Windows/MainWindow.WebViewVault.cs` (신규)
- `apps/windows/AMARE.Windows/MainWindow.xaml.cs`
- `apps/windows/AMARE.Windows/MainWindow.Vault.cs`
- `apps/windows/AMARE.Windows/MainWindow.xaml` (Hide tooltip)

### 테스트/검증

- `apps/windows/AMARE.Interop.Tests/Phase28Tests.cs` (신규, 15 tests)
- `apps/windows/AMARE.Interop.Tests/Program.cs`
- `tools/windows-ui-smoke/Program.cs` (+4 vault tests)
- `tools/windows-ui-smoke/assets/vault/*.html` (test-only virtual host)
- `tools/windows-ui-smoke/WindowsUiSmoke.csproj`
- `tools/windows-verify/Probes.cs`, `VerifyForm.cs` (`webview2_autofill_policy`)

### 주요 commit

```text
8ede038 Add WebView2 Vault capture and explicit autofill
830733f Cast profile generation for the WebView Vault context
af095ba Fix empty username detect and verify autofill policy per instance
f0b27a8 Use vault entry counts for capture tests and add save diagnostics
022830b Clarify that closing the vault panel hides it
1cb4780 Cover cross-origin frames and multiple account selection
```

---

## 15. 결론 및 다음 단계

- Phase 1.3D의 핵심 보안 기능은 **PASS**다.
  - top-level WebMessage + native token/generation + exact origin/URL + strict schema
  - 명시적 승인 후 capture, explicit fill, no auto-submit
  - built-in autofill disable runtime probe PASS
  - forged top-level / same-origin iframe / cross-origin iframe 차단 UI PASS
- Windows **167/0/8/8**, UI **64/64**, interop **99/99**, Linux **SUCCESS**.
- 실제 Windows 11/5분 idle/session lock/강제 종료 경합은 UNVERIFIED로 남긴다.

다음 단계는 이 보고서를 확인한 뒤 결정한다. 패키지/MSIX/서명/공개 배포는
수행하지 않았다.
