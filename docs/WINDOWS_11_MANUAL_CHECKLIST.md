# AMARE Windows 11 실기기 수동 검증표

- 대상: Windows 11 build 22000+, x64
- 기록 원칙: 실행하지 않은 항목은 **UNVERIFIED**, 실패는 **FAIL**, 성공은 **PASS**
- 각 실행 전 환경과 로그 경로를 기록합니다.

## 환경 기록

```text
Date:
Tester:
Windows version/build:
CPU / RAM / GPU:
WebView2 Runtime:
Windows App SDK Runtime:
AMARE build (commit):
DPI / monitor layout:
Data root: %LOCALAPPDATA%\AMARE
Log: %LOCALAPPDATA%\AMARE\logs\amare-YYYYMMDD.log
```

## 결과 코드

| Status | 의미 |
|---|---|
| PASS | 절차를 실행하고 기대 결과 확인 |
| FAIL | 실행했으나 기대 결과와 다름 |
| UNVERIFIED | 실행하지 않았거나 판정 불가 |
| SKIPPED | 의도적으로 제외 |

## 1. 실행/렌더링

| ID | 테스트 | 절차 | 기대 결과 | 실제 결과 | Status | 환경/로그 | 문제 |
|---|---|---|---|---|---|---|---|
| W11-01 | 첫 실행 | RC exe 실행 | 창과 새 탭 표시, crash 없음 |  |  |  |  |
| W11-02 | WebView2 초기화 | 새 탭 로딩 확인 | local new tab HTML 표시 |  |  |  |  |
| W11-03 | 실제 웹 탐색 | 주소창에 `https://example.com` | 실제 페이지 렌더링 |  |  |  |  |
| W11-04 | HTTPS 표시 | HTTPS 사이트 접속 | URL/자물쇠 상태 정상 |  |  |  |  |
| W11-05 | URL/제목 | 다른 페이지 이동 | 주소/탭 제목 갱신 |  |  |  |  |
| W11-06 | 탐색 버튼 | back/forward/reload/stop | 실제 WebView2 상태와 일치 |  |  |  |  |
| W11-07 | 로딩 오류 | 잘못된 host 접속 | 오류 상태 표시, crash 없음 |  |  |  |  |

## 2. 탭/프로필

| ID | 테스트 | 절차 | 기대 결과 | 실제 결과 | Status | 환경/로그 | 문제 |
|---|---|---|---|---|---|---|---|
| W11-08 | 탭 생성 | Ctrl+T 또는 + 버튼 5회 | 5개 탭 생성/전환 |  |  |  |  |
| W11-09 | 탭 10/20 | 10개, 20개 생성 | UI/입력 응답 유지 |  |  |  |  |
| W11-10 | 탭 닫기 | 활성/비활성/마지막 탭 | 올바른 탭 선택, 마지막은 새 탭 |  |  |  |  |
| W11-11 | 탭 순서 | drag reorder | 순서/모델/세션 일치 |  |  |  |  |
| W11-12 | 프로필 생성 | Profile menu → New | 새 프로필 활성화 |  |  |  |  |
| W11-13 | 프로필 전환 | A↔B 전환 | 이전 WebView2 종료, 새 env 생성 |  |  |  |  |
| W11-14 | 쿠키 분리 | A에서 쿠키 설정, B 확인 | B에서 쿠키 없음 |  |  |  |  |
| W11-15 | LocalStorage 분리 | A에서 localStorage 설정, B 확인 | B에서 없음 |  |  |  |  |
| W11-16 | IndexedDB 분리 | A에서 IDB 저장, B 확인 | B에서 없음 |  |  |  |  |
| W11-17 | 로그인 분리 | A 로그인, B 접속 | B는 비로그인 |  |  |  |  |
| W11-18 | 프로필 삭제 | active profile 삭제 | DB 삭제, WebView2 폴더 정리/pending |  |  |  |  |
| W11-19 | 잠금 cleanup | WebView2 폴더 잠금 상태로 삭제 | pending 기록, 재실행 시 재시도 |  |  |  |  |

## 3. 세션/입력/UI

