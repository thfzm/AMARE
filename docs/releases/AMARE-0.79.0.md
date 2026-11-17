# AMARE 0.79.0 — Restore Submit Event Trace & Window2 Activation Root Cause

0.79.0은 restore submit 이벤트와 titlebar window2 activation을 추적한 버전입니다.
**GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- AddressInput text input / submit callback 분리 유지
- 명시적 Enter interceptor 시도 → **앱 startup crash** 확인 후 제거
- restore-first: text 0 또는 1자, submit callback **FAIL**
- corrupt-resave: 1회 submit PASS, 2회 FAIL
- Titlebar staged readiness: window1 단계 PASS, **window2 단계 5/5 FAIL**
- Corrupt session **14/14 PASS — 3회 모두**
- Session persistence **29/0/0** 2회
- GPUI 3회: **6/15**, **9/10**, **9/11** — 0 FAIL 미달

## Restore-first 이벤트 추적

| 단계 | 결과 |
|---|---|
| window/tab/AddressInput mount | PASS |
| focus/재focus | PASS |
| WebView2 attach | PASS |
| text input | 0 또는 1자 |
| root Enter count | 미관측 |
| submit callback | **FAIL** |
| navigation request/source | 미완료 |

- `on_key_event` 기반 Enter interceptor는 app이 status를 만들기 전에 crash
  → **PRODUCT_BUG**로 기록하고 즉시 제거
- 기존 `SubmitAddress` keybinding 경로 유지
- submit callback/강제 입력/focus 강제 재설정 없음
- 정상 restore와 corrupt fallback 분리

## Titlebar window2

- HWND_CREATED/valid PASS
- webview attach PASS
- navigation idle PASS
- process responding PASS
- activation/window2 stage에서 **5/5 FAIL**
- original HWND 교체/재탐색 PASS 처리 없음
- 원인 **UNRESOLVED**

## 3회 완전 반복 (`6cb21c8`)

| run | Aggregate | corrupt | session | vault | navigation |
|---|---:|---:|---:|---:|---:|
| 44027467431 | **6/15** | 14/0/0 | **29/0/0** | 11/1/0 | 21/3/8 |
| 44027482849 | **9/10** | 14/0/0 | 11/1/1 | 1/1/0 | 21/3/5 |
| 44027492044 | **9/11** | 14/0/0 | **29/0/0** | 16/1/0 | 19/4/6 |

## 개별 영역

- Password 9/1, 6/1, 9/1
- Vault 11/1, 1/1, 16/1
- Tab **23/0/6**, 10/1/3, 16/1/4
- Library 3/1, 4/1, 5/1
- Settings **33/0/1**, 25/1/1, 25/1/1

## 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.79.0-win-x64.zip` | 126,906,482 bytes | `d89ebc795f74aed86f221ff1c83c5d0729efeee9fb89914959ecf5278fabe77e` |
| `AMARE-0.79.0-win-x64-unsigned.msix` | 126,841,817 bytes | `9be37b410d16a00659e3738847c28e97e1558884cec11d0f1f5cb89c2169007f` |
| `AMARE-0.79.0-gpui-win-x64.zip` | 5,961,008 bytes | `8e744653dd329dbb1f23ee91d27efa10bde9b03d787e01990b364c258a477b1e` |

- **UNSIGNED**, MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. Enter interceptor 회귀를 제거했지만
restore submit FAIL, titlebar 0/5, first-click 자동화, 3회 연속 0 FAIL 미달로
**GPUI 기본 전환은 보류**합니다.
