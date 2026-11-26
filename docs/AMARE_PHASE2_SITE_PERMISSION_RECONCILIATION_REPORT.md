# AMARE Phase 2.1.1 — WebView2 Legacy Permission Audit & Reconciliation 보고서

- 상태: **PASS** (Windows/Linux CI). 실제 Windows 11 실기기와 cross-process 동시성은 UNVERIFIED.
- 작업 branch: `fix/phase-2-webview-permission-reconciliation`
- 기준 branch: `feature/phase-2-site-permissions`
- 기준 제품/테스트 HEAD: **`f1d878d`**
- 기준 문서 HEAD: **`4f02dcf`**
- 최종 제품/테스트 HEAD: **`d41ee59`**
- 문서 HEAD: 이 보고서를 포함한 문서 commit
- Working tree: **clean** (문서 commit 후)
- DB schema 변경: **없음** (v6 유지)
- 패키지/MSIX/코드 서명/공개 Release: **없음**

---

## 1. 최종 요약

| 완료 기준 | 판정 | 근거 |
|---|---|---|
| WebView2 profile permission API 실제 지원 범위 조사 | **PASS** | SDK 1.0.4314.35 IDL/XML, runtime 157.0.4341.46 |
| 기존 non-default permission state 실제 조회 | **PASS** | 13개 named kind Allow seed → `before=13`, reset 후 `after=0` probe |
| startup audit 설계/구현 | **PASS** | 첫 tab navigation 전 audit, Reader/AI 포함 |
| AMARE SQLite 정책과의 일관성 | **PASS** | exact origin/kind 비교, 모든 legacy state를 Default로 reset |
| 기존 WebView2 Allow의 우회 가능성 차단 | **PASS** | legacy Allow seed → audit reset → AMARE consent dialog |
| audit 실패 시 fail closed | **PASS** | test-only audit failure → 설정 `Failed`, 권한 요청 deny |
| 동시 창/프로필 lifecycle | **PASS (코드) / UNVERIFIED (strict race)** | per-profile static coordinator, profile별 audit |
| 기존 Site Permissions 기능 회귀 없음 | **PASS** | UI 73/73, site permissions 9/9 |
| Windows/Linux CI | **PASS (기록)** | Windows 172/0/8/8, UI 73/73, interop 103/103, Linux SUCCESS |
| 사용자 데이터 영향 | **PASS (범위 제한)** | permission setting만 reset, cookie/history/Vault 무변경 |

**BLOCKED로 남긴 기능은 없다.** 다만 API 자체가 없는 구버전 WebView2
runtime에서는 legacy Allow가 event 없이 적용될 수 있으며, 이 경우 AMARE가
요청을 가로챌 수 없다. 이 조건은 아래 제한에 명시한다.

---

## 2. Git 기준 / branch

```text
feature/phase-2-site-permissions
  f1d878d  Cover settings entry for site permissions
  4f02dcf  Clarify Phase 0 scope after site permission update

fix/phase-2-webview-permission-reconciliation (신규)
  d41ee59  Probe all named WebView2 permission kinds during reconciliation
  cc8429d  Test fail-closed behavior after a legacy audit failure
  41b1b93  Rename audit lock to avoid type shadowing
  a65d604  Reuse the existing WebView2 test root constant
  a3dd019  Audit and reconcile legacy WebView2 permission settings
```

- `git merge-base --is-ancestor f1d878d HEAD`와 기준 branch 포함을 확인했다.
- Phase 2.1 구현/증거(`feature/phase-2-site-permissions`, `f1d878d`)는
  변경·삭제하지 않았다.
