# AMARE 기술 스택 제안

- 문서 상태: **제안(Proposed)** — 사용자 승인 대기
- 작성일: 2026-11-03
- 적용 범위: Phase 0 설계, Phase 1–4 Windows, Phase 5 Android
- 관련 문서: [엔진 비교](ENGINE_COMPARISON.md), [아키텍처](ARCHITECTURE.md), [보안 설계](SECURITY_DESIGN.md), [로드맵](ROADMAP.md)

이 문서는 "가장 멋진 기술"이 아니라 **매일 사용할 수 있는 브라우저를 소규모 팀이 장기간 유지보수할 수 있는가**를 기준으로 작성했습니다. 모든 선택에는 근거, 단점, 대안, 변경 가능성을 함께 기록합니다.

---

## 1. 결론 요약

| 계층 | 1차 선택 | 대안 | 변경 가능성 |
|---|---|---|---|
| Windows 엔진 | **WebView2 Evergreen** | CEF(Chrome runtime), Chromium 포크 | 중간 |
| Android 엔진 | **GeckoView 우선 검증** | Android WebView, Chromium 포크 | 높음 (Phase 5 결정 게이트) |
| 공유 핵심 로직 | **Rust** | C++/Kotlin/C# 중복 구현 | 낮음 |
| Windows UI | **C# .NET LTS + WinUI 3** | C++/WinRT, Rust UI, Tauri | 중간 |
| Android UI | **Kotlin + Jetpack Compose** | XML View, Flutter | 낮음 |
| 로컬 저장소 | **SQLite(WAL, FTS5)** + 암호화 | RocksDB, 파일 기반 | 낮음 |
| AI 연결 | **웹 AI(사용자 선택) 기본 + API 선택** | API 단일 경로 | 중간 |
| Agent 제어 | **CDP(Windows) + GeckoView WebExtension/PanZoomController** | 별도 자동화 브라우저 | 중간 |
| 배포 | **GitHub Releases + 서명** | Microsoft Store, Play Store | 중간 |

**핵심 판단:** AMARE는 "웹뷰를 감싼 앱"이 아니라, 자체 탭/워크스페이스/프로필/데이터/보안/AI/Agent 계층을 가진 브라우저입니다. 브라우저 엔진은 교체 가능한 구성요소로 취급하고, 제품의 핵심 자산은 Rust로 구현한 도메인 로직과 로컬 데이터에 둡니다.

---

## 2. 왜 이 조합인가

### 2.1 Windows: WebView2를 1차 엔진으로 선택

**근거**

- Windows 11에는 WebView2 Evergreen Runtime이 기본 탑재되어 있어 설치 파일을 작게 유지할 수 있습니다. Windows 10/변형 환경을 위해 Evergreen Bootstrapper/Standalone Installer를 설치 시 안내·배포합니다.
- Evergreen은 보안 업데이트가 자동입니다. Fixed Version 배포는 Edge 업데이트 회귀를 피할 수 있지만 보안 패치를 직접 번들/배포해야 하므로, v1은 Evergreen을 기본으로 하고 치명적 회귀 시에만 Fixed Version/특정 채널을 검토합니다.
- Chromium/Edge와 동일한 멀티프로세스 구조, 사이트 격리, GPU 가속, 최신 웹 호환성을 사용합니다.
- Microsoft가 보안 업데이트를 Edge Runtime을 통해 제공하므로, 소규모 팀이 Chromium 보안 패치를 직접 따라잡을 필요가 없습니다.
- `CoreWebView2` API로 탭별 WebView, 프로필, 쿠키, 다운로드, 권한, 트래킹 방지, 테마, 확장(제한적), CDP를 제어할 수 있습니다.
- `CallDevToolsProtocolMethodAsync`로 **사용자가 보고 있는 실제 WebView2 탭**을 CDP로 조작할 수 있습니다. 이는 Agent 요구사항의 핵심입니다.

**단점 / 한계**

- 엔진 내부를 직접 수정할 수 없습니다. Chromium 코드 레벨의 기능(예: 커스텀 네트워크 스택, 깊은 확장 API)은 불가능합니다.
- 확장 프로그램은 "unpacked extension" 로드 API 수준이며, Chrome Web Store 통합이나 Edge 확장 UI를 그대로 제공하지 않습니다. 호스트가 UI와 권한 관리를 구현해야 하고, 일부 확장은 동작하지 않을 수 있습니다.
- Edge Runtime 업데이트 주기에 종속됩니다. 마이크로소프트가 동작을 바꾸면 회귀 테스트가 필요합니다.
- 다중 WebView2는 탭마다 렌더러 프로세스를 만들 수 있어 메모리 사용량이 큽니다. 절전/폐기 정책이 필수입니다.
- 일부 브라우저 기능(내장 번역, 읽기 모드, 강제 다크, PIP 등)은 기본 제공되지 않으므로 직접 구현하거나 엔진 플래그에 의존해야 합니다.
- 런타임은 오픈소스가 아니며, 재배포/사용 조건은 Microsoft 라이선스를 따라야 합니다. "브라우저 제품"으로 사용하는 것에 대한 법무 검토가 필요합니다.

