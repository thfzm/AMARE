# AMARE Finalization Plan — Phase 0

- 문서 상태: **Phase 0 계획 + Phase 2.1/2.1.1/2.2/2.3 갱신**
- 작성일: 2026-11-23
- 최신 갱신: 2026-11-26 (Phase 2.3 Settings & Localization)
- 기준: `feature/phase-2-settings-localization`, 제품/테스트 `e548a1f`
- 최종 방향: **WinUI 3 + WebView2 Evergreen + C#**, Rust는 공통 모듈에 제한
- GPUI: **동결·보존**, 기본 패키지 전환 없음
- Phase 0 당시: **문서만 작성**, 제품 코드/스키마/패키지 변경 없음

---

## 1. 최종 결정

1. AMARE의 최종 목표는 **Windows 11 개인용 메인 브라우저** 완성이다.
2. Windows UI는 **WinUI 3**, 렌더링은 **WebView2 Evergreen**, 주 언어는 **C#**을
   최종 방향으로 채택한다.
3. Rust는 SQLite 저장소, 서비스 계층, 암호화, 업데이트 검증처럼 **공통 이득이
   객관적인 모듈**에만 유지한다.
4. GPUI는 신규 기능/테스트 확장을 중단하고 **소스·문서·패키지·증거를 보존**한다.
5. 기존 WinUI 주요 기능은 제거/비활성화하지 않고 **결손만 보완**한다.
6. 감사 결과 승인 전에는 제품 아키텍처와 기본 패키지를 변경하지 않는다.
7. WinUI와 GPUI 테스트 결과는 **절대 합산하지 않는다.**

---

## 2. 현재 기준선

| 항목 | 결과 |
|---|---|
| Branch / HEAD | `feature/phase-1-session-integrity`, 제품/테스트 `bcd476d`; Phase 0 문서 `81dfc82` |
| Working tree | clean |
| Stash | `stash@{0}` 보존 |
| WinUI UI smoke | **49/49** (기존 48 + multiwindow session 1) |
| Windows 통합 | **143 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** (패키지 단계 skip) |
| Interop | **76/76** (기존 70 + session window 6) |
| Linux | SUCCESS |
| GPUI 반복 | **11/13**, **12/10**, **13/10** |
| GPUI reload | DirectReload **10/10**, Ctrl+R **10/10** (HTTP 포함) |
| GPUI titlebar | full **3/5**, crash **0/5** |
| 최신 패키지 | **0.92.0** (0.93/Phase 1.1은 패키지 생성 없음) |
| 공개 Release | 0개 |
| MSIX | unsigned, 설치 `0x80073D2C` **BLOCKED/UNVERIFIED** |

### Phase 1.1 진행 상태

- **P0-1 멀티윈도우 세션 last-writer-wins: RESOLVED**
- schema v5 (`session_windows` + `session_tabs.window_id`)
- 2창 UI smoke PASS (`restored_windows=2 tabs=[2,3]`)
- 3창/순서/종료/강제종료/v4 migration Rust 테스트 PASS
- 상세: `docs/AMARE_PHASE1_SESSION_REPORT.md`, `docs/SESSION_SCHEMA_DESIGN.md`

### Phase 1.2 진행 상태

- **P0-2 Vault: 보안 감사/설계 완료**
- 재사용: XChaCha20-Poly1305 + KeyFile + DPAPI, 기존 `secrets` 테이블
- DB schema 변경 없이 whole-blob Vault 가능
- key file 분리, exact origin, top-level autofill, no auto-submit
- 예상 구현 **27–42 person-days**
- 상세: `docs/AMARE_PHASE1_VAULT_AUDIT.md`,
  `docs/VAULT_THREAT_MODEL.md`, `docs/VAULT_SECURITY_DESIGN.md`,
  `docs/VAULT_TEST_PLAN.md`

### Phase 1.3A 진행 상태

