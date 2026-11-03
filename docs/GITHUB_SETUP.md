# GitHub 저장소 연결 및 Actions 시작

이 문서는 현재 로컬 AMARE 프로젝트를 사용자의 GitHub 저장소에 연결하는 절차입니다. 실제 저장소 URL/계정은 사용자가 정해야 하며, AMARE가 임의의 URL을 만들지 않습니다.

---

## 0. 공개 전 감사

첫 commit 전에 [GitHub 공개 감사](GITHUB_PUBLICATION_AUDIT.md)에서 file-level REVIEW가 없는지 확인하세요. 공개 정책은 B(Public + All rights reserved)로 결정되었고, R-02 법적 권리/R-03 패키징/R-04 CI는 별도 REVIEW로 추적합니다.

## 1. 현재 상태 확인

```bash
cd AMARE
git status
git branch --show-current
git log --oneline -5
git remote -v
```

현재 이 작업 환경에서는:

- branch: `master`
- commit: **아직 없음**
- remote: **없음**
- working tree: 모든 프로젝트 파일이 untracked

## 2. GitHub 저장소 생성

GitHub 웹에서 새 repository를 만듭니다.

- Owner/Repository 이름은 사용자가 결정
- 빈 repository 권장 (README/gitignore 자동 생성하지 않음)
- Public/Private 선택

## 3. 로컬 Git identity 확인

```bash
git config user.name
git config user.email
```

없으면 본인 정보로 설정합니다.

```bash
git config user.name "YOUR NAME"
git config user.email "YOUR_GITHUB_EMAIL"
```

## 4. 첫 commit

```bash
cd AMARE
git add .
git status
git commit -m "Prepare AMARE Phase 2.2 Windows RC"
```

`.gitignore`가 `target/`, `bin/`, `obj/`, `artifacts/`, `*.db`, native DLL 등을 제외합니다. commit 전 `git status`에서 사용자 데이터/빌드 산출물/secret이 없는지 확인하세요.

## 5. remote 등록

아래 `YOUR_ACCOUNT`/`YOUR_REPOSITORY`를 실제 값으로 바꿉니다. URL을 추측해서 입력하지 마세요.

```bash
git remote add origin https://github.com/YOUR_ACCOUNT/YOUR_REPOSITORY.git
git remote -v
```

SSH를 사용하는 경우 GitHub 공식 절차로 SSH key를 등록한 뒤:

```bash
git remote add origin git@github.com:YOUR_ACCOUNT/YOUR_REPOSITORY.git
```

## 6. 첫 push

```bash
git push -u origin master
```

기본 branch를 `main`으로 쓰는 경우:

```bash
git branch -M main
git push -u origin main
```

GitHub 인증은 GitHub 공식 방식(PAT/credential manager/SSH)을 사용합니다. **토큰을 소스 코드, `.git/config`, workflow, 문서에 평문 저장하지 마세요.**

## 7. Actions 활성화/확인

push 후:

1. GitHub repository → **Actions**
2. `ci-linux`, `ci-windows`, `ci-android` 확인
3. `ci-android`는 manual-only scaffold
4. `ci-windows` 실행 완료 대기
5. Artifacts에서 `windows-verification-<run_id>` 다운로드
6. `windows-verification.json`에서 PASS/FAIL/UNVERIFIED/SKIPPED 확인

## 8. 실패 로그 확인

Windows Artifact 우선 확인 순서:

1. `windows-verification.json`
2. `windows-verify-summary.json`
3. `windows-verify-environment.json`
4. 실패 step의 `*.log`
5. `windows-verify-results.json`
6. `interop-results.json`

`windows_11=false`인 Runner 결과는 Windows 11 실기기 검증으로 간주하지 않습니다.

## 9. 이후 업데이트

```bash
git fetch origin
git rebase origin/master   # 또는 main
git add <files>
git commit -m "Describe change"
git push
```

강제 push는 공유 branch에서 피하고, Actions 결과를 확인한 뒤 다음 작업을 진행하세요.
