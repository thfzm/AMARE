# Android 통합 테스트 계획

- 기준: Android 0.12.0
- 원칙: 실제 실행하지 않은 항목은 PASS로 표시하지 않음
- 관련 문서: [기기 테스트](ANDROID_DEVICE_TESTING.md), [보안](ANDROID_SECURITY.md), [Release](ANDROID_RELEASE.md)

## 1. 자동화 수준 구분

| 수준 | 의미 |
|---|---|
| JVM PASS | Linux JVM에서 실제 실행 완료 |
| Instrumentation compile | Android 테스트 APK 컴파일 완료, 실행 아님 |
| Instrumentation PASS | Emulator/실기기에서 실제 실행 완료 |
| Device PASS | 실제 Android 기기에서 실제 실행 완료 |
| UNVERIFIED | 실행 환경 없음 또는 결과 미확인 |

현재 환경:

- JVM tests: 33/33 PASS
- Instrumentation: **13/13 실기기 PASS**
- 실제 기기: Lenovo TB138FC, Android 14/SDK 34, arm64-v8a
- hosted CI emulator: KVM 없음, ADB 불안정 (CI는 compile-only 유지)
- self-hosted KVM runner: 미확보

## 2. 통합 시나리오

### A. 브라우저 기본

1. 앱 실행
2. GeckoView 초기 화면 로드
3. HTTPS 사이트 접속
4. 입력 필드/스크롤/뒤로/앞으로/새로고침
5. 탭 생성/전환/종료
6. 앱 강제 종료 후 재실행
7. 전체 탭, 활성 탭, back/forward 히스토리 복원

자동화:

- 탭/세션 정책과 SessionRecoveryPolicy: JVM PASS
- Session snapshot DB: **Instrumentation PASS**
- 실제 GeckoView 렌더링: **Device PASS** (DuckDuckGo/example.com)
- 실제 GeckoView 입력: Device UNVERIFIED
- 세션 복구/force-stop: **Device PASS**
- HTTPS-only 실제 차단/해제: Device UNVERIFIED

### B. 새 창

1. `window.open()` 테스트 페이지
2. `target="_blank"` 링크
3. 사용자 동작 없는 팝업
4. rate limit 초과
5. 새 탭 생성/전환/종료
6. 새 탭이 포함된 세션 재실행 복구

자동화:

- PopupRules/KeyedRateLimiter: JVM PASS
- onNewSession 실제 세션 생성: Device UNVERIFIED
- 팝업 차단 prompt: Device UNVERIFIED

### C. 웹 알림

1. HTTPS 사이트에서 Notification 권한 요청
2. Allow / Deny / Reset
3. Android 13+ POST_NOTIFICATIONS 허용/거부
4. 실제 알림 표시
5. 알림 클릭 → 올바른 origin 탭
6. 앱 종료 후 알림 클릭
7. 알림 스와이프 삭제
8. 잠금 화면에서 내용 노출 여부

자동화:

- NotificationRules/KeyedRateLimiter: JVM PASS
- site permission 저장: JVM policy + existing repo tests compile
- NotificationManager/click/delete receiver: Device UNVERIFIED

### D. 비밀번호

1. 로그인 폼 감지
2. Save / Update / Not now / Never
3. 동일 사이트 여러 계정
4. 계정 선택 후 Fill
5. cross-origin iframe 차단
6. 새 탭에서 origin 검증
7. 앱 재실행 후 저장 데이터 유지

자동화:

- PasswordRules: JVM PASS
- PasswordRepository encryption/list/update/delete: **Instrumentation PASS**
- GeckoView Autofill/Login prompt: Device UNVERIFIED

### E. HTTPS/설정

1. 기본 HTTPS-only 상태에서 HTTP 주소 차단
2. 보안 패널에서 HTTPS-only 해제 후 HTTP 접속
3. HTTPS-only 재활성화
4. 인증서 오류 사이트가 자동 우회되지 않는지 확인
5. Mixed Content 페이지 동작 확인

자동화:

- 설정 저장/런타임 API 호출: 코드 수준 확인
- 실제 HTTP/HTTPS/Mixed Content/인증서 동작: Device UNVERIFIED

### F. 데이터 관리/기본 브라우저

1. 방문 기록만 삭제하고 쿠키가 유지되는지 확인
2. 쿠키/캐시/사이트 저장 데이터를 각각 삭제
3. 사이트 권한 초기화
4. 현재 사이트 데이터 삭제
5. 비밀번호 전체 삭제
6. 전체 브라우저 데이터 삭제 후 새 홈 탭
7. 기본 브라우저 role 요청
8. 외부 https/http Intent 처리
9. 최근 닫은 탭 Reopen

자동화:

- 외부 URL scheme/host 검증: JVM PASS
- 명시적 ACTION_VIEW 외부 링크: **Device PASS**
- 암시적 기본 브라우저 role: Device UNVERIFIED
- 데이터 삭제/reopen: Device UNVERIFIED

### G. 보안/데이터

1. Keystore AES-GCM round-trip
2. IV/ciphertext/AAD 변조
3. 키 삭제/무효화
4. DB v1/v2 → v3 migration
5. migration 중 종료 및 재실행
6. 신규 설치 평문 테이블 미생성
7. 암호화 데이터 재실행 후 조회

자동화:

- Keystore blob format: JVM PASS
- Keystore/repository/migration: **Instrumentation 13/13 PASS**
- 실제 실행: **Device PASS**

## 3. Debug 진단 화면

Debug APK에서 `Diagnostics` 버튼이 표시됩니다. Release UI에는 표시되지 않습니다.

표시 항목:

- GeckoRuntime 초기화 여부
- 탭/세션 수
- Browser/Password Keystore key 존재 및 AES-GCM round-trip
- Browser/Password DB schema version
- Android 알림 활성 여부
- POST_NOTIFICATIONS 상태
- 저장된 사이트 알림 권한 수
- 마지막 notice

표시하지 않는 항목:

- URL
- username/password
- 알림 title/body
- 암호문/payload
- Keystore key material

## 4. 실행 명령

```bash
cd apps/android
./gradlew :app:testDebugUnitTest
./gradlew :app:assembleDebugAndroidTest
./gradlew :app:connectedDebugAndroidTest   # 기기/AVD 필요
```

실기기에서 수동 설치:

```bash
adb install -r artifacts/android/AMARE-0.12.0-debug-arm64.apk
```

## 5. 성능 측정 준비

실기기 측정 전에는 수치를 추정하지 않습니다.

권장 측정:

- `adb shell am start -W org.amare.browser/.MainActivity`
- Android Studio Profiler: CPU/Memory
- `adb shell dumpsys meminfo org.amare.browser`
- 1/5/10/20 탭에서 메모리와 전환 시간
- 5분 백그라운드 후 복귀
- 세션 복구 시간
- 다운로드/알림 처리 시간

측정 항목:

- cold start
- warm start
- GeckoRuntime 초기화 시간
- 탭 전환 시간
- 프로세스 메모리
- 복구 성공률

현재 실제 수치: **UNVERIFIED**
