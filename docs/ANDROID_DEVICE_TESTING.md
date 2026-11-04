# Android 실제 기기 및 Emulator 테스트

- 기준: Android 0.5.0
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
```

## 3. 실제 기기에서만 검증 가능한 항목## 3. 실제 기기에서만 검증 가능한 항목

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
adb install -r /home/ubuntu/AMARE/artifacts/android/AMARE-0.5.0-debug.apk
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

## 6. 결과 표기 원칙

- JVM test PASS: JVM에서 실제 실행 완료
- instrumentation compile PASS: 테스트 APK 컴파일 완료, 실행 아님
- instrumentation PASS: Emulator/실기기에서 실제 실행 완료
- 실기기 PASS: 실제 기기에서 실행 완료
- 실행 환경이 없으면 UNVERIFIED
