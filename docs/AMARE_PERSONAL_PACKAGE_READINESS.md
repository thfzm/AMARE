# AMARE Personal Package Readiness — Phase 3.1

- Phase 3.0 app/package code: `9cb93f7`
- Phase 3.1 CI: Windows run `46012944076` at `7c7c6c3`
- 패키지 형식: unsigned portable ZIP, WinUI 3 + WebView2 Evergreen
- 상태: **SELF-CONTAINED VALIDATION-READY / FINAL RELEASE NOT READY**
- 실제 Windows 11: **UNVERIFIED**
- MSIX/코드 서명/공개 Release: **없음**

---

## 1. 권장 패키지 — self-contained

```text
AMARE-0.92.0-win-x64-self-contained-unsigned.zip
size:     155,316,668 bytes
files:    527
SHA-256:  24d123bbd2fbb466fd40a69c33f10f23be1b0dd2679939c9a44b6ebaa3c38f0f
signed:   NO (unsigned)
type:     self-contained .NET 8 + Windows App SDK self-contained
```

경로:

```text
artifacts/phase3-personal-package/AMARE-0.92.0-win-x64-self-contained-unsigned.zip
artifacts/phase3-personal-package/AMARE-0.92.0-win-x64-self-contained-unsigned.zip.sha256
artifacts/phase3-personal-package/package-report-self-contained.json
artifacts/phase3-personal-package/file-list-self-contained.txt
artifacts/phase3-validation-bundle/
```

포함:

- .NET 8 runtime: `hostfxr.dll`, `hostpolicy.dll`, `coreclr.dll`, `clrjit.dll`,
  `runtimeconfig.json`의 `includedFrameworks` (`Microsoft.NETCore.App 8.0.34`)
- Windows App SDK self-contained files
- VC++ runtime: `vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll`
- WinUI resources: `AMARE.Windows.pri`, `App.xbf`, `MainWindow.xbf`
- `AMARE.Windows.exe`, `AMARE.Windows.dll`, assets
- `AMARE.Updater.exe`, `AMARE.Backup.exe`
- `amare_ffi.dll`, Rust/C# interop binaries
- WebView2 loader/projection DLLs (Evergreen runtime 자체는 미포함)
- `VERSION`, `amare-update.json`

제외:

- `*.pdb`
- user profile, WebView2 cache/cookies
- Vault key/DPAPI key
- API key/Credential Manager
- `ai-api-settings.json`, downloads/app/update preferences
- 사용자 `.amarebak`, logs, updater staging
- GPUI

### Runtime prerequisite

| 항목 | self-contained |
|---|---|
| .NET 8 Runtime | **불필요** |
| Windows App SDK | 포함 |
| VC++ runtime | 포함 |
| **WebView2 Evergreen Runtime** | **필요** |
| Windows 11 x64 | 필요 |
| 관리자 권한 | 불필요 |
| 코드 서명 | 없음(UNSIGNED) |

Windows CI에서 self-contained XML/리소스/VC++ 포함, clean launch 8초 생존,
updater apply/data preservation을 PASS했다. 그러나 Windows CI는 Windows Server
2025이며 실제 Windows 11 소비자 PC의 .NET/VC++ 부재 상태는 **UNVERIFIED**다.

---

## 2. 기존 framework-dependent 패키지

기존 ZIP은 보존했고 덮어쓰지 않았다.

```text
AMARE-0.92.0-win-x64-unsigned.zip
size:     153,863,831 bytes
files:    360
SHA-256:  795d92eb42f94a71690ae893d7f668c6be52542de24af1794ec1efca59d147e8
runtime:  framework-dependent .NET 8
```

- .NET 8 Runtime 필요
- host VC++ runtime 필요
- WebView2 Evergreen 필요
- validation-only

---

## 3. 패키지 검증 결과

| 항목 | self-contained |
|---|---|
| ZIP SHA-256 sidecar | PASS |
| required files | PASS |
| manifest schema/product/version/arch/entry | PASS |
| .NET runtime 포함 | PASS (`hostfxr`/`hostpolicy`/`coreclr`) |
| Windows App SDK files | PASS |
| VC++ runtime 포함 | PASS |
| WinUI XBF/PRI resources | PASS |
| `amare_ffi.dll` | PASS |
| updater/backup helper | PASS |
| 사용자 데이터/키/backup 제외 | PASS |
| PDB 제외 | PASS |
| updater apply/data preservation | PASS (Windows CI) |
| clean launch | PASS (Windows Server CI) |
| 실제 Windows 11 launch | **UNVERIFIED** |
| WebView2 Evergreen 실제 설치 | **UNVERIFIED** |
| SmartScreen/AV | **UNVERIFIED** |
| 서명/publisher authentication | **BLOCKED (인증서 없음/정책)** |

---

## 4. 준비 판정

- **구조/무결성**: self-contained ZIP 준비 완료
- **Windows CI clean launch**: PASS
- **실제 Windows 11**: UNVERIFIED
- **최종 사용자 배포**: 아직 불가
- 이유:
  1. 실제 Windows 11에서 self-contained launch 미검증
  2. WebView2 Evergreen 실제 설치 상태 미검증
  3. Vault lifecycle, backup/restore, updater real-device gate 미통과
  4. unsigned/publisher authentication 없음
  5. IME/DPI/멀티모니터/Snap 미검증

이 ZIP은 **validation-only**다. 최종 사용 가능 표시는
`docs/AMARE_WINDOWS11_VALIDATION_PLAN.md`의 실기기 gate 통과 후로 미룬다.
