# AMARE 0.22.0 — Browser Interaction & Data Integrity

## Focus

This release validates actual browser interaction and data recovery boundaries. It adds local WebSocket cleanup, video/fullscreen/PiP probes, SQLite/WAL recovery tests, and clearer tests for tab/window behavior. It does not add a large new feature surface.

## Changes

- Local WebSocket probe now checks connect, echo, clean close, reconnect, and tab-close release.
- Local video probe checks playback, pause, volume, seek, unsupported codec handling, and records fullscreen/PiP actual outcomes separately from feature availability.
- Added SQLite/WAL recovery tests for normal checkpoint/reopen, corrupt-copy detection, missing DB restore from backup, and future-schema copy rejection.
- Analyzed and attempted native tab drag/new-window behavior. New-window open/close PASS; native drag reorder remains UNVERIFIED because the UI Automation drag did not produce reorder.
- Certificate TLS probe remains UNVERIFIED due `NavigationCompleted` timeout; no certificate bypass was implemented.
- 5/10/20/30-tab memory measurements and the 0.21 updater/data checks remain.

## Known limitations

- Actual Windows 11 DPI/IME, real public API key, signed MSIX, public Release update, fullscreen/PiP actual media, and cross-window tab movement are not claimed as complete.
- No user data is deleted or reset during recovery tests; all destructive tests use temporary copies/profiles.
