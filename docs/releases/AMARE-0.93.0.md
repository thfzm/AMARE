# AMARE 0.93.0 — Navigation Evidence & Regression Reliability

0.93.0은 HTTP fixture에 request counter/readiness를 추가해 DirectReload와
Ctrl+R을 독립 증거로 검증한 버전입니다. 제품 코드는 변경하지 않았으므로
**0.93.0 패키지는 생성하지 않았고 0.92.0 패키지를 유지합니다.**
**GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- 0.81 `0xC0000409` 수정 유지: titlebar crash **0/5**, exit classifier **7/7**
- HTTP fixture:
  - `/fixture-a.html`, `/fixture-b.html` request counter
  - `/stats`, `/ready` readiness marker
  - unique request id, no-store
- **DirectReload 10/10 PASS**
  - API success, HTTP counter +1, source 유지
  - navigation counter는 일부 회차에서 지연
- **Ctrl+R 10/10 PASS**
  - ReloadPage callback, HTTP counter +1, source 유지
  - navigation counter는 일부 회차에서 지연
- A reload HTTP counter +1, B counter unchanged **PASS**
- B reload HTTP counter 변화 없음 **UNVERIFIED**
- B close/A close/process exit **UNVERIFIED** (0.89 PASS와 불일치)
- 새 창 C 생성 **PASS**
- 동일 코드 3회: **11/13**, **12/10**, **13/10** FAIL/UNVERIFIED
- Titlebar 5회: full PASS **3/5**, runner cancel 2회, crash **0/5**

## 1. HTTP fixture 계측

`tools/fixture_server.py`:

- `a`, `b` 요청 횟수
- 마지막 요청 시각
- unique `data-request-id`
- `Cache-Control: no-store`
- `/stats` JSON
- `/ready` readiness

- WebView2 counter와 HTTP counter를 독립 기록
- readiness 전 입력은 제품 실패로 계산하지 않음

## 2. DirectReload 10회

| 항목 | 결과 |
|---|---|
| API 호출/성공 | **10/10** |
| HTTP counter +1 | **10/10** |
| source 유지 | **10/10** |
| NavigationStarting/Completed | 일부 회차만 증가 |
| 종합 | **PASS (HTTP evidence)** |

## 3. Ctrl+R 10회

| 항목 | 결과 |
|---|---|
| ReloadPage keybinding | **10/10** |
| `reload_count` +1 | **10/10** |
| HTTP counter +1 | **10/10** |
| source 유지 | **10/10** |
| NavigationStarting/Completed | 일부 회차 지연 |
| 종합 | **PASS (HTTP evidence)** |

## 4. A/B 독립 입력/navigation

- A `fixture-a`, B `fixture-b` 로드 시도
- B load: source example.com을 거쳐 fixture-b, HTTP b=1 → **UNVERIFIED**
- A reload: HTTP a 23→24, b 1→1, B source 유지 → **PASS**
- B reload: HTTP b 1→1, A 유지 → **UNVERIFIED**
- B close: `b_valid=True` → **UNVERIFIED**
- A after B close reload: 변화 없음 → **UNVERIFIED**
- A last close/process exit: 실패 → **UNVERIFIED**
- 새 창 C: source fixture-a, IsWindow 확인 → **PASS**

## 5. First-click / Keyboard

- 0.89 SendInput first-click **6/6 PASS** 유지
- 0.93 재실행 **UNVERIFIED**
- 0.91 fixture 로드 후 Ctrl+L/T/W/N/text **PASS** 유지
- Ctrl+R HTTP evidence **10/10**

## 6. 동일 코드 3회 (`49d4962`)

| run | Aggregate | navigation |
|---|---:|---:|
| 45042412219 | **11/13** | 73/6/11 |
| 45042420684 | **12/10** | 70/7/9 |
| 45042427495 | **13/10** | 70/7/9 |

**3회 연속 0 FAIL 미달.**

## 7. Titlebar

| run | stage | exit | 판정 |
|---|---|---|---|
| 45042434897 | 32/0/1 | 0 | **PASS** |
| 45042443403 | 32/0/1 | 0 | **PASS** |
| 45042450874 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |
| 45042458782 | 32/0/1 | 0 | **PASS** |
| 45042466259 | 28/0/1 | cancel | ENVIRONMENT_LIMITATION |

- full PASS **3/5**
- crash **0/5**

## 8. WinUI

- `45042473578`: UI **48/48**, **143/0/9/1**, interop **70/70**
- Linux **SUCCESS**

GPUI/WinUI 합산 없음.

## 9. 패키지 정책

- 0.93.0은 test/fixture 변경만 포함 → **새 패키지 없음**
- 0.92.0 패키지 유지:
  - `AMARE-0.92.0-win-x64.zip` `1012168d...`
  - `AMARE-0.92.0-win-x64-unsigned.msix` `39fea08c...`
  - `AMARE-0.92.0-gpui-win-x64.zip` `79afdfbd...`
- 공개 GitHub Release 없음
- MSIX unsigned/install **UNVERIFIED/BLOCKED**

## 10. 스크린샷 / 문서

- 0.93.0에서 새로 생성한 PNG 없음
- 0.92.0 스크린샷/로그 보존
- 문서:
  - `docs/releases/AMARE-0.93.0.md`
  - `artifacts/windows/release/AMARE-0.93.0-development-report.md`
  - `README.md`
  - `docs/WINDOWS_VALIDATION.md`
  - `docs/GPUI_MIGRATION_GAPS.md`

## 최종 판정

WinUI를 기본 패키지로 유지합니다. HTTP request counter로 DirectReload와
Ctrl+R 각 10/10을 독립 검증했지만, A/B load/close/exit, A/B navigation,
3회 연속 0 FAIL, 실제 Windows 11/IME 검증이 남아 **GPUI 기본 전환은
보류**합니다.
