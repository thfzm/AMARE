# AMARE Phase 3.1 — Self-contained Package & Windows 11 Validation Handoff

- 상태: **SELF-CONTAINED VALIDATION PACKAGE READY / REAL WINDOWS 11 UNVERIFIED**
- 작업 branch: `feature/phase-3-1-self-contained-validation-package`
- Phase 3.0 기준: app/package code `9cb93f7`, CI `b869f8f`, final docs `93a5548`
- Phase 3.1 CI 검증 HEAD: `7c7c6c3`
- 최종 branch HEAD: `a24fb6b` (docs/validation script evidence)
- DB schema/Vault crypto/backup format 변경: **없음**
- MSIX/코드 서명/공개 Release: **없음**

---

## 1. Self-contained 전환 결과

WinUI 3 + Windows App SDK + .NET 8 self-contained publish를 실제 Windows CI에서
수행하고 ZIP을 생성했다.

```text
dotnet publish apps/windows/AMARE.Windows/AMARE.Windows.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:Platform=x64 -p:WindowsPackageType=None \
  -p:WindowsAppSDKSelfContained=true \
  -p:PublishSingleFile=false -p:PublishTrimmed=false \
  -p:DebugType=None -p:DebugSymbols=false
```

Windows CI 결과:

- `self_contained_publish` **PASS**
- `self_contained_package` **PASS**
- `self_contained_clean_launch` **PASS** (8초 생존, `AMARE_DATA_ROOT` 격리)
- `self_contained_package`에서 updater apply/data preservation도 PASS

> Windows CI runner는 Windows Server 2025이며 실제 Windows 11 소비자 PC가 아니다.
> 실제 Windows 11에서 .NET 8 없이 실행되는지는 **UNVERIFIED**다.

---

## 2. 새 ZIP

```text
파일: AMARE-0.92.0-win-x64-self-contained-unsigned.zip
크기: 155,316,668 bytes
파일 수: 527
SHA-256: 24d123bbd2fbb466fd40a69c33f10f23be1b0dd2679939c9a44b6ebaa3c38f0f
서명: UNSIGNED
```

경로:

```text
artifacts/phase3-personal-package/AMARE-0.92.0-win-x64-self-contained-unsigned.zip
artifacts/phase3-validation-bundle/
```

기존 framework-dependent ZIP은 덮어쓰지 않았다.

```text
AMARE-0.92.0-win-x64-unsigned.zip
size 153,863,831
sha256 795d92eb42f94a71690ae893d7f668c6be52542de24af1794ec1efca59d147e8
```

---

## 3. 포함/제외

포함:

- `AMARE.Windows.exe`, `AMARE.Windows.dll`, WinUI/XBF/assets
- `AMARE.Windows.pri`, `App.xbf`, `MainWindow.xbf`
- .NET 8 self-contained runtime
  - `hostfxr.dll`, `hostpolicy.dll`, `coreclr.dll`, `clrjit.dll`, runtime DLLs
  - `runtimeconfig.json`의 `includedFrameworks` = `Microsoft.NETCore.App 8.0.34`
- Windows App SDK self-contained files
- VC++ runtime: `vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll`
- WebView2 loader/projection DLLs (`WebView2 Evergreen Runtime` 자체는 미포함)
- `amare_ffi.dll`, Rust/C# interop binaries
- `AMARE.Updater.exe`, `AMARE.Backup.exe`
- `VERSION`, `amare-update.json`

제외:

- `*.pdb`
- user profile, WebView2 cache/cookies
- Vault key/DPAPI key
- API key/Credential Manager
- `ai-api-settings.json`, downloads/app/update preferences
- 사용자 `.amarebak`, logs, updater staging
- GPUI

---

## 4. Runtime prerequisites

| 항목 | self-contained ZIP | framework-dependent ZIP |
|---|---|---|
| .NET 8 Runtime | **불필요** | 필요 |
| Windows App SDK | 포함 | 포함 |
| VC++ runtime | **포함** | host runtime 필요 |
| WebView2 Evergreen | **필요** | 필요 |
| Windows 11 x64 | 필요 | 필요 |
| 관리자 권한 | 불필요 | 불필요 |
| 서명 | 없음(UNSIGNED) | 없음(UNSIGNED) |

- `amare_ffi.dll`과 `AMARE.Backup.exe`가 `VCRUNTIME140.dll`에 의존하는 것을
  확인해 self-contained ZIP에 VC++ runtime을 포함했다.
- WebView2 Evergreen Runtime은 앱에 포함하지 않는다. Windows 11 일반 환경에는
  대개 설치되어 있지만, 검증 스크립트는 누락 시 **BLOCKED**로 판정한다.
