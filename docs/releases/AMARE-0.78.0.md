# AMARE 0.78.0 — Restore Input State Machine & Window2 Lifecycle

0.78.0은 restore-first 입력 상태와 titlebar window2 readiness를 단계별로
분리한 버전입니다. **GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- 복원 attach 후 AddressInput 재focus 추가 (`address_restore_focus_count`)
- restore-first text input이 1~5자까지 관측되도록 개선
- restore-first submit callback은 여전히 **FAIL**
- corrupt-resave는 1회 submit PASS, 2회 FAIL
- Titlebar staged readiness: HWND/IsWindow/webview/loading/responsive PASS, **5/5 activation/window2 단계 실패**
- Corrupt session **14/14 PASS — 3회 모두**
- GPUI 3회: **9/10**, **8/11**, **9/17** — 0 FAIL 미달

## Restore 입력 단계

| 단계 | 결과 |
|---|---|
| HWND/window/tab 생성 | PASS |
| AddressInput mount/focus request | PASS |
| WebView2 attach | PASS |
| 복원 후 AddressInput 재focus | PASS (`address_restore_focus_count`) |
| text input count | **1~5자 관측** |
| Enter root count | 미관측 (AddressInput 소비) |
| submit callback | **FAIL** |
| navigation request/source | 미완료 |

- text 0 문제는 일부 완화
- text는 들어가지만 submit callback이 실행되지 않음
- submit 강제 호출/입력 강제 삽입 없음
- 정상 restore와 corrupt fallback 분리

## Titlebar staged readiness 5회

단계별 결과(공통):

- `window1-stage-hwnd` PASS
- `window1-stage-iswindow` PASS (live_windows=2)
- `window1-stage-webview` PASS
- `window1-stage-navigation` PASS (loading=false)
- `window1-stage-responsive` PASS
- 이후 activation/window2 단계에서 FAIL

5회 결과: **0 PASS / 5 FAIL**, 각 5 PASS + 1 FAIL.

- window2 original HWND 재탐색으로 성공 처리하지 않음
- hang/PASS 변환 없음
- 원인 **UNRESOLVED**

## 3회 완전 반복 (`4c4e8aa`)

| run | Aggregate | corrupt | session | navigation |
|---|---:|---:|---:|---:|
| 44016069168 | **9/10** | 14/0/0 | 23/1/0 | 18/4/6 |
| 44016083544 | **8/11** | 14/0/0 | **29/0/0** | 21/3/8 |
| 44016092478 | **9/17** | 14/0/0 | 11/1/1 | 21/4/7 |

## 개별 영역

- Password: 9/1, 9/1, 2/1
- Vault: 1/1, 1/1, 1/1 (vault unlock/form 첫 단계)
- Tab: 10/1/3, 10/1/2, **22/0/8**
- Library: 17/1, 3/1, 5/1
- Settings: **33/0/1**, 20/1/1, 16/1/1

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.78.0-win-x64.zip` | 126,906,481 bytes | `8a50af45be170a0d4d0cad957fe762b269010905dddab118f5dbc8ab7ac49d4e` |
| `AMARE-0.78.0-win-x64-unsigned.msix` | 126,841,777 bytes | `d136f8fb3c75e8d7babeed2fa2b7103a7a6ed4d96c33e93dc6573ecea1eccdd5` |
| `AMARE-0.78.0-gpui-win-x64.zip` | 5,960,685 bytes | `98c8f91b01baccbdb36cbca029b7c691d5ad50d897692a314fcdac95d2e9a1d8` |

- **UNSIGNED**, MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. restore text input은 개선됐지만 submit
callback FAIL, titlebar 0/5, first-click 자동화, 3회 연속 0 FAIL 미달로
**GPUI 기본 전환은 보류**합니다.
