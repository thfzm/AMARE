# AMARE Phase 1.3C.1 — New Tab Internal Navigation Fix Report

- 상태: **PASS** (Windows/Linux CI). 실기기·수동 항목은 UNVERIFIED로 구분.
- 작업 branch: `fix/new-tab-internal-navigation`
- 기준 branch: `feature/phase-1-vault-ui`
- 기준 제품/테스트 HEAD: `8076759`
- 기준 문서 HEAD: `ff7de74`
- 최종 제품/테스트 HEAD: **`7f6a1d8`**
- 문서 HEAD: 이 보고서를 포함한 문서 commit
- 범위: New Tab 내부 문서 탐색 경로만 수정. Vault 암호화/ABI/DB schema/GPUI/Phase 1.3D 변경 없음.

---

## 1. Git / 무결성

| 항목 | 결과 |
|---|---|
| 시작 branch | `feature/phase-1-vault-ui` (`ff7de74`) |
| 작업 branch | **`fix/new-tab-internal-navigation`** |
| 최종 제품/테스트 HEAD | **`7f6a1d8`** |
| Working tree | clean (문서 commit 전) |
| Phase 1.3D branch | **`feature/phase-1-vault-webview` = `c0138ab`, 보존** |
| Phase 1.3D push/rewrite | **없음** |
| GPUI 변경 | **없음** |
| Vault 암호화 / ABI / DB schema | **변경 없음** (schema v5) |
| 패키지/MSIX/서명/Release | **없음** |

Phase 1.3D branch의 diff는 확인만 했고, checkout/merge/cherry-pick/rewrite하지 않았다.

---

## 2. Root Cause (확인된 코드 경로)

1. `MainWindow.ShowNewTabPageAsync`가 `NewTabPageBuilder.Build(...)`로 로컬 HTML을
   만들고 `WebView2.NavigateToString(html)`을 호출한다.
2. 이 WebView2 버전(`Microsoft.Web.WebView2 1.0.4314.35`)은 `NavigateToString`을
   `NavigationStarting`의 `data:text/html;charset=utf-8,<HTML>` 탐색으로 노출한다.
   (실측 길이 4,604~4,860자)
3. `AttachWebViewEvents`의 `NavigationStarting`이 `NavigationPolicy.Decide`를
   호출하고, `data:`는 `NavigationDecision.Block`이다.
4. 따라서 `args.Cancel = true`로 내부 문서가 취소되어 New Tab이 빈 화면이 되고,
   `StatusText.Text = "Blocked navigation: " + args.Uri`가 전체 data URI를 기록해
   상태 표시줄을 4.6KB 문자열로 덮어썼다.
5. Phase 1.3C의 profile 전환 테스트는 이 상태 문자열을 기다리다 실패했고, 원인
   분석 중 위 결함이 확인됐다.

기존 증거:

- `artifacts/windows/phase1.3c/lock-verification/root-cause-45457832491/`
  — `AttachWebViewEvents`의 `StatusText.Text` stack trace, `scheme=data length=4604`
- `artifacts/windows/phase1.3c/lock-verification/README.md`

현재 코드와 증거는 일치한다.

---

## 3. 설계 선택

우선순위에 따라 다음을 검토했다.

| 후보 | 판단 |
|---|---|
| `SetVirtualHostNameToFolderMapping` / `WebResourceRequested`로 내부 문서 제공 | **제외**. 내부 origin이 실제 탐색/요청 표면이 되어 외부 페이지가 요청할 수 있고, 결국 provenance 플래그가 필요하다. origin 격리 위험이 커진다. |
| `about:blank` + `ExecuteScriptAsync`로 HTML 주입 | **제외**. document lifecycle/script 실행 시점이 불안정하고, 검증·유지보수가 어렵다. |
| custom scheme 등록 | **제외**. environment 등록과 URL 표면이 추가되며 provenance 문제는 동일하다. |
| **`NavigateToString` 유지 + 1회성·정확한 payload provenance** | **채택**. 기존 내부 문서 생성 경로를 그대로 쓰면서, 일반 `NavigationPolicy`는 전혀 느슨하게 하지 않는다. |

