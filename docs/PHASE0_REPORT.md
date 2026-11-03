# Phase 0 수정 보고서 (Final Draft)

- 문서 상태: **승인됨(2026-11-03)** — Phase 0.5 결과는 [PHASE0_5_REPORT.md](PHASE0_5_REPORT.md) 참고
- 작성일: 2026-11-03
- 검토 반영: WebView2 아키텍처, Android Agent 구조, Rust 범위, AI 연결 정책, MVP 범위, 추가 검증
- 상세 문서: [기술 스택](TECH_STACK.md) · [엔진 비교](ENGINE_COMPARISON.md) · [아키텍처](ARCHITECTURE.md) · [보안 설계](SECURITY_DESIGN.md) · [로드맵](ROADMAP.md) · [기능 명세](FEATURES.md) · [개발 가이드](DEVELOPMENT.md)

> 이 문서는 Phase 0의 **수정된 최종 제안**입니다. 제품 코드는 아직 작성하지 않았습니다. "지원"은 설계/검증 계획을 의미하며, 실제 구현/검증 완료를 뜻하지 않습니다.

---

## 0. 이번 수정의 핵심 변경

| 항목 | 이전 | 수정 |
|---|---|---|
| AI 정책 | (이전) API 우선 | **일반 대화는 사용자가 선택한 공식 웹 AI 기본**, API는 선택. Agent는 API/도구 호출 |
| MVP | 브라우저 + 읽기 전용 AI 포함 | **브라우저 Core만 필수**, AI 패널은 선택 |
| Rust | 공통 기능 전반 | **데이터/정책/암호/검색은 Rust, UI/엔진/플랫폼은 네이티브** |
| Android Agent | PanZoomController + content script 개요 | **기능별 API 매트릭스**로 구체화 |
| WebView2 | 장단점 중심 | **제공 기능/AMARE 구현 기능/API 한계**를 분리 |
| 검증 | 계획 수준 | 라이선스/DRM/VMP/서명/격리/메모리/테스트 대안 추가 |

---

## 1. 변경된 기술 스택 결정

| 계층 | 결정 | 핵심 근거 | 주요 한계 |
|---|---|---|---|
| Windows 엔진 | **WebView2 Evergreen** | Windows 11 기본 Runtime, Edge 보안 업데이트, 멀티프로세스/사이트 격리, CDP, DRM 경로 | 엔진 수정 불가, 확장 제한, Edge 업데이트 종속 |
| Android 엔진 | **GeckoView 우선 검증** | 브라우저 임베딩 API, 멀티프로세스, WebExtensions, `contextId` | Chrome 호환 아님, Fission 상태 주의, 앱 업데이트로 엔진 갱신 |
| 코어 | **Rust(공유 가치가 큰 로직)** | 데이터/정책/암호/검색/AI 오케스트레이션 | FFI 복잡성, 네이티브와 경계 필요 |
| Windows UI | **C# .NET LTS + WinUI 3** | 네이티브 창/IME/접근성/다중 창, WebView2 통합 | WinUI 3 성숙도, 커스텀 브라우저 UI 직접 구현 |
| Android UI | **Kotlin + Jetpack Compose** | 플랫폼 통합/제스처/생체 인증 | GeckoView 호스팅 복잡성 |
| 저장소 | **SQLite WAL + FTS5 + AEAD vault** | 로컬 우선, 트랜잭션/검색/복구 | 대용량 blob/벡터는 별도 설계 |
| AI | **웹 AI 기본 + API 선택** | 사용자 선택/기존 로그인, Agent는 API | 웹 AI UI 자동화 불안정, 약관 |
| 배포 | **GitHub Releases + 서명** | 서버 없음, 사용자 통제 | Windows 코드 서명/Android 설치 확인 |

**유지:** AMARE 서버 없음, 로컬 우선, Windows 우선(Phase 1–4) → Android(Phase 5).

---

## 2. 플랫폼별 아키텍처

### 2.1 Windows: WebView2 + WinUI 3

```
┌─────────────────────────────────────────────────────────────┐
│ AMARE.Windows (C# / WinUI 3)                                │
│  Window/TabStrip/Omnibox/SidePanel/Settings                 │
│  WebView2 host (tab) + CDP adapter + platform services      │
└───────────────┬─────────────────────────────────────────────┘
                │ C ABI: commands(request_id) / events
┌───────────────▼─────────────────────────────────────────────┐
│ amare-core (Rust)                                           │
│  domain/store/AI/agent policy/vault/search/update verify    │
└─────────────────────────────────────────────────────────────┘
                │
┌───────────────▼─────────────────────────────────────────────┐
│ WebView2 Runtime (Edge/Chromium)                            │
│  browser process, renderer(s), GPU, network, sandbox         │
└─────────────────────────────────────────────────────────────┘
```

#### WebView2가 제공하는 것 / AMARE가 구현하는 것

