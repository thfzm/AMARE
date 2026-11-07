# AMARE 0.33.0 — Final RC Validation & Deployment Readiness

0.33.0 closes release-candidate gaps without adding unrelated features: native tear-off with session transfer, explicit CA trust-store limits, Windows 11 device validation packaging, and final security/performance regressions.

## Highlights

- Native tear-off creates a real browser window with the dragged tab snapshot when a drag is released outside the source window.
- Keep cross-window ownership, source safety, and session metadata behavior consistent.
- Preserve the existing certificate Cancel/no-bypass policy and document OS trust-store limitations.
- Prepare a Portable ZIP based Windows 11 validation checklist and known-limits package.

## Security

- LocalMachine certificate stores are never modified by tests.
- Certificate validation is never disabled or auto-bypassed.
- Test certificates and keys are generated at runtime and excluded from packages.
- No distribution signing certificate is fabricated.

## Scope

- Windows remains primary; Android stays at 0.14.0.
- No public GitHub Release is created.
- Unsigned MSIX installation remains BLOCKED.
