# Android 브라우저 완성도 및 다음 우선순위

- 기준: Android 0.7.0
- 범위: 미구현 기능의 기존 구조 호환성 분석 및 우선순위 제안
- 이번 단계에서는 아래 기능을 구현하지 않았습니다.

## 1. 현재 기반

- 단일 `MainActivity` + Compose + `BrowserViewModel`
- 탭별 `GeckoSession`, `SessionState` 기반 복구
- SQLite + Android Keystore AES-256-GCM 데이터 계층
- 사이트 권한과 Android 런타임 권한 분리
- Windows/Rust 공통 서비스 계층은 Android APK에 아직 탑재되지 않음

## 2. 기능별 분석

### 2.1 비밀번호 및 자동완성 — 0.6.0 구현

- GeckoView Autofill/Login APIs 기반 브라우저 내부 자동완성
- 별도 `amare_passwords.db` + Android Keystore alias로 저장 경계 분리
- 사용자 선택 없는 자동 입력/제출 없음
- Android 시스템 AutofillService는 구현하지 않음
- 실제 기기 GeckoView 로그인 감지/저장/자동완성 검증은 UNVERIFIED

### 2.2 window.open 및 새 창 — 0.7.0 구현

- `onNewSession`으로 새 탭 생성
- URL allowlist, rate limit, popup prompt 차단
- opener와 새 세션 분리
- 실제 GeckoView 동작은 UNVERIFIED

### 2.3 웹 알림 — 0.7.0 구현

- `WebNotificationDelegate`, Android NotificationChannel 구현
- 사이트 권한 + Android 13+ runtime permission 연동
- origin 검증, rate limit, 안전한 click Intent
- 실제 알림 표시/클릭은 UNVERIFIED

### 2.4 WebExtension

- 관련 API: GeckoView `WebExtensionController`
- AMO 서명/허용목록/권한 표시/업데이트 정책 필요
- Windows는 WebView2 확장 모델과 호환되지 않으므로 공통 기능으로 간주하지 않음
- 우선순위: **P3** (범위와 유지보수 비용 큼)

### 2.5 AI 패널

- 기존 AI 데이터 모델/서비스 계층과의 연결이 필요
- Android 단독 구현보다 Rust service FFI/JNI adapter가 먼저 필요
- 페이지 컨텍스트 전송은 사용자 동의/로컬 우선 원칙 준수
- 우선순위: **P3**

### 2.6 Research 및 Library 연동

- `amare-store`/`amare-services`의 Library/AI/Workspace 계층 재사용 필요
- Android Rust 런타임/JNI가 선행 조건
- 대용량 데이터/백업/동기화 정책은 Windows와 동일 계약 유지 필요
- 우선순위: **P4** (공통 코어 Android 탑재 후)

## 3. 권장 순서

1. **P0**: 실제 기기 검증 + Release 서명/배포 파이프라인 (0.5.0에서 암호화/마이그레이션 테스트와 CI emulator 기반 구축, 실제 기기 검증은 남음)
2. **P1**: 비밀번호/자동완성
3. **P2**: `window.open`/새 탭, 웹 알림
4. **P3**: WebExtension, AI 패널
5. **P4**: Research/Library 및 Rust 공통 서비스 Android 탑재

## 4. 공통 원칙

- Windows/Rust 기능을 복제하지 말고 서비스 계약을 공유
- 새 민감 데이터는 Keystore AES-GCM으로 보호하고 평문 fallback 금지
- 기능별 unit + instrumentation test 추가
- 실제 기기 검증 전 PASS 표기 금지