| 범주 | WebView2 제공 | AMARE 구현 |
|---|---|---|
| 렌더링/탐색 | HTML/CSS/JS, 뒤로/앞으로/새로고침, 현재 탭 `NavigationHistory` | 탭 스트립, 주소창, 검색, 세션 복원 |
| 프로필 | `CreateCoreWebView2ControllerOptions`, `ProfileName`, `IsInPrivateModeEnabled`, 프로필별 쿠키/설정 | 프로필 UI, Workspace, 프로필 정책 |
| 세션 | 같은 프로필의 탭은 쿠키/세션 공유 | 로그인 유지; 탭별 독립 세션은 별도 프로필/UDF(container) |
| 쿠키 | `Profile.CookieManager`(`GetCookiesAsync`, add/update/delete), `CoreWebView2Cookie`(HttpOnly/Session/SameSite) | 쿠키 관리 UI, 사이트별 삭제, AI/로그 노출 금지 |
| 캐시 | `Profile.ClearBrowsingDataAsync(kinds, start, end)` | 캐시/사이트 데이터 삭제 UX, 보존 정책 |
| 다운로드 | `DownloadStarting`, `CoreWebView2DownloadOperation`(Pause/Resume/Cancel) | 다운로드 관리자 DB/UI/검색/폴더 정책 |
| 방문 기록 | 영구 브라우저 기록 저장소 없음. 현재 탭 `NavigationHistory`만 | `NavigationCompleted`/`SourceChanged` 기반 기록 DB/검색 |
| 권한 | `PermissionRequested`, `Profile.SetPermissionStateAsync`, `GetNonDefaultPermissionSettingsAsync` | 사이트별 권한 UI/기본값/회수 |
| 보안 | HTTPS/인증서 오류, 트래킹 방지(Basic/Balanced/Strict), SmartScreen | 보안 설정 UI, 예외 정책 |
| 찾기/확대/인쇄 | `Find`, `Zoom`, `Print/PrintToPdf` | 찾기 바, 확대/인쇄 UI |
| DRM/미디어 | Edge Runtime EME/PlayReady/Widevine/코덱 | 서비스별 검증, 실패 시 외부 앱 안내 |
| 확장 | unpacked extension 로드 API | 확장 관리 UI/권한/업데이트 정책(제한) |
| 자동화 | CDP `CallDevToolsProtocolMethodAsync`, `ForSession` | Agent 계획/정책/승인/원장 |
| 업데이트 | Evergreen Runtime 자동 업데이트 | 앱 업데이트/서명/롤백/채널 |

#### 탭/세션/프로필 분리 결론

- **같은 프로필의 탭은 쿠키/로그인 세션/캐시를 공유**합니다. 일반 브라우저의 정상 동작입니다.
- **탭별 독립 세션은 WebView2 기본 기능이 아닙니다.** 필요하면 탭별 별도 프로필/UDF를 만들어야 하며 메모리/프로세스 비용이 큽니다. v0.1은 container tab을 제공하지 않습니다.
- **프로필 분리**는 `ProfileName`으로 가능하며, 여러 프로필이 하나의 UDF 아래에서 데이터를 분리합니다. 더 강한 격리가 필요하면 프로필별 UDF/Environment를 사용합니다.
- **시크릿**은 `IsInPrivateModeEnabled` 또는 별도 UDF로 구현합니다.
- **AMARE 데이터(북마크/기록/비밀번호/Library/대화)** 는 엔진 저장소와 분리된 AMARE SQLite/vault에 프로필 ID로 저장합니다.
- **방문 기록은 엔진이 저장하지 않으므로** AMARE가 직접 기록합니다.
- **권한은 프로필/origin 단위**로 저장/회수합니다.

#### CDP: DOM 분석과 실제 화면 조작

| 목적 | CDP 명령(예) |
|---|---|
| DOM 읽기 | `DOM.getDocument`, `DOM.querySelector(All)`, `Runtime.evaluate` |
| 접근성 | `Accessibility.getFullAXTree` |
| 좌표 | `DOM.getBoxModel`, `DOM.getContentQuads` |
| 클릭 | `Input.dispatchMouseEvent`(pressed/released) |
| 입력 | `Input.insertText`, `Input.dispatchKeyEvent` |
| 스크롤 | `Input.synthesizeScrollGesture` |
| 탐색 | `Page.navigate`, `Page.reload`, `Page.stopLoading`, `Page.getNavigationHistory`, `Page.navigateToHistoryEntry` |
| iframe/worker | `Target.setAutoAttach`, `Target.attachToTarget`, `CallDevToolsProtocolMethodForSession` |

**한계:** CDP는 공식 공개 API가 아니며 하위 호환이 보장되지 않습니다. 교차 출처 iframe/closed shadow DOM/canvas는 제한됩니다. WinUI 3 visual hosting에서는 `AllowHostInputProcessing`이 적용되지 않을 수 있어 사용자 개입 감지는 content script + 원장 대조 + 호스트 훅을 조합하고 Phase 1에서 검증합니다.

#### Chrome 확장/DRM/업데이트

- **확장:** `AreBrowserExtensionsEnabled=true` + `AddBrowserExtensionAsync(unpackedFolder)`만. 스토어/CRX 설치 API 없음. 지원 API 목록은 공개 보장되지 않으며 일부 확장은 실패합니다. v1은 내장 기능 우선, 확장은 실험적입니다.
- **DRM:** EME/PlayReady/Widevine 경로는 있으나 **VMP(Verified Media Path)**/서비스 정책 때문에 Netflix/Disney+ 재생을 보장하지 않습니다. 실제 서비스로 검증하고 실패 시 외부 Edge/앱 안내를 제공합니다.
- **업데이트 독립성:** WebView2 Evergreen Runtime은 AMARE와 독립적으로 Microsoft Edge 경로로 업데이트됩니다. AMARE 업데이트는 앱/코어/UI만 교체합니다. Fixed Version은 >250MB이고 자동 보안 업데이트가 없어 기본으로 사용하지 않습니다.