- **Rust Vault Core 완료** (`22e3e51`)
- `VaultService` CRUD/lock/CAS/export/import, profile AAD, format 1, key-file lock
- Linux Vault 23/23, Windows 24/24, DPAPI round-trip PASS
- DB schema **v5 유지**
- 아직 없음: FFI ABI 2, C# client, WinUI UI, WebView2 자동완성
- 상세: `docs/AMARE_PHASE1_VAULT_CORE_REPORT.md`

### Phase 1.3B 진행 상태

- **FFI ABI 2 + C# Vault Client 완료** (`7dd116b`)
- ABI1 유지, ABI2 별도 symbol/handle, `vault.*` 전체 연결
- Windows CI 실제 Rust DLL + C# UTF-8/CRUD/backup/conflict PASS
- Rust FFI **12/12**, C# interop **79/79**, Windows **146/0**
- 아직 없음: WinUI Vault UI, WebView2 자동완성, auto-lock, export/import UI
- 상세: `docs/AMARE_PHASE1_VAULT_FFI_REPORT.md`

### Phase 1.3C 진행 상태

- **WinUI Vault UI + 잔여 lock 경계 검증 완료** (`8076759`)
- Vault overlay: init/lock/unlock/list/detail/add/edit/delete/reveal/copy
- backup export/validate/import, failed import 보존
- idle 5분 policy + timer(테스트 override는 단축만), session lock 코드,
  multiwindow lock broadcast
- UI smoke **54/54**: profile switch isolation, multiwindow lock/unlock,
  idle timeout(30s 단축) 실제 WinUI 검증
- C# interop **80/80**, Windows **147/0**, Linux SUCCESS
- `VaultService::status`가 영속 Vault를 감지하도록 수정 (새 client가 기존
  Vault를 Locked로 표시)
- UNVERIFIED: 실제 5분 idle, 실제 Windows session lock, Windows 11 실기기
- 다음: Phase 1.3D WebView2 autofill/login capture
- 상세: `docs/AMARE_PHASE1_VAULT_UI_REPORT.md`

### Phase 1.3C.1 진행 상태

- **New Tab internal navigation 결함 수정 완료** (`7f6a1d8`)
- `NavigateToString`의 `data:text/html` NavigationStarting을 1회성·정확한
  payload provenance로만 허용 (URL prefix 신뢰 없음, fail closed)
- 일반 `data:`/`javascript:`/`file:` 차단과 http(s)/redirect/iframe/_blank
  정책은 변경 없음
- 차단 상태 메시지에서 긴 URI 제거 (scheme만 표시)
- UI smoke **60/60**, C# interop **84/84**, Windows **151/0**, Linux SUCCESS
- UNVERIFIED: 실기기 수동 확인, UI 수준 javascript/file 차단(unit only)
- 상세: `docs/AMARE_PHASE1_NEW_TAB_NAVIGATION_FIX_REPORT.md`

### Phase 1.3D 진행 상태

- **WebView2 capture/autofill 구현 및 CI 검증 완료** (`1cb4780`)
- top-level WebMessage + native per-document token/generation + exact
  origin/document URL + strict schema + profile/WebView context
- 사용자 승인 후에만 password read; explicit menu fill; no auto-submit/Enter/click
- native `IsPasswordAutosaveEnabled`/`IsGeneralAutofillEnabled` disable + runtime
  probe PASS
- UI smoke **64/64**, C# interop **99/99**, Windows **167/0**, Linux SUCCESS
- UNVERIFIED: 실제 5분 idle, Windows session lock, Windows 11 실기기,
  기존 WebView2 데이터 삭제 실행, 강제 종료/비동기 경합
- 상세: `docs/AMARE_PHASE1_VAULT_WEBVIEW_REPORT.md`

### Phase 2.1 진행 상태

