# AMARE Phase 1.3C — WinUI Password Vault UI Report

- 상태: **WinUI Vault UI PASS / 잔여 lock 경계 검증 PASS / Windows session lock·실기기 항목 UNVERIFIED**
- Branch: `feature/phase-1-vault-ui`
- 시작 HEAD: `4ca823e` (`feature/phase-1-vault-ffi`)
- 최종 제품/테스트 HEAD: **`8076759`**
- 문서 HEAD: 이 보고서를 포함한 문서 commit
- 작성일: 2026-11-24
- 범위: WinUI Vault UI, ABI 2 client 수명, lock 경계, clipboard, backup UI.
- 범위 밖: WebView2 로그인 감지/자동완성/폼 주입 (`get_for_fill` 호출 포함),
  Windows Hello, 패키지/MSIX/서명, WebView2 브라우저 영역 변경.

---

## 1. Git / 무결성

| 항목 | 결과 |
|---|---|
| 시작 branch | `feature/phase-1-vault-ffi` |
| 시작 HEAD | `4ca823e` |
| 작업 branch | **`feature/phase-1-vault-ui`** |
| 1차 UI 제품/테스트 HEAD | `7e09d04` |
| 잔여 lock 검증 제품/테스트 HEAD | **`8076759`** |
| Working tree | clean (문서 commit 전) |
| Stash | `stash@{0}` 보존 |
| Phase 1.3A/1.3B | 보존 |
| GPUI 변경 | **없음** |
| DB schema | **v5 유지, migration 없음** |
| ABI 1 | **변경/삭제 없음** (7/7) |
| 패키지/MSIX/Release/서명 | **없음** |
| `feature/phase-1-vault-webview` | 로컬 보존, rewrite/삭제 없음 |

제품/테스트 commit:

```text
1c9bbde Add WinUI password vault panel and lifetime management
6a7e268 Import Core vault DTO types in WinUI partial
67401b5 Use valid ContentDialog close button
9fdc9e9 Add WinUI vault UI smoke coverage
a6e9f1b Fix vault UI smoke panel detection and log lifecycle
81e1a22 Keep vault panel open between smoke tests
7e09d04 Cover vault edit and failed import in UI smoke
c12e52f Log vault profile switch diagnostics on smoke timeout
889384e Dump fresh main window on vault profile smoke timeout
d30ede6 Probe vault profile status text after switch
2bc2c96 Trace long status text assignments in smoke
b4b2d19 Verify vault profile isolation via profile menu state
cfb4b4b Log vault status and profile isolation timeout details
458ff68 Do not auto-create vault during profile isolation smoke
94aae57 Report persisted vault state before unlock
e645a3f Run vault lifetime smoke after window regression tests
8076759 Cap vault idle override at the five minute maximum
```

> `c12e52f`~`2bc2c96`은 실패 원인 진단을 위한 임시 계측이고,
> `b4b2d19`에서 진단 계측을 제거하고 실제 검증으로 대체했다. 진단 commit은
> history rewrite 없이 보존한다.

---

## 2. 변경 파일

- `apps/windows/AMARE.Windows/MainWindow.xaml`
- `apps/windows/AMARE.Windows/MainWindow.xaml.cs`
- `apps/windows/AMARE.Windows/MainWindow.Vault.cs` (신규)
- `apps/windows/AMARE.Windows/VaultIdleMonitor.cs`
- `apps/windows/AMARE.Windows/VaultSessionMonitor.cs`
- `apps/windows/AMARE.Windows/VaultWindowCoordinator.cs`
- `apps/windows/AMARE.Windows/VaultClipboard.cs`
- `apps/windows/AMARE.Windows/AMARE.Windows.csproj`
- `apps/windows/AMARE.Windows.Core/VaultIdlePolicy.cs`
- `apps/windows/AMARE.Interop.Tests/Phase27Tests.cs`
- `apps/windows/AMARE.Interop.Tests/Program.cs`
- `tools/windows-ui-smoke/Program.cs`
- ABI profile 공유 보완: `crates/amare-ffi/src/shared.rs`, `services.rs`,
  `services_v2.rs`, `crates/amare-services/src/profile.rs`
