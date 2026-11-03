# 프로필 격리 테스트

- 실제 WebView2 probe: `tools/windows-verify/Probes.cs`의 `profile_isolation`
- 경로/서비스 정책 테스트: Linux C#/Rust tests

---

## 1. 실제 WebView2 probe가 확인하는 것

프로필 A/B에 서로 다른 UserDataFolder를 사용하고:

1. A에서 `document.cookie` 설정
2. A에서 localStorage 설정
3. A에서 IndexedDB 설정
4. A의 CookieManager cookie 확인
5. B에서 cookie/localStorage/IndexedDB/CookieManager가 보이지 않는지 확인

단순 경로 문자열 비교가 아니라 실제 브라우저 저장소를 검사합니다.

## 2. 이번 환경의 상태

- probe 코드는 존재하지만 **Windows WebView2에서 실행하지 않았습니다.**
- 따라서 쿠키/localStorage/IndexedDB 격리 결과는 **UNVERIFIED**입니다.
- Linux에서 검증한 것은 Rust profile DB 격리, C# 경로 정책, profile context generation 차단입니다.

## 3. 추가 확인 항목

- 프로필 A 로그인 세션과 B 로그인 세션 분리
- 프로필 전환 시 이전 WebView2 control/process 종료
- 프로필 삭제 후 UserDataFolder 정리
- 프로필 삭제 실패 시 pending cleanup 기록
- 전환 직후 old context 사용 시 `PROFILE_SWITCHED` 오류

## 4. Windows 11 절차

1. 프로필 A 생성 → 테스트 사이트 로그인
2. 프로필 B 생성 → 같은 사이트에서 로그인 안 됨 확인
3. A로 전환 → 로그인 유지 확인
4. 각 프로필의 쿠키/저장소가 분리되는지 확인
5. A 삭제 → A 폴더 삭제/잠금 여부 확인
6. 앱 재시작 후 pending cleanup 재개 확인
