# AMARE Phase 2.5 — Portable Updater E2E & Rollback 보고서

- 상태: **PASS (CI Windows runner / Linux)**. 실제 Windows 11 실기기와 실제 배포 서버 E2E는 UNVERIFIED.
- 작업 branch: `feature/phase-2-portable-updater`
- 기준 branch: `feature/phase-2-vault-webview-integration`
- 기준 제품/테스트 HEAD: **`e1b9cbf`**
- 기준 문서 HEAD: **`3d44411`**
- 최종 제품/테스트 HEAD: **`fcf1ded`**
- 최종 문서 HEAD: **`85ab0ee`** (이 보고서, design, audit/plan/security/testing 문서 포함)
- DB schema 변경: **없음**
- MSIX/코드 서명/공개 Release/업로드: **없음**
- SHA-256의 의미: **파일 무결성**. 배포자 인증/서명이 아니다.

---

## 1. 최종 요약

| 완료 기준 | 판정 | 근거 |
|---|---|---|
| 업데이트 확인 → 다운로드 → 검증 → staging → 적용 연결 | **PASS (CI)** | C# 정책/통합 테스트 + Windows portable E2E |
| Stable/Prerelease 채널 및 SemVer 비교 | **PASS** | numeric prerelease ordering 테스트 |
| 검증 실패 파일 적용 금지 | **PASS** | hash/manifest/URL/size/traversal/executable 정책 |
| 업데이트 실패 시 이전 버전 복구 | **PASS (CI)** | apply 실패, launch 실패, crash recovery |
| 업데이트 중 사용자 데이터 보존 | **PASS (CI)** | profiles/Vault DB/key/site permissions/settings/bookmarks/history/downloads byte 비교 |
| 취소/중복 실행 안전 처리 | **PASS** | downloader cancel, UI guard, 부모/헬퍼 lock, repeat reject |
| Unit/Integration/Failure Injection | **PASS (CI)** | Phase24 + Phase34 + portable E2E |
| P0 데이터 손실/실행 불능 결함 | **없음** | 아래 테스트 및 rollback 검증 |
| 실제 Windows 11 실기기 | **UNVERIFIED** | CI는 Windows Server runner |
| 실제 GitHub Releases 공개 asset E2E | **UNVERIFIED** | 공개 Release/업로드 금지 |

---

## 2. 기준점 / branch

```text
feature/phase-2-vault-webview-integration
  e1b9cbf  Make Vault fill security tests behavior-based
  3d44411  Document Phase 2.4 Vault WebView integration security

feature/phase-2-portable-updater (신규)
  3736ff2  Harden portable updater and add package E2E
  <docs>   Document Phase 2.5 portable updater E2E and rollback
```

- 기존 branch와 GPUI 소스/증거는 삭제하거나 변경하지 않았다.
- force push/history rewrite를 하지 않았다.
- Phase 2.4의 Vault 암호화/DPAPI/backup/import, DB schema, Site Permissions,
  New Tab provenance, AI WebView 격리는 변경하지 않았다.

---

## 3. 기존 updater 감사 결과

기존 구현은 이미 다음을 갖고 있었다.

- `GitHubReleaseClient`: GitHub Releases API, stable/prerelease 선택, checksum asset 탐색.
- `UpdateDownloader`: SHA-256 sidecar 필수, `.part` 다운로드, hash/size 확인.
- `PortableUpdatePackage`: manifest schema/product/version/architecture, ZIP traversal/
  symlink/duplicate 거부, entry point/required files 확인.
- `PortableUpdateEngine`: SHA-256 재검증, version 비교, helper file lock, 부모 PID 종료 대기,
  app directory backup, staging 적용, health marker 확인, 실패 시 rollback, crash recovery.
- `PortableUpdateState`: `preparing/applying/applied/verifying/succeeded/rolling_back/
  rolled_back/failed`.
- `PortableUpdateLauncher`: helper를 data directory의 stable location으로 복사하고
  package hash/version/architecture/entry/parent PID를 전달.
