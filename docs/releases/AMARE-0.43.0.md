# AMARE 0.43.0 — GPUI Native Windows Architecture Transition

0.43.0 introduces the first real GPUI + Rust Windows application entry point while preserving the existing WinUI 3 implementation for comparison and staged migration.

## Highlights

- New `apps/amare-gpui` Rust + GPUI browser shell: native window, top tab bar, address/search field, navigation controls, theme toggle, and WebView2 content host.
- WebView2 is embedded as a native HWND child; the GPUI window remains the host and the WebView2 runtime creates its child processes.
- Windows x64 GPUI release build and smoke launch pass in CI.
- Existing WinUI/C# app remains present during the transition; no claim of full migration yet.

## Validation

- GPUI 0.2.2, WebView2 COM bindings, native HWND embedding.
- Smoke launch: GPUI process + window handle + WebView2 child processes 0→6.
- Detailed focus, input, DPI, resize, popup, and full tab lifecycle remain UNVERIFIED.

## Scope

- No public GitHub Release.
- Unsigned MSIX status unchanged.
- Handle forensics findings and existing regression suite are preserved.
