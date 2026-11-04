# Android 실제 기기 및 Emulator 테스트

- 기준: Android 0.12.0
- 관련 문서: [Release](ANDROID_RELEASE.md), [보안](ANDROID_SECURITY.md)

## 1. 현재 개발 호스트

- Oracle Cloud Linux `aarch64`
- `/dev/kvm` 없음
- 공식 Android Emulator Linux 패키지는 `x86_64`
- 로컬 `connectedDebugAndroidTest` 실행 불가

로컬에서 `./gradlew :app:connectedDebugAndroidTest`를 실행하면
`No connected devices!`로 실패합니다. 이는 테스트 실패가 아니라 실행 환경 부재입니다.

## 2. GitHub Actions Emulator 시도 결과

Android CI에서 `reactivecircus/android-emulator-runner@v2`로 API 34 x86_64 AVD 실행을
시도했습니다. 결과:

- GitHub hosted Ubuntu runner에 `/dev/kvm` 접근 권한 없음
- action이 자동으로 `-accel off`(software emulation)로 전환
- 부팅에 약 13~16분 소요
- 부팅 직후 `adb shell input keyevent 82`에서 `Broken pipe (32)` / exit 224 발생
- 테스트 APK 설치 전에 emulator 연결이 불안정해짐

따라서 hosted runner에서 instrumentation 실행은 신뢰할 수 없으며, CI는 현재
**test APK compile-only**로 유지합니다. 실행 결과는 UNVERIFIED입니다.

실행 가능한 대안:

1. `/dev/kvm`이 있는 self-hosted Linux runner + x86_64 AVD
2. USB/원격 ADB로 연결된 실제 기기
3. Android Studio/실기기에서 instrumentation APK 수동 실행

실행 대상:

```text
AndroidKeystoreProtectorTest
SessionSnapshotRepositoryTest
EncryptedDatabaseMigrationTest
PasswordRepositoryTest
```

실행 결과는 CI에서 compile-only이며, 기기/AVD가 연결된 환경에서만 PASS로 표시합니다.

## 3. 실제 기기에서만 검증 가능한 항목

- GeckoView 초기화/렌더링/입력
- 실제 TEE/StrongBox-backed Keystore
- 지문/화면 잠금 변경 등에 따른 키 무효화
- MediaStore 다운로드 파일 접근
- 카메라/마이크/위치 권한과 실제 하드웨어
- 배터리/메모리/장시간 세션
- 실제 Android 10~16 기기별 동작

## 4. 실기기 설치

USB debugging을 켠 뒤:

```bash
export PATH=/home/ubuntu/android-sdk/platform-tools:$PATH
adb devices
adb install -r /home/ubuntu/AMARE/artifacts/android/AMARE-0.12.0-debug-arm64.apk

Debug APK SHA-256: `a91782adea3bdc81ab2361294b8941a826040a168beb0db0daca1902e0cf6222`

실기기 테스트 패키지:
`/home/ubuntu/AMARE/artifacts/android/device-test/`
```

Release APK/AAB는 현재 unsigned이므로 실제 배포 전 서명이 필요합니다.

## 5. 최소 실기기 체크리스트

1. 앱 설치/실행 및 GeckoView 초기 화면
2. HTTPS 페이지 로드
3. 북마크 추가/검색/삭제
4. 방문 기록 검색/삭제
5. 다운로드 진행/완료/Open
6. 사이트 권한 Allow/Deny/Reset
7. 앱 강제 종료 후 탭/히스토리 복원
8. 기기 잠금 방식 변경 후 Keystore 데이터 접근 안내
9. 앱 데이터 삭제 후 새 세션 시작
10. 백업/기기 이전에서 앱 데이터 제외 확인
11. Debug 전용 Diagnostics에서 GeckoRuntime/Keystore/DB/알림 권한/HTTPS-only 상태 확인
12. Release APK에서는 Diagnostics 버튼이 보이지 않는지 확인
13. 보안 패널에서 HTTPS-only On/Off 후 HTTP/HTTPS 동작 차이 확인
14. 알림 클릭을 앱 프로세스 종료 후에도 확인
15. 다운로드 목록에서 위험 파일 경고 표시 확인
16. 설정에서 방문 기록/쿠키/캐시/사이트 데이터/권한/비밀번호/전체 데이터 삭제
17. 기본 브라우저 role 설정 및 외부 HTTPS 링크 열기
18. 외부 HTTP 링크가 HTTPS-only에서 차단되는지 확인
19. 최근 닫은 탭 Reopen 및 모든 탭 닫기 확인

## 5.1 실기기 결과 기록

각 항목은 PASS / FAIL / UNVERIFIED로 기록합니다. 실행하지 않은 항목은 UNVERIFIED입니다.

```text
기기 모델:
Android 버전:
APK SHA-256:
설치 시각:
테스터:
브라우저 기본:
새 창:
웹 알림:
비밀번호:
보안/데이터:
성능:
실패 재현 절차:
로그 파일:
```

## 5.2 로그 수집

```bash
adb logcat -c
adb logcat -d -v time > amare-device.log
```

로그에는 GeckoView/Android 시스템이 URL을 포함할 수 있습니다. 로그 공유 전에
비밀번호, 알림 본문, 민감한 URL이 없는지 확인해야 합니다. AMARE 앱 코드는 password,
credential, notification body를 로그에 기록하지 않습니다.

## 5.3 자동 실행

```bash
tools/run-android-device-tests.sh \
  artifacts/android/AMARE-0.12.0-debug-arm64.apk \
  artifacts/android/AMARE-0.8.0-debug-androidTest.apk
```

이 스크립트는 ADB 기기가 없으면 실패를 명확히 표시하고 UNVERIFIED로 남깁니다.

## 5.4 0.12.0 실행 결과

- ADB 연결 기기: 없음
- 자동 스크립트 결과: **UNVERIFIED**
- 결과 보고서: `artifacts/android/device-test/results/`
- 원인: USB/무선 ADB 기기 미연결
- 실제 기기 테스트: **UNVERIFIED**

Instrumentation 테스트는 정상 앱 데이터를 지우지 않도록 격리 DB 이름과 테스트 전용 Keystore alias를 사용합니다.

## 6. 결과 표기 원칙

- JVM test PASS: JVM에서 실제 실행 완료
- instrumentation compile PASS: 테스트 APK 컴파일 완료, 실행 아님
- instrumentation PASS: Emulator/실기기에서 실제 실행 완료
- 실기기 PASS: 실제 기기에서 실행 완료
- 실행 환경이 없으면 UNVERIFIED
