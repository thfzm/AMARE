# AMARE 0.45.0 — GPUI Browser Input, Focus & Single-Tab Completion

0.45.0 extends the GPUI/WebView2 browser foundation with actual window screenshot evidence and graceful single-tab shutdown checks.

## Highlights

- GPUI self-test verifies actual example.com URL, title, DOM text, Back, and Reload.
- Windows CI captures the real GPUI window before and after resize.
- Windows CI sends Alt+F4 and checks process exit plus WebView2 child cleanup.
- Existing WinUI 3 app remains the default packaged UI during the transition.

## Limits

- Address-bar keyboard/IME/focus, mouse/wheel/drag, DPI/multi-monitor, popup, and per-tab WebView2 remain UNVERIFIED until actual input automation/device evidence is available.
- No public GitHub Release.
- Unsigned MSIX status unchanged.
