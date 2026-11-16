# AMARE 0.76.0 — Titlebar Hang & Restore-First Navigation Root Cause

0.76.0은 titlebar multi-window hang, restore-first submit, first-click
자동화를 재현·분리 조사한 버전입니다. **GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- AddressInput 정확한 `address_bounds` 추가 → restore banner와 무관하게 주소 클릭
- Restore-first/corrupt-resave: exact bounds 사용 후에도 **submit callback FAIL**
- Titlebar 5회: **16/0/0 PASS 2회**, window2 hang 3회
- Corrupt session: **14/14 PASS — 3회 모두**
- Session persistence: **29/0/0** 1회, 조기 실패 2회
- Vault: **17/0/0** 1회, 13/1, 1/1
- GPUI 3회: **11/7**, **6/11**, **9/7** — 0 FAIL 미달

## Titlebar 조사

추가:

- window별 `Wait-WindowReady` (webview=true, loading=false)
- window2 ready 실패 시 UNVERIFIED 기록 후 입력 생략
- original HWND `IsWindow`/live count 단계별 기록

5회 결과:

| run | 결과 |
|---|---|
| 43891073335 | FAIL (hang, 9 PASS) |
| 43891084646 | FAIL (hang, 9 PASS) |
| 43891097123 | **16/0/0 PASS** |
| 43891107303 | FAIL (hang, 9 PASS) |
| 43891119894 | **16/0/0 PASS** |

- hang 3회는 window1 lifecycle 완료 후 window2 진입 단계
- 성공 2회는 두 창 전체 PASS
- hang을 PASS로 바꾸지 않음
- 원인은 여전히 harness/window activation 대기로 **UNRESOLVED**

## Restore-first navigation

- status `address_bounds`를 실제 click 좌표로 사용
- restore banner/좌표 문제 제거
- explicit submit 2회에도 `address_submit_count` 미증가
- new-tab/rapid/new-window submit은 **PASS**
- restore-first/corrupt-resave는 **FAIL**
- submit callback 미관측과 navigation 미완료를 분리

## 3회 완전 반복 (`26aeae5`)

| run | Aggregate | corrupt | session | vault | navigation |
|---|---:|---:|---:|---:|---:|
| 43891020135 | **11 FAIL / 7 UNV** | 14/0/0 | 29/0/0 | 13/1/0 | 11/7/2 |
| 43891043888 | **6 FAIL / 11 UNV** | 14/0/0 | 23/1/1 | 1/1/0 | 16/2/2 |
| 43891058469 | **9 FAIL / 7 UNV** | 14/0/0 | 2/1/1 | 17/0/0 | 14/4/2 |

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.76.0-win-x64.zip` | 126,906,492 bytes | `a1501b01f1122a1f49bb2fa798a7138eee20a3dc46a02394067b0ff4a5a619ac` |
| `AMARE-0.76.0-win-x64-unsigned.msix` | 126,841,787 bytes | `0fbcbb1104af90e6a1eb3e61901c44f96831ab9d100acf56484f2b594ee57fca` |
| `AMARE-0.76.0-gpui-win-x64.zip` | 5,946,137 bytes | `ccba3ab1d8620ae3d5d8ae5400dc19098d456b403dd6aa9693ba6b774c461e11` |

- **UNSIGNED**, MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. exact address bounds와 window readiness
gate로 진단은 개선됐지만, restore submit FAIL, titlebar 2/5 PASS, first-click
자동화, 3회 연속 0 FAIL 미달로 **GPUI 기본 전환은 보류**합니다.