- GPUI, Vault 암호화/capture/autofill, New Tab provenance는 변경하지 않았다.
- force push, history rewrite를 하지 않았다.
- DB schema는 v6 그대로이며 migration을 추가하지 않았다.

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
| `CoreWebView2Profile.GetNonDefaultPermissionSettingsAsync()` | **현재 및 이전 session의 non-default permission setting 전체**를 반환한다. 각 항목은 kind/origin/state다. **audit read에 사용**. |
| `CoreWebView2Profile.SetPermissionStateAsync(kind, origin, state)` | 특정 origin/kind의 profile permission state를 비동기로 설정한다. `Default`를 설정하면 **저장된 state를 지우고 기본 동작으로 되돌린다**. **reconciliation reset에 사용**. |
| `CoreWebView2PermissionSetting.PermissionKind` | `COREWEBVIEW2_PERMISSION_KIND`. **13개 named kind + Unknown**. |
| `CoreWebView2PermissionSetting.PermissionOrigin` | 설정의 origin. path/fragment는 무시되고 scheme/host가 유효해야 한다. | 
| `CoreWebView2PermissionSetting.PermissionState` | `Default/Allow/Deny`. non-default collection이므로 보통 Allow/Deny. |
| `CoreWebView2.PermissionRequested` | top-level 요청 event. Phase 2.1 정책 유지. |
| `CoreWebView2Frame.PermissionRequested` | iframe 요청 event. Phase 2.1과 동일하게 즉시 deny. |
| `CoreWebView2PermissionRequestedEventArgs3.SavesInProfile` | Phase 2.1과 동일하게 **모든 결정에서 false**. |
| `CoreWebView2.Profile` | 현재 WebView2의 profile. audit entry point. |
| `CoreWebView2.ProfilePath` / `ProfileName` | audit log의 profile 식별에 사용할 수 있으나 AMARE profile ID를 key로 사용했다. |

공식 문서/IDL에서 확인한 동작:

- `GetNonDefaultPermissionSettingsAsync`는 **현재 session뿐 아니라 이전
  session에서 저장된 setting**도 반환한다.
- `SetPermissionStateAsync(Default)`는 **profile에 저장된 state를 지우고
  default behavior로 복원**한다. 단순히 Deny를 쓰는 것과 다르다.
- `SetPermissionStateAsync`의 origin은 valid scheme/host가 필요하고 path/
  fragment는 무시된다. invalid origin은 실패한다.
- `GetNonDefaultPermissionSettingsAsync`/`SetPermissionStateAsync`는 비동기
  완료 handler를 사용하며, 실패는 handler의 error code로 전달된다.
- profile permission state는 같은 profile의 모든 WebView2가 공유한다.
- API는 cookie, history, Vault secret, 전체 browsing data를 반환/삭제하지
  않는다.
- API가 없는 구버전 runtime에서는 호출이 `E_NOINTERFACE`/`E_NOTIMPL`/
  `NotSupportedException` 등으로 실패할 수 있다. 이 경우 audit는
  `Unsupported`로 전환하고 fail closed한다.

**중요:** Phase 2.1은 `SavesInProfile=false`로 새 결정을 WebView2 profile에
저장하지 않았다. 그러나 Phase 2.1 이전의 WebView2 기본 prompt/다른 도구가
남긴 non-default setting은 **event를 발생시키지 않고 자동 적용될 수 있다**.
Phase 2.1.1의 audit는 이 상태를 startup에서 제거한다.

---

## 4. 기존 permission state 실제 조회 범위

Windows probe `webview2:permission_reconciliation`:

```text
profile = 새 임시 WebView2 profile
origin  = https://amare-permission-probe.test

for each of 13 named kinds:
  SetPermissionStateAsync(kind, origin, Allow)

GetNonDefaultPermissionSettingsAsync()
  → before = 13

for each returned setting:
  SetPermissionStateAsync(kind, origin, Default)

GetNonDefaultPermissionSettingsAsync()
  → after = 0
```

결과:

```text
PASS webview2:permission_reconciliation
all 13 named permission kinds accepted a legacy allow and were reset;
before=13 after=0
```

- 13개 kind: Microphone, Camera, Geolocation, Notifications, OtherSensors,
  ClipboardRead, MultipleAutomaticDownloads, FileReadWrite, Autoplay,
  LocalFonts, MidiSystemExclusiveMessages, WindowManagement, PersistentStorage.
