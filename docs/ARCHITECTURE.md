# AMARE 아키텍처

- 문서 상태: **제안(Proposed)**
- 작성일: 2026-11-03
- 전제: [기술 스택](TECH_STACK.md)의 Windows=WebView2, Android=GeckoView 우선 검증 제안을 기준으로 한다.
- 관련 문서: [엔진 비교](ENGINE_COMPARISON.md), [보안 설계](SECURITY_DESIGN.md)

---

## 1. 목표와 비목표

### 목표

1. Windows 11에서 매일 사용할 수 있는 성능/안정성/호환성.
2. AI와 Agent가 브라우저의 기본 동작을 방해하지 않는 구조.
3. 탭/워크스페이스/프로필/데이터/보안/AI/Agent 로직을 플랫폼 간 재사용.
4. 엔진을 교체 가능한 구성요소로 취급.
5. 로컬 우선, 서버 없음, 사용자 데이터 최소 전송.
6. 장기 유지보수가 가능한 명확한 경계.

### 비목표

- Chromium 전체 포크 유지보수
- iOS 지원
- AMARE 자체 클라우드/서버
- v1에서 완전한 Chrome 확장 패리티
- v1에서 모든 사이트의 DRM/4K 보장
- 개발 전용 IDE/코딩 Agent
- 숨겨진 자동화 브라우저로 사용자 몰래 작업

---

## 2. 시스템 개요

```
┌──────────────────────────────────────────────────────────────┐
│                         UI Shell                             │
│  Windows: C# / WinUI 3        Android: Kotlin / Compose      │
│  - Window, TabStrip, Omnibox, SidePanel, Settings            │
│  - Engine adapter (WebView2 / GeckoView)                     │
│  - Agent "hands": CDP / WebExtension / PanZoom input         │
└───────────────┬──────────────────────────────────────────────┘
                │ typed commands + async events (FFI/JNI)
┌───────────────▼──────────────────────────────────────────────┐
│                        amare-core (Rust)                     │
│  Domain │ Use cases │ Policy │ Search │ AI │ Library │ Agent │
│  Ports: EnginePort, KeychainPort, ClockPort, NetPort...      │
└───────┬──────────────┬───────────────┬───────────────────────┘
        │              │               │
┌───────▼──────┐ ┌─────▼──────┐ ┌──────▼──────────────────────┐
│ amare-store  │ │ amare-ai   │ │ amare-security              │
│ SQLite/FTS5  │ │ providers  │ │ crypto/vault/keychain       │
│ blobs        │ │ extraction │ │ update/backup verification  │
└──────────────┘ └────────────┘ └─────────────────────────────┘
        │
┌───────▼──────────────────────────────────────────────────────┐
│ OS / Engine                                                  │
│ WebView2 Runtime (Windows) / GeckoView (Android)             │
│ Renderer processes, GPU, network service, sandbox            │
└──────────────────────────────────────────────────────────────┘
```

### 핵심 원칙

- **코어는 엔진을 모릅니다.** 코어는 `EnginePort`를 통해 명령을 내리고 관찰을 받습니다.
- **UI는 정책을 모릅니다.** 승인/권한/토큰 예산/인젝션 방어는 코어가 결정합니다.
- **엔진은 데이터를 모릅니다.** 탭/워크스페이스/프로필의 영속 모델은 코어가 소유합니다.
- **렌더러는 신뢰하지 않습니다.** 웹 콘텐츠/주입 스크립트는 untrusted로 취급합니다.
- **API AI 처리는 렌더러 밖에서 실행됩니다.** API 네트워크/추론은 코어/워커에서 수행하고, 렌더러에 키를 노출하지 않습니다. 웹 AI는 사용자가 연 공식 사이트의 렌더러에서 동작하며, AMARE는 쿠키/세션을 추출하지 않습니다.

---

## 3. 프로세스와 신뢰 경계

| 경계 | 구성 | 신뢰 수준 | 비고 |
|---|---|---|---|
| Web content | 웹사이트, iframe, worker | Untrusted | 샌드박스/사이트 격리 |
| Content script | AMARE 주입 스크립트 | Semi-trusted | isolated world, 최소 API |
| Engine adapter | WebView2/GeckoView 래퍼 | Trusted | 엔진 API만 노출 |
| UI shell | WinUI/Compose | Trusted | 사용자 입력/표시 |
| Core | Rust | Trusted | 정책/데이터/AI |
| AI provider | 외부 API | External | 최소 데이터, 사용자 키 |
| Extensions | WebView2 unpacked / Gecko WebExtensions | Semi-trusted | 권한/격리 |
| Update channel | GitHub Releases | External | 서명/체크섬 검증 |

### 프로세스 배치

**Windows**

- UI 프로세스: WinUI 3 + WebView2 컨트롤.
- WebView2 브라우저/렌더러/GPU/네트워크: Edge 런타임 프로세스.
- Rust 코어: UI 프로세스 내 라이브러리로 시작. AI/임베딩/인덱싱은 별도 워커 스레드/프로세스로 분리 가능.
- 장기적으로 코어를 별도 프로세스로 분리해 UI 크래시/메모리 압박을 격리할 수 있도록 `CoreClient` 경계를 둔다.

**Android**

- UI 프로세스: Compose + GeckoView 호스트.
- Gecko 콘텐츠 프로세스: 엔진이 관리.
- Rust 코어: JNI 라이브러리. AI 네트워크는 코어 스레드.
- 대용량 인덱싱/임베딩은 WorkManager/별도 프로세스로 분리.

> v1에서는 코어를 UI 프로세스에 두되, **엔진 렌더러와 AI 처리 경로를 분리**한다. 이는 요구사항의 "렌더링 프로세스와 AI 처리 시스템 분리"를 만족하는 최소 구조다. 코어 프로세스 분리는 Phase 6 이후 최적화로 남긴다.

---

## 4. 구성요소

### 4.1 UI Shell

