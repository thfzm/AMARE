# 프로필과 WebView2 분리

- Rust: `ProfileService` + per-profile SQLite (`profiles/<id>/amare.db`)
- WebView2: per-profile UserDataFolder (`<localappdata>/AMARE/webview2/<id>`)
- 관련 문서: [프로필 관리](PROFILE_MANAGEMENT.md), [WebView2 통합](WEBVIEW2_INTEGRATION.md)

---

## 1. 경로

```text
%LOCALAPPDATA%\AMARE\
├─ data\                       # Rust root DB + profiles/<id>/amare.db
└─ webview2\<profile-id>\       # WebView2 cookies/cache/local storage
```

`ProfilePathPolicy`:

- profile id: ASCII letter/digit/`-`/`_`/`.`만 허용
- `.`/`..`, 공백, slash, 128 bytes 초과 거부
- `Path.GetFullPath` 후 base directory prefix 검증

Rust `validate_profile_id`와 동일한 보안 경계를 C#에서 한 번 더 확인합니다.

## 2. 프로필 전환

1. 기존 세션 저장
2. 모든 WebView2 `Stop`/`Close`, host 제거
3. `_environment = null`
4. Rust `ProfileService.switch_profile`로 새 context 생성
5. 새 프로필 settings 로드
6. 새 UserDataFolder로 WebView2 environment 생성
7. 새 프로필 세션 복원/새 탭
8. 이전 context는 generation 불일치로 Rust에서 거부

WebView2 environment를 다른 프로필에 재사용하지 않습니다.

## 3. 프로필 삭제

- 마지막 프로필은 삭제하지 않습니다.
- 삭제 전 모든 WebView2를 닫습니다.
- Rust profile registry/DB 삭제 후 WebView2 UserDataFolder를 삭제합니다.
- WebView2 process가 폴더를 잠그면 삭제가 실패할 수 있습니다.
- `ProfileDataCleanup`이 최대 3~5회 재시도하고, 실패 시 `%LOCALAPPDATA%\AMARE\cleanup\pending-webview2-cleanup.json`에 기록합니다.
- 다음 앱 시작 시 pending cleanup을 재개합니다.
- Rust profile registry/DB 삭제 완료와 WebView2 파일 정리 완료를 구분해 표시합니다.
- 현재 UI는 active profile 삭제만 제공하며, 비활성 프로필 삭제 UI는 이후 Phase입니다.

## 4. 데이터 분리 확인 상태

- Rust profile DB isolation: Linux 테스트로 검증.
- WebView2 UserDataFolder isolation: 코드/경로 정책만 구현, Windows 실기기에서 쿠키/로그인/캐시 분리는 **UNVERIFIED**.
- WebView2 shared environment가 프로필 간 재사용되지 않도록 코드상 차단했지만, Windows process 동작은 실기기 검증이 필요합니다.
