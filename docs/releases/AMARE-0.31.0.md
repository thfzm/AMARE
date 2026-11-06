# AMARE 0.31.0 — Hardware Validation & RC Hardening

0.31.0 focuses on the remaining release-candidate risks: local trusted-CA test infrastructure, tab and window edge cases, long-session guidance, Windows 11 validation readiness, and code-signing status. It does not add unrelated browser features.

## Highlights

- Diagnose and isolate local trusted-CA installation on the Windows runner; keep the test UNVERIFIED if the OS trust store cannot be changed non-interactively.
- Add a CurrentUser-store fallback while never touching LocalMachine or deleting unrelated certificates.
- Include DPI, display count, WebView2, update state, and certificate-policy state in Diagnostics; allow opening the local log file.
- Re-run certificate dialogs, native drag, cross-window ownership, WebSocket, session, storage, update, and packaging regressions.
- Document Windows 11 device validation and the difference between development self-signed and distribution code signing.

## Security

- Certificate validation is never globally disabled.
- Certificate errors are cancelled by default and never auto-bypassed.
- Test CA, certificates, and keys are generated at runtime and are not committed or packaged.
- The test trust store is scoped to CurrentUser; LocalMachine is not modified.

## Scope

- Windows remains the primary platform; Android stays at 0.14.0.
- No public GitHub Release is created.
- Unsigned MSIX install remains subject to the existing runner limitation.