핵심은 “URL prefix 허용”이 아니라 **AMARE가 방금 생성한 문서와 byte-for-byte
동일한 payload만 1회 허용**하는 것이다. 조건을 만족하지 못하면 fail closed 한다.

---

## 4. 내부 문서 provenance

새 파일 `apps/windows/AMARE.Windows.Core/NewTabDocumentNavigation.cs`:

### 4.1 `InternalDocumentNavigation` (WebView 인스턴스별 상태)

- `Arm(html)`: `NavigateToString` **직전**에만 호출.
- `TryAccept(uri)`: pending을 **먼저 소비**한 뒤 payload를 검증.
  - 성공/실패와 무관하게 pending은 제거 → 재사용 불가.
- `Disarm()`: 명시적 `Navigate`, `NavigationCompleted`, `CloseHost`,
  `NavigateToString` 예외 시 제거.
- `HasPending`: 진단/검증용.

### 4.2 `NewTabDocumentPolicy.Matches(uri, expectedHtml)`

- `data:`만 대상.
- header allowlist:
  - `data:text/html`
  - `data:text/html;charset=utf-8`
  - `data:text/html;base64`
  - `data:text/html;charset=utf-8;base64`
- payload를 raw / percent-decoded / base64-decoded로 해석해 **전체 문자열이
  expected HTML과 정확히 일치**할 때만 true.
- payload 크기 상·하한으로 비정상/DoS성 입력을 조기 거부.
- URL prefix(예: `data:text/html`)만으로는 절대 통과하지 않는다.

### 4.3 외부 페이지의 우회 가능성

- pending은 `ShowNewTabPageAsync`의 같은 UI thread에서 `Arm` 직후
  `NavigateToString`으로 이어지며, 그 사이에 외부 script가 실행될 틈이 없다.
- 외부 페이지는 사용자의 top sites와 WebView별 nonce가 포함된 정확한 HTML을
  알 수 없고, pending을 재사용할 수도 없다(1회 소비).
- 다른 탭/창은 각자 `InternalDocumentNavigation`을 가지므로 예외가 전파되지 않는다.
- 조건을 확인할 수 없으면 `NavigationPolicy`가 기존대로 `data:`를 차단한다.

---

## 5. 보안 정책

| 입력 | 결과 |
|---|---|
| AMARE가 생성한 New Tab 문서와 정확히 일치하는 `data:text/html` | **1회 허용** (해당 WebView만) |
| 그 외 모든 `data:` | **Block** (기존과 동일) |
| `javascript:` / `vbscript:` / `file:` | **Block** (기존과 동일) |
| `blob:` 및 기타 scheme | **External** (기존과 동일, 허용 범위 확대 없음) |
| `http:` / `https:` / `about:` / `amare:` | **Allow** (기존과 동일) |
| 잘못된 URI | **Block** (기존과 동일) |

추가:

- 차단 상태 메시지는 scheme만 표시한다
  (`Blocked navigation: data URL` 등). URI/HTML/쿼리를 기록하지 않는다.
- New Tab 내부 문서에서는 `Model.Url`을 비우고 `IsNewTabPage`를 유지해
  주소 표시줄에 data URI가 노출되지 않는다.
- `_blank`/`window.open`: http(s)는 기존처럼 새 탭, 그 외는 기존 확인 흐름 유지.
- iframe/frame 탐색, 다운로드, 인증서 오류, 리디렉션 정책은 변경하지 않았다.

---

## 6. 변경 파일

- `apps/windows/AMARE.Windows.Core/NewTabDocumentNavigation.cs` (신규)
- `apps/windows/AMARE.Windows/MainWindow.xaml.cs`
- `apps/windows/AMARE.Interop.Tests/Phase29Tests.cs` (신규)
- `apps/windows/AMARE.Interop.Tests/Program.cs`
- `tools/windows-verify/LocalHttpServer.cs` (테스트 페이지/요청 추적)
- `tools/windows-ui-smoke/Program.cs`

