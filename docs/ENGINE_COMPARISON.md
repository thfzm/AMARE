# 브라우저 엔진 비교

- 문서 상태: **제안(Proposed)**
- 작성일: 2026-11-03
- 목적: Windows/Android에서 AMARE를 구현할 엔진을 선택하고, 선택의 한계와 변경 조건을 명확히 한다.

---

## 1. 비교 대상

| 구분 | 대상 | 설명 |
|---|---|---|
| A | **Chromium Content API / CEF** | Chromium의 content 계층 또는 Chrome runtime을 임베딩. 엔진을 앱에 번들. |
| B | **WebView2** | Windows에 설치된 Edge/Chromium 런타임을 임베딩. Evergreen 또는 Fixed 버전. |
| C | **Android WebView** | Android 시스템의 Chromium WebView. OS/Play Store가 업데이트. |
| D | **GeckoView** | Firefox/Gecko 엔진을 Android 앱에 임베딩. |
| E | **Chromium //chrome 포크** | Chrome 브라우저 전체를 포크해 자체 브라우저로 빌드. |
| F | **Servo** | Rust 기반 신규 엔진. `servo` crate/LTS 제공. |

> CEF는 "Content API(Alloy style)"와 "Chrome runtime(Chrome style)"을 구분해야 합니다. 2024년 M125 이후 Alloy bootstrap은 제거되었고, 확장 프로그램은 Chrome runtime + Views 조합에서만 지원됩니다. (Alloy style 자체는 Chrome bootstrap으로 계속 사용 가능)

---

## 2. 평가 기준

| 기준 | 가중치 | 설명 |
|---|---:|---|
| 실제 웹사이트 호환성 | 20% | 로그인, 결제, 동영상, 국내 사이트 포함 |
| 보안/격리 | 18% | 멀티프로세스, 사이트 격리, 샌드박스, 업데이트 |
| 성능/메모리 | 15% | 시작, 탭 전환, 30탭, GPU |
| 엔진 제어/확장성 | 12% | 요청 가로채기, 확장, 정책, 커스텀 기능 |
| AI Agent 제어 | 10% | 실제 가시 탭 클릭/입력/스크롤/관찰 |
| DRM/미디어 | 8% | Widevine/PlayReady, H.264/AAC, HD |
| 확장 프로그램 | 7% | Chrome/WebExtensions 호환 |
| 업데이트/유지보수 | 6% | 보안 패치, 빌드/배포 부담 |
| 라이선스/배포 | 2% | 재배포, 코덱, DRM, 플랫폼 정책 |
| 코드 재사용 | 2% | Windows/Android/코어 공유 |

가중치는 AMARE의 "매일 쓰는 브라우저" 목표에 맞춘 것입니다. 기능 수보다 안정성/보안/업데이트를 우선합니다.

---

## 3. 요약 비교

표기: ◎ 매우 적합, ○ 적합, △ 제한적, × 부적합, ? 검증 필요

| 기준 | CEF | WebView2 | Android WebView | GeckoView | Chromium 포크 | Servo |
|---|---|---|---|---|---|---|
| 웹 호환성 | ◎ | ◎ | ○ | ○ | ◎ | △ |
| 멀티프로세스/샌드박스 | ◎ | ◎ | △ | ○ | ◎ | △ |
| 사이트 격리 | ◎ | ◎ | × (공유 렌더러) | △ (Fission 상태 주의) | ◎ | ? |
| 성능/메모리 | ○ (번들 큼) | ○ (탭당 렌더러) | ○ (가벼움) | △ (엔진 큼) | ○ | ○ (경량) |
| 엔진 제어 | ◎ | △ | △ | ○ | ◎ | ◎ |
| AI Agent 제어 | ◎ (CDP) | ◎ (CDP) | △ (JS/입력 주입) | ○ (content script+PanZoom/IME) | ◎ (CDP) | △ |
| DRM/미디어 | △ (코덱/Widevine 별도) | ○ (Edge 경로, VMP 검증 필요) | △ (L3, 서비스 제한) | △ (L3, 720p급) | △~◎ (라이선스) | × |
| Chrome 확장 | ○ (Chrome runtime, 제한) | △ (unpacked, 제한) | × | × (WebExtensions만) | ◎ | × |
| WebExtensions | × | × | × | ○ (부분) | × (별도) | × |
| 보안 업데이트 | △ (직접 빌드/배포) | ◎ (MS 자동) | ◎ (Play 자동) | △ (앱 릴리스) | △ (직접) | △ |
| 운영 부담 | × (매우 큼) | ◎ (낮음) | ◎ (낮음) | ○ (중간) | × (매우 큼) | ○ |
| 라이선스 | ○ (BSD, 코덱 별도) | △ (MS 라이선스) | ○ (Chromium/Google) | ○ (MPL 2.0) | ○ (BSD, 코덱/DRM 별도) | ○ (MPL/Apache) |
| Windows | ◎ | ◎ | × | × | ◎ | ○ |
| Android | × (공식 미지원) | × | ◎ | ◎ | ◎ | △ |
| 코드 재사용 | △ | △ | △ | △ | ◎ (동일 코드베이스) | ○ |

