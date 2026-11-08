# AMARE GPUI Windows Shell (0.47.0)

This is the GPUI + Rust Windows entry point for AMARE. It remains a transition shell; the WinUI 3 app is still the default packaged UI.

## Implemented

- GPUI 0.2.2 native window and app entry point.
- Top tab bar, new-tab button, real address/search text field, back control, theme toggle.
- `EntityInputHandler`/`ElementInputHandler` address input with selection and clipboard editing.
- Windows IME context on the GPUI HWND; Korean layout/native conversion is enabled when the active layout is Korean.
- WebView2 host created as a native child HWND below the GPUI toolbar.
- Ctrl+L focus/select-all, Ctrl+R, Alt+Left/Right, Escape, Enter navigation/search.
- WebView2 `AcceleratorKeyPressed` forwarding of browser shortcuts from page focus.
- Address/title synchronization after WebView2 navigation.
- WebView2 host, window resize, minimize/restore, and graceful shutdown.

## WebView2

The GPUI window HWND is obtained with `raw-window-handle`. A WebView2 environment/controller is created with `webview2-com` 0.39.1 using the GPUI HWND as parent. The controller bounds are updated from GPUI's viewport. Chromium/WebView2 remains the web engine; GPUI is the native browser UI shell.

## Verified in Windows CI (0.47.0)

- `cargo build --release` for Windows x64.
- Real GPUI window, resized window, address-input, Ctrl+L, and Korean-address screenshots.
- Actual address keyboard input and exact URL navigation.
- Ctrl+L from WebView2 page and Ctrl+L select-all when address is focused.
- Ctrl+R, Alt+Left/Right, Escape restore, Enter URL/search.
- Korean IME in the address bar: `한글`, composition/marked range, Enter search.
- Local test page: input/password/textarea, Korean IME, Tab, Ctrl+A/C/V, selection, mouse drag, link click, wheel, Enter/Escape.
- Address↔WebView2 focus transfer, minimize/restore, graceful exit, WebView2 0→0.
- WinUI regression remains separate and passing.

## Not verified / not implemented

- Page right-click context menu.
- DPI 125/150/200%, multi-monitor movement, exact pixel alignment.
- IME rapid/mixed input and composition during focus movement.
- Popup/new-window handling.
- One WebView2 per tab and full tab page state.
- Full bookmark/history/session/AI/update migration.
- Actual Windows 11 device validation.

The existing WinUI 3 app remains the default packaged UI during the transition.
