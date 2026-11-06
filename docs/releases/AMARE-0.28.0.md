# AMARE 0.28.0 — Reliable Certificate Testing & Tab Interaction Completion

0.28.0 focuses on making the local TLS certificate test infrastructure reliable, validating the actual WebView2 certificate-error path, and completing the remaining native tab-drag combinations without adding unrelated features.

## Highlights

- Replace the manual `SslStream` certificate probe with an in-process Kestrel HTTPS test server.
- Verify TLS handshake independently with a normal .NET client before WebView2 navigation.
- Validate actual `ServerCertificateErrorDetected` for self-signed, expired, and hostname-mismatch certificates, plus the Korean certificate warning dialog with UIAutomation.
- Add native drag middle→first, middle→other-middle, same-position, and cancel tests; verify cross-window forward and reverse transfers.
- Keep the 0.27 cross-window transfer, WebSocket lifecycle, session, update, and storage regressions.

## Security

- Certificate validation is never globally disabled.
- Certificate errors are never bypassed automatically.
- The default action remains Cancel.
- Test certificates/keys are generated at runtime and are not committed or packaged.

## Scope

- Windows remains the primary platform; Android stays at 0.14.0.
- No public GitHub Release is created.
- Unsigned MSIX installation remains subject to the existing runner limitation.
