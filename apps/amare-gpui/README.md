# AMARE GPUI Windows Shell (0.90.0)

This is the GPUI + Rust Windows entry point for AMARE. It remains a transition shell; the WinUI 3 app is still the default packaged UI.

## Implemented

- GPUI 0.2.2 native window and app entry point.
- Top tab bar with tab IDs, titles, loading indicator, close buttons, new-tab button, drag-to-reorder, Ctrl+T/W/Tab, real address/search text field, back/forward controls, theme toggle.
- `EntityInputHandler`/`ElementInputHandler` address input with selection and clipboard editing.
- Windows IME context on the GPUI HWND; Korean layout/native conversion is enabled when the active layout is Korean.
- WebView2 host created as a native child HWND below the GPUI toolbar.
- Ctrl+L focus/select-all, Ctrl+R, Alt+Left/Right, Escape, Enter navigation/search.
- WebView2 `AcceleratorKeyPressed` forwarding of browser shortcuts from page focus.
- Address/title synchronization after WebView2 navigation.
- Shared WebView2 Environment/Profile with one Controller/WebView2 per tab.
- SQLite-backed bookmarks, visit history, and download metadata through amare-store/amare-services.
- Bookmark/history/download panels with search, delete, empty states, and restart persistence.
- WebView2 NewWindowRequested popups (target=_blank/window.open) open as new tabs.
- Ctrl+N and toolbar button open independent browser windows with shared profile.
- Per-tab URL/title/DOM/input/scroll/history preservation and inactive-controller hiding.
- Window resize, minimize/restore, last-tab close exit, and graceful WebView2 cleanup.
- Actual mouse tab drag reorder/cancel with GPUI drag/drop.
- SQLite settings-backed browser settings screen with searchable categories, theme/density/font/tab width, startup/session, search engine/address behavior, download path/panel, history, advanced diagnostics and confirmed reset.
- Windows DPAPI-protected password vault: XChaCha20-Poly1305 encrypted vault file, account add/edit/delete/reveal/search/multiple accounts, lock/unlock, explicit login-save proposal and exact-origin autofill. WebView2 built-in password autosave/general autofill are disabled.
- 0.56 hardening: actual delete confirm/cancel/complete and restart persistence, corrupted-vault preservation/retry/backup guidance, multi-window merge/generation sync path.
- 0.57: iframe/nested/cross-origin action no-capture, last-account empty state, privacy autofill/save-prompt toggles, WebView2 ALL_SITE data deletion entry point, and measured multi-tab/multi-window screenshots.
- 0.58: navigation ID recheck on `fill_login`, missing-key vault preservation test, key-missing/corruption screenshots, and a full revalidation of the 43-check password suite. ALL_SITE deletion is read-only/BLOCKED because the UI lifecycle could not be verified.
- 0.59: vault format v2 with persistent tombstones and per-entry revisions, optimistic conflict rejection/reload, stale-delete no-resurrection, process-wide save serialization, process-wide lock propagation, and WebView2 `CapturePreview` PNG. The 0.58 encrypted array is migrated automatically. Actual same-process two-window GUI covers independent edits, same-entry conflict, modify/delete conflict, stale save, lock/unlock, close, and restart integrity.
- SQLite settings-backed session save/restore for tab URLs/titles/order, active tab, window geometry, and maximized state, with `AMARE_GPUI_RESTORE=0` opt-out.
- Cross-window tab transfer/detach is UNVERIFIED; official `SetParentWindow` path exists but is not validated end-to-end.

## WebView2

The GPUI window HWND is obtained with `raw-window-handle`. A WebView2 environment/controller is created with `webview2-com` 0.39.1 using the GPUI HWND as parent. The controller bounds are updated from GPUI's viewport. Chromium/WebView2 remains the web engine; GPUI is the native browser UI shell.

## Verified in Windows CI (0.90.0)

