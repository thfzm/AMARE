# Android 새 창 및 탭 관리

- 기준: Android 0.12.0
- 관련 문서: [보안](ANDROID_SECURITY.md), [기기 테스트](ANDROID_DEVICE_TESTING.md)

## 1. 기존 탭 구조

- 단일 `MainActivity` + `BrowserViewModel`
- 탭별 `GeckoSession`
- `SessionState` 기반 back/forward/폼 상태 복원
- `SharedPreferences` URL 저장은 제거되고 SQLite session snapshot으로 이전됨
- 탭 종료 시 300ms 후 `GeckoSession.close()`

## 2. window.open / target="_blank"

GeckoView `NavigationDelegate.onNewSession(session, uri)`를 사용합니다.

```text
window.open() / target="_blank"
  -> GeckoView onNewSession
  -> Security.PopupRules URL 검증
  -> KeyedRateLimiter (opener tab 기준 5회/10초)
  -> 새 GeckoSession.open(runtime)
  -> 새 BrowserTab 추가 + 즉시 활성화
  -> GeckoSession 반환
```

- `http`, `https`, `about:blank`만 허용
- `javascript:`, `file:`, `content:`, `data:` 등은 차단
- GeckoView가 팝업 차단 prompt를 발생시키면 `PromptDelegate.onPopupPrompt`에서 dismiss
- 새 탭은 기존 탭 목록/닫기/복원 구조를 그대로 사용
- 새 세션에는 기존과 동일한 delegate, 추적 보호, 비밀번호 origin 검증 적용
- opener와 새 탭은 별도 `GeckoSession`이며 session state를 공유하지 않음
- 쿠키/프로필 저장소는 동일 GeckoRuntime 프로필을 공유 (일반 브라우저 동작)

## 3. 정책

- 새 창은 모바일 브라우저 관례에 따라 새 탭으로 열고 즉시 활성화
- 별도 창 관리 UI 없음
- 팝업 rate limit 초과 시 tab을 만들지 않고 notice 표시
- 새 세션 생성 실패 시 notice 표시 및 안전하게 요청 취소
- `about:blank`는 window.open 초기 문서로 허용

## 4. 세션 복원 호환성

- 새 창으로 만든 탭도 `tabList`에 들어가므로 기존 session snapshot에 포함
- 탭 종료/전환/앱 background 저장 경로 동일
- `onNewSession`에서 새 세션을 open하므로 crash/kill 복구 delegate 적용

## 5. 비밀번호 관리자 호환성

- 새 탭에도 `Autofill.Delegate`/`PromptDelegate`가 동일하게 연결
- 저장된 비밀번호 origin은 현재 탭 URL과 다시 비교
- cross-origin form/node domain은 기존과 동일하게 차단
- 새 창이 기존 탭의 비밀번호 prompt state를 공유하지 않음

## 6. 메모리/탭 정책

- 탭 수 제한으로 강제 종료하지 않음
- 비활성 탭은 `setActive(false)`로 GeckoView 메모리 사용을 줄임
- 탭 전환/종료/백그라운드에서 session snapshot 저장
- 다수 탭/저메모리 측정은 실기기 UNVERIFIED
- 세션 데이터가 큰 경우 크기 제한/정리 정책은 아직 없음

## 7. 검증 상태

| 항목 | 상태 |
|---|---|
| Popup URL 정책 JVM 테스트 | PASS |
| Popup rate limiter JVM 테스트 | PASS |
| window.open 실제 GeckoView 동작 | UNVERIFIED |
| target="_blank" 실제 동작 | UNVERIFIED |
| 팝업 차단 prompt 실제 동작 | UNVERIFIED |
| 새 탭 세션 복원 실제 동작 | UNVERIFIED |
| 새 탭 비밀번호 자동완성 | UNVERIFIED |
