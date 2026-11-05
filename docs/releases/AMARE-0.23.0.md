# AMARE 0.23.0 — Native Interaction & Resource Lifecycle

## Focus

0.23.0 targets the remaining native interaction and lifecycle gaps from 0.22.0. It does not add a large new feature surface.

## Changes

- Added explicit `ServerCertificateErrorDetected` handling: certificate errors are cancelled by default, with a user-visible Korean warning and no automatic bypass.
- Extended local TLS probe to use the actual WebView2 certificate-error event.
- Added explicit WebView2 disposal on tab close.
- Added SQLite/WAL recovery test coverage from 0.22.
- Retained local WebSocket, media/fullscreen/PiP, memory, updater, packaging, and regression tests.

## Known limitations

- Native tab drag reorder and cross-window tab movement remain UNVERIFIED.
- Certificate error page behavior in the real Windows 11 UI remains UNVERIFIED if the local TLS probe cannot surface the WebView2 event.
- WebSocket tab-close cleanup still requires real-server lifecycle observation.
- DPI/IME, actual API key, signed MSIX, and public release remain UNVERIFIED/BLOCKED.