- **vault 상태 정정**: `crates/amare-services/src/vault.rs`
- **회귀 테스트**: `crates/amare-services/tests/vault.rs`,
  `crates/amare-ffi/src/services_v2.rs`

---

## 3. UI 구조와 상태

Vault는 기존 AI/Reader와 같은 **콘텐츠 오버레이 패널**로 통합했다. 새 영구 좌측
사이드바는 추가하지 않았다.

- `VaultHost` overlay
- header: 상태, Create/Unlock/Refresh, Add, Backup, Lock, Close
  (1.3D에서 Close는 Hide로 변경; 1.3D 보고서 참조)
- account list (`VaultEntryList`) — title/username/origin만 표시, password 없음
- detail: title/origin/username, masked PasswordBox, Reveal/Copy/Edit/Delete
- editor: site, origin, username, password, reveal checkbox, Save/Cancel
- backup menu: export, validate, import
- `VaultIdleMonitor` / `VaultSessionMonitor` / `VaultWindowCoordinator`

상태:

- `Closed`
- `Uninitialized` — Create vault
- `Locked` — Unlock
- `Loading`
- `Unlocked`
- `Empty`
- `Error`

### 3.1 상태 판정 결함 수정

진단 중 **기존 Vault가 있는데도 새 ABI 2 client가 `initialized=false`를 반환**하는
제품 결함을 발견했다. 이 때문에 잠긴 Vault가 "Create vault"로 표시됐다.
`VaultService::status`가 in-memory 상태가 비어 있을 때 영속 secret 존재 여부를
확인하도록 수정했다.

- 기존 Vault + 새 client → `initialized=true`, `unlocked=false` → **Locked**
- Vault가 없는 profile → `initialized=false` → **Uninitialized**
- 기존 데이터는 변경하지 않는다 (status 조회만 수행).

---

## 4. ABI 2 client 수명 / profile 전환

- ABI 1 profile 초기화 후 ABI 2 `AmareVaultClient` 생성.
- root는 ABI 1과 동일한 `<appDataRoot>\data`.
- `_profileId`/`_context.Generation`이 바뀌면 이전 client를 lock/dispose 후 재생성.
- profile 전환/삭제 전 `ResetVaultForProfileChangeAsync`:
  - native Vault lock
  - ABI 2 handle Dispose
  - UI 목록/상세/secret/편집 상태 제거
  - 다른 창에 profile change broadcast
- ABI 1과 ABI 2가 같은 `ProfileService`를 공유해 context generation을 일치시켰다
  (`crates/amare-ffi/src/shared.rs`).
- 초기화 실패 시 UI를 unlocked로 남기지 않고 일반화된 오류를 표시한다.

**검증됨 (UI smoke `vault_ui_profile_switch_isolation`):**

1. default profile에서 Vault를 unlock하고 계정 수를 기록.
2. 새 profile을 생성하고, profile 메뉴의 `(current)` 표시로 전환 확인.
3. 새 profile의 Vault는 **Not initialized / 0 entries** — 이전 profile 데이터 없음.
4. default로 복귀 후 Vault는 **Locked**, unlock 후 계정 수가 이전과 동일.

---

## 5. 다중 창 동작

- 창마다 자체 `AmareVaultClient` handle과 UI state.
- `VaultWindowCoordinator`가 lock 요청, 같은 profile의 unlock, profile 변경을 broadcast.
- lock 시 각 창이 **실제 native `vault.lock`**을 호출한 뒤 UI를 비운다.
- UI만 잠그고 native Vault를 열어두는 경로는 없다.

**검증됨 (UI smoke `vault_ui_multiwindow_lock`, 실제 두 개의 WinUI 창):**