- `tools/amare-updater`: `apply`, `recover`, `validate` CLI.
- `tools/verify-windows.ps1`: ZIP 생성, helper smoke, clean launch 검증.
- `Phase24Tests`: package validation, apply data preservation, rollback, crash recovery,
  launch failure, checksum/cancel, manifest 실패, repeat/downgrade, concurrent helper,
  health timeout, corrupt state.
- `.github/workflows/ci-windows.yml`: `skip_package` 입력. 기본 CI에서는 package 단계가 SKIPPED.

### 3.1 확인된 결손

| # | 결손 | 영향 |
|---:|---|---|
| 1 | `SemanticVersion.Compare` prerelease를 문자열 비교 | `beta.10 < beta.2` 같은 잘못된 선택 |
| 2 | downloader에 HTTPS/host 검증 없음 | HTTP/임의 URL 다운로드 가능 |
| 3 | downloader에 최대 크기/디스크 공간 검증 없음 | 과대 다운로드/디스크 고갈 |
| 4 | manifest entry point와 helper 요청 entry point 일치 강제 없음 | 다른 실행 파일을 entry로 지정 가능 |
| 5 | 위험한 script/installer/nested executable 정책 없음 | 예상치 못한 실행 파일 포함 |
| 6 | rollback 시 backup entry point 검증 없음 | 손상 backup으로 부분 복구 가능 |
| 7 | recovery 시도 횟수 제한 없음 | 무한 rollback/restart loop 가능 |
| 8 | pre-apply 실패가 "rollback 실패"로 오인되고 staging이 남음 | 잘못된 상태/임시 파일 |
| 9 | 기존 보존 테스트에 Vault DB/key, site permissions, Credential Manager 없음 | 데이터 보존 증거 부족 |
| 10 | 정상 helper E2E가 `skip_package=true`에서 모두 SKIPPED | 실제 ZIP/helper 검증 없음 |

기존의 정상 동작(traversal/symlink/duplicate, SHA-256, backup/apply/health/rollback,
parent-exit wait, helper lock, Phase24 tests)은 재작성하지 않고 유지했다.

---

## 4. 구현 변경 사항

### 4.1 버전 비교

`SemanticVersion`을 SemVer 2.0 precedence로 보강했다.

- numeric prerelease identifier는 numeric으로 비교:
  `alpha < alpha.1 < alpha.beta < beta.2 < beta.11 < rc.1 < 1.0.0`
- numeric identifier leading zero 거부, 빈 identifier/잘못된 문자 거부.
- build metadata는 precedence에 영향 없음.
- Windows `FileVersion` 호환을 위해 `major.minor.patch.revision` 4-part를 허용.
  revision이 있는 version에는 prerelease를 허용하지 않는다.
- stable channel은 prerelease 제외, prerelease channel은 stable+prerelease 포함.

### 4.2 업데이트 확인

- `CheckOnStartup`/`Stable|Prerelease` 설정은 기존 `update-preferences.json` 경로와 값 유지.
- 수동 확인은 기존 menu에서 항상 가능.
- `CheckAsync`는 다음을 구분한다.
  - rate limit
  - HTTP server error (status code 포함)
  - network error
  - invalid server response
  - timeout
  - user cancellation
- `_updateCheckRunning`으로 동일 확인 중복 실행을 막는다.
- 발견된 version/notes/checksum 유무를 기존 dialog에 표시한다.

### 4.3 다운로드