- 창/탭 스트립/주소창/북마크 바/사이드 패널/설정/다운로드/Library/AI 채팅.
- 엔진 어댑터를 통해 탭을 생성/전환/닫습니다.
- Agent 실행 시 CDP(Windows) 또는 WebExtension/PanZoomController(Android)를 실제 탭에 적용하고, 결과를 코어에 반환합니다.
- UI는 코어의 상태 이벤트를 구독해 렌더링합니다. 코어 상태가 단일 진실원(single source of truth)입니다.

### 4.2 EnginePort (엔진 추상화)

코어가 요구하는 최소 계약입니다. 실제 구현은 Rust의 `FfiEngineAdapter`가 담당하고, 이 어댑터가 C ABI/UniFFI를 통해 C#/Kotlin의 엔진 호스트에 명령을 전달하고 이벤트를 수신합니다. 아래 trait는 개념을 설명하기 위한 단순화된 형태이며, 실제로는 비동기 명령 ID/콜백 기반으로 구현합니다.

```rust
pub trait EnginePort: Send + Sync {
    fn create_tab(&self, profile: ProfileId, url: Option<Url>) -> Result<TabId>;
    fn close_tab(&self, tab: TabId) -> Result<()>;
    fn activate_tab(&self, tab: TabId) -> Result<()>;
    fn navigate(&self, tab: TabId, url: Url) -> Result<()>;
    fn go_back(&self, tab: TabId) -> Result<()>;
    fn go_forward(&self, tab: TabId) -> Result<()>;
    fn reload(&self, tab: TabId, ignore_cache: bool) -> Result<()>;
    fn stop(&self, tab: TabId) -> Result<()>;
    fn find(&self, tab: TabId, query: &str, options: FindOptions) -> Result<FindResult>;
    fn set_zoom(&self, tab: TabId, zoom: f64) -> Result<()>;
    fn suspend(&self, tab: TabId) -> Result<()>;
    fn resume(&self, tab: TabId) -> Result<()>;

    // Observation
    fn snapshot(&self, tab: TabId) -> Result<TabSnapshot>;
    fn extract_content(&self, tab: TabId, opts: ExtractOptions) -> Result<PageContent>;
    fn capture_screenshot(&self, tab: TabId) -> Result<Image>;

    // Agent actions (must target the visible tab)
    fn agent_action(&self, tab: TabId, action: EngineAction) -> Result<ActionResult>;

    // Data / permissions / downloads
    fn clear_data(&self, profile: ProfileId, scope: DataScope) -> Result<()>;
    fn set_permission(&self, origin: &Origin, perm: Permission, state: PermissionState) -> Result<()>;
    fn start_download(&self, tab: TabId, url: Url) -> Result<DownloadId>;
}
```

이벤트(어댑터 → 코어):

```text
TabCreated, TabClosed, TabActivated
NavigationStarted, NavigationCommitted, NavigationCompleted, NavigationFailed
TitleChanged, UrlChanged, FaviconChanged, LoadingChanged
HistoryChanged, SecurityStateChanged, FindResultChanged
DownloadStarted, DownloadProgress, DownloadCompleted, DownloadFailed
PermissionRequested, NewWindowRequested, CloseRequested
ProcessFailed, RendererUnresponsive, Crashed
UserInputDetected (Agent 충돌 감지)
```

**설계 규칙**

- `EngineAction`은 닫힌 enum(click, type, key, scroll, navigate, select, hover, screenshot)으로 제한합니다. 임의 JS 실행은 별도 `evaluate_isolated`로 분리하고, Agent 기본 도구에서는 금지합니다.
- `extract_content`는 DOM/AX/Readability 결과를 구조화해 반환합니다. 원본 HTML을 그대로 LLM에 넘기지 않습니다.
- `snapshot`은 URL/제목/로딩/뒤로가기 가능/미디어 상태/보안 상태를 포함합니다.
- 엔진별 차이는 어댑터에서 정규화합니다. UI/코어에 WebView2/GeckoView 타입이 새지 않게 합니다.

### 4.2.1 엔진 계약(Command/Event/Capability)

엔진 교체를 위해 Rust trait만으로는 부족합니다. 실제 경계는 **언어 중립 command/event 스키마 + capability 협상**으로 정의합니다. `engine/contracts`에 JSON Schema/Protobuf로 두고 C#/Kotlin/Rust가 함께 사용합니다.

```text
EngineCommand {
  request_id, tab_id?, profile_id, kind,
  payload { ... }
}

EngineEvent {
  request_id?, tab_id?, kind,
  payload { ... },
  engine_version, adapter_version
}

EngineCapabilities {
  supports_cdp, supports_trusted_input, supports_extensions,
  supports_private_profile, supports_per_tab_profile,
  supports_download_pause, supports_pdf, supports_drm,
  supports_agent_input, max_tabs_hint, ...
}
```

- **Capability 협상:** 앱 시작 시 어댑터가 capability를 보고합니다. 코어/UI는 없는 기능을 숨기거나 대안을 안내합니다. 예: `supports_trusted_input=false`면 Agent 모드를 "JS 보조"로 제한하고 UI에 표시합니다.
- **Command:** `CreateTab`, `CloseTab`, `ActivateTab`, `Navigate`, `GoBack`, `GoForward`, `Reload`, `Stop`, `Find`, `SetZoom`, `Suspend`, `Resume`, `Download`, `SetPermission`, `ClearData`, `ExtractContent`, `AgentAction`, `CaptureScreenshot`.
- **Event:** `TabCreated/Closed/Activated`, `NavigationStarted/Committed/Completed/Failed`, `TitleChanged`, `UrlChanged`, `FaviconChanged`, `LoadingChanged`, `HistoryChanged`, `DownloadStarted/Progress/Completed/Failed`, `PermissionRequested`, `NewWindowRequested`, `ProcessFailed`, `UserInputDetected`.
- **비동기:** 모든 command는 `request_id`를 갖고, 결과는 `EngineEvent`로 돌아옵니다. UI 스레드를 블로킹하지 않습니다. 같은 탭의 이벤트 순서를 보장합니다.
- **오류:** `EngineError { code, message, retryable, details }`. 엔진별 에러를 정규화합니다.
- **버전:** `contract_version`, `adapter_version`, `engine_version`을 기록하고, 앱 업데이트 후 호환성 테스트를 수행합니다.
- **교체 시나리오:** WebView2 → CEF 어댑터를 추가해도 `EngineCommand/Event`와 코어/데이터/UI는 유지됩니다. 엔진별 차이는 capability와 어댑터 내부에 격리합니다.
- **금지:** 엔진 객체 포인터/핸들을 코어나 다른 플랫폼으로 넘기지 않습니다. `tab_id`/`profile_id`는 AMARE가 소유하는 불투명 ID입니다.

