# Android 브라우저 완성도 및 다음 우선순위

- 기준: Android 0.5.0
- 범위: 미구현 기능의 기존 구조 호환성 분석 및 우선순위 제안
- 이번 단계에서는 아래 기능을 구현하지 않았습니다.

## 1. 현재 기반

- 단일 `MainActivity` + Compose + `BrowserViewModel`
- 탭별 `GeckoSession`, `SessionState` 기반 복구
- SQLite + Android Keystore AES-256-GCM 데이터 계층
- 사이트 권한과 Android 런타임 권한 분리
- Windows/Rust 공통 서비스 계층은 Android APK에 아직 탑재되지 않음

## 2. 기능별 분석

### 2.1 비밀번호 및 자동완성

- 관련 API: GeckoView Autofill/Login APIs, Android Autofill Framework
- 저장 위치: GeckoView profile에 맡기지 않고 AMARE 자체 Secret 저장소를 사용할 경우
  Keystore AES-GCM + row 단위 key version 필요
- Windows의 `amare-security` SecretRepository/KeyProtector 의미론과 맞춰야 함
- 위험: credential 평문 로그/백업, 잘못된 자동 채움, 사이트 오인
- 우선순위: **P1** (보안 민감도 높음)

### 2.2 window.open 및 새 창

- 관련 API: `GeckoSession.NavigationDelegate.onNewSession`
- 현재 탭 모델을 재사용해 새 `GeckoSession`을 생성하고 새 탭으로 추가 가능
- 팝업 차단 정책, 사용자 제스처, opener 관계, 새 창 크기/위치 정책 필요
- Windows WebView2의 NewWindowRequested 처리와 사용자 경험을 맞춰야 함
- 우선순위: **P2**

### 2.3 웹 알림

- 관련 API: `WebNotificationDelegate`, Android NotificationChannel
- 사이트 권한은 이미 저장되지만 실제 알림 표시는 미구현
- Android 13+ `POST_NOTIFICATIONS`, 채널, 탭/사이트별 그룹화, 클릭 시 탭 활성화 필요
- 우선순위: **P2**

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