- package/checksum URL은 **HTTPS + GitHub host만** 허용.
- IP literal, userinfo, HTTP, FTP, 임의 host 거부.
- redirect 후 최종 URL도 재검증.
- 최대 package 크기 **1 GiB**, checksum **64 KiB**.
- release metadata size와 실제 byte 수 일치 확인.
- 다운로드 중 선언 크기/최대 크기 초과 시 즉시 중단.
- staging 전 disk 여유 공간 확인.
- collision-safe `.part` 이름, 성공 시 atomic move, 실패/취소 시 삭제.
- UI에 진행률, 취소 menu, 중복 다운로드 guard 추가.
- release에 checksum이 없으면 자동 다운로드 차단.
- `.sha256` sidecar와 `checksums.txt`를 모두 처리하되, `checksums.txt`에서는
  package file name과 일치하는 line의 hash만 사용한다(다중 asset hash 혼동 방지).

### 4.4 Manifest / ZIP / 무결성

- schema 1만 허용, product `AMARE`, version SemVer, architecture 일치.
- `Runtime`, `Architecture`, `EntryPoint` 필수 필드 확인.
- manifest는 최대 1 MiB.
- entry point는 package root의 `AMARE.Windows.exe`와 정확히 일치해야 한다.
- ZIP 정책:
  - absolute path, drive/colon, `..`, `.`, 중간 empty segment 거부
  - Windows reserved device name, trailing dot/space, control character 거부
  - symlink/특수 file type 거부
  - duplicate entry 거부
  - entry 수 20,000, uncompressed size 8 GiB 제한
  - `.bat/.cmd/.ps1/.vbs/.js/.hta/.msi/.msix/.scr/.com/.cpl/.jar/.reg/.lnk`
    등 script/installer 거부
  - nested `.exe` 거부, 위험한 root executable(`cmd.exe` 등) 거부
  - 알 수 없는 root `.exe`는 추출만 되고 entry point로 실행되지 않음
- 검증 실패 시 apply를 시작하지 않는다.

**중요:** 이 단계에는 publisher signature 검증이 연결되어 있지 않다. SHA-256은
전송/파일 무결성만 보장하며, GitHub release를 신뢰하는 범위를 넘는 배포자 인증이 아니다.

### 4.5 Staging

- staging directory는 `data/updates/work/staging-<guid>`.
- 기존 app/data를 staging 중 수정하지 않는다.
- `ExtractValidated`에서도 path/symlink/duplicate/size 정책을 다시 적용한다.
- disk 여유 공간 부족 시 apply 전에 거부.
- pre-apply 실패 시 staging/부분 backup을 정리하고 `failed` state를 기록한다.
- `EnsureReplaceable`로 대상 app 파일이 다른 process에 잠겨 있으면 apply 전에 거부.
- backup에 현재 entry point가 실제로 복사되었는지 확인한 뒤 `applying`으로 전환.

### 4.6 Apply / Restart

- helper는 부모 AMARE PID 종료를 최대 60초 대기.
- app directory 전체를 backup한 뒤 staging 파일을 적용.
- 각 파일은 같은 directory의 임시 파일에 복사한 후 `File.Move(overwrite)`로 교체.
- entry executable은 마지막에 교체.
- apply 후 `VERSION` 기록.
- `LaunchApp=true`이면 새 version을 health marker와 함께 실행.
- health marker가 확인되면 새 process를 **종료하지 않고 유지**한다(기존 bug 수정).
- health 실패/timeout이면 새 process를 종료하고 rollback.
- MSIX packaged build에서는 portable file replacement를 명시적으로 차단한다.

### 4.7 Rollback / Recovery

- rollback 전 backup의 entry executable 존재를 확인한다. 없으면 복사하지 않고
  `failed`로 기록하고 backup을 보존한다.
- rollback은 staging에만 있고 backup에 없는 새 파일을 제거한 뒤 backup 파일을 복원한다.
- 복원 후 app entry executable 존재를 확인한다.
- rollback 성공 시 staging/backup을 정리하고, 실패 시 backup을 삭제하지 않는다.
- `RecoveryAttempts`를 state에 기록하고 최대 **3회**로 제한한다.
- 제한 도달 시 `NeedsRecovery=false`, 자동 복구 menu를 숨기고, backup은 보존한다.
- 기존 state JSON에 `RecoveryAttempts`가 없으면 0으로 load한다.
- restart/rollback loop를 만들지 않는다.

