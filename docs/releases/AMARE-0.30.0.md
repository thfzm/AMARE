# AMARE 0.30.0 — Final Stability & Release Readiness

0.30.0 is the final stability pass before deciding whether AMARE is ready for daily use. It adds local trusted-CA validation, certificate-dialog regression coverage, remaining tab interaction checks, and release-readiness reporting without adding unrelated browser features.

## Highlights

- Validate a locally trusted test CA with a properly signed server certificate, then remove the test trust anchor.
- Exercise certificate dialogs across normal and error tabs.
- Re-run native drag, cross-window transfer, session recovery, update/rollback, storage, and WebSocket regressions.
- Separate technically verified behavior from Windows CI-only and real Windows 11 device checks.

## Security

- Certificate validation is never globally disabled.
- Certificate errors are cancelled by default and never auto-bypassed.
- Test CA, certificates, and keys are generated at runtime and are not committed or packaged.
- Temporary test trust is removed after the probe.

## Scope

- Windows remains the primary platform; Android stays at 0.14.0.
- No public GitHub Release is created.
- Unsigned MSIX install remains subject to the existing runner limitation.