**대안을 지금 선택하지 않는 이유**

- **CEF:** 엔진을 번들링하고 2주 단위 Chromium 릴리스에 맞춰 보안 패치를 배포해야 합니다. 소규모 팀에게 현실적이지 않습니다. 또한 공식 CEF 빌드는 proprietary codecs(H.264/AAC)가 꺼져 있고, Widevine은 별도 라이선스/CDM 통합이 필요합니다.
- **Chromium //chrome 포크:** Edge/Brave급 조직이 필요한 작업입니다. AMARE의 1차 목표가 아닙니다.
- **Servo:** 2026년 기준으로 `servo` crate가 crates.io에 공개되고 LTS가 시작되었지만, 임의의 웹 콘텐츠를 렌더링하는 프로덕션 브라우저 엔진으로 쓰기에는 보안/호환성/생태계 위험이 큽니다. Servo 공식 문서도 "신뢰된 콘텐츠 렌더링"과 "임의 콘텐츠를 렌더링하는 브라우저"의 위험 프로파일이 다르다고 명시합니다.

**변경 조건**

다음 중 하나가 **측정 가능한 차단 요인**이 되면 CEF 또는 Chromium 포크 마이그레이션을 별도 프로젝트로 검토합니다.

- 필수 확장 프로그램의 80% 이상이 WebView2에서 동작하지 않음
- 광고 차단/요청 가로채기 오버헤드가 목표치를 초과함
- DRM/코덱/미디어 요구사항이 WebView2로 충족되지 않음
- 엔진 내부 정책(프록시, DNS, 프로세스 모델) 제어가 제품 요구사항상 필수임

### 2.1.1 WebView2 기능 소유권(요약)

| 범주 | WebView2가 제공 | AMARE가 구현 |
|---|---|---|
| 렌더링/탐색 | HTML/CSS/JS, 뒤로/앞으로, 새로고침, 현재 탭 탐색 히스토리 | 탭 스트립, 주소창, 검색, 세션 복원 |
| 프로필/세션 | Profile API, InPrivate, 쿠키 저장소, 캐시/데이터 삭제 | 프로필 UI, Workspace, 탭 그룹, 프로필 간 정책 |
| 쿠키/캐시 | `CookieManager`, `ClearBrowsingDataAsync` | 쿠키/캐시 관리 UI, 사이트별 삭제 UX |
| 다운로드 | `DownloadStarting`, pause/resume/cancel, 기본 폴더 | 다운로드 관리자 UI/DB, 검색/재시도 정책 |
| 방문 기록 | 현재 탭의 NavigationHistory만 제공(영구 브라우저 기록 저장소 없음) | 방문 기록 DB/검색/가져오기/내보내기 |
| 권한 | `PermissionRequested`, `Profile.SetPermissionStateAsync` | 사이트별 권한 UI/정책/기본값 |
| 보안 | HTTPS/인증서 오류, 트래킹 방지(기본/균형/엄격), SmartScreen | 보안 설정 UI, 경고/예외 정책, 광고 차단 |
| 자동화 | CDP API(`CallDevToolsProtocolMethodAsync`) | Agent 계획/정책/승인/원장/관찰 |
| 확장 | unpacked extension 로드 API | 확장 관리 UI/권한/업데이트 정책(제한적) |
| DRM/미디어 | Edge Runtime의 EME/PlayReady/Widevine/코덱 경로 | 서비스별 검증, 실패 시 외부 앱 안내 |
| 업데이트 | Evergreen Runtime 자동 업데이트 | 앱 업데이트/서명/롤백/채널 |

핵심: **WebView2는 엔진이고, 브라우저 제품의 데이터/정책/UI는 AMARE가 소유**합니다. 상세 API와 한계는 [엔진 비교](ENGINE_COMPARISON.md) 4.2/5장을 참고하세요.

### 2.2 Android: GeckoView 우선, Phase 5에서 결정

**근거**

