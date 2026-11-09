# AMARE GPUI Windows Shell (0.53.0)

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
- SQLite settings-backed session save/restore for tab URLs/titles/order, active tab, window geometry, and maximized state, with `AMARE_GPUI_RESTORE=0` opt-out.
- Cross-window tab transfer/detach is UNVERIFIED; official `SetParentWindow` path exists but is not validated end-to-end.

## WebView2

The GPUI window HWND is obtained with `raw-window-handle`. A WebView2 environment/controller is created with `webview2-com` 0.39.1 using the GPUI HWND as parent. The controller bounds are updated from GPUI's viewport. Chromium/WebView2 remains the web engine; GPUI is the native browser UI shell.

## Verified in Windows CI (0.53.0)

- `cargo build --release` for Windows x64.
- Real GPUI window, resized window, address-input, Ctrl+L, and Korean-address screenshots.
- Actual address keyboard input and exact URL navigation.
- Ctrl+L from WebView2 page and Ctrl+L select-all when address is focused.
- Ctrl+R, Alt+Left/Right, Escape restore, Enter URL/search.
- Korean IME in the address bar: `한글`, composition/marked range, Enter search.
- Local test page: input/password/textarea, Korean IME, Tab, Ctrl+A/C/V, selection, mouse drag, link click, wheel, Enter/Escape.
- Address↔WebView2 focus transfer, minimize/restore, graceful exit, WebView2 0→0.
- GPUI bookmark/history add, duplicate handling, list, navigation, search, delete, clear confirmation, restart persistence.
- Session restore after restart: tab count/order/title/URL/active tab/window geometry PASS for one window; `15-session-restored.png` and `16-downloads-after-restart.png`.
- WebView2 DownloadStarting progress/complete/failure/restart metadata and file open/folder controls.
- Windows custom titlebar tab integration with system min/max/restore/close hit areas.
- AMARE.ico embedded Windows resource and window/taskbar/Alt+Tab icon capture.
- Actual screenshots for initial/example/multi-tab/bookmark/history/search/download/maximized screens.
- Tab/popup/new-window/IME regression subset and WebView2 cleanup.
- Multi-window restore, session settings/progress/failure UI, DOM/scroll/history restore, cross-window transfer/detach, download pause/resume/cancel automation, context menu, and DPI/multi-monitor remain UNVERIFIED.
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
