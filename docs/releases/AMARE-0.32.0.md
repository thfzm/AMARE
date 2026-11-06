# AMARE 0.32.0 — Release Candidate Stabilization

0.32.0 is a stabilization release focused on certificate-test separation, tab/window lifecycle evidence, long-run expectations, Windows 11 validation readiness, and RC packaging.

## Highlights

- Keep CurrentUser Trust Store installation explicitly UNVERIFIED/BLOCKED on the non-interactive Windows runner.
- Add a separate custom-trust TLS simulation for CA-signed chain, SAN, EKU, trusted handshake, and untrusted-CA rejection.
- Preserve existing certificate error UI and no-bypass behavior.
- Keep existing native drag, cross-window, session, update, storage, and packaging regressions.
- Document Windows 11 device checks, signing status, and remaining limits.

## Security

- Certificate validation is never globally disabled.
- Certificate errors are cancelled by default and never auto-bypassed.
- Test certificates and keys are generated at runtime and are not committed or packaged.
- LocalMachine certificate stores are not modified.

## Scope

- Windows remains the primary platform; Android stays at 0.14.0.
- No public GitHub Release is created.
- Unsigned MSIX installation remains BLOCKED by the missing distribution signing identity.