> "◎"는 해당 플랫폼에서 제품 수준으로 사용 가능하다는 뜻이지, 모든 요구사항을 자동 충족한다는 뜻이 아닙니다. 특히 DRM/확장은 별도 검증이 필요합니다.

---

## 4. 엔진별 상세

### 4.1 Chromium Content API / CEF

**장점**

- Chromium의 멀티프로세스, 샌드박스, GPU, 최신 웹 플랫폼을 그대로 사용합니다.
- Chrome runtime(Chrome style + Views)을 사용하면 확장 프로그램, 자동완성, 권한 UI, 표준 Chrome UI 일부를 사용할 수 있습니다.
- CDP를 사용할 수 있어 Agent가 실제 탭을 조작하기 좋습니다.
- 요청 가로채기/응답 수정/다운로드/권한/프로필을 C++ 레벨에서 세밀하게 제어할 수 있습니다.
- 엔진을 번들하므로 Windows/Android(별도 빌드)에서 동일 계열 엔진을 사용할 수 있습니다.

**단점**

- **운영 부담이 가장 큽니다.** 2026년 9월부터 Chrome은 2주 주기로 마일스톤을 배포합니다. CEF를 따라가며 보안 패치를 2주마다 빌드/서명/배포하는 것은 소규모 팀에게 현실적이지 않습니다.
- 공식 CEF 바이너리는 proprietary codecs(H.264/AAC)가 비활성화되어 있습니다. 활성화하려면 직접 빌드하고 MPEG-LA 등 라이선스를 검토해야 합니다.
- Widevine은 CDM 배포/라이선스가 필요하고, 서비스(Netflix 등)가 비인증 브라우저를 차단할 수 있습니다.
- Chrome runtime은 Views 프레임워크를 요구하고 off-screen rendering을 지원하지 않습니다. 커스텀 브라우저 UI와의 통합 제약이 있습니다.
- Rust 바인딩은 C++ API에 비해 성숙도가 낮습니다. C++ 호스트 또는 C#/C++ 혼합이 필요할 수 있습니다.
- 설치 파일이 커지고, 엔진 업데이트마다 회귀 테스트가 필요합니다.

**적합한 경우**

- Chrome 확장 패리티가 제품의 필수 조건이고, 이를 유지할 엔지니어링 조직이 있을 때
- 엔진 내부 정책(프록시, DNS, 프로세스 모델, 요청 처리)을 깊게 제어해야 할 때
- DRM/코덱 라이선스를 직접 확보할 수 있을 때

**AMARE 판단:** v1–v2에서는 채택하지 않습니다. WebView2가 차단 요인으로 판명될 때만 별도 마이그레이션 프로젝트로 검토합니다.

---

### 4.2 WebView2

**장점**

- Windows 11에 Evergreen Runtime이 사실상 기본 제공되어 설치 크기와 배포가 가볍습니다.
- Edge와 동일한 Chromium/멀티프로세스/사이트 격리/GPU 경로를 사용합니다.
- Microsoft가 보안 업데이트를 제공하므로 패치 부담이 낮습니다.
- 프로필, 쿠키, 다운로드, 권한, 트래킹 방지(기본/균형/엄격), 테마, 확장(unpacked), CDP를 API로 제어할 수 있습니다.
- CDP(`CallDevToolsProtocolMethodAsync`)로 실제 WebView2 탭을 조작할 수 있습니다.
- DRM/코덱은 Edge Runtime 경로를 사용하므로 Windows에서 가장 현실적인 선택입니다.

**단점**

- 엔진 내부를 수정할 수 없습니다.
- 확장 지원은 "unpacked extension" 중심이며, Chrome Web Store/Edge 확장 UI를 그대로 제공하지 않습니다. 호스트가 관리 UI와 권한을 구현해야 하고, 일부 확장은 동작하지 않습니다. Microsoft의 기능 비교 표에는 Browser Extensions가 "Off/Configurable No"로 표시되어 있지만, 별도의 `CoreWebView2Profile.AddBrowserExtensionAsync` API가 제공됩니다. 실제 지원 범위는 Runtime 버전/플랫폼에 따라 다르므로 Phase 1 스파이크로 검증해야 합니다.
- 탭마다 WebView2를 만들면 렌더러 프로세스/메모리가 증가합니다. `TrySuspendAsync`, `MemoryUsageTargetLevel.Low`, 탭 폐기 정책이 필요합니다.
- Edge Runtime 업데이트에 따른 회귀 위험이 있습니다.
- 내장 번역/읽기 모드/강제 다크/PIP 등은 직접 구현하거나 실험적 플래그에 의존해야 합니다.
- Microsoft 라이선스와 "브라우저 제품" 사용 범위를 법무 검토해야 합니다.
- Windows 전용입니다.

### 4.2.1 WebView2가 제공하는 것 vs AMARE가 구현하는 것

