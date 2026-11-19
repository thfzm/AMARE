# AMARE 0.91.0 — Keyboard Delivery Forensics & Reload Lifecycle Isolation

0.91.0은 0.90의 "첫 Ctrl+R 이후 키 입력 손실"을 세 가지 격리 시나리오로
재현한 버전입니다. **GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- 0.81 `0xC0000409` 수정 유지: titlebar crash **0/5**, exit classifier **7/7**
- reload target telemetry 추가 (`last_reload_tab_id`, `last_reload_url`)
- 격리 keyboard probe:
  - local HTTP fixture 로드 후 A(no reload), B(after one Ctrl+R), C(page interaction)
  - **fixture가 실제 로드된 뒤에는 Ctrl+L/T/R/W, text, post-reload key 모두 PASS**
  - 결과 **13 PASS / 1 UNVERIFIED** (초기 fixture-load race)
  - **0.90의 첫 reload 이후 key loss는 재현되지 않음**
- app `Responding=True`, status age 1.3s
- Direct WebView2 Reload API 및 A/B 독립 navigation은 **UNVERIFIED**
- 동일 코드 3회: **11/11**, **13/10**, **13/13** FAIL/UNVERIFIED
- Titlebar 5회: full PASS **2/5**, runner cancel 3회, crash **0/5**

## 1. 격리 시나리오

| scenario | 내용 | 결과 |
|---|---|---|
| A | fixture 로드 후 키 입력 반복 | Ctrl+L/T/R/W/text **PASS** |
| B | Ctrl+R 1회 후 키 입력 | reload PASS, Ctrl+L/T/text **PASS** |
| C | 페이지 상호작용 반복 | tab 생성 3회 **PASS** |

- fixture-load 최초 확인은 `about:blank` → **UNVERIFIED**
- 이후 `source=fixture-a.html` 확인
- keydown 수: Ctrl 계열 4, text 14~16
- keybinding action: `FocusAddress`, `NewTab`, `ReloadPage`, `CloseTab`
- app health: `responding=True`, status_age 1.3s

## 2. Keyboard delivery 경로

- SendInput/scan-code 입력 생성
- native `WM_KEYDOWN` 관측
- GPUI keystroke 수신
- keybinding action 실행
- 실제 제품 상태 변화
- **fixture 로드 후 전 단계 PASS**

0.90에서 관측된 `keydown=0`은 fixture가 실제로 로드되지 않은 상태의
**harness timing/race**와 일치합니다. 제품 키 처리 결함으로 확정하지 않습니다.

## 3. Ctrl+R / reload

- A 시나리오: `ReloadPage`, reload_count 0→1 **PASS**
- B 시나리오: first reload 이후 Ctrl+L/T/text **PASS**
- WebView2 navigation counter는 일부 run에서 증가
- Direct WebView2 Reload API 5회 검증은 **미구현/UNVERIFIED**
- callback만으로 PASS하지 않음

## 4. Focus / controller

- focus class `Zed::Window`
- `Chrome_WidgetWin_1` 전환 일부
- app responsive
- controller 재생성/focus 강제 변경 없음

## 5. First-click / 0.89 기준선

- 0.89 SendInput first-click **6/6 PASS** 유지
- 0.91 재실행은 **UNVERIFIED**
- Address selection `0,11→11,11` 유지

## 6. Multi-window

- 0.89 A/B 입력, B 종료 `IsWindow` PASS 유지
- 0.91 A/B 독립 navigation은 keyboard probe에서 미수행
- **UNVERIFIED / SKIPPED**

## 7. 동일 코드 3회 (`1e39d9b`)

| run | Aggregate | navigation |
|---|---:|---:|
| 44543575291 | **11/11** | 71/7/10 |
| 44543590852 | **13/10** | 70/7/9 |
| 44543605465 | **13/13** | 71/7/12 |

**3회 연속 0 FAIL 미달.**

## 8. Titlebar / crash

| run | stage | exit | 판정 |
|---|---|---|---|
| 44543621634 | 32/0/1 | 0 | **PASS** |
| 44543635990 | 33/0/0 | 0 | **PASS** |
| 44543651488 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |
| 44543665811 | 29/0/0 | cancel | ENVIRONMENT_LIMITATION |
| 44543680349 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |

- full PASS **2/5**
- crash **0/5**
- exit classifier **7/7**

## 9. WinUI / 패키지

- WinUI `44543695442`: UI **48/48**, **143/0/9/1**, interop **70/70**
- Linux **SUCCESS**

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.91.0-win-x64.zip` | 126,906,521 bytes | `c425d8787a10ec7594f6dab334bebdb3c19fa7e074961fb57a38ddd9d2eb2cc3` |
| `AMARE-0.91.0-win-x64-unsigned.msix` | 126,841,814 bytes | `7da3bc962994d1d820f45b7c3f77284f0f3e0ad00017529ee868215529e26275` |
| `AMARE-0.91.0-gpui-win-x64.zip` | 5,969,008 bytes | `e29d06ed8775e326599dfef1a043744d4a68dd852fb8672391e14b86b0cd5b9b` |

- manifest `git_commit=1e39d9b`, **UNSIGNED**
- MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. fixture가 실제 로드된 뒤에는 첫 reload
이후 키 입력 손실이 재현되지 않았고, 0.90 현상은 harness timing/race와
일치합니다. 그러나 Direct WebView2 Reload, A/B 독립 navigation, 3회 연속
0 FAIL, 실제 Windows 11/IME 검증이 남아 **GPUI 기본 전환은 보류**합니다.
