# Windows 테스트 전략

- 원칙: Linux에서 가능한 로직은 Linux에서, Windows 전용은 Windows Runner/실기기에서 검증합니다.
- 실행하지 않은 테스트는 PASS로 표시하지 않습니다.

---

## 1. Linux에서 실행 가능

- Rust workspace: fmt, clippy, tests, service/store/backup
- C ABI: `amare-ffi` Rust tests, C header link smoke, C# P/Invoke interop
- C# `AMARE.Windows.Core`: 주소창/검색엔진/navigation policy/profile path/tab manager/download/new-tab HTML/adapter fake bridge
- C# native service interop: profile/settings/session/search/bookmarks/history + stale context error
- Address suggestions, Reader HTML builder/security, download policy/new tab
- 기존 `verify-linux.sh`

## 2. Windows Runner

`tools/verify-windows.ps1 -CI`:

- Rust workspace + service tests + JSON reports
- C ABI build + MSVC header compile
- C# interop tests (core logic + native service)
- WinUI 3 restore/build (required; 실패 시 FAIL)
- Portable ZIP packaging
- WinUI UIAutomation smoke (window/address/tabs/Find/Reader/AI/settings/session)
- Unsigned MSIX packaging 및 설치 시도
- WebView2 Runtime 존재 확인
- 기존 WebView2 headless probes

Windows Runner는 Windows 11 실기기와 동일하지 않습니다.

## 3. Windows 11 실기기에서 반드시 확인할 것

- 실제 WebView2 페이지 로딩/렌더링
- 탭 5/10/20개 메모리와 전환
- WebView2 UserDataFolder 프로필 분리(쿠키/로그인/캐시)
- IME/DPI/멀티 모니터/keyboard shortcut
- 다운로드 완료/파일 열기
- 외부 protocol 확인 dialog
- 비정상 종료 후 세션 복구
- 인증서 오류/HTTPS 동작
- DRM/미디어(이번 Phase 범위 밖)

## 4. 결과 구분

- PASS: 실제 실행해 성공
- FAIL: 실제 실행해 실패
- UNVERIFIED: 실행하지 않았거나 Runner 환경에서 판정하지 않음
- SKIPPED: 의도적으로 제외

## 5. 현재 상태 (2026-11-05, Windows Runner)

- Windows 자동 검증 하네스: `tools/verify-windows.ps1` + `tools/windows-verify/`
- Rust tests: **PASS**
- C# interop/core tests: **70/70 PASS** (bookmarks/history native round-trip, address suggestions, Reader builder 포함)
- WinUI XAML compile / MakePri / Debug+Release build / portable ZIP + updater helper: **PASS** (Windows Server 2025 Runner)
- UIAutomation GUI smoke: **42/42 PASS** (window, address, tabs, switch/close, native drag 9 combos+cancel+after-close, tear-off new window+re-merge, self-signed/expired/hostname-mismatch certificate dialog+retry+back+other-tab, cross-window forward+reverse, tab churn, WebSocket tab/window close cleanup, Find 1/2, Reader, AI panel, settings, API AI settings/no-key, manual update check, diagnostics, history, bookmarks, downloads, maximize/restore, new window, graceful/forced session restore, 7-tab stress)
- Update system unit tests: semver, stable/prerelease selection, checksum requirement PASS
- Portable package manifest, traversal, hash mismatch, apply, rollback, crash recovery, launch-failure rollback PASS
- Updater helper end-to-end smoke on isolated app/data directories PASS
- Repeat/downgrade/concurrent helper rejection, health-mark timeout, crash-after-apply recovery PASS
- Korean/space path clean ZIP launch PASS
- Release preflight version/arch/assets/hashes PASS
- AI mock success, 401/429/network errors, key redaction, SSE streaming, provider selection PASS
- Local web compatibility: SPA, cookie/session, upload, WebSocket connect/echo/reconnect, audio/video play/pause/volume/seek, fullscreen/PiP actual, media capability, zoom, redirect PASS
- SQLite/WAL recovery: **4/4 PASS** (WAL checkpoint/reopen, corrupt copy, missing DB restore, future schema copy)
- Native tab drag: **PASS** actual pointer first→last, last→first, first→middle, middle→last, middle→first, middle→other-middle, same-position, cancel; cross-window forward+reverse **PASS**; rapid/tear-off/create-close-move **UNVERIFIED**
- WebSocket actual AMARE tab-close and browser-close cleanup: **PASS**; connect/echo/reconnect PASS
- Certificate error: **PASS** Kestrel Tls13 handshake + actual self-signed/expired/hostname-mismatch event/dialog + retry/back/other-tab; no bypass; CA chain/SAN/EKU/custom-trust TLS simulation **PASS**; CurrentUser/WebView2 local CA **UNVERIFIED** (trust-store prompt/timeout)
- Certificate error: `ServerCertificateErrorDetected` Cancel + Korean warning; actual self-signed/expired/hostname-mismatch **PASS**; trusted local CA **UNVERIFIED**; no bypass
- 5/10/20/30-tab WebView2 memory breakdown, empty/content/background/suspend/close, startup/navigation/tab timing **PASS** (0.41 values in report); A~J lifecycle/ETL 수집 완료, ETW stack/leak root cause는 **UNVERIFIED**
- Native tab drag reorder: **PASS** actual pointer 9 combos+cancel+after-close+tear-off new window+re-merge; cross-window forward+reverse **PASS**
- New window open/close: **PASS**; cross-window tab move **UNVERIFIED**
- AI API request/response parsing tests: PASS
- Unsigned MSIX package: **PASS**; install: **BLOCKED** by unsigned publisher namespace
- GitHub Releases: 현재 public release 0개, 실제 공개 asset update flow UNVERIFIED
- Portable ZIP/MSIX: **PASS**, helper 포함; unsigned MSIX install **BLOCKED**
- Forced-kill session restore: **PASS** (6→6)
- Release metadata: `VERSION`, `amare-update.json`, `RELEASE_NOTES.md`, SHA-256 sidecar, `checksums.txt`, release manifest **PASS**
- API AI: provider 선택/streaming/cancel UI 및 mock provider PASS, 실제 key/Credential Manager end-to-end UNVERIFIED
- Windows 11 actual GUI: **UNVERIFIED**
- DPI/IME/multi-monitor: **UNVERIFIED**
- Diagnostics dialog: **PASS** (GPU 정보는 UNVERIFIED)
- 코드 서명/signed MSIX 업데이트: **BLOCKED**
- 공개 GitHub Release 기반 실제 업데이트: **UNVERIFIED**