| 기능 | WebView2 API/동작 | AMARE 구현 |
|---|---|---|
| 렌더링/탐색 | WebView2 렌더러, `NavigationStarting/Completed`, `NavigationHistory`(현재 탭) | 탭 스트립, 주소창, 검색, 세션 복원 |
| 프로필 | `CreateCoreWebView2ControllerOptions`, `ProfileName`, `IsInPrivateModeEnabled`, 프로필별 쿠키/설정 | 프로필 UI, Workspace, 프로필 간 정책 |
| 세션 | 같은 프로필의 WebView2는 쿠키/스토리지 공유 | 로그인 세션 유지, 탭별 독립 세션은 별도 프로필/UDF 필요 |
| 쿠키 | `Profile.CookieManager`(`GetCookiesAsync`, add/update/delete), `CoreWebView2Cookie`(HttpOnly/Session/SameSite) | 쿠키 관리 UI, 사이트별 삭제, AI/로그 노출 금지 |
| 캐시 | `Profile.ClearBrowsingDataAsync(kinds, start, end)` | 캐시/사이트 데이터 삭제 UX, 보존 정책 |
| 다운로드 | `DownloadStarting`, `CoreWebView2DownloadOperation`(Pause/Resume/Cancel, bytes/state) | 다운로드 관리자 UI/DB/검색/재시도/폴더 정책 |
| 방문 기록 | 영구 브라우저 방문 기록 저장소는 제공하지 않음. 현재 탭 `NavigationHistory`만 제공 | `NavigationCompleted`/`SourceChanged` 기반 기록 DB, 검색/가져오기/내보내기 |
| 권한 | `PermissionRequested`, `Profile.SetPermissionStateAsync`, `GetNonDefaultPermissionSettingsAsync` | 사이트별 권한 UI, 기본값/정책, 권한 회수 |
| 보안 | HTTPS/인증서 오류, 트래킹 방지(Basic/Balanced/Strict), SmartScreen | 보안 설정 UI, 예외 정책, 광고/트래커 필터(별도) |
| 찾기/확대/인쇄 | `Find`, `Zoom`, `Print/PrintToPdf` | 찾기 바, 확대 UI, 인쇄/PDF UI |
| 미디어/DRM | EME, Edge Runtime의 PlayReady/Widevine/코덱 | 서비스별 재생 검증, 실패 시 외부 앱 안내 |
| 확장 | unpacked extension 로드 API | 확장 관리 UI, 권한 표시, 업데이트/호환성 정책 |
| 자동화 | CDP `CallDevToolsProtocolMethodAsync`, `ForSession` | Agent 계획/정책/승인/원장/관찰 |
| 업데이트 | Evergreen Runtime 자동 업데이트 | 앱 업데이트/서명/롤백/채널 |

### 4.2.2 프로필/세션/데이터 격리

- **같은 프로필의 탭은 쿠키/로그인 세션/캐시를 공유**합니다. 이는 일반 브라우저의 탭 동작과 같습니다. "탭마다 독립 세션"은 기본 동작이 아니며, 필요하면 탭별로 별도 프로필/UDF를 만들어야 합니다(메모리/프로세스 비용 큼).
- **프로필 분리**는 `CoreWebView2ControllerOptions.ProfileName`으로 만들 수 있고, 여러 프로필이 하나의 UDF 아래에서 쿠키/설정/스토리지를 분리할 수 있습니다. 더 강한 프로세스 격리가 필요하면 프로필별 UDF/Environment를 사용합니다.
- **시크릿/민감 프로필**은 `IsInPrivateModeEnabled` 또는 별도 UDF를 사용합니다.
- **AMARE 데이터(북마크/기록/비밀번호/Library/대화)** 는 엔진 저장소와 별도로 AMARE SQLite/vault에 프로필 ID로 저장합니다.
- **쿠키**는 `CookieManager`로 읽기/쓰기/삭제가 가능하지만 HttpOnly/SameSite/Session 쿠키를 포함하므로 민감 데이터로 취급하고 로그/AI에 절대 넣지 않습니다.
- **캐시**는 직접 열람 API가 없고 `ClearBrowsingDataAsync`로 삭제합니다.
- **다운로드**는 엔진이 작업을 제공하고 AMARE가 DB/UI를 소유합니다.
- **방문 기록**은 엔진이 영구 저장하지 않으므로 AMARE가 직접 기록합니다.
- **권한**은 프로필/origin 단위로 저장되며 `SetPermissionStateAsync`로 제어합니다.

### 4.2.3 CDP: DOM 분석과 실제 화면 조작

| 목적 | CDP 명령(예) | 비고 |
|---|---|---|
| DOM 읽기 | `DOM.getDocument`, `DOM.querySelector(All)`, `Runtime.evaluate` | 렌더링 후 DOM |
| 접근성 트리 | `Accessibility.getFullAXTree` | 요소 탐색 보조 |
| 요소 좌표 | `DOM.getBoxModel`, `DOM.getContentQuads` | 클릭 좌표 계산 |
| 클릭 | `Input.dispatchMouseEvent`(mousePressed/Released) | CDP 입력은 trusted로 전달됨 |
| 텍스트 입력 | `Input.insertText`, `Input.dispatchKeyEvent` | IME/한글 입력은 별도 검증 |
| 스크롤 | `Input.synthesizeScrollGesture`/`Input.dispatchMouseEvent` | |
| 탐색 | `Page.navigate`, `Page.reload`, `Page.stopLoading`, `Page.getNavigationHistory`, `Page.navigateToHistoryEntry` | |
| 스크린샷 | `Page.captureScreenshot` | 비전 fallback |
| OOPIF/worker | `Target.setAutoAttach`, `Target.attachToTarget`, `CallDevToolsProtocolMethodForSession` | 교차 출처 iframe은 별도 target |

