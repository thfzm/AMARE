# AMARE Phase 2.3 — Settings & Localization Completion 보고서

- 상태: **PASS (CI)**. 실제 Windows 11 Korean IME/DPI/layout는 UNVERIFIED.
- 작업 branch: `feature/phase-2-settings-localization`
- 기준 브랜치: `feature/phase-2-ai-api`
- 기준 제품/테스트 HEAD: **`a8d1747`**
- 기준 문서 HEAD: **`b3992ca`**
- 최종 제품/테스트 HEAD: **`e548a1f`**
- 문서 HEAD: 이 보고서를 포함한 문서 commit
- Working tree: **clean** (문서 commit 후)
- DB schema 변경: **없음** (v6 유지)
- 패키지/MSIX/코드 서명/공개 Release: **없음**

---

## 1. 최종 요약

| 완료 기준 | 판정 | 근거 |
|---|---|---|
| Settings 기능 전체 감사 | **PASS** | General/Appearance/Privacy/AI/Downloads/Updates 분류 |
| 실제 구현 기능과 Settings UI 연결 | **PASS** | theme/engine/session/history/login toggles/site/Vault/AI/download/update |
| 한국어/영어 리소스 완전성 | **PASS (Core)** | 253 keyed + 82 static strings, placeholder 일치 test |
| 설정 저장 및 재시작 복원 | **PASS** | UI `settings_persistence_restart` |
| 프로필/앱/창 scope 구분 | **PASS** | SQLite profile settings + app JSON + Credential Manager |
| 접근성 기본 구조 | **PASS (code) / UNVERIFIED (실기기)** | Automation ID, masking, standard focus |
| Windows/Linux CI | **PASS (기록)** | Windows 182/0/8/8, UI 76/76, interop 113/113, Linux SUCCESS |
| 실제 Windows 11 | **UNVERIFIED** | CI는 Windows Server 2025 |

**BLOCKED로 남긴 기능은 없다.** 다만 UI font/size, tab density, default browser,
home/new-tab behavior, tracking/telemetry처럼 **구현되지 않은 설정은 UI에
노출하지 않았다.**

---

## 2. Git 기준 / branch

```text
feature/phase-2-ai-api
  a8d1747  Persist AI key rejection state in settings
  b3992ca  Document Phase 2.2 actual AI API integration

feature/phase-2-settings-localization (신규)
  e548a1f  Keep Settings UI consistent when app-wide saves fail
  c8dcdfb  Clarify app-wide language scope in Settings
  c6787a2  Use non-sensitive login setting keys for privacy toggles
  29c2f91  Use non-sensitive setting keys for credential toggles
  d4c014a  Add settings audit UI, localization tests and restart persistence
  979a906  Remove unsupported UI font settings from Settings
  cfb56ae  Expand settings and add Korean/English localization
```

- `git merge-base --is-ancestor a8d1747 HEAD`로 기준 제품/테스트 HEAD 포함을
  확인했다.
- Phase 2.2 구현/문서는 변경·삭제하지 않았다.
- GPUI, Vault 암호화/credential 저장, Site Permissions 보안 정책, AI WebView
  격리는 변경하지 않았다.
- force push/history rewrite를 하지 않았다.
- DB schema는 v6 그대로다. 설정 정의(`credential`/`login` toggle)만 추가했고
  table/migration은 추가하지 않았다.

---

## 3. Settings 기능 감사

### 3.1 실제 UI에 연결한 설정

