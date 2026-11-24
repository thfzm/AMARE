# AMARE Phase 1.3C — WinUI Password Vault UI Report

- 상태: **WinUI Vault UI 구현 완료 / 일부 Windows 시나리오 UNVERIFIED**
- Branch: `feature/phase-1-vault-ui`
- 시작 HEAD: `4ca823e` (`feature/phase-1-vault-ffi`)
- 제품/테스트 HEAD: **`7e09d04`**
- 문서 HEAD: `7685b10` (`Document Phase 1.3C WinUI vault UI results`)
- 작성일: 2026-11-24
- 범위: WinUI Vault UI, 수명 관리, idle/session lock, clipboard, backup UI.
- 범위 밖: WebView2 로그인 감지/자동완성/폼 주입 (`get_for_fill` 호출 포함), Windows Hello, 패키지.

---

## 1. Git / 무결성

| 항목 | 결과 |
|---|---|
| 시작 branch | `feature/phase-1-vault-ffi` |
| 시작 HEAD | `4ca823e` |
| 작업 branch | **`feature/phase-1-vault-ui`** |
| 최종 제품/테스트 HEAD | **`7e09d04`** |
| Working tree | clean (문서 commit 전) |
| Stash | `stash@{0}` 보존 |
| Phase 1.3A/1.3B | 보존 |
| GPUI 변경 | 없음 |
| DB schema | **v5 유지, migration 없음** |
| ABI 1 | 변경/삭제 없음 |
| 패키지/MSIX/Release | 없음 |

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
- ABI profile 공유 보완: `crates/amare-ffi/src/shared.rs`, `services.rs`, `services_v2.rs`,
  `crates/amare-services/src/profile.rs`

---

## 3. UI 구조와 상태

Vault는 기존 AI/Reader와 같은 **콘텐츠 오버레이 패널**로 통합했다. 새 영구 좌측
사이드바는 추가하지 않았다.

- `VaultHost` overlay
- header: 상태, Create/Unlock/Refresh, Add, Backup, Lock, Close
- account list (`VaultEntryList`) — title/username/origin만 표시, password 없음
- detail: title/origin/username, masked PasswordBox, Reveal/Copy/Edit/Delete
- editor: site, origin, username, password, reveal checkbox, Save/Cancel
- backup menu: export, validate, import
- `VaultIdleMonitor` / `VaultSessionMonitor`

상태:

- `Closed`
- `Uninitialized` — Create vault
- `Locked` — Unlock
- `Loading`
- `Unlocked`
- `Empty`
- `Error`

---

## 4. ABI 2 client 수명 / profile 전환

- ABI1 profile 초기화 완료 후 `_context`가 준비되면 ABI2 `AmareVaultClient`를 생성.
- root는 ABI1과 동일한 `<appDataRoot>\data`.
- `_profileId`와 `_context.Generation`이 바뀌면 이전 client를 lock/dispose하고 재생성.
- profile 전환/삭제 전 `ResetVaultForProfileChangeAsync`:
  - native Vault를 lock
  - ABI2 handle Dispose
  - UI 목록/상세/secret/편집 상태 제거
  - 다른 창에 profile change broadcast
- 새 profile context로 ABI2 client 재생성.
- ABI1/ABI2가 같은 `ProfileService`를 공유하도록 FFI를 보완했다
  (`shared.rs`). 이로써 ABI1 context generation과 ABI2 context generation이 일치한다.
- 초기화 실패는 `VaultErrorText`에 일반화된 메시지로 표시하고 unlocked 상태로
  남기지 않는다.

**UNVERIFIED:** 실제 UI에서 profile switch 후 ABI2 client 재생성/이전 profile
데이터 접근 차단은 WinUI smoke에 포함되지 않았다. native profile 격리는
ABI2/Core 테스트로 검증했다.

---

## 5. 다중 창 동작

- 각 창은 자체 `AmareVaultClient` handle과 UI state를 가진다.
- `VaultWindowCoordinator`로 다음을 broadcast:
  - lock 요청
  - unlock 알림(같은 profile만)
  - profile 변경
- lock 시 각 창이 **실제 native `vault.lock`**을 호출한 뒤 UI를 비운다.
- UI만 잠금 표시하고 native Vault를 열어두는 경로는 없다.
- idle/session lock도 같은 lock broadcast를 사용한다.
- 같은 profile의 다른 창에서 unlock하면 해당 창도 native unlock을 수행한다.