### 2.2 Android: GeckoView + Compose

```
┌─────────────────────────────────────────────┐
│ AMARE.Android (Kotlin/Compose)              │
│  주소창/탭 카드/하단 바/설정                │
│  GeckoView host + WebExtension bridge       │
│  Agent hands: content script/PanZoom/IME    │
└───────────────┬─────────────────────────────┘
                │ JNI/UniFFI
┌───────────────▼─────────────────────────────┐
│ amare-core (Rust) — Windows와 공유          │
└─────────────────────────────────────────────┘
```

- `GeckoRuntime` 1개, `GeckoSession` 1개/탭. 활성 탭만 `GeckoView`에 attach.
- 프로필은 `contextId`로 저장소 분리. 프로세스 수준 격리는 제한적임을 명시.
- 확장은 WebExtensions 허용목록/내장. Chrome 확장 아님.
- DRM은 Widevine L3 범위. HD/4K/L1 보장 없음.
- GeckoView의 Fission(사이트 격리) 상태는 릴리스/버전에 따라 달라질 수 있으므로 Phase 5에서 실제 상태를 확인하고 한계를 문서화합니다.
- Phase 5 초입에 GeckoView vs Android WebView 비교 후 최종 결정. Chrome 확장 패리티를 위해 Chromium 포크를 즉시 시작하지 않습니다.

---

## 3. 엔진 추상화 구조

### 3.1 경계

- `engine/contracts`: 언어 중립 command/event/capability 스키마.
- Rust `EnginePort` + `FfiEngineAdapter`가 코어와 네이티브 엔진 호스트 사이를 중계.
- UI/코어는 WebView2/GeckoView 타입을 import하지 않습니다.
- `tab_id`/`profile_id`는 AMARE가 소유하는 불투명 ID입니다.

### 3.2 Command/Event/Capability

```text
EngineCommand { request_id, tab_id?, profile_id, kind, payload }
EngineEvent   { request_id?, tab_id?, kind, payload, engine_version, adapter_version }
EngineCapabilities {
  supports_cdp, supports_trusted_input, supports_extensions,
  supports_private_profile, supports_per_tab_profile,
  supports_download_pause, supports_pdf, supports_drm,
  supports_agent_input, max_tabs_hint
}
```

- 앱 시작 시 어댑터가 capability를 보고하고, 코어/UI는 없는 기능을 숨기거나 대안을 안내합니다.
- 모든 command는 비동기이며 `request_id`로 결과를 매칭합니다. 같은 탭/요청 내 이벤트 순서를 보장합니다.
- 오류는 `EngineError { code, message, retryable, details }`로 정규화합니다.
- 계약/어댑터/엔진 버전을 기록하고 앱 업데이트 후 호환성 테스트를 수행합니다.

### 3.3 Rust/네이티브 책임 경계와 FFI

| 영역 | 구현 | 이유 |
|---|---|---|
| 도메인/유스케이스/정책 | Rust | Windows/Android 공유 |
| SQLite/FTS5/blob/vault/암호 | Rust | 데이터 무결성/공유 |
| AI 오케스트레이션/추출/인용 | Rust | 제공자/플랫폼 독립 |
| Agent 계획/승인/원장/인젝션 방어 | Rust | 보안 정책 단일 지점 |
| 엔진 어댑터/이벤트 | C#/Kotlin | 엔진 API 1급 시민 |
| UI/창/탭/IME/드래그/접근성 | C#/Kotlin | 네이티브 품질 |
| 플랫폼 서비스(알림/생체/파일/설치/기본 브라우저) | C#/Kotlin | OS 통합 |
| 다운로드/권한/설정 UI | C#/Kotlin | UI/플랫폼 결합 |
| 탭 절전/입력 주입 | C#/Kotlin | 엔진/OS API 직접 사용 |
| 업데이트/백업 검증 | Rust(검증) + 네이티브(실행) | 서명 검증과 설치 분리 |

**FFI/비동기/오류**

- Windows: C ABI + C# P/Invoke(ClangSharp/수동 래퍼). Android: UniFFI Kotlin.
- `command {request_id, kind, payload}` → `event {request_id, kind: progress|result|error, payload}`.
- 장시간 작업은 취소 토큰/타임아웃/백프레셔. 같은 탭/요청 내 이벤트 순서 보장.
- 오류: `AmareError {code, message, retryable, details, trace_id}` → C# 예외/Kotlin Result. Rust panic은 `catch_unwind`로 차단.
- 큰 payload는 blob handle/공유 메모리. FFI를 채팅처럼 자주 호출하지 않음.
- ABI/계약 버전을 명시하고 앱/엔진 업데이트 후 호환성 테스트.

### 3.4 엔진 교체 시나리오

- WebView2 → CEF 어댑터 추가 시 `EngineCommand/Event`와 코어/데이터/UI는 유지합니다.
- 엔진별 차이는 capability와 어댑터 내부에 격리합니다.
- 데이터 스키마는 엔진과 무관하게 유지합니다.
- 엔진 교체는 "설정 변경"이 아니라 별도 마이그레이션 프로젝트로 취급합니다.

