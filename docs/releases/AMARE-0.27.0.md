# AMARE 0.27.0 — Certificate Validation & Tab Lifecycle Hardening

## Focus

0.27.0 focuses on actual WebView2 certificate error validation and additional native tab drag combinations. It retains the cross-window transfer and WebSocket lifecycle work.

## Changes

- Local TLS probe now uses a valid self-signed certificate with SAN/key usage, explicit ALPN HTTP/1.1, and reports server-side handshake errors separately.
- Added native drag combination tests for last→first, first→middle, and middle→last.
- Kept first→last drag, cross-window transfer, and WebSocket tab/window cleanup regressions.
- Cross-window transfer still uses recreation and preserves URL/title/pinned/zoom, not DOM/JS/back-forward/scroll.
- Retained SQLite/WAL recovery, media, memory, updater, and packaging checks.

## Known limitations

- Actual certificate error event/UI may remain UNVERIFIED if WebView2 does not surface the event.
- Native drag cancel/repeated/tear-off and full cross-window state preservation remain UNVERIFIED.
- DPI/IME, actual API key, signed MSIX, and public release remain UNVERIFIED/BLOCKED.