- GeckoView는 Firefox for Android를 구동하는 **브라우저 임베딩용 엔진**입니다. Android WebView와 달리 "브라우저를 만들기 위한" API와 멀티프로세스 콘텐츠 구조를 제공합니다.
- `GeckoSession`을 탭 단위로 관리할 수 있고, `contextId`로 쿠키/로컬스토리지 등 저장소를 프로필 단위로 분리할 수 있습니다.
- WebExtensions(Firefox 형식)를 지원하므로 uBlock Origin 같은 확장을 내장/허용목록 방식으로 사용할 수 있습니다. Chrome 확장은 아닙니다.
- Agent 제어는 WebExtension content script(DOM/요소) + `PanZoomController`(좌표/스크롤) + `SessionTextInput`(키/IME) 조합으로 설계합니다. Chrome CDP만큼의 신뢰도는 보장하지 않습니다.
- Widevine L3를 사용할 수 있어 일부 DRM 콘텐츠(예: 720p급) 재생이 가능하지만, L1/HD/4K와 서비스 정책은 보장되지 않습니다.

**단점 / 한계**

- Chrome/Chromium과 웹 호환성이 100% 동일하지 않습니다. 국내 사이트를 포함한 호환성 테스트가 필요합니다.
- GeckoView 엔진 보안 업데이트는 앱 업데이트로 전달해야 합니다. Android WebView처럼 Play Store가 자동 업데이트해 주지 않습니다.
- 2026년 기준 Firefox for Android의 Fission(사이트 격리)은 릴리스/베타 채널에서 일시 비활성화된 이력이 있습니다. GeckoView는 멀티프로세스이지만 Chromium 수준의 사이트 격리를 보장하지 않습니다.
- Chrome 확장 프로그램 호환은 사실상 불가능합니다. Chrome 확장 패리티는 Chromium 포크 없이는 달성할 수 없습니다.
- APK 크기와 메모리 사용량이 Android WebView보다 큽니다.

**Android WebView를 1차 후보로 두지 않는 이유**

- Google 공식 문서는 WebView를 "in-app browser"나 임의의 서드파티 콘텐츠를 여는 브라우저 용도로 권장하지 않습니다. 일반적으로 모든 WebView가 하나의 렌더러 프로세스를 공유해 사이트 격리가 불가능하고, 브라우저 수준 보안 UI(자물쇠, 인증서 상태 등)를 만들 API도 부족합니다.
- 다만 "프로토타입/호환성 비교"에는 유용하므로 Phase 5 스파이크에서 비교 대상으로만 사용합니다.

**결정 게이트 (Phase 5 초입)**

Android 엔진은 다음 기준으로 최종 결정합니다.

1. 상위 100개 사이트 + 국내 주요 사이트 호환성
2. 20탭 메모리/전환 성능
3. DRM 재생 범위
4. WebExtensions 필요성
5. 보안 격리 수준
6. 엔진 업데이트 운영 부담
7. APK 크기

GeckoView가 기준을 통과하지 못하면 (a) Android WebView 기반으로 범위를 축소한 "AMARE Mobile Preview"를 먼저 출시하거나, (b) Android 출시를 연기하고 Windows에 집중합니다. **Chrome 확장 패리티를 위해 Chromium 포크를 즉시 시작하는 선택지는 권장하지 않습니다.**

### 2.3 Rust: 핵심 로직에 집중, 네이티브는 단순하게

Rust를 "모든 공통 기능"에 강제하지 않습니다. **데이터/정책/암호/검색처럼 플랫폼 간 공유 가치가 큰 로직은 Rust**, **UI/엔진/OS 통합처럼 네이티브가 더 단순하고 안정적인 로직은 C#/Kotlin**으로 둡니다.

| 영역 | 구현 | 이유 |
|---|---|---|
| 도메인 모델/유스케이스(프로필, 탭, 워크스페이스, 북마크, 기록, 세션) | Rust | Windows/Android 공유, 테스트 용이 |
| SQLite 마이그레이션/FTS5/blob store/백업 스냅샷 | Rust | 데이터 무결성/공유 |
| AI 오케스트레이션/컨텍스트/인용/작업 기억 | Rust | 제공자/플랫폼 독립 |
| Agent 정책/승인/실행 원장/프롬프트 인젝션 규칙 | Rust | 보안 정책의 단일 신뢰 지점 |
| 암호화/비밀번호 vault/키 계층/업데이트·백업 검증 | Rust | 민감 로직, 공유 |
| 본문 추출/정규화/검색 인덱싱 | Rust | 공유, 성능 |
| Windows UI/창/탭 스트립/IME/드래그앤드롭/접근성 | C# + WinUI 3 | 네이티브 품질/생산성 |
| Android UI/제스처/PiP/생체 인증/인텐트 | Kotlin + Compose | 플랫폼 통합 |
| 엔진 어댑터(WebView2/GeckoView) | C#/Kotlin | 엔진 API가 1급 시민 |
| 다운로드 UI/권한 프롬프트/설정 화면 | C#/Kotlin | UI/플랫폼 결합 |
| 탭 절전/엔진 프로세스 이벤트/입력 주입 | C#/Kotlin | 엔진/OS API 직접 사용 |
| 설치/업데이트 실행/기본 브라우저 등록 | C#/Kotlin + Rust 검증 | OS 통합 + 서명 검증 분리 |

