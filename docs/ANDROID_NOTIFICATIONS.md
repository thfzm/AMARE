# Android 웹 알림

- 기준: Android 0.10.0
- 관련 문서: [보안](ANDROID_SECURITY.md), [기기 테스트](ANDROID_DEVICE_TESTING.md)

## 1. API

GeckoView:

- `GeckoRuntime.setWebNotificationDelegate(WebNotificationDelegate)`
- `WebNotification`의 `show()`, `click()`, `dismiss()`
- 사이트 권한은 기존 `GeckoSession.PermissionDelegate.onContentPermissionRequest` 사용

Android:

- `NotificationManager`
- `NotificationChannel("amare_web_notifications")`
- Android 13+ `POST_NOTIFICATIONS` 런타임 권한

## 2. 권한 흐름

1. 사이트가 Notification API 권한 요청
2. AMARE가 기존 사이트 권한 dialog 표시
3. 허용 시 `notifications` site permission 저장
4. Android 13+이면 `POST_NOTIFICATIONS` 런타임 권한 요청
5. OS 권한이 허용된 경우에만 알림 표시

- 사이트 권한과 Android OS 권한은 별도
- OS 거부 시 notice 안내, 알림은 표시하지 않고 `notification.dismiss()`
- 사이트 권한 초기화/거부 시 해당 origin의 활성 알림 취소
- Never/Reset 동작은 기존 사이트 권한 저장소와 일치

## 3. 표시 정책

- HTTPS origin만 표시
- site permission이 `allow`인 경우만 표시
- origin별 rate limit: 5 notifications / 60초
- origin+tag 기준 중복 알림은 기존 알림 교체
- title/body/site origin/notification channel 사용
- 작은 아이콘은 AMARE 앱 아이콘
- channel lockscreen visibility `PRIVATE`
- notification visibility `PRIVATE`
- `silent`, `requireInteraction` 반영
- 알림 내용/URL/credential을 로그에 기록하지 않음

## 4. 클릭 및 취소

- 알림 클릭 `PendingIntent`는 explicit `MainActivity` + opaque SHA-256 key
- key는 메모리의 active notification map에 존재할 때만 유효
- 임의의 외부 Intent extra/key는 무시
- 같은 origin의 열린 탭이 있으면 활성화
- 없으면 새 탭 생성
- source URL은 notification origin과 동일한 HTTPS origin일 때만 사용
- 아니면 notification origin으로 fallback
- 클릭 시 `WebNotification.click()`
- 취소/자동 취소 시 `WebNotification.dismiss()`
- Android 알림 스와이프 삭제는 `NotificationActionReceiver`의 deleteIntent로
  WebNotification에 전달

0.9.0에서는 PendingIntent에 Android Keystore AES-GCM으로 보호한 click payload
(origin/source/tag)를 함께 넣습니다. 프로세스가 재시작되어도 payload를 검증한 뒤
같은 HTTPS origin/source로만 이동합니다. 위조 Intent는 복호화/검증을 통과할 수 없습니다.
프로세스가 살아 있을 때는 기존 opaque key map으로 `WebNotification.click()`도 호출합니다.
프로세스 재시작 후에는 WebNotification 객체가 없으므로 사이트로 이동만 하며 웹 콘텐츠에
click 이벤트를 전달하지 못합니다.

## 5. 보안 검토

- 알림 body/title은 로그에 남기지 않음
- origin은 `https://`만 허용
- site permission이 없거나 deny면 즉시 dismiss
- private browsing notification은 표시하지 않음 (현재 private mode 미지원)
- Android lockscreen에는 채널/알림 visibility private
- 팝업과 별도로 알림 rate limit 적용
- 알림 Intent는 explicit component + app-memory nonce로 검증

## 6. 제한

- Android OS AutofillService와 무관
- 알림 action 버튼 UI는 표시하지 않음
- 알림 그룹화/사이트별 channel은 단일 channel 정책
- 프로세스 종료 후 클릭 시 사이트 이동은 지원하지만 WebNotification.click() 전달은 미지원
- 실제 기기 알림 표시/클릭은 UNVERIFIED
