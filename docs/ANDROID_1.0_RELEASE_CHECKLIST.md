# Android 1.0.0 출시 기준 및 체크리스트

- 기준: Android 0.12.0
- 현재 판정: **출시 불가 / Release 서명 및 일부 실기기 UI 시나리오 미검증**
- 실기기 자동 검증: **PASS** (Lenovo TB138FC, Android 14/SDK 34, arm64-v8a)
- 상세 결과: `artifacts/android/device-test/results/AMARE-0.12.0-real-device-validation.md`

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
- [x] Instrumentation 13 tests compile
- [x] Instrumentation 13/13 real-device PASS
- [x] Linux/Windows/Android CI

## 2. 출시 전 실제 실행 필수 및 상태

| 항목 | 상태 | 근거 |
|---|---|---|
| 실기기 연결 | **PASS** | Lenovo TB138FC, Android 14 |
| GeckoView 렌더링 | **PASS** | DuckDuckGo/Example Domain HTTPS 렌더링 |
| GeckoView 입력/SSL 오류 처리 | UNVERIFIED | 주소창 입력/인증서 오류 시나리오 미실행 |
| HTTPS-only/Mixed Content | PASS(HTTPS 로드) / UNVERIFIED(차단·Mixed) | HTTPS 페이지 로드만 확인 |
| window.open/target=_blank | UNVERIFIED | UI 팝업 시나리오 미실행 |
| 비밀번호 감지/자동완성 | UNVERIFIED | Repository instrumentation PASS, GeckoView UI 미검증 |
| 웹 알림/클릭/재시작 | UNVERIFIED | 미실행 |
| 데이터 삭제 각 범위 | UNVERIFIED | 미실행 |
| 기본 브라우저 role | UNVERIFIED | Chrome이 role 보유; 사용자 설정 변경 안 함 |
| 외부 링크 | **PASS** (명시적 ACTION_VIEW) / UNVERIFIED (기본 브라우저 암시적 처리) | `https://example.com/` 새 탭 렌더링 |
| 다운로드 파일 열기/중복명 | UNVERIFIED | Downloads 화면만 확인 |
| 세션 복구/강제 종료 | **PASS** | force-stop 후 기존 3탭 복원 |
| 탭 생성/전환/종료 | **PASS** | 테스트 탭 생성/종료 후 기존 탭 유지 |
| Bookmarks/History/Downloads/Settings 화면 | **PASS** | 각 화면 진입 확인 |
| Keystore 실제 AES-GCM/키 무효화 reporting | **PASS** (repository test) / UNVERIFIED (UI) | instrumentation 13/13 실행 |
| DB migration/신규 설치 평문 테이블/빈 legacy 보존 | **PASS** | instrumentation 13/13 실행 |
| 실제 기기 성능 측정 | PASS(cold start 1,031 ms) / UNVERIFIED(지속 부하) | 단일 cold launch만 측정 |
| Release 서명 | **BLOCKED** | 배포 키 없음/사용자 승인 필요 |
| signed APK/AAB 검증 | **BLOCKED** | Release unsigned |
| 설치/업데이트 호환성 | **BLOCKED** | signed release 없음 |

Instrumentation:

- 테스트 APK compile: **PASS**
- 실제 실행: **PASS** (13/13, 실기기)
- 결과 보고서: `artifacts/android/device-test/results/device-test-20261105-020023.md`
- isolated test DB/alias: **PASS**
- 수정한 결함:
  - 신규 DB가 legacy plaintext `site_permissions`를 생성하던 문제
  - 비밀번호 손상 payload 테스트가 DB 생성 전에 raw SQLite를 열던 문제

## 3. 출시 차단 요소

- Release 서명 및 signed APK/AAB 검증
- 남은 실기기 UI 시나리오(팝업, 웹 알림, 비밀번호 UI, 데이터 삭제, 기본 브라우저 role)
- 지속 부하/메모리/장시간 성능
- signed-release 설치/업데이트 호환성

## 4. 1.0.0 제외 범위

- WebExtension
- AI 패널
- Research/Library Android 연동
- Android 시스템 전체 AutofillService
- Play Store 배포/심사

위 제외 기능은 1.0.0 필수 조건으로 간주하지 않습니다.
