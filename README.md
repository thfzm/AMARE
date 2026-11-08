<p align="center"><img src="AMARE_logo.png" alt="AMARE logo" width="120"></p>

# AMARE

> **Built with love. Made for discovery.**

AMARE(아마레)는 Windows와 Android에서 매일 사용할 수 있는 독립형 범용 웹 브라우저를 목표로 합니다. AI 기반 탐색과 자료 관리는 브라우저를 대체하는 별도 제품이 아니라, 브라우저 안에서 자연스럽게 동작하는 보조 계층으로 설계합니다.

현재 저장소는 **AMARE 0.43.0 GPUI Native Windows Architecture Transition** 단계입니다. Windows는 기존 WinUI 3 앱을 유지하면서 `apps/amare-gpui`에 Rust + GPUI 0.2.2 기반 차세대 Windows 진입점과 WebView2 native HWND embedding을 추가했습니다. GPUI Windows x64 release build, process/window 생성, WebView2 child processes 0→6을 CI에서 확인했지만 focus/input/DPI/popup/full tab lifecycle은 **UNVERIFIED**이며, 실제 Windows 11/local CA/signing도 **UNVERIFIED/BLOCKED**입니다.

## 핵심 원칙

1. 일반 웹 브라우징의 속도, 안정성, 호환성을 최우선으로 합니다.
2. AI 기능은 브라우징 성능과 안정성을 저해하지 않습니다.
3. 보안 격리와 샌드박스를 성능을 이유로 제거하지 않습니다.
4. Windows와 Android는 각 플랫폼에 최적화된 UI를 사용하되, 핵심 로직과 데이터 구조를 공유합니다.
5. 실제로 구현하지 않은 기능을 구현했다고 보고하지 않습니다.
6. 기술적 한계는 숨기지 않고, 이유와 대안을 함께 문서화합니다.
7. AMARE 자체 서버를 운영하지 않는 로컬 우선(local-first) 제품을 지향합니다.

## Phase 0 제안 요약

| 영역 | 제안 | 상태 |
|---|---|---|
| Windows 브라우저 엔진 | WebView2 Evergreen (Chromium/Edge) | 제안 |
| Android 브라우저 엔진 | GeckoView 우선 검토, Phase 5에서 최종 결정 | 제안 / 결정 게이트 |
| 핵심 로직 | Rust (`amare-core` 계열) | 제안 |
| Windows UI | C# .NET LTS + WinUI 3 / Windows App SDK | 제안 |
| Android UI | Kotlin + Jetpack Compose | 제안 |
| 로컬 데이터 | SQLite(WAL, FTS5) + 암호화 저장소 | 제안 |
| AI | 일반 대화는 사용자가 선택한 공식 웹 AI, API는 선택 설정, Agent는 API/도구 호출 경로 | 제안 |
| 배포 | GitHub Releases + 서명된 설치 파일/APK | 제안 |

### 이 제안에서 의도적으로 하지 않는 것

- Chromium 전체를 처음부터 포크하여 유지보수하는 것
- Servo를 임의의 웹 콘텐츠를 렌더링하는 주 엔진으로 사용하는 것
- Android에서 Chrome 확장 프로그램 완전 호환을 v1 범위에 넣는 것
- Netflix/Disney+ 4K, 모든 사이트의 DRM 재생을 보장하는 것
- 웹 AI 사이트의 UI 자동화를 안정적인 API처럼 취급하는 것
- 사용자 몰래 페이지 내용, 쿠키, 검색어를 외부로 전송하는 것

## 문서