- Windows CI self-contained clean launch는 PASS했지만 실제 Windows 11 소비자 PC의
  .NET/VC++ 부재 상태는 **UNVERIFIED**다.

---

## 5. Validation script 변경

`tools/windows11-validation/Invoke-AmAreWindows11Validation.ps1`:

- self-contained / framework-dependent ZIP 자동 판별
- self-contained이면 `hostfxr.dll`, `hostpolicy.dll`, `coreclr.dll` 검사
- framework-dependent이면 .NET 8 존재 검사, 없으면 **BLOCKED**
- WebView2 Evergreen 누락 시 **BLOCKED**
- `vcruntime140.dll` 및 WinUI `App.xbf`/`MainWindow.xbf`/`AMARE.Windows.pri` 검사
- 패키지 channel `portable-unsigned-validation` marker가 없으면 destructive test 중단
- `AMARE_DATA_ROOT` 격리 test root 사용
- 실행마다 새 evidence directory 생성, 기존 run 덮어쓰기 없음
- 결과 JSON/환경/로그/screenshot/manual checklist 저장
- self-contained ZIP default 우선 탐색
- 최초 실패 보존

---

## 6. 실기기 인계

```text
artifacts/phase3-validation-bundle/
  Invoke-AmAreWindows11Validation.ps1
  manual-checklist.md
  README.md
  RUN.txt
  AMARE-0.92.0-win-x64-self-contained-unsigned.zip
  AMARE-0.92.0-win-x64-self-contained-unsigned.zip.sha256
```

실행:

```powershell
powershell -ExecutionPolicy Bypass -File `
  .\Invoke-AmAreWindows11Validation.ps1 `
  -PackageZip .\AMARE-0.92.0-win-x64-self-contained-unsigned.zip
```

수동 체크리스트는 P0 → P1 → P2 순서로 정리되어 있으며, 각 항목의 성공 기준,
실패 복구, 증거 수집 항목을 포함한다.

---

## 7. CI 결과

Windows run [46012944076](https://github.com/thfzm/AMARE/actions/runs/46012944076)
at `7c7c6c3`:

```text
203 PASS / 0 FAIL / 8 UNVERIFIED / 8 SKIPPED (219 total)
UI 86/86
interop 127/127
self-contained publish/package/clean launch PASS
self-contained updater apply/data preservation PASS
```

Linux run: [46016969061](https://github.com/thfzm/AMARE/actions/runs/46016969061) at `a24fb6b` — **SUCCESS**.

---

## 8. 최초 실패 및 수정

| # | 실패 | 원인 | 수정 |
|---:|---|---|---|
| 1 | self-contained ZIP에 `App.xbf`/`MainWindow.xbf`/`AMARE.Windows.pri` 없음 | `dotnet publish` 출력에 WinUI 컴파일 리소스 누락 | build output에서 XAML/PRI 리소스를 stage에 복사 |
| 2 | self-contained app startup exit `0xC000027B` | WinUI 리소스 누락으로 stowed exception | UI 리소스 포함 후 clean launch PASS |
| 3 | `amare_ffi.dll`/`AMARE.Backup.exe`의 `VCRUNTIME140.dll` 의존 | VC++ runtime 미포함 | VC++ redist/System32 DLL을 self-contained ZIP에 포함 |
| 4 | framework-dependent ZIP이 self-contained로 오표기 | 기존 manifest runtime 문자열 | `win-x64-framework-dependent`로 수정, self-contained 별도 manifest |

---

## 9. UNVERIFIED / BLOCKED

**UNVERIFIED**

- 실제 Windows 11 소비자 PC에서 .NET 8/VC++ 미설치 상태의 self-contained launch
- WebView2 Evergreen 실제 설치/버전
- Windows 11 한글 IME/DPI/멀티모니터/Snap/Alt+Tab
- 실제 Vault 로그인/Fill/5분 idle/session lock
- backup full/partial restore/power-loss
- updater 실제 download/apply/restart/rollback
- 실제 AI provider, media/DRM, certificate trust prompt
- Windows 11 validation script 실제 실행 결과

**BLOCKED**

- 코드 서명/publisher authentication: 실제 인증서 없음/정책상 미진행
- MSIX 서명/공개 Release: 이번 단계 금지

---

## 10. 결론

- .NET 8 self-contained ZIP을 실제 publish/CI/clean launch까지 구현했다.
- WinUI 리소스와 VC++ runtime을 포함해 startup failure를 수정했다.
- WebView2 Evergreen은 여전히 별도 prerequisite이며, 검증 스크립트가 누락 시
  BLOCKED로 판정한다.
- Windows CI clean launch는 PASS했지만, 실제 Windows 11 실기기 검증은 아직
  UNVERIFIED다. 따라서 이 ZIP도 **validation-only**이며 최종 배포 완료가 아니다.