`NavigationPolicy.cs`는 **수정하지 않았다**.

---

## 7. 테스트

### 7.1 Unit / Core (C#)

`Phase29Tests` (Windows/Linux 공통):

```text
PASS new_tab_document_policy_accepts_exact_payload
PASS new_tab_document_policy_rejects_foreign_payloads
PASS internal_document_navigation_is_one_shot
PASS navigation_policy_keeps_data_family_blocked
```

검증 내용:

- raw / percent-encoded / base64 / 대소문자 header의 정확한 payload 허용
- prefix-only, truncated, appended, 다른 HTML, `data:image`, `data:text/plain`,
  알 수 없는 parameter, 잘못된 base64, `javascript:`, `file:`, `blob:`, `https:`,
  null/빈 expected, trailing space 거부
- pending 1회 소비, 실패 후 재사용 불가, Disarm, 빈 HTML arm 거부
- `data`/`javascript`/`vbscript`/`file` Block, `blob` External, http(s) Allow,
  `CanOpenInNewTab`의 data/blob 거부

### 7.2 UI smoke (실제 WebView2)

```text
PASS new_tab_internal_page
PASS new_tab_address_search
PASS external_data_navigation_blocked
PASS external_blank_popup
PASS iframe_child_navigation
PASS external_http_redirect
```

| 테스트 | 내용 | 결과 |
|---|---|---|
| `new_tab_internal_page` | 새 탭 2회 반복 렌더링(Find in page로 `Frequently visited` 확인), 주소 비움, 상태에 data/HTML 없음, New Tab에서 example.com 주소 입력, 원래 탭 복귀 | **PASS** |
| `new_tab_address_search` | New Tab에서 주소창 검색 → DuckDuckGo URL로 탐색 | **PASS** |
| `external_data_navigation_blocked` | 외부 HTTP 페이지가 script로 `data:text/html` 이동 시도 → 페이지 유지, payload(`amare-data-payload`) 미표시, 주소/상태에 긴 URI 없음, 이후 HTTPS 정상 | **PASS** |
| `external_blank_popup` | 외부 페이지의 `window.open(...,'_blank')` → 새 탭/Popup 로드, 서버 요청 확인, 정리 | **PASS** |
| `iframe_child_navigation` | same-origin iframe child(`/page2.html`) 요청/로드 | **PASS** |
| `external_http_redirect` | HTTP 302 `/redirect` → `/page2.html` 추적, Page Two 렌더링 | **PASS** |

### 7.3 회귀

| Suite | 결과 |
|---|---|
| WinUI UI smoke | **60/60 PASS** (기존 54 + 신규 6) |
| C# <-> Rust interop | **84/84 PASS** (기존 80 + Core 4) |
| Rust ABI 1 / ABI 2 | **7/7 / 6/6 PASS** |
| Linux / Windows vault core | **24/24 / 25/25 PASS** |
| Windows CI | **151 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** |
| Linux CI | **SUCCESS** |
| Package/MSIX | **SKIPPED (7)**, 생성 없음 |

기존 Vault UI smoke 5개, 주소/탭/멀티윈도우/세션/인증서 테스트가 모두 유지됐다.

### 7.4 UNVERIFIED / SKIPPED

**UNVERIFIED**

- UI 수준의 `javascript:`/`file:` 차단 (Core unit으로만 검증; NavigationPolicy
  변경 없음)
- cross-origin iframe 정책의 신규 실기기 검증 (코드 경로 변경 없음)
- 실제 Windows 11 수동 세션에서의 IME/DPI/접근성
- 향후 WebView2 버전이 `NavigateToString`을 다른 인코딩으로 구현할 경우
  (현재 정책은 fail closed; 자동 회귀는 현재 버전에서만 확인)