- `Allow` 13개를 실제 profile에 저장한 뒤 collection에서 13개를 확인했고,
  `Default` reset 후 0개가 남았다.
- 같은 origin의 서로 다른 kind, 다른 origin, subdomain, scheme/port, IDN은
  Core 정책 테스트로 분리했다.
- `UnknownPermission`은 named 13개에 포함하지 않았으며, audit에서 만나면
  Unknown으로 처리해 reset/fail closed한다.

---

## 5. Startup audit 설계

### 5.1 실행 시점

audit는 **page navigation 전**에 실행한다.

```text
CreateTabAsync
  → EnsureCoreWebView2Async(environment)
  → EnsurePermissionAuditAsync(profile)
  → ConfigureWebViewVaultAsync
  → AttachWebViewEvents
  → Navigate / ShowNewTabPage
```

- 첫 tab, session restore tab, popup/new window, 새 profile의 첫 tab 모두
  같은 경로다.
- Reader WebView는 `EnsureReaderAsync`에서 audit 후 deny-all handler를
  attach한다.
- AI WebView는 별도 `ai-webview2` environment/profile을 audit한 뒤 deny-all
  handler를 attach한다.
- audit가 `Reconciled`가 아니면 `PermissionRequested` handler는 모든 요청을
  deny한다.
- audit 자체는 WebView2 profile API만 사용하며 cookie/history/Vault API를
  호출하지 않는다.

### 5.2 상태

```text
NotStarted
Auditing
Reconciled
Failed
Unsupported
```

| 상태 | permission 요청 | 설명 |
|---|---|---|
| NotStarted | **Deny** | audit 전 |
| Auditing | **Deny** | audit 진행 중 |
| Reconciled | 정상 Phase 2.1 흐름 | legacy state 제거 완료 |
| Failed | **Deny** | API/Store/verify 실패, 재시도 가능 |
| Unsupported | **Deny** | API 미지원 runtime, fail closed |

`PermissionAuditGate.CanServePermissionRequests`는 **Reconciled만 true**다.

### 5.3 profile별 coordinator

- `Dictionary<string, PermissionAuditEntry>`를 process static + lock으로
  관리한다.
- key는 AMARE profile ID, AI는 `ai-webview2`다.
- 같은 profile의 두 번째 WebView/창은 진행 중 task를 await한다. 중복 audit를
  시작하지 않는다.
- `Auditing`/`Reconciled`는 기존 task를 재사용한다.
- `Failed`는 30초 cooldown 후 새 WebView 생성 시 재시도하고, 앱 재시작 시
  항상 새로 시도한다. `Unsupported`는 같은 session에서 재시도하지 않는다.
- profile 삭제 시 entry를 제거한다.
- audit 중 profile switch가 일어나면 이전 profile의 task는 계속될 수 있으나,
  새 profile은 별도 key로 audit한다. 이전 task의 결과는 현재 profile 요청에
  사용되지 않는다.

### 5.4 테스트 전용 seed

UI smoke가 legacy state를 재현할 수 있도록 test-only hook을 추가했다.

```text
AMARE_TEST_VAULT_ROOT가 설정된 경우에만 동작
AMARE_TEST_LEGACY_PERMISSION = <kind>|<origin>|allow|deny
```

- audit 직전에 `SetPermissionStateAsync`로 legacy state를 만든다.
- profile당 한 번만 seed한다.
- production에서는 `AMARE_TEST_VAULT_ROOT`가 없으므로 inert다.

---

## 6. Reconciliation 정책

### 6.1 결정

각 non-default setting에 대해:

1. origin과 kind를 읽는다.
2. AMARE SQLite의 exact origin/kind 정책을 조회한다.
3. WebView2 state와 AMARE state를 audit log에 기록한다.
4. **WebView2 state를 `Default`로 reset**한다.
5. AMARE handler가 다음 요청에서 다시 판단한다.

핵심 결정:

