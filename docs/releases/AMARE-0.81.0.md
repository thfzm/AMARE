# AMARE 0.81.0 — Window Crash Forensics & Restore Focus Ownership

0.81.0은 0.80의 `0xC0000409` crash와 restore-first focus/Enter 경로를 추적한
버전입니다. **GPUI 기본 전환은 승인하지 않았습니다.**

## 핵심 결과

- WinUI 기준선 유지: UI smoke **48/48**, **143/0/9/1**, interop **70/70**, Linux **SUCCESS**
- Exit code harness 수정: `0`, non-zero, `-1073740791`/`0xC0000409`, timeout,
  cancellation, invalid handle을 분리하고 classifier **7/7 PASS**
- `0xC0000409` 원인 확정: `gpui` `RefCell already borrowed` panic
  (`start_status_poll`가 paint 중 GPUI에 재진입) → `ucrtbase.dll` fail-fast
- 수정: status poll 제거, status write를 `render`로 이동, auto-capture는 `cx.defer`
- Titlebar 5회: **full PASS 3/5**, 2회 runner cancel, **crash 0/5**
- Restore-first: 주소 비우기와 text input은 성공, native focus 재확인 추가
- Restore-first Enter/submit: 일부 run PASS, 최종 3회는 **관측 실패**
- 3회 동일 코드: **13/14**, **13/18**, **13/28** FAIL/UNVERIFIED — 0 FAIL 미달
- Corrupt session **14/14 — 3회 모두**

## 1. Exit code harness

`tools/gpui-exit-code.ps1`가 signed/unsigned 종료 코드를 분류합니다.

| 입력 | 결과 |
|---|---|
| `0` | PASS / NORMAL_EXIT |
| `3` | FAIL / NONZERO_EXIT |
| `-1073740791` | FAIL / NTSTATUS / `0xC0000409` STATUS_STACK_BUFFER_OVERRUN |
| `3221226505` | FAIL / NTSTATUS / `0xC0000409` |
| timeout | UNVERIFIED / TIMEOUT |
| runner cancel | UNVERIFIED / ENVIRONMENT_LIMITATION |
| invalid process handle | UNVERIFIED / PROCESS_HANDLE_INVALID |

- 이전 `titlebar-multiwindow-exit`의 음수 exit code PASS 판정을 **FAIL**로 수정
- `tools/test-gpui-exit-code.ps1` 독립 테스트 **7/7 PASS**

## 2. `0xC0000409` crash forensics

0.80/0.81 초기 코드에서 두 번 재현했습니다.

- WER event: `Application Error`, faulting module **`ucrtbase.dll`**, exception
  **`0xc0000409`**, fault offset `0xa4aee`
- WER mini dump와 app stderr 확보
- panic: **`RefCell already borrowed`**
  - `gpui::app::async_context::update_entity`
  - `amare_gpui::start_status_poll::closure`
  - 호출 경로: `handle_paint_msg` → `draw_window` → `draw_roots` → status poll
- 즉, status poll task가 paint 중 GPUI `AppCell`을 다시 borrow하면서 panic,
  Rust panic이 Win32 callback을 넘지 못하고 `ucrtbase` fail-fast로 종료

수정:

- `start_status_poll` 제거
- status JSON은 `Render`에서 100ms throttle로 기록
- `AMARE_GPUI_AUTO_CAPTURE_URL`은 `cx.defer`로 paint 이후 실행

검증: titlebar 5회에서 `0xC0000409` **0회**. 3회 full PASS, 2회는 stage FAIL 없이
runner cancellation.

## 3. Win32 / GPUI focus ownership

- Win32 `GetForegroundWindow`, `GetGUIThreadInfo`, focus HWND/class/PID를
  status와 harness 양쪽에서 기록
- 정상 restore 시 OS focus는 대부분 `Zed::Window` (GPUI window)
- 일부 run에서 `Chrome_WidgetWin_1` (WebView2)이 keyboard focus를 가져감
- GPUI `address_focused=True`와 실제 Win32 focus는 별개임을 확인
- restore attach 후 `SetFocus(parent)`로 native focus를 재확인하는 최소 수정 추가

추가 발견: 기존 restore-first fixture가 **두 창 세션**을 restore하고 있었습니다.
new-window 테스트가 같은 data dir를 오염시켰고, harness가 `states[0]`를 임의로
선택했습니다. new-window를 별도 data dir로 분리해 restore-first는 단일 창
세션을 사용하도록 수정했습니다.

## 4. Restore-first text / Enter

- 주소 선택·삭제 후 text input은 **64~72자**까지 도달
- `key_count_delta == actual_len`인 run이 많아, 누락 문자는 GPUI 도달 전
  SendKeys 단계 손실로 분류 (**TEST_AUTOMATION_BUG**)
- clipboard paste fallback으로 74자 전체 입력 성공 (2/3 final runs)
- Enter keybinding은 키가 도달한 run에서
  `action=amare::SubmitAddress`로 **PASS**
- restore-first Enter는 최종 3회 모두 `SendKeys`, retry, raw `keybd_event`에서
  관측되지 않음 → **UNRESOLVED** (IME/pending input 가능성)
- submit callback: 과거 run 1회 PASS, 최종 3회 FAIL

## 5. 동일 코드 3회 (`8fd98e5`, 제품 `22f3c8e`)

| run | Aggregate | password | vault | tab | library | settings | session | corrupt | navigation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 44110522179 | **13/14** | 2/1 | 1/1 | 1/1 | 8/1 | 32/1/1 | 2/1 | 14/0 | 46/7/13 |
| 44110538472 | **13/18** | 2/1 | 2/1 | 1/1 | 4/1 | 20/1/1 | 2/1 | 14/0 | 42/7/17 |
| 44110559875 | **13/28** | 2/1 | 1/1 | 1/1 | 4/1 | 20/1/1 | 2/1 | 14/0 | 43/7/27 |

**3회 연속 0 FAIL 미달.** 실패는 password/vault/tab/library first-click,
settings persistence, restore Enter/submit, URL typing 손실에 집중됩니다.

## 6. 패키지

| 파일 | 크기 | SHA-256 |
|---|---:|---|
| `AMARE-0.81.0-win-x64.zip` | 126,906,501 bytes | `bd0eb03ea6dcf932bc0c92db289cdc7999a0cb6a81e32a362617c7db2392aad7` |
| `AMARE-0.81.0-win-x64-unsigned.msix` | 126,841,833 bytes | `031d6ea4d01c43d0ec3cd3c3a0f99cfaa218580a5e90d573a8a74802ebd9fbe4` |
| `AMARE-0.81.0-gpui-win-x64.zip` | 5,955,676 bytes | `c6e50c8d3b5b21757c6ceadae89ee321c8049b6787f8e9d048028f3b7a4a3d2d` |

- WinUI ZIP/MSIX manifest `git_commit=22f3c8e`, **UNSIGNED**
- MSIX install `0x80073D2C` **UNVERIFIED/BLOCKED**
- 공개 GitHub Release 없음

## 최종 판정

WinUI를 기본 패키지로 유지합니다. Crash root cause는 확정하고 수정했으며,
Win32/GPUI focus와 SendKeys 손실을 분리했습니다. 그러나 restore-first
Enter/submit, first-click, 3회 연속 0 FAIL, 실제 Windows 11/DPI 검증이 남아
**GPUI 기본 전환은 보류**합니다.