---

## 4. Agent 제어 구조

### 4.1 공통 원칙

- Agent는 **사용자가 보고 있는 실제 탭 핸들**만 조작합니다. 숨겨진 복제 브라우저를 만들지 않습니다.
- 코어는 계획/정책/승인/원장을 담당하고, 네이티브 어댑터가 실제 엔진 명령을 실행합니다.
- 사용자 입력이 감지되면 일시정지 → 재관찰 → 계속/중단 확인.
- 대상 탭이 비활성화되면 기본 일시정지, 사용자가 "백그라운드 계속"을 선택한 경우에만 계속.
- 페이지 콘텐츠는 `UNTRUSTED_CONTENT`로 취급하고 도구 호출은 정책 엔진/승인 없이는 불가능합니다.

### 4.2 Windows Agent

- 관찰: CDP `DOM`/`Accessibility`/`Runtime`/`Page`.
- 클릭: `Input.dispatchMouseEvent`.
- 입력: `Input.insertText`/`Input.dispatchKeyEvent`.
- 스크롤: `Input.synthesizeScrollGesture`.
- 탐색: `Page.navigate`/`Page.reload`/`Page.getNavigationHistory`.
- iframe: `Target.setAutoAttach` + `CallDevToolsProtocolMethodForSession`.
- 사용자 개입: content script trusted 이벤트 + 호스트 입력 훅 + Agent 원장 대조.
- CDP는 로컬 호스트 내부에서만 사용합니다.

### 4.3 Android Agent: 기능별 API 매트릭스

| 기능 | 1차 방법 | 필요 API | 가능성/한계 |
|---|---|---|---|
| DOM 읽기 | WebExtension content script → native messaging | `WebExtensionController.installBuiltIn`, `SessionController.setMessageDelegate`, `runtime.sendNativeMessage/connectNative` | 높음. isolated world |
| 요소 탐색 | content script `querySelectorAll`/`getBoundingClientRect`/`elementsFromPoint` | 위와 동일 | 높음. closed shadow DOM/교차 출처 iframe 제한 |
| 클릭 | content script `element.click()` 또는 좌표 클릭 | content script + `PanZoomController.onMouseEvent`/`onTouchEvent` | 중간. synthetic은 `isTrusted=false`; 좌표 클릭은 더 사용자에 가까움 |
| 텍스트 입력 | focus 후 IME 경로 commit | `SessionTextInput.onCreateInputConnection(EditorInfo)` + `InputConnection.commitText`; fallback content script value+input | 중간. React/한글/IME 검증 |
| 키보드 이벤트 | 엔진 키 이벤트 | `SessionTextInput.onKeyDown/onKeyUp/onKeyMultiple`; fallback content script KeyboardEvent | 중간. 조합키/IME 제약 |
| 스크롤 | 터치/모션 이벤트 | `PanZoomController.onTouchEvent`/`onMotionEvent`; fallback `window.scrollTo/scrollBy` | 높음. 관성/줌 검증 |
| 파일 다운로드 | 외부 응답 처리 | `ContentDelegate.onExternalResponse(WebResponse)` + Android `DownloadManager`/OkHttp | 중간. 쿠키/인증/파일명/진행률 직접 처리 |
| 페이지 이동 | 세션 API | `GeckoSession.loadUri`, `goBack`, `goForward`, `reload`, `stop` | 높음 |
| 사용자 개입 감지 | 호스트 입력 + content script + 원장 대조 | `GeckoView.setOnTouchListener`/`OnKeyListener`, content script trusted events, Agent ledger | 중간. 합성/사용자 입력 구분 스파이크 |
| 탭/세션 | `GeckoSession` | `open`, `setActive`, `setFocused`, `close` | 높음. `contextId`는 저장소 분리 |

**설계 결론:** `PanZoomController`는 스크롤/좌표 입력, `SessionTextInput`은 키/IME 입력, WebExtension content script는 DOM/요소 탐색과 클릭 후보 계산에 사용합니다. 공개 embedder API에 안정적인 `evaluateJS`가 없으므로 content script를 우선합니다.

### 4.4 권한/승인

- 스코프: 현재 탭/다른 탭/웹 검색/페이지 이동/탭 관리/북마크/Library 읽기·쓰기·삭제/다운로드/파일 수정·삭제/게시·전송/결제/비밀번호 사용.
- 모드: 자동/반자동(기본)/항상 승인.
- 외부 영향(게시/전송/삭제/결제/파일 수정)은 반자동에서도 승인.
- 작업 단위 capability token + TTL/취소. 실행 원장 append-only.
- 비밀번호 원문은 LLM/Agent에 반환하지 않고 `credential.fill`만 허용.

---

## 5. AI 연결 구조

### 5.1 일반 대화(기본): 공식 웹 AI