- **사이트 권한 구현 및 CI 검증 완료** (`f1d878d`)
- `CoreWebView2.PermissionRequested` + `CoreWebView2Frame.PermissionRequested`
- 13개 named kind 지원, Unknown deny, `SavesInProfile=false`
- per-profile SQLite schema **v6** `site_permissions`, CAS revision
- FFI ABI 2 `permissions.*`, C# client/DTO, WinUI consent/settings UI
- exact HTTPS origin, subdomain 비상속, IDN/punycode, HTTP/data/file/New Tab 차단
- iframe same/cross-origin deny, AI/Reader deny-all, popup 컨텍스트 유지
- UI smoke **71/71**, interop **100/100**, Windows **168/0/8/8**, Linux SUCCESS
- UNVERIFIED: 실제 Windows 11, notification toast 표시, 13개 kind 전체 live
  request, UI 수준 비동기 cancel/store failure, 기존 WebView2 non-default
  permission setting의 startup audit/reset
- 상세: `docs/AMARE_PHASE2_SITE_PERMISSIONS_REPORT.md`

### Phase 2.1.1 진행 상태

- **Legacy WebView2 permission audit/reconciliation 완료** (`d41ee59`)
- `GetNonDefaultPermissionSettingsAsync`로 기존 non-default setting 조회
- `SetPermissionStateAsync(Default)`로 13개 named kind reset
  (probe: Allow 13 → reset → 0)
- 첫 tab navigation 전 audit, profile별 coordinator, Reader/AI 포함
- `NotStarted/Auditing/Failed/Unsupported`에서는 모든 권한 요청 deny
- audit 실패 UI injection + gate unit test PASS
- UI smoke **73/73**, interop **103/103**, Windows **172/0/8/8**, Linux SUCCESS
- UNVERIFIED: 실제 Windows 11 profile upgrade, cross-process 동시 audit,
  strict lifecycle race, 구버전 WebView2 runtime
- 상세: `docs/AMARE_PHASE2_SITE_PERMISSION_RECONCILIATION_REPORT.md`

### Phase 2.2 진행 상태

- **Actual AI API 기반 구현 및 CI 검증 완료** (`a8d1747`)
- OpenAI Responses API provider + OpenAI-compatible Chat provider + Mock provider
- Settings: Enabled / Mock·Actual / model / key / test / delete
- API key: Windows Credential Manager (`CredWrite/Read/Delete`)
- page context checkbox 기본 OFF, current tab title/URL/article only
- SSE streaming, cancel, Retry, Clear conversation, 401/429/quota/5xx/timeout/
  network 오류 UI
- AI WebView 별도 profile/deny-all 유지, API key bridge 없음
- UI **74/74**, interop **110/110**, Windows **179/0/8/8**, Linux SUCCESS
- UNVERIFIED: 실제 API key를 사용한 `/v1/responses` E2E, 실제 rate limit/
  quota 응답, UI Mock flow, late-response race, Windows 11
- 상세: `docs/AMARE_PHASE2_ACTUAL_AI_API_REPORT.md`

### Phase 2.3 진행 상태

- **Settings 감사/연결/localization 완료** (`e548a1f`)
- General/Appearance/Privacy/AI/Downloads/Updates, scope 표시
- language (app-wide/restart), download folder, update channel, history/login
  toggles, site permissions/Vault 연결
- 253 keyed + 82 static Korean/English strings, fallback
- settings restart persistence UI test, settings category UI test
- UI **76/76**, interop **113/113**, Windows **182/0/8/8**, Linux SUCCESS
- UNVERIFIED: Windows 11 Korean IME/DPI/layout, 언어 restart UI, 강제 save
  failure, multiwindow profile live sync
- 상세: `docs/AMARE_PHASE2_SETTINGS_LOCALIZATION_REPORT.md`

---

## 3. 감사 결과 요약

상세 상태는 `docs/AMARE_FEATURE_AUDIT.md`에 기록했다.

- **이미 충분히 검증된 것**: 탭, 주소창/검색, navigation, 북마크, 히스토리,
  다운로드, AI 패널, AI API(모의), Reader, Find, 설정, 성능/진단.