- [Phase 0 수정 보고서(승인됨)](docs/PHASE0_REPORT.md)
- [Phase 0.5 검증 결과](docs/PHASE0_5_REPORT.md)
- [Phase 1 스캐폴딩 현황](docs/PHASE1_SCAFFOLD.md)
- [Windows 검증](docs/WINDOWS_VALIDATION.md)
- [Windows RC 빌드](docs/WINDOWS_RC_BUILD.md)
- [GitHub 연결](docs/GITHUB_SETUP.md)
- [GitHub 공개 감사](docs/GITHUB_PUBLICATION_AUDIT.md)
- [공개 정책/라이선스 선택지](docs/PROJECT_LICENSE_OPTIONS.md)
- [WebView2 테스트](docs/WEBVIEW2_TESTING.md)
- [프로필 격리 테스트](docs/PROFILE_ISOLATION_TESTING.md)
- [세션 복구](docs/SESSION_RECOVERY.md)
- [탭/메모리](docs/TAB_MEMORY_MANAGEMENT.md)
- [Windows 성능](docs/WINDOWS_PERFORMANCE.md)
- [Windows 11 체크리스트](docs/WINDOWS_11_MANUAL_CHECKLIST.md)
- [Windows 브라우저 아키텍처](docs/WINDOWS_BROWSER_ARCHITECTURE.md)
- [WebView2 통합](docs/WEBVIEW2_INTEGRATION.md)
- [탭 수명주기](docs/TAB_LIFECYCLE.md)
- [프로필/WebView2](docs/PROFILE_WEBVIEW2.md)
- [Windows UI 설계](docs/WINDOWS_UI_DESIGN.md)
- [Windows 테스트](docs/WINDOWS_TESTING.md)
- [Windows 11 검증 가이드](docs/WINDOWS_VERIFICATION.md)
- [Android 다음 단계 설계](docs/ANDROID_NEXT_STEPS.md)
- [Android Keystore/JNI 분석](docs/ANDROID_KEYSTORE.md)
- [Android 데이터 암호화](docs/ANDROID_DATA_ENCRYPTION.md)
- [Android 브라우저 다음 우선순위](docs/ANDROID_BROWSER_ROADMAP.md)
- [Android 보안](docs/ANDROID_SECURITY.md)
- [Android 기기 테스트](docs/ANDROID_DEVICE_TESTING.md)
- [Android Release](docs/ANDROID_RELEASE.md)
- [Android 비밀번호 관리자](docs/ANDROID_PASSWORD_MANAGER.md)
- [Android 새 창/탭](docs/ANDROID_TABS.md)
- [Android 웹 알림](docs/ANDROID_NOTIFICATIONS.md)
- [Android 통합 테스트](docs/ANDROID_INTEGRATION_TESTS.md)
- [Android 데이터 관리](docs/ANDROID_DATA_MANAGEMENT.md)
- [Android 성능 측정](docs/ANDROID_PERFORMANCE.md)
- [Android 1.0 출시 체크리스트](docs/ANDROID_1.0_RELEASE_CHECKLIST.md)
- [GitHub Actions 자동 검증](docs/GITHUB_ACTIONS.md)
- [저장소 아키텍처](docs/STORAGE_ARCHITECTURE.md)
- [공통 서비스 아키텍처](docs/SERVICE_ARCHITECTURE.md)
- [프로필 관리](docs/PROFILE_MANAGEMENT.md)
- [설정 아키텍처](docs/SETTINGS_ARCHITECTURE.md)
- [검색 아키텍처](docs/SEARCH_ARCHITECTURE.md)
- [데이터베이스 스키마](docs/DATABASE_SCHEMA.md)
- [Library 아키텍처](docs/LIBRARY_ARCHITECTURE.md)
- [AI 데이터 모델](docs/AI_DATA_MODEL.md)
- [Workspace 기억](docs/WORKSPACE_MEMORY.md)
- [백업 및 복원](docs/BACKUP_RESTORE.md)
- [키 보호](docs/KEY_PROTECTION.md)
- [데이터베이스 복구](docs/DATABASE_RECOVERY.md)
- [기술 스택](docs/TECH_STACK.md)
- [브라우저 엔진 비교](docs/ENGINE_COMPARISON.md)
- [아키텍처](docs/ARCHITECTURE.md)
- [보안 설계](docs/SECURITY_DESIGN.md)
- [로드맵](docs/ROADMAP.md)
- [기능 명세와 지원 범위](docs/FEATURES.md)
- [개발 가이드](docs/DEVELOPMENT.md)

## MVP Core (v0.1)