- ChatGPT/Gemini/Claude 등 **사용자가 선택한 공식 사이트**를 AI 패널/탭(같은 프로필의 WebView2)에서 직접 로그인해 사용합니다. 기존 로그인 세션을 사용할 수 있지만 AMARE는 쿠키를 읽지 않습니다.
- AMARE는 쿠키/세션을 추출하거나 API처럼 재사용하지 않습니다.
- 기본 보조: "AI 사이트 열기 + 프롬프트 복사".
- 자동 입력/전송은 사용자가 명시적으로 켠 경우에만 시도하고, UI 변경/약관/안티봇 실패를 정상 상태로 처리합니다.
- 웹 AI는 Agent의 도구 호출/브라우저 제어를 안정적으로 지원하지 않습니다.

### 5.2 API(선택)

- 사용자가 제공자(OpenAI 호환, Anthropic, Gemini, Ollama 등)/모델/키를 설정한 경우 사용합니다.
- **기본 모델 1개를 일반 대화와 Agent가 공유**하고, 작업별 오버라이드를 허용합니다.
- 키는 OS 키체인에 저장하고 렌더러/웹 콘텐츠에 노출하지 않습니다.
- Agent의 도구 호출/브라우저 제어는 API 경로를 사용합니다. 웹 AI가 Agent를 지원하지 못하면 API 설정을 안내합니다.
- 스트리밍/취소/재시도/토큰 예산/비용 표시.

### 5.3 웹페이지 이해/Research/Library(후속)

- 렌더링 후 DOM/Readability/Accessibility 추출, 보일러플레이트 제거, 질문 관련 청크만 전송.
- 인용/출처/스냅샷 해시 유지.
- 멀티탭은 제목/URL/내용으로 후보 선별, 사용자 지정 우선.
- Research/Library/작업 기억은 Phase 3. v0.1에서는 구현하지 않습니다.

---

## 6. 보안 및 데이터 저장 구조

### 6.1 저장소

```
Windows: %LOCALAPPDATA%\AMARE\
  profiles\<id>\engine\        # WebView2 UDF
  profiles\<id>\amare.db       # SQLite (북마크/기록/설정/대화 등)
  profiles\<id>\library\blobs\ # 콘텐츠 해시
  profiles\<id>\backups\
  logs\                         # 리댁션
Android: /data/data/<pkg>/files/profiles/<id>/...
```

- 엔진: 쿠키/캐시/로컬스토리지/IndexedDB/권한.
- AMARE: 방문 기록/북마크/다운로드 메타데이터/세션/비밀번호/Library/대화/Agent 원장.
- 쿠키/세션은 AMARE DB에 복제하지 않습니다.

### 6.2 암호화/키

- 민감 데이터는 XChaCha20-Poly1305 AEAD, 키는 OS 키체인(DPAPI/Keystore).
- 마스터 비밀번호는 Argon2id로 키 유도, 자동 잠금/재인증.
- 비밀번호 원문은 AI/로그/원장에 기록하지 않음. Agent는 `credential.fill`만 사용.
- 백업은 암호화된 아카이브 + 무결성 검증.

### 6.3 프롬프트 인젝션/Agent 안전

- 지시 계층: 시스템 정책 > 사용자 지시 > 페이지 콘텐츠(`UNTRUSTED_CONTENT`).
- 페이지 콘텐츠는 도구를 직접 호출할 수 없음.
- 도구 인자 스키마/허용목록 검증, 위험 스킴/경로 차단.
- 외부 영향은 승인, 민감 도구는 재인증.
- 인젝션 코퍼스 회귀 테스트.

### 6.4 업데이트/백업

- GitHub Releases + 서명 매니페스트(Ed25519/minisign) + SHA-256 + Authenticode/APK 서명.
- 롤백 방지, 실패 시 현재 버전 유지.
- WebView2 Runtime은 독립 Evergreen 업데이트. GeckoView는 앱 릴리스에 포함.
- 자동 로컬 백업/수동 내보내기/가져오기, 병합/덮어쓰기, 민감 데이터 재암호화.

---

## 7. 수정된 MVP 명세

### 7.1 MVP Core = v0.1 Windows only

**필수**

1. WebView2 웹 렌더링
2. 탭 생성/닫기/전환/복원
3. 주소창(URL/검색) + 검색엔진 선택/관리
4. 뒤로/앞으로/새로고침/중지
5. 북마크
6. 방문 기록
7. 다운로드
8. 세션 복원
9. 기본 설정(테마/검색엔진/시작 페이지)
10. 기본 보안: HTTPS 경고/인증서 오류/사이트 권한/트래킹 방지
11. 탭 절전/메모리 관리 v0
12. SQLite 저장/복구, 서명된 설치/업데이트 확인

**선택(없어도 출시)**

- 읽기 전용 AI 패널: 현재 페이지 질문/출처/Markdown/코드/표/스트리밍
- 웹 AI 사이트 패널 열기(사용자 로그인)
- API 제공자 1개 설정

**제외**

- Workspace/탭 그룹/탭 검색/휠 전환
- 다중 프로필/시크릿 모드
- 비밀번호 관리자/확장/번역/읽기 모드/강제 다크/PIP/영상 배속
- 광고 차단 커스텀 필터
- Library/Research/작업 기억/Agent
- Android
- DRM 4K/HDR/Chrome 확장 패리티

### 7.2 성공 기준