| Category | Setting | Scope | 저장 위치 | 기본값 | 상태 |
|---|---|---|---|---|---|
| Application | Language (`system/en-US/ko-KR`) | App-wide | `app-preferences.json` | system | PASS; restart 필요 |
| General | Search engine | Profile | SQLite `settings.engine` | duckduckgo | PASS |
| General | Restore previous tabs | Profile | SQLite `settings.restore_session` | true | PASS |
| Appearance | Theme (system/light/dark) | Profile | SQLite `settings.theme` | system | PASS |
| Privacy | Save browsing history | Profile | SQLite `settings.save_history` | true | PASS; 실제 기록 중단에 사용 |
| Privacy | Allow password autofill | Profile | SQLite `settings.login_autofill_enabled` | true | PASS; explicit Fill 경로에 적용 |
| Privacy | Offer to save passwords | Profile | SQLite `settings.login_save_prompt_enabled` | true | PASS; capture prompt 경로에 적용 |
| Privacy | Manage site permissions | Profile | Site permissions SQLite v6 | 없음 | PASS; 기존 Phase 2.1 UI 연결 |
| Privacy | Open password vault | Profile | Vault DB/blob | 없음 | PASS; 기존 Vault UI 연결 |
| Privacy | Clear WebView saved passwords | Profile/WebView2 | WebView2 clear API | none | PASS; 기존 확인 dialog 연결 |
| AI | Enabled / Mock·Actual / model / key / test / delete | App-wide | `ai-api-settings.json` + Credential Manager | enabled, Actual, gpt-6-luna | PASS |
| Downloads | Download folder | App-wide | `app-preferences.json` | empty → AMARE default | PASS; DownloadStarting에서 사용 |
| Updates | Check on startup | App-wide | `update-preferences.json` | true | PASS |
| Updates | Update channel (Stable/Prerelease) | App-wide | `update-preferences.json` | stable | PASS |
| Updates | Check now | App-wide | — | — | PASS; 기존 update dialog |

### 3.2 구현되지 않아 UI에 노출하지 않은 설정

| 기능 | 이유 | 상태 |
|---|---|---|
| Startup/new-tab behavior (custom home/new tab URL) | New Tab provenance/내부 문서 정책과 충돌; 별도 설계 필요 | NOT_EXPOSED |
| Default browser registration | Windows 등록/해제 구현 없음 | NOT_IMPLEMENTED |
| Window/tab behavior (open in new tab/window, tab width) | 실제 동작 연결 없음 | NOT_EXPOSED |
| UI font / UI font size | 전역 글꼴 적용이 WinUI 전체에 안전하게 적용되지 않음; 1차 구현에서 compile/동작 문제로 UI에서 제거 | NOT_IMPLEMENTED |
| Tab density / side panel visibility / animations | 구현 없음 | NOT_IMPLEMENTED |
| Tracking/telemetry / HTTPS-only toggle | telemetry 서버 없음; HTTPS 정책은 기존 navigation 정책에 고정 | NOT_EXPOSED |
| Ask where to save / download auto-open | download prompt/panel 미구현 | NOT_EXPOSED |
| Agent/notifications settings | 실행 기능 없음 | NOT_EXPOSED |

Settings는 복잡한 sidebar 없이 **하나의 scrollable dialog + category heading**
구조를 유지했다. 각 category에 scope를 표시한다.

---

## 4. 저장/복원 / scope

### 4.1 Scope

- **Application-wide**
  - language
  - download folder
  - update check/channel
  - AI settings/key 상태
- **Profile-specific**
  - theme
  - search engine
  - restore session
  - save history
  - login autofill/save prompt
  - site permissions
  - Vault
- **Window-specific**: 현재 없음.
- **Session-only**: 현재 없음.

### 4.2 저장 위치

```text
SQLite profile settings
  theme, engine, restore_session, save_history,
  login_autofill_enabled, login_save_prompt_enabled

app-preferences.json
  language, download_directory

update-preferences.json
  check_on_startup, channel, last_check_unix_ms

ai-api-settings.json
  model, base_url, provider, enabled, mode, key_rejected
  (API key 없음)

Windows Credential Manager
  AMARE.AI.OpenAI API key

Site permissions / Vault
  기존 profile SQLite / Vault 저장 구조 그대로
```

### 4.3 기본값 / 복원 / 손상

- 기본값:
  - language=system, download=empty
  - theme=system, engine=duckduckgo, restore=true
  - save_history=true, login autofill/save prompt=true
  - update check=true, channel=stable
  - AI enabled=true, mode=Actual, model=gpt-6-luna
- JSON 로드 실패/손상 시 default로 fallback하고 다른 파일은 초기화하지 않는다.
- profile setting individual read는 `ReadBooleanSettingAsync`/`ReadTextSettingAsync`로
  손상 값을 default로 fallback한다.
- app/update/AI JSON 저장은 `*.tmp` 작성 후 `File.Move(overwrite:true)`로
  atomic replace한다.
