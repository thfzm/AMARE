# AMARE 0.24.0 — Browser Interaction Verification & Lifecycle Fixes

## Focus

0.24.0 targets three repeatedly unresolved areas: WebSocket/tab resource cleanup, native tab drag/window movement, and real WebView2 certificate error UX. It keeps the existing browser feature surface and prioritizes reproducible lifecycle tests.

## Changes

- Fixed the local WebSocket test page so reconnect sockets close; the previous active-count false observation was partly test instrumentation.
- Added actual AMARE tab-close and window-close WebSocket checks using a local server and UIAutomation.
- Added certificate error handling through `ServerCertificateErrorDetected` with `Cancel` and a user-visible Korean warning; no bypass.
- Retained SQLite/WAL recovery, video/fullscreen/PiP, memory, updater, packaging, and regression checks.
- Added a local WS server to the UI smoke harness for real app lifecycle observation.

## Known limitations

- Native tab drag reorder and cross-window tab movement remain unverified unless the native UI test can reproduce real pointer reorder.
- Certificate error event/page behavior can remain UNVERIFIED if the local TLS server cannot reach WebView2's certificate event.
- DPI/IME, actual API key, signed MSIX, and public release remain UNVERIFIED/BLOCKED.
