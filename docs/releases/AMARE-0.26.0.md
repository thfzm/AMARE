# AMARE 0.26.0 — Cross-Window Tabs & Real Certificate UX

## Focus

0.26.0 adds safe cross-window tab movement and expands native tab interaction and certificate validation. It preserves the existing browser surface.

## Changes

- Added cross-window tab transfer using URL/title/pinned/zoom state recreation; source tab is removed only after the target accepts it.
- Added window registry and cross-window drag/drop data.
- Added native tab first→last regression and cross-window UI Automation drag test.
- Kept WebSocket tab/window cleanup regression.
- Improved local certificate probe with SAN/key-usage certificate generation; actual event remains conditional.
- Retained SQLite/WAL recovery, media/PiP, memory, updater, packaging, and GUI checks.

## Limitations

- WebView2 DOM/JS/back-forward/scroll state is not transferred; only URL/title/pinned/zoom.
- Cross-window transfer uses recreation, not WebView2 controller reuse.
- Actual certificate error event/UI remains UNVERIFIED if the local TLS probe cannot surface it.
- DPI/IME, actual API key, signed MSIX, and public release remain UNVERIFIED/BLOCKED.