---

## 5. 사용자 데이터 보호

업데이트 대상은 app directory뿐이다. 사용자 데이터는
`%LOCALAPPDATA%\AMARE`에 분리되어 있으며 updater는 이 directory의 user file을
삭제/초기화/재생성하지 않는다. updater 자신의 metadata(`update-state.json`,
`update.lock`, `updates/`, `updater/`, `logs/updater.log`)만 변경한다.

보존 검증 fixture:

```text
profiles/default/amare.db
profiles/default/site-permissions.json
vault/default.key                  (DPAPI-protected Vault key)
settings.json
app-preferences.json
update-preferences.json
ai-api-settings.json               (key 자체는 저장하지 않음)
bookmarks.db
history.db
downloads.json
profile.db
```

- apply 성공/rollback/recovery 전후 byte-for-byte SHA-256 비교.
- Windows Credential Manager의 AI API key는 updater가 접근하지 않는다.
  Windows runner에서는 실제 Credential Manager entry를 쓰고 apply 후 읽어 보존을 확인한다.
- DB schema 변경 없음.

---

## 6. 테스트 결과

### 6.1 로컬 managed 테스트 (Linux, `AMARE_CORE_ONLY=1`)

```text
SUMMARY passed=103 failed=0 core_only=True
```

Phase 2.5 신규 12 tests:

```text
PASS portable_update_semver_prerelease_ordering
PASS portable_update_channel_selection
PASS portable_update_update_check_errors
PASS portable_update_manifest_and_entry_policy
PASS portable_update_download_url_and_size_policy
PASS portable_update_download_limits_and_hash
PASS portable_update_preserves_all_user_data
PASS portable_update_rollback_preserves_all_user_data
PASS portable_update_staging_failure_never_touches_app
PASS portable_update_corrupt_backup_not_applied
PASS portable_update_recovery_attempt_limit
PASS portable_update_windows_credential_manager_untouched
     (Linux: UNVERIFIED detail; Windows runner에서 실제 검증)
```

### 6.2 Windows / Linux CI

| Suite | 결과 |
|---|---|
| Windows interop | **125/125 PASS** |
| WinUI UI smoke | **84/84 PASS** |
| Windows 통합 검증 | **195 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED** (211 total) |
| Portable updater E2E | **11/11 PASS** |
| Linux CI | **SUCCESS** (interop 125/125) |

