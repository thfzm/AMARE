# WebView2 통합

- 구현: `apps/windows/AMARE.Windows/MainWindow.xaml.cs`
- 패키지: `Microsoft.Web.WebView2 1.0.4314.35`
- 관련 문서: [브라우저 아키텍처](WINDOWS_BROWSER_ARCHITECTURE.md), [프로필/WebView2](PROFILE_WEBVIEW2.md), [탭 수명주기](TAB_LIFECYCLE.md)

---

## 1. 환경 생성

프로필 전환/초기화 시:

```csharp
var folder = ProfilePathPolicy.GetWebViewDataFolder(appDataRoot, profileId);
_environment = await CoreWebView2Environment.CreateAsync(null, folder);
```

- 프로필별 UserDataFolder를 사용해 쿠키/캐시/로그인/저장소를 분리합니다.
- `null` browserExecutableFolder → Evergreen Runtime 사용.
- 환경은 프로필 전환 시 재사용하지 않으며, 기존 WebView2를 모두 닫은 뒤 새로 만듭니다.

## 2. 탭별 WebView2

- 탭마다 독립 `WebView2` control과 `CoreWebView2` 인스턴스를 사용합니다.
- 같은 프로필 안에서는 하나의 `CoreWebView2Environment`를 공유합니다.
- 탭을 닫으면 `CoreWebView2.Stop()` + `CoreWebView2.Close()` 후 visual tree에서 제거합니다.
- 프로필 전환/삭제 전 모든 control을 닫아 UserDataFolder 잠금을 해제합니다.

## 3. 이벤트 → 상태

| WebView2 event | UI/model |
|---|---|
| `NavigationStarting` | URL, loading, external/block policy |
| `SourceChanged` | URL 동기화 |
| `DocumentTitleChanged` | 탭 제목/window title |
| `NavigationCompleted` | loading 종료, back/forward, error status |
| `NewWindowRequested` | 내부 새 탭 또는 외부 앱 확인 |
| `DownloadStarting` | 파일명/경로 검증, 다운로드 감지 |
| `WebMessageReceived` | 새 탭 페이지 검색 요청(nonce 검증) |

WebView2가 실제로 보고한 `CanGoBack`/`CanGoForward`를 사용하며, UI가 별도 추정 상태를 만들지 않습니다.

## 4. 보안 설정

- `IgnoreCertificateErrors`를 켜지 않습니다.
- `javascript:`, `data:`, `vbscript:`, `file:` navigation은 차단합니다.
- 외부 protocol은 `ContentDialog` 확인 후 실행합니다.
- WebView2 기본 sandbox/site isolation 설정을 변경하지 않습니다.
- 새 탭 HTML은 외부 리소스/서버에 의존하지 않습니다.

## 5. 오류 처리

- `NavigationCompleted.IsSuccess == false`이고 `OperationCanceled`가 아니면 상태 표시줄에 오류를 표시합니다.
- WebView2 환경 생성 실패는 시작 오류 dialog로 표시합니다.
- 다운로드 경로 검증 실패 시 다운로드를 취소합니다.
- 미검증: Windows 실기기에서 WebView2 process crash/recovery, DRM, 실제 다운로드 완료, IME/DPI.