**한계/주의**

- CDP는 Chromium/Edge의 공식 공개 API가 아니며 하위 호환이 보장되지 않습니다. WebView2가 래핑하지만 명령 의미/가용성은 Edge 업데이트에 따라 바뀔 수 있습니다.
- `Input.dispatchMouseEvent`는 DOM `.click()`과 달리 실제 입력에 가깝지만, `navigator.webdriver`/안티봇/캔버스/복잡한 위젯은 여전히 실패할 수 있습니다.
- 교차 출처 iframe/closed shadow DOM/canvas는 DOM 분석이 제한됩니다.
- 사용자 개입 감지: WinUI 3의 visual hosting에서는 `AllowHostInputProcessing`이 적용되지 않을 수 있습니다. content script의 trusted 이벤트 + Agent 행동 원장 + 호스트 입력 훅을 조합하고 Phase 1 스파이크로 검증합니다.
- CDP는 로컬 호스트 내부에서만 사용하고 원격 디버깅 포트를 열지 않습니다.

### 4.2.4 Chrome 확장 프로그램의 실제 한계

- `AreBrowserExtensionsEnabled=true` + `Profile.AddBrowserExtensionAsync(unpackedFolder)`로 **로컬 unpacked 확장**만 로드할 수 있습니다.
- Chrome Web Store/Edge Add-ons에서 직접 설치하는 API는 없습니다. CRX 다운로드/압축 해제/설치 자동화는 스토어 약관 검토가 필요합니다.
- Edge/WebView2가 모든 Chrome extension API를 제공한다고 공개적으로 보장하지 않습니다. UI API(browserAction/action, sidePanel, contextMenus 등)는 호스트가 구현해야 하며, 일부 확장은 동작하지 않습니다.
- 확장 권한/업데이트/비활성화/삭제 UI는 AMARE가 구현해야 합니다.
- 결론: **v1은 내장 기능 우선, 확장은 실험적**입니다. 완전한 Chrome 패리티는 CEF Chrome runtime/Chromium 포크 없이는 약속하지 않습니다.

### 4.2.5 DRM/스트리밍 호환성

- WebView2는 Edge Runtime의 EME 경로를 사용하며 PlayReady/Widevine/H.264/AAC가 가능합니다.
- 그러나 Netflix/Disney+ 등은 **VMP(Verified Media Path)** 또는 서비스별 브라우저 정책을 요구할 수 있습니다. WebView2 호스트 앱이 해당 정책을 통과한다고 가정하지 않습니다.
- HD/4K/HDR, HEVC, 서비스별 DRM은 별도 검증 대상입니다. 실패 시 외부 Edge/앱으로 열기 안내를 제공합니다.
- Phase 1에서 YouTube, Spotify, Netflix, Disney+, Shaka/Widevine 테스트 페이지를 실제 계정/공개 테스트 콘텐츠로 검증합니다.

### 4.2.6 런타임 업데이트와 앱 업데이트의 독립성

- Evergreen WebView2 Runtime은 Microsoft Edge 업데이트 경로로 **AMARE와 독립적으로** 업데이트됩니다.
- AMARE 업데이트는 앱/코어/UI만 교체합니다. 새 SDK API는 최신 Runtime이 배포되어 있다는 전제로 사용하되, 시작 시 Runtime 버전을 확인하고 기능 감지/폴백을 둡니다.
- Fixed Version은 특정 Runtime을 번들(>250MB)하고 자동 보안 업데이트가 없으므로, v1은 Evergreen을 기본으로 하고 치명적 회귀 시에만 검토합니다.
- Runtime 업데이트 후 회귀 테스트(탐색/다운로드/권한/DRM/CDP/확장)를 자동화합니다.

**적합한 경우**

- Windows 우선, 빠른 일상 사용 가능 브라우저, 강한 보안 업데이트, DRM/코덱이 중요할 때
- 소규모 팀이 엔진 유지보수 대신 제품/AI/UX에 집중할 때

**AMARE 판단:** Windows 1차 엔진으로 채택 제안. 단, 엔진 어댑터를 두어 CEF/Chromium 포크로 교체할 여지를 남깁니다.

---

### 4.3 Android WebView

**장점**

- 모든 Android 기기에 사실상 존재하고 Play Store가 엔진을 자동 업데이트합니다.
- Chromium 호환성이 좋고, APK 크기가 작습니다.
- `androidx.webkit`으로 프로필, 쿠키, 권한, 어두운 테마, JavaScript 제어 등이 가능합니다.
- WebView 하나를 띄우는 것은 매우 빠릅니다.

