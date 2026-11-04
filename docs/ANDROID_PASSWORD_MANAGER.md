# Android 비밀번호 관리자 및 자동완성

- 기준: Android 0.6.0
- 상태: 구현 및 JVM 테스트 PASS, 기기/Keystore 실행 검증 UNVERIFIED
- 관련 문서: [보안](ANDROID_SECURITY.md), [Keystore/JNI](ANDROID_KEYSTORE.md), [기기 테스트](ANDROID_DEVICE_TESTING.md)

## 1. 저장소 경계

비밀번호는 일반 브라우저 DB(`amare_browser.db`)와 분리된
`amare_passwords.db`에 저장합니다.

| 항목 | 일반 브라우저 데이터 | 비밀번호 |
|---|---|---|
| DB | `amare_browser.db` | `amare_passwords.db` |
| Keystore alias | `amare_browser_data_v1` | `amare_passwords_v1` |
| AAD | bookmark/history/download/permission/session | `amare.password.v1` |
| 접근 계층 | `BrowserRepository` | `PasswordRepository` |

Rust `SecretRepository`는 현재 Android APK에 탑재되지 않았습니다. Android에서는 동일한
AndroidKeystoreKeyProtector 의미론을 Kotlin으로 구현하고, 별도 alias/DB로 보안 경계를
분리했습니다. 공통 Rust 서비스가 Android에 탑재되면 JNI/UniFFI adapter로 계약을 공유해야
합니다.

## 2. 저장 형식

```text
credentials
  id INTEGER PK
  origin_key TEXT          # SHA-256(lowercase(origin))
  payload BLOB             # AES-256-GCM
  created_at, updated_at

site_settings
  origin_key TEXT PK
  save_disabled INTEGER
  updated_at INTEGER
```

`payload` 평문 JSON:

```json
{"origin":"https://example.com","username":"alice","password":"..."}
```

- URL/username/password는 DB 평문 컬럼에 저장하지 않음
- origin_key는 조회용 hash이며 원문 origin은 암호화 payload에만 저장
- 동일 origin + username은 새 행을 만들지 않고 기존 행 update
- 동일 origin의 다른 username은 별도 계정으로 저장

## 3. 암호화

- AndroidKeyStore AES-256-GCM
- alias `amare_passwords_v1`
- record마다 random IV
- AAD `amare.keystore.v1|amare.password.v1`
- 인증 태그 실패/키 무효화 시 평문 fallback 없음
- 목록 조회 시 password를 메모리 summary에서 제외
- 계정 상세/자동완성 선택 시에만 full credential을 복호화
- 로그에 URL/username/password/payload를 기록하지 않음

## 4. 로그인 폼 감지 및 저장

GeckoView의 `Autofill.Delegate`와 `PromptDelegate`를 사용합니다.

- `Autofill.Delegate.onNodeAdd/Update/Focus`
  - `Hint.USERNAME`, `Hint.EMAIL_ADDRESS`, `Hint.PASSWORD` 노드 감지
  - node domain이 top-level origin과 일치하는 경우만 사용
- `PromptDelegate.onLoginSave`
  - form submit 시 GeckoView가 제안하는 LoginSaveOption 사용
  - HTTPS origin만 저장
  - `formActionOrigin`이 top-level origin과 다르면 저장 거부
  - 사용자 확인 없이 저장하지 않음
  - 기존 username이면 Update, 없으면 Save로 표시
  - Not now / Never / Save / Update 제공
  - Never는 `origin_key` 단위로 저장 제안 비활성화

GeckoView `Autocomplete.StorageDelegate`에는 빈 login 목록만 반환합니다. 이는 GeckoView가
자체적으로 단일 계정을 자동 입력하는 것을 막고, AMARE 자체 UI에서 사용자 선택 후
`Autofill.Session.autofill()`을 호출하기 위한 것입니다.

## 5. 자동완성

- 로그인 필드가 감지되고 해당 HTTPS origin에 저장 계정이 있을 때만 배너 표시
- 자동 입력/자동 제출 없음
- 계정이 1개: Fill 버튼, 여러 개: Choose 목록
- 선택 시 현재 탭 URL origin을 재검증한 뒤 username/password field ID에만 값 입력
- iframe/cross-origin node는 node domain 불일치로 제외
- 입력 후 ViewModel의 autofill prompt와 credential 참조 제거
- 사이트 이동/세션 시작/취소 시 field ID와 prompt 초기화

## 6. Android Autofill Framework

GeckoView는 `Autofill.Delegate`와 `ViewStructure`/`AutofillValue`를 통해 Android Autofill
Framework와 연결할 수 있습니다. 이번 단계에서는:

- 브라우저 내부 자동완성을 구현
- Android `AutofillService`는 구현하지 않음
- 다른 앱의 로그인 폼 자동완성은 제공하지 않음
- OS Autofill Service와 브라우저 내부 자동완성의 동시 동작 충돌 없음
- 사용자가 AMARE를 시스템 Autofill 서비스로 선택할 필요 없음

OS 전체 AutofillService는 별도 제품 범위/권한/보안 검토가 필요하므로 후속 단계로 분리합니다.

## 7. UI

- 기존 LibraryBar에 `Passwords` 항목 추가
- Passwords panel
  - 검색
  - Recent/A-Z 정렬
  - 계정 추가
  - 계정 상세/수정/삭제
  - 비밀번호 Show/Hide
  - 현재 사이트의 save prompt 재활성화
- 로그인 저장 확인 dialog
- 자동완성 배너 및 다중 계정 선택 dialog

브라우저 기본 toolbar에는 메뉴를 추가하지 않았습니다.

## 8. 테스트 상태

JVM:

- `PasswordRulesTest`: origin 검증, cross-origin form 거부, node domain, username 정규화 PASS
- 기존 JVM 19개 + 신규 4개 = 23개 PASS

Instrumentation 작성(실행 UNVERIFIED):

- 비밀번호 암호화/저장/목록/수정/삭제
- origin별 계정 분리
- 중복 username update
- DB payload 평문 미포함
- 손상 payload skip 및 notice
- 키 삭제 시 복호화 실패/notice
- 기존 Keystore/Session/Migration 테스트

실제 기기/안정적인 Emulator가 없어 instrumentation 실행은 UNVERIFIED입니다.
특히 GeckoView 로그인 폼 감지, 저장 prompt, 자동완성 banner는 실제 기기에서 반드시
검증해야 합니다.
