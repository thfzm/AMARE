# AMARE GitHub 공개 전 보안 감사

- 감사일: 2026-11-03
- 범위: 현재 working tree (commit 전)
- 실제 commit/push/remote/staging: **수행하지 않음**
- 공개 정책: **B — GitHub Public + All rights reserved**
- 결론: **첫 commit 파일/보안 준비 완료. Cargo package 5 pass/5 fail(경로 의존성 version 미지정), 법적/패키징/CI 항목은 별도 REVIEW.**

---

## 1. 저장소 전체 파일/용량

| 구분 | 값 |
|---|---:|
| Git branch | `master` |
| commit | **없음** |
| remote | **없음** |
| staged files | **0** |
| Git non-ignored untracked | **170 files** |
| filesystem scan (non-ignored + ignored candidate) | **171 files** |
| ignored candidate | **1** — root `interop-results.json` (487 bytes) |
| 공개 INCLUDE 파일 수 | **170 files** |
| 공개 INCLUDE 총 용량 | 약 **1.6 MB** (정확한 값은 manifest 참조) |
| workspace 전체 용량 | 약 **2.4 GB** (`target/` 포함) |
| workspace 전체 파일 수 | 11,955 (`target/`, `artifacts/` 포함) |

공개 후보는 `.git/`, `target/`, `artifacts/`, `bin/`, `obj/`를 제외한 소스/문서/도구 기준입니다.

**170/171 불일치 원인:** filesystem scan의 171번째 파일은 root `interop-results.json`(487 bytes)이며 `.gitignore`의 `interop-results.json` 규칙으로 ignored입니다. 따라서 Git non-ignored/public INCLUDE는 **170개**입니다.

### 상위 용량 파일

| 크기 | 파일 |
|---:|---|
| 501,249 | `AMARE_logo.png` |
| 48,558 | `crates/amare-store/src/backup.rs` |
| 44,570 | `crates/amare-store/tests/library_ai.rs` |
| 41,885 | `docs/ARCHITECTURE.md` |
| 41,489 | `apps/windows/AMARE.Windows/MainWindow.xaml.cs` |
| 36,136 | `docs/PHASE0_REPORT.md` |
| 35,040 | `docs/ENGINE_COMPARISON.md` |
| 32,613 | `crates/amare-services/tests/services.rs` |

1 MB 초과 파일은 로고 1개뿐입니다. 중복 SHA-256 파일 그룹은 없습니다.

### untracked 구성

| 영역 | 파일 수 |
|---|---:|
| `.github` | 3 |
| `apps` | 44 |
| `crates` | 55 |
| `docs` | 43 |
| `engine` | 4 |
| `tools` | 14 |
| root (`.gitignore`, Copyright, Cargo, README, logo, toolchain) | 7 |
| **합계** | **170** |

---

## 2. 민감 정보 검사 결과

검사 방식:

- GitHub PAT/API key/OAuth token 패턴
- AWS/Google/Slack token 패턴
- private key / certificate header
- JWT / DB connection string / Bearer literal
- `.env`, key/certificate 파일
- 개인 경로 및 이메일 패턴
- 바이너리/메타데이터 검사

### 결과

| 구분 | 결과 |
|---|---|
| 실제 API key/PAT/OAuth token | **발견되지 않음** |
| 실제 private key/certificate/keyfile | **발견되지 않음** |
| `.env`, secrets 파일 | **없음** |
| SQLite DB/WAL/SHM | 공개 후보에 **없음** |
| DB connection string | **발견되지 않음** |
| 실제 개인 이메일 | **발견되지 않음** |
| 실제 내부 서버/사내 주소 | **발견되지 않음** |
| localhost/127.0.0.1 | 테스트 하네스의 로컬 서버에만 존재 |

### 패턴 hit (원문 비노출)

- private-key header 문자열 6건
  - 메시지 저장 거부 로직
  - private-key 거부 테스트 fixture
  - 실제 키 본문은 없음
- `test@example.com` 등 문서/테스트용 placeholder email
- `YOUR_GITHUB_EMAIL` 등 복사용 placeholder

이 항목들은 실제 인증 정보가 아니므로 민감 정보 유출로 판정하지 않았습니다.

### 로고 메타데이터

`AMARE_logo.png`에는 C2PA provenance chunk가 포함되어 있습니다.

