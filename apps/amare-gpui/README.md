# AMARE GPUI Windows Shell (0.43.0)

This is the first GPUI + Rust Windows entry point for AMARE. It is intentionally a transition scaffold, not a completed replacement for the WinUI 3 browser.

## Implemented

- GPUI 0.2.2 native window and app entry point.
- Top tab bar, new-tab button, address/search field, back control, theme toggle.
- Light/dark visual tokens and resizable layout.
- WebView2 host created as a native child HWND below the GPUI toolbar.
- Address Enter navigation, Ctrl+T/L/W handling, tab switching.

## WebView2

The GPUI window HWND is obtained with `raw-window-handle`. A WebView2 environment/controller is created with `webview2-com` 0.39.1 using the GPUI HWND as parent. The controller bounds are updated from GPUI's viewport. This keeps Chromium/WebView2 as the web engine; GPUI is only the native UI shell.

## Verified in Windows CI

- `cargo build --release` for Windows x64.
- Process starts and remains alive.
- Main window handle is created.
- WebView2 child processes are created (observed 0 → 6).

## Not verified / not implemented

- Focus transfer between GPUI toolbar and WebView2.
- Keyboard/mouse/wheel interaction inside the embedded content.
- DPI changes, minimize/restore, monitor moves.
- Popup/new-window handling.
- One WebView2 per tab and real tab switching.
- Full bookmark/history/session/AI/update feature migration.
- Actual Windows 11 device validation.

The existing WinUI 3 app remains the default packaged UI during the transition.
