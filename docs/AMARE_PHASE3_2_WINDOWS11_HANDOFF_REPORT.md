# AMARE Phase 3.2 — Windows 11 Real-device Validation Handoff

- 상태: **HANDOFF READY / REAL WINDOWS 11 UNVERIFIED**
- 작업 branch: `feature/phase-3-2-windows11-real-validation`
- Phase 3.1 app/package HEAD: `7c7c6c3`
- Phase 3.1 final HEAD: `f59758e`
- Phase 3.2 최종 HEAD: 이 문서/스크립트 commit
- Self-contained ZIP:
  `AMARE-0.92.0-win-x64-self-contained-unsigned.zip`
- SHA-256:
  `24d123bbd2fbb466fd40a69c33f10f23be1b0dd2679939c9a44b6ebaa3c38f0f`

---

## 1. 현재 결론

- self-contained ZIP과 sidecar, bundle manifest, validation script의 SHA-256
  일치를 확인했다.
- 단일 handoff 폴더를 별도 staging 경로에 구성했다. 기존 ZIP/bundle은 덮어쓰지
  않았다.
- Windows 11 실기기에서 실행할 자동 검증과 P0/P1/P2 수동 체크리스트를
  준비했다.
- 실제 Windows 11 PC에서 실행하지 않았으므로 **실기기 검증 완료가 아니다.**
  이 단계의 결과는 **실행 준비 완료**다.

---

## 2. 단일 인계 폴더

```text
artifacts/phase3.2-windows11-handoff/
  AMARE-0.92.0-win-x64-self-contained-unsigned.zip
  AMARE-0.92.0-win-x64-self-contained-unsigned.zip.sha256
  Invoke-AmAreWindows11Handoff.ps1
  Invoke-AmAreWindows11Validation.ps1
  bundle-manifest.json
  README.md
  RUN.txt
  manual-checklist.md
  EXPECTED-RESULTS.md
  RECOVERY.md
```

기존 `artifacts/phase3-validation-bundle/`, Phase 3.1 ZIP, framework-dependent ZIP,
preliminary ZIP은 보존했다.

### 실행 한 줄

```powershell
powershell -ExecutionPolicy Bypass -File .\Invoke-AmAreWindows11Handoff.ps1
```

- wrapper가 ZIP SHA-256, sidecar, bundle manifest, validation script SHA-256을
  먼저 검증한다.
- 관리자 권한 요청 없음.
- WebView2 Evergreen Runtime 필요.
- self-contained ZIP이므로 .NET 8/VC++ runtime 별도 설치 불필요.
- evidence는 `artifacts/phase3.2-windows11-handoff/evidence/run-*`에 생성된다.

---

## 3. 패키지 최종 점검

| 항목 | 결과 |
|---|---|
| ZIP SHA-256 vs sidecar | **PASS** |
| bundle manifest vs ZIP/script SHA-256 | **PASS** |
| manifest schema/product/version/arch/entry | **PASS** |
| required files (`AMARE.Windows.exe`, updater, backup, VERSION) | **PASS** |
| .NET 8 self-contained runtime (`hostfxr`/`hostpolicy`/`coreclr`) | **PASS** |
| VC++ runtime (`vcruntime140`, `msvcp140`) | **PASS** |
| WinUI XBF/PRI resources | **PASS** |
| `amare_ffi.dll` / Rust FFI | **PASS** |
| validation marker `portable-unsigned-validation` | **PASS** |
| user profile/Vault key/API key/backup 미포함 | **PASS** |
| PDB 미포함 | **PASS** |
| actual Windows 11 launch | **UNVERIFIED** |
| WebView2 Evergreen actual installation | **UNVERIFIED** |
| .NET/VC++ 미설치 clean PC 실행 | **UNVERIFIED** |

---

## 4. Validation script 변경