**단점**

- Google 공식 문서는 WebView를 "in-app browser"나 임의의 서드파티 콘텐츠를 여는 브라우저 용도로 권장하지 않습니다.
- 모든 WebView가 하나의 렌더러 프로세스를 공유해 사이트 격리가 불가능합니다. 악성 사이트 하나가 다른 탭의 데이터에 영향을 줄 수 있는 위험이 있습니다.
- 브라우저 수준 보안 UI(자물쇠, 인증서 오류, 사이트 정보)를 만들 API가 부족합니다.
- 확장 프로그램을 지원하지 않습니다.
- Widevine L3만 가능하고, L1/HD/4K 및 서비스 정책은 기기/앱에 따라 제한됩니다.
- 탭/세션/다운로드/권한/파일 업로드 등 브라우저 기능을 호스트가 대부분 직접 구현해야 합니다.
- 다중 프로필은 가능하지만, 프로필마다 렌더러 프로세스가 추가되어 메모리가 증가합니다.

**적합한 경우**

- 프로토타입, 호환성 비교, "가벼운 모바일 브라우저"를 빠르게 검증할 때
- 보안 위험을 명시적으로 수용하고 범위를 축소할 때

**AMARE 판단:** 제품 본체가 아닌 Phase 5 비교/프로토타입 용도로만 사용합니다.

---

### 4.4 GeckoView

**장점**

- Firefox for Android를 구동하는 브라우저 임베딩 엔진입니다. Android WebView보다 브라우저 지향 API가 풍부합니다.
- `GeckoSession` 단위 탭, `contextId` 기반 저장소 분리, `StorageController`로 사이트 데이터 삭제가 가능합니다.
- WebExtensions(Firefox 형식)를 지원해 uBlock Origin 같은 확장을 내장/허용목록으로 사용할 수 있습니다.
- Widevine L3를 사용할 수 있어 일부 DRM 콘텐츠(720p급)가 재생됩니다.
- Mozilla가 정기적으로 보안/호환성 업데이트를 제공합니다.

**단점**

- Chrome/Chromium과 웹 호환성이 완전히 같지 않습니다. 국내 사이트/결제/관리 도구 테스트가 필요합니다.
- 엔진 업데이트를 앱 릴리스로 전달해야 합니다. Android WebView처럼 자동 업데이트되지 않습니다.
- Fission(사이트 격리)은 2026년 중 릴리스 채널에서 비활성화된 이력이 있어, Chromium 수준의 격리를 보장할 수 없습니다. (구현 시점에 상태 재확인 필요)
- Chrome 확장은 지원하지 않습니다. WebExtensions 생태계만 사용할 수 있습니다.
- APK 크기와 메모리가 Android WebView보다 큽니다.
- Agent 제어는 WebExtension content script + `PanZoomController`/`SessionTextInput` 조합에 의존하며, Windows CDP만큼 세밀하지 않을 수 있습니다.

**적합한 경우**

- Android에서 "웹뷰 래퍼"가 아닌 독립 브라우저를 만들고 싶을 때
- Firefox WebExtensions 생태계를 활용하고 싶을 때
- Chrome 확장 패리티를 v1 범위에서 제외할 수 있을 때

**AMARE 판단:** Android 1차 후보. Phase 5 초입에 Android WebView와 비교하는 스파이크를 수행하고 최종 결정합니다.

---

### 4.5 Chromium //chrome 포크

**장점**

- Chrome과 동일한 수준의 웹 호환성, 확장, DRM, 프로세스 모델, UI를 확보할 수 있습니다.
- Windows와 Android에서 동일한 코드베이스를 사용할 수 있습니다.
- 브라우저 기능을 코드 레벨에서 자유롭게 수정할 수 있습니다.

**단점**

- 빌드/유지보수 비용이 매우 큽니다. Brave/Edge/Vivaldi 급 조직이 필요합니다.
- 2주 단위 Chromium 릴리스마다 패치를 rebase해야 합니다.
- 보안 취약점 대응이 늦어지면 사용자에게 직접 위험이 됩니다.
- 코드 서명, 배포, 확장 스토어, DRM/코덱 라이선스 등 운영 부담이 큽니다.
- 소규모 팀이 "매일 쓰는 브라우저"를 만드는 것과 동시에 수행하기 어렵습니다.

**AMARE 판단:** v1–v2에서는 채택하지 않습니다. 장기적으로 Chrome 확장/Android 패리티가 제품의 생존 조건이 되고 조직이 커질 때만 재검토합니다.

---

### 4.6 Servo

**장점**

- Rust 기반, 메모리 안전성, 경량 엔진을 지향합니다.
- 2026년 `servo` crate 0.1.0이 crates.io에 공개되고 LTS가 시작되었습니다.
- 임베딩 API가 발전 중이며, 향후 대안이 될 수 있습니다.

**단점**