- AI 생성 이미지로 표시됨
- 제작 도구 메타데이터(ChatGPT/gpt-image) 포함
- 개인 경로/계정/이메일은 발견되지 않음

사용자는 로고를 유지하고 공개 후보에 포함하기로 결정했습니다. C2PA metadata는 보존합니다.

---

## 3. 제외 대상 파일

| 대상 | 상태 |
|---|---|
| `target/` | `.gitignore` 제외 |
| `apps/windows/**/bin/`, `obj/` | 제외 |
| `tools/windows-verify/**/bin/`, `obj/` | 제외 |
| `artifacts/` | 제외 |
| root `interop-results.json` | 제외 (이전 로컬 산출물) |
| `*.log`, `*.trx`, `*.coverage`, dump/ETL | 제외 |
| `*.db`, `*.db-wal`, `*.db-shm` | 제외 |
| native `*.dll`, `*.so`, `*.dylib` | 제외 |
| `.env`, key/certificate/secrets | 제외 |
| WebView2 `EBWebView/`, `UserData/`, `webview2/` | 제외 |
| release package `*.zip`, `*.msi`, `*.msix`, `*.appx`, `*.nupkg` | 제외 |
| `.vs/`, IDE 임시 파일 | 제외 |

`Cargo.lock`, workflow, 테스트 코드, fixture, 문서, 스크립트는 제외하지 않았습니다.

---

## 4. `.gitignore` 수정

추가한 보호 패턴:

- `.env`, `.env.*`
- `*.pem`, `*.key`, `*.pfx`, `*.p12`, `*.crt`, `*.cer`
- `secrets.json`, `appsettings.*.local.json`
- `EBWebView/`, `UserData/`, `webview2/`
- `*.msi`, `*.msix`, `*.appx`, `*.zip`, `*.7z`, `*.dmp`, `*.etl`

검증:

- `.github/workflows/*` → 포함
- `Cargo.lock` → 포함
- 테스트 코드/문서/스크립트 → 포함
- `target/`, `artifacts/`, `bin/`, `obj/`, root `interop-results.json` → 제외

---

## 5. 공개 정책/라이선스

### 최종 정책

- GitHub Public
- **All rights reserved**
- MIT/Apache-2.0 미적용
- 소스 열람은 허용하지만 일반적인 재사용/수정/재배포 권한은 부여하지 않음
- 제3자 의존성은 각자의 라이선스 적용

관련 파일:

- `COPYRIGHT.md`: All rights reserved 고지
- `Cargo.toml`: `license-file = "COPYRIGHT.md"`
- 각 crate: `license-file.workspace = true`
- `README.md`: 공개 정책/저작권 안내
- `docs/PROJECT_LICENSE_OPTIONS.md`: 정책 비교 및 최종 결정 기록
- `docs/ASSET_ATTRIBUTION.md`: 로고 출처/C2PA/권리 한계

### Cargo 검증

- `license = "TBD"` 제거
- 유효하지 않은 SPDX 식별자/`LicenseRef-Proprietary` 사용하지 않음
- workspace `license-file` 상속 확인
- `cargo metadata --no-deps`: 각 package `license_file=../../COPYRIGHT.md`, `license=None`
- `cargo package --allow-dirty --no-verify` 전체 결과: **5 PASS / 5 FAIL**
  - PASS: `amare-core`, `amare-security`, `amare-ai`, `amare-agent`, `amare-testkit`
  - FAIL: `amare-ffi`, `amare-services`, `amare-store`, `amare-update`, `amare-platform`
  - 실패 원인: workspace path dependency에 Cargo package용 `version` 요구사항이 없음
  - 모든 package의 `--list`에서 `COPYRIGHT.md` 포함 확인
- `Cargo.toml`/`Cargo.lock`은 이번 검증에서 변경하지 않음
- 실제 `cargo publish`는 수행하지 않음

### 라이선스 REVIEW

- 공개 정책 결정: **완료**
- Cargo/copyright notice 반영: **완료**
- 저작권 보유자의 법적 실체/상업 배포 조건: **미확정, 법률 REVIEW**
- NuGet/WebView2 재배포 고지: **R-03 REVIEW**

---

## 6. GitHub Actions 정적 검사

