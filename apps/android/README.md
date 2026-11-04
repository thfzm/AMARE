# AMARE Android

GeckoView 기반 Android 브라우저 셸입니다. 현재 Debug APK 빌드와 정적 검증은 완료했고,
실제 기기 실행은 **UNVERIFIED**입니다.

## 구현 상태

### 구현됨

- Jetpack Compose 단일 Activity 브라우저 UI
- GeckoView `GeckoRuntime` 프로세스 싱글턴 및 `GeckoSession` 수명주기
- HTTPS/HTTP 페이지 로드, 로딩 진행률, 페이지 제목 표시
- 뒤로 가기, 앞으로 가기, 새로고침/중지
- 주소창 입력, URL 정규화, HTTPS DuckDuckGo 검색 fallback
- 여러 탭 생성/선택/종료
- 시스템 뒤로 가기와 브라우저 뒤로 가기 연결
- 네트워크/SSL/HTTPS 오류 메시지와 재시도 UI
- 화면 회전 시 ViewModel 기반 세션 유지
- 앱 재실행 시 열린 탭 URL/활성 탭 복원
- 백그라운드 전환 시 세션 상태 flush 및 URL 저장
- 앱 아이콘, Android 12+ 데이터 추출 차단, cleartext HTTP 차단 설정

### 미구현 또는 미검증

- 실제 기기에서의 GeckoView 초기화/렌더링/입력: **UNVERIFIED**
- 북마크, 방문 기록, 다운로드, 비밀번호/자동완성
- 사이트 권한 요청 UI(카메라, 위치, 알림 등)
- `window.open()`/새 창 처리, WebExtension, AI 계층
- 전체 세션 히스토리 복원(현재는 URL/활성 탭만 복원)
- 릴리스 서명, Play Store/AAB 배포, Android Keystore 연동
- Android 11+ scoped storage 기반 파일 다운로드

## 빌드 환경

- JDK 17
- Android SDK Platform 36
- Android Build Tools 36.0.0
- Gradle 8.13 (wrapper)
- Android Gradle Plugin 8.13.0
- Kotlin 2.2.20
- GeckoView `152.0.20260713164047`

```bash
export JAVA_HOME=/usr/lib/jvm/java-17-openjdk-arm64
export ANDROID_HOME=/home/ubuntu/android-sdk
cd apps/android
./gradlew :app:testDebugUnitTest :app:assembleDebug :app:lintDebug
```

ABI split 결과:

```text
app/build/outputs/apk/debug/app-arm64-v8a-debug.apk
app/build/outputs/apk/debug/app-armeabi-v7a-debug.apk
app/build/outputs/apk/debug/app-x86_64-debug.apk
app/build/outputs/apk/debug/app-universal-debug.apk
```

배포용으로 복사한 로컬 파일:

```text
/home/ubuntu/AMARE/artifacts/android/AMARE-0.1.0-arm64-v8a-debug.apk
/home/ubuntu/AMARE/artifacts/android/AMARE-0.1.0-armeabi-v7a-debug.apk
/home/ubuntu/AMARE/artifacts/android/AMARE-0.1.0-universal-debug.apk
```

## APK 정보

- Package: `org.amare.browser`
- Version: `0.1.0` (`versionCode 1`)
- minSdk: 29 (Android 10)
- targetSdk/compileSdk: 36
- Debug 서명: Android Debug key
- arm64 APK: 약 124 MiB, 설치 후 GeckoView native library extraction 때문에 추가 저장 공간 필요
- 필요 권한: `INTERNET`, `ACCESS_NETWORK_STATE` 및 GeckoView가 병합한 권한

## 설치

USB debugging이 켜진 기기를 연결한 뒤:

```bash
export PATH=/home/ubuntu/android-sdk/platform-tools:$PATH
adb devices
adb install -r /home/ubuntu/AMARE/artifacts/android/AMARE-0.1.0-arm64-v8a-debug.apk
```

`INSTALL_FAILED_NO_MATCHING_ABIS`가 나오면 universal APK를 사용합니다.

## 실기기 체크리스트

1. APK 설치 후 launcher에서 **AMARE** 실행
2. 초기 GeckoView 화면과 DuckDuckGo 홈 로드 확인
3. 주소창에 `https://example.com` 입력 후 이동
4. 뒤로/앞으로/새로고침 버튼 동작 확인
5. `+ Tab`으로 탭 생성, 탭 선택, `×`로 종료
6. 주소창에 검색어 입력 후 검색 결과 로드 확인
7. 페이지 로드 후 화면 회전, 탭 URL/세션 유지 확인
8. 앱 종료 후 재실행, 열린 탭 URL 복원 확인
9. 비행기 모드 또는 잘못된 도메인으로 네트워크 오류/재시도 확인
10. 홈으로 이동 후 앱 백그라운드/복귀, 페이지 유지 확인
11. 시스템 뒤로 가기: 히스토리가 있으면 뒤로, 없으면 앱 종료

## 아키텍처 경계

- Android 전용 코드는 `apps/android` 아래에만 둡니다.
- Rust core, Windows C#/WinUI/WebView2 코드는 이번 Android UI 작업에서 변경하지 않습니다.
- 향후 공통 데이터/서비스 계층은 Android에서 JNI/UniFFI adapter로 연결합니다.
