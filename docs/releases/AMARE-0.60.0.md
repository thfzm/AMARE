# AMARE 0.60.0 — GPUI Stability & Reliability

0.60.0은 Vault v2와 CapturePreview를 회귀 검증하고, GPUI Windows 자동화의 반복 안정성을 측정한 안정화 릴리스다. 신규 기능 확장은 하지 않았다.

## 결과 요약

- Vault v2 multi-window: **20 PASS / 0 FAIL / 1 UNVERIFIED**
- Vault unit: **11/11 PASS**
- WebView2 CapturePreview: 실제 DOM PNG **PASS**
- Windows CI: **143/0/9/1**, WinUI GUI **42/42**
- Linux CI: **SUCCESS**
- GPUI 동일 commit 3회:
  - 1회: **0 FAIL / 17 UNVERIFIED**
  - 2회: **0 FAIL / 16 UNVERIFIED**
  - 3회: password autofill FAIL (11/1/0), 이후 suite skipped — **3회 연속 0 FAIL 기준 미달**
- 최종 판정: **반복 안정성 3/3 미달**. 2회 연속 0 FAIL만으로 안정성을 주장하지 않는다.

## 유지된 Vault v2 동작

- persistent tombstone + per-entry revision
- 같은 계정 동시 수정 conflict rejection + reload
- 수정/삭제 충돌 거부
- stale 창의 삭제 계정 부활 방지
- cross-window lock propagation
- DPAPI vault 보존, restart/unlock 무결성
- pending autofill cancel 코드는 유지했지만, 실제 pending 생성 자동화가 실패해 **UNVERIFIED**

## GPUI 안정화 조치

- foreground arbitration (`AttachThreadInput`, BringWindowToTop, SetFocus)
- test별 고유 data/status, readiness wait
- failure gate 유지
- titlebar click 실패는 OS fallback + UNVERIFIED
- 같은 code 반복 실행 결과를 개별 보존

## 남은 문제

- password autofill suggestion hit-test가 반복 run에서 간헐적으로 form을 채우지 못함
- 새 탭 생성이 일부 run에서 2 tabs 이후 진행되지 않고 step timeout 발생
- 두 번째 창을 사용자 Ctrl+N/버튼 입력만으로 여는 경로가 불안정해 test-only startup env를 사용했다. 제품 `open_new_window` 경로를 사용하지만 사용자 입력 경로의 안정성은 **UNVERIFIED**다.
- 반복 새 탭 자동화는 test-only new-tab trigger를 사용했다. 제품 `new_tab` 경로를 사용하지만 실제 Ctrl+T/버튼 입력 반복성은 **UNVERIFIED**다.
- pending autofill 취소 실제 click, 개별 탭 CapturePreview, titlebar 실제 click, multi-window session restore, scroll/corrupt session은 **UNVERIFIED**다.
- ALL_SITE deletion은 **read-only/BLOCKED**다.
- unsigned MSIX install은 `0x80073D2C`로 **UNVERIFIED**다.

공개 GitHub Release는 생성하지 않는다. 전체 상세와 해시는 개발 보고서에 기록한다.