| ID | 테스트 | 절차 | 기대 결과 | 실제 결과 | Status | 환경/로그 | 문제 |
|---|---|---|---|---|---|---|---|
| W11-20 | 정상 종료 복원 | 탭 3개 후 종료/재실행 | 3개 탭/active 복원 |  |  |  |  |
| W11-21 | 강제 종료 복원 | 탭 변경 후 강제 종료/재실행 | 마지막 성공 저장 복원 |  |  |  |  |
| W11-22 | Ctrl+L | 페이지 focus 상태에서 실행 | 주소창 focus/select all |  |  |  |  |
| W11-23 | Ctrl+T/W/R | 단축키 실행 | 새 탭/닫기/reload |  |  |  |  |
| W11-24 | Ctrl+Shift+T | 탭 닫은 뒤 실행 | 닫은 탭 복원 |  |  |  |  |
| W11-25 | Alt+Left/Right | 탐색 후 실행 | 뒤로/앞으로 |  |  |  |  |
| W11-26 | 한글 IME | 주소창/페이지 입력 | 한글 조합/Enter 정상 |  |  |  |  |
| W11-27 | 포커스 전환 | 주소창↔페이지 클릭 | 키 입력 대상 정상 |  |  |  |  |
| W11-28 | 창 크기/DPI | resize, DPI 125/150% | WebView2 크기/선명도 정상 |  |  |  |  |
| W11-29 | 멀티모니터 | 창을 다른 monitor로 이동 | 재배치/DPI 정상 |  |  |  |  |

## 4. 다운로드/보안/성능

| ID | 테스트 | 절차 | 기대 결과 | 실제 결과 | Status | 환경/로그 | 문제 |
|---|---|---|---|---|---|---|---|
| W11-30 | 다운로드 | local/공개 파일 다운로드 | 감지, 안전한 파일명/경로 |  |  |  |  |
| W11-31 | 외부 protocol | `mailto:` 링크 클릭 | 확인 dialog 후 실행/취소 |  |  |  |  |
| W11-32 | 인증서 오류 | 공개 인증서 오류 테스트 사이트 | 우회 없이 오류 처리 |  |  |  |  |
| W11-33 | 초기 실행 시간 | 시작→첫 탭 ready 5회 | 시간 기록 |  |  |  |  |
| W11-34 | 탭 생성/전환 | 5/10/20탭 | 시간 기록 |  |  |  |  |
| W11-35 | 메모리 | 5/10/20/30탭 안정화 후 | process working set 기록 |  |  |  |  |
| W11-36 | 종료 정리 | 모든 탭 닫고 앱 종료 | WebView2 process 잔류 확인 |  |  |  |  |

## 5. 결과 요약

```text
PASS:
FAIL:
UNVERIFIED:
SKIPPED:
Critical issues:
Logs:
Next action:
```

## 6. AMARE 0.15.0 추가 기능

| ID | 테스트 | 절차 | 기대 결과 | 실제 결과 | Status | 환경/로그 | 문제 |
|---|---|---|---|---|---|---|---|
| W11-37 | 창 아이콘 | 작업 표시줄/Alt+Tab 확인 | AMARE 로고 아이콘 표시 |  |  |  |  |
| W11-38 | 주소 자동완성 | 주소창에 방문 기록/북마크 일부 입력 | 로컬 제안 표시, 선택 시 이동 |  |  |  |  |
| W11-39 | 북마크 | 현재 페이지 북마크 추가/목록/삭제 | 재시작 후에도 유지 |  |  |  |  |
| W11-40 | 방문 기록 | 페이지 방문 후 History 열기 | 제목/URL 표시, 클릭 이동 |  |  |  |  |
| W11-41 | 다운로드 | 파일 다운로드 완료 후 앱 재시작 | 완료 상태/파일 열기 유지 |  |  |  |  |
| W11-42 | Reader | 본문 페이지에서 Reader Mode | 읽기 화면, 글꼴/크기/테마/원문 복귀 |  |  |  |  |
| W11-43 | Find | Ctrl+F로 한글/영문 검색 | 결과 수/이전/다음/종료 |  |  |  |  |
| W11-44 | AI 패널 | AI 버튼 → ChatGPT/Gemini/Claude | 로그인/탐색/닫기/재열기 |  |  |  |  |
| W11-45 | AI 문맥 | Page info/Article 복사 | 클립보드에만 복사, 자동 전송 없음 |  |  |  |  |
| W11-46 | AI/일반 탭 분리 | AI 로그인 후 일반 브라우저 확인 | 저장소/로그인 상태 분리 |  |  |  |  |
| W11-47 | 테마 | System/Light/Dark 변경 | 즉시 반영, 재시작 후 유지 |  |  |  |  |

## 0.17 update / API AI
- [ ] GitHub Release가 실제로 발행된 뒤 `Check for updates…` → 다운로드 → SHA-256 → 적용/rollback 확인
- [ ] `API AI settings…`에서 실제 key 저장/삭제 후 test connection 확인
- [ ] API AI 요청에서 page context 사용자 승인/취소 동작 확인