Windows 11에서 웹 렌더링, 탭, 주소창/검색, 뒤로/앞으로/새로고침/중지, 북마크, 방문 기록, 다운로드, 세션 복원, 기본 설정, 기본 보안/권한을 제공하는 최소 브라우저입니다. 읽기 전용 AI 패널은 선택이며, Agent/Research/Library/작업 기억/Android 정식 버전은 이후 단계입니다.

## 상태

- Phase 0: **승인됨**
- Phase 0.5: **부분 검증 완료** — Linux 실행 가능 항목 통과, Windows/Android/DRM/성능은 미검증
- Phase 1: **스캐폴딩 + SQLite 저장소 + Phase 1.5 백업/복원/키 보호 + Phase 1.6 Library/AI/Workspace 데이터 계층 + Phase 1.7 공통 서비스 계층 구현 완료** — WebView2 UI/Windows DPAPI 실기기 검증은 보류
- Phase 2: **Windows WinUI 3 + WebView2 브라우저 본체 + C ABI/C# Adapter 구현** — Windows Runner/실기기 검증 UNVERIFIED
- Phase 2.1: **Windows 안정화 + 프로필/세션/탭 검증 하네스 + WebView2 probe + 성능/체크리스트** — Windows 실행 UNVERIFIED
- Phase 2.2: **Windows 검증 게이트 + 통합 결과 JSON + RC 빌드/실행 절차 + GitHub 연결 안내** — Windows GUI UNVERIFIED
- Windows 0.15.0: **탭/주소 제안/북마크/방문 기록/다운로드/설정/Reader/Find/AI Web Panel/로고** 구현 — WinUI build·interop PASS
- Windows 0.16.0: **실제 GUI UIAutomation 10/10 PASS**, WinUI Debug/Release, portable ZIP, unsigned MSIX build PASS; MSIX install/서명 BLOCKED, Windows 11 실기기 UNVERIFIED
- Windows 0.17.0: **GitHub Releases update check + optional OpenAI-compatible API AI core/UI** 구현; 실제 Release 0개/실제 API key 미제공으로 end-to-end UNVERIFIED
- Windows 0.18.0: **portable updater helper + manifest/checksum + backup/rollback + crash recovery**, API provider/mock streaming, diagnostics, release draft prep 구현; 실제 공개 Release/API key/서명/Windows 11 실기기는 UNVERIFIED/BLOCKED
- Windows 0.19.0: **반복/다운그레이드/동시 helper 차단, cancel temp cleanup, health-marker timeout, 한글·공백 경로 및 클린 ZIP 실행, release preflight, GUI 19/19**; 실제 Windows 11/API key/서명은 여전히 UNVERIFIED/BLOCKED
- Windows 0.20.0: **local web compatibility, Ctrl+Tab, forced session restore, history search, 30-tab memory, 데이터/보안 회귀 검증**; 실제 Windows 11/API key/서명은 여전히 UNVERIFIED/BLOCKED
- Windows 0.21.0: **memory scope breakdown, WebSocket/audio probes, certificate attempt, new-window, forced restore, 5/10/20/30-tab**; actual drag/cert/DPI/API/signing은 UNVERIFIED/BLOCKED
- Windows 0.22.0: **video seek/fullscreen/PiP, WebSocket connect/reconnect, SQLite/WAL recovery, media range server**; native drag/창 간 이동/cert UX/WS close cleanup은 UNVERIFIED, public release 없음
- Windows 0.23.0: **certificate error event Cancel UX, WinUI lifecycle 수정, WebSocket/cert probe 재검증**; WS tab-close cleanup observed false, native drag/cert actual/DPI/API/signing은 UNVERIFIED/BLOCKED
- Windows 0.24.0: **WebSocket tab/window close cleanup 실제 UI PASS, reconnect 테스트 계측 수정, 0.24 회귀**; native drag/창 간 이동/cert actual/DPI/API/signing은 UNVERIFIED/BLOCKED
- Windows 0.25.0: **실제 native tab first→last drag PASS, WebSocket tab/window cleanup 회귀 PASS, GUI 25/25**; 창 간 이동/cert actual/DPI/API/signing은 UNVERIFIED/BLOCKED
- Windows 0.26.0: **실제 두 창 간 tab transfer PASS, native first→last drag PASS, GUI 26/26**; 나머지 drag 조합/tear-off/cert actual/DPI/API/signing은 UNVERIFIED/BLOCKED
- Windows 0.27.0: **native drag first/last/first-middle/middle-last PASS, GUI 30/30, WebSocket/cross-window 회귀**; cert actual/TLS server, middle→first/cancel/tear-off/DPI/API/signing은 UNVERIFIED/BLOCKED
- Windows 0.28.0: **Kestrel TLS handshake PASS, self-signed/expired/hostname-mismatch cert event PASS, 실제 cert dialog PASS, native drag 8개 조합+cancel PASS, cross-window 정/역방향 PASS, GUI 34/34, 139 PASS/0 FAIL**; 나머지 drag 변형/tear-off/local trusted CA/DPI/API/signing은 UNVERIFIED/BLOCKED
- Windows 0.29.0: **self-signed/expired/hostname-mismatch 실제 certificate dialog+retry+back PASS, native drag 9개 조합+after-close PASS, cross-window 정/역방향 PASS, GUI 40/40, 139 PASS/0 FAIL**; rapid/tear-off/close-during-move/local trusted CA/DPI/API/signing은 UNVERIFIED/BLOCKED
- Windows 0.30.0: **인증서 3종 실제 dialog+retry/back/other-tab PASS, local trusted CA UNVERIFIED, native drag/cross-window/session 회귀 PASS, GUI 41/41, 139 PASS/0 FAIL**; tear-off/이동 중 창 종료/장시간/DPR/API/signing은 UNVERIFIED/BLOCKED
- Windows 0.31.0: **인증서 3종 실제 dialog+retry/back/other-tab, native drag/cross-window/session/update 회귀 PASS, GUI 41/41, 139 PASS/0 FAIL; Diagnostics에 DPI/display/WebView2/log/update/cert 상태 추가**; local trusted CA/tear-off/이동 중 창 종료/30분 stress/Windows 11 실기기/DPI/API/signing은 UNVERIFIED/BLOCKED
- Windows 0.32.0: **CA chain/SAN/EKU/custom-trust TLS simulation+untrusted 거부 PASS, 인증서 3종 실제 dialog+retry/back/other-tab PASS, drag/cross-window/session/update 회귀 PASS, GUI 41/41, 140 PASS/0 FAIL**; CurrentUser trust-store/WebView2 local CA, tear-off/이동 중 창 종료/30분 stress/Windows 11 실기기/DPI/API/signing은 UNVERIFIED/BLOCKED
- Windows 0.33.0: **native tear-off 실제 새 창+tab snapshot PASS, GUI 42/42, 140 PASS/0 FAIL, CA chain/custom-trust simulation+untrusted 거부 PASS**; CurrentUser/WebView2 local CA/30분 stress/tear-off 재이동/이동 중 창 종료/Windows 11/DPI/API/signing은 UNVERIFIED/BLOCKED
- Windows 0.34.0: **tear-off+실제 re-merge PASS, 30분/689-cycle stress 완료(메모리 안정, handle 증가 추세 관찰), GUI 42/42, 140 PASS/0 FAIL**; 실제 Windows 11/local CA/signing은 UNVERIFIED/BLOCKED
- Windows 0.35.0: **keyboard-only 30분 stress 1,033 cycles 완료(메모리 안정, handles 965→6,736), handle type31/type8 증가 확인, tear-off re-merge/GUI 42/42/140 PASS**; harness 분리 결과 앱/WebView2 lifecycle leak 의심, 수정/검증은 UNVERIFIED; Windows 11/local CA/signing은 BLOCKED
- Windows 0.36.0: **type31=Composition, type8=Process 동적 확인, 1033-cycle 30분 stress에서 965→6,736, UIA per-cycle 제거 후에도 재현; 생성 stack/근본 수정은 UNVERIFIED**, tear-off re-merge/GUI 42/42/140 PASS; 실제 Windows 11/local CA/signing은 BLOCKED
- Windows 0.37.0: **type31=Composition/type8=Process 동적 확인, Process handle targets=msedgewebview2(5728) 11→233, Composition 41→490, 171-cycle type run**; tab lifecycle 연관 확인, 생성 stack/정리 실패 지점과 수정은 UNVERIFIED, GUI 42/42/140 PASS
- Windows 0.38.0: **독립 handle-forensics 도구 build/self-test PASS, Process targets=msedgewebview2(4568) 10→232, Composition 37→486, 172-cycle type run**; 생성 stack/ETW/수정은 UNVERIFIED, GUI 42/42/142 PASS
- Windows 0.39.0: **handle-forensics 도구 build/self-test PASS, Process targets=msedgewebview2(9444) 11→233, Composition 41→490, 171-cycle 0.39 run**; 생성 stack/ETW/수정은 UNVERIFIED, GUI 42/42/142 PASS
- Windows 0.40.0: **handle-forensics diff/PID-start attribution, ETW providers 9개+WPR 존재 확인, Process target(9356) 11→233, Composition 41→490, 0.40 171-cycle run**; 실제 ETW stack/cleanup 수정은 UNVERIFIED, GUI 42/42/143 PASS
- Windows 0.41.0: **WPR ETL 1.44GB 생성, A~J lifecycle 16:31 PASS, idle stable(855→858), tab close 후 Process/Composition 유지(E 995/G 1217, 5분 1186)**; stack/cleanup 원인/수정은 UNVERIFIED, GUI 42/42/143 PASS
- Windows 0.42.0: **ETL 606MB/SHA 815dee…, 25.7M events/CSV 생성, A~J lifecycle 855→858 idle·종료 후 Process/Composition 유지 확인**; Handle provider 부재로 생성 stack/cleanup 원인은 UNVERIFIED, GUI 42/42/143 PASS
- Windows 0.43.0: **GPUI 0.2.2 Rust shell Windows release build+smoke PASS, native window handle+WebView2 child 0→6 PASS, WinUI 3/C# preserved**; GPUI focus/input/DPI/popup/full tabs/Windows 11은 UNVERIFIED, 기존 GUI 42/42/143 PASS
- Android Phase 5: **GeckoView 셸 + 기본 브라우저 연동 + 데이터 관리 + 북마크/방문 기록/다운로드/사이트 권한 + 세션 복구 + 데이터 암호화 + 비밀번호 관리자 + 새 창/웹 알림 + HTTPS-only 보안 설정** — 빌드/단위 테스트 PASS, 실기기 UNVERIFIED (0.12.0)
- Rust AndroidKeystore JNI 연결: Rust Android 런타임 미탑재로 보류 — [JNI 분석](docs/ANDROID_KEYSTORE.md)
- 기존 로드맵 Phase 2–6: 미착수