- self-contained / framework-dependent ZIP 자동 판별
- self-contained이면 .NET 8 system runtime 불필요로 처리
- framework-dependent이면 .NET 8 없을 때 BLOCKED
- WebView2 Evergreen 없으면 BLOCKED
- self-contained bundle의 `hostfxr.dll`/`hostpolicy.dll`/`coreclr.dll` 검사
- `vcruntime140.dll` 검사
- WinUI `App.xbf`/`MainWindow.xbf`/`AMARE.Windows.pri` 검사
- `bundle-manifest.json`의 ZIP/script SHA-256 검사
- `AMARE_DATA_ROOT` 격리 test root 강제
- test root가 실제 `%LOCALAPPDATA%\AMARE` 내부이면 거부
- 실제 user data root의 last-write timestamp 변화 여부 기록
- 실행마다 `evidence/run-<timestamp>-<id>` 새 디렉터리 생성
- 기존 evidence 덮어쓰기 금지
- 최초 실패 보존
- self-contained runtime independence는 system .NET 8이 없고 앱이 실행된
  경우에만 PASS, system .NET 8이 있으면 UNVERIFIED

---

## 5. 수동 P0 순서

`manual-checklist.md`:

1. 앱 실행 및 종료
2. 주소창 탐색과 한글 IME
3. 탭 생성·전환·닫기·새 창
4. 100/125/150/200% DPI
5. Vault 저장 및 명시적 Fill
6. Vault 5분 idle lock
7. Windows session lock
8. 테스트 프로필 full/partial restore
9. 복원 도중 강제 종료와 recovery
10. updater 적용·재시작·롤백

각 항목에 조작 방법, 성공 기준, 실패 시 복구, 필요한 증거가 포함되어 있다.
P1/P2 항목도 같은 문서에 정리했다.

---

## 6. CI

PENDING_CI

---

## 7. 최초 실패 및 수정

이번 단계에서 실제 Windows 11 실행은 수행하지 않았으므로 실기기 최초 실패는
아직 없다. 대신 인계 전 점검에서 다음을 수정했다.

| # | 항목 | 수정 |
|---:|---|---|
| 1 | handoff evidence가 repo artifacts로 흩어질 수 있음 | bundle manifest가 있으면 `evidence/` 사용 |
| 2 | self-contained runtime independence가 system .NET 8 유무를 오표기할 수 있음 | app launch 성공 + system .NET 8 부재일 때만 PASS |
| 3 | 실제 user data root 사용 여부 검증 부족 | test root가 real root 내부면 거부, real root timestamp 비교 |
| 4 | bundle script/ZIP 동일 빌드 검증 부족 | wrapper + main script가 bundle manifest SHA-256 검증 |
| 5 | run directory timestamp 충돌 가능 | run id에 GUID suffix |
| 6 | WebView2 누락 시 FAIL로 표시 | BLOCKED로 중단 |

---

## 8. UNVERIFIED / BLOCKED

**UNVERIFIED**

- 실제 Windows 11 x64에서 앱 launch/clean launch
- .NET 8/VC++ 미설치 환경의 self-contained 실행
- WebView2 Evergreen 실제 설치/버전
- 한글 IME, DPI 100/125/150/200%, 멀티모니터/Snap/Alt+Tab
- 실제 Vault 저장/Fill/5분 idle/session lock
- backup full/partial restore, power-loss, recovery
- updater 실제 download/apply/restart/rollback
- 실제 AI provider, media/DRM, certificate trust prompt
- validation script 실제 실행 결과

**BLOCKED**

- 코드 서명/publisher authentication: 인증서 없음/정책상 미진행
- MSIX 서명/공개 Release: 금지
- 실제 Windows 11 PC: 현재 환경에 없음

---

## 9. 사용자가 실제로 해야 하는 작업

1. `artifacts/phase3.2-windows11-handoff/` 폴더 전체를 Windows 11 PC로 복사
2. PowerShell에서 한 줄 실행:

```powershell
powershell -ExecutionPolicy Bypass -File .\Invoke-AmAreWindows11Handoff.ps1
```

3. WebView2가 없다는 `BLOCKED`가 나오면 WebView2 Evergreen Runtime 설치 후 재실행
4. `manual-checklist.md` P0 항목을 순서대로 수행
5. `evidence/run-*` 폴더 전체와 완료한 checklist를 전달

실제 개인 프로필/Vault/기존 backup은 사용하지 않는다. 관리자 권한은 요구하지
않는다.
