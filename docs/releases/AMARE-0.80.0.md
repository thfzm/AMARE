# AMARE 0.80.0 — Restore Submit Root Cause & GPUI Window Activation Trace

0.80.0은 restore submit 이벤트와 Win32/GPUI activation을 추적한 버전입니다.
**GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- 0.79 explicit Enter interceptor 잔존 없음 확인
- 40ms submit dedupe 제거 (도움 증거 없음)
- restore text input: 0 / 30 / 0자, submit callback **FAIL**
- corrupt-resave: 1 PASS / 2 FAIL
- Titlebar Win32 trace: ShowWindow/SetForegroundWindow true, window2 foreground 전환 확인
- GPUI `window_active=true` 확인 (일부 UNVERIFIED)
- Titlebar 5회: **31/0/0 PASS 2회**, 나머지 3회는 후속 단계 실패
- Corrupt **14/14 — 3회 모두**
- GPUI 3회: **10/10**, **10/7**, **10/8** — 0 FAIL 미달

## 0.79 변경 영향

- explicit Enter interceptor: 최종 코드에 없음
- dedupe: 실제 restore submit을 돕지 않았고 정상 연속 입력을 차단할 수 있어 제거
- `submit_address`는 기존 keybinding 단일 경로로 복귀
- restore attach 후 AddressInput 재focus 유지

## Restore-first trace

| 단계 | 결과 |
|---|---|
| mount/focus/refocus/attach | PASS |
| text input | 0 / 30 / 0 |
| Enter root event | 미관측 |
| submit callback | **FAIL** |
| navigation/source | 미완료 |

- text 30자 run에서도 submit callback 미실행
- 정상 restore와 corrupt fallback 분리
- 원인 **UNRESOLVED**

## Titlebar Win32/GPUI activation

Win32:

```text
show=True setForeground=True
beforeFg=window1 afterFg=window2
afterActive=0 afterFocus=0
```

- SetActiveWindow/SetFocus 반환 0
- foreground HWND는 window2로 변경
- GPUI `window_active=true` 대부분 확인
- 실패는 activation API가 아니라 이후 window2 조작/종료 단계
- 원인 **UNRESOLVED** (harness vs lifecycle)

## 3회 완전 반복 (`1a23e2e`)

| run | Aggregate | corrupt | session | vault | navigation |
|---|---:|---:|---:|---:|---:|
| 44035390439 | **10/10** | 14/0/0 | 2/1/0 | 13/1/0 | 15/5/5 |
| 44035408395 | **10/7** | 14/0/0 | 23/1/0 | 6/1/0 | 18/5/6 |
| 44035419170 | **10/8** | 14/0/0 | 1/1/1 | 16/1/0 | 18/5/6 |

## 개별 영역

- Password 3/1, 9/1, 9/1
- Vault 13/1, 6/1, 16/1
- Tab 16/1/4, 6/1/0, 6/1/0
- Library **20/0/0**, 17/1, 17/1
- Settings 25/1/1, **33/0/1**, **33/0/1**

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.80.0-win-x64.zip` | 126,906,469 bytes | `09bf02cdd4fa98b7fe770c90b501d5e488ae90d51bb404b2812ea4f2182652bb` |
| `AMARE-0.80.0-win-x64-unsigned.msix` | 126,841,780 bytes | `2cd0ad72371fe7e657cf87696af2bab8bee648be0b6db38e434845a79932cf32` |
| `AMARE-0.80.0-gpui-win-x64.zip` | 5,960,627 bytes | `930e1badaa133f5ea438604e4e914464dbe0ac74f0a1a1c4a693f70955b1d600` |

- **UNSIGNED**, MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. dedupe와 activation trace로 원인 범위를
좁혔지만 restore submit FAIL, titlebar 2/5, 3회 연속 0 FAIL 미달로 **GPUI
기본 전환은 보류**합니다.
