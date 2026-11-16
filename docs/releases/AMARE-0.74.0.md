# AMARE 0.74.0 — Input-to-Navigation Reliability & Multi-Window Lifecycle

0.74.0은 AddressInput submit 경로와 titlebar multi-window HWND lifecycle을
증거 기반으로 분리 조사한 버전입니다. **GPUI 기본 전환은 승인하지 않았고
WinUI를 기본 패키지로 유지합니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- AddressInput telemetry: submit callback / dispatch / final Source 분리
- Navigation regression submit callback: **전 case PASS** (`focused=True`, `selected=True`, `result=dispatched`)
- Corrupt session 14-case: **14/14 PASS — 3회 모두**
- Session persistence core: **2회 PASS** (multi-window 2→2, active URL, scroll, geometry, maximized, immediate close, no-resurrection)
- Titlebar 별도 workflow: **16/0/0 PASS 2회**, 1회 window2 hang
- GPUI 3회 완전 반복(`80c2329`): **10/6**, **8/2**, **8/2**
- **3회 연속 0 FAIL 미달**

## AddressInput 조사 결과

추가 telemetry:

- `address_enter_count`
- `address_submit_count`
- `address_focused`
- `last_submit_at_ms`
- `last_submit_result` (`dispatched`, `queued-no-controller`, `settings-sync`)
- `last_submitted_len`
- `last_navigation_dispatch_at_ms`

확인된 사실:

- foreground=True, selected=True
- submit callback은 실제로 호출됨
- `result=dispatched`까지 도달
- root `key_down_count`는 AddressInput 내부 입력을 반영하지 않으므로 submit 판정에 부적합

남은 navigation regression 실패는 submit callback 이전이 아니라 **테스트의
second-window 상태 수집/restore fixture**에서 발생했습니다. submit callback과
dispatch를 navigation 실패로 오분류하지 않도록 분리했습니다.

## Titlebar multi-window

별도 workflow 3회:

| run | 결과 |
|---|---|
| 43858198011 | **16/0/0 PASS** |
| 43860855365 | **16/0/0 PASS** |
| 43860865292 | window2 진입 후 hang, canceled (9 PASS, FAIL 0) |

- window1 max/restore/minimize 후 window2 `IsWindow=true`를 단계별로 확인
- HWND invalid는 2회 재현되지 않음
- 1회는 window2 진입 후 harness hang
- **UNRESOLVED**: 제품 lifecycle인지 harness hang인지 확정하지 못함
- 테스트 삭제/무한 timeout 없이 별도 workflow로 유지

## Navigation fixture

- `[Uri]::new(path).AbsoluteUri`로 file URL 생성 통일
- fixture 존재 확인
- submit callback → request → pending/attach → source → loading=false 분리
- new-window/restore/corrupt-resave 일부는 second-window state count와
  restore banner/fixture에서 실패

## Session / corrupt

- corrupt 14-case: **14/0** 3회
- corrupt raw backup, mixed valid/invalid, resave/restart 유지
- session core: 2회 PASS
- unreachable URL probe는 request 미관측 시 UNVERIFIED로 기록하되 session
  실패로 오인하지 않도록 분리

## 3회 완전 반복

| run | Aggregate | corrupt | session | navigation |
|---|---:|---:|---:|---:|
| 43853054117 | **10 FAIL / 6 UNV** | 14/0/0 | 24/1/1 | 10/4 |
| 43853072690 | **8 FAIL / 2 UNV** | 14/0/0 | 22/1/1 | 12/2 |
| 43853085540 | **8 FAIL / 2 UNV** | 14/0/0 | 1/1/1 | 11/3 |

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.74.0-win-x64.zip` | 126,906,481 bytes | `77e17fb322b2019f1336ae6c2fe514d7fad1eb4c11e5b39f22a9bb418ea142bf` |
| `AMARE-0.74.0-win-x64-unsigned.msix` | 126,841,799 bytes | `66e93eaef4c6f74ff5759104b85056ff6599676d8d070d05985b1f4f609fce7b` |
| `AMARE-0.74.0-gpui-win-x64.zip` | 5,944,783 bytes | `e9b61e4d0bf7bca35f5b9523cd982df6e5a9945dd7a5aab3d439618811cc08d8` |

- **UNSIGNED**, MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. AddressInput submit 경로는 telemetry로
분리되었고 submit callback은 회귀 suite에서 통과했지만, 3회 연속 0 FAIL과
titlebar window2 hang, first-click 자동화, 실제 Windows 11 검증이 남아 있어
**GPUI 기본 전환은 계속 보류**합니다.