### 4.2.2 Rust/네이티브 책임 경계

| 책임 | Rust | C#/Kotlin |
|---|---|---|
| 도메인/유스케이스/정책 | ● | |
| SQLite/FTS5/blob/vault/암호 | ● | |
| AI 오케스트레이션/추출/인용 | ● | |
| Agent 계획/승인/원장/인젝션 방어 | ● | |
| 엔진 어댑터/이벤트 | | ● |
| UI/창/탭/IME/드래그/접근성 | | ● |
| 플랫폼 서비스(알림/생체/파일/설치/기본 브라우저) | | ● |
| 다운로드/권한/설정 UI | | ● |
| 탭 절전/엔진 입력 주입 | | ● |
| 업데이트 서명 검증 | ● (검증) | ● (설치 실행) |

**FFI 원칙:** C ABI(C#) + UniFFI(Kotlin), command/request_id/event 스트림, 취소/타임아웃/백프레셔, `AmareError` 정규화, panic 차단, 큰 payload는 blob handle. 자주 호출하지 않고 배치/스트림으로 처리합니다.


### 4.3 amare-core

- 도메인 모델과 유스케이스.
- 탭/워크스페이스/프로필/세션 복원.
- 북마크/방문 기록/다운로드/권한/검색엔진/설정.
- Library 메타데이터/태그/폴더/검색.
- AI 대화/작업 기억/Research 상태.
- Agent 정책/승인/원장.
- 업데이트/백업 오케스트레이션(플랫폼 어댑터 호출).

### 4.4 amare-store

- SQLite WAL, 마이그레이션, FTS5.
- 콘텐츠 주소 기반 blob store(해시).
- 트랜잭션 경계: 탭 세션 저장, Library 저장, 대화 저장.
- 백업용 일관된 스냅샷.

### 4.5 amare-services

- 프로필 활성 상태/context generation과 tombstone 접근 정책.
- 설정 기본값/검증/원자적 변경/reset.
- 통합 검색 kind 필터/pagination/정렬.
- UI/엔진/플랫폼 타입을 포함하지 않는다.

### 4.6 amare-ai

- 제공자 어댑터(OpenAI-compatible, Anthropic, Gemini, Ollama).
- 스트리밍, 재시도, 타임아웃, 토큰 계정.
- 컨텍스트 구성: 선택/추출/청크/랭킹/예산.
- 인용/출처 매핑.
- 임베딩/벡터 검색(옵션).
- 웹 AI(일반 대화 기본): 공식 사이트 열기/사용자 로그인/프롬프트 복사 — 자동 UI 조작은 보장하지 않음.

### 4.7 amare-agent

- 단일 Agent, 역할 전환(일반 대화/웹 조사/자료 정리/브라우저 조작).
- 계획 → 도구 선택 → 승인 → 실행 → 관찰 → 재계획 루프.
- 도구 레지스트리와 권한 스코프.
- 승인 큐와 실행 원장.
- 중지/일시정지/재개.
- 프롬프트 인젝션 방어(콘텐츠 격리, 도구 게이팅, 출력 검증).

### 4.8 amare-security

- 키 계층/암호화/비밀번호 보관함.
- OS 키체인/생체 인증 어댑터.
- 업데이트/백업 서명 검증.
- 민감 데이터 리댁션.

### 4.9 amare-platform

- OS별 경로, 키체인, 알림, 기본 브라우저 등록, 파일 선택기, 설치/업데이트, 생체 인증.
- Windows는 C#에서 일부 구현하고 Rust에는 trait로 노출.

---

## 5. 탭/워크스페이스/프로필 모델

```
Profile
 ├─ Workspace (1..n)
 │   └─ TabGroup (0..n)
 │       └─ Tab (1..n)
 └─ Window (1..n) — 활성 Workspace/Tab을 참조
```

- **Profile**: 쿠키/세션/기록/비밀번호/북마크/설정/데이터 분리 단위.
  - Windows: 기본은 WebView2 Profile API(`CoreWebView2ControllerOptions.ProfileName`)로 데이터를 분리하고, 시크릿/민감 프로필은 별도 UDF/Environment로 프로세스 격리를 강화합니다. (메모리 비용과 격리의 트레이드오프)
  - Android: GeckoView `contextId`로 저장소 분리. 프로세스 수준 격리는 제한적임을 명시.
  - **같은 프로필의 탭은 쿠키/로그인 세션/캐시를 공유**합니다(일반 브라우저 동작). "탭별 독립 세션"은 기본이 아니며, 필요하면 탭별 별도 프로필/UDF(container tab)로 구현하되 메모리 비용을 측정합니다. v0.1은 container tab을 제공하지 않습니다.
- **Workspace**: 탭/그룹/고정 탭의 독립 집합. 이름/아이콘/순서 저장.
- **Window**: OS 창. 하나의 활성 Workspace를 표시하며, 창마다 다른 Workspace를 열 수 있습니다. Windows는 다중 창, Android는 단일 창(시스템).
- **TabGroup**: 색/이름/접힘 상태. Workspace 내 독립.
- **Tab**: URL, 제목, favicon, history index, pinned, group, workspace, last active, scroll, muted, audio state, engine handle.
- **Session**: 창/탭 트리, 활성 탭, 그룹/고정 상태, 스크롤, 마지막 URL. 비밀번호/폼 값은 저장하지 않음.

### 5.1 데이터 소유권

- **엔진(WebView2/GeckoView)**: 쿠키, 캐시, 로컬스토리지, IndexedDB, 세션 스토리지, 권한 상태(프로필 단위), 현재 탭의 navigation history.
- **AMARE(Rust SQLite/vault)**: 방문 기록, 북마크, 다운로드 메타데이터, 세션/탭 상태, 비밀번호, Library, 대화, 작업 기억, Agent 원장, 설정, 검색엔진.
- **중복 저장 금지:** 엔진의 쿠키/세션을 AMARE DB에 복제하지 않습니다. AI/로그/백업에 쿠키 원문을 넣지 않습니다.
- **프로필 삭제:** AMARE 데이터와 엔진 UDF/프로필 데이터를 함께 삭제하고, 사용자 확인을 받습니다.

### 탭 수명주기

1. 생성: 코어가 TabId 발급 → 어댑터가 엔진 탭 생성 → 이벤트.
2. 탐색: 코어가 URL 정규화/검색어 판별 → `navigate`.
3. 관찰: 어댑터가 title/url/loading/security 이벤트 → 코어 상태 갱신.
4. 절전: 비활성 + 오디오 없음 + 사용자 설정 시간 경과 → `suspend`.
5. 복귀: 활성화 시 `resume`; 필요하면 새 WebView2로 재생성 후 URL/스크롤 복원.
6. 종료: 닫힌 탭 스택에 저장(복원용). 세션 저장.

### 탭 절전 정책

- 보호 대상: 활성 탭, 오디오/영상 재생 탭, 다운로드/업로드 중 탭, Agent 조작 탭, 사용자가 고정한 탭(설정).
- 절전: WebView2 `TrySuspendAsync` + `MemoryUsageTargetLevel.Low`; Android `GeckoSession.setActive(false)`/`setFocused(false)`.
- 회수: 메모리 압박 시 오래된 비활성 탭부터 WebView2 dispose. 복귀 시 URL/스크롤 복원(폼 상태는 보장하지 않음).
- 사용자 설정: 절전까지의 시간(5/15/30/60분/안 함), 메모리 관리 수준(보통/적극적).

---

## 6. 엔진 어댑터 설계

### Windows (WebView2)

**탭/프로필**

- 탭당 `WebView2` 컨트롤 1개. 같은 프로필의 탭은 공통 `CoreWebView2Environment`를 공유.
- 프로필은 `CreateCoreWebView2ControllerOptions` + `ProfileName`으로 생성. 기본 프로필은 하나의 UDF 아래 여러 프로필을 사용하고, 시크릿/민감 프로필은 별도 UDF/Environment로 격리.
- `IsInPrivateModeEnabled`로 InPrivate 컨트롤러를 생성.
- 같은 프로필의 탭은 쿠키/세션을 공유합니다. 탭별 독립 세션이 필요하면 별도 프로필/UDF(container)로 구현하며 v0.1에서는 제공하지 않습니다.

**쿠키/캐시/방문 기록**

- 쿠키: `Profile.CookieManager`(`GetCookiesAsync`, `AddOrUpdateCookie`, `DeleteCookie(s)`). `CoreWebView2Cookie`의 HttpOnly/Session/SameSite를 확인하되 로그/AI에 넣지 않습니다.
- 캐시/데이터: `Profile.ClearBrowsingDataAsync(kinds, start, end)`로 삭제. 캐시 내용을 직접 열람하는 API는 없음.
- 방문 기록: WebView2는 영구 방문 기록 저장소를 제공하지 않습니다. `NavigationCompleted`/`SourceChanged`에서 AMARE가 기록 DB에 저장하고, 현재 탭의 뒤로/앞으로는 `CoreWebView2.NavigationHistory`를 사용합니다.

**다운로드/권한**

- 다운로드: `DownloadStarting` → `CoreWebView2DownloadOperation`(Pause/Resume/Cancel, bytes/state) → AMARE 다운로드 DB/UI. 기본 폴더는 `Profile.DefaultDownloadFolderPath`.
- 권한: `PermissionRequested`에서 정책 조회 후 `SetPermissionStateAsync`로 저장. `GetNonDefaultPermissionSettingsAsync`로 목록 조회. 프로필/origin 단위.

**CDP(Agent/관찰)**

- `CallDevToolsProtocolMethodAsync` + `GetDevToolsProtocolEventReceiver`.
- iframe/worker: `Target.setAutoAttach`/`Target.attachToTarget` + `CallDevToolsProtocolMethodForSession`.
- DOM: `DOM.getDocument`, `DOM.querySelector(All)`, `DOM.getBoxModel`, `DOM.getContentQuads`, `Runtime.evaluate`, `Accessibility.getFullAXTree`.
- 입력: `Input.dispatchMouseEvent`, `Input.dispatchKeyEvent`, `Input.insertText`, `Input.synthesizeScrollGesture`.
- 탐색: `Page.navigate`, `Page.reload`, `Page.stopLoading`, `Page.getNavigationHistory`, `Page.navigateToHistoryEntry`.
- 사용자 개입 감지: WinUI 3 visual hosting에서는 `AllowHostInputProcessing`이 적용되지 않을 수 있으므로, content script trusted 이벤트 + 호스트 입력 훅 + Agent 원장 대조를 조합하고 Phase 1에서 검증합니다.
- CDP는 로컬 호스트 내부에서만 사용하고 원격 포트를 열지 않습니다.

**확장/DRM/업데이트**

- 확장: `AreBrowserExtensionsEnabled=true`, `Profile.AddBrowserExtensionAsync(unpackedFolder)` only. 스토어/CRX 설치 없음. 지원 API 목록은 보장되지 않으므로 실측.
- DRM: Edge Runtime EME/PlayReady/Widevine 경로. VMP/서비스 정책 때문에 Netflix/Disney+ 재생을 보장하지 않으며 실제 테스트로 확인.
- 업데이트: Evergreen Runtime은 AMARE와 독립적으로 업데이트. 앱은 Runtime 버전을 확인하고 기능 감지/폴백. Fixed Version은 >250MB/자동 보안 업데이트 없음.

### Android (GeckoView)

- `GeckoRuntime` 1개, `GeckoSession` 1개/탭. 활성 탭만 `GeckoView`에 attach, 비활성 세션은 `setActive(false)`.
- `contextId`로 프로필 저장소 분리. `StorageController`로 사이트 데이터 삭제.
- `ContentBlocking`으로 트래킹 보호; 확장은 WebExtension.
- DRM: Widevine L3; 서비스별 재생 결과를 매트릭스로 관리.
- Agent: 아래 표의 API를 사용합니다.

### 6.1 GeckoView Agent 기능/API 매트릭스

| 기능 | 구현 방법 | 필요 API | 가능성/한계 |
|---|---|---|---|
| DOM 읽기 | WebExtension content script → native messaging | `WebExtensionController.installBuiltIn`, `SessionController.setMessageDelegate`, `runtime.sendNativeMessage/connectNative` | 높음. isolated world |
| 요소 탐색 | content script `querySelectorAll`/`getBoundingClientRect`/`elementsFromPoint` | 위와 동일 | 높음. closed shadow DOM/교차 출처 iframe 제한 |
| 클릭 | content script `element.click()` 또는 좌표 클릭 | content script + `PanZoomController.onMouseEvent`/`onTouchEvent` | 중간. synthetic은 `isTrusted=false`; 좌표 클릭은 더 사용자에 가까움 |
| 텍스트 입력 | focus 후 IME 경로 commit | `SessionTextInput.onCreateInputConnection(EditorInfo)` + `InputConnection.commitText`; fallback content script value+input event | 중간. React/한글/IME 검증 필요 |
| 키보드 이벤트 | 엔진 키 이벤트 | `SessionTextInput.onKeyDown/onKeyUp/onKeyMultiple`; fallback content script KeyboardEvent | 중간. 조합키/IME 제약 |
| 스크롤 | 터치/모션 이벤트 | `PanZoomController.onTouchEvent`/`onMotionEvent`; fallback `window.scrollTo/scrollBy` | 높음. 관성/줌 검증 |
| 파일 다운로드 | 외부 응답 처리 | `ContentDelegate.onExternalResponse(WebResponse)` + Android `DownloadManager`/OkHttp | 중간. 쿠키/인증/파일명/진행률 직접 처리 |
| 페이지 이동 | 세션 API | `GeckoSession.loadUri`, `goBack`, `goForward`, `reload`, `stop` | 높음 |
| 사용자 개입 감지 | 호스트 입력 + content script + 원장 대조 | `GeckoView.setOnTouchListener`/`OnKeyListener`, content script trusted events, Agent action ledger | 중간. 합성/사용자 입력 구분 스파이크 필요 |
| 탭/세션 | `GeckoSession` | `open`, `setActive`, `setFocused`, `close` | 높음. `contextId`는 저장소 분리 |

> `PanZoomController`는 스크롤/좌표 입력 중심, DOM/요소 조작은 content script 중심으로 사용합니다. content script native messaging에는 `geckoViewAddons`/`nativeMessaging`(필요 시 `nativeMessagingFromContent`) 권한이 필요합니다. 공개 embedder API에 안정적인 `evaluateJS`가 없으므로 WebExtension을 우선합니다.

### Android WebView (프로토타입 전용)

- Phase 5 비교용. 제품 본체로 사용하지 않음.
- 다중 프로필(`androidx.webkit` 1.9+)은 가능하지만 렌더러 공유/사이트 격리 한계를 문서화.

---

## 7. AI 컨텍스트 파이프라인

### 7.0 AI 연결 구조

AMARE는 두 가지 AI 연결을 모두 지원합니다.

**A. 웹 AI(일반 대화 기본)**

- 사용자가 ChatGPT/Gemini/Claude 등 공식 사이트를 선택하고, AI 패널/탭(같은 프로필의 WebView2)에서 직접 로그인해 사용합니다. 기존 로그인 세션을 그대로 사용할 수 있지만, AMARE는 쿠키를 읽거나 추출하지 않습니다.
- AMARE는 쿠키/세션을 추출하거나 API처럼 재사용하지 않습니다.
- "AI 사이트 열기 + 프롬프트 복사"를 기본 보조로 제공합니다.
- 자동 입력/전송은 사용자가 명시적으로 켠 경우에만 시도하며, UI 변경/약관/안티봇 때문에 실패를 정상 상태로 간주합니다.
- 웹 AI는 Agent의 도구 호출/브라우저 제어를 안정적으로 지원하지 않습니다.
- 웹 AI 사이트는 일반 웹 콘텐츠로 격리되며 AMARE의 북마크/비밀번호/Library/다른 탭/Agent 도구에 접근할 수 없습니다.

**B. API(선택)**

- 사용자가 제공자(OpenAI 호환, Anthropic, Gemini, Ollama 등)/모델/키를 설정합니다.
- 기본 모델 하나를 일반 대화와 Agent가 공유하고, 작업별 오버라이드를 허용합니다.
- 키는 OS 키체인에 저장하고 렌더러/웹 콘텐츠에 노출하지 않습니다.
- Agent가 도구 호출/브라우저 제어를 해야 하면 API 경로를 사용합니다. 웹 AI에서 Agent를 지원하지 못하면 API 설정을 안내합니다.
- 스트리밍/취소/재시도/토큰 예산/비용 표시를 코어가 관리합니다.

**공통:** AMARE 서버 없음. 웹 AI는 사용자가 보는 화면/붙여넣은 내용 범위, API는 선택 영역/추출 본문/질문 관련 청크만 전송합니다. 전송 전 컨텍스트 미리보기를 제공합니다.

### 7.1 컨텍스트 파이프라인

```
사용자 질문
  → 관련 탭 후보 선택(제목/URL/최근성/임베딩)
  → 사용자 지정 탭 우선
  → 각 탭에서 DOM/AX/Readability 추출
  → 보일러플레이트 제거, 구조 보존
  → 질문 관련 청크 랭킹
  → 토큰 예산 내 컨텍스트 구성
  → 제공자 호출(스트리밍)
  → 인용/출처 매핑
  → 답변 + 참고자료
```

### 원칙

- 렌더링 후 DOM을 우선합니다. JS 실행 전 HTML을 신뢰하지 않습니다.
- 전체 페이지를 무조건 보내지 않습니다. 질문 관련 청크만 보냅니다.
- DOM으로 부족할 때만 스크린샷/비전 분석을 사용합니다.
- 원본 구조(제목/문단/목록/표/링크)와 출처 URL을 유지합니다.
- 광고/메뉴/푸터/쿠키 배너는 휴리스틱 + Readability로 제거합니다.
- 사용자가 선택한 텍스트가 있으면 그것을 우선합니다.
- 교차 출처 iframe, closed shadow DOM, canvas 기반 콘텐츠는 DOM 추출이 제한됩니다. 필요한 경우 사용자 선택/스크린샷/비전 분석으로 보완하고, 한계를 답변에 표시합니다.

### 인용

- 각 주장에 `source_id`를 부여하고, `source_id`는 URL/제목/접근 시각/스냅샷 해시를 가집니다.
- 답변에는 인라인 각주/링크를 표시하고, 마지막에 참고자료 목록을 제공합니다.
- 출처 클릭 시 원본 페이지로 이동합니다. 원본이 변경되었으면 스냅샷을 함께 보여줍니다.

### 멀티탭

- 사용자가 특정 탭을 지정하면 그 탭만 사용합니다.
- 지정이 없으면 제목/URL/최근성/내용 임베딩으로 후보를 선별하고, 사용된 탭 목록을 답변에 표시합니다.
- 탭이 너무 많으면 상위 N개만 사용하고, "제외" UI를 제공합니다.

---

## 8. Research 파이프라인

```
질문
  → 검색어 생성(1..n)
  → 검색 API/검색엔진 결과 수집
  → 후보 URL 선별
  → 페이지 로드(HTTP fetch 우선, JS 필요 시 사용자가 볼 수 있는 탭)
  → 본문 추출/정규화
  → 출처/주장 추출
  → 비교/요약
  → 인용 포함 보고서
  → 작업 기억에 저장
```

- 검색은 공식 API(사용자 키) 우선. HTML 스크래핑은 약관/안티봇 리스크를 명시하고 fallback으로만.
- JS 렌더링이 필요하면 사용자가 볼 수 있는 탭(가시/백그라운드)에서 로드해 추출합니다. 현재 탭을 복제한 숨겨진 브라우저를 만들지 않습니다.
- Research는 장기 작업이므로 작업 기억에 페이지/출처/요약/미완료 항목을 저장합니다.
- 사용자는 조사 범위(사이트/언어/기간)를 지정할 수 있습니다.

---

## 9. Personal Library

### 저장

- 웹페이지 스냅샷(추출 HTML + 텍스트 + 썸네일), PDF, 이미지, 문서 텍스트, 메모, 스크랩.
- 원본 URL/접근 시각/스냅샷 해시/저자/제목.
- 폴더/태그/즐겨찾기/연결 자료.

### 미리보기

- PDF: 엔진 PDF 뷰어(WebView2) 또는 Android PdfRenderer.
- 이미지/텍스트/Markdown/HTML: 샌드박스 내부 뷰어.
- Office 문서: 초기에는 추출 텍스트/썸네일. 완전한 렌더링은 후순위.
- 저장된 HTML은 **샌드박스된 내부 페이지**에서 렌더링하고, 사이트 쿠키/권한에 접근하지 않습니다.

### 검색

- FTS5 키워드 + 메타데이터 필터 + (Phase 3 후반) 벡터 유사도.
- 자연어 검색은 질의 확장 → FTS/벡터 하이브리드 → 리랭크.
- AI 자동 태그/폴더/제목 제안. 사용자가 지정한 값이 항상 우선.

### 저장 위치

- `profiles/<profile>/library/blobs/<sha256>` (콘텐츠 주소).
- DB에는 메타데이터/추출 텍스트/임베딩 참조.
- 백업 시 blob과 DB를 함께 암호화.

---

## 10. 작업 기억(Work Memory)

- 일반 대화 기록과 분리된 테이블/스토어.
- 저장 항목: 조사한 페이지, 출처, 핵심 내용, 요약/비교, 메모, 미완료 항목.
- 세션이 바뀌어도 이어서 사용 가능.
- 일반 대화는 자동 저장하지 않음. 사용자가 "작업 기억에 저장"을 명시하거나 Research/Agent 작업 완료 시 저장.
- 검색: 키워드 + 의미 기반.
- 사용자가 삭제/내보내기 가능.

---

## 11. Agent 실행 흐름

```
사용자 지시
  → 코어: 의도 분류/역할 선택/권한 스코프 결정
  → 계획 수립(단계/도구/예산)
  → 각 단계:
       - 관찰 요청(탭 snapshot/extract)
       - 도구 선택
       - 정책 엔진 검사(권한/승인/위험)
       - 필요 시 사용자 승인
       - UI 어댑터가 실제 탭에 EngineAction 실행
       - 결과/오류 관찰
       - 실행 원장 기록
       - 중지/개입 확인
  → 결과 정리(목적/수행/핵심/출처/미완료)
  → 작업 기억 저장
```

### 승인/권한

- 스코프: 현재 탭 접근, 다른 탭 접근, 웹 검색, 페이지 이동, 탭 관리, 북마크 저장, Library 읽기/쓰기/삭제, 파일 다운로드/수정/삭제, 게시/전송, 결제/구매, 비밀번호 사용.
- 모드: 자동/반자동(기본)/항상 승인.
- 외부 영향(게시/전송/삭제/결제/파일 수정)은 반자동에서도 승인.
- 권한은 작업 단위 capability token으로 부여하고 TTL/취소 가능.
- 비밀번호 원문은 Agent/LLM에 반환하지 않음. "자격 증명으로 입력" 도구만 허용.

### 사용자 개입

- Agent가 조작 중인 탭에 사용자 입력이 감지되면 일시정지 → 재관찰 → 계속/중단 확인. Windows는 호스트 입력 이벤트(가능한 경우) + content script 이벤트 + Agent 행동 원장과의 시간/대상 대조로 감지하고, Android는 content script/엔진 입력 이벤트로 감지합니다. 엔진별 감지 방식은 Phase 1/5에서 검증합니다.
- 사용자가 다른 탭을 사용하는 것은 자유. 다만 Agent가 조작하는 대상 탭이 비활성화되면 기본적으로 일시정지하고, 사용자가 "백그라운드 계속"을 선택한 경우에만 계속합니다.
- 긴 작업은 진행 표시/단계/현재 행동/중지 버튼을 제공.

### 실행 원장

- append-only. 시간, 작업 ID, 탭, 도구, 파라미터(민감 정보 리댁션), 승인자, 결과, 오류.
- 사용자가 조회/삭제 가능.
- 디버깅/감사/재현에 사용.

---

## 12. 데이터 모델

| 엔티티 | 주요 필드 |
|---|---|
| Profile | id, name, icon, engine_data_dir, created_at, settings_json |
| Workspace | id, profile_id, name, icon, position |
| WindowState | id, profile_id, workspace_id, bounds, maximized, active_tab |
| Tab | id, window_id, workspace_id, group_id, url, title, favicon, pinned, position, last_active, scroll, muted, engine_handle |
| TabGroup | id, workspace_id, name, color, collapsed |
| ClosedTab | id, profile_id, url, title, closed_at, group_id |
| Bookmark | id, profile_id, url, title, folder_id, tags, created_at |
| BookmarkFolder | id, profile_id, parent_id, name, position |
| HistoryEntry | id, profile_id, url, title, visited_at, transition, visit_count |
| Download | id, profile_id, url, path, mime, bytes, state, started_at, completed_at |
| Permission | id, profile_id, origin, type, state, updated_at |
| SearchEngine | id, profile_id, name, url_template, suggest_template, icon, is_default |
| Setting | profile_id, key, value_json |
| LibraryItem | id, profile_id, kind, title, url, blob_hash, text, metadata_json, folder_id, created_at |
| Tag | id, profile_id, name |
| LibraryItemTag | item_id, tag_id, source(user/ai) |
| Conversation | id, profile_id, title, created_at, updated_at |
| Message | id, conversation_id, role, content, tokens, created_at |
| Source | id, url, title, accessed_at, snapshot_hash |
| MessageSource | message_id, source_id, span |
| ResearchTask | id, conversation_id, goal, state, created_at, updated_at |
| WorkMemory | id, profile_id, task_id, kind, content, source_id, created_at |
| AgentTask | id, conversation_id, goal, mode, state, budget, created_at |
| AgentAction | id, task_id, step, tool, params_redacted, approval, result, error, created_at |
| Credential | id, profile_id, origin, username, secret_enc, created_at, updated_at |
| Backup | id, path, created_at, size, encrypted |

인덱스: URL/도메인/방문시각, FTS5(제목/본문/메모), Library 태그/폴더, 대화 검색.

---

## 13. 저장소 레이아웃

**Windows**

```
%LOCALAPPDATA%\AMARE\
├─ profiles\
│  └─ <profile-id>\
│     ├─ engine\          # WebView2 UDF
│     ├─ amare.db         # SQLite
│     ├─ amare.db-wal
│     ├─ library\
│     │  └─ blobs\<sha256>
│     ├─ backups\
│     └─ logs\            # redacted
├─ cache\
├─ updates\
└─ crash\
```

**Android**

```
/data/data/<package>/
├─ files/profiles/<profile-id>/
│  ├─ engine/             # Gecko data
│  ├─ amare.db
│  ├─ library/blobs/
│  └─ backups/
└─ cache/
```

- 로그는 기본적으로 URL/제목/쿠키/본문을 기록하지 않습니다. 디버그 로그는 사용자 동의 + 리댁션.
- 백업은 암호화된 아카이브. 키는 사용자 암호/키체인.

---

## 14. 세션 복원과 장애 복구

- 코어는 탭 트리 변경을 주기적으로 저널링합니다(예: 2초 debounce + 종료 시 flush).
- 비정상 종료 후 시작 시:
  1. 마지막 정상 세션 로드
  2. 손상된 DB면 WAL 복구/백업에서 복원 제안
  3. 탭은 lazy restore(URL만 먼저, 활성 탭만 로드)
- 세션에 비밀번호/폼 값/쿠키를 저장하지 않습니다.
- WebView2 `ProcessFailed`/GeckoView crash 이벤트 → 해당 탭만 복구 UI, 전체 앱 크래시 방지.
- 다운로드/업로드 중 탭은 절전/회수 대상에서 제외.

---

## 15. 성능/메모리 전략

- 시작: WebView2 Environment pre-warm, 첫 탭만 로드, 나머지는 lazy.
- 탭: 활성 탭 외 `TrySuspendAsync`/`setActive(false)`.
- 메모리 압박: LRU 비활성 탭 dispose, 복귀 시 재로드.
- 보호: 활성/오디오/다운로드/Agent 탭.
- AI: 사용하지 않을 때 스트리밍/임베딩 워커 종료. 모델은 필요 시 로드.
- 렌더러와 AI를 같은 프로세스에 두지 않음.
- 목표(내부): 콜드 스타트 < 2초, 탭 전환 p95 < 300ms, 20탭 < 2.5GB, 30탭에서도 UI 응답 유지.

---

## 16. 멀티 윈도우 / 멀티 디스플레이

- Windows: 창마다 독립 탭 스트립. 같은 프로필/워크스페이스 공유 가능.
- 탭 드래그로 창 간 이동: 대상 창에 새 WebView2를 만들고 URL/히스토리/스크롤을 복원. WebView2 자체를 재부모화하지 않음.
- 창 닫기/복원: 창별 세션 저장.
- Android: 단일 창 + 시스템 PiP/멀티 윈도우 모드. 앱 자체의 다중 창은 지원하지 않음.

---

## 17. 오류 처리와 관찰성

- 코어 에러는 `AmareError { code, message, retryable, context }`로 정규화.
- UI는 사용자 메시지 + "자세히" + 복구 액션 제공.
- 엔진 오류(프로세스 크래시, 네트워크, 인증서)는 탭 단위로 격리.
- 로컬 진단 로그: 순환 버퍼, 리댁션, 사용자 내보내기.
- 크래시 리포트는 기본 비활성. 사용자가 명시적으로 내보내 GitHub 이슈에 첨부.
- 메트릭은 로컬 카운터만. 외부 전송 없음.

---

## 18. 디렉터리 구조(제안)

```
AMARE/
├─ README.md
├─ docs/
│  ├─ TECH_STACK.md
│  ├─ ENGINE_COMPARISON.md
│  ├─ ARCHITECTURE.md
│  ├─ SECURITY_DESIGN.md
│  ├─ ROADMAP.md
│  ├─ FEATURES.md
│  ├─ DEVELOPMENT.md
│  └─ spikes/                 # Phase 1/5 검증 결과
├─ apps/
│  ├─ windows/                # C# WinUI 3 solution
│  │  ├─ AMARE.Windows/
│  │  ├─ AMARE.Engine.WebView2/
│  │  └─ AMARE.Tests/
│  └─ android/                # Gradle project
│     ├─ app/
│     ├─ engine-geckoview/
│     └─ engine-webview-spike/
├─ crates/
│  ├─ amare-core/             # domain + use cases + ports
│  ├─ amare-store/            # SQLite/FTS/blob
│  ├─ amare-services/         # Profile/Settings/Search 공통 서비스
│  ├─ amare-ai/               # providers/extraction/embeddings/research
│  ├─ amare-agent/            # planner/policy/tools/ledger
│  ├─ amare-security/         # crypto/vault/keychain
│  ├─ amare-platform/         # OS abstractions
│  ├─ amare-ffi/              # C ABI + UniFFI
│  └─ amare-testkit/
├─ engine/
│  ├─ contracts/              # event/action schema (language-neutral)
│  └─ README.md
├─ design/
│  ├─ tokens/                 # color/type/spacing JSON
│  └─ icons/
├─ tools/
│  ├─ packaging/              # WiX/APK scripts
│  ├─ signing/
│  └─ ci/
├─ tests/
│  ├─ fixtures/               # local HTTP sites
│  ├─ extraction/             # golden documents
│  ├─ injection/              # prompt injection corpus
│  └─ performance/
└─ .github/workflows/
```

- `engine/contracts`는 C#/Kotlin/Rust가 공유하는 이벤트/액션 스키마(JSON Schema 또는 Protobuf)를 둡니다.
- `design/tokens`에서 C#/Kotlin 스타일을 코드 생성해 UI 일관성을 유지합니다.
- 실제 디렉터리는 Phase 1에서 생성하며, Phase 0에서는 문서만 존재합니다.

---

## 19. ADR(Architecture Decision Record) 요약

| ID | 결정 | 이유 | 되돌릴 조건 |
|---|---|---|---|
| ADR-001 | Windows 1차 엔진은 WebView2 | 보안 업데이트/DRM/비용 | 확장/엔진 제어가 차단 요인 |
| ADR-002 | Android 1차 후보는 GeckoView | 브라우저 임베딩/확장/멀티프로세스 | Phase 5 비교에서 탈락 |
| ADR-003 | 코어는 Rust, UI는 네이티브 | 재사용/플랫폼 품질 | FFI/생산성 문제 |
| ADR-004 | 로컬 우선, 서버 없음 | 개인정보/운영비 | 사용자 요구가 동기화로 바뀜 |
| ADR-005 | 일반 대화는 웹 AI 기본, API는 선택. Agent는 API/도구 호출 | 사용자 선택/안정성 | 웹 AI가 안정적 도구 호출을 제공할 때 |
| ADR-006 | Agent는 실제 가시 탭만 조작 | 요구사항/투명성 | 없음 |
| ADR-007 | v1에서 Chrome 확장 패리티 약속 안 함 | 유지보수/법무 | CEF/포크 결정 시 |

---

## 20. 열린 설계 이슈

1. Windows에서 Rust 코어를 UI 프로세스에 둘지, 별도 프로세스로 분리할지 (v1은 in-process, Phase 6 재검토).
2. WebView2 광고 차단을 `WebResourceRequested`로 할지, MV3 확장/Declarative Net Request로 할지 (오버헤드/호환성 벤치마크).
3. Android GeckoView의 Agent 입력을 content script/`SessionTextInput`/`PanZoomController` 중 어떻게 조합할지 (신뢰도/안정성).
4. Android 프로필 격리를 `contextId`로 충분히 볼지, 별도 런타임/프로세스로 강화할지.
5. AI 패널을 네이티브로 렌더링할지, 샌드박스 WebView로 렌더링할지 (Markdown/코드/표 품질 vs 공격면).
6. Library의 HTML 스냅샷 형식(MHTML vs 추출 HTML+asset)과 장기 보존성.
7. 다중 창 탭 이동 시 히스토리 복원 범위.
8. Windows 기본 브라우저 등록/설치 방식(WiX vs MSIX).
9. GeckoView 업데이트 주기와 보안 패치 SLA.
10. WebView2 라이선스의 "full browser" 사용 범위 법무 검토.

---

## 21. 참고

- [기술 스택](TECH_STACK.md)
- [엔진 비교](ENGINE_COMPARISON.md)
- [보안 설계](SECURITY_DESIGN.md)
- [로드맵](ROADMAP.md)
