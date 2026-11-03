# WebView2 통합 테스트

- 하네스: `tools/windows-verify/`
- 관련 문서: [Windows 검증](WINDOWS_VALIDATION.md), [프로필 격리](PROFILE_ISOLATION_TESTING.md)

---

## 1. 자동 probe

| Probe | 검증 내용 | 상태 |
|---|---|---|
| `webview2_local_navigation` | Environment/새 WebView2/로컬 HTTP/제목/URL/뒤로/앞으로/reload/stop/새 창/다운로드 이벤트/control 제거 | 구현, Windows 실행 UNVERIFIED |
| `webview2_https_navigation` | `https://example.com` 네트워크 로딩 | 구현, 네트워크/Windows 실행 UNVERIFIED |
| `webview2_tab_lifecycle_10` | 10개 WebView2 생성, 5개 닫기, suspend, 나머지 닫기 | 구현, Windows 실행 UNVERIFIED |
| `profile_isolation` | 쿠키/localStorage/IndexedDB/CookieManager 실제 분리 | 구현, Windows 실행 UNVERIFIED |
| `cdp_dom_click_input_scroll` | CDP click/input/scroll | 기존 probe, Windows 실행 UNVERIFIED |
| `windows_browser_performance` | Environment 생성, local page, 5/10/20탭 생성/종료, process memory | 구현, Windows 실행 UNVERIFIED |
| `memory_30_tabs` | 30탭 메모리/프로세스 snapshot | 기존 probe, 실기기 측정 필요 |

로컬 테스트는 `LocalHttpServer`가 `127.0.0.1`의 임시 포트에서 HTML을 제공합니다. 인터넷 연결이 없어도 local probe는 실행할 수 있습니다. HTTPS probe는 네트워크 실패 시 FAIL이 아니라 UNVERIFIED로 남깁니다.

## 2. GUI/CI 분리

- headless probe는 CI에서 실행 시도하지만, WebView2 GUI가 불안정하면 결과를 UNVERIFIED로 분리합니다.
- Windows 11 실기기에서 실제 UI/IME/DPI/프로필 전환을 별도 확인해야 합니다.
- 이번 환경에서는 Windows WebView2를 실행하지 않았으므로 어떤 WebView2 probe도 PASS로 주장하지 않습니다.

## 3. 보안 설정

- 인증서 오류 무시 옵션을 사용하지 않습니다.
- `javascript:`, `data:`, `vbscript:`, `file:`은 block합니다.
- 외부 protocol은 사용자 확인 후 실행합니다.
- 로컬 새 탭 HTML은 외부 서버를 사용하지 않고, 메시지는 nonce로 검증합니다.
