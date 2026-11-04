# Android 보안 설계 및 검증 상태

- 기준: Android 0.12.0
- 관련 문서: [데이터 암호화](ANDROID_DATA_ENCRYPTION.md), [Keystore/JNI](ANDROID_KEYSTORE.md), [Release](ANDROID_RELEASE.md)

## 1. 보호 대상과 저장 위치

| 데이터 | 위치 | 보호 |
|---|---|---|
| 북마크 URL/제목 | app-private SQLite `bookmarks_enc` | AES-256-GCM |
| 방문 기록 URL/제목 | `history_enc` | AES-256-GCM |
| 다운로드 메타데이터 | `downloads_enc` | AES-256-GCM |
| 사이트 권한 origin/decision | `site_permissions_enc` | AES-256-GCM, SHA-256 lookup key |
| 세션 URL/제목/SessionState | `session_tabs` | AES-256-GCM |
| 정렬/상태 메타데이터 | 각 테이블 | 평문 (민감 콘텐츠 아님) |
| 다운로드 완료 파일 | MediaStore `Download/AMARE` | OS 미디어 저장소 |
| 비밀번호/username | 별도 `amare_passwords.db` | AES-256-GCM, 별도 alias |
| 웹 알림 표시 | Android NotificationManager | origin permission + rate limit + private visibility |
| legacy URL 목록 | SharedPreferences | 마이그레이션 후 삭제 |

## 2. Android Keystore

- AndroidKeyStore AES-256-GCM, alias `amare_browser_data_v1`
- StrongBox 우선, 미지원 시 TEE key fallback
- 키 material은 앱 코드/파일/로그로 export하지 않음
- 12-byte random IV, 레코드마다 새로 생성
- blob: `version || iv_length || iv || ciphertext+tag`
- AAD: `amare.keystore.v1|<data type context>`
- 데이터 종류별 AAD가 다르므로 다른 종류의 레코드로 교체 사용할 수 없음
- 인증 태그/AAD/IV 오류는 `INVALID_MATERIAL`, 키 삭제/무효화는 `KEY_INVALIDATED`

## 3. 실패 시 정책

- 암호화 실패 시 평문 저장 fallback 없음
- 복호화 실패 레코드는 UI에 노출하지 않고 고정된 사용자 안내를 표시
- 세션 복구 중 키 무효화가 확인되면 세션 snapshot만 삭제하고 새 세션 시작
- 북마크/방문 기록/다운로드/권한의 undecryptable row는 자동 삭제하지 않음
- migration 실패 시 legacy 평문 테이블을 DROP하지 않음
- legacy 평문 테이블이 남아 있어도 신규 UI는 이를 읽지 않음
- 로그에 URL/제목/SessionState/키/암호문을 기록하지 않음

## 4. 키 무효화 및 복구 정책

Android Keystore 키는 non-exportable이므로 키가 삭제/무효화되면 과거 AES-GCM 데이터는
AMARE가 복구할 수 없습니다.

| 상황 | 동작 |
|---|---|
| 앱 업데이트 | alias/key가 유지되면 기존 데이터 복호화 가능 |
| 앱 재설치 | Android가 앱 데이터를 삭제하므로 DB도 제거됨 |
| 앱 데이터 삭제 | DB/키 모두 제거됨 |
| 키만 무효화/삭제 | 암호화 데이터 접근 불가, 사용자 안내 필요 |
| migration 실패 | legacy 평문 보존, 신규 UI 접근 차단 |
| 키 분실 | 복구 불가. 평문 fallback 금지 |

현재 구현은 키 무효화 시 암호화 데이터를 자동으로 삭제하지 않습니다. 따라서 사용자가
수동으로 앱 데이터를 초기화하기 전까지 데이터는 복구 불가 상태로 남습니다. 향후 "암호화
데이터 초기화" UI와 사전 경고를 추가해야 하며, 자동 초기화는 사용자 데이터 손실 위험이
있으므로 이번 단계에서 구현하지 않았습니다.

## 5. 네트워크/프라이버시

- cleartext HTTP 차단 설정
- SSL 오류 시 로드 중단, 인증서 자동 우회 없음
- GeckoView remote debugging 비활성화
- GeckoView console output 비활성화
- 추적 보호 및 Global Privacy Control 활성화
- 사이트 권한은 사용자 선택 전 자동 허용하지 않음
- Android 런타임 권한과 웹 권한 분리

## 6. 백업/로그

- `allowBackup=false`
- Android 12+ cloud backup/device transfer에서 앱 데이터 제외
- release `debuggable=false`
- DB/Keystore 오류 메시지는 고정 문자열만 기록
- Exception 객체를 그대로 로그에 남기지 않는 경로를 사용

## 7. 0.8.0 보안 감사 메모

- 비밀번호/알림/URL을 로그에 남기는 코드 경로 없음 (고정 오류 문자열 사용)
- GeckoView remote debugging/console output 비활성화 유지
- cleartext traffic false 유지. GeckoView의 HTTPS-only 강제 모드는 별도 정책 결정 필요
- 팝업 URL allowlist + rate limit
- 알림 HTTPS origin/site permission 검증 + rate limit
- 알림 클릭은 explicit Intent + 앱 메모리 opaque key로 검증
- 알림 삭제/스와이프는 내부 receiver로 WebNotification에 전달
- cross-origin Autofill node/form 차단 유지
- Keystore 키 무효화 시 평문 fallback 없음
- DB migration은 transaction + 이전 평문 table rollback 보존
- 탭/웹 알림 메모리 사용량과 장시간 성능은 실기기 UNVERIFIED