Windows 검증은 [가이드](docs/WINDOWS_VERIFICATION.md)에 따라 `tools/verify-windows.ps1`로 실행합니다. [GitHub Actions](docs/GITHUB_ACTIONS.md) workflow는 작성되었지만 실제 실행은 아직 확인되지 않았습니다. 결과는 [Phase 0.5 검증 결과](docs/PHASE0_5_REPORT.md)에 반영해야 합니다.

## 라이선스 및 공개 정책

AMARE는 **GitHub Public 저장소 + All rights reserved** 정책입니다.

- 소스코드는 공개 열람할 수 있습니다.
- MIT 또는 Apache-2.0 오픈소스 라이선스를 적용하지 않습니다.
- 별도의 서면 허가 없이 복제·수정·재배포·상업적 사용 권한을 부여하지 않습니다.
- GitHub의 열람/fork 등 플랫폼 기능은 일반적인 오픈소스 재사용 허가와 다릅니다.
- 제3자 의존성과 자산에는 각자의 라이선스가 적용됩니다.
- 현재 로고의 출처/권리와 Windows 재배포 고지는 [자산 귀속](docs/ASSET_ATTRIBUTION.md), [라이선스 선택지](docs/PROJECT_LICENSE_OPTIONS.md), [기술 스택](docs/TECH_STACK.md)을 참고하세요.

전체 저작권 고지는 [COPYRIGHT.md](COPYRIGHT.md)에 있습니다. 이 저장소는 오픈소스 프로젝트가 아닙니다.