Windows run: [45867470795](https://github.com/thfzm/AMARE/actions/runs/45867470795) (`fcf1ded`)
Linux run: [45861613864](https://github.com/thfzm/AMARE/actions/runs/45861613864) (`fcf1ded`)

중간 Windows run에서 UI automation flake가 2회 발생했다.
[45861607840](https://github.com/thfzm/AMARE/actions/runs/45861607840)은
`external_data_navigation_blocked`, [45864351395](https://github.com/thfzm/AMARE/actions/runs/45864351395)은
`vault_fill_settings_off`에서 실패했고, 동일 HEAD 재실행
[45867470795](https://github.com/thfzm/AMARE/actions/runs/45867470795)에서 UI **84/84**로 통과했다.

### 6.3 Portable updater E2E (실제 ZIP + helper)

`tools/verify-portable-updater.ps1`가 실제 WinUI build output과
`AMARE.Updater.exe`로 ZIP을 만들고, helper process를 실제 실행한다.

검증 항목:

- ZIP에 entry/helper/manifest 존재, user data 미포함
- ZIP file list/size/SHA-256 기록
- helper `validate`
- apply 성공 + 모든 user data byte 보존 + state `succeeded`
- duplicate apply 거부 + version/data 불변
- wrong SHA-256 거부 + app/data 불변
- app entry file lock 시 apply 전 거부 + app/data 불변
- health marker 없는 새 version 실행 실패 → 이전 version rollback
- apply 후 crash를 모사한 state/recover → 이전 version 복원
- 손상 backup recover 거부 + backup 보존 + app 부분 변경 없음
- recovery attempt 3회 제한 → 추가 복구 차단 + backup 보존

이 E2E는 MSIX/서명/공개 Release를 생성하지 않는다.

---

## 7. 최초 실패 및 수정

| # | 단계 | 실패 | 분류 | 수정 |
|---:|---|---|---|---|
| 1 | local managed run | data snapshot 11 → 13 files | TEST_FIXTURE | updater metadata(`update-state.json`, `update.lock`, `updates/`)를 user-data snapshot에서 제외 |
| 2 | local managed run | corrupt/capped recovery data snapshot changed `update-state.json` | TEST_FIXTURE | state/lock/health marker를 metadata로 분류 |
| 3 | code review | health 성공 후 새 app process를 kill | PRODUCT_BUG | healthy이면 process 유지, 실패/timeout만 kill |
| 4 | code review | pre-apply 실패가 rollback 실패로 오인 | PRODUCT_BUG | `preparing` 상태 실패는 staging 정리 후 `failed` 기록 |
| 5 | code review | rollback 후 새-only 파일 잔존 | PRODUCT_BUG | staging에만 있고 backup에 없는 파일 제거 |
| 6 | Windows E2E run [45853907994](https://github.com/thfzm/AMARE/actions/runs/45853907994) | data 상대경로가 `ata/logs/updater.log`로 계산되어 metadata 제외 실패 | TEST_AUTOMATION | `[IO.Path]::GetRelativePath`로 교체 |
| 7 | Windows E2E run 45853907994 | 수동 backup을 `updates/work` 아래에 두어 apply 시작 시 `CleanupStaleWork`가 삭제 | TEST_FIXTURE | 수동 backup을 data 밖 temp root로 이동 |
| 8 | Windows E2E run 45853907994 | `tear_off_tab` 기존 UI flake 1건 | PRE-EXISTING UI FLAKE | 동일 HEAD 재실행 [45858112497](https://github.com/thfzm/AMARE/actions/runs/45858112497)에서 84/84 PASS |
| 9 | Windows run 45861607840 | `external_data_navigation_blocked` UI fixture timeout | PRE-EXISTING UI FLAKE | 동일 HEAD 재실행 |
| 10 | Windows run 45864351395 | `vault_fill_settings_off` `MenuButton missing` | PRE-EXISTING UI FLAKE | 동일 HEAD 재실행 [45867470795](https://github.com/thfzm/AMARE/actions/runs/45867470795)에서 84/84 PASS |

제품 데이터 손실/실행 불능 P0는 발견되지 않았다.

---

## 8. 생성 패키지 정보

- artifact: `artifacts/windows/portable-updater-e2e.json`
- ZIP: `AMARE-0.92.0-portable-updater-e2e.zip`
- size: **152,001,418 bytes**
- SHA-256: **`de0662d6fd0943617d3e4c100fafd1e8c5f8f2bd2d17f81d162edd9def3e3f93`**
- file count: **360**
- entry/helper/manifest 포함, user data 미포함
- MSIX: 생성하지 않음
- 코드 서명: 없음
- 공개 Release/업로드: 없음
- 실제 Windows 11 실행: UNVERIFIED (CI Windows runner에서 helper 실행만 검증)

---

## 9. PASS / FAIL / UNVERIFIED / BLOCKED

**PASS**

- SemVer numeric prerelease ordering / Stable·Prerelease channel
- update check network/server/timeout/cancel 구분
- HTTPS/GitHub host URL, size, disk space, hash, manifest policy
- traversal/symlink/duplicate/dangerous executable 거부
- staging 실패 격리 및 정리
- apply 성공/rollback/recovery 데이터 보존
- 손상 backup 미적용 및 backup 보존
- recovery attempt cap
- duplicate/wrong hash/locked file/launch failure failure injection
- Windows Credential Manager 보존 (Windows runner)
- 기존 Settings/Vault/Site Permissions/AI/Downloads/localization 회귀 (CI)

**UNVERIFIED**

- 실제 Windows 11 실기기에서의 ZIP 다운로드/적용/재시작
- 실제 공개 GitHub Releases asset 다운로드 및 redirect
- 실제 배포 서버/사용자 환경의 방화벽/proxy/AV 간섭
- 전원 손실/강제 종료와 동일한 물리 조건
- 실제 Windows installer/MSIX 설치 성공 경로
- publisher signature 검증 (구현되지 않음)

**BLOCKED**

- 없음.

---

## 10. 남은 P0/P1/P2

### P0

- 없음. 이번 범위에서 데이터 손실/실행 불능 P0는 발견되지 않았다.

### P1

1. 실제 Windows 11 실기기에서 ZIP update E2E.
2. 실제 GitHub Releases 공개 asset 및 redirect 검증 (release 생성은 별도 승인).
3. 전원 손실/강제 종료 조건의 filesystem durability 검증.
4. publisher signature/서명 key 관리 설계 (현재 SHA-256은 무결성만).
5. helper self-update 중 running helper 교체 경합 검증.

### P2

1. update notes UI localization 잔여 문자열.
2. 대용량 package/저사양 disk soak test.
3. rollback 후 stale backup 보존 정책/정리 UX.
4. update 실패 telemetry/진단 bundle.

---

## 11. 변경 파일 / commit

제품/테스트:

- `apps/windows/AMARE.Windows.Core/UpdateService.cs`
- `apps/windows/AMARE.Windows.Core/PortableUpdatePackage.cs`
- `apps/windows/AMARE.Windows.Core/PortableUpdateEngine.cs`
- `apps/windows/AMARE.Windows.Core/PortableUpdateState.cs`
- `apps/windows/AMARE.Windows.Core/Localization.cs`
- `apps/windows/AMARE.Windows/MainWindow.xaml.cs`
- `tools/amare-updater/Program.cs`
- `apps/windows/AMARE.Interop.Tests/Phase23Tests.cs`
- `apps/windows/AMARE.Interop.Tests/Phase24Tests.cs`
- `apps/windows/AMARE.Interop.Tests/Phase34Tests.cs` (신규)
- `apps/windows/AMARE.Interop.Tests/Program.cs`

검증/CI:

- `tools/verify-windows.ps1`
- `tools/verify-portable-updater.ps1` (신규)
- `.github/workflows/ci-windows.yml`

문서:

- `docs/AMARE_PHASE2_PORTABLE_UPDATER_REPORT.md` (신규)
- `docs/PORTABLE_UPDATER_DESIGN.md` (신규)
- `docs/AMARE_FEATURE_AUDIT.md`
- `docs/AMARE_FINALIZATION_PLAN.md`
- `docs/SECURITY_DESIGN.md`
- `docs/WINDOWS_TESTING.md`
- `docs/AMARE_ARCHITECTURE_DECISIONS.md`

```text
3736ff2 Harden portable updater and add package E2E
1881aa9 Fix portable updater E2E path and backup fixtures
fcf1ded Select matching checksum entry from checksums.txt
<docs>  Document Phase 2.5 portable updater E2E and rollback
```

---

## 12. 결론

- Portable ZIP updater의 확인/다운로드/검증/staging/apply/rollback/recovery 흐름을
  기존 구조 위에서 보강했다.
- 검증 실패 파일은 적용되지 않고, apply/launch 실패는 이전 version으로 복구된다.
- profiles, Vault DB/key, site permissions, settings, bookmarks, history, downloads,
  AI Credential Manager key가 보존된다.
- duplicate/cancel/locked file/corrupt backup/recovery cap failure injection이 통과했다.
- 실제 Windows 11 실기기, 실제 공개 Release, 전원 손실 조건은 UNVERIFIED로 남긴다.
- MSIX/서명/공개 Release는 수행하지 않았다.
