# AMARE 0.75.0 — Navigation Lifecycle & Automation Reliability

0.75.0은 AddressInput submit 정상 동작을 유지하면서 navigation 후속 단계,
second-window 상태 수집, titlebar multi-window hang, first-click 자동화를
분리 조사한 버전입니다. **GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- Navigation lifecycle telemetry: tab별 `navigation_started`, `navigation_completed`, 시작/완료 timestamp
- AddressInput submit callback: new-tab, rapid-submit, new-window **PASS**
- Corrupt session 14-case: **14/14 PASS — 3회 모두**
- Session persistence: **29/0/0** 2회 (multi-window 2→2, active URL, scroll, geometry, maximized, immediate close, no-resurrection)
- Vault: **29/0/0** 2회
- Titlebar 별도 workflow 5회: **16/0/0 PASS 1회**, window2 hang 4회
- GPUI 3회 완전 반복(`8618b0e`): **9/3**, **9/12**, **10/7**
- **3회 연속 0 FAIL 미달**

## Navigation lifecycle

추가 상태:

- tab별 `navigation_started`, `navigation_completed`
- `last_started_at_ms`, `last_completed_at_ms`
- window `navigation_requests`, `last_navigation_url`, `pending_navigation`
- AddressInput `address_submit_count`, `last_submit_result`, `address_focused`

검증 결과:

- new-tab immediate/rapid/new-window submit callback PASS
- submit callback과 NavigationStarting/Completed/Source를 분리
- restore-first/corrupt-resave는 submit callback 미관측 FAIL
- request/dispatch/source를 submit 성공으로 간주하지 않음

## Titlebar 5회

| run | 결과 |
|---|---|
| 43881549062 | FAIL (hang after window1, 9 PASS) |
| 43881560526 | **PASS 16/0/0** |
| 43881572565 | FAIL (hang, 9 PASS) |
| 43881583692 | FAIL (hang, 9 PASS) |
| 43881595181 | FAIL (hang, 9 PASS) |

- 성공 run은 window1 lifecycle check + window2 max/restore/minimize PASS
- 실패 run은 window2 진입 전/직후 harness hang
- HWND invalid는 성공 run에서 재현되지 않음
- 1/5만 PASS, **3회 연속 조건 미달**
- 별도 workflow 결과를 blocking suite와 합산하지 않음

## Session / corrupt

- corrupt 14/0 3회
- session core 2회 PASS
- unreachable/local request probe는 session 실패와 분리
- second-window collection은 per-window status/HWND를 사용

## 3회 완전 반복

| run | Aggregate | corrupt | session | vault | navigation |
|---|---:|---:|---:|---:|---:|
| 43881498684 | **9 FAIL / 3 UNV** | 14/0/0 | 29/0/0 | 1/1/0 | 14/4 |
| 43881520833 | **9 FAIL / 12 UNV** | 14/0/0 | 1/1/1 | 8/1/0 | 9/5 |
| 43881534451 | **10 FAIL / 7 UNV** | 14/0/0 | 29/0/0 | 7/1/0 | 13/5 |

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.75.0-win-x64.zip` | 126,906,499 bytes | `14fb8a1e4ebef29c3ca9b31e4f255f762f47f0c966ca4e05dd20f1d1eb70b3e3` |
| `AMARE-0.75.0-win-x64-unsigned.msix` | 126,841,797 bytes | `588cbd423202a3893c12311490cd9e7d57d7c09a1e1f34a52cf9222679639979` |
| `AMARE-0.75.0-gpui-win-x64.zip` | 5,944,775 bytes | `7ebdd08755dde8804f1d416df3e305a9a2c2b9877e7b3c13ad7b96a54c6a922a` |

- **UNSIGNED**, MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. navigation lifecycle telemetry, corrupt
14/14, session core, Vault 29/0, submit callback은 개선됐지만, 3회 연속 0 FAIL,
restore/corrupt submit, first-click 자동화, titlebar 1/5 PASS, 실제 Windows 11
검증이 남아 있어 **GPUI 기본 전환은 보류**합니다.