## 8. 네트워크 보안 정책

### HTTPS-only

GeckoRuntimeSettings의 `allowInsecureConnections`를 사용합니다.

- 기본값: `HTTPS_ONLY` (HTTP 차단)
- 사용자가 보안 패널에서 HTTPS-only를 끌 수 있음
- 설정은 `amare_android_browser` SharedPreferences에 저장
- 런타임 변경은 `setAllowInsecureConnections`로 적용
- HTTPS-only가 켜져 있으면 HTTP 주소는 로드되지 않음
- HTTPS-only를 끄면 HTTP 사이트를 열 수 있으며 보안 수준이 낮아짐

HTTP-only 사이트 호환성 영향:

- 기본 설정에서는 HTTP-only 사이트가 열리지 않음
- 사용자가 명시적으로 해제해야 함
- 인증서 오류와 별개이며 인증서 오류 우회는 제공하지 않음

### Mixed Content

GeckoView의 기본 Mixed Content 보호 정책을 사용합니다. AMARE가 별도로 active
content를 자동 허용하거나 우회하지 않습니다. GeckoView가 제공하는 mixed-content
우회 UI는 이번 버전에서 추가하지 않았습니다. 실제 사이트별 동작은 기기에서 UNVERIFIED입니다.

### 인증서 오류

- `onLoadError`에서 로드를 중단
- 인증서 예외를 자동 허용하지 않음
- 별도의 "위험 감수하고 계속" UI 없음

### 알림 클릭 프로세스 복원

알림 클릭 데이터는 Android Keystore AES-GCM으로 암호화한 payload로 PendingIntent에
포함합니다. 프로세스가 재시작되어도 암호화 payload를 검증한 뒤 HTTPS origin/source만
이동할 수 있습니다. 외부 앱이 Intent를 위조해도 복호화/검증을 통과할 수 없습니다.
암호화 실패 시 알림을 표시하지 않습니다.

## 9. 현재 설정 UI 범위

실제 기능과 연결된 설정:

- HTTPS-only On/Off
- 사이트별 카메라/마이크/위치/알림/저장소 권한 Allow/Deny/Reset
- 비밀번호 저장/수정/삭제 및 현재 사이트 저장 제안 재활성화
- 다운로드 Open/Remove 및 위험 파일 경고
- Debug 빌드 Diagnostics

아직 제공하지 않는 설정(가짜 토글을 추가하지 않음):

- 전체 방문 기록/쿠키/사이트 데이터 일괄 삭제 UI
- 기본 브라우저 역할 설정 UI
- Mixed Content 상세 정책 선택
- 다운로드 자동 재시도/중복 파일명 사용자 정책

## 10. 데이터/외부 Intent 보안

- 외부 Intent는 `ACTION_VIEW` + http/https + host 검증
- javascript/file/content/data URI 거부
- HTTPS-only가 켜진 경우 http 외부 링크 차단
- 외부 URL은 새 탭으로 열어 현재 탭 상태를 보존
- GeckoView `StorageController` flags를 사용해 데이터 종류를 분리
- 전체 삭제는 모든 탭 close 후 `ClearFlags.ALL`
- 전체 삭제 실패 시 부분 성공을 성공으로 표시하지 않음
- 다운로드 Open은 MediaStore content URI + MIME type 사용
- 전체 데이터 삭제 후에도 다운로드 파일은 남음을 UI에 명시
- 최근 닫은 탭은 메모리만 사용하며 앱 종료 후 사라짐

## 11. 검증 상태

| 항목 | 상태 |
|---|---|
| JVM Keystore blob format/version | PASS |
| AES-GCM round-trip | **Instrumentation PASS** (실기기) |
| 잘못된 키/IV/ciphertext/AAD 거부 | **Instrumentation PASS** |
| 키 삭제/무효화 | **Instrumentation PASS** |
| DB migration transaction | **Instrumentation PASS** |
| 신규 설치 평문 테이블 미생성 | **Instrumentation PASS** |
| 빈 legacy 테이블의 encrypted 데이터 보존 | **Instrumentation PASS** |
| 실제 강한 보안 키(StrongBox) | UNVERIFIED |
| 실제 기기 로그/백업 정책 | UNVERIFIED |
| 비밀번호 저장소 AES-GCM | **Instrumentation PASS** |
| 비밀번호 origin 격리 | JVM PasswordRules + **Instrumentation PASS** |
| 키 무효화 후 사용자 초기화 UI | 미구현 |

실행되지 않은 instrumentation 테스트는 PASS로 표시하지 않습니다. 2026-11-05
Lenovo TB138FC(Android 14)에서 13/13 PASS를 확인했습니다. GeckoView 자동완성 UI와
StrongBox/백업 정책은 여전히 UNVERIFIED입니다.