**Rust를 사용하지 않을 영역(명시)**

- Windows/Android UI와 윈도우/탭 시각 상태
- 엔진 어댑터와 엔진 이벤트 처리
- 플랫폼 서비스(알림, 파일 선택기, 생체 인증, 기본 브라우저 등록, 설치)
- 초기 프로토타입의 UI(Rust UI 프레임워크는 브라우저급 접근성/IME/다중 창 비용이 큼)

**FFI/비동기/오류 구조(요약)**

- `amare-ffi`가 **C ABI(Windows C#)** 와 **UniFFI(Kotlin)** 를 제공합니다. 계약은 `engine/contracts`의 언어 중립 스키마로 관리합니다.
- 호출은 **command + request_id + event stream** 구조입니다. UI는 `command(request)`를 보내고, 코어는 `event(request_id, progress/result/error)`를 비동기로 보냅니다.
- 장시간 작업(AI, 인덱싱, 추출, 다운로드 메타데이터)은 **취소 토큰/타임아웃/백프레셔**를 가집니다.
- 오류는 `AmareError { code, message, retryable, details, trace_id }`로 전달하고, 네이티브 예외/Result로 매핑합니다. Rust panic은 `catch_unwind`로 차단합니다.
- 큰 payload(스냅샷/추출 본문/이미지)는 JSON 문자열보다 **blob handle/공유 메모리**를 우선하고, FFI를 채팅처럼 자주 호출하지 않습니다.
- C#은 Phase 1에서 `uniffi-bindgen-cs`/ClangSharp/수동 P/Invoke를 비교하고, Kotlin은 UniFFI 공식 바인딩을 우선합니다.
- ABI/계약 버전을 명시하고, 엔진/앱 업데이트 후 호환성 테스트를 수행합니다.

### 2.4 UI 프레임워크

**Windows: WinUI 3 + Windows App SDK**

- Fluent 디자인, Mica/아크릴, 네이티브 탭/창/컨텍스트 메뉴, 접근성, IME, DPI, 멀티 윈도우를 제공합니다.
- WebView2 컨트롤과 공식 통합됩니다.
- C#/.NET LTS는 생산성이 높고, WinRT API(CsWinRT)로 알림, 파일 선택기, 기본 브라우저 등록을 사용할 수 있습니다. Windows Hello는 패키지/비패키지 앱 지원 범위를 Phase 2에서 검증합니다.
- 단점: WinUI 3는 여전히 일부 데스크톱 시나리오에서 성숙도 이슈가 있고, 커스텀 타이틀바/탭 드래그 같은 브라우저 UI는 직접 구현해야 합니다.

**대안: Tauri v2**

- Rust 백엔드 + 웹 UI로 빠르게 만들 수 있지만, 브라우저 자체가 WebView2를 호스팅해야 하는 구조와 충돌합니다. 다중 WebView, 탭 분리, IME, 컨텍스트 메뉴, CDP 제어를 직접 다루기 어렵고, Android에서는 GeckoView가 아니라 Android WebView를 사용합니다. **제품 본체에는 권장하지 않습니다.** 설정/마케팅 페이지 같은 주변 도구에는 사용할 수 있습니다.

**대안: Rust UI(Slint/iced/egui)**

- 단일 언어의 장점은 있지만, 브라우저급 접근성/IME/다중 창/플랫폼 통합 품질을 확보하는 비용이 큽니다. Phase 1 이후 재평가 가능합니다.

**Android: Kotlin + Jetpack Compose**

- 모바일 UI, 제스처, 애니메이션, 접근성, 다크 테마, 생체 인증, PiP를 자연스럽게 구현합니다.
- `AndroidView`로 GeckoView를 호스팅하고, 탭 전환/주소창/카드형 탭 화면은 Compose로 작성합니다.

### 2.5 데이터 저장

- **SQLite (WAL)** 를 기본으로 사용합니다. 단일 파일, 트랜잭션, 안정성, 복구 도구가 검증되어 있습니다.
- **FTS5**로 북마크/방문 기록/Library/대화 검색을 구현합니다.
- **벡터 검색**은 Phase 3에서 `sqlite-vec` 또는 별도 인덱스를 벤치마크 후 선택합니다. 초기에는 FTS5 + 키워드 + 메타데이터 필터로 시작하고, 임베딩은 옵션으로 추가합니다.
- **암호화**: 비밀번호/토큰/민감 설정은 앱 레벨 AEAD(XChaCha20-Poly1305) + OS 키체인으로 보호합니다. 데이터베이스 전체 암호화가 필요하면 SQLCipher를 검토하되, 마이그레이션/성능/라이선스를 비교합니다.
- **blob store**: Library의 PDF/이미지/스냅샷은 콘텐츠 해시 기반으로 저장하고, DB에는 메타데이터만 둡니다. 중복 제거와 백업이 쉬워집니다.

### 2.6 AI

AI는 "API 전용"이 아닙니다. 두 경로를 모두 지원합니다.

1. **일반 AI 대화(기본): 사용자가 선택한 공식 AI 웹사이트**
   - ChatGPT, Gemini, Claude 등 공식 사이트를 AI 패널/탭에서 사용자가 직접 로그인해 사용합니다.
   - AMARE는 사용자의 쿠키/세션을 추출하거나 API처럼 재사용하지 않습니다.
   - 웹 AI UI 자동화는 서비스 UI 변경, 약관, 안티봇에 취약하므로 안정성을 보장하지 않습니다. 기본은 "AI 사이트 열기 + 프롬프트 복사/붙여넣기" 보조입니다.
   - 자동 입력/전송은 사용자가 명시적으로 켠 경우에만 시도하고, 실패를 정상 상태로 간주합니다.

2. **API(선택): 사용자가 제공자/모델/키를 설정한 경우**
   - OpenAI 호환 엔드포인트, Anthropic, Google Gemini, 로컬 Ollama/LM Studio 등을 추상화합니다.
   - 기본 모델 하나를 일반 대화와 Agent가 공유하고, 작업별 오버라이드를 허용합니다.
   - 키는 OS 키체인에 저장하고 웹 콘텐츠/렌더러에 노출하지 않습니다.
   - Agent의 도구 호출/브라우저 제어는 안정성을 위해 API 경로를 우선 사용합니다. 웹 AI가 Agent 기능을 지원하지 못하면 API 설정을 안내합니다.

- **AMARE 서버 없음**: API 호출은 클라이언트에서 직접 이루어집니다.
- **임베딩**: 한국어/영어 지원이 좋은 다국어 모델을 우선 검토합니다. 초기에는 원격 임베딩을 옵션으로 두고, 로컬 ONNX 모델은 Phase 3 후반에 벤치마크합니다.
- **데이터 최소화**: 전체 페이지를 무조건 전송하지 않습니다. 웹 AI는 사용자가 보는 화면/붙여넣은 내용 범위에서, API는 선택 영역/추출 본문/질문 관련 청크만 전송하고, 전송 전 사용자에게 컨텍스트 미리보기를 제공합니다.

### 2.7 검색 및 리서치

- 브라우저 검색엔진은 URL 템플릿 기반으로 관리합니다(Google, Bing, DuckDuckGo, Naver, Daum, YouTube + 사용자 정의).
- 리서치는 검색 API(예: Brave Search, Tavily, Serper 등 사용자 키) 또는 사용자가 선택한 검색엔진의 결과 페이지를 사용자가 볼 수 있는 탭에서 읽어 링크를 추출하는 방식으로 시작합니다.
- 검색 결과 HTML 스크래핑은 제공자 약관과 안티봇 정책을 확인해야 하며, 안정성을 보장하지 않습니다. 공식 API를 우선합니다.
- JS 렌더링이 필요한 페이지는 사용자가 볼 수 있는 탭(가시/백그라운드)에서 로드한 뒤 DOM을 추출합니다. **현재 탭을 복제한 숨겨진 브라우저를 만들지 않습니다.**

### 2.8 미디어/DRM

- Windows는 WebView2 Runtime이 Edge의 PlayReady/Widevine/코덱 경로를 사용합니다. Netflix/Disney+ 재생은 **Phase 1 스파이크에서 실제 검증**합니다.
- Android GeckoView는 Widevine L3 경로를 사용합니다. L3 범위(예: 720p급)로 제한될 수 있으며, HD/4K/L1과 서비스별 정책은 보장하지 않습니다.
- CEF/Chromium 포크로 갈 경우 proprietary codecs와 Widevine 라이선스/MPEG-LA 검토가 별도로 필요합니다.
- DRM이 동작하지 않는 서비스는 외부 앱/브라우저로 열기 안내를 제공합니다. "브라우저 엔진이 지원하므로 동작한다"고 가정하지 않습니다.

### 2.9 확장 프로그램

- **Windows v1**: 내장 기능 우선. WebView2의 unpacked extension API(`AreBrowserExtensionsEnabled=true` 필요)를 "실험적"으로 노출하고, 호스트가 확장 관리 UI/권한/업데이트를 구현합니다. Chrome Web Store 설치는 공식 지원이 아니며, 약관 검토 없이 구현하지 않습니다.
- **Android v1**: GeckoView WebExtensions(AMO 형식) 허용목록/내장 확장 중심. Chrome 확장은 지원하지 않습니다.
- **장기**: Chrome 확장 패리티는 CEF Chrome runtime 또는 Chromium 포크가 필요합니다. 별도 의사결정 없이는 약속하지 않습니다.

### 2.10 업데이트/배포

- 공식 배포는 GitHub Releases를 사용합니다.
- Windows: Win32 설치 프로그램(WiX/Burn) + Authenticode 서명. MSIX는 기본 브라우저 등록/셸 통합 제약이 있어 v1에서는 Win32 설치를 우선 검토합니다.
- **WebView2 Runtime은 AMARE 업데이트와 독립적으로 Microsoft Edge 경로를 통해 Evergreen 업데이트됩니다.** AMARE 업데이트는 앱/코어/UI만 교체하며, WebView2 API는 SDK/런타임 버전을 확인하고 기능 감지 후 사용합니다. 런타임 회귀가 치명적이면 Fixed Version(>250MB, 수동 보안 업데이트) 또는 특정 채널을 검토합니다.
- Android: 서명된 APK/AAB. GitHub에서 직접 설치하는 경우 `REQUEST_INSTALL_PACKAGES`와 사용자 확인이 필요합니다. Play Store 배포는 별도 정책 검토 후 결정합니다. GeckoView 엔진 업데이트는 앱 릴리스에 포함됩니다.
- 업데이트 매니페스트는 Ed25519(또는 minisign)로 서명하고, SHA-256 체크섬과 버전/롤백 방지를 검증합니다. Windows는 Authenticode, Android는 APK 서명/패키지명/버전 코드를 추가 검증합니다.
- 실패 시 현재 버전으로 계속 실행하고, 서명/체크섬 검증 실패 시 설치하지 않습니다.

### 2.11 테스트/CI

- GitHub Actions: Rust(unit/integration), .NET(WinUI build + UI smoke), Android(Gradle + instrumentation), 보안/성능 벤치마크.
- 실제 사이트 호환성 테스트는 자동화(상위 사이트 스모크) + 수동 매트릭스(국내 사이트, 로그인, 결제, DRM, 동영상)로 나눕니다.
- AI 품질/프롬프트 인젝션은 고정 코퍼스 기반 회귀 테스트를 둡니다.
- 릴리스 산출물: 설치 파일, APK, 체크섬, 서명, 변경 로그.

---

## 3. 언어/런타임 버전 정책

| 항목 | 정책 |
|---|---|
| Rust | stable, edition 2024, `rust-toolchain.toml`으로 고정. MSRV는 코어와 FFI에서 명시. |
| .NET | 운영 목표는 .NET LTS(현재 .NET 10 계열). Phase 0.5 스캐폴드는 환경상 .NET 8로 검증했으며, Windows CI에서 .NET 10/WinUI 3 호환성 확인 후 올린다. |
| Kotlin | 최신 stable, Compose BOM 고정. |
| Android | `minSdk 29`(Android 10) 우선, targetSdk는 최신 Play 요구사항(2026년 기준 API 36) 준수. |
| Node/TypeScript | 주입 스크립트/내부 페이지에만 사용. 제품 본체 런타임에 Node를 넣지 않음. |
| 데이터베이스 | SQLite 3.x, FTS5. 마이그레이션은 코드로 관리. |

버전은 "최신"을 자동 추종하지 않고, 릴리스마다 고정/검증합니다. 보안 패치는 예외적으로 빠르게 반영합니다.

---

## 4. 라이선스/법무 체크리스트

| 항목 | 확인 사항 | 담당 시점 |
|---|---|---|
| WebView2 Runtime | Microsoft 라이선스, 재배포/사용 범위, "full browser" 사용 가능 여부 법무 검토 | Phase 0–1 |
| WinUI 3 / Windows App SDK | Windows App SDK 리포지토리는 MIT, NuGet/런타임/재배포 조건은 Microsoft 약관·고지 확인 | Phase 0–1 |
| 엔진 런타임 텔레메트리 | WebView2/Edge/GeckoView 자체 데이터 처리, SmartScreen, 진단 | Phase 0–1 |
| GeckoView | MPL 2.0, 수정/배포 시 소스 공개 범위 | Phase 5 |
| Chromium/CEF(향후) | BSD + 제3자 라이선스, proprietary codecs, Widevine | 마이그레이션 결정 시 |
| Widevine | Google 라이선스, CDM 배포, L1/L3, 서비스 정책 | DRM 스파이크 |
| H.264/AAC/HEVC/AV1 | MPEG-LA/특허 풀, 배포 권한, 하드웨어/소프트웨어 디코딩 | DRM/미디어 스파이크 |
| 필터 목록 | EasyList/EasyPrivacy 등 라이선스와 저작자 표시 | 광고 차단 구현 시 |
| 폰트/아이콘 | OFL/Apache/MIT 등 | 디자인 시스템 |
| Readability/추출 라이브러리 | Apache 2.0 등 | Phase 3 |
| GitHub Releases | 서명 키 관리, 체크섬, 릴리스 노트 | Phase 1 |
| AI 제공자 | 사용자 약관, 데이터 처리, 한국어/지역 제한 | Phase 3 |
| 검색 API | 제공자 약관, 쿼터, 스크래핑 금지 여부 | Phase 3 |
| Chrome Web Store | 확장 배포/설치 약관 | 확장 기능 결정 시 |

> 이 표는 법률 자문이 아닙니다. 실제 배포 전에 각 라이선스 원문과 법무 검토가 필요합니다.

---

## 5. 채택하지 않은 대안과 이유

| 대안 | 채택하지 않는 이유 |
|---|---|
| Electron | Chromium을 통째로 번들링해 설치/메모리가 크고, Android를 지원하지 않습니다. 확장/DRM도 제약이 있습니다. |
| Tauri (제품 본체) | OS WebView를 사용하므로 브라우저 엔진/탭/확장/DRM 제어가 제한적이고, Android에서는 GeckoView를 쓸 수 없습니다. |
| Servo/Verso | Servo는 임의 콘텐츠 브라우저로는 아직 위험하고, Verso는 유지보수가 중단되었습니다. 장기 관찰 대상입니다. |
| Android WebView (제품 본체) | Google이 브라우저 용도로 권장하지 않으며, 사이트 격리/보안 UI/확장에 한계가 있습니다. |
| Chromium 전체 포크 | 보안 패치/빌드/배포 운영 비용이 소규모 팀 범위를 넘습니다. |
| 웹 AI UI 자동화를 핵심 경로로 사용 | 서비스 UI 변경/안티봇/약관에 취약합니다. 일반 대화는 웹 AI를 기본으로 하되, Agent/도구 호출은 API 경로를 사용합니다. |

---

## 6. 공개 결정 사항과 남은 질문

### 승인이 필요한 결정

1. Windows 1차 엔진으로 WebView2를 사용하는가?
2. Android는 GeckoView 우선 검증으로 시작하는가?
3. Rust 코어 + C#/Kotlin UI 하이브리드 구조를 승인하는가?
4. v1에서 Chrome 확장 패리티를 약속하지 않는가?
5. v1에서 Netflix/Disney+ 4K를 보장하지 않는가?
6. Android WebView를 제품 본체가 아닌 비교/프로토타입 용도로만 사용하는가?
7. 일반 AI 대화는 웹 AI 기본, API는 선택, Agent는 API/도구 호출로 하는가?
8. MVP Core는 브라우저 기본 기능만 포함하고 AI 패널은 선택으로 두는가?

### Phase 1에서 검증할 질문

- WebView2 30탭에서 메모리/전환 성능이 목표를 만족하는가?
- WebView2 CDP로 실제 탭 클릭/입력/스크롤이 안정적으로 동작하는가?
- WebView2에서 Netflix/Disney+/YouTube DRM이 실제로 재생되는가?
- (후속) WebView2 확장 API로 uBlock Origin Lite 등 필수 확장이 동작하는가?
- (후속) 광고 차단 필터 엔진의 오버헤드가 허용 가능한가?
- Rust ↔ C# FFI의 지연/안정성이 UI에 영향을 주지 않는가?

### Phase 5에서 검증할 질문

- GeckoView의 국내 사이트 호환성과 20탭 메모리가 충분한가?
- GeckoView에서 Agent 입력(클릭/스크롤)을 신뢰 가능한 방식으로 보낼 수 있는가?
- DRM/미디어 요구사항을 어디까지 만족하는가?
- WebExtensions 허용목록이 제품 가치를 주는가?
- Android WebView 대비 보안/호환성/운영 비용의 균형이 맞는가?

---

## 7. 추가 검증 계획

| 항목 | 검증 내용 | 시점/산출물 |
|---|---|---|
| WebView2/WinUI 라이선스 | Runtime 재배포, 고지, "full browser" 사용, NuGet/런타임 조건 | Phase 0.5 법무 메모 |
| DRM/VMP | Netflix/Disney+/YouTube/Spotify, Shaka/Widevine, VMP 여부 | Phase 1 S3 매트릭스 |
| 업데이트 서명 | Authenticode+Ed25519/minisign+SHA-256, 롤백, 설치 실패 | Phase 1 S4 리허설 |
| 프로필/data 격리 | 프로필 간 쿠키/기록/설정/다운로드 누출 | Phase 1 S5 테스트 |
| 메모리 | 1/5/10/20/30탭, 절전/폐기, AI 패널, i5-7500/16GB | Phase 1 S1 실측 |
| WebView2 API | 프로필/쿠키/캐시/다운로드/권한/기록 이벤트 | Phase 1 S2 |
| CDP/입력 감지 | 10개 사이트 클릭/입력/스크롤, 사용자 개입, WinUI visual hosting | Phase 1 S7 |
| Android Agent | GeckoView content script/PanZoom/SessionTextInput | Phase 5 S11 |
| 테스트/실패 대안 | 기능별 테스트와 대안 표 | Phase 0.5 확정 |

메모리 예상치는 공개 자료 기반이며 실측이 아닙니다. 자세한 표는 [Phase 0 수정 보고서](PHASE0_REPORT.md) 8.1/8.4를 참고하세요.

## 8. 참고 자료

아래 자료는 2026-11-03 기준으로 확인했습니다. 버전/정책은 변경될 수 있으므로 구현 시점에 재확인이 필요합니다.

- Microsoft Edge WebView2 개요: <https://learn.microsoft.com/microsoft-edge/webview2/>
- WebView2 프로세스 모델: <https://learn.microsoft.com/microsoft-edge/webview2/concepts/process-model>
- WebView2 성능/메모리: <https://learn.microsoft.com/microsoft-edge/webview2/concepts/performance>
- WebView2 브라우저 기능 차이: <https://learn.microsoft.com/microsoft-edge/webview2/concepts/browser-features>
- WebView2 확장 API: <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2profile.addbrowserextensionasync>
- WebView2 프로필/컨트롤러 옵션: <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2profile>, <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2controlleroptions>
- WebView2 쿠키/다운로드/권한: <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2cookiemanager>, <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2downloadoperation>, <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2profile.setpermissionstateasync>
- WebView2 배포/Evergreen vs Fixed: <https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution>, <https://learn.microsoft.com/microsoft-edge/webview2/concepts/evergreen-vs-fixed-version>
- Windows App SDK 라이선스: <https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE>
- WebView2 CDP: <https://learn.microsoft.com/microsoft-edge/webview2/how-to/chromium-devtools-protocol>
- WebView2 트래킹 방지: <https://learn.microsoft.com/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2profile>
- WebView2 라이선스: <https://learn.microsoft.com/microsoft-edge/webview2/>
- CEF 아키텍처(Chrome runtime/Alloy): <https://chromiumembedded.github.io/cef/architecture.html>
- CEF 확장/코덱/Widevine 이슈: <https://github.com/chromiumembedded/cef/issues/3559>, <https://github.com/chromiumembedded/cef/issues/3149>
- Chromium 릴리스 주기(2026년 2주 주기): <https://chromium.googlesource.com/chromium/src/+/main/docs/process/release_cycle.md>
- Servo 0.1.0 crates.io: <https://servo.org/blog/2026/04/13/servo-0.1.0-release/>
- Verso 아카이브: <https://github.com/versotile-org/verso>
- GeckoView 아키텍처: <https://firefox-source-docs.mozilla.org/mobile/android/geckoview/contributor/geckoview-architecture.html>
- GeckoView WebExtensions: <https://firefox-source-docs.mozilla.org/mobile/android/geckoview/consumer/web-extensions.html>
- GeckoView StorageController/contextId: <https://mozilla.github.io/geckoview/javadoc/mozilla-central/org/mozilla/geckoview/StorageController.html>
- GeckoView PanZoomController(입력 주입): <https://mozilla.github.io/geckoview/javadoc/mozilla-central/org/mozilla/geckoview/PanZoomController.html>
- GeckoView SessionTextInput(키/IME): <https://mozilla.github.io/geckoview/javadoc/mozilla-central/org/mozilla/geckoview/SessionTextInput.html>
- GeckoView ContentDelegate(다운로드/크래시): <https://mozilla.github.io/geckoview/javadoc/mozilla-central/org/mozilla/geckoview/GeckoSession.ContentDelegate.html>
- GeckoView Widevine L3 설정(참고 구현): <https://searchfox.org/mozilla-central/source/mobile/android/geckoview/src/main/java/org/mozilla/gecko/media/GeckoMediaDrmBridge.java>
- Android WebView 보안 권고: <https://web.dev/articles/web-on-android>
- Android WebView 메모리/프로세스: <https://developer.android.com/topic/performance/memory/guide/webview-memory>
- AndroidX WebKit Profile: <https://developer.android.com/reference/androidx/webkit/Profile>
- Widevine 개요: <https://developers.google.com/widevine/drm/overview>
- Kiwi Browser 아카이브(Android Chrome 확장의 현실): <https://github.com/kiwibrowser/src.next>
- Firefox for Android Fission 상태(2011886): <https://bugzilla.mozilla.org/show_bug.cgi?id=2011886>