| 항목 | 결과 |
|---|---|
| `.github/workflows/ci-windows.yml` 존재 | PASS |
| `.github/workflows/ci-linux.yml` 존재 | PASS |
| `.github/workflows/ci-android.yml` 존재 | PASS (manual scaffold) |
| actionlint | **PASS** |
| shellcheck (`tools/verify-linux.sh`) | **PASS** |
| PowerShell parser (`tools/verify-windows.ps1`) | **PASS** |
| 실제 GitHub Actions 실행 | **하지 않음 / UNVERIFIED** |
| Windows Runner 결과 | **없음 / UNVERIFIED** |

workflow는 `permissions: contents: read`, concurrency, 고정 Action 버전, Artifact 업로드를 유지합니다.

---

## 7. 첫 commit 포함 예정 파일

현재 `git status` 기준 **170 untracked files**가 후보입니다. 171은 ignored 파일을 포함한 filesystem scan 수치입니다.

| 분류 | 수 | 내용 |
|---|---:|---|
| INCLUDE | **170** | source, tests, docs, workflows, tools, Cargo.lock, Cargo.toml, COPYRIGHT, README, `.gitignore`, `AMARE_logo.png` |
| EXCLUDE | - | ignored build/user artifacts |
| REVIEW (file-level) | **0** | — |

Manifest:

- ignored artifact: `artifacts/github-commit-manifest.json`
- INCLUDE entries: **170**
- INCLUDE exact bytes: manifest 참조
- EXCLUDE (ignored) files: **11,801** (target/artifacts/bin/obj/generated result; audit manifest/path helper 포함)
- REVIEW entries: **4** (R-02/R-03/R-04/R-05)
- path helper: `artifacts/github-include-paths.txt` (170 paths)

External REVIEW (commit 파일 자체를 막지는 않음):

1. R-02 로고 법적 권리/상업 배포 조건
2. R-03 WindowsAppSDK/WebView2 Runtime 패키징/고지
3. R-04 remote 연결 후 실제 Actions 실행
4. R-05 crate 배포를 위한 path dependency version 정책

**파일/보안 기준 첫 commit 준비는 완료되었습니다.**

---

## 8. REVIEW 항목

| ID | 항목 | 현재 상태 | 다음 작업 |
|---|---|---|---|
| R-01 | 공개 정책/라이선스 | **RESOLVED** — B 정책, COPYRIGHT/Cargo 반영, metadata 검증 | 저작권 보유자 법적 실체는 필요 시 확인 |
| R-02 | 로고 | **결정 완료** — 파일 유지/C2PA 보존, 법적 권리 확인 미완료 | 배포 전 사용/배포 권리 확인 |
| R-03 | Windows 재배포 | **조사 완료** — package license/NOTICE 확인 | 실제 패키징/설치 프로그램/법무 검토 |
| R-04 | CI | **UNVERIFIED** — remote/실행 없음 | remote 연결 후 GitHub Actions 실행 |
| R-05 | Cargo crate packaging | **REVIEW** — 5 pass/5 fail, path dependency version 미지정 | 배포 시 workspace dependency version 정책 결정 |

---

## 9. GitHub 공개 가능 여부

**민감 정보 관점: 공개 가능**
실제 secret/key/token/user DB는 발견되지 않았습니다.

**첫 commit 파일/보안 관점: 가능**
INCLUDE 170 files (1,659,976 bytes), EXCLUDE file-level 0, REVIEW file-level 0. ignored candidate 1 (`interop-results.json`).

다만 다음은 공개 후/배포 전 별도 REVIEW입니다.

1. 로고의 법적 사용/배포 권리
2. WindowsAppSDK/WebView2 Runtime 고지 및 패키징
3. GitHub Actions 실제 실행 결과
4. crate 배포 시 path dependency version 정책

---

## 10. 사용자가 직접 수행할 다음 단계

```bash
cd /home/ubuntu/AMARE

# 1) 최종 파일 확인
git status --short
git check-ignore -v target artifacts apps/windows/AMARE.Windows/bin interop-results.json

# 2) 명시적으로 파일 추가 (git add . 금지)
# exact 170-path list (generated/ignored; not added to Git)
git add --pathspec-from-file=artifacts/github-include-paths.txt

# 3) staging 내용을 다시 검토
git status --short
git diff --cached --stat

# 4) 사용자가 승인한 경우에만 commit
git commit -m "Initial AMARE source import"

# 5) remote 등록/push는 별도 승인 후
```

commit/push/remote 등록은 이 감사 단계에서 수행하지 않았습니다.
