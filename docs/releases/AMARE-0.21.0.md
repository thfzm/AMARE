# AMARE 0.21.0 — Reliability, Memory & Interaction Polish

## Focus

0.21.0 is a reliability release. It expands memory measurements, local WebSocket/media/certificate probes, tab interaction checks, and recovery validation without adding a large new feature surface.

## Changes

- Memory reports now distinguish WebView2 browser/renderer/GPU/utility, the WindowsVerify test harness, and the AMARE main process when present.
- 30-tab probe now records empty-page, content-page, background, suspend, and close snapshots.
- Added local WebSocket connect/echo/close/reconnect probe.
- Added local audio play/pause/volume/seek probe and media feature checks.
- Added self-signed/expired certificate rejection probe; no automatic bypass is implemented.
- Added clearer certificate/network error messages.
- Added UIAutomation tab drag reorder and new-window open/close checks.
- Kept the 0.20 updater, packaging, release preflight, and data preservation checks.

## Known limitations

- Actual Windows 11 DPI 125/150/200%, mixed-DPI monitors, Korean IME, real media/PiP/fullscreen playback, real API key connections, signed MSIX, public release updates, and actual power-loss recovery remain UNVERIFIED/BLOCKED.
