# Android 데이터 관리 및 기본 브라우저

- 기준: Android 0.10.0
- 관련 문서: [보안](ANDROID_SECURITY.md), [기기 테스트](ANDROID_DEVICE_TESTING.md)

## 1. 데이터 삭제 지원 범위

GeckoView `StorageController`의 실제 ClearFlags를 사용합니다.

| 설정 항목 | GeckoView flags/동작 | AMARE DB |
|---|---|---|
| 방문 기록 삭제 | 없음 | `history_enc` 전체 삭제 |
| 쿠키 삭제 | `COOKIES` | 변경 없음 |
| 캐시 삭제 | `ALL_CACHES` (network + image) | 변경 없음 |
| 사이트 저장 데이터 삭제 | `DOM_STORAGES` | 변경 없음 |
| 사이트 권한 초기화 | `PERMISSIONS | SITE_SETTINGS` | `site_permissions_enc` 전체 삭제 |
| 현재 사이트 데이터 삭제 | `clearDataFromHost(host, SITE_DATA)` | 해당 origin 권한 삭제 |
| 비밀번호 삭제 | 없음 | 별도 `amare_passwords.db` 전체 삭제 |
| 모든 브라우저 데이터 삭제 | `ALL`, 모든 탭 close 후 실행 | 북마크/기록/다운로드 기록/권한/session 전체 삭제 + 비밀번호 DB 삭제 |

### 명확한 경계

- **방문 기록 삭제**는 AMARE의 방문 기록 DB만 삭제합니다. 열린 탭의 back/forward 세션 history는 유지됩니다.
- **쿠키/캐시/사이트 저장 데이터 삭제**는 GeckoView 저장소만 삭제합니다.
- **사이트 데이터 삭제**와 **방문 기록 삭제**는 별개입니다.
- **사이트별 데이터 삭제**는 현재 탭 origin 기준이며, GeckoView API 특성상 그 사이트가 만든 third-party 데이터는 별도 범위입니다.
- **전체 삭제**는 열린 탭을 모두 닫은 뒤 GeckoView ALL clear를 수행합니다.
- 전체 삭제 후에도 Android MediaStore의 다운로드 파일은 자동 삭제하지 않습니다.
- 전부 성공하지 않으면 성공으로 표시하지 않고 실패 notice를 표시합니다.

### 삭제 실패

- GeckoView GeckoResult exception → 실패 notice
- DB clear 실패 → 실패 notice
- 일부만 성공한 경우 부분 성공을 성공으로 표현하지 않음

## 2. 기본 브라우저

Manifest:

```text
ACTION_VIEW
CATEGORY_DEFAULT
CATEGORY_BROWSABLE
scheme http
scheme https
```

Android `RoleManager.ROLE_BROWSER` 지원 여부를 확인하고 설정 화면에서 role request를 실행합니다.

외부 URL 처리:

- `ACTION_VIEW`만 처리
- http/https + host 존재 검증
- HTTPS-only 설정에서 http 외부 링크 차단
- 유효한 URL은 새 탭으로 열기
- 앱 restore 중이면 복원 완료 후 새 탭 열기
- javascript/file/content/data URL 거부

기본 브라우저가 아닐 때는 설정 화면에서 현재 상태를 표시하고 Android role 설정 화면을 연다.

## 3. 다운로드

- 파일명/MIME/진행률/크기/상태 표시
- 완료 파일 Open
- 위험 파일 확장자/MIME 경고
- 기록 Remove
- 전체 데이터 삭제 시 다운로드 기록만 삭제, 실제 파일은 유지
- 재시도는 WebResponse one-shot stream을 안전하게 재현할 수 없어 미구현
- 중복 파일명 처리는 MediaStore 동작에 맡기며 실제 기기 검증 필요

## 4. 최근 닫은 탭

- 최근 닫은 탭 최대 10개를 메모리에 보관
- URL/title/SessionState를 보관해 Reopen 시 GeckoSession 상태 복원 시도
- 앱 프로세스 종료 후 최근 닫은 탭 목록은 사라짐 (열린 탭 session snapshot과 별개)
- 모든 탭 닫기는 현재 탭을 모두 종료하고 새 홈 탭 생성

## 5. 검증 상태

| 항목 | 상태 |
|---|---|
| 외부 URL scheme/host 검증 | JVM PASS |
| GeckoView 데이터 삭제 실제 동작 | UNVERIFIED |
| DB 전체 삭제/권한 삭제 | 기기 UNVERIFIED |
| 기본 브라우저 role 요청 | 기기 UNVERIFIED |
| 최근 닫은 탭 복구 | 기기 UNVERIFIED |
| 다운로드 중복 파일명/재시도 | UNVERIFIED / 미구현 |
