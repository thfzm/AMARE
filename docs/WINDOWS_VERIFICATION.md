# Windows 11 Phase 0.5 검증 가이드

- 대상: Windows 11 x64, WebView2, i5-7500/16GB/GTX 1050 권장
- 목적: Phase 0.5의 Windows 전용 항목을 실제 실행하고 결과 JSON을 남긴다.
- 원칙: **실행하지 못한 항목은 PASS로 처리하지 않는다.**

---

## 1. 실행 전제 조건

| 항목 | 요구사항 | 확인 방법 |
|---|---|---|
| OS | Windows 11 build 22000+ | `winver` |
| CPU | x64. 목표: Intel Core i5-7500 | 작업 관리자/`Get-CimInstance` |
| RAM | 16GB 권장 | 작업 관리자 |
| GPU | NVIDIA GTX 1050 2GB 권장 | 장치 관리자 |
| .NET SDK | 8.0 이상 LTS | `dotnet --list-sdks` |
| Rust | stable, cargo/rustc | `cargo --version`, `rustc --version` |
| Visual Studio Build Tools | C++/Windows SDK 포함 | Visual Studio Installer |
| WebView2 Runtime | Evergreen Runtime | 설치 스크립트가 자동 확인 |
| PowerShell | Windows PowerShell 5.1+ | `$PSVersionTable` |
| 네트워크 | DRM 수동 테스트 시 선택 | 오프라인이면 DRM 실제 재생은 미검증 |

WebView2 Runtime이 없으면 설치 스크립트가 중단됩니다.
공식 다운로드: <https://developer.microsoft.com/microsoft-edge/webview2/>

> .NET 10/WinAppSDK 2.6.x 호환성은 Windows CI에서 확인 후 올립니다. 현재 스캐폴드는 .NET 8로 검증했습니다.

---

## 1.1 CI(GitHub Actions) vs 실기기

GitHub Actions `windows-latest`는 **Windows Server**이며 Windows 11 실기기와 동일하지 않습니다.
자동화 가능한 범위는 [GitHub Actions 문서](GITHUB_ACTIONS.md)를 참고하세요.

| 항목 | CI 자동 | 실기기 필요 |
|---|---|---|
| WinUI 3 build | O | - |
| SQLite storage/migrations/CRUD/격리 | O | - |
| Backup/restore/integrity | O | O(실기기 데이터) |
| Windows DPAPI key protection | O (Windows Runner) | O(계정 범위) |
| SQLite/backup benchmark | O(참고용) | O(목표 하드웨어) |
| WebView2 초기화/기본 CDP | O(환경 의존) | O |
| 프로필 격리 | O | O |
| 사용자 개입 감지 | **UNVERIFIED** | O(수동) |
| 30탭 메모리/성능 | **UNVERIFIED** | O(목표 하드웨어) |
| DRM 실제 재생/VMP/HDR | **UNVERIFIED/SKIPPED** | O |
| 설치 프로그램 롤백 | **UNVERIFIED** | O(Phase 2) |

CI에서 실행하지 않은 항목은 `PASS`로 기록하지 않습니다.

---

## 2. 실행

```powershell
cd AMARE
.\tools\verify-windows.ps1
```

옵션:

```powershell
# 수동 개입 테스트를 건너뛰고 UNVERIFIED로 기록
.\tools\verify-windows.ps1 -SkipManual

# 30탭 메모리 테스트를 건너뛰고 UNVERIFIED로 기록
.\tools\verify-windows.ps1 -SkipMemory

# WinUI 빌드만 건너뛰기
.\tools\verify-windows.ps1 -SkipWinUI

# CI/headless: 수동 개입/메모리/DRM 매트릭스를 UNVERIFIED로 두고 자동 실행
.\tools\verify-windows.ps1 -CI
```

기본 동작:

1. 환경/전제조건 확인
2. `windows-verify-environment.json` 생성
3. Rust fmt/clippy/test
4. 업데이트/암호화 로직 테스트
5. FFI/C# interop
6. WinUI 3 빌드(선택 실패 허용)
7. Windows 프로브 빌드
8. WebView2 프로브 실행

결과는 `artifacts/windows/`에 저장됩니다.

- `windows-verify-environment.json`
- `windows-verify-summary.json`
- `windows-verify-results.json`
- `storage-ci.json`, `storage-benchmark.json`
- `backup-ci.json`, `backup-benchmark.json`
- `interop-results.json`
- 각 단계 `.log`

---

## 3. 프로브별 동작

