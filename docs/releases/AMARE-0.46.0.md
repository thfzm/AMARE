# AMARE 0.46.0 — GPUI Real User Input & Focus Completion

0.46.0 adds actual external pointer/keyboard input into the GPUI address bar and verifies the resulting WebView2 URL, while preserving the existing WinUI 3 path.

## Highlights

- GPUI address bar click + keyboard text entry + Enter navigation verified through an external Windows automation step.
- Exact WebView2 URL verified through a status file written by the actual NavigationCompleted event.
- Real GPUI window, resized-window, and address-input screenshots captured.
- Alt+F4 graceful exit and WebView2 child cleanup remain PASS.

## Limits

- Ctrl+L focus/clear behavior, URL-vs-search classification, page-internal input, Korean IME composition, mouse/wheel/drag, DPI, and per-tab WebView2 remain UNVERIFIED.
- No public GitHub Release.
- Unsigned MSIX status unchanged.
