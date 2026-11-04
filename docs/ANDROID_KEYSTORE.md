# Android Keystore 및 JNI/FFI 경계 분석

- 기준: Android 0.2.0 이후 세션 복구/Keystore 작업
- 관련 문서: [Android 다음 단계 설계](ANDROID_NEXT_STEPS.md), [키 보호](KEY_PROTECTION.md)

## 1. 현재 경계

- Android APK는 GeckoView, Kotlin/Compose, Android 프레임워크만 포함합니다.
- Rust `amare-core`, `amare-services`, `amare-security`는 현재 Android APK에 탑재되지 않습니다.
- Android에는 Rust JNI 라이브러리(`System.loadLibrary`)가 없고 JNI ABI도 정의되어 있지 않습니다.
- `amare-ffi`는 C ABI이며 Windows/Linux C# interop에 사용됩니다.
- 따라서 Rust `AndroidKeystoreKeyProtector`를 지금 JNI로 연결하면 실제로 호출되지 않는
  미사용 경로가 됩니다. 이번 단계에서는 가짜 JNI 루프를 추가하지 않았습니다.

## 2. 구현한 Android Keystore

Kotlin `org.amare.browser.security.AndroidKeystoreProtector`:

- AndroidKeyStore AES-256-GCM
- alias: `amare_browser_data_v1`
- StrongBox 우선 시도, `StrongBoxUnavailableException`이면 TEE-backed key로 fallback
- AndroidKeyStore 키는 export하지 않음
- 레코드마다 12-byte random IV
- blob 형식: `version(1) || iv_length(1) || iv(12) || ciphertext+tag(16+)`
- AAD: `amare.keystore.v1|<context>`
- 인증 태그 실패/버전 불일치/IV 길이 오류는 `INVALID_MATERIAL`
- 키 삭제/무효화는 `KEY_INVALIDATED`
- 암호화 실패 시 평문 fallback 없음

## 3. 세션 저장 연결

`session_snapshots`와 `session_tabs` 테이블에서:

- snapshot/tab 위치와 활성 index만 평문
- 각 탭의 URL, 제목, `GeckoSession.SessionState` JSON은 AES-GCM BLOB으로 암호화
- 저장/교체는 SQLite transaction
- 복호화 실패 시 평문 fallback 없이 안전하게 세션을 초기화하고 사용자 알림
- 키 무효화/앱 재설치로 복호화가 불가능하면 저장된 세션 snapshot을 삭제

## 4. 미구현/후속 JNI 설계

Rust 코어를 Android에 탑재하는 단계에서 다음 계약을 추가해야 합니다.

```text
Kotlin NativeBridge.protect(context, plaintext) -> blob
Kotlin NativeBridge.unprotect(context, blob) -> plaintext
```

- Rust JNI 라이브러리를 APK에 포함
- `AndroidKeystoreKeyProtector`가 JNI를 통해 Android Keystore helper를 호출
- ABI별 `.so` 빌드, CI NDK/Rust target 추가
- Rust 단위 테스트와 Android instrumentation 테스트를 분리
- Android 기기/에뮬레이터에서 round-trip, 변조, 키 삭제, 재설치 검증

이 작업은 Rust 코어가 실제 Android 런타임에 포함될 때 진행해야 하며, 현재는 Kotlin
플랫폼 계층에서 동일한 KeyProtector 의미론을 구현했습니다.

## 5. 검증 상태

| 항목 | 상태 |
|---|---|
| Android Keystore blob format 테스트 | PASS (JVM) |
| Android Keystore 실제 round-trip | UNVERIFIED (기기/에뮬레이터 필요) |
| 키 무효화/재설치 | UNVERIFIED |
| 세션 snapshot 암호화/복호화 | UNVERIFIED (기기 필요) |
| Rust AndroidKeystore JNI 연결 | 미구현 (Rust Android 런타임 미탑재) |
