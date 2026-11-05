# Android 1.0.0 출시 기준 및 체크리스트

- 기준: Android 0.14.0
- 현재 판정: **출시 불가 / 0.14.0 실기기 검증 BLOCKED, Release 서명 BLOCKED, 일부 UI UNVERIFIED**
- 0.12.0 실기기 기준선: Lenovo TB138FC, Android 14/SDK 34, Instrumentation **13/13 PASS**
- 0.14.0 기기 상태: 태블릿 Tailscale 오프라인으로 **BLOCKED**
- 상세 개발 보고서: `artifacts/android/release/AMARE-0.14.0-development-report.md`

## 1. 필수 기능

- [x] HTTPS 기본 브라우징
- [x] 탭 생성/전환/종료/세션 복구
- [x] 북마크/방문 기록/다운로드/사이트 권한
- [x] 비밀번호 저장/자동완성 코드
- [x] 웹 알림/팝업 코드
- [x] HTTPS-only/외부 URL 검증
- [x] Keystore AES-GCM/DB migration
- [x] Debug/Release/AAB 빌드
- [x] JVM 44 tests / Lint 0 errors
- [x] Instrumentation 13 tests compile
- [x] Reader Mode 구현 (GeckoView PageExtractionController)
- [x] Find in Page 구현 (GeckoView SessionFinder)
- [x] AI Web Panel 최소 구조 (웹 AI + 명시적 문맥 복사)
- [x] Linux/Windows/Android CI

## 2. 0.14.0 변경 검증

| 항목 | 상태 | 근거 |
|---|---|---|
| Reader Markdown 파싱/보안 URL | **PASS** | JVM ReaderMarkdownTest 4/4 |
| Reader 진입/종료/테마/글꼴/크기 UI | 컴파일 PASS / 기기 **BLOCKED** | Compose build |
| Reader 실제 페이지 추출 | **BLOCKED** | 기기 오프라인 |
| Find API/UI/결과 이동 | 컴파일 PASS / 기기 **BLOCKED** | GeckoView SessionFinder |
| Find 실제 검색/한글/대소문자 | **BLOCKED** | 기기 오프라인 |
| AI 웹 패널 열기/닫기 | 컴파일 PASS / 기기 **BLOCKED** | Separate GeckoSession |
| AI 서비스 선택/로그인/세션 유지 | **BLOCKED** | 기기/네트워크 필요 |
| AI 페이지 문맥 복사 | 컴파일 PASS / 기기 **BLOCKED** | 명시적 클립보드 복사 |
| API 방식 | **미구현 (P2)** | 이번 버전 범위 아님 |
| JVM 전체 회귀 | **PASS** | 44/44 |
| Lint | **PASS** | 0 errors / 8 warnings |
| 실기기 Instrumentation | **BLOCKED** | 연결된 기기 없음 |
| 0.12.0 실기기 회귀 기준 | **PASS** | 13/13, prior report |

## 3. 0.12.0 실기기 기준선 (회귀 참고)

| 항목 | 상태 | 근거 |
|---|---|---|
| GeckoView HTTPS 렌더링 | **PASS** | DuckDuckGo/Example Domain |
| 세션 복구/강제 종료 | **PASS** | force-stop 후 기존 3탭 복원 |
| 탭 생성/전환/종료 | **PASS** | 테스트 탭 생성/종료 |
| 명시적 외부 ACTION_VIEW | **PASS** | `https://example.com/` |
| Keystore AES-GCM/키 무효화 reporting | **PASS** | instrumentation 13/13 |
| DB migration/신규 설치 평문/빈 legacy 보존 | **PASS** | instrumentation 13/13 |
| 주소창 입력/뒤로/앞으로/새로고침 | UNVERIFIED | 0.12 UI smoke 미완료 |
| window.open/웹 알림/자동완성 UI | UNVERIFIED | 미실행 |
| 기본 브라우저 role | UNVERIFIED | Chrome role 유지 |
| Release 서명 | **BLOCKED** | 배포 키 없음/사용자 승인 필요 |
| signed APK/AAB 검증 | **BLOCKED** | Release unsigned |
| 설치/업데이트 호환성 | **BLOCKED** | signed release 없음 |

## 4. 출시 차단 요소

- 0.14.0 실기기 재검증(Reader/Find/AI 패널)
- Release 서명 및 signed APK/AAB 검증
- 남은 실기기 UI 시나리오(팝업, 웹 알림, 비밀번호 UI, 데이터 삭제, 기본 브라우저 role)
- 지속 부하/메모리/장시간 성능
- signed-release 설치/업데이트 호환성

## 5. 1.0.0 제외 범위

- WebExtension
- AI API provider/Agent 전체 구현
- 다중 탭 AI 비교/Research
- Research/Library Android 연동
- Android 시스템 전체 AutofillService
- Play Store 배포/심사

위 제외 기능은 1.0.0 필수 조건으로 간주하지 않습니다.
