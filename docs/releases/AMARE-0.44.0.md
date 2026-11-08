# AMARE 0.44.0 — GPUI WebView2 Usable Browser Foundation

0.44.0 extends the GPUI Windows shell from process/window creation to an actual WebView2 page-rendering and navigation self-test.

## Highlights

- GPUI self-test opens `https://example.com/` in an embedded WebView2 child and verifies URL, title, DOM text, and Back.
- GPUI release build and smoke launch pass on Windows CI.
- Existing WinUI 3 browser remains present as the packaged/default implementation.
- Focus transfer, IME, mouse/keyboard interaction, DPI, resize synchronization, popup, and per-tab WebView2 remain UNVERIFIED unless separately tested.

## Scope

- No public GitHub Release.
- Unsigned MSIX status unchanged.
- Existing regression suite and 0.43 GPUI shell are preserved.
