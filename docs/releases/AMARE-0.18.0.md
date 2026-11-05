# AMARE 0.18.0

## Highlights

- Portable update pipeline can verify, stage, apply, and roll back a downloaded package.
- Update packages now include `amare-update.json` with product, version, architecture, runtime, and required-file metadata.
- The app verifies the release SHA-256 and the package manifest before offering installation.
- A separate updater helper waits for the browser to exit, preserves `%LOCALAPPDATA%\AMARE` user data, backs up the current app files, and restores the backup if the new version fails to report a healthy startup.
- Update state is journaled in `%LOCALAPPDATA%\AMARE\update-state.json`; interrupted updates can be recovered from the browser menu.
- API AI settings now include provider selection and continue to support OpenAI-compatible Chat Completions endpoints.
- Diagnostics shows app, Windows, WebView2, architecture, window size, DPI, engine state, and update state without secrets.
- AI API mock coverage now exercises success, authentication failure, key redaction, and streaming.

## Packaging

- Portable ZIP and unsigned MSIX are still development artifacts.
- The portable package includes `AMARE.Updater.exe`, `VERSION`, `amare-update.json`, and `RELEASE_NOTES.md`.
- The release also publishes a SHA-256 sidecar and `checksums.txt`.
- No production code-signing certificate is included. Unsigned MSIX installation remains blocked by Windows.

## Verification notes

- GitHub Releases are not published automatically.
- The update end-to-end path is tested with isolated filesystem packages and mock downloads; a real public release update is UNVERIFIED until a signed/approved release is published.
- A real API key is not included. Provider paths are mock-tested; live account authentication and billing behavior are UNVERIFIED.
- Windows 11 real-device DPI, IME, and multi-monitor checks remain UNVERIFIED.