- **모든 non-default WebView2 state를 Default로 reset**한다.
  - legacy Allow를 AMARE의 "Always allow"로 승격하지 않는다.
  - legacy Deny를 AMARE의 "Block always"로 승격하지 않는다.
  - WebView2에 남은 state가 AMARE store를 덮어쓰거나 event를 가로채지
    못하게 한다.
- AMARE SQLite row는 **읽기만** 하고 변경하지 않는다.
- AMARE 명시적 Allow는 reset 후 다음 `PermissionRequested`에서 handler가
  store를 조회해 적용한다. 이는 "현재 요청에서 AMARE 승인 절차에 따라
  허용"과 일치한다.
- AMARE 명시적 Deny는 reset 후 handler가 deny한다.
- AMARE 정책이 없는 origin은 reset 후 일반 Phase 2.1 consent dialog를 거친다.
  legacy Allow로 자동 허용되지 않는다.
- unknown kind/invalid origin/invalid AMARE decision은 reset 대상이며,
  reset API가 실패하면 audit `Failed`로 전환한다.

### 6.2 exact matching

`WebViewPermissionReconciliation.Plan`은 다음을 exact 비교한다.

- HTTPS scheme
- hostname (IDN → punycode lowercase)
- port (default 443 정규화)
- permission kind
- subdomain 비상속
- lookalike/Unicode host 분리
- userinfo/trailing dot/invalid origin 거부

`http`, `data:`, `file:`, opaque origin은 AMARE 정책과 매칭하지 않는다.
legacy setting이 이런 origin이면 Default reset을 시도하고, 실패하면 audit
Failed로 fail closed한다.

### 6.3 왜 Deny를 쓰지 않고 Default로 reset하는가

- `SetPermissionStateAsync(Deny)`는 profile에 deny를 영구 저장한다.
  이후 사용자가 AMARE 설정에서 Allow로 바꿔도 WebView2가 event를 발생시키지
  않아 AMARE 변경이 적용되지 않을 수 있다.
- `Default`는 저장된 state를 지우고 **다음 요청에서 `PermissionRequested`가
  다시 발생**하게 한다. 따라서 AMARE SQLite가 유일한 source of truth가 된다.
- unknown/invalid state도 Default reset으로 제거한다. API가 거부하면
  audit Failed/Unsupported로 fail closed한다.

---

## 7. 사용자 데이터 영향

- 변경 대상: **WebView2 profile의 non-default permission setting만**.
- 변경하지 않는 것:
  - cookie / localStorage / IndexedDB / cache
  - browsing history
  - password / Vault secret
  - downloads
  - New Tab provenance
  - AMARE SQLite permission row
- `ClearBrowsingDataAsync`를 permission reset에 사용하지 않는다.
- audit log 항목:
  - profile key
  - normalized origin
  - permission kind
  - 기존 WebView2 state
  - AMARE decision (allow/deny/none)
  - action/reason
  - API 성공/실패와 오류 유형
  - timestamp는 AppLog line 시각
- log에 password, cookie, history, Vault secret, payload를 남기지 않는다.
- 사용자는 Settings → Site permissions의
  `SitePermissionAuditState`에서 audit 상태와 reset 수를 확인할 수 있다.

예:

```text
Legacy WebView2 audit: Reconciled (1 legacy setting(s) reset).
Legacy WebView2 audit: Failed. Permission requests are blocked; the audit will retry on the next app start.
```

---

## 8. 동시성 / lifecycle

| 상황 | 처리 | 검증 |
|---|---|---|
| 동일 profile 두 창 동시 시작 | per-profile static task 공유, 중복 audit 없음 | 코드; strict 동시 시작 UI는 UNVERIFIED |
| audit 중 새 WebView 생성 | 진행 중 task await, navigation 전 완료 | 코드 |
| audit 중 profile 전환 | 새 profile은 별도 key로 audit; 이전 결과 무시 | 코드; UI profile isolation PASS |
| audit 중 창 닫힘 | task는 실패/무효화될 수 있고 요청은 deny | 코드 |
| audit API 실패 | `Failed`, 모든 요청 deny, 30초/앱 재시작 후 재시도 | UI failure injection PASS |
| WebView2 Environment 재사용 | 같은 profile key로 audit 1회 | 코드 |
| 앱 재시작 후 반복 | static state 초기화, 새 audit | UI restart에서 seed/reset PASS |
| audit와 권한 요청 경합 | audit state가 Reconciled가 아니면 deny | gate unit + failure UI PASS |