- **부분 구현**: Vault, 창 관리/Windows 통합. (세션 멀티윈도우 P0는 Phase 1.1에서 해결)
- **Phase 2.1/2.1.1 완료(CI)**: WinUI 사이트 권한 — consent, per-profile
  store(schema v6), FFI ABI2, 설정 UI, iframe/AI/Reader/popup 정책, 그리고
  legacy WebView2 permission startup audit/reset. 실제 Windows 11과
  cross-process/strict race는 UNVERIFIED.
- **Phase 2.2 완료(CI/contract)**: AI API — OpenAI Responses provider,
  Mock/Actual, Credential Manager key, page context consent, SSE/cancel/errors.
  실제 endpoint E2E는 API key가 없어 UNVERIFIED.
- **Phase 2.3 완료(CI)**: Settings/현지화 — General/Appearance/Privacy/AI/
  Downloads/Updates 연결, Korean/English resource, restart persistence.
  Windows 11 Korean IME/DPI/layout는 UNVERIFIED.
- **미구현**: WinUI 비밀번호 관리자(1.3D capture/autofill은 구현), 실제 Windows
  11 실기기 검증.
- **미검증**: 실제 Windows 11 IME/DPI/Snap/Alt+Tab, AI 실제 서비스,
  업데이트 설치 end-to-end, MSIX 설치.

핵심 원칙: 검증된 기능을 다시 개발하지 않는다. P0 결손과 실기기 게이트에
자원을 집중한다.

---

## 4. 재사용 자산

### 그대로 사용

- C# Core: `TabManager`, `SessionRestorePlanner`, `AddressBarResolver`,
  `SearchEngineCatalog`, `DownloadPolicy`, `ReaderPageBuilder`, `AiApiClient`,
  `PortableUpdateEngine`, `ProfileDataCleanup`, `KeyboardShortcutRouter`
- Rust: `amare-store`, `amare-services`, `amare-security`, `amare-update`,
  `amare-ffi`, `amare-platform`
- 테스트: UI smoke 48, Windows probes, interop 70, `fixture_server.py`,
  `gpui-reload-probe.ps1`의 HTTP 계측 아이디어

### 참고 후 재설계

- GPUI `SessionWindowSnapshot` → WinUI 창 단위 세션
- GPUI `password_vault.rs` → WinUI Vault/비밀번호 저장
- GPUI 다운로드 관리/설정 카테고리/한국어 문자열

### 이관하지 않음

- GPUI custom rendering/input 계층
- GPUI 전용 자동화/status probe
- GPUI library/workspaces/tasks (현재 요구 범위 밖)
- GPUI 기본 패키지 전환

---

## 5. P0 / P1 / P2 게이트

### P0 — 이 단계가 끝나기 전에는 개인용 완료로 판정하지 않는다

1. ~~멀티윈도우 세션 last-writer-wins~~ — **RESOLVED (Phase 1.1)**
2. WinUI 비밀번호 관리자/Vault 부재
3. 실제 Windows 11 핵심 입력(IME/DPI/Snap/Alt+Tab) 미검증

### P1 — 개인용 핵심 기능/배포

- ~~사이트 권한~~ — **RESOLVED (Phase 2.1, CI)**
- AI 패널 실제 서비스/세션 격리 — **Actual API provider DONE (2.2); real E2E/web service UNVERIFIED**
- 업데이트 설치 end-to-end
- WinUI/GPUI 데이터 루트 불일치
- WinUI 멀티윈도우 독립 navigation
- First-click 6/6 최신 재실행
- Backup/Restore UI
- 기본 브라우저 등록
- 한국어 UI/IME — **Korean/English resource DONE (2.3); IME/DPI UNVERIFIED**

### P2 — 품질/자동화

- 다운로드 이력 SQLite/취소 UI
- 설정 범위/로컬라이제이션
- UIA/titlebar 자동화 안정성
- MSIX 서명
- Reader 복잡 사이트 품질
- 30탭 메모리/장시간 누수
- GPUI 동결 유지보수

**게이트 규칙**

