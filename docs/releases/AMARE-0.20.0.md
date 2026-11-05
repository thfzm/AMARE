# AMARE 0.20.0 — Daily Driver Readiness

## Focus

This release prioritizes day-to-day browsing reliability, tab/session stability, user-data safety, local web compatibility, and measurable performance over new features.

## Changes

- Added Ctrl+Tab and Ctrl+Shift+Tab tab switching.
- Added local browser compatibility probes for SPA updates, cookies/session persistence, file upload, redirects, zoom, media capability, and local navigation.
- Expanded tab/session tests to 30 sequential tabs and last-tab replacement.
- Enabled 5/10/20-tab and 30-tab memory measurements in Windows CI.
- Added startup, navigation, tab creation, close, and session restore timing to the GUI smoke test.
- Added corrupt-update-state and AI settings round-trip coverage.
- Kept the portable updater safety checks from 0.19: duplicate/downgrade rejection, helper lock, checksum/manifest validation, rollback, crash recovery, and user-data preservation.

## Packaging

- Portable ZIP includes `AMARE.Updater.exe`, `VERSION`, `amare-update.json`, and release notes.
- SHA-256 sidecar, `checksums.txt`, release manifest, and release preflight are generated.
- No public GitHub Release is created by this build.
- Unsigned MSIX remains a development artifact; signed install is not available.

## Known limitations

- Actual Windows 11 device, DPI 125/150/200%, mixed-DPI multi-monitor, Korean IME, real power-loss recovery, signed MSIX, and real API-key connections remain UNVERIFIED/BLOCKED.
- WebSocket, actual media playback, certificate-error UX, and fullscreen/PiP behavior are not fully automated.