- audit는 **window UI thread**에서 시작되지만 WebView2 async API를 await한다.
- 같은 profile의 audit task는 process 내에서 공유한다. **별도 process의
  동시 실행은 조율하지 않는다** (아래 제한).
- audit 실패 상태를 성공으로 cache하지 않는다. `Failed`는 재시도하고,
  `Unsupported`는 session 동안 유지한다.

---

## 9. 테스트 결과

### 9.1 Windows / Linux

| Suite | 결과 |
|---|---|
| WinUI UI smoke | **73/73 PASS** (기존 64 + Phase 2.1 7 + 2.1.1 2) |
| C# ↔ Rust interop | **103/103 PASS** (기존 100 + Phase31 3) |
| Windows 통합 검증 | **172 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** (188 total) |
| `webview2_permission_reconciliation` | **PASS** (13 kinds Allow → reset → 0) |
| Rust FFI | **14/14 PASS** |
| Rust site permissions | **5/5 PASS** |
| Rust services | **38/38 PASS** |
| Rust storage | **13/13 PASS** |
| Rust session windows | **10/10 PASS** |
| Rust vault (Windows) | **25/25 PASS** |
| Linux CI | **SUCCESS** (interop 103/103, vault 24/24) |
| Package/MSIX | **SKIPPED (8)**, 생성 없음 |

