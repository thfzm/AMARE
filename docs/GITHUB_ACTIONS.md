# GitHub Actions 기반 Windows 자동 검증

- 작성일: 2026-11-04
- 단계: Phase 2.2 Windows 검증 게이트/Release Candidate
- 목적: Ubuntu에서 개발 → GitHub Push → Windows Runner 빌드/테스트 → Artifacts 확인
- 원칙: **CI에서 검증하지 못한 항목은 PASS로 처리하지 않는다.**
- 현재 상태: Windows 0.82.0 WinUI Debug/Release build + GPUI password security/iframe boundary/multiwindow merge/privacy/browser settings/session restore/downloads/custom titlebar/icon/bookmarks/history/screenshots/Korean IME/exit validation, portable ZIP + updater helper, unsigned MSIX, Kestrel TLS handshake + 실제 self-signed/expired/hostname-mismatch event/dialog+retry/back/other-tab, native drag 9개 조합+cancel+after-close, cross-window 정/역방향, actual AMARE WebSocket tab/window cleanup, video/fullscreen/PiP, SQLite/WAL recovery, 5/10/20/30-tab memory, clean path launch, C#/Rust interop 70/70, GUI 42/42, release preflight **PASS** (Windows Server 2025 Runner). local trusted CA는 CurrentUser trust-store timeout으로 **UNVERIFIED**, CA chain/custom-trust simulation은 **PASS**, native tear-off+re-merge **PASS**, 30분 keyboard-only stress **SUCCESS**, handle type31/type8 증가 추세 **UNVERIFIED**, 빠른 연속 drag/tear-off/이동 중 창 종료/Windows 11 GUI도 **UNVERIFIED**입니다. 0.82.0은 WinUI 기준선(48/48, 143/0/9/1, interop 70/70)과 0.81 crash 수정/exit classifier를 유지했습니다. Win32 메시지 추적에서 restore-first Enter는 foreground·external focus·window_active·AddressInput focus가 모두 정상인데도 `WM_KEYDOWN VK_RETURN`이 앱 HWND에 도달하지 않았고, new-window/corrupt Enter는 `SubmitAddress`까지 PASS했습니다. 원인은 **UNRESOLVED**입니다. titlebar는 crash 0/5·full 2/5, corrupt **14/14**, 동일 코드 3회 **13/13/13 FAIL**로 0 FAIL에 미달했습니다. 실제 Windows 11/IME는 **UNVERIFIED**입니다. Android CI는 0.14.0 최종 코드 `9110ddc`에서 **SUCCESS**했으며 Windows-only 변경에서는 path filter로 실행되지 않습니다.
- 0.52.0 최종 코드 검증 run: [Windows #42656165390](https://github.com/thfzm/AMARE/actions/runs/42656165390) **success**, [Linux #42656165445](https://github.com/thfzm/AMARE/actions/runs/42656165445) **success**, [GPUI #42656165553](https://github.com/thfzm/AMARE/actions/runs/42656165553) **success**, Android [0.14.0 #41952767737](https://github.com/thfzm/AMARE/actions/runs/41952767737) **success**
- 0.52 GPUI Windows run: [#42654159337](https://github.com/thfzm/AMARE/actions/runs/42654159337) **success** (WebView2 download progress/complete/failure, actual 0.52 screenshots, download/ library, titlebar/desktop captures; download 5/0/1, library 13/0/1, input 26/0/3)
- 0.53.0 최종 코드 검증 run: [Windows #42720515760](https://github.com/thfzm/AMARE/actions/runs/42720515760) **success**, [Linux #42720516059](https://github.com/thfzm/AMARE/actions/runs/42720516059) **success**, [GPUI #42720515770](https://github.com/thfzm/AMARE/actions/runs/42720515770) **success** (single-window session restore, `15`/`16` screenshots, download restart `12`, library 18/0/0, download 7/0/1, input 26/0/3, tab 20/0/6)
- 0.54.0 최종 코드 검증 run: [Windows #42761911386](https://github.com/thfzm/AMARE/actions/runs/42761911386) **success**, [Linux #42761911384](https://github.com/thfzm/AMARE/actions/runs/42761911384) **success**, [GPUI #42761911210](https://github.com/thfzm/AMARE/actions/runs/42761911210) **success** (settings UI/search/reset/restart persistence, two-window restore, `11`/`12` screenshots; settings 18/0/2, library 18/0/0, download 7/0/1, input 25/0/4, tab 20/0/6)
- 0.55.0 최종 코드 검증 run: [Windows #42819548216](https://github.com/thfzm/AMARE/actions/runs/42819548216) **success**, [Linux #42819548309](https://github.com/thfzm/AMARE/actions/runs/42819548309) **success**, [GPUI #42822606900](https://github.com/thfzm/AMARE/actions/runs/42822606900) **success** (DPAPI vault/login autofill, password 18 PASS/0 FAIL/1 UNVERIFIED, unit 4/4, WinUI 142/0/10/1, actual 0.55 screenshots)
- 0.56.0 최종 코드 검증 run: [Windows #42840083476](https://github.com/thfzm/AMARE/actions/runs/42840083476) **success**, [Linux #42840083317](https://github.com/thfzm/AMARE/actions/runs/42840083317) **success**, [GPUI #42840083529](https://github.com/thfzm/AMARE/actions/runs/42840083529) **success** (password 25 PASS/0 FAIL/0 UNVERIFIED, unit 4/4, WinUI 143/0/9/1 GUI 42/42, delete cancel/confirm/restart, corrupt-vault preservation, actual 0.56 screenshots)
- 0.57.0 최종 코드 검증 run: [Windows #42893279844](https://github.com/thfzm/AMARE/actions/runs/42893279844) **success**, [Linux #42893280004](https://github.com/thfzm/AMARE/actions/runs/42893280004) **success**, [GPUI #42893279948](https://github.com/thfzm/AMARE/actions/runs/42893279948) **success** (password 42/0/0, iframe/nested/action no-capture, delete/empty/restart, DPAPI corruption, multiwindow screenshot; WinUI 143/0/9/1 GUI 42/42)
- 0.82.0 검증 run: [Windows #44140410424](https://github.com/thfzm/AMARE/actions/runs/44140410424) **success**, [Linux #44140410517](https://github.com/thfzm/AMARE/actions/runs/44140410517) **success**, GPUI 3회 [#44140459564](https://github.com/thfzm/AMARE/actions/runs/44140459564) 13/20, [#44140476371](https://github.com/thfzm/AMARE/actions/runs/44140476371) 13/23, [#44140491440](https://github.com/thfzm/AMARE/actions/runs/44140491440) 13/25, titlebar 5회 full 2 PASS/crash 0
- 0.81.0 검증 run: [Windows #44099436225](https://github.com/thfzm/AMARE/actions/runs/44099436225) **success**, [Linux #44110507502](https://github.com/thfzm/AMARE/actions/runs/44110507502) **success**, GPUI 3회 [#44110522179](https://github.com/thfzm/AMARE/actions/runs/44110522179) 13/14, [#44110538472](https://github.com/thfzm/AMARE/actions/runs/44110538472) 13/18, [#44110559875](https://github.com/thfzm/AMARE/actions/runs/44110559875) 13/28, titlebar 5회 full 3 PASS/crash 0
- 0.80.0 검증 run: [Windows #44040753851](https://github.com/thfzm/AMARE/actions/runs/44040753851) **success**, [Linux #44035390244](https://github.com/thfzm/AMARE/actions/runs/44035390244) **success**, GPUI 3회 [#44035390439](https://github.com/thfzm/AMARE/actions/runs/44035390439) 10/10, [#44035408395](https://github.com/thfzm/AMARE/actions/runs/44035408395) 10/7, [#44035419170](https://github.com/thfzm/AMARE/actions/runs/44035419170) 10/8, titlebar 5회 2 PASS
- 0.79.0 검증 run: [Windows #44027467341](https://github.com/thfzm/AMARE/actions/runs/44027467341) **success**, [Linux #44027467410](https://github.com/thfzm/AMARE/actions/runs/44027467410) **success**, GPUI 3회 [#44027467431](https://github.com/thfzm/AMARE/actions/runs/44027467431) 6/15, [#44027482849](https://github.com/thfzm/AMARE/actions/runs/44027482849) 9/10, [#44027492044](https://github.com/thfzm/AMARE/actions/runs/44027492044) 9/11, titlebar 5회 0 PASS
- 0.78.0 검증 run: [Windows #44021496091](https://github.com/thfzm/AMARE/actions/runs/44021496091) **success**, [Linux #44016069309](https://github.com/thfzm/AMARE/actions/runs/44016069309) **success**, GPUI 3회 [#44016069168](https://github.com/thfzm/AMARE/actions/runs/44016069168) 9/10, [#44016083544](https://github.com/thfzm/AMARE/actions/runs/44016083544) 8/11, [#44016092478](https://github.com/thfzm/AMARE/actions/runs/44016092478) 9/17, titlebar 5회 0 PASS
- 0.77.0 검증 run: [Windows #43918766969](https://github.com/thfzm/AMARE/actions/runs/43918766969) **success**, [Linux #43918767315](https://github.com/thfzm/AMARE/actions/runs/43918767315) **success**, GPUI 3회 [#43918766926](https://github.com/thfzm/AMARE/actions/runs/43918766926) 12/9, [#43918788385](https://github.com/thfzm/AMARE/actions/runs/43918788385) 9/10, [#43918802317](https://github.com/thfzm/AMARE/actions/runs/43918802317) 9/13, titlebar 5회 1 PASS/4 FAIL
- 0.76.0 검증 run: [Windows #43891020113](https://github.com/thfzm/AMARE/actions/runs/43891020113) **success**, [Linux #43891020130](https://github.com/thfzm/AMARE/actions/runs/43891020130) **success**, GPUI 3회 [#43891020135](https://github.com/thfzm/AMARE/actions/runs/43891020135) 11/7, [#43891043888](https://github.com/thfzm/AMARE/actions/runs/43891043888) 6/11, [#43891058469](https://github.com/thfzm/AMARE/actions/runs/43891058469) 9/7, titlebar 5회 2 PASS/3 hang
- 0.75.0 검증 run: [Windows #43881498595](https://github.com/thfzm/AMARE/actions/runs/43881498595) **success**, [Linux #43881498633](https://github.com/thfzm/AMARE/actions/runs/43881498633) **success**, GPUI 3회 [#43881498684](https://github.com/thfzm/AMARE/actions/runs/43881498684) 9/3, [#43881520833](https://github.com/thfzm/AMARE/actions/runs/43881520833) 9/12, [#43881534451](https://github.com/thfzm/AMARE/actions/runs/43881534451) 10/7, titlebar 5회 1 PASS/4 hang
- 0.74.0 검증 run: [Windows #43858573835](https://github.com/thfzm/AMARE/actions/runs/43858573835) **success**, [Linux #43853054122](https://github.com/thfzm/AMARE/actions/runs/43853054122) **success**, GPUI 3회 [#43853054117](https://github.com/thfzm/AMARE/actions/runs/43853054117) 10/6, [#43853072690](https://github.com/thfzm/AMARE/actions/runs/43853072690) 8/2, [#43853085540](https://github.com/thfzm/AMARE/actions/runs/43853085540) 8/2, titlebar 별도 16/0/0 2회 + hang 1회
- 0.73.0 검증 run: [Windows #43831963819](https://github.com/thfzm/AMARE/actions/runs/43831963819) **success**, [Linux #43831963861](https://github.com/thfzm/AMARE/actions/runs/43831963861) **success**, GPUI 3회 [#43831963871](https://github.com/thfzm/AMARE/actions/runs/43831963871) 8/5, [#43831980338](https://github.com/thfzm/AMARE/actions/runs/43831980338) 9/11, [#43831991718](https://github.com/thfzm/AMARE/actions/runs/43831991718) 10/6, titlebar 별도 [#43836783288](https://github.com/thfzm/AMARE/actions/runs/43836783288) **failure**
- 0.72.0 검증 run: [Windows #43814816238](https://github.com/thfzm/AMARE/actions/runs/43814816238) **success**, [Linux #43814816318](https://github.com/thfzm/AMARE/actions/runs/43814816318) **success**, GPUI 3회 완전 반복 [#43814816281](https://github.com/thfzm/AMARE/actions/runs/43814816281) 3/9, [#43814829207](https://github.com/thfzm/AMARE/actions/runs/43814829207) 4/5, [#43814837469](https://github.com/thfzm/AMARE/actions/runs/43814837469) 4/8
- 0.71.0 검증 run: [Windows #43774705662](https://github.com/thfzm/AMARE/actions/runs/43774705662) **success**, [Linux #43774705622](https://github.com/thfzm/AMARE/actions/runs/43774705622) **success**, GPUI 3회 완전 반복 [#43774705732](https://github.com/thfzm/AMARE/actions/runs/43774705732) 5/2, [#43774723774](https://github.com/thfzm/AMARE/actions/runs/43774723774) 6/6, [#43778302308](https://github.com/thfzm/AMARE/actions/runs/43778302308) 6/6 (별도 #43774716984는 canceled로 제외)
- 0.70.0 검증 run: [Windows #43732722790](https://github.com/thfzm/AMARE/actions/runs/43732722790) **success**, [Linux #43732722784](https://github.com/thfzm/AMARE/actions/runs/43732722784) **success**, GPUI 반복 [#43732722754](https://github.com/thfzm/AMARE/actions/runs/43732722754), [#43735180784](https://github.com/thfzm/AMARE/actions/runs/43735180784), [#43735187244](https://github.com/thfzm/AMARE/actions/runs/43735187244) **failure** (2/17, 3/18, 1/17)
- 0.69.0 검증 run: [Windows #43637524147](https://github.com/thfzm/AMARE/actions/runs/43637524147) **success**, [Linux #43637524008](https://github.com/thfzm/AMARE/actions/runs/43637524008) **success**, GPUI 반복 [#43639966790](https://github.com/thfzm/AMARE/actions/runs/43639966790), [#43642547300](https://github.com/thfzm/AMARE/actions/runs/43642547300), [#43642552642](https://github.com/thfzm/AMARE/actions/runs/43642552642) **failure** (1/15, 2/15, 2/14)
- 0.68.0 검증 run: [Windows #43618485012](https://github.com/thfzm/AMARE/actions/runs/43618485012) **success**, [Linux #43618485031](https://github.com/thfzm/AMARE/actions/runs/43618485031) **success**, GPUI 반복 [#43618485296](https://github.com/thfzm/AMARE/actions/runs/43618485296), [#43622848541](https://github.com/thfzm/AMARE/actions/runs/43622848541), [#43622854309](https://github.com/thfzm/AMARE/actions/runs/43622854309) **failure** (5/7, 5/5, 5/7)
- 0.67.0 검증 run: [Windows #43595487371](https://github.com/thfzm/AMARE/actions/runs/43595487371) **success**, [Linux #43595487407](https://github.com/thfzm/AMARE/actions/runs/43595487407) **success**, [GPUI #43596729728](https://github.com/thfzm/AMARE/actions/runs/43596729728) **failure** (password 51/0/1, multiwindow 5/1, input 25/0/4, tab 10/1/2, library 2/1, settings 20/0/5, session 0/1; 3회 연속 0 FAIL 미달)
- 0.66.0 검증 run: [Windows #43575072487](https://github.com/thfzm/AMARE/actions/runs/43575072487) **success**, [Linux #43575072515](https://github.com/thfzm/AMARE/actions/runs/43575072515) **success**, [GPUI #43566952547](https://github.com/thfzm/AMARE/actions/runs/43566952547) **failure** (password 51/0/1, input 25/0/4, multiwindow 1/1/0, tab 8/1/4, library 2/1/0, session 0/1/0; 실제 사용자 검증 UNVERIFIED)
- 0.65.0 검증 run: [Windows #43546465352](https://github.com/thfzm/AMARE/actions/runs/43546465352) **success**, [Linux #43546465379](https://github.com/thfzm/AMARE/actions/runs/43546465379) **success**, [GPUI #43546465343](https://github.com/thfzm/AMARE/actions/runs/43546465343) **failure** (CI 입력 제약, 단축키 사용자 검증 UNVERIFIED)
- 0.64.0 검증 run: [Windows #43488605542](https://github.com/thfzm/AMARE/actions/runs/43488605542) **success**, [Linux #43488605596](https://github.com/thfzm/AMARE/actions/runs/43488605596) **success**, [GPUI #43488605751](https://github.com/thfzm/AMARE/actions/runs/43488605751) **failure** (layout bounds, SendInput 0, UIA toolbar 없음, button action 없음, tab 4/5 실패)
- 0.63.0 검증 run: [Windows #43452497687](https://github.com/thfzm/AMARE/actions/runs/43452497687) **success**, [Linux #43452497753](https://github.com/thfzm/AMARE/actions/runs/43452497753) **success**, [GPUI #43452497868](https://github.com/thfzm/AMARE/actions/runs/43452497868) **failure** (SendInput 0, UIA toolbar 없음, button hit-test FAIL, 실제 입력/3회 BLOCKED)
- 0.62.0 검증 run: [Windows #43414011274](https://github.com/thfzm/AMARE/actions/runs/43414011274) **success**, [Linux #43414011463](https://github.com/thfzm/AMARE/actions/runs/43414011463) **success**, [GPUI #43414011192](https://github.com/thfzm/AMARE/actions/runs/43414011192) **failure** (input diagnostics, Ctrl+T/Ctrl+N 반복 실패, button hit-test 실패 — 실제 입력/3회 미달)
- 0.61.0 검증 run: [Windows #43376017765](https://github.com/thfzm/AMARE/actions/runs/43376017765) **success**, [Linux #43376017585](https://github.com/thfzm/AMARE/actions/runs/43376017585) **success**, [GPUI #43376017640](https://github.com/thfzm/AMARE/actions/runs/43376017640) **failure** (실제 Ctrl+T 4 tabs current=2, Ctrl+N windows=1 — 실제 입력/3회 목표 미달)
- 0.60.0 최종 코드 검증 run: [Windows #43324378154](https://github.com/thfzm/AMARE/actions/runs/43324378154) **success**, [Linux #43324378157](https://github.com/thfzm/AMARE/actions/runs/43324378157) **success**, [GPUI #43324378220](https://github.com/thfzm/AMARE/actions/runs/43324378220) **attempt 1/2 success, attempt 3 password FAIL** (attempt 1 0 FAIL/18 UNV, attempt 2 0 FAIL/16 UNV, attempt 3 password 11/1/0 — 3/3 미달)
- 0.59.0 최종 코드 검증 run: [Windows #43161123572](https://github.com/thfzm/AMARE/actions/runs/43161123572) **success**, [Linux #43161123592](https://github.com/thfzm/AMARE/actions/runs/43161123592) **success**, [GPUI #43161123680](https://github.com/thfzm/AMARE/actions/runs/43161123680) **success** (password 51/0/0, unit 11/11, multiwindow 20/0/1, tab 20/0/6, library 19/0/0, settings 18/0/7, session 8/0/2, aggregate 167/0/22, WinUI 143/0/9/1 GUI 42/42, CapturePreview DOM PNG; pending/titlebar/session restore/개별 탭 캡처 **UNVERIFIED**, ALL_SITE **read-only/BLOCKED**)
- 0.58.0 최종 코드 검증 run: [Windows #42972643407](https://github.com/thfzm/AMARE/actions/runs/42972643407) **success**, [Linux #42972643461](https://github.com/thfzm/AMARE/actions/runs/42972643461) **success**, [GPUI #42974740733](https://github.com/thfzm/AMARE/actions/runs/42974740733) **success** (password 43/0/0, unit 4/4, iframe/nested/action no-capture, key-missing/corruption preservation, delete/empty/restart, 34 PNG; WinUI 143/0/9/1 GUI 42/42; GPUI tab 10/1/3, library 2/1/0, settings 1/1/0, session 0/1/0 내부 FAIL은 continue-on-error이며 PASS 아님; 다중 창 동시 Vault **FAIL/UNVERIFIED**, ALL_SITE **read-only/BLOCKED**, page DOM capture **UNVERIFIED**)
- 0.41 ETL/lifecycle: [Windows #42396103458](https://github.com/thfzm/AMARE/actions/runs/42396103458) **success**; ETL 606,780,794 bytes, 25,734,983 events, CSV 12.3GB
- 30분 keyboard-only stress run: [Windows #42352609389](https://github.com/thfzm/AMARE/actions/runs/42352609389) **success** (1,033 cycles, handles 965→6,736)
- 0.41 lifecycle: idle stable 855→858, tab close 후 E 995/G 1217, 5분 I 1186; ETL artifact ~1.44GB
- AMARE 0.54.0: WinUI GUI **42/42**, WinUI **143 PASS / 0 FAIL / 9 UNVERIFIED / 1 SKIPPED**, GPUI release build, SQLite settings UI/search/reset/restart persistence **PASS**, theme/density/font/tab width actual apply, search/engine/address/download/history/advanced settings, two-window session restore **PASS (forced-exit snapshot)**, actual 0.54 screenshots **31개**, settings **18/0/2**, library **18/0/0**, download **7/0/1**, input **25/0/4**, tab **20/0/6**; DOM/page-state restore, cookies/site permission UI, graceful multi-window exit, download pause/resume/cancel automation, DPI/multi-monitor, restart WebView2 cleanup, MSIX install **UNVERIFIED/BLOCKED**, GitHub Release 0개.
- AMARE 0.56.0: WinUI GUI **42/42**, WinUI **143 PASS / 0 FAIL / 9 UNVERIFIED / 1 SKIPPED**, GPUI password **25 PASS / 0 FAIL / 0 UNVERIFIED** (unit 4/4), actual delete confirm/cancel/complete + restart persistence, corrupted vault 원본 보존/재시도/백업 안내, DPAPI restart restore, exact-origin autofill, no auto-submit, plaintext scan; iframe/교차 출처 공격 자동화, 다른 Windows 사용자/DPAPI 권한 실패, 다중 창 동시 UI mutation, last-account empty-state, cookie/site data control, DPI/Windows 11/signing **UNVERIFIED/BLOCKED**, GitHub Release 0개.
- AMARE 0.57.0: WinUI GUI **42/42**, WinUI **143/0/9/1**, GPUI password **42 PASS / 0 FAIL / 0 UNVERIFIED**, iframe/nested iframe/action-origin no-capture 실제 WebView2 PASS, delete cancel/confirm/last-account/empty/restart, corrupt vault preserved, privacy autofill/save toggles, ALL_SITE WebView2 deletion UI, 0.57 screenshots 41 PNG; per-site cookie/permission control, DPAPI other-user failure, same-process multiwindow simultaneous UI mutation, hardware-surface visual page capture, DPI/Windows 11/signing **UNVERIFIED/BLOCKED**, GitHub Release 0개.
- AMARE 0.58.0: WinUI GUI **42/42**, WinUI **143/0/9/1**, GPUI password **43 PASS / 0 FAIL / 0 UNVERIFIED**, vault unit 4/4, navigation ID recheck, iframe/nested/action no-capture, key-missing/corruption vault 원본 보존, delete cancel/confirm/empty/restart, privacy toggles, 0.58 screenshots 34 PNG(ZIP/MSIX SHA-256 기록); same-process multiwindow same-entry conflict/삭제 부활 방지/창 간 잠금 **FAIL/UNVERIFIED**, ALL_SITE **read-only/BLOCKED**, page DOM capture **UNVERIFIED**, GPUI tab/library/settings/session 내부 **FAIL**, unsigned MSIX install `0x80073D2C` **UNVERIFIED**, GitHub Release 0개.
- AMARE 0.82.0: WinUI GUI **48/48**, Windows **143/0/9/1**, interop **70/70**, Linux SUCCESS, **GPUI FAIL** — crash 0/5, exit 7/7, restore Enter WM_KEYDOWN 미도달(UNRESOLVED), SendKeys 손실/paste 분리, corrupt 14/14, 3회 13/13/13 FAIL, 실제 Win11/IME UNVERIFIED, unsigned MSIX BLOCKED, GitHub Release 0개.
- AMARE 0.81.0: WinUI GUI **48/48**, Windows **143/0/9/1**, interop **70/70**, Linux SUCCESS, **GPUI FAIL** — exit classifier 7/7, `0xC0000409` RefCell panic 원인 확정/status poll 제거, titlebar crash 0/5·full 3/5, restore Enter/submit 미해결, corrupt 14/14, 3회 13/13/13 FAIL, 실제 Win11/DPI UNVERIFIED, unsigned MSIX BLOCKED, GitHub Release 0개.
- AMARE 0.80.0: WinUI GUI **48/48**, Windows **143/0/9/1**, interop **70/70**, Linux SUCCESS, **GPUI FAIL** — dedupe 제거, Win32/GPUI activation trace, restore 30/0/0·submit FAIL(run1), titlebar full 2/5(2 cancel·1 crash), corrupt 14/14, 3회 10/10/10 FAIL, 실제 Win11/DPI UNVERIFIED, unsigned MSIX BLOCKED, GitHub Release 0개.
- AMARE 0.79.0: WinUI GUI **48/48**, Windows **143/0/9/1**, interop **70/70**, Linux SUCCESS, **GPUI FAIL** — restore submit event trace, Enter interceptor crash 제거, restore submit FAIL, titlebar 0/5, corrupt 14/14, session 29/0/0 2회, 3회 6/9/9 FAIL, 실제 Win11/DPI UNVERIFIED, unsigned MSIX BLOCKED, GitHub Release 0개.
- AMARE 0.78.0: WinUI GUI **48/48**, Windows **143/0/9/1**, interop **70/70**, Linux SUCCESS, **GPUI FAIL** — restore attach refocus/staged readiness, restore text 1~5자/submit FAIL, titlebar 0/5, corrupt 14/14, session 29/0/0 1회, 3회 9/8/9 FAIL, 실제 Win11/DPI UNVERIFIED, unsigned MSIX BLOCKED, GitHub Release 0개.
- AMARE 0.77.0: WinUI GUI **48/48**, Windows **143/0/9/1**, interop **70/70**, Linux SUCCESS, **GPUI FAIL** — AddressInput text/focus telemetry, restored focus steal fix, new-tab/rapid/new-window submit PASS, corrupt 14/14, session 29/0/0 1회, titlebar 별도 1/5, 3회 12/9/9 FAIL, 실제 Win11/DPI UNVERIFIED, unsigned MSIX BLOCKED, GitHub Release 0개.
- AMARE 0.76.0: WinUI GUI **48/48**, Windows **143/0/9/1**, interop **70/70**, Linux SUCCESS, **GPUI FAIL** — exact address bounds/readiness gate, corrupt 14/14, session 29/0/0, Vault 17/0/0, titlebar 별도 2/5, restore/corrupt submit FAIL, 3회 11/6/9 FAIL, 실제 Win11/DPI UNVERIFIED, unsigned MSIX BLOCKED, GitHub Release 0개.
- AMARE 0.75.0: WinUI GUI **48/48**, Windows **143/0/9/1**, interop **70/70**, Linux SUCCESS, **GPUI FAIL** — tab navigation lifecycle telemetry, submit callback(new-tab/rapid/new-window) PASS, corrupt 14/14(3회), session 29/0/0(2회), Vault 29/0/0(2회), titlebar 별도 1/5, 3회 **9/9/10 FAIL**로 0 FAIL 미달, restore/corrupt submit·first-click·실제 Win11 **UNVERIFIED**, unsigned MSIX **BLOCKED**, GitHub Release 0개.
- AMARE 0.74.0: WinUI GUI **48/48**, Windows **143/0/9/1**, interop **70/70**, Linux SUCCESS, **GPUI FAIL** — AddressInput submit/dispatch telemetry, navigation submit callback PASS, corrupt 14/14(3회), session core 2회 PASS, titlebar 별도 16/0/0 2회/hang 1회, 3회 **10/8/8 FAIL**로 0 FAIL 미달, 실제 Win11/DPI **UNVERIFIED**, AI/updater/permission 미구현, unsigned MSIX **BLOCKED**, GitHub Release 0개.
- AMARE 0.73.0: WinUI GUI **48/48**, Windows **143/0/9/1**, interop **70/70**, Linux SUCCESS, **GPUI FAIL** — input delivery telemetry + navigation regression, corrupt session 14/14 (3회), session core 2회 PASS, 3회 **8/9/10 FAIL**로 0 FAIL 미달, titlebar 별도 workflow window2 handle invalid FAIL, 실제 Win11/DPI **UNVERIFIED**, AI/updater/permission 미구현, unsigned MSIX **BLOCKED**, GitHub Release 0개.
- AMARE 0.72.0: WinUI GUI **48/48**, Windows **143/0/9/1**, interop **70/70**, Linux SUCCESS, **GPUI FAIL** — first navigation pending queue, corrupt session **14/14**, session persistence **30/0/1**, multi-window 2→2, immediate close/no-resurrection PASS; 3회 완전 반복 **3/4/4 FAIL**(best 3/9)로 0 FAIL 미달, password/vault/library/settings 첫 click·submit과 titlebar multi-window CI hang 남음, 실제 Win11/DPI **UNVERIFIED**, AI/updater/permission 미구현, unsigned MSIX **BLOCKED**, GitHub Release 0개.
- AMARE 0.71.0: WinUI GUI **48/48**, Windows **143/0/9/1**, interop **70/70**, Linux SUCCESS, **GPUI FAIL** — titlebar 두 창 max/restore/minimize **16/0/0**, multi-window session **2→2**, active URL/scroll/geometry/maximized PASS, corrupt metadata/empty/DB PASS, Vault delete/tombstone PASS; 3회 완전 반복 **5/6/6 FAIL**(best 5/2)로 0 FAIL 미달, 남은 FAIL은 첫 click/navigation 자동화, corrupt JSON/실제 Win11/DPI **UNVERIFIED**, AI/updater/permission 미구현, unsigned MSIX **BLOCKED**, GitHub Release 0개.
- AMARE 0.70.0: WinUI GUI **48/48**, Windows **143/0/9/1**, interop **70/70**, Linux SUCCESS, **GPUI FAIL** — WebView2 create/navigate/focus outside entity borrow(RefCell panic 제거), titlebar minimize/maximize, Alt+F4 두 창 순차 종료·WebView2 cleanup PASS, password 51/0/1, input 25/0/4, tab 21/0/7, library 20/0/0, settings 22/0/4, session 10/0/5, multiwindow 12/1; best 1 FAIL/17 UNVERIFIED, 3회 2/3/1 FAIL로 0 FAIL 미달, titlebar restore/close·다중 창 restore·session scroll/geometry/maximized/corrupt fallback·multiwindow `dave` delete 미해결, AI/updater/permission 미구현, 실제 Windows 11 사용자 검증 **UNVERIFIED**, unsigned MSIX **BLOCKED**, GitHub Release 0개.
- AMARE 0.69.0: WinUI GUI **48/48**, Windows **143/0/9/1**, interop **70/70**, Linux SUCCESS, **GPUI FAIL** — controller outside entity borrow, Session Restore metadata/controller/window PASS, tab 21/0/6, library 20/0/0, settings 21/0/4, session 10/0/4, multiwindow 12/1; best 1 FAIL/15 UNVERIFIED, 3회 1/2/2 FAIL로 0 FAIL 미달, titlebar restore/close·session active/scroll·multiwindow delete 미해결, AI/updater/permission 미구현, 실제 Windows 11 사용자 검증 **UNVERIFIED**, unsigned MSIX **BLOCKED**, GitHub Release 0개.
- AMARE 0.68.0: WinUI GUI **48/48**, Windows **143/0/9/1**, interop **70/70**, Linux SUCCESS, **GPUI FAIL** — WebView2 attach outside effect flush, lazy restored controllers, 2nd/3rd window + tab independence 6 PASS, three repeats 5/7·5/5·5/7, Session Restore/titlebar/tab/password/navigation unresolved, AI/updater/permission 미구현, 실제 Windows 11 사용자 검증 **UNVERIFIED**, unsigned MSIX **BLOCKED**, GitHub Release 0개.
- AMARE 0.67.0: WinUI GUI **48/48**, Windows **143/0/9/1**, interop **70/70**, Linux SUCCESS, **GPUI FAIL** — 두 번째 창 WebView2 attach 순서/activate, tab loading 준비 조건, 실제 입력/창 크기 fixture, tear-off re-merge 창 배치; GPUI 최종 password 51/0/1, input 25/0/4, multiwindow 5/1, settings 20/0/5, aggregate 4/8, 3회 연속 0 FAIL **미달**, AI/updater/permission **미구현**, 실제 Windows 11 사용자 검증 **UNVERIFIED**, unsigned MSIX **BLOCKED**, GitHub Release 0개.
- AMARE 0.66.0: WinUI GUI **48/48**, Windows **143/0/9/1**, Linux SUCCESS, **GPUI FAIL** — WinUI shortcut router/Ctrl+N/last-tab recovery/AMARE title strip, WebView2 page Ctrl+T/L/N PASS, last tab → example.com PASS, tab reorder/tear-off/cross-window PASS, WinUI 7 PNG + GPUI 52 PNG; 실제 Windows 11 사용자 PASS/3회 반복 **UNVERIFIED/미달**, ALL_SITE **BLOCKED**, unsigned MSIX install `0x80073D2C` **UNVERIFIED**, GitHub Release 0개.
- AMARE 0.65.0: Windows GUI **42/42**, Windows **143/0/9/1**, Linux SUCCESS, **GPUI FAIL** — WebView2→GPUI modifier 명시 전송, Ctrl+T/L/N 수정 시도, NewWindow/Ctrl+N 동일 action, 57 PNG, GPUI 사용자 ZIP 제공; 실제 Windows 11 사용자 PASS/3회 반복 **UNVERIFIED/미달**, ALL_SITE **BLOCKED**, unsigned MSIX install `0x80073D2C` **UNVERIFIED**, GitHub Release 0개.
- AMARE 0.64.0: Windows GUI **42/42**, Windows **143/0/9/1**, Linux SUCCESS, **GPUI FAIL** — toolbar layout bounds 확보, SendInput=0, UIA toolbar 없음, layout click action 없음, tab 4/5 실패, Ctrl+N windows=1, 61 PNG + diagnostics; 실제 입력/3회 **BLOCKED/미달**, ALL_SITE **BLOCKED**, unsigned MSIX install `0x80073D2C` **UNVERIFIED**, GitHub Release 0개.
- AMARE 0.63.0: Windows GUI **42/42**, Windows **143/0/9/1**, Linux SUCCESS, **GPUI FAIL** — WinForms harness SendInput=0, UIA GPUI toolbar 없음, Ctrl+T 1회/반복 FAIL, Ctrl+N 1회/반복 FAIL, button hit-test FAIL, 52 PNG + diagnostics; 실제 입력/3회 **BLOCKED/미달**, ALL_SITE **BLOCKED**, unsigned MSIX install `0x80073D2C` **UNVERIFIED**, GitHub Release 0개.
- AMARE 0.62.0: Windows GUI **42/42**, Windows **143/0/9/1**, Linux SUCCESS, **GPUI FAIL** — diagnostics trace, Ctrl+T action 1회 PASS/반복 FAIL, Ctrl+N window creation 1회 PASS/반복 FAIL, 새 탭/새 창 버튼 FAIL, multiwindow 11/1/1, 3회 반복/실제 입력 **미달**, 59 PNG, ALL_SITE **BLOCKED**, unsigned MSIX install `0x80073D2C` **UNVERIFIED**, GitHub Release 0개.
- AMARE 0.61.0: Windows GUI **42/42**, Windows **143/0/9/1**, Linux SUCCESS, **GPUI FAIL** — 실제 Ctrl+T/새 탭 버튼 4 tabs current=2, Ctrl+N/새 창 버튼 windows=1, multiwindow/pending/titlebar/3회 반복 **미달**, CapturePreview normal/active/max/restore PASS, 51 PNG; ALL_SITE **BLOCKED**, unsigned MSIX install `0x80073D2C` **UNVERIFIED**, GitHub Release 0개.
- AMARE 0.60.0: WinUI GUI **42/42**, WinUI **143/0/9/1**, Vault unit **11/11**, multiwindow **20/0/1**, CapturePreview normal/active/max/restore PASS, 71 PNG, ZIP/MSIX 생성; GPUI 3회 중 **1·2차 0 FAIL, 3차 password autofill FAIL**로 반복 안정성 **미달**, 새 탭/두 번째 창 실제 입력·pending cancel·titlebar click·multiwindow session restore·개별 탭 캡처 **UNVERIFIED**, ALL_SITE **read-only/BLOCKED**, unsigned MSIX install `0x80073D2C` **UNVERIFIED**, GitHub Release 0개.
- AMARE 0.59.0: WinUI GUI **42/42**, WinUI **143/0/9/1**, GPUI aggregate **167/0/22** (password 51/0/0, unit 11/11, multiwindow 20/0/1, tab 20/0/6, library 19/0/0, settings 18/0/7, session 8/0/2), persistent tombstone + per-entry revision, same-entry/modify-delete conflict rejection, stale delete no-resurrection, process-wide save mutex, cross-window lock propagation, same-process two-window GUI, WebView2 **CapturePreview** 1024×544 actual DOM PNG, 0.59 screenshots 77 PNG, failure gate 0 FAIL; pending autofill cancellation/titlebar actual click/multi-window session restore/개별 탭 캡처 **UNVERIFIED**, ALL_SITE **read-only/BLOCKED**, unsigned MSIX install `0x80073D2C` **UNVERIFIED**, GitHub Release 0개.
- 상세 분석: [CI 재검증 보고서](CI_REVALIDATION_REPORT.md)

---

## 1. Workflow 목록

| Workflow | 파일 | 실행 조건 |
|---|---|---|
| Linux CI | `.github/workflows/ci-linux.yml` | PR, main/master push, 수동 |
| Windows CI | `.github/workflows/ci-windows.yml` | PR, main/master push, 수동 |
| Android scaffold | `.github/workflows/ci-android.yml` | `apps/android/**`/workflow path push·PR, 수동 |

공통:

- `permissions: contents: read` (최소 권한)
- `concurrency`로 같은 PR/branch의 중복 실행 취소
- GitHub Secrets에 계정/유료 서비스 정보를 저장하지 않음
- 외부 Action은 공식/신뢰 버전 고정 (`actions/*`, `microsoft/setup-msbuild`, `dtolnay/rust-toolchain`)
- 캐시: Cargo registry/target, NuGet packages

---

## 2. Windows CI 구조

`windows-latest` (Windows Server)에서 실행합니다. **Windows 11 실기기와 동일하지 않습니다.** Windows 11 build 22000+ 여부는 `windows-verify-environment.json`의 `windows_11`에 기록하며, Runner가 Windows Server여도 빌드/테스트는 계속 수행합니다.

1. Checkout
2. .NET 8 SDK 설치/확인
3. Rust stable + `x86_64-pc-windows-msvc` target
4. MSBuild 설정
5. Cargo/NuGet 캐시
6. WebView2 Runtime 확인, 없으면 Evergreen Bootstrapper 설치
7. Visual Studio C++ Build Tools/Windows SDK/Rust 확인
8. `tools/verify-windows.ps1 -CI` 실행
   - Rust fmt/clippy/test (workspace + service layer)
   - update/security 로직 테스트
   - SQLite storage tests (`amare-store`, `rusqlite` bundled + MSVC C compiler)
   - Service layer tests (`amare-services`) + `service-ci.json` + `service-benchmark.json`
   - SQLite integration report (`storage-ci.json`, Phase 1.6 Library/AI/Workspace FTS5 포함)
   - SQLite benchmark (`storage-benchmark.json`)
   - Backup/restore tests + integration (`backup-ci.json`, Phase 1.6 데이터 카테고리 포함)
   - Backup/restore benchmark (`backup-benchmark.json`)
   - Windows DPAPI tests (`cargo test -p amare-platform`)
   - FFI build
   - C header compile (MSVC `cl /c`)
   - C# ↔ Rust interop (AMARE.Windows.Core + native service JSON ABI)
   - WinUI 3 restore + Release build (required; Windows only, Linux에서 XAML compiler 실행 불가)
   - Windows probe build
   - WebView2 probes: local HTTP navigation, HTTPS(optional), 10-tab lifecycle, profile isolation, CDP, memory(30탭)
   - Windows 11 실기기 결과는 별도 UNVERIFIED
   - `windows_11=false` Runner 결과를 Windows 11 실기기 PASS로 변환하지 않음
9. Artifacts 업로드 (`if: always()`)

### 통합 결과

`windows-verification.json`은 PowerShell step, WebView2 probe, C# interop, service/storage/backup CI case를 하나의 배열로 합칩니다. 각 항목은 `name/status/executed/duration_ms/environment/error/log_path`를 포함하고 status는 `PASS/FAIL/UNVERIFIED/SKIPPED`만 사용합니다. `windows-verify-summary.json`은 기존 요약 형식을 유지합니다.

### `verify-windows.ps1 -CI` 동작

- `AMARE_VERIFY_HEADLESS=1`: GUI를 숨기고 자동 실행 후 종료
- `AMARE_VERIFY_MANUAL=0`: 사용자 개입 테스트를 `UNVERIFIED`
- `AMARE_VERIFY_MEMORY=0`: 30탭 메모리 테스트를 `UNVERIFIED`
- `AMARE_VERIFY_DRM=0`: DRM 매트릭스를 `UNVERIFIED` (CI Runner DRM/VMP는 실기기 대표성 없음)
- `AMARE_VERIFY_OUTPUT=<repo>\artifacts\windows`: 결과/로그 저장
- 각 단계 exit code를 `windows-verify-summary.json`에 기록
- 필수 단계 실패 시 workflow 실패, 가능한 로그/결과는 Artifact로 보존

---

## 3. Ubuntu CI 구조

`ubuntu-24.04`에서 실행합니다.

1. Rust fmt
2. Clippy `-D warnings`
3. Rust workspace tests
4. `amare-services` 서비스 테스트 + `service-ci` + `service-benchmark`
5. `amare-security` 암호화 테스트
6. `amare-update` 서명/롤백 테스트
7. `amare-store` SQLite 테스트
8. Backup/restore tests/CI/benchmark
9. Key protection interface tests (Linux: Android stub, no DPAPI)
10. C ABI build
11. C header link/run smoke test (gcc)
12. C# ↔ Rust interop (Linux 가능 범위)
13. Engine JSON Schema 검증
14. 로그/Artifacts 업로드

Linux에서는 WinUI 3/WebView2/Windows DPAPI/설치 프로그램 테스트를 실행하지 않습니다.

---

## 4. 수동 실행

GitHub UI:

1. Repository → **Actions**
2. **ci-windows** 선택
3. **Run workflow**
4. Branch 선택 후 실행
5. 완료 후 Artifacts 다운로드

CLI:

```bash
gh workflow run ci-windows.yml
gh run list --workflow=ci-windows.yml
gh run watch <run-id>
gh run download <run-id>
```

로컬 Windows에서 동일한 검증:

```powershell
.\tools\verify-windows.ps1 -CI
```

수동 개입/메모리 테스트를 포함하려면:

```powershell
.\tools\verify-windows.ps1
```

---

## 5. Artifacts

Windows Artifact (`windows-verification-<run_id>`):

```text
artifacts/windows/
├─ windows-verify-environment.json
├─ windows-verify-summary.json
├─ windows-verification.json
├─ windows-verify-results.json
├─ interop-results.json
├─ rust_fmt.log
├─ rust_clippy.log
├─ rust_tests.log
├─ update_logic_tests.log
├─ ffi_build.log
├─ c_header.log
├─ rust_windows_target.log
├─ webview2_runtime.log
├─ csharp_core_build.log
├─ storage_tests.log
├─ storage_ci.log
├─ service_tests.log
├─ service_ci.log
├─ service-ci.json
├─ service_benchmark.log
├─ service-benchmark.json
├─ storage-ci.json
├─ storage_benchmark.log
├─ storage-benchmark.json
├─ backup_tests.log
├─ backup_ci.log
├─ backup-ci.json
├─ backup_benchmark.log
├─ backup-benchmark.json
├─ platform_key_tests.log
├─ csharp_rust_interop.log
├─ winui_restore.log
├─ winui_build.log
├─ windows_probe_build.log
└─ webview2_probe_run.log
```

Linux Artifact (`linux-phase0.5-<run_id>`):

```text
artifacts/linux/
├─ rust-fmt.log
├─ rust-clippy.log
├─ rust-tests.log
├─ security-tests.log
├─ update-tests.log
├─ ffi-build.log
├─ c-header.log
├─ storage-tests.log
├─ storage-ci.log
├─ service-tests.log
├─ service-ci.log
├─ service-ci.json
├─ service-benchmark.log
├─ service-benchmark.json
├─ storage-ci.json
├─ storage-benchmark.log
├─ storage-benchmark.json
├─ backup-tests.log
├─ backup-ci.log
├─ backup-ci.json
├─ backup-benchmark.log
├─ backup-benchmark.json
├─ key-protection-tests.log
├─ ci-summary.json
├─ csharp-interop.log
├─ header_link_smoke
└─ interop-results.json
```

---

## 6. 결과 상태

기존 상태 체계를 유지합니다.

- `PASS`: 실행 + 검증 성공
- `FAIL`: 실행 + 검증 실패
- `UNVERIFIED`: 미실행/수동 입력 없음/증거 부족
- `SKIPPED`: 정책상 의도적 미실행 (유료 계정 등)

`windows-verify-results.json`의 각 항목에는 다음이 포함됩니다.

- 테스트 ID/이름
- `executed`
- `status`
- 실행 환경 (`RunReport.Environment`)
- 실패 원인 (`Detail`)
- 로그 경로 (`windows-verify-summary.json` steps)
- 데이터/한계 (`Data`, `Detail`)

**CI에서 실행하지 않은 항목은 PASS가 아닙니다.**

---

## 7. CI vs 실기기 검증 범위

| 항목 | GitHub Actions Windows | Windows 11 실기기 |
|---|---|---|
| WinUI 3 build | 가능 | 가능 |
| WebView2 초기화 | 가능(환경 의존) | 가능 |
| Rust/C# FFI | 가능 | 가능 |
| C ABI header | 가능 | 가능 |
| CDP DOM/클릭/입력/스크롤 | 가능(가상 디스플레이/세션 의존) | 가능 |
| 프로필 격리 | 가능 | 가능 |
| 사용자 개입 감지 | **UNVERIFIED**(자동화 불가) | 수동 |
| 30탭 메모리 | **UNVERIFIED**(부하/정확도) | 목표 하드웨어 |
| i5-7500/16GB/GTX1050 성능 | **UNVERIFIED** | 필수 |
| Netflix/Disney+ | **SKIPPED** | 계정 동의 필요 |
| DRM robustness/VMP | **UNVERIFIED**(정책) | 수동 |
| HDR/특정 GPU HW 디코딩 | **UNVERIFIED** | 실기기 |
| 실제 설치/롤백 | **UNVERIFIED** | Phase 2 |

`windows-latest`는 **Windows Server**이며 GPU/DRM/HDR/사용자 세션이 실기기와 다릅니다.

또한 GitHub-hosted Runner의 세션/데스크톱 제약으로 WinForms/WebView2 headless 프로브가 실패하거나 지연될 수 있습니다. 이 경우 단계 로그를 확인하고, 필요하면 WebView2 windowless/offscreen 호스트 또는 전용 self-hosted Windows runner로 전환합니다. CI 실패를 PASS로 처리하지 않습니다.

---

## 8. 실패 디버깅

1. Actions run → job `windows-verify` 선택
2. 실패 step 로그 확인
3. Artifacts 다운로드
4. `windows-verify-summary.json`의 `steps`/`failures` 확인
5. `windows-verify-results.json`에서 `FAIL`/`UNVERIFIED` 구분
6. 로컬 Windows에서 `.\tools\verify-windows.ps1 -CI` 재현
7. Ubuntu에서 수정 후 다시 Push

자주 발생하는 실패:

| 증상 | 원인 | 조치 |
|---|---|---|
| WinUI build 실패 | Windows SDK/WinAppSDK/플랫폼 불일치 | `dotnet restore`, `-p:Platform=x64`, 패키지 버전 확인 |
| WebView2 probe hang | headless/세션 문제 | `webview2_probe_run.log` 확인, `AMARE_VERIFY_HEADLESS` 동작 확인 |
| DRM matrix FAIL | Runner에 EME/Widevine 없음 | CI 결과를 실기기 검증으로 대체하지 않음 |
| C header FAIL | MSVC/vcvars/헤더 경로 | `c_header.log`, VS Build Tools 확인 |
| interop FAIL | DLL 경로/PATH | `target\debug` PATH, `interop-results.json` 확인 |

---

## 9. 비용/보안

- 공개 저장소 기준 표준 runner 사용, 불필요한 반복 실행은 concurrency로 취소
- 캐시로 빌드 시간 절감
- Secrets 미사용, 유료 계정 로그인 없음
- PR from fork에서도 Secrets를 사용하지 않으므로 노출 위험 없음
- WebView2 설치 파일은 Microsoft 공식 Bootstrapper 링크 사용
- 외부 사이트는 테스트에 사용하지 않음 (DRM 실제 서비스 제외, 수동/SKIPPED)