- 저장 실패 시 status를 `partialSave`로 표시하고, app-wide in-memory 값은
  이전 값으로 되돌린다. SQLite 저장과 app JSON 저장은 서로 다른 저장소이므로
  cross-store transaction은 없으며, 부분 저장을 성공으로 표시하지 않는다.

### 4.4 Multiwindow

- theme 변경은 `WindowRegistry`를 통해 모든 window에 즉시 broadcast한다.
- language는 restart 후 적용, download/update/AI는 app-wide 저장소를 다음
  사용/시작 시 읽는다.
- profile-specific search/restore/history/login/site/Vault 설정은 다른 window에
  live broadcast하지 않는다. 새 window/다음 load에서 적용된다.
- 이 동작은 UI로 엄격히 검증하지 않았으며 UNVERIFIED다.

---

## 5. Localization

### 5.1 리소스 구조

- `apps/windows/AMARE.Windows.Core/Localization.cs`
  - `L10n` keyed resources: **253개**
  - English/Korean pair, placeholder count 일치
  - static XAML/menu/dialog English-key resources: **82개**
  - fallback: 현재 언어에 없으면 English, keyed key가 없으면 key 자체
- `apps/windows/AMARE.Windows/LocalizationUi.cs`
  - static XAML `TextBlock`/Button/CheckBox/TextBox placeholder/ToolTip/
    AutomationProperties.Name 번역
  - `ContentDialog` title/primary/secondary/close/content 번역
- 새 Settings/AI/Site permission 문자열은 `L10n.T(key)`를 사용한다.
- 모델명, API ID, URL, 파일 경로, "Segoe UI" 등은 번역하지 않는다.

### 5.2 언어 전환

- `system`은 Windows UI culture가 ko이면 Korean, 아니면 English.
- Settings에서 English/한국어/System 선택.
- 저장 후 **AMARE 재시작**이 필요하다. Settings에 명확히 표시한다.
- `LocalizationUi.Apply(RootGrid)`가 startup 시 static UI/tooltip/automation name을
  번역하고, 각 dialog 생성 시 `ApplyDialog`를 호출한다.

### 5.3 커버 범위

Localized:

- Settings 전체 category/label/description/button/status
- Browser menu 전체
- AI settings/dialog, AI 오류 메시지
- Site permission request/list/error
- Vault 주요 title/button/status
- Downloads/Update dialog/status
- toolbar tooltip/accessibility names (static map)
- validation/save failure status

Known limitation:

- 아주 오래된 diagnostic/status 문자열 일부는 English fallback으로 남을 수 있다.
  Settings와 직접 연결된 화면은 keyed resource를 사용한다.
- 긴 한국어 문구의 실제 잘림/겹침은 실제 Windows 11/DPI에서 UNVERIFIED다.

---

## 6. 접근성 / UX

- Settings control에 Automation ID를 부여했다.
- 기존 standard WinUI focus/tab order를 유지하고 sidebar를 추가하지 않았다.
- Password/API key는 `PasswordBox`/masked, API key 원문 재표시 없음.
- Site permission/Vault/clear WebView/Check updates는 별도 확인/기존 dialog를
  사용한다.
- category heading과 `scopeProfile`/`scopeApp` 문구로 현재 설정 범위를 표시한다.
- Key missing/rejected 상태, Mock/Actual, cost warning을 명시한다.
- 실제 DPI 100/125/150/200%, Korean IME, screen reader 발화는 UNVERIFIED.

---

## 7. 테스트 결과

### 7.1 Windows / Linux

| Suite | 결과 |
|---|---|
| WinUI UI smoke | **76/76 PASS** (기존 74 + settings categories + restart persistence) |
| C# ↔ Rust interop | **113/113 PASS** (기존 110 + Phase33 3) |
| Windows 통합 검증 | **182 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** (198 total) |
| Linux CI | **SUCCESS** (interop 113/113, vault 24/24) |
| Rust services | **38/38 PASS** |
| Package/MSIX | **SKIPPED (8)**, 생성 없음 |