- P0가 하나라도 열려 있으면 Phase 4 패키지 생성 금지.
- P1이 열려 있으면 “개인용 완료”가 아니라 “개발 빌드”다.
- P2는 완료를 막지 않지만, 최종 패키지 전에 목록과 수용 여부를 기록한다.
- FAIL을 UNVERIFIED로 바꾸거나 assertion을 약화해 통과시키지 않는다.

---

## 6. 테스트 체계 개편

### A. Unit tests

목적: 빠른 반복, 변경 모듈 중심.

- Rust: `cargo test` (core/store/services/security/update/platform/ffi)
- C#: `AMARE.Windows.Core` 정책/모델 테스트
- 대상: 주소 해석, 검색 인코딩, 세션 planner, download policy, 암호화,
  checksum, migration, rollback, shortcut routing
- 목표: 10분 이내, 모든 변경에서 실행
- 원칙: mock으로 계약을 검증하되, 제품 동작 증거로 과대평가하지 않는다.

### B. Feature integration tests

목적: 기능 완료 시 독립 검증.

- WinUI UI smoke: 기능별 1개 이상 시나리오
- WebView2 probe: 실제 navigation/profile/download/permission
- Interop: C#↔Rust 계약
- Fixture: HTTP request counter/readiness (`tools/fixture_server.py`)
- 대상: 탭, 주소창, navigation, 세션, 비밀번호, 권한, AI, Reader, Find,
  업데이트, 다운로드
- 목표: 기능 PR마다 해당 suite 실행, 30~60분
- 원칙: 최초 실패와 재시도 결과를 모두 보존한다.

### C. Full regression

목적: 마일스톤/패키지 전/실기기 검증.

- `tools/verify-windows.ps1 -CI`
- UI smoke 48 + Windows 143 + interop 70
- 전체 feature integration
- GPUI는 **동결 참고 실행**만 하고 WinUI 집계에 넣지 않는다.
- 실제 Windows 11 수동 체크리스트 (`docs/WINDOWS_11_MANUAL_CHECKLIST.md`)
- 목표: 패키지 전 1회, 릴리스 후보에서 2회 연속, 실기기 1회
- 원칙: CI PASS와 실기기 PASS를 별도 표로 기록한다.

### 결과 기록 형식

```text
run_id:
head:
environment:
suite:
first_failure:
retry_result:
product_code_reached:
native_message_received:
webview_event_received:
fixture_ready:
classification: PRODUCT_DEFECT | TEST_AUTOMATION_BUG | ENVIRONMENT_LIMITATION | UNRESOLVED
```

### CI 배치 제안

| 시점 | 실행 |
|---|---|
| PR | Unit + 변경 기능 integration |
| master merge | Unit + WinUI feature suite |
| 주 1회 | Full regression (CI) |
| 패키지 전 | Full regression + real Win11 checklist |
| GPUI | 동결 상태 빌드/기존 suite 보존, 결과는 참고 |

---

## 7. WinUI 완성 로드맵

### Phase 1 — 브라우저 핵심 안정화

**목표:** 개인용으로 매일 켜도 데이터가 사라지지 않고, 핵심 입력이 실기기에서
동작하는 상태.

| 작업 | 내용 | 예상 |
|---|---|---:|
| 1-1 멀티윈도우 세션 | 창 ID/그룹 모델, migration, 창별 저장/복원, 마지막 창 판정 | **DONE** (`bcd476d`) |
| 1-2 비밀번호/Vault | Rust 암호화 + Windows 보호 저장소, 저장/자동완성/잠금/삭제 | **Vault UI DONE; WebView2 자동완성/실기기 남음** |
| 1-3 Win11 조기 스모크 | 실행/navigation/IME/DPI/Snap/Alt+Tab 1차 확인 | 1–2일 |
| 1-4 WinUI A/B fixture | A/B 독립 navigation, HTTP counter, readiness | 1–2일 |
| 1-5 lifecycle/장시간 | 탭 30/50, 창 3, 반복 open/close, 핸들/메모리 | 1–2일 |
| 1-6 다운로드 정리 | SQLite 이력, 취소/재시도/열기, 경로 설정 | 1–2일 |
| 1-7 first-click 회귀 | New Tab/Settings/Password/Downloads/Bookmarks/Address | 0.5–1일 |