- 1.0 이전이며, 임의의 웹 콘텐츠를 렌더링하는 프로덕션 브라우저로 사용하기에는 호환성/보안 위험이 큽니다.
- DRM, 확장, 미디어 코덱, 사이트 호환성 생태계가 부족합니다.
- Servo 공식 문서도 "신뢰된 콘텐츠"와 "임의 콘텐츠"의 위험 프로파일이 다르다고 명시합니다.
- Verso(Tauri/Servo 기반 브라우저)는 2025년에 유지보수가 중단되었습니다.

**AMARE 판단:** 제품 엔진으로 채택하지 않습니다. 장기 관찰 대상으로만 기록합니다.

---

## 5. AI Agent 브라우저 제어 관점

Agent 요구사항의 핵심은 **"사용자가 보고 있는 실제 탭"** 을 조작하는 것입니다. 숨겨진 자동화 브라우저/복제 화면은 허용되지 않습니다.

| 엔진 | 관찰 | 클릭/입력 | 스크롤 | 신뢰도 | 비고 |
|---|---|---|---|---|---|
| WebView2 | CDP DOM/AX/Screenshot | CDP `Input.dispatchMouseEvent` (trusted) | CDP `Input.synthesizeScrollGesture` | 높음 | 실제 WebView2 탭에 적용 |
| CEF | CDP/DOM | CDP `Input.*` | CDP | 높음 | Chrome runtime/Views 제약 |
| Android WebView | JS/Accessibility | JS `.click()` 또는 MotionEvent | JS/View | 중간 | isTrusted=false 이슈 |
| GeckoView | WebExtension content script | PanZoomController.onTouchEvent/onMouseEvent | PanZoomController | 중간 | 합성 입력은 OS 입력과 동일하지 않을 수 있음 |
| Chromium 포크 | CDP | CDP | CDP | 높음 | 유지보수 비용 큼 |

**설계 원칙**

- Agent는 **탭 핸들**을 통해 특정 WebView2/GeckoSession에만 작용합니다.
- 클릭/입력은 가능하면 CDP의 trusted input 경로를 사용합니다. 단순 DOM `.click()`은 사이트가 무시할 수 있으므로 fallback으로만 사용합니다.
- 페이지 콘텐츠는 "관찰 데이터"이며 시스템 지시가 아닙니다. 프롬프트 인젝션 방어는 [보안 설계](SECURITY_DESIGN.md)를 따릅니다.
- 사용자가 조작 중인 탭/입력과 충돌하면 Agent를 일시정지하고 재관찰 후 계속 여부를 묻습니다.
- Agent가 다른 탭을 조작하는 동안에도 사용자는 다른 탭을 자유롭게 사용할 수 있어야 합니다.

**주의:** CDP는 Chromium/Edge의 공식 공개 API가 아니며 하위 호환이 보장되지 않습니다. WebView2의 `CallDevToolsProtocolMethodAsync`는 이를 래핑하지만, CDP 명령의 의미/가용성은 Edge 업데이트에 따라 바뀔 수 있습니다. 회귀 테스트가 필요합니다.

---

## 6. Windows 권장 아키텍처

```
┌─────────────────────────────────────────────┐
│ AMARE.Windows (C# / WinUI 3)                │
│  - 탭/주소창/북마크/설정/사이드 패널 UI      │
│  - WebView2 컨트롤 (탭별)                   │
│  - CDP Agent 실행기                          │
└───────────────┬─────────────────────────────┘
                │ C ABI / UniFFI (async events)
┌───────────────▼─────────────────────────────┐
│ amare-core (Rust)                           │
│  - 도메인/저장소/AI/Agent 정책/Library      │
│  - 엔진 비의존 로직                          │
└─────────────────────────────────────────────┘
```

- 엔진: WebView2 Evergreen.
- UI: WinUI 3, 커스텀 타이틀바/탭 스트립.
- 탭: `CoreWebView2` 1개/탭, 비활성 탭은 `TrySuspendAsync`/메모리 목표 Low.
- 프로필: 기본은 WebView2 Profile API(`ProfileName`), 시크릿/민감 프로필은 별도 UDF/Environment로 프로세스 격리.
- 데이터: 쿠키/캐시는 엔진, 북마크/방문 기록/다운로드 메타데이터/비밀번호는 AMARE SQLite/vault가 소유.
- Agent: C# 어댑터가 CDP를 실행하고, Rust는 계획/정책/원장을 담당.
- DRM/코덱: WebView2 Runtime 경로. 실제 서비스 재생은 스파이크로 검증.
- 확장: v1은 실험적. 내장 기능이 기본.

**탈출구:** `EnginePort` 인터페이스를 두고, WebView2가 차단 요인이 되면 CEF 어댑터를 추가합니다. 코어/데이터/UI는 유지합니다.

---

## 7. Android 권장 아키텍처

```
┌─────────────────────────────────────────────┐
│ AMARE.Android (Kotlin / Compose)            │
│  - 주소창/탭 카드/하단 바/설정               │
│  - GeckoView 호스트                          │
│  - Agent 실행기 (content script/PanZoom)     │
└───────────────┬─────────────────────────────┘
                │ JNI / UniFFI
┌───────────────▼─────────────────────────────┐
│ amare-core (Rust)                           │
│  - Windows와 동일한 도메인/저장소/AI        │
└─────────────────────────────────────────────┘
```