- 2주 내부 도그푸딩에서 데이터 손실/치명 크래시 0.
- 상위 50개 + 국내 20개 사이트 기본 탐색/로그인 성공.
- 30탭 세션 복원 성공.
- 북마크/기록/다운로드/설정/세션 재시작 후 유지.
- 프로필 간 데이터 누출 0.
- 콜드 스타트 < 2초, 탭 전환 p95 < 300ms.
- 설치/업데이트 서명 검증 성공.
- AI 패널은 선택이며 포함 시에만 출처 검증.

### 7.3 예상 기간

- MVP Core: 10–14주 (Phase 0.5 검증/스파이크 3–4주 포함)
- 선택 AI 패널: +2–3주
- v1.0 전체: 약 14–21개월(1–2명 기준, 선택 AI 포함 시 상한, 변동 가능)

---

## 8. 기술적 위험과 대응책

| 위험 | 가능성 | 영향 | 대응/대안 |
|---|---|---|---|
| WebView2 확장 제한 | 높음 | 중간 | 내장 기능 우선, 실험적 확장, CEF 탈출구 |
| DRM VMP/서비스 정책 | 중간 | 중간 | 실제 서비스 테스트, 외부 Edge/앱 안내, 4K 보장 안 함 |
| CDP 비공식 API/회귀 | 중간 | 중간 | 버전 고정 테스트, 명령 추상화, JS fallback, Agent 범위 축소 |
| WinUI 3 입력 감지 | 중간 | 중간 | content script+원장 대조, 필요 시 호스팅 재검토 |
| WebView2 메모리(탭당 렌더러) | 높음 | 높음 | 절전/폐기/lazy restore, 30탭 벤치마크 |
| Edge Runtime 업데이트 회귀 | 중간 | 중간 | 자동 스모크, 기능 감지, Fixed Version 검토 |
| Android GeckoView 호환/보안 | 높음 | 높음 | Phase 5 게이트, WebView 프리뷰/연기 |
| Android Agent 입력 신뢰도 | 중간 | 중간 | content script+PanZoom+IME 조합, API 매트릭스 스파이크 |
| Rust FFI 복잡성 | 중간 | 중간 | 굵은 경계, command/event, 코드 생성, 통합 테스트 |
| AI 웹 UI 자동화/약관 | 높음 | 중간 | 기본은 열기/복사, 자동화 opt-in, API 대안 |
| AI 비용/지연 | 중간 | 중간 | 사용자 키, 토큰 예산, 캐시, 로컬 모델 |
| WebView2/WinUI 라이선스 | 중간 | 높음 | 법무 검토, 고지/재배포 조건 |
| 업데이트 서명 키 관리 | 중간 | 높음 | 오프라인/HSM, 키 회전, 롤백 테스트 |
| 프로필 데이터 누출 | 낮음 | 높음 | 프로필/UDF 분리 테스트, 누출 시 UDF 강제 |
| 법무/DRM/코덱 | 중간 | 높음 | 조기 검토, 보장 범위 명시 |

### 8.1 메모리 예상/테스트(i5-7500, 16GB, GTX 1050 2GB)

> 아래는 **공개 자료 기반 예상치**이며 실측이 아닙니다.

- WebView2 첫 인스턴스: 대략 150–250MB(브라우저/GPU/네트워크 포함).
- 활성 탭 추가: 대략 80–150MB/탭(사이트 복잡도 의존).
- 30탭(전부 활성): 대략 3–5GB. 16GB에서 OS/앱 포함 시 빠듯할 수 있음.
- 절전/폐기 적용: 대략 1.5–3GB 목표. `TrySuspendAsync`/`MemoryUsageTargetLevel.Low`/탭 dispose.
- AI 패널: +100–200MB 예상. 로컬 모델은 MVP 제외(1–4GB+ 가능).

**테스트 계획**

1. 1/5/10/20/30탭, 동일 사이트/다른 사이트/동영상/긴 문서.
2. 활성/백그라운드/절전/폐기 상태별 Working Set/Private Bytes/GPU/프로세스 수.
3. 콜드 스타트, 탭 전환 p50/p95, 8시간 soak, 메모리 증가율.
4. 16GB에서 30탭 + (선택) AI 패널 + 다운로드 동시 사용.
5. 실패 기준: 20탭에서 3GB 초과, OOM, 전환 p95 > 500ms → 탭 정책/엔진 재검토.

### 8.2 라이선스/배포/서명 검증

**WebView2 Runtime**

- Windows 11에는 Evergreen Runtime이 기본 탑재되어 있지만, 변형/손상 환경을 위해 Evergreen Bootstrapper 또는 Standalone Installer를 설치 시 안내·배포합니다.
- Runtime은 재배포 가능한 개체 코드이지만, Microsoft 라이선스의 "significant primary functionality", 고지, 상표, 단독 배포 금지 등의 조건을 확인해야 합니다. AMARE가 "일반 브라우저"로 사용하는 것이 허용되는지 법무 검토가 필요합니다.
- Fixed Version은 앱에 Runtime을 번들(>250MB)하고 자동 보안 업데이트가 없으므로 기본으로 사용하지 않습니다.

**WinUI 3 / Windows App SDK**

- Windows App SDK 리포지토리는 MIT이지만, NuGet 패키지/런타임/재배포에는 Microsoft 약관과 고지가 적용될 수 있습니다. 프레임워크 종속/자체 포함 배포, Windows App SDK Runtime 설치 방식, 앱 패키징(MSIX vs Win32)을 Phase 0.5에서 확인합니다.
- WinUI 3 소스는 shared source이며 빌드 가능한 오픈소스가 아닙니다. 라이선스 문구를 앱 라이선스와 혼동하지 않습니다.

