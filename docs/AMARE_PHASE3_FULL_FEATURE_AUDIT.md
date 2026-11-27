# AMARE Phase 3.0 — Full Feature Audit

- Phase 2.7 제품/테스트 HEAD: `3fff704`
- Phase 2.7 문서 HEAD: `9780e8b`
- Phase 3 감사/패키징 app HEAD: `9cb93f7`
- Phase 3 final branch HEAD: `b869f8f` (Phase 3.0)
- Phase 3.1 self-contained HEAD: `7c7c6c3`
- 감사 branch: `feature/phase-3-full-audit-windows-validation`
- 감사 기준: 실제 코드, 테스트 코드, CI 결과, 설계/검증 문서 대조
- 원칙: **문서에 PASS라고 적혀 있다는 이유만으로 완료로 판정하지 않는다.**
- 상태 정의:
  - `IMPLEMENTED`: 제품 코드에 실제 경로가 있음
  - `AUTOMATED_PASS`: 자동화 테스트가 통과함
  - `WINDOWS_CI_PASS`: Windows CI runner에서 통과함
  - `REAL_DEVICE_UNVERIFIED`: 실제 Windows 11 PC 검증이 필요함
  - `INCOMPLETE`: 코드/연결/범위가 부족함
  - `BLOCKED`: 외부 조건/정책/서명/실기기 때문에 진행 불가

---

## 1. 기준점 확인

실제 저장소 확인 결과:

```text
branch: feature/phase-2-backup-restore-ui
HEAD:   d18f45b
product/test HEAD: 3fff704
docs HEAD: 9780e8b
remote: d18f45b (동기화됨)
working tree: clean
```

Phase 2.7 기준점과 일치한다. Phase 3.0은 `d18f45b`에서
`feature/phase-3-full-audit-windows-validation` 브랜치로 분리했다.

---

## 2. 증거 인벤토리

