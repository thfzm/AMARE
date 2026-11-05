# Android Release 및 서명

- 기준: Android 0.13.0
- 관련 문서: [보안](ANDROID_SECURITY.md), [기기 테스트](ANDROID_DEVICE_TESTING.md)

## 1. 빌드

```bash
cd apps/android
export JAVA_HOME=/usr/lib/jvm/java-17-openjdk-arm64
export ANDROID_HOME=/home/ubuntu/android-sdk
./gradlew :app:testDebugUnitTest :app:assembleDebug \
  :app:assembleDebugAndroidTest :app:assembleRelease :app:bundleRelease :app:lintDebug
```

- Debug APK: universal, debug signed
- Release APK: R8 + resource shrinking, unsigned unless signing env is present
- Release AAB: unsigned unless signing env is present
- Release `debuggable=false`
- R8 mapping: `app/build/outputs/mapping/release/mapping.txt`

기본 빌드는 모든 ABI를 포함하는 universal APK입니다. 로컬 최적화 빌드는
`-PamareAbi=arm64-v8a`로 ARM64 전용 APK를 만들 수 있습니다. AAB는 표준 universal
구성으로 빌드합니다.

0.13.0 실제 크기:

| 산출물 | 크기 |
|---|---:|
| arm64 Debug APK | 129,839,587 bytes |
| universal Debug APK | 291,461,161 bytes |
| arm64 Release APK | 92,683,392 bytes |
| universal Release APK | 243,241,738 bytes |
| Release AAB | 244,225,983 bytes |

위 수치는 2026-11-05 0.13.0 재빌드 결과입니다. Release 산출물은 서명 키가 없어
모두 unsigned입니다.

## 2. 로컬 서명

다음 환경변수가 모두 설정된 경우에만 release signingConfig가 생성됩니다.

```text
AMARE_ANDROID_KEYSTORE
AMARE_ANDROID_KEYSTORE_PASSWORD
AMARE_ANDROID_KEY_ALIAS
AMARE_ANDROID_KEY_PASSWORD
```

예시는 값이 아닌 변수 이름만 문서화합니다. 키와 비밀번호를 Git, 로그, 채팅, 스크린샷에
포함하지 마십시오.

## 3. GitHub Actions Secrets 설계

권장 secret:

```text
AMARE_ANDROID_KEYSTORE_BASE64
AMARE_ANDROID_KEYSTORE_PASSWORD
AMARE_ANDROID_KEY_ALIAS
AMARE_ANDROID_KEY_PASSWORD
```

CI에서는 base64 keystore를 runner 임시 파일로 decode한 뒤 `AMARE_ANDROID_KEYSTORE`에
그 경로를 지정합니다. 임시 파일은 job 종료 시 삭제합니다. 실제 secret 등록은 저장소
소유자의 승인 없이 수행하지 않습니다.

## 4. 서명 검증

Debug APK:

```bash
apksigner verify --verbose app-debug.apk
```

Release APK가 unsigned인 경우 `DOES NOT VERIFY`가 정상입니다.

```bash
apksigner verify --verbose app-release-unsigned.apk
jarsigner -verify app-release.aab
```

자동 검증:

```bash
tools/verify-android-artifacts.sh \
  apps/android/app/build/outputs/apk/debug/app-debug.apk \
  apps/android/app/build/outputs/apk/release/app-release-unsigned.apk \
  apps/android/app/build/outputs/bundle/release/app-release.aab
```

서명된 release를 기대할 때는 `EXPECT_SIGNED_RELEASE=1`을 설정합니다.

## 5. 산출물 구분

| 산출물 | 현재 상태 | 배포 가능 여부 |
|---|---|---|
| `app-debug.apk` | Debug key 서명 | 개발 설치용 |
| `app-release-unsigned.apk` | 서명 없음 | 배포 불가 |
| `app-release.aab` | 서명 없음 | 배포 불가 |
| signing env가 있는 release | 환경변수 키로 서명 | 검증 후 배포 가능 |

## 6. 1.0.0 전 필수 확인

- 실제 기기에서 Keystore/DB migration/instrumentation 실행
- Release signing key 등록 및 signed APK/AAB 검증
- HTTPS-only/Mixed Content 실제 사이트 호환성 확인
- 알림 프로세스 재시작 클릭 동작 확인
- 다운로드 중복 파일명/재시도 정책 확정

## 7. 실제 배포 키 준비 절차 (사용자 승인 필요)

아래 작업은 저장소 소유자의 승인 없이 실행하지 않았습니다.

1. 오프라인/암호화된 위치에서 release keystore 생성
2. 키스토어 비밀번호와 key password를 별도 암호 관리 도구에 보관
3. `AMARE_ANDROID_KEYSTORE`에 로컬 keystore path 지정
4. 나머지 3개 환경변수 설정
5. `EXPECT_SIGNED_RELEASE=1`로 signed release APK/AAB 검증
6. GitHub Actions Secrets에는 keystore를 base64로 저장
7. Play 배포 시 Play App Signing 사용 여부 결정

키 생성 예시는 명령 형식만 문서화하며, 실제 키는 생성하지 않았습니다.

```bash
keytool -genkeypair -v \
  -keystore /secure/path/amare-release.jks \
  -alias amare-release \
  -keyalg RSA -keysize 4096 -validity 10000
```

키 분실 시 같은 package의 업데이트가 불가능할 수 있습니다. Android App Bundle을 Play에
업로드하면 Play App Signing을 사용해 앱 서명 키를 Google이 관리하도록 선택할 수 있습니다.
등록 키/업로드 키/keystore를 Git, CI 로그, 채팅에 포함하지 마십시오.

## 8. 주의

- 실제 배포용 키를 임의로 생성/등록/업로드하지 않음
- Release unsigned 파일을 배포 가능한 파일로 표현하지 않음
- key password가 Gradle 로그에 노출되지 않도록 `--info`/`--debug` 로그 공유 금지
- 실제 서명 키 백업/복구 정책은 저장소 소유자가 별도로 관리
