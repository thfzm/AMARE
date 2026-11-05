# Android 실제 기기 및 Emulator 테스트

- 기준: Android 0.13.0
- 관련 문서: [Release](ANDROID_RELEASE.md), [보안](ANDROID_SECURITY.md)

## 1. 현재 개발 호스트

- Oracle Cloud Linux `aarch64`
- `/dev/kvm` 없음
- 공식 Android Emulator Linux 패키지는 `x86_64`
- 로컬 `connectedDebugAndroidTest` 실행 불가

로컬에서 `./gradlew :app:connectedDebugAndroidTest`를 실행하면
`No connected devices!`로 실패합니다. 이는 테스트 실패가 아니라 실행 환경 부재입니다.

2026-11-05 0.12.0 기준으로 원격 ADB에 연결한 Lenovo TB138FC(Android 14/SDK 34, arm64-v8a)에서
Instrumentation 13개를 실제 실행해 **13/13 PASS**를 확인했습니다. 0.13.0 재검증 시점에는
태블릿 Tailscale이 오프라인이라 **BLOCKED**입니다.

## 2. GitHub Actions Emulator 시도 결과

Android CI에서 `reactivecircus/android-emulator-runner@v2`로 API 34 x86_64 AVD 실행을
시도했습니다. 결과:

- GitHub hosted Ubuntu runner에 `/dev/kvm` 접근 권한 없음
- action이 자동으로 `-accel off`(software emulation)로 전환
- 부팅에 약 13~16분 소요
- 부팅 직후 `adb shell input keyevent 82`에서 `Broken pipe (32)` / exit 224 발생
- 테스트 APK 설치 전에 emulator 연결이 불안정해짐

따라서 hosted runner에서 instrumentation 실행은 신뢰할 수 없으며, CI는 현재
**test APK compile-only**로 유지합니다. CI 결과는 계속 compile-only로 표기하고,
실기기 실행 결과와 구분합니다.

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
2026-11-05 0.12.0 실기기 실행 결과는 **13/13 PASS**이며, 0.13.0은 **BLOCKED**입니다.

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
adb install -r /home/ubuntu/AMARE/artifacts/android/AMARE-0.13.0-debug-arm64.apk

Debug APK SHA-256: `0d6edd2c8441796b0549e326c8aea031d16b76982cb1a856d2abfb60a13d1959`

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
  artifacts/android/AMARE-0.13.0-debug-arm64.apk \
  artifacts/android/AMARE-0.13.0-debug-androidTest.apk
```

이 스크립트는 ADB 기기가 없으면 실패를 명확히 표시하고 UNVERIFIED로 남깁니다.

## 5.4 0.12.0 실행 결과 (회귀 기준선)

- ADB 연결 기기: Lenovo TB138FC (Android 14/SDK 34, arm64-v8a)
- 자동 스크립트 결과: **PASS**
- Instrumentation: **13/13 PASS**
- 결과 보고서: `artifacts/android/device-test/results/device-test-20261105-020023.md`
- 상세 보고서: `artifacts/android/device-test/results/AMARE-0.12.0-real-device-validation.md`
- UI smoke: 앱 실행, HTTPS 렌더링, 세션 복원, 탭 생성/종료, 명시적 외부 VIEW, Bookmarks/History/Downloads/Settings 화면 PASS
- UNVERIFIED: 주소창 입력, 뒤로/앞으로/새로고침, 기본 브라우저 role, 팝업, 웹 알림, 자동완성 UI, 다운로드 파일 열기, 데이터 삭제, Mixed Content, 지속 성능
- 데이터 보존: 두 APK 모두 `-r` 설치, uninstall/pm clear/factory reset 미수행

Instrumentation 테스트는 정상 앱 데이터를 지우지 않도록 격리 DB 이름과 테스트 전용 Keystore alias를 사용합니다.

실기기 검증 중 발견한 테스트/스키마 결함:

1. 신규 DB `onCreate`가 legacy plaintext `site_permissions`를 생성해 migration 테스트가 충돌했습니다. 신규 DB에서는 해당 테이블을 만들지 않도록 수정했습니다.
2. 비밀번호 손상 payload 테스트가 DB 생성 전에 raw SQLite를 열어 실패했습니다. 테스트에서 `PasswordDatabase`를 먼저 생성하도록 수정했습니다.
3. 빈 legacy plaintext 테이블이 존재할 때 migration이 해당 encrypted 테이블을 삭제할 수 있었습니다. 이제 빈 legacy 테이블은 encrypted 데이터를 교체하지 않고 drop만 하며, `emptyLegacyPermissionTableDoesNotEraseEncryptedPermissions` 회귀 테스트를 추가했습니다.

## 5.5 0.13.0 실행 결과

- ADB/Tailscale: 태블릿 `pad-pro-2022` 오프라인 (마지막 확인 6시간 전)
- JVM: **40/40 PASS**
- Lint: **0 errors / 8 warnings**
- Debug/Release/AAB 빌드: **PASS**
- Instrumentation 실행: **BLOCKED** — 연결된 기기 없음
- 주소창 제안/검색엔진/테마/중지 UX 실기기 검증: **BLOCKED**
- 0.12.0 실기기 13/13 PASS는 회귀 기준으로만 사용하며, 0.13.0 PASS로 대체하지 않음

연결 후 `ANDROID_SERIAL=<serial> tools/run-android-device-tests.sh`로 재검증해야 합니다.
좌표 기반 ADB 입력은 사용하지 않고, 앱 대상 Instrumentation/Intent만 사용합니다.

## 6. 결과 표기 원칙

- JVM test PASS: JVM에서 실제 실행 완료
- instrumentation compile PASS: 테스트 APK 컴파일 완료, 실행 아님
- instrumentation PASS: Emulator/실기기에서 실제 실행 완료
- 실기기 PASS: 실제 기기에서 실행 완료
- 실행 환경이 없으면 UNVERIFIED