| 프로브 | 자동/수동 | 확인 내용 |
|---|---|---|
| `profile_isolation` | 자동 | 쿠키, document.cookie, localStorage, IndexedDB, CookieManager가 프로필 간 공유되지 않음 |
| `cdp_dom_click_input_scroll` | 자동 | DOM.getDocument, CDP 클릭, Input.insertText, 스크롤이 실제 페이지에 반영 |
| `user_intervention_detection` | **수동 입력 필요** | Agent 유사 CDP 루프 중 OS 입력을 감지하고 루프 중단 |
| `memory_30_tabs` | 자동(1–2분, 수 GB) | 전체 WebView2 프로세스, browser/renderer/GPU/utility, 생성/절전/종료 시점 |
| `storage_tests` | 자동 | amare-store SQLite/migration/repository/동시성 테스트 |
| `storage_ci` | 자동 | 실제 DB 통합 시나리오 JSON (`storage-ci.json`) |
| `storage_benchmark` | 자동(참고) | 1,000 bookmarks / 10,000 history / 300 tabs |
| `backup_tests` | 자동 | backup/restore/integrity/WAL 테스트 |
| `backup_ci` | 자동 | backup/restore 통합 JSON |
| `backup_benchmark` | 자동(참고) | 5 profiles / 1,000 bookmarks / 10,000 history / 100 tabs |
| `platform_key_tests` | Windows 자동 | DPAPI 보호/복호화 테스트 |
| `drm_capability_matrix` | 자동 | Widevine/PlayReady robustness, 코덱/해상도, powerEfficient(하드웨어 가속), HDR 신호 |
| `netflix_disney_paid_services` | **SKIPPED** | 명시적 계정 동의 없이는 시도하지 않음 |
| `drm_playback_manual` | **수동** | 공개 Shaka/Widevine 자산으로 재생/해상도/코덱/HW/HDR 기록 |
| `windows_installer_update_flow` | **UNVERIFIED** | Rust 로직 테스트만 완료, Windows 설치 프로그램은 Phase 2 |
| `windows_dpapi_keychain` | **UNVERIFIED** | 암호화 로직만 테스트, DPAPI/Hello 미검증 |

### 수동 개입 테스트

1. 하네스가 `Intervene now...`를 표시할 때까지 마우스를 건드리지 않습니다.
2. 표시 후 6초 안에 AMARE 창에서 마우스를 움직이거나 키를 누릅니다.
3. `intervention_detected=true`, `agent_stopped=true`가 기록되면 PASS입니다.
4. 입력이 없으면 `UNVERIFIED`입니다. FAIL로 처리하지 않지만 통과도 아닙니다.

### 수동 DRM 재생 테스트

자동 매트릭스는 **기능 존재 여부**만 확인합니다. 실제 재생은 다음처럼 별도 기록합니다.

1. WebView2에서 공개 Shaka 데모/공개 Widevine 자산을 엽니다.
2. 재생 성공 여부
3. `video.videoWidth`/`videoHeight`
4. 사용 코덱(H.264/VP9/AV1/HEVC)
5. 하드웨어 가속 여부(작업 관리자 GPU/`powerEfficient`)
6. HDR(`dynamic-range: high`) 지원 여부

유료 계정(Netflix/Disney+ 등)은 **사용자의 명시적 동의 없이 로그인하지 않습니다.**

---

## 4. 결과 JSON 상태

`windows-verify-results.json`:

- `PASS`: 실행 + 검증 성공
- `FAIL`: 실행 + 검증 실패
- `UNVERIFIED`: 미실행/수동 입력 없음/증거 부족
- `SKIPPED`: 정책상 의도적 미실행(예: 유료 계정)

각 결과에는 `executed`가 포함됩니다. `windows-verify-summary.json`은 빌드/테스트 단계의 exit code와 로그 경로를 기록합니다.

**실행 실패(exit code ≠ 0)는 PASS로 변환하지 않습니다.**

---

## 5. 해석 시 주의

- 30탭 메모리는 `msedgewebview2.exe` 전체 프로세스를 합산합니다. 다른 WebView2 앱이 있으면 함께 포함됩니다.
- 탭별 렌더러 매핑은 프로세스 수준 근사치이며, WebView2 내부 매핑을 직접 보장하지 않습니다.
- `after_suspend`/`after_close`는 즉시 회수되지 않을 수 있습니다. 5초 대기 후 측정합니다.
- DRM/VMP와 Netflix/Disney+ 재생은 별도 검증 없이는 지원으로 간주하지 않습니다.
- Windows Hello/DPAPI는 아직 미검증입니다.

---

## 6. 실패 시 조치

| 실패 | 가능한 원인 | 조치 |
|---|---|---|
| WebView2 Runtime 없음 | 미설치/손상 | Evergreen Bootstrapper 설치 |
| WinUI build 실패 | Windows SDK/WinAppSDK 불일치 | Build Tools/패키지 버전 확인 |
| `profile_isolation` 실패 | UDF 분리 미적용/가상 호스트 문제 | UDF 경로/가상 호스트 매핑 확인 |
| CDP click 실패 | 좌표/가상 호스트/입력 포커스 | 페이지 좌표/포커스 확인, 로그 첨부 |
| user intervention UNVERIFIED | 입력 없음/헤드리스 | 실제 사용자 입력으로 재시도 |
| memory FAIL | 프로세스 회수 지연/다른 WebView2 앱 | 다른 앱 종료 후 재측정 |
| DRM matrix FAIL | EME/Widevine 미지원 | WebView2/Edge 버전 확인 |
| update UNVERIFIED | 설치 프로그램 미구현 | Phase 2에서 설치/롤백 구현 후 재검증 |
