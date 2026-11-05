# AMARE 0.25.0 — Native Tab Interaction & Certificate Validation

## Focus

0.25.0 targets two remaining gaps: native tab drag/window movement and real WebView2 certificate error validation. WebSocket lifecycle from 0.24.0 is retained as a regression.

## Changes

- Added native pointer coordinate drag validation with tab ID/order checks in UI Automation.
- Added actual AMARE local WebSocket tab/window cleanup regression tests.
- Added local HTTPS certificate probes using the actual WebView2 `ServerCertificateErrorDetected` event.
- Certificate errors remain Cancel-only; no automatic bypass.
- Retained SQLite/WAL recovery, video/fullscreen/PiP, memory, updater, packaging, and GUI regression checks.

## Known limitations

- Cross-window tab transfer is not implemented/verified.
- Certificate error event/page can remain UNVERIFIED if the local TLS server cannot reach WebView2 validation.
- DPI/IME, actual API key, signed MSIX, and public release remain UNVERIFIED/BLOCKED.