최종 Windows run: [45820636982](https://github.com/thfzm/AMARE/actions/runs/45820636982)
(`e548a1f`)

최종 Linux run: [45820642988](https://github.com/thfzm/AMARE/actions/runs/45820642988)
(`e548a1f`)

### 7.2 신규 Core/Interop tests

```text
PASS localization_resource_completeness
  253 keyed + 82 static strings, non-empty, placeholder count 일치
PASS localization_fallback_and_language_switch
  Korean/English, static translation, missing key fallback, system resolution
PASS app_preferences_roundtrip
  language/download path roundtrip, defaults, language options
```

### 7.3 신규 UI tests

```text
PASS settings_categories_and_scope
  Language/theme/engine/restore/history/login toggles,
  site permissions/Vault/clear WebView, AI, download, update channel/check now

PASS settings_persistence_restart
  save history off + update channel prerelease
  → restart
  → values restored
  → restore defaults
```

기존 `settings_dialog`, `settings_shortcut_actions`, AI settings, Site
Permissions, Vault, New Tab, navigation, multiwindow 회귀 PASS.

### 7.4 PASS / UNVERIFIED / BLOCKED / SKIPPED

**PASS**

- Settings category/scope audit 및 실제 control 연결
- profile settings 저장/재시작 복원
- app-wide language/download/update/AI 저장
- language resource completeness/fallback
- Korean/English static/menu/dialog translation
- AI key masking/rejected status (Phase 2.2 회귀)
- site permission/Vault 연결
- download folder/update channel wiring
- Windows/Linux CI

**UNVERIFIED**

- 실제 Windows 11 Korean IME/DPI/접근성/screen reader
- 언어 변경 후 실제 UI restart rendering (Core language switch는 PASS,
  UI restart automation은 미추가)
- 긴 한국어 문자열의 실제 layout 잘림/겹침
- 설정 저장 실패를 강제 주입한 UI consistency (partial save code/status만)
- multiwindow profile setting live sync (theme만 broadcast)
- download folder path를 실제 다운로드에 사용하는 UI E2E
- legacy non-Settings diagnostic 문자열 전체 번역

**BLOCKED**

- 없음.

**SKIPPED**

- 패키지 8단계
- Netflix/Disney 유료 서비스 probe

---

## 8. 최초 실패 및 수정 이력

| # | run | 실패 | 분류 | 수정 |
|---:|---|---|---|---|
| 1 | [45803036370](https://github.com/thfzm/AMARE/actions/runs/45803036370) | `Grid.FontFamily/FontSize` compile error | PRODUCT_BUILD_DEFECT | 미지원 UI font/size 설정을 UI에서 제거 |
| 2 | [45808025434](https://github.com/thfzm/AMARE/actions/runs/45808025434) | `password_autofill_enabled`가 sensitive key로 거부되어 settings save 실패 | PRODUCT_DEFECT / SETTINGS_POLICY | `credential_*` 시도 |
| 3 | [45810524621](https://github.com/thfzm/AMARE/actions/runs/45810524621) | `credential_*`도 sensitive keyword로 거부, persistence 실패 | PRODUCT_DEFECT | `login_autofill_enabled`/`login_save_prompt_enabled`로 변경 |
| 4 | [45804593150](https://github.com/thfzm/AMARE/actions/runs/45804593150) | `tear_off_tab` pre-existing flake | PRE-EXISTING UI FLAKE | 동일 HEAD 재실행 |
| 5 | [45813103823](https://github.com/thfzm/AMARE/actions/runs/45813103823) | 첫 clean: 182/0/8/8, UI 76/76, interop 113 | — | 증거 보존 |
| 6 | [45815376327](https://github.com/thfzm/AMARE/actions/runs/45815376327) | `tear_off_tab` flake | PRE-EXISTING UI FLAKE | 재실행 |
| 7 | [45820636982](https://github.com/thfzm/AMARE/actions/runs/45820636982) | 최종: 182/0/8/8, UI 76/76, interop 113 | — | 최종 증거 |

증거: `artifacts/windows/phase2.3-settings-localization/`

---

## 9. 변경 파일 / 커밋

### 제품

- `apps/windows/AMARE.Windows.Core/Localization.cs` (신규)
- `apps/windows/AMARE.Windows.Core/AppPreferences` (Localization.cs 내)
- `apps/windows/AMARE.Windows.Core/Models.cs` (`AsInteger`)
- `apps/windows/AMARE.Windows.Core/SitePermissionCatalog.cs`
- `apps/windows/AMARE.Windows/LocalizationUi.cs` (신규)
- `apps/windows/AMARE.Windows/MainWindow.Settings.cs` (신규)
- `apps/windows/AMARE.Windows/MainWindow.xaml.cs`
- `apps/windows/AMARE.Windows/MainWindow.Ai.cs`
- `apps/windows/AMARE.Windows/MainWindow.Permissions.cs`
- `apps/windows/AMARE.Windows/MainWindow.PermissionAudit.cs`
- `apps/windows/AMARE.Windows/MainWindow.Vault.cs`
- `apps/windows/AMARE.Windows/MainWindow.WebViewVault.cs`
- `crates/amare-services/src/settings.rs` (login setting definitions)

### 테스트

- `apps/windows/AMARE.Interop.Tests/Phase33Tests.cs` (신규, 3 tests)
- `apps/windows/AMARE.Interop.Tests/Program.cs`
- `tools/windows-ui-smoke/Program.cs` (+2 UI tests)

### 주요 commit

```text
cfb56ae Expand settings and add Korean/English localization
979a906 Remove unsupported UI font settings from Settings
d4c014a Add settings audit UI, localization tests and restart persistence
29c2f91 Use non-sensitive setting keys for credential toggles
c6787a2 Use non-sensitive login setting keys for privacy toggles
c8dcdfb Clarify app-wide language scope in Settings
e548a1f Keep Settings UI consistent when app-wide saves fail
```

---

## 10. 알려진 한계

1. **Windows 11 실기기**: CI는 Windows Server 2025다. Korean IME/DPI/접근성/
   실제 OS language display는 UNVERIFIED.
2. **언어 UI restart**: Core language switch/resource test는 PASS지만, Settings에서
   Korean 선택 → restart → Korean UI 확인을 UI automation으로 추가하지 않았다.
   코드는 restart 후 `L10n.Initialize`와 `LocalizationUi.Apply`로 적용한다.
3. **긴 문자열 layout**: 253 keyed/82 static strings의 completeness는 test했지만,
   Korean 긴 문구가 125/150/200% DPI에서 잘리지 않는지는 실제 화면 확인이 필요하다.
4. **Settings save failure UI**: partial save status/revert code는 있지만, 저장
   실패를 강제 주입한 UI test는 없다.
5. **Multiwindow**: theme만 즉시 broadcast한다. search/restore/login/history 등
   profile settings는 다른 window에 live sync하지 않는다.
6. **미구현 설정**: UI font/size, tab density, side panel, animations, default
   browser, home/new-tab behavior, tracking/telemetry, HTTPS-only toggle,
   download prompt/auto-open은 구현하지 않았고 UI에 노출하지 않았다.
7. **Legacy strings**: Settings/menu/AI/site/Vault/download/update 주요 문자열은
   localized, 오래된 diagnostic/status 일부는 English fallback 가능.
8. **Credential setting keys**: 기존 `password_*` 정의는 store의 sensitive-key
   정책과 충돌해 사용할 수 없었다. 동작은 `login_*` key로 저장하며, 기존
   `password_*` key의 데이터 초기화는 하지 않는다(저장된 값이 없음).
9. **DB schema**: 변경 없음. settings definition만 추가했다.
10. **Download folder**: app-wide path를 실제 다운로드 시작 시 사용하지만,
    UI E2E 다운로드/권한/경로 오류는 별도 확인이 필요하다.

---

## 11. 결론

- Phase 2.3 Settings 감사/연결/localization은 **PASS**다.
  - General/Appearance/Privacy/AI/Downloads/Updates를 실제 기능과 연결
  - 구현되지 않은 설정은 UI에 노출하지 않음
  - 253 keyed + 82 static Korean/English resources, fallback test
  - save history/update channel restart persistence UI test
  - 기존 AI/Site permissions/Vault/New Tab/navigation 회귀 유지
- Windows **182/0/8/8**, UI **76/76**, interop **113/113**, Linux **SUCCESS**.
- 실제 Windows 11 Korean IME/DPI/layout와 언어 restart UI automation은
  UNVERIFIED로 남긴다.

다음 단계는 이 보고서를 확인한 뒤 결정한다. 패키지/MSIX/서명/공개 배포는
수행하지 않았다.
