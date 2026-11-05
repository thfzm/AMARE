# Android 1.0.0 출시 기준 및 체크리스트

- 기준: Android 0.13.0
- 현재 판정: **출시 불가 / 0.13.0 실기기 검증 BLOCKED, Release 서명 BLOCKED, 일부 고급 UI UNVERIFIED**
- 0.12.0 실기기 기준선: Lenovo TB138FC, Android 14/SDK 34, Instrumentation **13/13 PASS**
- 0.13.0 기기 상태: 태블릿 Tailscale 오프라인으로 **BLOCKED**
- 상세 개발 보고서: `artifacts/android/release/AMARE-0.13.0-development-report.md`

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
- [x] JVM 40 tests / Lint 0 errors
- [x] Instrumentation 13 tests compile
- [x] 0.13.0 주소창 로컬 제안/검색엔진 선택/테마 선택
- [x] Linux/Windows/Android CI

## 2. 0.13.0 변경 검증

| 항목 | 상태 | 근거 |
|---|---|---|
| 주소창 로컬 제안 로직 | **PASS** | JVM `AddressSuggestionsTest` 4/4 |
| 검색엔진 선택/URL 정규화 | **PASS** | JVM `SearchEngineTest` 3/3, `UrlNormalizerTest` 5/5 |
| Material You/시스템·라이트·다크 테마 | 컴파일 PASS / 기기 **BLOCKED** | `MainActivity`, `Theme.AMARE` |
| 로딩 중 중지 버튼 전환 | 컴파일 PASS / 기기 **BLOCKED** | `BrowserToolbar` |
| 주소창 제안 UI/검색엔진 설정 UI | 컴파일 PASS / 기기 **BLOCKED** | Compose build PASS |
| JVM 전체 회귀 | **PASS** | 40/40 |
| Lint | **PASS** | 0 errors / 8 warnings |
| 실기기 Instrumentation | **BLOCKED** | Tailscale/ADB 오프라인 |
| 0.12.0 실기기 회귀 기준 | **PASS** | 13/13, prior report |

## 3. 0.12.0 실기기 기준선 (회귀 참고)

| 항목 | 상태 | 근거 |
|---|---|---|
| GeckoView HTTPS 렌더링 | **PASS** | DuckDuckGo/Example Domain |
| 세션 복구/강제 종료 | **PASS** | force-stop 후 기존 3탭 복원 |
| 탭 생성/전환/종료 | **PASS** | 테스트 탭 생성/종료 |
| 명시적 외부 ACTION_VIEW | **PASS** | `https://example.com/` |
| Keystore AES-GCM/키 무효화 reporting | **PASS** | instrumentation 13/13 |
| DB migration/신규 설치 평문 테이블/빈 legacy 보존 | **PASS** | instrumentation 13/13 |
| 주소창 입력/뒤로/앞으로/새로고침 | UNVERIFIED | 0.12 UI smoke 미완료 |
| window.open/웹 알림/자동완성 UI | UNVERIFIED | 미실행 |
| 기본 브라우저 role | UNVERIFIED | Chrome role 유지 |
| Release 서명 | **BLOCKED** | 배포 키 없음/사용자 승인 필요 |
| signed APK/AAB 검증 | **BLOCKED** | Release unsigned |
| 설치/업데이트 호환성 | **BLOCKED** | signed release 없음 |

## 4. 출시 차단 요소

- 0.13.0 실기기 재검증(주소창 제안, 검색엔진, 테마, 중지 UX)
- Release 서명 및 signed APK/AAB 검증
- 남은 실기기 UI 시나리오(팝업, 웹 알림, 비밀번호 UI, 데이터 삭제, 기본 브라우저 role)
- 지속 부하/메모리/장시간 성능
- signed-release 설치/업데이트 호환성

## 5. 1.0.0 제외 범위

- WebExtension
- AI 패널/Agent 전체 구현
- Research/Library Android 연동
- Android 시스템 전체 AutofillService
- Play Store 배포/심사

위 제외 기능은 1.0.0 필수 조건으로 간주하지 않습니다.
