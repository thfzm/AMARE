# AMARE Personal Package Readiness — Phase 3.0

- 기준 제품/테스트 HEAD: `3fff704`
- 패키지 생성 CI: Windows run `45961967631` at `b869f8f` (app code `9cb93f7`)
- 패키지 형식: unsigned portable ZIP, WinUI 3 + WebView2 Evergreen
- 상태: **VALIDATION-READY / FINAL-RELEASE NOT READY**
- Windows CI package step: run [45961967631](https://github.com/thfzm/AMARE/actions/runs/45961967631) at `b869f8f` (app code `9cb93f7`)
- `AMARE_DATA_ROOT` validation isolation marker: **included** (`portable-unsigned-validation`)
- 실기기 Windows 11: **UNVERIFIED**
- MSIX/코드 서명/공개 Release: **없음**

---

## 1. 패키지

```text
AMARE-0.92.0-win-x64-unsigned.zip
size:     153,863,831 bytes
SHA-256:  795d92eb42f94a71690ae893d7f668c6be52542de24af1794ec1efca59d147e8
files:    360
signed:   NO (unsigned)
```

Staging paths:

```text
artifacts/phase3-personal-package/AMARE-0.92.0-win-x64-unsigned.zip
artifacts/phase3-personal-package/AMARE-0.92.0-win-x64-unsigned.zip.sha256
artifacts/phase3-personal-package/package-report-final.json
artifacts/phase3-personal-package/file-list.txt
artifacts/phase3-validation-bundle/   (script + checklist + ZIP)
```

기존 Phase 3.0 초기 `...-validation.zip` (3fff704 기반)은
`artifacts/phase3-personal-package/preliminary-3fff704/`에 보존했다. 덮어쓰지 않았다.

---

## 2. 포함 파일

- `VERSION` (`0.92.0`)
- `amare-update.json`
  - schema 1, product AMARE, version 0.92.0
  - architecture `win-x64`
  - entryPoint `AMARE.Windows.exe`
  - runtime `win-x64-framework-dependent`
  - channel `portable-unsigned-validation`
  - requiredFiles: VERSION, AMARE.Windows.exe, AMARE.Updater.exe, AMARE.Backup.exe
- `AMARE.Windows.exe`, `AMARE.Windows.dll`, WinUI/XBF/assets, Windows App SDK
  self-contained files
- `AMARE.Updater.exe`
- `AMARE.Backup.exe`
- `amare_ffi.dll` 및 Rust/C# interop binaries
- WebView2 loader/runtime projection DLLs
- `RELEASE_NOTES.md`는 해당 버전 notes가 존재할 때만 포함

## 3. 제외 항목

- 모든 `*.pdb`
- `%LOCALAPPDATA%\AMARE` user data
- `profiles/`, `vault/`, `vault/*.key`, DPAPI key
- `ai-api-settings.json`, API key
- `downloads.json`, `app-preferences.json`, `update-preferences.json`
- 사용자 backup `.amarebak`
- WebView2 cache/cookies
- logs
- updater staging/backup
- 개발용 secret/config

CI package report에서 required/forbidden entries를 검사해 위 항목이 없음을 확인했다.

---

## 4. 런타임 요구 사항

| 요구 | 상태 | 비고 |
|---|---|---|
| Windows 11 x64 | 필요 | CI runner는 Windows Server 2025 |
| WebView2 Evergreen Runtime | 필요 | Windows 11 일반 배포에는 대개 존재 |
| .NET 8 runtime (`Microsoft.NETCore.App 8.x`) | **필요** | 패키지는 framework-dependent |
| Windows App SDK | self-contained 포함 | 별도 설치 불필요 |
| VC++ runtime | Windows App SDK/OS 의존 | 실기기 확인 필요 |
| 관리자 권한 | 불필요 | portable |
| 코드 서명 | **없음** | SmartScreen/서명 경고 가능 |
| Publisher authentication | **없음** | SHA-256은 무결성만 |

**중요:** 이 ZIP은 self-contained .NET 배포가 아니다. .NET 8 runtime이 없는
Windows 11 PC에서는 앱이 시작되지 않을 수 있다. 개인용 배포 전에 다음 중 하나가
필요하다.

1. .NET 8 Desktop/Runtime 사전 설치를 명시하고 검증
2. `dotnet publish --self-contained true` 기반 ZIP으로 전환
3. 부트스트래퍼/설치 관리자 제공

현재는 1번도 실기기에서 검증되지 않았다.

---

## 5. 패키지 검증 결과 (구조)

| 항목 | 결과 |
|---|---|
| ZIP SHA-256 sidecar | PASS |
| required files | PASS |
| manifest schema/product/version/arch/entry | PASS |
| PDB 제외 | PASS |
| user data/Vault key/API key/backup 제외 | PASS |
| framework dependency 명시 | PASS |
| unsigned 표시 | PASS |
| 실제 Windows 11 launch | **UNVERIFIED** |
| 실제 updater download/apply/restart | **UNVERIFIED** |
| SmartScreen/AV | **UNVERIFIED** |
| 서명/publisher authentication | **BLOCKED (정책/인증서 없음)** |

---

## 6. 패키지 준비 판정

- **개인용 ZIP 구조/무결성**: 준비 가능
- **실제 사용자 배포**: 아직 불가
- **최종 release**: 불가
- 이유:
  1. 실제 Windows 11 실기기 launch/update/restore 미검증
  2. framework-dependent .NET 8 prerequisite 미검증
  3. unsigned/publisher authentication 없음
  4. Vault cross-PC/updater real-device gate 미통과
  5. MSIX/signing/public release는 이번 단계에서 금지

따라서 이 ZIP은 **validation-only**로만 사용한다. 최종 사용 가능 표시는
`docs/AMARE_WINDOWS11_VALIDATION_PLAN.md`의 실기기 gate 통과 후로 미룬다.