**DRM/스트리밍 배포 가능성**

- WebView2의 EME/PlayReady/Widevine 지원과 실제 서비스 재생은 별개입니다. Netflix/Disney+는 VMP(Verified Media Path) 또는 자체 브라우저 정책을 요구할 수 있습니다.
- Phase 1 S3에서 YouTube/Spotify/Netflix/Disney+/Shaka 테스트 콘텐츠를 실제로 검증하고, 서비스별 결과를 매트릭스로 기록합니다.
- 4K/HDR/HEVC는 보장하지 않습니다. 재생 불가 시 외부 Edge/앱으로 열기 안내를 제공합니다.
- CEF/Chromium 포크로 전환하더라도 proprietary codecs/Widevine/VMP 라이선스 문제가 별도로 남습니다.

**업데이트 패키지**

- Windows: Authenticode 서명 + 업데이트 매니페스트 Ed25519/minisign 서명 + SHA-256. 설치 파일/매니페스트/체크섬을 모두 검증하고, 현재 버전보다 낮은 버전은 거부(사용자 확인 예외).
- Android: APK 서명/패키지명/versionCode 검증. GitHub 직접 설치 시 사용자 확인이 필요하며 무음 자동 설치를 약속하지 않습니다.
- 서명 키는 오프라인/HSM/제한된 CI 시크릿으로 관리하고, 키 회전/폐기 절차를 문서화합니다.
- 실패 시 현재 버전으로 계속 실행하고, 서명/체크섬 실패 시 설치하지 않습니다.

### 8.3 프로필/데이터 격리 검증

- Windows: 같은 프로필의 탭은 쿠키/세션을 공유(정상). 프로필 간에는 `ProfileName`/UDF로 쿠키/캐시/설정/기록을 분리.
- 민감/시크릿 프로필은 별도 UDF/Environment로 프로세스 격리.
- Android: `contextId`로 저장소 분리. 프로세스 수준 격리는 제한적임을 문서화.
- AMARE DB는 프로필 ID로 분리하고, 프로필 삭제 시 엔진 데이터와 AMARE 데이터를 함께 삭제.
- 테스트: 프로필 A에서 로그인/쿠키/북마크/기록/설정/다운로드를 생성한 뒤 프로필 B에서 누출 0을 확인. UDF 삭제/재생성 후 복구 동작 확인.

### 8.4 기능별 테스트/실패 대안

| 기능 | 테스트 | 실패 시 대안 |
|---|---|---|
| 웹 렌더링 | 상위/국내 사이트 매트릭스 | 엔진/플래그 회귀 분석, CEF 검토 |
| 탭/세션 | 30탭/재시작/크래시 복구 | 탭 폐기/lazy restore |
| 주소창/검색 | URL/검색/북마크/기록 검색 | 검색엔진 템플릿 수정 |
| 북마크/기록/다운로드 | DB 마이그레이션/가져오기/내보내기 | SQLite 백업/복구 |
| 쿠키/캐시/권한 | 프로필/origin 격리 | UDF 분리/권한 초기화 |
| 확장 | 5–20개 로드/동작 | 내장 기능, 확장 비노출 |
| DRM | Netflix/Disney+/YouTube/Spotify | 외부 Edge/앱 안내 |
| CDP Agent | 10–20개 사이트 클릭/입력/스크롤 | JS fallback, Agent 범위 축소 |
| AI 웹 | 사이트 열기/복사/붙여넣기 | API/수동 |
| AI API | 스트리밍/취소/오류/비용 | 제공자 변경/재시도 |
| 업데이트 | 서명/체크섬/롤백/설치 | 수동 다운로드 |
| Android | GeckoView 20탭/국내 사이트/DRM | WebView 프리뷰/Android 연기 |

---

## 9. 단계별 구현 순서

### Phase 0.5 — 승인 직후 검증(2–3주, Phase 1 스파이크에 포함)

1. WebView2/WinUI 3 라이선스·재배포 조건 법무 검토.
2. DRM/VMP 실제 서비스 테스트.
3. 업데이트 서명/설치 리허설.
4. 프로필/data 격리 테스트 설계.
5. 메모리 측정 계획/하네스.
6. 테스트/실패 대안 표 확정.

### Phase 1 — MVP Core(10–14주, Phase 0.5 포함; AI +2–3주)

1. 저장소 스캐폴딩/contracts/FFI.
2. WebView2 호스트/탭/프로필.
3. SQLite 스키마/세션 저널.
4. 주소창/검색/탐색/탭.
5. 북마크/기록/다운로드/설정/세션 복원.
6. 보안/권한/HTTPS/트래킹.
7. 설치/업데이트/서명.
8. (선택) 읽기 전용 AI 패널.
9. 2주 도그푸딩/성능/보안 검증.

### Phase 2 — 브라우저 완성도(10–14주)

Workspace/탭 그룹/다중 프로필/시크릿/비밀번호/확장 실험/번역/읽기 모드/강제 다크/PIP/영상 배속/광고 차단/가져오기/다중 창/접근성.

### Phase 3 — AI/Library/Research(8–12주)