**Exit criteria**

- P0-2 구현/검증, P0-3 해소 (P0-1은 완료)
- 세션 2창/3창/강제 종료/한 창 닫기 테스트 PASS (완료)
- 비밀번호 저장/자동완성/잠금/삭제/오류 키 거부 PASS
- WinUI 49/49, 143/0, interop 76/76 유지
- 실제 Win11 핵심 스모크 PASS
- 새 기능의 최초 실패/재시도 기록 존재

**예상:** **12–19 person-days (남은 Phase 1)** — Vault UI 완료분 제외

> 기존 12–22일 추정은 세션 P0만 기준이었다. Vault 구현/자동완성/보안 검증을
> 포함하면 상향이 필요하다.

---

### Phase 2 — 주요 기능 완성

**목표:** 개인용 완료 기준의 기능 결손을 없앤다.

| 작업 | 내용 | 예상 |
|---|---|---:|
| 2-1 사이트 권한 | `PermissionRequested`, per-profile store, 허용/차단/초기화 | **DONE (Phase 2.1, CI)** |
| 2-2 AI 패널/API | 실제 서비스 로그인/격리, 실제 API 1회, 키 삭제 | **Actual API DONE (CI); real E2E UNVERIFIED** |
| 2-3 설정/한국어 | 다운로드/개인정보/권한/언어, 리소스 문자열 | **Core Settings/localization DONE (2.3); Windows 11 layout UNVERIFIED** |
| 2-4 업데이터 E2E | Portable ZIP 적용/롤백/health marker/데이터 보존 | 3–5일 |
| 2-5 Reader/Find 보완 | 실제 사이트 표본, IME 중 Find | 1–2일 |
| 2-6 Backup/Restore | 사용자 백업/복구 UI, 검증/체크섬 | 2–3일 |

**Exit criteria**

- P1 기능 결손 해소
- 사이트 권한/백업/업데이트가 실제 시나리오에서 PASS
- AI 실제 호출/취소/오류/키 삭제 PASS
- 기능별 integration suite 존재

**예상:** 13–22 person-days

---

### Phase 3 — Windows 11 실사용 안정화

**목표:** CI가 아닌 실제 Windows 11 개인 PC에서 매일 사용 가능한 상태.

| 작업 | 내용 | 예상 |
|---|---|---:|
| 3-1 DPI/멀티모니터 | 100/125/150/200%, 창 이동, 재배치 | 2–3일 |
| 3-2 한국어 IME | 주소창/페이지/Find/비밀번호 입력, 조합/Enter | 2–3일 |
| 3-3 Snap/Alt+Tab/작업표시줄 | Snap Layouts, 그룹화, 포커스 | 1–2일 |
| 3-4 장시간/자원 | 8시간 soak, 30/50탭, 창 반복, 핸들/메모리 | 2–3일 |
| 3-5 기본 브라우저 | 등록/해제, 시작 인자, 복원 | 1–2일 |

**Exit criteria**

- `WINDOWS_11_MANUAL_CHECKLIST.md`의 P0/P1 항목 PASS
- 치명적 크래시 0
- IME/DPI/Snap/Alt+Tab 실제 확인
- 장시간 실행 후 데이터 손실/누수 없음

**예상:** 5–8 person-days + 실기기 접근 시간

---

### Phase 4 — 개인용 최종 패키지

| 작업 | 내용 | 예상 |
|---|---|---:|
| 4-1 Portable ZIP | x64/ARM64, self-contained, 실행 확인 | 1일 |
| 4-2 데이터 무결성 | 업그레이드/다운그레이드/rollback/backup | 2–3일 |
| 4-3 패키지 검증 | SHA-256, unsigned 표시, 설치 한계 문서화 | 1일 |
| 4-4 최종 회귀 | 2회 연속 full regression + 실기기 | 2–3일 |