최종 Windows run: [45760634957](https://github.com/thfzm/AMARE/actions/runs/45760634957)
(`d41ee59`)

최종 Linux run: [45753322439](https://github.com/thfzm/AMARE/actions/runs/45753322439)
(`d41ee59`)

### 9.2 신규 Core / Interop test

```text
PASS permission_audit_exact_origin_policy
  IDN / port / subdomain / kind / scheme exact matching
PASS permission_audit_fail_closed_policy
  legacy allow/deny, no policy, unknown kind/state, invalid AMARE decision
PASS permission_audit_gate
  only Reconciled serves permission requests
```

### 9.3 신규 UI test

```text
PASS site_permissions_legacy_audit
  seed legacy WebView2 Allow
  → audit text Reconciled + 1 reset
  → AMARE consent dialog still appears
PASS site_permissions_audit_failure_fail_closed
  test-only audit failure
  → settings text Failed
  → permission request denied without dialog
```

기존 Phase 2.1 site permissions 7개와 Vault/New Tab/navigation 회귀도 모두
PASS했다.

### 9.4 PASS / UNVERIFIED / BLOCKED / SKIPPED

**PASS**

- `GetNonDefaultPermissionSettingsAsync`로 기존 non-default state 조회
- 13개 named kind의 Allow seed → `Default` reset → collection 0
- startup audit가 첫 page navigation 전에 실행
- legacy Allow가 AMARE consent를 우회하지 못함
- legacy state와 AMARE SQLite exact origin/kind 비교
- AMARE Allow/Deny/없음/unknown/invalid 정책의 fail-closed planning
- audit failure 시 설정 상태 표시 + 권한 요청 deny
- profile별 audit, 재시작 반복, profile 전환 시 key 분리
- 기존 Site Permissions / Vault / New Tab / navigation 회귀
- Windows/Linux CI

**UNVERIFIED**

- 실제 Windows 11 workstation에서의 upgrade/startup (CI는 Windows Server 2025)
- 실제 Windows 11의 이전 AMARE/WebView2 profile upgrade
- 별도 process 두 개가 같은 profile을 동시에 audit하는 cross-process 조율
- 완전히 동시에 시작하는 두 창의 strict audit race (process 내 coordinator는
  코드/로그로 확인)
- audit 도중 창 강제 종료/프로세스 crash
- 30초 retry/장시간 반복 audit의 soak
- API가 unsupported일 때 legacy Allow가 event 없이 적용되는 경우의 완전 차단
  (API 없이는 가로챌 수 없음)

**BLOCKED**

- 없음 (지원 runtime 157에서는 13개 kind 모두 reset PASS).
- 단, API 미지원 runtime은 `Unsupported` + fail closed이며, 그 runtime의
  legacy setting을 **완전히 제거할 수단이 없으므로** 해당 환경은 BLOCKED로
  취급해야 한다. 현재 CI runtime에서는 발생하지 않았다.

**SKIPPED**

- 패키지 8단계 (MSIX/portable/release metadata/preflight)
- Netflix/Disney 유료 서비스 probe

---

## 10. 최초 실패 및 수정 이력

| # | run | 실패 | 분류 | 수정 |
|---:|---|---|---|---|
| 1 | [45746773793](https://github.com/thfzm/AMARE/actions/runs/45746773793) | `TestVaultRootEnv` duplicate constant, XAML cascade | PRODUCT_BUILD_DEFECT | 기존 constant 재사용 |
| 2 | [45747921579](https://github.com/thfzm/AMARE/actions/runs/45747921579) | `PermissionAuditGate` field가 type을 shadowing, XAML cascade | PRODUCT_BUILD_DEFECT | lock field를 `PermissionAuditSync`로 rename |
| 3 | [45749049523](https://github.com/thfzm/AMARE/actions/runs/45749049523) | 첫 green (probe 2 kinds, UI 72/72) | — | 증거 보존 |
| 4 | [45751281117](https://github.com/thfzm/AMARE/actions/runs/45751281117) | audit failure UI test 추가 후 green (probe 2 kinds) | — | 증거 보존 |
| 5 | [45753319459](https://github.com/thfzm/AMARE/actions/runs/45753319459) | 13-kind probe PASS, UI `new_window_button` flake | PRE-EXISTING UI FLAKE | assertion 약화 없이 재실행 |
| 6 | [45755189976](https://github.com/thfzm/AMARE/actions/runs/45755189976) | UI `certificate_error_retry`, `new_window_button` flake | PRE-EXISTING UI FLAKE | 재실행 |
| 7 | [45757037180](https://github.com/thfzm/AMARE/actions/runs/45757037180) | UI `external_data_navigation_blocked` flake | PRE-EXISTING UI FLAKE | 재실행 |
| 8 | [45758684436](https://github.com/thfzm/AMARE/actions/runs/45758684436) | UI `webview_focus_shortcuts` flake | PRE-EXISTING UI FLAKE | 재실행 |
| 9 | [45760634957](https://github.com/thfzm/AMARE/actions/runs/45760634957) | 최종 재실행: 172/0/8/8, UI 73/73, interop 103/103, probe PASS | — | 최종 증거 |

- compile failure log:
  `artifacts/windows/phase2.1.1-permission-reconciliation/compile-failures.log`
- 중간/flake/final 증거:
  `artifacts/windows/phase2.1.1-permission-reconciliation/`
- flake는 모두 Phase 2.1.1 변경과 무관한 기존 UI test이며, 동일 HEAD 재실행
  에서 73/73 PASS를 확인했다. assertion을 약화하지 않았다.

---

## 11. 변경 파일 / 커밋

### 제품

- `apps/windows/AMARE.Windows.Core/SitePermissionAudit.cs` (신규)
  - audit state, legacy state, reconciliation planner, fail-closed gate
- `apps/windows/AMARE.Windows/MainWindow.PermissionAudit.cs` (신규)
  - profile별 coordinator, WebView2 audit/reset, logging, test seed
- `apps/windows/AMARE.Windows/MainWindow.Permissions.cs`
  - audit gate, settings audit 상태 표시
- `apps/windows/AMARE.Windows/MainWindow.xaml.cs`
  - 첫 tab/Reader/AI audit 연결, profile 삭제 시 entry 제거

### 테스트 / 검증

- `apps/windows/AMARE.Interop.Tests/Phase31Tests.cs` (신규, 3 tests)
- `apps/windows/AMARE.Interop.Tests/Program.cs`
- `tools/windows-ui-smoke/Program.cs` (+2 UI tests, test seed, restart helper)
- `tools/windows-verify/Probes.cs` (`webview2_permission_reconciliation`)
- `tools/windows-verify/VerifyForm.cs`

### 주요 commit

```text
a3dd019 Audit and reconcile legacy WebView2 permission settings
a65d604 Reuse the existing WebView2 test root constant
41b1b93 Rename audit lock to avoid type shadowing
cc8429d Test fail-closed behavior after a legacy audit failure
d41ee59 Probe all named WebView2 permission kinds during reconciliation
```

---

## 12. 알려진 한계 / 남은 보안 제약

1. **구버전 WebView2 runtime**: profile permission API가 없으면 audit는
   `Unsupported`로 fail closed한다. 그러나 API 없이는 profile에 남은 legacy
   Allow가 event 없이 적용되는 것을 **완전히 막을 수 없다**. 지원 runtime을
   요구하거나, 미지원 시 사용자에게 명시적 경고를 보여야 한다.
2. **실제 Windows 11 upgrade**: CI는 Windows Server 2025다. 실제 Windows 11의
   이전 AMARE profile/WebView2 data upgrade는 UNVERIFIED다.
3. **cross-process**: static coordinator는 같은 process의 창만 조율한다.
   별도 AMARE process가 같은 profile을 동시에 열면 audit가 중복/경합할 수
   있다. 단일 인스턴스 정책 또는 cross-process lock은 후속 작업이다.
4. **strict race**: audit 중 새 WebView/프로필 전환/창 닫힘은 fail closed
   코드로 처리하지만, 모든 timing window를 UI 자동화로 재현하지는 못했다.
5. **retry/soak**: 실패 후 30초 cooldown 및 앱 재시작 재시도는 코드/UI
   injection으로 확인했지만, 장시간 반복/강제 종료 soak은 UNVERIFIED다.
6. **unknown kind**: audit에서 unknown kind를 만나면 Default reset을 시도하고,
   실패 시 audit Failed로 전체 권한 요청을 deny한다. unknown kind의 실제
   WebView2 동작은 runtime 의존이다.
7. **user consent**: audit는 non-default setting을 startup에서 reset하지만,
   별도 확인 dialog를 띄우지 않는다. 변경 항목은 per-setting log와 Settings의
   audit summary로 확인 가능하다. 광범위한 browsing data 삭제는 하지 않는다.
8. **test-only hook**: `AMARE_TEST_VAULT_ROOT`가 설정된 경우에만 seed가
   동작한다. 일반 실행에서는 inert다.
9. **DB schema**: 변경 없음. AMARE SQLite 정책은 audit에서 read-only다.
10. **AI/Reader**: audit 후에도 기존 deny-all 정책을 유지한다. AI는 별도
    environment/profile key로 audit한다.

---

## 13. 결론 및 다음 단계

- Phase 2.1.1의 핵심 보안 과제는 **PASS**다.
  - 기존 WebView2 non-default permission state를 실제 API로 조회
  - 13개 named kind Allow seed → `Default` reset → 0 확인
  - startup에서 page navigation 전 audit
  - legacy Allow가 AMARE consent를 우회하지 못함
  - audit failure 시 설정 상태 표시 + 모든 권한 요청 deny
  - AMARE SQLite는 read-only source of truth로 유지
- Windows **172/0/8/8**, UI **73/73**, interop **103/103**, Linux **SUCCESS**.
- 실제 Windows 11, cross-process 동시성, strict lifecycle race, 구버전 runtime은
  UNVERIFIED/제한으로 남긴다.

다음 단계는 이 보고서를 확인한 뒤 결정한다. 패키지/MSIX/서명/공개 배포는
수행하지 않았다.
