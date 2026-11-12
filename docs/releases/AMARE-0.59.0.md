# AMARE 0.59.0 — Vault Sync and GPUI Stability

0.59.0은 0.58.0에서 FAIL/UNVERIFIED로 남은 Vault 다중 창 무결성, GPUI 자동화 flake, WebView2 페이지 캡처를 실제 Windows GUI에서 다시 검증한 안정화 릴리스다.

## Highlights

- 영속 tombstone과 per-entry revision을 vault format v2에 추가했다. 0.58의 encrypted array도 자동 migration한다.
- 같은 계정 동시 수정과 수정/삭제 충돌을 **거부**하고 최신 상태를 reload하는 conflict UI를 추가했다.
- stale 창이 삭제 계정을 다시 추가하지 못하는 tombstone merge를 구현했다.
- 저장을 process-wide mutex로 직렬화하고 temp+fsync+rename을 유지했다.
- 한 창의 잠금을 같은 process의 다른 창에 전파하고 decrypted entries/pending login을 제거한다.
- 실제 Windows GUI **same-process 두 창**에서 독립 추가/수정, 동일 계정 충돌, 수정/삭제 충돌, stale save, 잠금 전파, unlock, 재시작 무결성을 검증했다.
- WebView2 `CapturePreview`를 구현해 실제 DOM 렌더링 PNG를 확보했다.
- GPUI 자동화는 test trigger와 readiness 조건을 사용해 deterministic하게 만들었다.
- GPUI aggregate **0 FAIL / 16 UNVERIFIED**, password **51/0/0**, vault unit **11/11**, multi-window **20/0/1**.
- Windows **143/0/9/1**, WinUI GUI **42/42**.

## 검증된 Vault 동작

- 서로 다른 계정을 두 창에서 추가/수정하면 둘 다 보존된다.
- 같은 계정을 stale form에서 저장하면 충돌로 거부되고 최신 revision을 reload한다.
- 수정 중인 계정을 다른 창에서 삭제하면 수정 저장이 거부되고 삭제 상태가 유지된다.
- stale 창이 다른 계정을 저장해도 삭제된 계정은 tombstone으로 제거되어 부활하지 않는다.
- 재시작 후 tombstone과 revision이 유지되고, unlock만으로 vault hash가 바뀌지 않는다.
- 한 창에서 잠그면 다른 창도 즉시 locked가 되고 entries=0, `globally_locked=true`가 된다.

## 제한과 미검증

- 잠금 전에 window 2에서 login-save pending을 자동 생성하지 못해 **pending 자동 완성 취소는 UNVERIFIED**다. lock 시 pending을 지우는 코드 경로는 유지한다.
- 다중 창 테스트는 actual second window를 만들기 위해 test-only env `AMARE_GPUI_OPEN_SECOND_WINDOW=1`을 사용한다. 제품의 `open_new_window` 경로를 그대로 사용하지만, 사용자 키 입력으로 창을 만든 것은 아니다.
- 새 탭 반복 생성 테스트는 test-only trigger file을 사용한다. 제품 `new_tab` 경로를 사용하지만 keyboard shortcut 자체의 반복 안정성은 별도 UNVERIFIED다.
- GPUI titlebar min/max/restore/close 실제 click은 일부 run에서 상태를 바꾸지 못해 OS fallback과 **UNVERIFIED**로 기록했다.
- multi-window session restore는 2창 생성/종료는 PASS했지만 재시작 복원은 1창만 복원되어 **UNVERIFIED**다.
- session scroll restore, corrupt session error banner, Korean IME, context menu, DPI는 **UNVERIFIED**다.
- 여러 탭 각각의 개별 CapturePreview는 아직 자동화하지 않았고, active tab/maximize/minimize-restore 캡처만 PASS다.
- ALL_SITE 삭제는 0.58.0과 동일하게 **read-only/BLOCKED**다.
- unsigned MSIX 설치는 `0x80073D2C`로 **UNVERIFIED**다. 공개 GitHub Release는 만들지 않는다.

전체 결과와 해시는 `artifacts/windows/release/AMARE-0.59.0-development-report.md`에 기록한다.
