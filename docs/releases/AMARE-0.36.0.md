# AMARE 0.36.0 — Native Handle Attribution & Resource Leak Root Cause

0.36.0 is an analysis-first stabilization release focused on the AMARE main-process handle growth observed in 0.34.0/0.35.0.

## Findings

- Per-cycle UI Automation is not sufficient to explain the growth; keyboard-only stress still grows handles.
- Handle type snapshots show type31/type8 as the primary increase; their actual object type names remain UNVERIFIED because safe dynamic resolution failed.
- No speculative `CloseHandle`, forced GC, or WebView2 rewrite was made.

## Scope

- Preserve tear-off/re-merge, certificate UX, cross-window, session, update, storage, and packaging behavior.
- Keep CurrentUser/WebView2 trusted-CA validation UNVERIFIED/BLOCKED on the non-interactive runner.
- Provide Windows 11 Portable ZIP validation guidance and known limits.

## Security

- LocalMachine certificate stores are never modified.
- Certificate errors are cancelled and never auto-bypassed.
- Test certificates and keys are generated at runtime and excluded from packages.
- No distribution signing certificate is fabricated.