1. main 창에서 Vault unlock.
2. 새 창(Ctrl+N)에서 Vault unlock → main과 동일 상태.
3. 두 번째 창에서 Lock → **main 창도 Locked**.
4. main에서 Unlock → **두 번째 창도 Unlocked**.
5. 두 번째 창을 닫고 main Vault를 정리.

---

## 6. Idle / Session lock

- 기본 idle timeout **5분** (`VaultIdlePolicy`, unit test PASS).
- `AMARE_VAULT_IDLE_SECONDS` override는 테스트용으로 **단축만** 허용하며 5분을
  넘길 수 없다.
- `VaultIdleMonitor` 단일 timer (5초 tick), 모든 창이 공유.
- 활동 감지: RootGrid pointer/key, Window Activated, Vault UI 조작.
- **WebView2 페이지 내부 입력은 자동 감지 범위 밖** (문서화된 한계).
- `VaultSessionMonitor`는 `Microsoft.Win32.SystemEvents.SessionSwitch`의
  SessionLock/ConsoleDisconnect/RemoteDisconnect를 사용.

**검증됨 (UI smoke `vault_ui_idle_lock`):**
`AMARE_VAULT_IDLE_SECONDS=30`으로 timeout을 단축해, 실제 WinUI에서
idle 만료 → native `vault.lock` → 목록/상세 제거 → 재unlock까지 확인했다.

**UNVERIFIED:**
- 실제 기본값 5분 대기
- 실제 Windows workstation lock에 의한 session lock (CI에서 잠금 불가)
- idle timer와 백그라운드 WebView2 활동의 상호작용

---

## 7. 비밀번호 평문 수명

- `status`/`list`는 password-free.
- `get`은 명시적 Reveal/Copy/Edit에서만 호출.
- **`get_for_fill`은 이번 단계에서 호출하지 않는다.**
- Reveal/Hide, selection 변경, lock, panel close, profile change 시:
  - PasswordBox.Password = ""
  - PasswordRevealMode = Hidden
  - DTO `ClearPassword()`
- ViewModel/global state/검색/진단에 password를 저장하지 않는다.
- `VaultEntryDto.Password`는 immutable string이며 `ClearPassword()`는
  실제 zeroize가 아닌 best-effort다.

---

## 8. Clipboard 정책

- 명시적 Copy 동작에서만 복사.
- 30초 후 best-effort 정리.
- 현재 clipboard 텍스트가 AMARE가 복사한 값과 **정확히 같을 때만** clear.
- 다른 내용이면 건드리지 않는다.
- lock/profile change/panel close 시 `ClearIfOwnedAsync`.
- Windows Clipboard History/Cloud Clipboard는 통제 밖임을 문서화했다.

---

## 9. Backup UI

- Export: backup password + confirmation, 암호화 payload만 파일 저장,
  temp file 후 atomic move.
- Validate: 파일/password 검증, entry/tombstone/format 요약.
- Import: 현재 Vault 교체 확인, 실패 시 기존 Vault 보존, 성공 시 목록 새로고침.
- 잘못된 password import가 entry를 변경하지 않는 것을 UI smoke로 확인했다.
- OS picker 대신 path TextBox를 사용해 자동화/일관성을 확보했다.

---

## 10. 테스트 결과 (최종)

| Suite | 결과 |
|---|---|
| Rust ABI 1 | **7/7 PASS** |
| Rust ABI 2 | **6/6 PASS** (기존 5 + persisted status 1) |
| Linux Vault Core | **24/24 PASS** (기존 23 + 1) |
| Windows Vault Core | **25/25 PASS** (기존 24 + 1) |
| `amare-store` | 79/79 PASS (기존 기준선) |
| `amare-services` 기존 | 38/38 PASS |
| C# interop | **80/80 PASS** |
| WinUI UI smoke | **54/54 PASS** (기존 49 + Vault 5) |
| Windows CI 최종 | **147 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** |
| Linux CI | **SUCCESS** |

