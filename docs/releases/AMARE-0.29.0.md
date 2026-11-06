# AMARE 0.29.0 — Browser Stability & Interaction Polish

0.29.0 focuses on the remaining certificate-dialog scenarios, tab-drag and cross-window edge cases, multi-window lifecycle checks, and longer session stability. It does not add a new browser architecture or unrelated features.

## Highlights

- Validate actual self-signed, expired, and hostname-mismatch certificate dialogs in the running AMARE UI.
- Add certificate retry and back flows; keep Cancel as the only default and never bypass certificate validation.
- Add native drag tests for tab close followed by drag, tab churn, same-position, cancel, and repeated order changes.
- Revalidate cross-window transfer in both directions with exact tab ownership.
- Re-run GUI, interop, storage recovery, WebSocket, update, and packaging regressions.

## Security

- Certificate validation is never globally disabled.
- Certificate errors are cancelled by default and never auto-bypassed.
- Test certificates and keys are generated at runtime and are not committed or packaged.

## Scope

- Windows remains the primary platform; Android stays at 0.14.0.
- No public GitHub Release is created.
- Unsigned MSIX install remains subject to the existing runner limitation.
