# Android 세션 복구 및 보안 개선 설계

- 기준: Android 0.3.0 세션 복구/Keystore 구현
- 범위: 우선순위 4(세션 복구)와 5(보안)
- 원칙: GeckoView API를 우선 사용하고 Windows/Linux/Rust 코어는 이번 단계에서 변경하지 않음

## 0. 0.3.0 구현 반영

- `GeckoSession.SessionState`를 탭별로 저장하고 `restoreState()`로 복원
- SQLite transaction으로 `session_snapshots`/`session_tabs` 교체
- clean shutdown marker, unexpected exit 알림, URL fallback 구현
- 세션 payload(URL/제목/SessionState)는 Android Keystore AES-256-GCM으로 암호화
- `GeckoSession` crash/kill 시 새 세션 생성 및 SessionState/URL 복구, 반복 crash 제한
- Kotlin `AndroidKeystoreProtector` 구현, 키 무효화/삭제 오류 처리
- Rust `AndroidKeystoreKeyProtector` JNI 연결은 Rust Android 런타임이 APK에 없어 보류
- 에뮬레이터/실기기 검증은 미실행

## 1. 현재 상태

### 세션/탭

- `BrowserViewModel`이 열린 탭의 `GeckoSession`을 메모리에 보관
- `SharedPreferences`에 URL 배열과 활성 탭 index만 저장
- 화면 회전은 ViewModel로 유지
- 앱 프로세스 강제 종료 후에는 URL/활성 탭만 복원되고 페이지 히스토리는 사라짐
- GeckoView `ContentDelegate.onCrash`/`onKill`은 아직 처리하지 않음
- 탭 최대 복원 수: 8

### 보안/개인정보

- 웹사이트 권한은 SQLite에 origin + permission 단위로 저장
- 카메라/마이크/위치 Android 런타임 권한은 별도로 요청
- SSL 오류는 `onLoadError`에서 null을 반환해 로드를 중단하며 자동 우회 없음
- 세션 추적 보호 `useTrackingProtection=true`
- Runtime GPC(Global Privacy Control) 활성화
- 북마크/방문 기록/다운로드 메타데이터 DB는 현재 평문 SQLite
- Rust `AndroidKeystoreKeyProtector`는 여전히 `Unsupported` stub

## 2. 세션 복구 설계

### 목표

- 앱 강제 종료/프로세스 사망 후 전체 탭과 각 탭의 back/forward 히스토리 복원
- 정상 종료와 비정상 종료를 구분
- 복구 실패 시 URL 로드로 안전하게 fallback
- 개인정보 데이터는 저장 정책과 함께 관리

### 저장 모델

새 SQLite 테이블:

```sql
CREATE TABLE session_snapshots (
  id INTEGER PRIMARY KEY,
  clean_shutdown INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);

CREATE TABLE session_tabs (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  snapshot_id INTEGER NOT NULL,
  position INTEGER NOT NULL,
  url TEXT NOT NULL,
  title TEXT NOT NULL,
  session_state BLOB,
  active INTEGER NOT NULL,
  crashed INTEGER NOT NULL DEFAULT 0
);
```

- `session_state`는 `GeckoSession.SessionState.toString()` 결과
- 전체 저장은 transaction으로 수행
- 앱 시작 시 `clean_shutdown=0`이면 비정상 종료로 간주하고 복구 배너 표시

### 저장 시점

1. `ProgressDelegate.onSessionStateChange(session, state)`에서 탭별 state 갱신
2. `onPageStop`, 탭 전환, 새 탭, 탭 종료 시 메모리 스냅샷 갱신
3. `Activity.onStop`에서 `session.flushSessionState()` 호출 후 DB 저장
4. 정상 종료 경로에서 `clean_shutdown=1`
5. 쓰기는 단일 백그라운드 executor + transaction, 실패 시 이전 스냅샷 유지

### 복원 절차

1. DB에서 snapshot과 탭 목록 로드
2. 각 탭에 새 `GeckoSession` 생성 및 `open(runtime)`
3. `SessionState.fromString(saved)` 성공 시 `session.restoreState(state)`
4. 실패/손상 시 저장 URL로 `loadUri(url)`
5. 활성 탭 index 복원, 실패 탭은 오류 배너 표시
6. 복원 완료 후 `clean_shutdown=1`로 갱신

### Crash/Kill 처리

- `ContentDelegate.onCrash`/`onKill`: 해당 session을 closed로 표시하고 복구 대상으로 기록
- 새 session을 열고 마지막 DB snapshot을 `restoreState`로 적용
- 반복 crash 방지를 위해 같은 탭의 자동 복구 횟수를 제한
- 복구 불가 시 URL reload 후 사용자에게 알림

### 테스트 계획

- URL/활성 탭 복원: JVM 로직 테스트 + 기기 instrumentation
- back/forward 히스토리 복원: 기기에서 3페이지 이동 후 `adb shell am force-stop` → 재실행
- 회전/백그라운드/저메모리: 실제 기기
- 손상 SessionState, 빈 snapshot, 8탭 초과, 중복 URL
- 검증 전에는 PASS로 표시하지 않음

## 3. Android Keystore 및 개인정보 보호 설계

### Keystore 적용

- AndroidKeyStore에 AES-256-GCM 키 생성, alias 예: `amare_browser_data_v1`
- StrongBox 사용 가능 시 선택, 불가 시 TEE-backed key로 fallback
- 키는 export하지 않고 Keystore에서만 사용
- 레코드마다 12-byte random IV, AAD에 스키마 버전/레코드 종류 사용
- 저장 형식: `version || iv || ciphertext`
- 키 무효화/삭제 시 복호화 실패를 명시적으로 처리하고 데이터 초기화 안내
- 현재 Rust stub은 건드리지 않고 Android Kotlin 계층에서 구현한 뒤, 공통 서비스가 필요해질 때 JNI/UniFFI로 연결

### 무엇을 암호화할지

- 우선 암호화: 향후 비밀번호/토큰/AI API 키/민감한 설정
- 북마크/방문 기록 검색을 유지해야 하므로 전체 암호화는 검색 인덱스 설계가 필요
- 1차 구현은 민감 credential만 Keystore로 보호하고, history/bookmark는 앱 전용 저장소 + 백업 제외 정책 유지
- 전체 DB 암호화가 필요하면 SQLCipher 같은 검증된 라이브러리 도입 여부를 별도 결정

### 추적 방지/프라이버시

- GeckoView tracking protection 및 GPC는 이미 활성화
- 후속 설정 UI: 추적 보호 강도, 쿠키/사이트 데이터 삭제, Do Not Track/GPC 표시
- `StorageController.clearData`로 사이트 데이터 삭제 기능 제공
- 권한 기본값은 prompt/deny이며 자동 allow 없음
- SSL 인증서 오류는 계속 차단하고 사용자 예외 허용을 기본 제공하지 않음

### 완료 기준

- Keystore round-trip 및 키 무효화 테스트
- 앱 재설치/키 삭제 시 데이터 처리 확인
- DB/다운로드 경로에 민감정보 평문 저장 없음
- 실제 기기에서 세션 복구와 권한 흐름 검증 완료 후 PASS 표기
