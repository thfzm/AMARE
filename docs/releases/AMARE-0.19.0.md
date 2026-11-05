# AMARE 0.19.0

## Release readiness

- Portable updater now rejects repeat installation, downgrade, and concurrent helper execution.
- Cancelled/failed downloads remove partial `.part` files.
- Update validation covers malformed manifests, architecture/version mismatch, corrupt ZIPs, traversal, checksum mismatch, failed replacement, crash recovery, and missing health markers.
- The updater keeps user data, settings, bookmarks, and history outside the replaced application directory.
- Windows CI adds isolated helper application and path/space-friendly launch checks.
- Diagnostics and API AI mock coverage remain available from 0.18.

## Packaging

- Portable ZIP includes `AMARE.Updater.exe`, `VERSION`, `amare-update.json`, and release notes.
- SHA-256 sidecar, `checksums.txt`, and release manifest are generated.
- Unsigned MSIX remains a development artifact. Actual signing and signed MSIX installation are not available.
- No public GitHub Release is created by this build.

## Known limitations

- A real public GitHub Release update, real API key connection, signed MSIX install, and Windows 11 real-device DPI/IME/multi-monitor checks remain UNVERIFIED/BLOCKED.
- Actual power-loss recovery is not claimed; process-crash recovery is simulated and tested separately.