**UNVERIFIED:** 다중 창 lock/unlock 일관성은 WinUI smoke에 포함되지 않았다.
기존 multiwindow UI harness의 UIA flake도 있어 별도 실기기 검증이 필요하다.

---

## 6. Idle / Session lock

- 기본 idle timeout **5분** (`VaultIdlePolicy`).
- `VaultIdleMonitor` singleton timer (5초 tick), 모든 창이 하나의 timer를 공유.
- 활동 감지:
  - RootGrid PointerMoved/PointerPressed/KeyDown
  - Window Activated
  - Vault 작업/버튼 조작
  - WebView2 페이지 내부 입력은 자동 감지 범위에 포함되지 않는다 (명시적 한계).
- idle 만료 시 lock broadcast.
- `VaultSessionMonitor`는 `Microsoft.Win32.SystemEvents.SessionSwitch`의
  SessionLock/ConsoleDisconnect/RemoteDisconnect를 사용한다.
- session event를 받지 못하는 환경은 UNVERIFIED.

**UNVERIFIED:** 실제 5분 idle UI와 Windows session lock을 실기기에서
검증하지 않았다. `VaultIdlePolicy` unit test는 PASS.

---

## 7. 비밀번호 평문 수명

- `status`/`list`에는 password가 없다.
- `get`은 다음 경우에만 호출:
  - 사용자가 Reveal을 클릭
  - 사용자가 Copy를 클릭
  - 사용자가 Edit를 클릭
- `get_for_fill`은 **이번 단계에서 호출하지 않는다.**
- `VaultEntryDto.Password`는 immutable string이며 `ClearPassword()`는 best-effort.
- Reveal/Hide, selection 변경, lock, panel close, profile change 시:
  - PasswordBox.Password = ""
  - PasswordRevealMode = Hidden
  - DTO `ClearPassword()`
- 편집/추가 저장 후 PasswordBox와 credential DTO 참조를 폐기.
- ViewModel/global state/검색 인덱스/진단에 password를 저장하지 않는다.
- immutable string이 실제 zeroize되지 않는다는 한계를 문서화했다.

---

## 8. Clipboard 정책

- 사용자가 Copy를 명시적으로 클릭할 때만 복사.
- `VaultClipboard`가 30초 후 정리 시도.
- 정리 전 현재 clipboard 텍스트가 복사한 값과 **정확히 같을 때만** `Clipboard.Clear()`.
- 사용자가 다른 값을 복사했다면 건드리지 않는다.
- lock / profile change / panel close 시 `ClearIfOwnedAsync`.
- Windows Clipboard History/Cloud Clipboard/다른 앱의 보존은 best-effort이며
  완전 삭제를 주장하지 않는다.

---

## 9. Backup UI

- Backup 메뉴:
  - Export encrypted backup
  - Validate backup
  - Import backup
- Export:
  - backup password + confirmation
  - encrypted payload만 파일에 기록 (`vault-*.amarevault`)
  - plaintext JSON/로그 없음
  - temp file 후 atomic move
- Validate:
  - 파일/password로 `vault.validate_backup`
  - entry/tombstone/format 요약
- Import:
  - “현재 Vault를 교체” 확인 문구
  - 잘못된 password/손상/format 오류는 기존 Vault 보존
  - 성공 시 목록 새로고침
- 파일 선택은 OS picker 대신 path TextBox를 사용해 자동화/일관성을 확보.
  사용자 경로를 직접 지정할 수 있다.

---

## 10. 테스트 결과 (실제 실행)

| Suite | 결과 |
|---|---|
| Rust ABI 1 | **7/7 PASS** |
| Rust ABI 2 | **5/5 PASS** |
| Linux Vault Core | 23/23 PASS |
| Windows Vault Core | 24/24 PASS |
| `amare-store` | 79/79 PASS |
| `amare-services` 기존 | 38/38 PASS |
| C# interop | **80/80 PASS** (기존 79 + idle policy 1) |
| WinUI UI smoke | **51/51 PASS** (기존 49 + Vault 2) |
| Windows CI 최종 | **147 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** |
| Linux CI | **SUCCESS** |

