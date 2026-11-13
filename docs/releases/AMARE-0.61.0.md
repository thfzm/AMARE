# AMARE 0.61.0 — Real Input Path & GPUI Stability (Verification Incomplete)

0.61.0은 실제 사용자 입력 경로로 새 탭/새 창을 검증하고 GPUI 반복 안정성을 확보하는 안정화 버전이다. **그러나 실제 입력 경로 검증이 실패해 목표를 완료하지 못했다.**

## 결과

- Product code HEAD: `b3f5a14`
- Windows CI: **143/0/9/1**, WinUI GUI **42/42**
- Linux CI: **SUCCESS**
- GPUI run [43376017640](https://github.com/thfzm/AMARE/actions/runs/43376017640): **FAIL**
- GPUI 3회 연속 실행: **미달/미완료** — 첫 실행에서 실제 새 탭/새 창 경로가 FAIL
- Vault multi-window: 두 번째 창 생성 실패로 **1 PASS / 1 FAIL**
- CapturePreview normal/active/max/restore: **PASS**
- 실제 개별 탭 2개 캡처: **UNVERIFIED**

## 실제 입력 경로 결과

- **Ctrl+T/새 탭 버튼:** 4 tabs에서 실패 (`current=2`), list 생성이 반복 입력에서 진행되지 않음
- **Ctrl+N/새 창 버튼:** 두 번째 창이 생성되지 않음 (`windows=1`)
- 테스트 전용 trigger를 제거하고 실제 HWND mouse/key 입력과 포커스 arbitration을 사용했지만 해결하지 못했다.
- 새 탭 WebView2 controller 생성을 다음 effect cycle로 defer하는 제품 수정을 적용했지만, 입력 action 자체가 반복 run에서 tab strip을 갱신하지 못했다.
- Product UI E2E와 internal integration 결과를 합산하지 않는다.

## Vault / Pending

- 0.60.0의 Vault v2 구현은 유지했다.
- 0.61.0 run에서 두 번째 창 생성 실패로 multiwindow conflict/stale/lock/restart 검증을 완료하지 못했다.
- pending autofill 생성/취소 실제 click은 **UNVERIFIED**다. test-only pending 생성은 사용하지 않았다.
- DPAPI/plaintext/삭제/재시작 회귀는 0.60.0 결과를 유지하지만 0.61.0에서 재검증하지 못한 항목은 PASS로 승격하지 않는다.

## CapturePreview

- normal/active/maximize/minimize-restore DOM PNG **PASS**.
- 서로 다른 2개 탭 개별 캡처는 **UNVERIFIED**.
- CapturePreview PNG는 실제 image content를 검사했다.

## 스크린샷

`artifacts/windows/screenshots/0.61.0/` — PNG **51개**.

확보:

- 초기/새 탭, 일반 웹페이지, 설정, Vault 목록/저장 제안/DPAPI 오류, 다운로드, 단일 탭 multi-tab, CapturePreview, maximize/restore.

미확보(실제 multiwindow 실패):

- 두 번째 창, 두 창 동시 표시
- Vault conflict/stale rejection
- Vault lock propagation/unlock
- multi-window restart integrity
- multi-window maximized

미확보 파일을 PASS로 기록하지 않았다.

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.61.0-win-x64.zip` | 126,900,841 bytes | `f5c13b798532613fdac975c3372fe50f3c7195f781a8b0f6f93fdb1227896ff8` |
| `AMARE-0.61.0-win-x64-unsigned.msix` | 126,836,704 bytes | `b4128ed8eba32e6a1d46266e9c1773fb1079e5eca678ce8fe5cce1baf4bcc16d` |

Unsigned. MSIX install `0x80073D2C` UNVERIFIED/BLOCKED. 공개 Release 없음.

## 판정

**0.61.0은 안정화 목표를 완료하지 못했다.** 실제 Ctrl+T/새 탭 버튼과 Ctrl+N/새 창 버튼이 반복 run에서 동작하지 않았고, 이로 인해 Vault multiwindow/pending/titlebar/3회 반복 검증이 연쇄적으로 미완료됐다. 다음 단계는 GPUI custom titlebar hit-test와 keyboard focus/accelerator 전달을 실제 Windows에서 진단하는 것이다.