- 외부 `data:` 탐색을 Chromium 자체가 먼저 차단하는 경우, 차단 계층이
  AMARE인지 Chromium인지 UI만으로 구분하지 않음 (동작/비노출은 PASS)

**SKIPPED**

- 패키지 7단계, Netflix/Disney 유료 서비스 1단계 (기존과 동일)

---

## 8. 최초 실패와 수정 이력

| # | 실패 | 분류 | 수정 |
|---:|---|---|---|
| 1 | `external_data_navigation_blocked`/`external_blank_popup`/`iframe_child_navigation` timeout | TEST_AUTOMATION_BUG | UIA `ValuePattern`+Go가 `AutoSuggestBox.Text`를 커밋하지 못함 → 주소창에 실제 타이핑 후 Go |
| 2 | 서버 요청은 발생하나 제목 검증 timeout | TEST_AUTOMATION_BUG | `Process.MainWindowTitle`/custom title bar가 stale → Find in page 기반 **문서 내용** 검증으로 교체 |
| 3 | `AssertPageDoesNotContain`가 `0/0`만 기대 | TEST_AUTOMATION_BUG | 로컬라이즈된 `No results`도 허용 |
| 4 | `webview_focus_shortcuts`/`tear_off_tab`/`session_multiwindow_restore` 간헐 실패 | PRE-EXISTING UI FLAKE | 동일 HEAD 재실행 60/60 PASS, 실패 로그 보존 |

최초 실패 증거:

- Windows 최초: [45548033165](https://github.com/thfzm/AMARE/actions/runs/45548033165)
- 진단/중간 실패: `artifacts/windows/phase1.3c1-new-tab/` (flake 로그 포함)
- 최종: 아래 CI 결과

참고(범위 밖 관찰): 페이지 제목 변경 시 `Window.Title`은 갱신되지만 커스텀
`TitleBarTitle`은 탭 선택 시에만 갱신된다. 이번 수정과 무관한 기존 UI 사항이며
변경하지 않았다.

---

## 9. CI 결과

| 항목 | 결과 |
|---|---|
| Windows 최종 | [45566053292](https://github.com/thfzm/AMARE/actions/runs/45566053292) — **151 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** |
| WinUI UI smoke | **60/60 PASS** |
| C# interop | **84/84 PASS** |
| Linux 최종 | [45566089795](https://github.com/thfzm/AMARE/actions/runs/45566089795) — **SUCCESS** (vault 24/24, interop 84/84) |

증거: `artifacts/windows/phase1.3c1-new-tab/` (gitignored, local evidence)

- `final-45566053292/`
- `final-45564113928/` (redirect test 추가 전 동일 green)
- `flake-45562303100/`
- `first-failure-45548033165/`
- `README.md`, `SHA256SUMS.txt`

---

## 10. Phase 1.3D 보존 / 다음 단계

- `feature/phase-1-vault-webview` = `c0138ab` 그대로 보존. push/rewrite/삭제 없음.
- Phase 1.3D 코드는 시작하지 않았다.
- 다음 단계 프롬프트는 이 보고서 확인 후 별도로 제공한다.

권장 후속:

1. Phase 1.3D WebView2 login capture/autofill 착수 여부 결정.
2. 커스텀 title bar 제목 갱신(관찰 항목)은 별도 소규모 수정으로 처리.
3. 실제 Windows 11에서 New Tab 반복 생성/검색/iframe/manual 확인.

---

## 11. 결론

- **New Tab 내부 페이지 정상 표시: PASS**
- **외부 임의 `data:` 차단 및 비노출: PASS**
- **기존 navigation policy 보안 회귀 없음: PASS**
- **핵심 탐색 및 Vault UI 회귀: PASS**
- Windows **151/0**, UI **60/60**, interop **84/84**, Linux **SUCCESS**
- 실패/검증 불가 항목은 위 7.4절에 UNVERIFIED로 구분했다.
