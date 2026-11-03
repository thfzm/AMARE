# 세션 복구 설계

- 저장: Rust `SessionRepository` (SQLite transaction)
- UI: `MainWindow` + `SessionRestorePlanner` + `SessionSaveRetryPolicy`
- 관련 문서: [탭 수명주기](TAB_LIFECYCLE.md)

---

## 1. 저장 흐름

- 탭/내비게이션 변경 시 500ms debounce
- 저장 중복 방지: `SemaphoreSlim` gate + snapshot fingerprint
- retry: 최대 3회, 100/200/400ms backoff (`SessionSaveRetryPolicy`)
- 각 시도에 10초 timeout token
- 성공 시 `session_id`와 fingerprint 갱신
- 실패 시 기존 정상 세션을 삭제하지 않고 status만 표시

## 2. 정상 종료

`MainWindow.Closed`에서:

1. debounce timer 중지
2. gate 최대 8초 대기
3. UI thread에서 snapshot/fingerprint 캡처
4. off-UI `Task.Run`으로 native session save
5. 최대 8초 대기 후 WebView2/native 정리

무한 대기를 피하기 위해 timeout을 두었습니다.

## 3. 복원

`SessionRestorePlanner`:

- `SessionStateDto` null/빈 탭 → 복원 없음
- position/id 순 정렬
- 빈 URL → `amare://newtab`
- active tab ID를 index로 변환, 없으면 0
- 최대 100개 탭
- input DTO를 변경하지 않음

## 4. 손상/실패

- Rust session replace는 transaction이므로 old rows 보존
- session JSON/row가 손상되면 `LatestAsync` 오류를 UI가 표시하고 새 탭으로 시작
- 저장 실패 시 old session row 유지
- 비정상 종료 시 마지막 성공 debounce 저장을 복원

## 5. 검증 상태

Linux C# 테스트:

- null/empty restore
- ordering/active/fallback/cap
- retry policy
- tab lifecycle/snapshot

Windows 실제 종료/강제 종료/잠금 중 save는 **UNVERIFIED**입니다.