Windows 최종: [45477499016](https://github.com/thfzm/AMARE/actions/runs/45477499016)
Linux 최종: [45480021199](https://github.com/thfzm/AMARE/actions/runs/45480021199)

Vault UI smoke:

```text
PASS vault_ui_initialize_lock_unlock
PASS vault_ui_crud_reveal_backup
PASS vault_ui_profile_switch_isolation
PASS vault_ui_multiwindow_lock
PASS vault_ui_idle_lock
```

8 SKIPPED = package 7 + Netflix/Disney 1. **패키지는 생성하지 않았다.**

> `vault_ui_*` 5개 테스트는 기존 window/session 회귀 테스트 **뒤**에 배치했다.
> Vault 테스트가 추가 창 생성/전환을 수행해 기존 UIA 테스트의 focus 조건을
> 바꾸지 않도록 하기 위함이다.

---

## 11. UI smoke 증거

- `artifacts/windows/phase1.3c/` — 1차 51/51 증거 (기존)
- `artifacts/windows/phase1.3c/lock-verification/`
  - `final-45477499016/` — 최종 54/54, Windows 147/0
  - `final-45474184039/` — idle cap 이전 동일 결과 (54/54)
  - `flake-45471640788/` — vault 5개 PASS, `tear_off_tab` 기존 flake 실패 로그
  - `first-failure-45445869450/` — 최초 profile status timeout + certificate flake
  - `root-cause-45457832491/` — New Tab `data:` 차단이 status를 덮어쓴 stack trace
  - `auto-create-45463332983/` — smoke helper가 Vault를 자동 생성한 실패
  - `README.md` — 분석 요약
- `SHA256SUMS.txt` — 증거 파일 checksum.

모든 증거는 `vault-smoke.example` fixture 데이터만 포함하며 password, backup
plaintext, Vault JSON은 포함하지 않는다.

---

## 12. 최초 실패와 분류

| # | 실패 | 분류 | 수정/결과 |
|---:|---|---|---|
| 1 | `using Amare.Windows.Core` 누락 | PRODUCT_BUILD_DEFECT | using 추가 |
| 2 | `ContentDialogButton.Cancel` 미존재 | PRODUCT_BUILD_DEFECT | `Close` |
| 3 | `VaultHost` Grid UIA 미탐지 | TEST_AUTOMATION_BUG | visible `VaultStatusText` 기준 |
| 4 | 두 번째 Vault smoke에서 panel toggle로 닫힘 | TEST_AUTOMATION_BUG | panel 재사용 |
| 5 | Vault panel이 열린 채 후속 UIA 테스트 실패 | TEST_AUTOMATION_BUG | panel 정리 후 PASS |
| 6 | profile switch status timeout | **PRE-EXISTING PRODUCT DEFECT (out of scope)** | New Tab `data:` 차단이 `StatusText`를 4,624자로 덮어씀. profile 검증을 메뉴 `(current)` + Vault 격리로 변경 |
| 7 | 새 profile Vault가 `Not initialized`가 아님 | TEST_AUTOMATION_BUG | smoke helper가 primary button을 자동 실행 → `OpenVault(autoPrimary:false)` |
| 8 | default 복귀 후 `Locked`가 아님 | **PRODUCT_DEFECT** | `VaultService::status`가 영속 Vault를 감지하도록 수정 + 회귀 테스트 |
| 9 | `tear_off_tab`/`certificate`/`new_window`/`session_multiwindow`/`websocket` 간헐 실패 | PRE-EXISTING UI FLAKE | Vault 테스트와 무관(일부는 Vault 테스트보다 먼저 실행). 동일 HEAD 재실행 54/54 PASS, 실패 로그 보존 |

최초 Windows build run [45358382926](https://github.com/thfzm/AMARE/actions/runs/45358382926)은
컴파일 실패, 최초 Vault UI run [45364000703](https://github.com/thfzm/AMARE/actions/runs/45364000703)은
panel 탐지 실패였다. 실패 로그는 evidence에 보존했다.

---

## 13. 발견된 기존 결함 — New Tab `data:` navigation

- `NavigationPolicy`는 `data:`를 차단한다.
- 이 WebView2 버전에서 `NavigateToString`은 `data:text/html;charset=utf-8,...`
  NavigationStarting을 발생시키고, 앱이 이를 취소한다.
- 결과:
  1. New Tab 페이지 내용이 로드되지 않는다 (빈 화면).
  2. `StatusText`에 4,600~4,900자의 data URI가 기록되어 다른 상태 메시지를 덮어쓴다.
- Phase 1.3C 이전부터 존재하던 동작이며, Phase 1.3C는 WebView2 브라우저 영역을
  변경하지 않는다는 제약에 따라 **수정하지 않았다**.
- 권장 수정(별도 승인 필요):
  - `NavigateToString` 직전에만 허용되는 명시적 플래그 또는 정확한 payload 비교로
    해당 `data:text/html` 탐색 1회만 허용 (그 외 `data:`는 계속 차단),
  - status 텍스트 길이 상한/축약,
  - New Tab 렌더링과 차단 navigation 회귀 테스트 추가.
- Phase 1.3D(autofill/origin 검증) 전에 이 항목을 먼저 처리할지 결정해야 한다.

---

## 14. 미해결 / UNVERIFIED

1. 실제 기본값 5분 idle 후 UI lock — **UNVERIFIED** (30초 단축 UI smoke PASS)
2. 실제 Windows session lock — **UNVERIFIED** (SystemEvents 코드만)
3. 실제 Windows 11 IME/DPI/접근성 — **UNVERIFIED**
4. C# immutable password string zeroize — 구조적 한계, best-effort
5. 프로세스 강제 종료 중 Vault UI — UNVERIFIED
6. 비동기 작업 중 창 닫힘/취소의 모든 경합 — UNVERIFIED
7. WebView2 autofill/login detection/`get_for_fill` — Phase 1.3D 범위
8. Windows Hello — 미구현
9. New Tab `data:` navigation — 기존 결함, 미수정 (13절)
10. 실제 사용자 데이터가 아닌 fixture만 사용

---

## 15. Phase 1.3D 준비

Phase 1.3D — WebView2 Password Capture & Autofill Security:

- `get_for_fill` 첫 사용, exact origin, top-level document, no auto-submit
- username/password field 및 form action 검증
- login submit capture + 사용자 저장 승인
- WebView2 내장 password/general autofill 비활성 + 사용자 동의 후 clear
- iframe/cross-origin/IDN/punycode 테스트
- 실제 Windows 11 IME/autofill 검증
- Vault UI 54/54, Windows 147/0, interop 80/80 유지

진입 전 결정 필요:

- 13절의 New Tab `data:` navigation 결함을 1.3D와 함께/먼저 수정할지
- WebView2 브라우저 영역 변경에 대한 별도 승인
- `get_for_fill` 평문 수명과 로그 금지 규칙 유지

---

## 16. 결론

**Phase 1.3C PASS (실기기 항목 제외):**

- 초기화/lock/unlock, CRUD/list/detail/reveal/edit/delete
- backup export/validate/import, 실패 import rollback
- profile 전환 시 Vault 격리 (UI 검증)
- 다중 창 lock/unlock 일관성 (UI 검증)
- idle timeout → native lock (단축 timeout UI 검증)
- session lock 코드 경로 (실기기 UNVERIFIED)
- clipboard 30초 best-effort
- password-free status/list, `get_for_fill` 미호출
- 최종 Windows CI **147 PASS / 0 FAIL**, UI **54/54**, interop **80/80**, Linux SUCCESS

실제 5분 idle·Windows session lock·Windows 11 실기기 항목은 **UNVERIFIED**로
남기며, New Tab `data:` 결함은 범위 밖 기존 결함으로 보고한다.