Windows 최종: [45371417168](https://github.com/thfzm/AMARE/actions/runs/45371417168)  
Linux 최종: [45374432218](https://github.com/thfzm/AMARE/actions/runs/45374432218)

Vault UI smoke:

```text
PASS vault_ui_initialize_lock_unlock
PASS vault_ui_crud_reveal_backup
```

`vault_ui_crud_reveal_backup` covers:

- add account
- list
- detail
- reveal → hide
- edit
- export encrypted backup
- failed import with wrong password (entries unchanged)
- delete
- valid import restore
- validate backup
- close/lock

8 SKIPPED = package 7 + Netflix/Disney 1. **패키지는 생성하지 않았다.**

---

## 11. 최초 실패와 수정

| # | 실패 | 분류 | 수정 |
|---:|---|---|---|
| 1 | `VaultStatusDto`/`AmareVaultClient` using 누락 | PRODUCT_BUILD_DEFECT | `using Amare.Windows.Core` |
| 2 | `ContentDialogButton.Cancel` 없음 | PRODUCT_BUILD_DEFECT | `Close` |
| 3 | Vault panel UIA 탐지 timeout (`VaultHost` Grid) | TEST_AUTOMATION_BUG | visible `VaultStatusText` 기준 탐지 |
| 4 | 두 번째 Vault smoke에서 panel toggle로 닫힘 | TEST_AUTOMATION_BUG | panel이 열려 있으면 재사용 |
| 5 | 최초 UI smoke에서 `tear_off_tab`/`tab_churn`/multiwindow 연쇄 실패 | TEST_AUTOMATION_BUG | Vault panel 상태 정리 후 재실행 PASS |

최초 Windows build run [45358382926](https://github.com/thfzm/AMARE/actions/runs/45358382926)은
컴파일 실패였고, 최초 UI Vault run [45364000703](https://github.com/thfzm/AMARE/actions/runs/45364000703)은
Vault panel 탐지 실패였다. 최초 로그를 evidence에 보존했다.

---

## 12. 미해결 / UNVERIFIED

1. 실제 5분 idle 후 UI lock — **UNVERIFIED** (`VaultIdlePolicy` unit test만 PASS)
2. Windows session lock 이벤트 실기기 — **UNVERIFIED** (`SystemEvents` 코드만)
3. 다중 창 lock/unlock 일관성 UI smoke — **UNVERIFIED** (broadcast 코드만)
4. profile switch 후 이전 Vault 접근 차단 UI — **UNVERIFIED** (native/profile 코드만)
5. 실제 Windows 11 IME/DPI/접근성 — **UNVERIFIED**
6. C# immutable string zeroize — 구조적 한계, best-effort only
7. WebView2 autofill / login detection / get_for_fill — 이번 단계에서 **미구현**
8. Windows Hello — 미구현
9. process kill 중 vault UI — UNVERIFIED
10. 실제 사용자 데이터가 아닌 smoke fixture만 사용

---

## 13. Phase 1.3D 준비

Phase 1.3D — WebView2 Password Capture & Autofill Security:

- `get_for_fill`을 처음 호출하는 단계
- exact origin, top-level, no auto-submit
- form detection / username/password field 검증
- login submit capture + 사용자 저장 승인
- WebView2 built-in password/general autofill 비활성 및 기존 데이터 clear
- clipboard/IME/frame/iframe 테스트
- Vault UI 51/51, Windows 147/0, interop 80/80 유지

OpenCode 프롬프트에 포함할 것:

- `get_for_fill`의 평문 수명
- exact origin/profile AAD 정책 유지
- 자동 입력/자동 submit 금지
- 사용자 명시적 Fill 승인
- built-in autofill 데이터 삭제는 사용자 동의
- 실제 Windows 11 실기기 검증 항목

---

## 14. 결론

**WinUI Vault UI 구현 범위는 PASS**:

- 초기화/lock/unlock
- account CRUD/list/detail/reveal/edit/delete
- backup export/validate/import
- 실패 import rollback
- profile별 ABI2 client 수명/재생성
- idle/session lock 코드
- clipboard 30초 best-effort
- 평문 로그/상태 미노출

단, idle/session/profile/multiwindow의 실제 WinUI/Windows 11 시나리오는
**UNVERIFIED**이며, 이 항목을 전체 Vault 완료로 과장하지 않는다.