- 엔진: GeckoView(1차 후보). `GeckoRuntime` 1개, `GeckoSession` 1개/탭.
- 프로필: `contextId`로 저장소 분리. 하드 프로세스 격리가 필요하면 별도 프로세스/런타임을 검토하되, 메모리 비용을 측정합니다.
- UI: Compose. 활성 탭만 `GeckoView`에 attach하고, 비활성 세션은 `setActive(false)` 등으로 절전.
- 확장: WebExtensions 허용목록/내장. Chrome 확장 아님.
- DRM: Widevine L3. HD/4K 보장 없음.
- Agent: WebExtension content script로 DOM/요소를 읽고, `PanZoomController`/`SessionTextInput`로 입력을 주입. Windows CDP보다 신뢰도가 낮을 수 있음을 UI에 표시.

### 7.1 GeckoView Agent 기능/API 매트릭스

| 기능 | 1차 방법 | 필요 API | 가능성/한계 |
|---|---|---|---|
| DOM 읽기 | WebExtension content script | `WebExtensionController.installBuiltIn`, `SessionController.setMessageDelegate`, `runtime.sendNativeMessage/connectNative` | 높음. isolated world, 앱과 native messaging |
| 요소 탐색 | content script `querySelectorAll`, `getBoundingClientRect`, `elementsFromPoint` | 위와 동일 | 높음. closed shadow DOM/교차 출처 iframe 제한 |
| 클릭 | content script `element.click()`(synthetic) 또는 좌표 클릭 | content script + `PanZoomController.onMouseEvent`/`onTouchEvent` | 중간. synthetic은 `isTrusted=false`; 좌표 클릭은 더 사용자에 가까움 |
| 텍스트 입력 | content script로 focus 후 IME 경로로 commit | `SessionTextInput.onCreateInputConnection(EditorInfo)` + `InputConnection.commitText`; fallback content script value+input event | 중간. React/IME/한글은 검증 필요 |
| 키보드 이벤트 | 엔진 키 이벤트 | `SessionTextInput.onKeyDown/onKeyUp/onKeyMultiple`; fallback content script KeyboardEvent | 중간. 조합키/IME 제약 |
| 스크롤 | 터치/모션 이벤트 | `PanZoomController.onTouchEvent`/`onMotionEvent`; fallback `window.scrollTo/scrollBy` | 높음. 관성/줌 상호작용 검증 |
| 파일 다운로드 | 외부 응답 처리 | `ContentDelegate.onExternalResponse(WebResponse)` + Android `DownloadManager`/OkHttp | 중간. 쿠키/인증/파일명/진행률 직접 처리 |
| 페이지 이동 | 세션 API | `GeckoSession.loadUri`, `goBack`, `goForward`, `reload`, `stop` | 높음 |
| 사용자 개입 감지 | 호스트 입력 + content script + 원장 대조 | `GeckoView.setOnTouchListener`/`OnKeyListener`, content script trusted events, Agent action ledger | 중간. 합성 입력과 사용자 입력 구분 스파이크 필요 |
| 탭/세션 | `GeckoSession` | `GeckoSession.open`, `setActive`, `setFocused`, `close` | 높음. `contextId`는 저장소 분리 |

> `PanZoomController`는 스크롤/좌표 입력 중심, DOM/요소 조작은 content script 중심으로 사용합니다. content script native messaging에는 `geckoViewAddons`/`nativeMessaging`(필요 시 `nativeMessagingFromContent`) 권한이 필요합니다. `evaluateJS`는 공개 embedder API로 안정적이지 않으므로 WebExtension을 우선합니다.

**대안:** Phase 5 스파이크에서 GeckoView가 기준을 통과하지 못하면 Android WebView 기반 "Mobile Preview"로 범위를 축소하거나 Android 출시를 연기합니다. Chromium 포크는 조직 규모가 커지기 전에는 시작하지 않습니다.

---

## 8. 코드 재사용 전략

