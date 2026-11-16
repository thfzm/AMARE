# AMARE 0.77.0 — Input Event Forensics & Multi-Window Hang Isolation

0.77.0은 AddressInput 내부 이벤트, restore-first submit, titlebar window2
hang을 증거 기반으로 분리 조사한 버전입니다. **GPUI 기본 전환은 승인하지
않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- AddressInput 내부 text/focus/edit telemetry 추가
- **복원된 active WebView2가 address focus를 steal하던 제품 경로 수정**
- new-tab/rapid/new-window: text input + submit callback **PASS**
- restore-first: 한 run은 text input PASS/submit FAIL, 한 run은 text input 0
- Corrupt session **14/14 PASS — 3회 모두**
- Titlebar 5회: **18/0/0 PASS 1회**, 3회 11 PASS 후 window2 미검증, 1회 window2 FAIL
- GPUI 3회: **12/9**, **9/10**, **9/13** — 0 FAIL 미달

## AddressInput 조사

추가 telemetry:

- `address_text_input_count`, `address_mark_input_count`
- `address_set_count`, `address_last_text_len`, `address_last_edit_at_ms`
- `address_focus_gained_count`, `address_focus_lost_count`
- `address_enter_count`, `address_submit_count`
- `last_submit_result`, `last_navigation_dispatch_at_ms`

결과:

- root keydown은 AddressInput Enter를 관측하지 못함 → enter counter는 판정에 부적합
- text input counter로 실제 주소 입력 전달을 분리
- new-tab/rapid/new-window text + submit callback PASS
- restore-first:
  - run A: text 17 PASS, submit FAIL
  - run B: text 0 FAIL
  - run C: text 19 PASS, submit FAIL
- corrupt-resave도 run별 text 11/73/1로 불안정
- 복원 attach에서 `view.focus()`가 address focus를 덮어쓰던 경로를 제거
- product 변경 후에도 restore submit은 **UNRESOLVED**

## Titlebar 5회

| run | 결과 |
|---|---|
| 43918816847 | FAIL (11 PASS, window2 미검증) |
| 43918827730 | FAIL (11 PASS) |
| 43918837666 | FAIL 6 PASS / 2 FAIL / 2 UNV |
| 43918849242 | FAIL (11 PASS) |
| 43918859471 | **PASS 18/0/0** |

- 성공 run은 두 창 전체 PASS
- 실패 3회는 window2 readiness 이후 진행되지 않음
- 실패 1회는 window2 상태 FAIL
- ready/responding gate가 hang을 PASS로 바꾸지 않음
- **1/5 PASS**, window2 hang/lifecycle **UNRESOLVED**

## 3회 완전 반복 (`e6bee49`)

| run | Aggregate | corrupt | session | vault | navigation |
|---|---:|---:|---:|---:|---:|
| 43918766926 | **12/9** | 14/0/0 | 23/1/0 | 7/1/0 | 18/6/8 |
| 43918788385 | **9/10** | 14/0/0 | 1/1/1 | 1/1/0 | 20/4/5 |
| 43918802317 | **9/13** | 14/0/0 | **29/0/0** | 1/1/0 | 15/5/8 |

## 개별 영역

- Password 3/1/0, 9/1/0, 9/1/0
- Vault 7/1/0, 1/1/0, 1/1/0
- Tab 6/1/0, 10/1/3, 16/1/4
- Library 4/1/0, 6/1/0, 5/1/0
- Settings 25/1/1, 33/0/1, 33/0/1

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.77.0-win-x64.zip` | 126,906,481 bytes | `3476781b03693c2f1e6b94c91dbd42aabbb5db6f1c952d20d7b250af34aa3635` |
| `AMARE-0.77.0-win-x64-unsigned.msix` | 126,841,823 bytes | `1c22bae963f96529f78d603ce523bdd1cb84837e5ab7bd4a933c499133ecb40d` |
| `AMARE-0.77.0-gpui-win-x64.zip` | 5,960,807 bytes | `2c7c47f99e0d64c6c485151a33e4b8cabc94c8ac0d63ac34fa1fd1757ff66d7d` |

- **UNSIGNED**, MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. restored focus steal 수정과 AddressInput
내부 telemetry는 진전이지만 restore submit, titlebar 1/5, first-click 자동화,
3회 연속 0 FAIL 미달로 **GPUI 기본 전환은 보류**합니다.
