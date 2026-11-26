# AMARE Portable Updater Design — Phase 2.5

- 상태: 구현/CI 검증, 실제 Windows 11 실기기 UNVERIFIED
- 적용 대상: Windows x64 Portable ZIP (`AMARE-<version>-win-x64.zip`)
- MSIX: 이 설계의 자동 적용 대상이 아니다.
- SHA-256: **파일 무결성**만 의미한다. publisher signature가 아니다.

---

## 1. 구성 요소

| 구성 요소 | 역할 |
|---|---|
| `GitHubReleaseClient` | GitHub Releases에서 stable/prerelease release 선택 |
| `UpdateDownloader` | HTTPS/크기/disk 검증, `.part` 다운로드, SHA-256 확인 |
| `PortableUpdatePackage` | manifest/ZIP 정책 검증, 안전한 extraction |
| `PortableUpdateEngine` | backup/apply/health/rollback/recovery |
| `PortableUpdateState` | update 상태와 recovery 시도 횟수 |
| `PortableUpdateLauncher` | helper를 stable location에 복사하고 apply/recover 시작 |
| `AMARE.Updater.exe` | 최소 CLI(`apply`, `recover`, `validate`) |
| `%LOCALAPPDATA%\AMARE` | user data + updater state/log/package |

---

## 2. 신뢰 경계

```text
GitHub Releases (HTTPS, GitHub host)
  └─ release metadata: version, asset URL, size, checksum URL
       └─ SHA-256 sidecar
            └─ portable ZIP (manifest + app files)
                 └─ local staging
                      └─ local app directory (backup → replace)
```

- release metadata와 checksum은 GitHub를 신뢰한다.
- ZIP의 SHA-256이 checksum과 일치해야 apply한다.
- manifest는 product/schema/version/architecture/entry point를 확인한다.
- user data directory는 update 대상이 아니다.
- helper는 임의 명령을 받지 않는다. `apply/recover/validate`와 고정 option만 받는다.

---

## 3. Update check

- current version: app assembly version (3-part).
- stable channel: prerelease 제외.
- prerelease channel: stable + prerelease 포함.
- SemVer precedence:
  - numeric prerelease identifier는 numeric 비교
  - numeric < alphanumeric
  - build metadata 무시
- network/server/timeout/cancel 오류를 구분해 UI에 표시한다.
- 한 번에 하나의 check만 실행한다.

---

## 4. Download / staging

- package/checksum URL: HTTPS + `github.com`/`*.githubusercontent.com`만 허용.
- IP literal, userinfo, HTTP/FTP, 임의 host 거부. redirect 최종 URL도 재검증.
- package 최대 1 GiB, checksum 최대 64 KiB.
- release size와 실제 byte 수 일치.
- `.sha256`/`checksums.txt`를 지원하되, `checksums.txt`는 package file name과
  일치하는 line의 hash만 사용한다.
- destination disk 여유 공간 확인.
- `*.part`는 GUID suffix로 collision을 피하고, 성공 시 atomic move.
- 실패/취소/timeout 시 `.part` 삭제.
- staging은 `data/updates/work/staging-<guid>`.
- staging 중 기존 app/data를 수정하지 않는다.

---

## 5. Manifest / ZIP 정책

필수:

- `schema == 1`
- `product == "AMARE"`
- SemVer `version`
- `architecture == win-x64`
- non-empty `runtime`, root `entryPoint == AMARE.Windows.exe`
- `requiredFiles` 존재

ZIP:

- absolute/drive/`..`/`.`/empty middle segment 금지
- reserved device name, trailing dot/space, control character 금지
- symlink/특수 file type 금지
- duplicate entry 금지
- entry 수 ≤ 20,000, uncompressed size ≤ 8 GiB, manifest ≤ 1 MiB
- `.bat/.cmd/.ps1/.vbs/.js/.hta/.msi/.msix/.scr/.com/.cpl/.jar/.reg/.lnk` 등 금지
- nested `.exe` 금지
- `cmd.exe`/`powershell.exe`/`mshta.exe` 등 위험 executable 금지
- 알 수 없는 root `.exe`는 추출만 되고 entry point로 실행되지 않음

검증 실패 시 apply하지 않는다.

---

## 6. Apply

```text
hash(package) == expected
  → manifest/ZIP validation
  → entry point policy
  → disk space
  → helper lock + version newer check
  → wait parent PID exit
  → extract to staging
  → ensure target files replaceable
  → backup app directory + verify entry exists
  → applying
  → replace files (temp + atomic move, entry last)
  → VERSION
  → applied
  → launch new version with health marker
  → verifying
  → marker seen → succeeded (new process stays running)
  → marker timeout/launch fail → rollback
```

- 실행 중 binary를 직접 덮어쓰지 않는다.
- apply 중 예외/취소/프로세스 종료는 state에 기록한다.
- pre-apply 실패는 app을 변경하지 않고 staging/부분 backup을 정리한다.

---

## 7. Rollback / Recovery

- rollback 전 backup entry executable 존재 확인.
- staging에만 있고 backup에 없는 새 파일 제거 후 backup 복원.
- 복원 후 entry executable 존재 확인.
- rollback 실패 시 backup을 삭제하지 않는다.
- recovery 시도 횟수는 state에 기록하고 최대 3회.
- 제한 도달 시 자동 복구를 중단하고 backup을 보존한다.
- state JSON에 `RecoveryAttempts`가 없으면 0으로 load한다.
- success 시 staging/backup/health marker/package를 정리한다.

---

## 8. User data

update 대상이 아닌 것:

```text
profiles/<id>/amare.db
profiles/<id>/site-permissions.json
vault/<profile>.key
settings.json
app-preferences.json
update-preferences.json
ai-api-settings.json
bookmarks.db
history.db
downloads.json
Windows Credential Manager (AMARE.AI.OpenAI)
```

updater metadata만 data root에서 변경된다.

```text
update-state.json
update.lock
updates/
updater/
logs/updater.log
```

DB schema 변경은 이 단계에 없다.

---

## 9. 서명 / 배포 한계

- 현재 Windows portable updater에는 publisher signature 검증이 없다.
- SHA-256은 checksum sidecar와 ZIP의 무결성만 확인한다.
- 가짜 인증서/서명 검증 가장을 하지 않는다.
- MSIX 서명, 공개 Release, 자동 upload는 이 단계에서 수행하지 않는다.
- 실제 공개 asset E2E와 실제 Windows 11 실기기 검증은 UNVERIFIED다.
