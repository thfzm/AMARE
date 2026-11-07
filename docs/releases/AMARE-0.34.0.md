# AMARE 0.34.0 — Windows 11 RC Candidate & Final Stability

0.34.0 is the Windows 11 release-candidate stabilization build. It adds tear-off re-merge coverage, stress-test preparation, Windows 11 validation packaging, and final security/performance regressions without adding unrelated features.

## Highlights

- Verify native tear-off and moving the torn-off tab back to the original window.
- Preserve URL/title/pinned/zoom/profile/cookies while documenting the WebView re-creation limits.
- Provide a Portable ZIP based Windows 11 device-validation package and checklist.
- Keep the CurrentUser/WebView2 trusted-CA path explicitly UNVERIFIED/BLOCKED on CI.
- Re-run certificate, drag, cross-window, WebSocket, session, update, storage, and packaging regressions.

## Security

- LocalMachine certificate stores are never modified.
- Certificate errors are cancelled and never auto-bypassed.
- Test certificates/keys are generated at runtime and excluded from packages.
- No distribution signing certificate is fabricated.

## Scope

- Windows remains primary; Android stays at 0.14.0.
- No public GitHub Release is created.
- Unsigned MSIX installation remains BLOCKED.