- **재사용:** Rust 코어(도메인, 저장소, AI, Agent 정책, 보안, 검색, 백업), 데이터 스키마, 프롬프트/정책, 테스트 코퍼스, 디자인 토큰.
- **플랫폼별:** 엔진 어댑터(WebView2 C# / GeckoView Kotlin), UI, 알림, 생체 인증, 파일 선택기, 업데이트 설치, 기본 브라우저 등록.
- **공유하지 않는 것:** 엔진 객체, UI 프레임워크 타입, 스레드/이벤트 루프. FFI 경계를 넘기지 않습니다.

---

## 9. 검증 계획(스파이크)

| ID | 스파이크 | 성공 기준 | 시점 |
|---|---|---|---|
| S0 | WebView2/WinUI 라이선스/배포 | 재배포/고지/부트스트랩/약관 확인 | Phase 0.5 |
| S1 | WebView2 30탭 메모리/전환 | 16GB에서 30탭(선택 AI 패널 포함 시에도) 사용 가능, 전환 p95 < 300ms | Phase 1 초 |
| S2 | WebView2 기본 API | 프로필/쿠키/캐시 삭제/다운로드/권한/방문 기록 이벤트 검증 | Phase 1 |
| S3 | DRM/VMP | Netflix/Disney+/YouTube/Spotify 재생 결과 기록 | Phase 1 |
| S4 | 업데이트/서명 | Authenticode+Ed25519+체크섬+롤백 검증 | Phase 1 |
| S5 | 프로필/data 격리 | 프로필 간 쿠키/기록/설정 누출 0 | Phase 1 |
| S6 | FFI/비동기/오류 | Rust↔C#/Kotlin 왕복 p99 < 2ms, 취소/에러 전달 정상 | Phase 1 |
| S7 | CDP/입력 감지 | 10개 사이트 클릭/입력/스크롤/탐색, 사용자 개입 감지 | Phase 1(기술), Agent는 Phase 4 |
| S8 | WebView2 확장 기초 | uBlock Origin Lite 등 5개 확장 로드/동작, 권한 UI | Phase 2 |
| S9 | 광고 차단 오버헤드 | 필터 10만 개 기준 페이지 로드 지연 < 5% | Phase 2 |
| S10 | GeckoView 기본 | 20탭, 국내 사이트 20개, 동영상, DRM | Phase 5 |
| S11 | GeckoView Agent | 클릭/입력/스크롤/관찰, 사용자 개입 감지 | Phase 5 |
| S12 | Android WebView 비교 | 보안/호환성/메모리 비교표 | Phase 5 |
| S13 | 추출/인젝션 | 한국어/영어 문서 추출 품질, 인젝션 코퍼스 0건 성공 | Phase 3 |

스파이크 결과는 `docs/spikes/`에 기록하고, 결정이 바뀌면 이 문서를 갱신합니다.

---

## 10. 참고 자료

- WebView2 개요: <https://learn.microsoft.com/microsoft-edge/webview2/>
- WebView2 프로세스 모델: <https://learn.microsoft.com/microsoft-edge/webview2/concepts/process-model>
- WebView2 성능: <https://learn.microsoft.com/microsoft-edge/webview2/concepts/performance>
- WebView2 브라우저 기능 차이: <https://learn.microsoft.com/microsoft-edge/webview2/concepts/browser-features>
- WebView2 확장: <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2profile.addbrowserextensionasync>
- WebView2 프로필/컨트롤러 옵션: <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2profile>, <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2controlleroptions>
- WebView2 쿠키/다운로드/권한: <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2cookiemanager>, <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2downloadoperation>, <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2profile.setpermissionstateasync>
- WebView2 배포/Evergreen vs Fixed: <https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution>, <https://learn.microsoft.com/microsoft-edge/webview2/concepts/evergreen-vs-fixed-version>
- Windows App SDK 라이선스: <https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE>
- WebView2 CDP: <https://learn.microsoft.com/microsoft-edge/webview2/how-to/chromium-devtools-protocol>
- CEF 아키텍처: <https://chromiumembedded.github.io/cef/architecture.html>
- CEF codec 이슈: <https://github.com/chromiumembedded/cef/issues/3559>
- CEF Widevine: <https://github.com/chromiumembedded/cef/issues/3149>
- Chromium 릴리스 주기: <https://chromium.googlesource.com/chromium/src/+/main/docs/process/release_cycle.md>
- Android WebView 보안: <https://web.dev/articles/web-on-android>
- Android WebView 메모리: <https://developer.android.com/topic/performance/memory/guide/webview-memory>
- GeckoView 아키텍처: <https://firefox-source-docs.mozilla.org/mobile/android/geckoview/contributor/geckoview-architecture.html>
- GeckoView WebExtensions: <https://firefox-source-docs.mozilla.org/mobile/android/geckoview/consumer/web-extensions.html>
- GeckoView StorageController: <https://mozilla.github.io/geckoview/javadoc/mozilla-central/org/mozilla/geckoview/StorageController.html>
- GeckoView PanZoomController: <https://mozilla.github.io/geckoview/javadoc/mozilla-central/org/mozilla/geckoview/PanZoomController.html>
- GeckoView SessionTextInput: <https://mozilla.github.io/geckoview/javadoc/mozilla-central/org/mozilla/geckoview/SessionTextInput.html>
- GeckoView ContentDelegate: <https://mozilla.github.io/geckoview/javadoc/mozilla-central/org/mozilla/geckoview/GeckoSession.ContentDelegate.html>
- GeckoView Widevine L3 참고 구현: <https://searchfox.org/mozilla-central/source/mobile/android/geckoview/src/main/java/org/mozilla/gecko/media/GeckoMediaDrmBridge.java>
- Widevine: <https://developers.google.com/widevine/drm/overview>
- Servo: <https://servo.org/blog/2026/04/13/servo-0.1.0-release/>
- Verso 아카이브: <https://github.com/versotile-org/verso>
- Kiwi Browser 아카이브: <https://github.com/kiwibrowser/src.next>
- Firefox Android Fission 상태: <https://bugzilla.mozilla.org/show_bug.cgi?id=2011886>
