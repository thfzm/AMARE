# AMARE GPUI Windows Shell (0.63.0)

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

## Verified in Windows CI (0.63.0)

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