**Exit criteria**

- ZIP 실행/데이터 보존/업데이트/롤백 PASS
- SHA-256 기록, unsigned/MSIX BLOCKED 명시
- 공개 Release 없이 개인 배포 가능
- P0/P1 미해결 항목 없음

**예상:** 4–6 person-days

---

## 8. 예상 기간과 불확실성

| 구분 | 예상 |
|---|---|
| Phase 1 (남은) | **12–19 person-days** |
| Phase 2 | 13–22 person-days |
| Phase 3 | 5–8 person-days + 실기기 |
| Phase 4 | 4–6 person-days |
| 합계 (남은) | **34–55 person-days** |
| full-time 1인 | **7–11주** |
| part-time | **3–5개월** |

Phase 1.2 Vault 감사로 P0-2의 실제 구현 범위(키 보호/자동완성/복구/실패 테스트)가
확인되어 기존 12–22일 추정을 상향했다.

불확실성:

| 요인 | 영향 | 완화 |
|---|---|---|
| Windows 11 실기기 접근 | 높음 | Phase 1에서 조기 스모크 |
| WebView2/AI 외부 서비스 | 중~높음 | 실제 서비스 표본 고정, 실패 로그 |
| 세션 schema migration | 중간 | 복사본에서 dry-run, rollback 테스트 |
| Windows Hello/DPAPI | 중간 | Credential Manager 우선, 단계적 |
| MSIX 서명/설치 | 높음(외부) | Portable ZIP 1차, MSIX는 BLOCKED 유지 |
| GPUI 동결 영향 | 낮음 | 공통 crate 변경 시 양쪽 빌드 확인 |
| 자동화 flakiness | 중간 | first failure 보존, 환경 실패 분리 |

이 기간은 **기능을 새로 만드는 기간이 아니라 검증된 기능을 유지하면서 P0/P1을
메우는 기간**이다. 요구가 늘어나면 별도 승인과 재산정이 필요하다.

---

## 9. 개인용 최종 완료 조건

아래 조건을 모두 만족해야 “개인용 AMARE 완성”으로 판정한다.
하나라도 미충족이면 완료가 아니다. 기능을 제거/비활성화해 통과시키지 않는다.

| # | 조건 | 검증 방법 |
|---:|---|---|
| 1 | Windows 11에서 정상 실행, 치명적 크래시 없음 | 실기기 + soak |
| 2 | 주요 웹사이트 브라우징 | 실제 사이트 표본 |
| 3 | 탭/멀티윈도우 정상 | UI smoke + 실기기 |
| 4 | 주소창/한국어 IME 정상 | 실기기 입력 (localization DONE, IME UNVERIFIED) |
| 5 | 북마크/방문 기록 유지 | 재시작 후 확인 |
| 6 | 다운로드 정상 | 실제 파일 다운로드 |
| 7 | 세션 복원/충돌 복구 | 2·3창 + 강제 종료 |
| 8 | 비밀번호 저장/자동완성 보안 | unit + 실기기 |
| 9 | AI Web Panel 정상 | 실제 서비스 + API provider contract (Phase 2.2); real API E2E UNVERIFIED |
| 10 | Reader/Find 정상 | UI smoke + 실기기 |
| 11 | 사이트 권한 설정 가능 | UI smoke 73/73 + Store/FFI + legacy audit (Phase 2.1/2.1.1); 실제 Windows 11은 UNVERIFIED |
| 12 | 설정/데이터 보존 | 업데이트 전후 비교 |
| 13 | 업데이트/실패 복구 | ZIP apply/rollback |
| 14 | 장시간 사용 중 치명적 크래시 없음 | 8시간 soak |
| 15 | 최종 ZIP 실행/데이터 무결성 | 패키지 검증 |

추가 품질 기준:

- P0 0개, P1 0개(또는 명시적 수용), P2 목록 문서화
- WinUI 기준선 유지: 49/49, 143/0, 76/76 (package 단계는 skip)
- GPUI 결과는 참고로만 기록, 합산 없음
- 모든 FAIL/UNVERIFIED/BLOCKED에 근거와 다음 조치 기록