| 증거 | 결과 | 성격 |
|---|---|---|
| Windows CI (Phase 3.0 run) | **200 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** (216 total) | Windows runner |
| WinUI UI smoke | **86/86** | 실제 WinUI UI Automation |
| C#↔Rust interop | **127/127** | C# + Rust DLL |
| Backup CI | **10/10** | Rust backup integration |
| Portable updater E2E | **11/11** | 실제 ZIP + helper process |
| Personal package step | **PASS** | framework + self-contained ZIP |
| Self-contained ZIP | **PASS (Windows CI)** | 527 files, 155,316,668 bytes, SHA-256 `24d123...` |
| Self-contained clean launch | **PASS (Windows Server CI)** | 8s alive; real Windows 11 UNVERIFIED |
| Linux CI | **SUCCESS** | Linux Rust/C# interop |
| Rust store/services/vault | store/services/vault tests PASS | host tests |
| UI smoke 범위 | window/tab/address/cert/Reader/AI/settings/vault/permissions/backup/updater check | Windows Server 2025 runner |
| 실기기 Windows 11 | **UNVERIFIED** | 물리 PC 없음 |
| Windows run | [45961967631](https://github.com/thfzm/AMARE/actions/runs/45961967631) at `b869f8f` |
| Linux run | [45961970830](https://github.com/thfzm/AMARE/actions/runs/45961970830) at `b869f8f` |

주의:

- Windows CI runner는 **Windows Server 2025**일 수 있으며 실제 Windows 11
  소비자 PC와 동일하지 않다.
- UI smoke는 UIAutomation 기반이며 실제 사람의 IME/DPI/멀티모니터 조작을
  대체하지 않는다.
- 실제 공개 GitHub Release asset, 코드 서명, MSIX 설치는 이번 단계에서
  수행하지 않았다.
- Phase 3.0 검증 스크립트는 실제 Windows 11에서 아직 실행하지 않았다.

---

## 3. 15개 기능 감사

표기: `I`=IMPLEMENTED, `A`=AUTOMATED_PASS, `W`=WINDOWS_CI_PASS,
`R`=REAL_DEVICE_UNVERIFIED, `IN`=INCOMPLETE, `B`=BLOCKED.

| # | 기능 | 상태 | 구현 위치 | 기존 검증 | 남은 위험/우선순위 |
|---:|---|---|---|---|---|
| 1 | 앱 시작/종료/복구 | I,A,W,R | `App.xaml.cs`, `MainWindow.xaml.cs`, `AppLog`, `PortableUpdateState` | `window_created`, `session_restore*`, app launch/exit, startup log tests | 실제 Win11 첫 실행, AV/EDR, .NET runtime 부재, 비정상 종료 복구. **P0/P1** |
| 2 | 탭 생성·전환·닫기·재정렬·새 창·창 간 이동 | I,A,W,R | `TabManager`, `ClosedTabHistory`, `MainWindow.xaml.cs` | 7 drag 조합, cancel, tear-off, cross-window move, 30 tabs, tab churn | 실제 마우스/터치 드래그, Snap, Alt+Tab, 다중 모니터. **P1** |
| 3 | 주소창/검색/단축키/한글 IME | I,A,W,R,IN(IME) | `AddressBarResolver`, `SearchEngineCatalog`, `KeyboardShortcutRouter`, `BrowserShortcutPolicy` | address/search tests, shortcut tests, UI address/search | 한글 IME 조합, 포커스 이동, Ctrl 조합 실제 키보드. **P0/P1** |
| 4 | WebView2 탐색/팝업/다운로드 | I,A,W,R | `NavigationPolicy`, `NewTabDocumentNavigation`, `DownloadPolicy`, `MainWindow.xaml.cs` | navigation/popup/redirect/data-block UI tests, download policy tests | 실제 다운로드 저장 대화상자/경로/취소, 실제 WebView2 runtime, 사이트 호환성. **P1** |
| 5 | 북마크/방문 기록/세션 복원 | I,A,W,R | `BookmarkClient`, `HistoryClient`, `SessionClient`, `SessionRestorePlanner`, `SessionWindowRestorePlanner` | native CRUD/interop, multiwindow restore, forced restore, UI restart | 장시간/대량 데이터, 실제 종료 race, session 손상 복구. **P1** |
| 6 | 다중 프로필/데이터 격리 | I,A,W,R | `ProfileClient`, `ProfilePathPolicy`, `ProfileDataCleanup`, per-profile DB/WebView2 | profile CRUD, isolation, cleanup, WebView2 path, UI profile switch | 실제 multi-window 동시 사용, delete/reset race, 다른 계정 데이터. **P1** |
| 7 | Password Vault/저장/Fill | I,A,W,R | `VaultClient`, `MainWindow.Vault`, `MainWindow.WebViewVault`, `WebViewVaultTabState`, Rust vault | Vault UI 9+ tests, capture/fill/forged/cross-origin/hidden/duplicate, settings OFF, idle 30s, Credential Manager | 실제 로그인 페이지, 5분 idle, Windows session lock, force-kill, real WebView2 frame identity. **P0** |
| 8 | Site Permissions | I,A,W,R | `MainWindow.Permissions`, `SitePermissionAudit`, `SitePermissionCatalog`, Rust permissions | consent, one-time, allow/block, iframe, popup, profile isolation, legacy audit fail-closed | 실제 OS permission prompt, camera/mic/notifications real device. **P1** |
| 9 | AI Web Panel/API/Mock/key | I,A,W,R | `MainWindow.Ai`, `AiApiClient`, `AiCredentialStore`, `crates/amare-services` | panel, mock, contract, 401/429/network/timeout/SSE, key save/delete, redaction | 실제 provider endpoint/API key, OAuth/session isolation, quota. **P1** |
| 10 | Reader/Find in Page | I,A,W,R | `ReaderPageBuilder`, `ReaderExtractionScript`, `MainWindow.xaml.cs` | reader basic, safe image URLs, find 1/2, UI | 복잡한 실제 사이트 layout, paywall/SPA, large page. **P2** |
| 11 | 설정/한국어·영어/DPI | I,A,W,R,IN(일부 설정) | `MainWindow.Settings`, `Localization`, `LocalizationUi`, `SettingsClient` | categories, scope, persistence restart, 86 UI tests, localization completeness | 실제 한국어 IME, 125/150/200% DPI, 멀티모니터, 일부 미구현 설정. **P1/P2** |
| 12 | Backup & Restore/Vault 교차 PC | I,A,W,R,IN(category partial) | `crates/amare-store/backup.rs`, `amare-backup`, `BackupClient`, `MainWindow.Backup` | backup CI 10/10, Rust helper full/partial/journal, UI create/verify/full dialog | 실제 Win11 full/partial restore, power-loss, cross-PC Vault, multi-window race, category partial. **P0/P1** |
| 13 | Portable Updater/rollback | I,A,W,R,IN(signature) | `PortableUpdateEngine`, `PortableUpdatePackage`, `MainWindow.xaml.cs`, `AMARE.Updater` | Portable E2E 11/11, rollback/crash/recovery/cap, data preservation | 실제 Win11 download/apply/restart/rollback, real server/AV, publisher signature 없음. **P0/P1** |
| 14 | 진단/로그/오류 처리 | I,A,W,R | `AppLog`, Diagnostics dialog, `MainWindow.xaml.cs`, startup-error log | log tests, diagnostics UI, error mapping | GPU/ETW, real crash dump/WER, log redaction real secrets. **P2** |
| 15 | 개인용 ZIP 패키징/업데이트 경로 | I,A,W,R,B(final release) | `tools/verify-windows.ps1` `personal_package`, updater helper, `AppPaths` | package build/step, package validation, portable E2E | framework-dependent .NET 8, unsigned, no real-device launch, no signed/public release. **P0/P1** |

---

## 4. 기능별 상세 결론

### 4.1 앱 시작/종료/복구

- 구현: WinUI unpackaged entry, startup log, session restore, updater health marker,
  restore journal, profile cleanup.
- 자동화: UI `window_created`, `session_restore`, `session_restore_forced`,
  `session_multiwindow_restore`; Windows process launch/exit.
- 남은 위험: 실제 Windows 11 consumer PC에서 .NET 8 runtime/WebView2/WindowsAppSDK
  충족 여부, AV/EDR 지연, 전원 손실, 실제 첫 실행.
- 판정: **IMPLEMENTED + AUTOMATED_PASS + WINDOWS_CI_PASS + REAL_DEVICE_UNVERIFIED**.

### 4.2 탭/창

- 구현: `TabManager`, `ClosedTabHistory`, cross-window move, tear-off.
- 자동화: 7 drag 조합, cancel, after-close, cross-window, 30 tabs, churn.
- 남은 위험: 실제 Windows 11 마우스/터치 드래그, Snap Layout, Alt+Tab,
  다중 모니터, 창 생성/종료 race.
- 판정: 구현/CI PASS, 실기기 UNVERIFIED.

### 4.3 주소창/검색/키보드/IME

- 구현: URL vs search 자동 판별, search engine catalog, shortcut router,
  custom title bar, address suggestions.
- 자동화: `address_bar_url_vs_search`, `search_engine_catalog_encodes_query`,
  `shortcut_*`, UI address/search.
- 남은 위험: 한국어 IME 조합 중 Enter/Backspace, 포커스 전환, 실제 키보드
  layout, DPI.
- 판정: 주소/검색/단축키는 CI PASS, **IME는 REAL_DEVICE_UNVERIFIED**.

### 4.4 WebView2 탐색/팝업/다운로드

- 구현: `NavigationPolicy`, external scheme blocking, `_blank`/popup tab,
  download policy, certificate error handling.
- 자동화: local page, redirect, data blocking, popup, iframe child,
  certificate error self-signed/expired/hostname mismatch, downloads menu.
- 남은 위험: 실제 다운로드 저장 대화상자/파일, 취소/재개, WebView2 Evergreen
  실제 버전, 사이트 CSP/COOP/COEP, DRM/미디어.
- 판정: 정책/탐색 CI PASS, 실제 download/media REAL_DEVICE_UNVERIFIED.

### 4.5 북마크/방문 기록/세션

- 구현: SQLite repositories, C# clients, session/window planners.
- 자동화: native CRUD, history aggregation, session restore, multiwindow,
  forced kill, restart.
- 남은 위험: 대량 데이터, 장시간 session, 실제 강제 종료/WER, profile switch race.
- 판정: 구현/CI PASS, 실기기 UNVERIFIED.

### 4.6 다중 프로필/격리

- 구현: per-profile DB, WebView2 user data folder, profile cleanup, profile switch.
- 자동화: profile CRUD, path confinement, cleanup, WebView2 isolation,
  profile switch, site permission/Vault isolation.
- 남은 위험: multi-window same profile concurrency, delete/reset during restore,
  cross-process profile registry.
- 판정: 구현/CI PASS, 실기기/동시성 UNVERIFIED.

### 4.7 Vault/저장/Fill — P0 gate

- 구현: Rust Vault + DPAPI key, ABI2, WinUI Vault UI, WebView2 capture/fill,
  exact origin, top-level frame/token/generation, approval-before-read,
  no auto-submit, settings OFF, lock epoch, idle 5분 policy (30s test override).
- 자동화: Vault UI/CRUD/backup/import, capture/fill/forged/cross-origin/
  hidden/duplicate/settings OFF/locked/idle, site permissions profile isolation,
  Credential Manager key.
- 남은 위험: 실제 Windows 11 로그인 페이지, 실제 5분 idle, 실제 Windows session
  lock, 강제 종료 race, WebView2 frame identity real-world, existing autofill data clear.
- 판정: 구현/CI PASS, **REAL_DEVICE_UNVERIFIED P0**.

### 4.8 Site Permissions

- 구현: per-profile v6 store, consent UI, legacy audit, iframe/popup/AI/Reader policy.
- 자동화: consent, deny/allow once, allow/block always, iframe, popup, settings,
  profile isolation, audit fail-closed.
- 남은 위험: 실제 Windows 권한 prompt, camera/mic/notification devices.
- 판정: CI PASS, 실기기 UNVERIFIED.

### 4.9 AI Panel/API/Mock/Key

- 구현: WebView2 AI panel, Mock/Actual, Responses provider, Credential Manager,
  page-context consent, SSE/cancel/error mapping/redaction.
- 자동화: panel open/close, no-key, key save/delete, mock offline, contract,
  401/429/5xx/timeout/network, SSE, page consent, redaction.
- 남은 위험: 실제 provider/API key, OAuth login/session isolation, quota, rate limit.
- 판정: CI/contract PASS, 실제 provider E2E UNVERIFIED (key 필요).

### 4.10 Reader/Find

- 구현: extraction script/page builder, find in page.
- 자동화: basic Reader, safe image URLs, find 1/2.
- 남은 위험: 복잡한 실제 사이트, paywall, SPA, long article, image lazy load.
- 판정: CI PASS, real-site UNVERIFIED.

### 4.11 설정/현지화/DPI

- 구현: General/Appearance/Privacy/AI/Downloads/Updates, Korean/English,
  scope labels, restart persistence.
- 자동화: settings categories, persistence, 86 UI tests, localization resource
  completeness and language switch.
- 남은 위험: 실제 한국어 IME/DPI/멀티모니터, UI font/tab density/default browser
  등 미구현 설정.
- 판정: CI PASS, 실기기/일부 설정 INCOMPLETE.

### 4.12 Backup & Restore

- 구현: Rust v2 container, profile DB + app files + optional Vault export,
  full/profile partial restore, prepared/applying journal, progress, backup list,
  separate Vault password, UI confirmation.
- 자동화: backup CI 10/10, Rust full/partial/Vault/journal, UI create/verify/full
  dialog, C# helper tests.
- 남은 위험: 실제 Win11 full/partial restore, power-loss, cross-PC Vault,
  multi-window race, category partial restore.
- 판정: 구현/CI PASS, **REAL_DEVICE_UNVERIFIED P0/P1**.
- Category 단위 partial restore는 **INCOMPLETE/미지원**으로 유지한다. 테이블/FK
  무결성과 unselected data 보존이 검증되지 않았으므로 UI에 노출하지 않는다.

### 4.13 Portable Updater

- 구현: SemVer, HTTPS/GitHub, size/disk/hash, manifest/ZIP policy, atomic replace,
  backup/health/rollback/recovery cap, helper lock, UI apply.
- 자동화: Portable E2E 11/11, data preservation, wrong hash, locked file,
  launch rollback, crash recovery, attempt cap.
- 남은 위험: 실제 Windows 11 download/apply/restart/rollback, real server/AV,
  publisher signature 없음, framework-dependent app.
- 판정: 구현/E2E PASS, **REAL_DEVICE_UNVERIFIED P0/P1**.

### 4.14 진단/로그/오류

- 구현: AppLog, startup-error log, Diagnostics dialog, status messages,
  update/backup/vault error mapping.
- 자동화: log create/append, diagnostics UI, error mapping tests.
- 남은 위험: 실제 WER/crash dump, GPU/ETW, 다중 창 log race, secret redaction
  real-world.
- 판정: CI PASS, 실기기 UNVERIFIED.

### 4.15 개인용 ZIP/업데이트 경로

- 구현: `personal_package` CI step, app output + updater/backup helper, manifest,
  SHA-256, excluded PDB, unsigned marker.
- 패키지: Phase 3.0에서 별도 staging 경로에 validation ZIP을 생성/검증한다.
- 남은 위험: framework-dependent .NET 8, WebView2 Evergreen, unsigned,
  실제 Windows 11 launch/update, no public release.
- 판정: **IMPLEMENTED + AUTOMATED_PASS(package structure) + REAL_DEVICE_UNVERIFIED +
  BLOCKED(final release/signing)**.

---

## 5. P0 / P1 / P2

### P0 — 출시 전 반드시 해결/검증

| ID | 항목 | 상태 | 조치 |
|---|---|---|---|
| P0-1 | 실제 Windows 11 핵심 브라우저/입력 미검증 | **OPEN** | Windows 11 실기기에서 launch/navigation/tabs/IME/DPI 검증 |
| P0-2 | Vault 실제 로그인/5분 idle/session lock/force-kill 미검증 | **OPEN** | 실제 Windows 11 Vault gate |
| P0-3 | 개인용 ZIP runtime/signing | **PARTIALLY RESOLVED** | self-contained .NET 8/VC++ ZIP 구현·CI launch PASS; 실제 Win11/signing UNVERIFIED |
| P0-4 | backup full/partial restore 실제 Windows 11 미검증 | **OPEN** | 실기기 full/partial restore + power-loss |
| P0-5 | portable updater 실제 download/apply/restart/rollback 미검증 | **OPEN** | 실기기 updater gate |

> 코드 수준에서 새로 확인된 P0 데이터 손실/credential 노출 결함은 없다.
> 위 항목은 **검증 gate**이며, 통과 전 “출시 가능”으로 표시하지 않는다.

### P1 — 주요 실사용 위험

1. Vault cross-PC restore(다른 Windows 계정/PC), DPAPI/key crash window.
2. Multi-window restore 경합, restore 중 신규 창/탭.
3. 실제 WebView2 로그인 저장/Fill, cross-origin frame identity.
4. 실제 Windows session lock/5분 idle.
5. 실제 updater server/redirect/AV/재시작.
6. 실제 한국어 IME/DPI/멀티모니터/Snap/Alt+Tab.
7. 실제 AI provider/API key/OAuth.
8. 코드 서명/publisher authentication 부재.
9. Category 단위 partial restore 미지원(의도적).
10. Framework-dependent .NET 8 배포/런타임 설치 UX.

### P2 — 품질/편의

1. Backup 목록 삭제/보존 정책 UI.
2. Download metadata/cancel/remove UI.
3. Reader 복잡 사이트 품질.
4. UI font/tab density/default browser.
5. 진단 GPU/ETW, crash dump 수집.
6. 장시간/대용량 soak.
7. GPUI 동결 유지.

---

## 6. 자동화와 실기기 경계

**자동화로 판정 가능한 것**

- 코드/DB/백업/업데이터 계약과 failure injection
- WinUI UIAutomation의 기본 window/tab/address/settings/Vault UI 시나리오
- 패키지 구조/manifest/SHA-256/금지 파일 검사
- 격리된 test root에서 backup/restore/updater helper 실행

**실제 Windows 11 사용자 개입이 필요한 것**

- 실제 consumer PC 첫 실행, .NET 8/WebView2 설치 상태
- 한국어 IME, DPI, 멀티모니터, Snap/Alt+Tab
- 실제 로그인 페이지 capture/fill, 실제 session lock/idle
- 실제 force-kill/WER/power-loss
- cross-PC Vault
- 실제 provider AI OAuth/key
- 코드 서명/publisher 검증

---

## 7. 결론

AMARE는 **핵심 브라우저 기능의 상당 부분이 구현되고 Windows CI에서 검증된 상태**다.
그러나 실제 Windows 11 실기기 검증, Vault lifecycle, backup/restore, updater,
IME/DPI, cross-PC Vault는 아직 **UNVERIFIED**다.

따라서 현재 상태를 “최종 개인용 브라우저 완성”으로 표시하지 않는다.
Phase 3.0의 완료 조건은 **실기기 검증을 시작할 수 있는 패키지와 절차를 갖추는 것**이며,
최종 출시 판정은 Windows 11 검증 결과 이후에 한다.

- P0 code defect: 새로 발견 없음
- P0 validation gates: 5개 OPEN
- P1: 10개
- Category partial restore: 계속 미지원
- 개인용 ZIP: validation-only, unsigned, framework-dependent, real-device UNVERIFIED
