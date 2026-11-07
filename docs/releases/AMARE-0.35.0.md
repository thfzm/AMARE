# AMARE 0.35.0 — Handle Leak Investigation & Windows RC Hardening

0.35.0 investigates the AMARE main-process handle growth observed during the 0.34.0 30-minute stress run and hardens the Windows RC without adding unrelated features.

## Highlights

- Repeat stress with keyboard-only tab churn to separate UIAutomation harness cost from application handle growth.
- Keep the 30-minute stress workflow and record time-series handle/memory/WebView2 process data.
- Preserve tear-off, cross-window, certificate, session, update, storage, and packaging regressions.
- Keep CurrentUser/WebView2 trusted-CA validation UNVERIFIED/BLOCKED on CI.
- Provide Windows 11 Portable ZIP validation guidance and known limits.

## Security

- LocalMachine certificate stores are never modified.
- Certificate errors are cancelled and never auto-bypassed.
- Test certificates and keys are generated at runtime and excluded from packages.
- No distribution signing certificate is fabricated.

## Scope

- Windows remains primary; Android stays at 0.14.0.
- No public GitHub Release is created.
- Unsigned MSIX installation remains BLOCKED.
