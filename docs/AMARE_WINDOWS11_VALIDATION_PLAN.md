# AMARE Windows 11 Validation Plan — Phase 3.0

- 목적: Windows CI(Server runner)로 확인할 수 없는 실제 Windows 11 항목을
  개인용 ZIP 패키지로 검증한다.
- 검증 branch: `feature/phase-3-full-audit-windows-validation`
- Phase 2.7 제품/테스트 HEAD: `3fff704`
- Phase 3 감사/패키징 HEAD: `9cb93f7`
- Phase 2.7 문서 HEAD: `9780e8b`
- 실기기 결과: **UNVERIFIED (아직 수행하지 않음)**
- 원칙: 실기기에서 확인하지 않은 항목을 PASS로 표시하지 않는다.

---

## 1. 검증 패키지

Validation-only ZIP:

```text
artifacts/phase3-personal-package/AMARE-0.92.0-win-x64-unsigned.zip
```

- SHA-256: `6f2d606bb4c85f89e16383e0c353cfdc91b589ea012db5146cc15a6443a42d76`
- size: 153,863,774 bytes / 360 files
- unsigned
- framework-dependent .NET 8 runtime
- WebView2 Evergreen 필요
- Windows App SDK self-contained 포함
- `AMARE.Updater.exe`, `AMARE.Backup.exe` 포함
- PDB 제외
- user data/Vault key/API key/backup 미포함

SHA-256과 파일 수는 `docs/AMARE_PERSONAL_PACKAGE_READINESS.md`와
`package-report.json`에 기록한다.

---

## 2. 실행 진입점

저장소에서:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File `
  .\tools\windows11-validation\Invoke-AmAreWindows11Validation.ps1 `
  -PackageZip .\artifacts\phase3-personal-package\AMARE-0.92.0-win-x64-unsigned.zip
```

ZIP과 스크립트를 같은 폴더에 둔 경우:

```powershell
powershell -ExecutionPolicy Bypass -File `
  .\Invoke-AmAreWindows11Validation.ps1 `
  -PackageZip .\AMARE-0.92.0-win-x64-unsigned.zip