- `cargo build --release` for Windows x64.
- Real GPUI window, resized window, address-input, Ctrl+L, and Korean-address screenshots.
- Actual address keyboard input and exact URL navigation.
- Ctrl+L from WebView2 page and Ctrl+L select-all when address is focused.
- Ctrl+R, Alt+Left/Right, Escape restore, Enter URL/search.
- Korean IME in the address bar: `한글`, composition/marked range, Enter search.
- Local test page: input/password/textarea, Korean IME, Tab, Ctrl+A/C/V, selection, mouse drag, link click, wheel, Enter/Escape.
- Address↔WebView2 focus transfer, minimize/restore, graceful exit, WebView2 0→0.
- GPUI bookmark/history add, duplicate handling, list, navigation, search, delete, clear confirmation, restart persistence.
- Settings changes apply immediately and persist after restart; theme/size/tab-width/density PASS from real UI clicks.
- Password vault: **51 password GUI checks PASS**, **11 vault unit tests PASS**; actual delete cancel/confirm/last-account/restart persistence PASS; corrupt vault and missing-key vault unchanged after failed unlock; no plaintext password in data dir or status; DPAPI restart restore PASS; iframe/nested/action-origin tests PASS.
- 0.59: same-process two-window GUI **20 PASS / 0 FAIL / 1 UNVERIFIED** — independent add/edit, same-entry conflict rejection, modify/delete conflict, stale-delete no-resurrection, lock propagation, unlock, close, and restart integrity PASS. Pending autofill cancellation is UNVERIFIED because the automation could not create the pending proposal.
- WebView2 `CapturePreview` produces actual DOM PNGs at 1024×544 for normal, active-tab, maximize, and minimize/restore. Per-tab individual captures and the keyboard shortcut itself remain UNVERIFIED.
- 0.90 adds reload counters and a 10x Ctrl+R probe. Reload #1 invokes ReloadPage but WebView2 nav counters do not change; #2-#10 lose the key event. A/B independent navigation was not reached. Three repeats are 12/13/11 FAIL.
- 0.89 uses SendInput for first-click. All six toolbar controls pass with down/up 1/1 and real state changes; Address selection changes 0,11→11,11. Ctrl+L/T/W/N pass, Ctrl+R varies. Multi-window typing and close pass in the successful run. Three repeats are 13/11/11 FAIL.
- 0.88 exposes stable toolbar bounds and verifies first-click from native mouse to product state. New Tab/Password/Address pass; Settings/Downloads/Bookmarks receive WM_LBUTTONDOWN without WM_LBUTTONUP and remain unverified. Ctrl+L/T/W/N pass, Ctrl+R is inconsistent. Three repeats are 11/12/12 FAIL.
- 0.87 adds mouse-event tracing and a shortcut probe. Ctrl+L/T/R/W/N actual effects pass; multi-window A/B/A focus passes. First-click toolbar bounds and multi-window input/close are unverified. Three repeats are 12/12/14 FAIL.
- 0.86 compares four input paths: scan-code 3/3, paste 3/3, SendKeys 2/3, Unicode 1/3. SendKeys/Unicode losses happen before GPUI; matching strings submit normally. 0.85 focus recovery remains. Three repeats are 13/12/12 FAIL.
- 0.85 adds accurate foreground-thread focus telemetry and a one-shot GotFocus recovery gated on navigation-time AddressInput focus. Probe rounds recover to Zed::Window and Ctrl+L/mouse/clear pass 10/10. Remaining probe failures are SendKeys character loss. Three repeats are 13/12/12 FAIL.
- 0.84 instruments WebView2 `GotFocus`/`LostFocus`/`MoveFocusRequested`/`AcceleratorKeyPressed`. The trace shows `navigation_completed` then `webview_got_focus` on `Chrome_WidgetWin_1`. Enter/submit is green when address text is entered; Ctrl+L/address click focus recovery after navigation is unresolved. Three repeats are 12/12/12 FAIL.
- 0.83 adds an isolated Enter probe. When address text is actually entered, normal and restored windows both reach `WM_KEYDOWN`, GPUI `SubmitAddress`, and submit. Failures are preceded by navigation-time WebView2 focus ownership (`Chrome_WidgetWin_1`) while GPUI `address_focused` is stale. Enter itself is not broken. Three main repeats are 12/13/13 FAIL.
- 0.82 keeps the 0.81 status-poll crash fix and adds Win32/IME message tracing. Restore-first Enter does not produce `WM_KEYDOWN VK_RETURN` at the app HWND even when foreground, external focus, window_active and AddressInput focus are verified; new-window/corrupt Enter dispatches SubmitAddress. The restore Enter cause is unresolved. Titlebar 5 repeats had no crash (2 full PASS, 3 runner cancels). Three main repeats are 13/13/13 FAIL.
- 0.81 reproduced `0xC0000409` with a WER dump and panic backtrace: `RefCell already borrowed` from the status-poll task re-entering GPUI during paint. The poll was removed; status is written from render. Five titlebar repeats produced no crash (3 full PASS, 2 runner cancels). Restore text input improved, but Enter/submit remains intermittent. Three repeats are 13/13/13 FAIL.
- 0.80 removes the unevidenced submit dedupe and traces Win32/GPUI activation. Restore run1 reaches 30 chars but submit fails; run2/3 receive 0 chars despite `address_focused=True`. Win32 foreground changes to window2 and GPUI `window_active` is true in 3/4, but the full titlebar sequence passes 2/5 (2 runner cancels, 1 app exit `0xC0000409`). Corrupt session is 14/0. Three repeats are 10/10/10 FAIL, so GPUI is not the default build.
- 0.79 traces restore submit and window2 activation. An explicit Enter interceptor crashed startup and was removed. Restore submit still fails; titlebar fails at window2 in all five runs. Corrupt session is 14/0; session reaches 29/0/0 twice. Three repeats are 6/9/9 FAIL, so GPUI is not the default build.
- 0.78 re-requests AddressInput focus after restored WebView2 attach and splits titlebar readiness into HWND/IsWindow/webview/loading/responsive stages. Restore text input is now observed (1–5 chars), but submit still fails; titlebar fails all five runs at window2. Corrupt session is 14/0; session reaches 29/0/0 once. Three repeats are 9/8/9 FAIL, so GPUI is not the default build.
- 0.77 adds AddressInput-internal text/focus/edit counters and removes restored WebView2 focus stealing. New-tab/rapid/new-window submit passes; restore/corrupt submit is still unstable. Corrupt session is 14/0; session reaches 29/0/0 once. Titlebar passes 18/0/0 once and fails four times. Three repeats are 12/9/9 FAIL, so GPUI is not the default build.
- 0.76 adds exact address bounds and a titlebar window-readiness gate. Corrupt session is 14/0; session reaches 29/0/0 and Vault 17/0/0 in one repeat. Restore-first/corrupt-resave submit still fails even with exact address clicks. Titlebar passes 16/0/0 twice and hangs three times. Three repeats are 11/6/9 FAIL, so GPUI is not the default build.
- 0.75 adds per-tab navigation started/completed timestamps and keeps submit callback/dispatch/source separate. New-tab/rapid/new-window submit passes; restore/corrupt submit fails. Corrupt session is 14/0 in all three repeats; session core and Vault reach 29/0/0 in two repeats. The separate titlebar workflow passes once and hangs at window2 four times. Three repeats are 9/9/10 FAIL, so GPUI is not the default build.
- 0.74 adds AddressInput-specific submit/focus/dispatch telemetry and separates submit callback from navigation dispatch/source. Submit callbacks pass in the navigation regression suite. Corrupt session is 14/0 in all three repeats; session core passes in two. The separate titlebar workflow passes 16/0/0 twice and hangs once at window2. Three repeats are 10/8/8 FAIL, so GPUI is not the default build.
- 0.73 adds mouse/key/foreground/navigation-request telemetry and a navigation regression suite that separates event delivery from state change. Corrupt session is 14/0 in all three repeats; session persistence core passes multi-window 2→2, immediate close and no-resurrection. The titlebar multi-window workflow fails on window2 handle validity, and first-click automation remains flaky. Three repeats are 8/9/10 FAIL, so GPUI is not the default build.
- 0.72 queues address navigation when a tab has no WebView2 controller yet, so the first submit is not lost; adds an explicit double-click toggle, invalid-JSON raw backup, and a 14-case corrupt-session matrix. Corrupt session is 14/0 in all three repeats; session persistence is 30/0/1 in two repeats. Three repeats are 3/4/4 FAIL (best 3 FAIL/9 UNVERIFIED), so GPUI is not the default build.
- 0.71 adds an explicit Win32 `SW_RESTORE` branch for titlebar restore (GPUI `zoom()` always maximizes), revisioned session payloads with a final-snapshot guard and multi-window close grace, and normal-placement/maximized geometry persistence. Both-window titlebar max/restore/minimize is 16/0/0; multi-window session saves/restores 2 windows; active URL, scroll, geometry and maximized restore pass. Three complete repeats are 5/6/6 FAIL (best 5 FAIL/2 UNVERIFIED), so GPUI is not the default build.
- 0.70 moves WebView2 create/navigate/set_active/focus outside GPUI entity borrows, removing the restored-window `RefCell already borrowed` panic. Titlebar minimize/maximize and Alt+F4 two-window graceful exit pass, and WebView2 cleanup reaches 0. Best repeat is 1 FAIL/17 UNVERIFIED; three repeats are 2/3/1 FAIL, so GPUI is not the default build. Titlebar restore/close, multi-window session restore, session scroll/geometry/maximized/corrupt fallback, and multiwindow Vault delete remain unresolved.
- 0.69 creates WebView2 controllers outside GPUI entity borrows, restoring tab/library/session stability and Session Restore metadata/controller/window recovery. Best repeat is 1 FAIL/15 UNVERIFIED; three repeats are 1/2/2 FAIL, so GPUI is not the default build.
- 0.68 moves WebView2 controller creation outside the GPUI effect/paint flush and lazily attaches restored tabs; second/third window and tab independence reach 6 PASS, but three repeated runs still have 5 FAIL. GPUI is not the default build.
- 0.67 defers the first WebView2 attach for new windows until after the window renders, activates the new window, and publishes tab status before controller creation. GPUI CI still reports 4 FAIL, so GPUI is not the default build.
- 0.66 delivers WebView2 accelerator shortcuts synchronously to GPUI (`SetFocus` + `SendMessageW`) so Ctrl+L and immediately typed characters reach the address bar, and creates a fresh tab when the last tab is closed. The GPUI password smoke now passes 51/0/1; Windows 11 user verification is still UNVERIFIED.
- 0.65 forwards Ctrl/Shift/Alt keydown/keyup explicitly from WebView2 AcceleratorKeyPressed to GPUI, removing reliance on GetKeyState/focus for Ctrl+T/L/N. Actual Windows 11 user verification is still UNVERIFIED; CI cannot validate it.
- 0.64 added GPUI toolbar layout bounds diagnostics via `on_children_prepainted`. Actual bounds: new tab x786,y7 30×30; new window x824,y7 30×30. CI SendInput still returns 0; UIA still has no GPUI toolbar elements; layout-bounds clicks did not produce actions. Actual input E2E remains BLOCKED.
- 0.63 added independent WinForms input harness and AMARE UIA diagnostics. SendInput returned 0 in CI, PostMessage delivered keys without modifiers, UIA exposed only the GPUI window and WebView2 panes (no toolbar buttons), and real button click scans did not trigger actions. Actual input E2E remains FAIL/BLOCKED.
- 0.62 added input-pipeline diagnostics. Actual Ctrl+T and Ctrl+N each reached `action`/`open_new_window result=ok` once, but repeated runs lost foreground/focus; new-tab/new-window button scans produced no mouse trace. Input E2E and 3x stability goals are not met.
- 0.61 removed test-only new-tab/second-window triggers, used direct HWND key/mouse input and deferred new-tab WebView2 attachment, but actual Ctrl+T/new-tab and Ctrl+N/new-window input still failed in repeated automation (`current=2`, `windows=1`). **0.61 real-input and 3x stability goals are not met.**
- 0.60 repeated the same commit three times: attempt 1/2 reached **0 FAIL**; attempt 3 timed out in the tab step after 20 minutes and produced only a partial result JSON. **The 3x consecutive 0 FAIL criterion is not met.**
- The failure gate checks FAIL totals only; it did not convert the tab-step timeout/partial JSON into FAIL. This is a known gate gap.
- Test-only triggers are still used to create repeated tabs and the second window deterministically while invoking the product `new_tab`/`open_new_window` paths. Actual repeated Ctrl+T/Ctrl+N/button input remains UNVERIFIED.
- Vault v2 multi-window verification is **20 PASS / 0 FAIL / 1 UNVERIFIED**. Pending autofill cancellation is UNVERIFIED because the automation could not create the pending proposal.
- CapturePreview normal/active/max/minimize-restore is PASS. Per-tab individual capture is UNVERIFIED.
- ALL_SITE deletion remains **read-only/BLOCKED**. Titlebar actual clicks, multi-window session restore, session scroll restore, corrupt-session banner, Korean IME, context menu, DPI, and WebView2 cleanup remain UNVERIFIED.
- Session restore after restart: tab count/order/title/URL/active tab/window geometry PASS for one window; two-window restore PASS with forced-exit session snapshot; `11-multi-window-session-restored.png`, `15-session-restored.png` and `16-downloads-after-restart.png`.
- WebView2 DownloadStarting progress/complete/failure/restart metadata and file open/folder controls.
- Windows custom titlebar tab integration with system min/max/restore/close hit areas.
- AMARE.ico embedded Windows resource and window/taskbar/Alt+Tab icon capture.
- Actual screenshots for initial/example/multi-tab/bookmark/history/search/download/maximized screens.
- Tab/popup/new-window/IME regression subset and WebView2 cleanup.
- Per-site cookie/permission control, other-user DPAPI failure, multi-window concurrent UI mutation, multi-window graceful exit, DOM/scroll/history restore, download pause/resume/cancel, DPI/multi-monitor remain UNVERIFIED.
- WinUI regression remains separate and passing.

## Not verified / not implemented

- Cross-window tab move/detach and explicit popup window policy.
- Favicon display, loading/error UI visual details.
- Page right-click context menu.
- DPI 125/150/200%, multi-monitor movement, exact pixel alignment.
- IME rapid/mixed input and composition during focus movement.
- Popup/new-window handling.
- One WebView2 per tab and full tab page state.
- Full bookmark/history/AI/update migration.
- Actual Windows 11 device validation.

The existing WinUI 3 app remains the default packaged UI during the transition.