웹 AI 기본 + API 선택, 페이지/멀티탭 이해, Library, Research, 작업 기억, 인용.

### Phase 4 — Agent(10–14주)

실제 가시 탭 제어, 권한/승인/원장, 중지/개입, 프롬프트 인젝션 방어, 결과 정리.

### Phase 5 — Android(12–18주)

엔진 결정 게이트 → Compose UI/GeckoView, 프로필/탭/보안, AI/Agent 제한, 업데이트.

### Phase 6 — 안정화/배포(8–12주+)

메모리/보안/DRM/확장 테스트, 백업/복원, GitHub Releases v1.0.

---

## 10. 개발 착수 전 남은 미결정 사항

| 항목 | 결정 시점 | 필요한 입력 |
|---|---|---|
| WebView2의 "브라우저 제품" 라이선스 범위 | 승인 전 | 법무 검토 |
| WinUI 3/Windows App SDK NuGet/런타임 재배포 | 승인 전 | 약관/고지 검토 |
| WinUI 3 visual hosting에서 CDP/입력 감지 충분성 | Phase 1 S7 | 실제 스파이크 |
| 확장 프로그램 v0.2 노출 여부 | Phase 2 | S8/약관 |
| 광고 차단 v0.2 포함 여부 | Phase 2 | S9 오버헤드 |
| Netflix/Disney+ VMP 통과 여부 | Phase 1 S3 | 실제 서비스 |
| DRM/VMP 지원 범위(어디까지 보장할지) | Phase 1 S3 | 서비스 매트릭스 |
| 업데이트 설치 방식(WiX vs MSIX) | Phase 1 S4 | 기본 브라우저/셸/서명 |
| 코드 서명 인증서(EV/OV) | Phase 1 전 | 비용/일정 |
| C# FFI 바인딩 방식 | Phase 1 S6 | 프로토타입 |
| 메모리 목표/탭 폐기 임계치 | Phase 1 S1 | i5-7500 실측 |
| 웹 AI 자동화 허용 범위/약관 | Phase 3 | 서비스별 검토 |
| Research 검색 API | Phase 3 | 약관/쿼터/가격 |
| Android 엔진 최종 선택 | Phase 5 초 | GeckoView vs WebView |
| Android 최소 버전/배포 | Phase 5 | 정책/요구 |
| 동기화/외부 클라우드 | v1 이후 | E2E 설계 |

---

## 11. 승인 요청

1. Windows 1차 엔진 **WebView2**, Android **GeckoView 우선 검증**.
2. **Rust는 데이터/정책/암호/검색/AI**, UI/엔진/플랫폼은 **C#/Kotlin**.
3. AI 일반 대화는 **웹 AI 기본**, API는 **선택**, Agent는 **API/도구 호출**.
4. MVP Core는 **브라우저 기본 기능만**, AI 패널은 **선택**.
5. Agent/Research/Library/작업 기억/Android 정식 버전은 **이후 단계**.
6. v1 비보장: Chrome 확장 패리티, Android Chrome 확장, DRM 4K/HDR, 모든 사이트 Agent 자동화, 웹 AI UI 자동화 안정성.
7. Phase 0.5의 라이선스/DRM·VMP/서명/프로필 격리/메모리 검증 결과를 Phase 1 진행 기준으로 승인한다.

승인 후 Phase 0.5 검증과 Phase 1 스캐폴딩을 시작합니다.

---

## 12. 참고 자료

- WebView2 개요: <https://learn.microsoft.com/microsoft-edge/webview2/>
- WebView2 프로필: <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2profile>
- WebView2 컨트롤러 옵션: <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2controlleroptions>
- WebView2 쿠키: <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2cookiemanager>
- WebView2 다운로드: <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2downloadoperation>
- WebView2 권한: <https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2profile.setpermissionstateasync>
- WebView2 CDP: <https://learn.microsoft.com/microsoft-edge/webview2/how-to/chromium-devtools-protocol>
- WebView2 배포: <https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution>
- WebView2 Evergreen/Fixed: <https://learn.microsoft.com/microsoft-edge/webview2/concepts/evergreen-vs-fixed-version>
- WebView2 성능: <https://learn.microsoft.com/microsoft-edge/webview2/concepts/performance>
- Windows App SDK 라이선스: <https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE>
- GeckoView 아키텍처: <https://firefox-source-docs.mozilla.org/mobile/android/geckoview/contributor/geckoview-architecture.html>
- GeckoView WebExtensions: <https://firefox-source-docs.mozilla.org/mobile/android/geckoview/consumer/web-extensions.html>
- GeckoView SessionTextInput: <https://mozilla.github.io/geckoview/javadoc/mozilla-central/org/mozilla/geckoview/SessionTextInput.html>
- GeckoView PanZoomController: <https://mozilla.github.io/geckoview/javadoc/mozilla-central/org/mozilla/geckoview/PanZoomController.html>
- GeckoView ContentDelegate: <https://mozilla.github.io/geckoview/javadoc/mozilla-central/org/mozilla/geckoview/GeckoSession.ContentDelegate.html>
- Widevine: <https://developers.google.com/widevine/drm/overview>
- Chromium 릴리스 주기: <https://chromium.googlesource.com/chromium/src/+/main/docs/process/release_cycle.md>