```

옵션:

- `-AllowNonWindows11`: Windows Server lab에서 구조 검증만 수행
- `-RunUiSmoke`: 저장소와 .NET SDK가 있을 때 WinUI UI smoke 추가 실행
- `-SkipBackup`, `-SkipUpdater`: 해당 파괴적 테스트를 건너뜀
- `-TestRoot`: 격리 test root 지정

관리자 권한은 요청하지 않는다. 필요한 경우에도 자동 상승하지 않고
`BLOCKED`로 기록한다.

---

## 3. 데이터 격리

- 앱은 `AMARE_DATA_ROOT=<unique temp root>`로 실행된다.
- 실제 `%LOCALAPPDATA%\AMARE` 프로필, Vault, backup, 설정을 사용하지 않는다.
- backup/restore/updater 테스트는 모두 `<TestRoot>` 아래에서만 수행한다.
- 실제 사용자 backup 파일을 선택/삭제/덮어쓰지 않는다.
- 테스트 후 `<TestRoot>`는 증거 수집 후 수동 삭제할 수 있다.
- 파괴적 테스트는 `-SkipBackup`/`-SkipUpdater`로 제외할 수 있다.

---

## 4. 자동 검증 단계

| 단계 | 내용 | 성공 기준 | 실패 시 |
|---|---|---|---|
| P1 환경 | Windows 11 build 22000+, x64, PowerShell, WebView2, .NET 8, disk, display | 필수 조건 충족 | BLOCKED/FAIL 기록, destructive test 중단 |
| P2 패키지 | ZIP SHA-256, sidecar, required/forbidden entries, manifest, runtime config | 모두 일치/존재 | FAIL, 앱 실행 중단 |
| P3 격리 | `AMARE_DATA_ROOT` marker/channel 확인 | `portable-unsigned-validation` | BLOCKED, 앱/backup/updater 중단 |
| P4 앱 실행 | test root로 앱 시작, 25초 생존, screenshot, 종료 | process alive/window | FAIL, 로그/스크린샷 보존 |
| P5 데이터 루트 | test root에 `amare.db` 생성 | 존재 | FAIL |
| P6 Backup | full backup create/verify | status Valid/ValidWithWarnings | FAIL |
| P7 Full restore | empty test root로 full restore `--no-relaunch` | root DB+profiles 생성 | FAIL |
| P8 Partial restore | source profile→`default` profile restore | target DB 존재 | FAIL |
| P9 Password | encrypted backup wrong password | `InvalidPassword` | FAIL |
| P10 Updater success | test app에 update apply, data hash 비교 | version 갱신/data 불변/state succeeded | FAIL |
| P11 Wrong hash | 잘못된 SHA-256 apply | 거부/변경 없음 | FAIL |
| P12 Locked file | app exe lock 상태 apply | 거부/변경 없음 | FAIL |
| P13 Launch rollback | health 실패 package apply | old version 복원/state rolled_back | FAIL |
| P14 Concurrency | update.lock 보유 상태 apply | 두 번째 helper 거부 | FAIL |
| P15 Evidence | environment/package/results/logs/screenshots | 모두 생성 | FAIL |

`-RunUiSmoke`를 지정하면 P4 이후 WinUI UI smoke 86개를 추가 실행한다.
UI smoke는 저장소와 .NET SDK가 필요하며, 없으면 FAIL이 아니라 SKIPPED/UNVERIFIED로
기록해야 한다.

---

## 5. 수동 검증

`tools/windows11-validation/manual-checklist.md`를 사용한다.

필수 수동 항목:

- 한국어 IME 조합, DPI 100/125/150/200%, 멀티모니터, Snap/Alt+Tab
- 실제 로그인 페이지 Vault 저장/Fill, 5분 idle, Windows session lock
- 실제 WebView2 다운로드 저장 대화상자/취소
- 실제 full/partial restore through Settings UI + restart
- 다른 Windows 계정/PC에서 Vault restore
- restore/apply 중 force-kill/power-loss
- 실제 AI provider/OAuth, media/DRM, certificate trust prompt
- long-running soak

수동 결과는 `PASS/FAIL/UNVERIFIED/BLOCKED`로 기록하고, 스크린샷/로그/환경 정보를
`run-*/manual-checklist.md`와 함께 보관한다.

---

## 6. 성공/실패/복구 기준

### 성공

- P1~P15 PASS
- 수동 필수 항목 PASS
- 새 P0 결함 없음
- 결과/증거/SHA-256 보존

### 실패

- FAIL 항목을 수정하거나 차단 사유를 명시
- 동일 run을 재실행해 덮어쓰지 않고 새 run directory 생성
- 최초 실패의 `results.json`, logs, screenshot 보존
- P0이면 출시 판정 중단

### 복구

- 앱/도우미 crash: test root만 삭제, 실제 user data는 건드리지 않음
- backup/restore 실패: helper `recover`가 journal/recovery를 사용; 실패 시
  recovery directory를 보존
- updater rollback 실패: backup/recovery를 보존하고 수동 복구 절차 기록
- 패키지 손상: ZIP과 sidecar를 새로 받아 SHA-256 확인

---

## 7. 예상 산출물

```text
artifacts/windows11-validation/run-<timestamp>/
  environment.json
  package.json
  results.json
  manual-checklist.md
  screenshots/app-launch.png
  logs/app-logs/
  logs/native-logs/
  hosts/README.txt
  README.md
```

`results.json` summary:

```json
{
  "passed": 0,
  "failed": 0,
  "unverified": 0,
  "blocked": 0,
  "skipped": 0
}
```

---

## 8. 알려진 제한

- PowerShell 스크립트는 Ubuntu ARM64 환경에서 실행/검증할 수 없다. Windows 11
  실기기에서 첫 실행 결과를 별도로 기록해야 한다.
- UI smoke를 포함하려면 저장소와 .NET SDK가 필요하다. 패키지만 있는 사용자 PC는
  앱 launch/backup/updater 자동 검증만 수행한다.
- framework-dependent .NET 8과 WebView2 Evergreen이 없으면 앱 launch는 FAIL한다.
- 실제 Windows 11에서 스크립트를 실행하지 않았으므로 현재 상태는 **UNVERIFIED**다.
- 코드 서명/publisher authentication은 구현되어 있지 않다. SHA-256은 무결성만
  의미한다.