---

## 10. 다음 개발 작업의 구체적인 순서

승인 직후 **Phase 1**에서 다음 순서로 진행한다.

1. **동결 선언 확인**
   - GPUI 신규 기능/테스트 확장 중단
   - 소스/패키지/증거/stash 보존 확인
2. **Phase 1 브랜치 생성**
   - `master`에서 작업 브랜치
   - 시작 HEAD/working tree/stash 기록
3. ~~실패 테스트 먼저 작성 — 세션~~ **DONE**
   - 2창/3창/한 창 닫기, 저장 순서, 강제 종료, v4 migration
4. ~~세션 schema 설계 리뷰~~ **DONE**
   - v5 migration dry-run, 원본 hash 불변
5. ~~멀티윈도우 세션 구현~~ **DONE** (`bcd476d`)
   - 창별 저장/복원, 마지막 창 판정, 강제 종료
6. **비밀번호/Vault (Vault UI 완료, 1.3D autofill)**
   - WebView2 login capture + exact origin/top-level/no auto-submit
   - builtin autofill 비활성/동의 후 clear
   - 실제 5분 idle/session lock/profile switch/multiwindow UI 검증
   - Windows 11 실기기 검증
7. **WinUI A/B fixture 테스트**
   - HTTP counter/readiness
   - A/B 독립 navigation, 다른 창 불변 확인
8. **Windows 11 조기 스모크**
   - 실행/IME/DPI/Snap/Alt+Tab
   - 실패는 즉시 P0/P1로 등록
9. **first-click/keyboard 회귀 재실행**
   - 6개 control, Ctrl+L/T/W/N/R/text
   - 최초 실패 보존
10. **Phase 1 full regression**
    - 49/49, 143/0, 76/76
    - 새 세션/비밀번호 suite
11. **Phase 1 문서/증거 커밋**
    - 코드와 문서 분리 commit
    - 패키지는 만들지 않음
12. **승인 후 Phase 2 진입**
    - ~~사이트 권한~~ **DONE (Phase 2.1)** → AI → 설정 → 업데이터 → 백업

각 단계는 “제품 결함”과 “테스트 자동화 결함”을 분리해 기록한다.

---

## 11. 변경 통제

### 이번 Phase 0에서 금지된 것 (완료)

- 제품 코드 수정 없음
- 프레임워크 마이그레이션 없음
- GPUI 삭제 없음
- 사용자 데이터 삭제 없음
- DB 스키마 변경 없음
- 패키지 재생성 없음
- 공개 GitHub Release 없음
- 인증서 생성/서명 없음
- force push/history rewrite 없음

### Phase 1 이후 규칙

- P0/P1 이슈는 이슈 번호와 재현 절차를 먼저 기록
- 스키마 변경은 migration + backup/restore + dry-run 필수
- 보안 저장은 평문 로그/스크린샷/status JSON 금지
- 테스트 약화/삭제 금지
- GPUI 결과를 WinUI 통과 근거로 사용 금지
- 패키지는 full regression 후에만 생성
- 공개 Release는 별도 승인

---

## 12. 승인 체크리스트

- [ ] WinUI 3 + WebView2 + C# 최종 방향 승인
- [ ] GPUI 동결·보존 승인
- [ ] 멀티윈도우 세션 P0 수정 승인
- [ ] 비밀번호/Vault P0 구현 승인
- [ ] 실제 Windows 11 실기기 검증을 Phase 3 필수 게이트로 승인
- [ ] Portable ZIP 1차 배포, MSIX는 BLOCKED 유지 승인
- [ ] GPUI 데이터 자동 이전 금지, 필요 시 명시적 import 승인
- [ ] Phase 1 예산/기간(12–22 person-days) 승인

승인 전까지 **제품 아키텍처와 기본 패키지를 변경하지 않는다.**
WinUI를 기본 패키지로 유지한다.
