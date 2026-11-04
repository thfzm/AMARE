# Android 1.0.0 출시 기준 및 체크리스트

- 기준: Android 0.12.0
- 현재 판정: **출시 불가 / UNVERIFIED·BLOCKED 항목 존재**

## 1. 필수 기능

- [x] HTTPS 기본 브라우징
- [x] 탭 생성/전환/종료
- [x] SessionState 탭 복구
- [x] 최근 닫은 탭 복구(메모리)
- [x] 비밀번호 저장/자동완성 코드
- [x] 웹 알림 코드
- [x] 다운로드/열기/위험 파일 경고
- [x] 데이터 관리 UI
- [x] HTTPS-only/외부 URL 검증
- [x] Keystore AES-GCM 코드
- [x] DB migration 코드
- [x] Debug/Release/AAB 빌드
- [x] JVM 33 tests / Lint 0 errors
- [x] Instrumentation 12 tests compile
- [x] Linux/Windows/Android CI

## 2. 출시 전 실제 실행 필수 및 상태

| 항목 | 상태 | 근거 |
|---|---|---|
| 실기기 연결 | **BLOCKED** | ADB 기기 없음 |
| GeckoView 렌더링/입력/SSL | UNVERIFIED | 실기기 필요 |
| HTTPS-only/Mixed Content | UNVERIFIED | 실기기 필요 |
| window.open/target=_blank | UNVERIFIED | 실기기 필요 |
| 비밀번호 감지/자동완성 | UNVERIFIED | 실기기 필요 |
| 웹 알림/클릭/재시작 | UNVERIFIED | 실기기 필요 |
| 데이터 삭제 각 범위 | UNVERIFIED | 실기기 필요 |
| 기본 브라우저 role/외부 링크 | UNVERIFIED | 실기기 필요 |
| 다운로드 파일 열기/중복명 | UNVERIFIED | 실기기 필요 |
| 세션 복구/백그라운드/강제 종료 | UNVERIFIED | 실기기 필요 |
| Keystore 실제 AES-GCM/키 무효화 | UNVERIFIED | instrumentation compile-only |
| DB migration/rollback 실제 실행 | UNVERIFIED | instrumentation compile-only |
| 실제 기기 성능 측정 | UNVERIFIED | 실기기 없음 |
| Release 서명 | **BLOCKED** | 배포 키 없음/사용자 승인 필요 |
| signed APK/AAB 검증 | BLOCKED | Release unsigned |
| 설치/업데이트 호환성 | BLOCKED | signed release 없음 |

Instrumentation:

- 테스트 APK compile: **PASS**
- 실제 실행: **UNVERIFIED**
- isolated test DB/alias: 코드상 PASS
- `tools/run-android-device-tests.sh`는 기기 미연결 시 UNVERIFIED report 생성

## 3. 출시 차단 요소

- 실기기 통합 테스트
- Keystore/DB migration 실제 실행
- Release 서명
- 성능 측정
- 설치/업데이트 검증

## 4. 1.0.0 제외 범위

- WebExtension
- AI 패널
- Research/Library Android 연동
- Android 시스템 전체 AutofillService
- Play Store 배포/심사

위 제외 기능은 1.0.0 필수 조건으로 간주하지 않습니다.
