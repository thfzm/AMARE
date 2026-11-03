# Windows Phase 2.1 probes

This WinForms harness runs the Windows-specific checks that cannot run on the
Linux development host.

## Prerequisites

- Windows 10/11 x64 (Windows 11 build 22000+ for the full manual checklist; CI may use Windows Server)
- .NET 8 SDK (or the version selected by the project)
- WebView2 Evergreen Runtime
- Rust stable toolchain (`cargo`, `rustc`)
- Visual Studio 2022 Build Tools with Windows SDK (for WinUI/WinForms builds)
- Internet access only if you perform the manual DRM playback step

`tools/verify-windows.ps1` checks the core prerequisites and writes
`windows-verify-environment.json` before running the probes.

## Run

```powershell
cd AMARE
.\tools\verify-windows.ps1
```

Or run the harness directly:

```powershell
cd tools\windows-verify
dotnet run
```

Headless/CI mode (manual intervention, memory, and DRM matrix are marked
`UNVERIFIED`):

```powershell
dotnet run -- --ci
```

Click **Run Phase 0.5 probes**. For the user-intervention probe, move the mouse
or press a key when the status bar asks you to. Do not touch input during the
short CDP control phase.

## Result status

`windows-verify-results.json` uses four statuses:

- `PASS`: executed and assertions passed
- `FAIL`: executed and assertions failed
- `UNVERIFIED`: not executed (missing prerequisite/manual interaction) or the
  result is not sufficient to claim support
- `SKIPPED`: intentionally not executed (for example, paid service login)

`executed` is `false` for skipped/unrun probes. **A failed or unexecuted probe is
never reported as PASS.**

## What is automated

- Local HTTP navigation: title/URL/back/forward/reload/stop/new-window/control cleanup
- HTTPS navigation: optional network probe (UNVERIFIED if network is unavailable)
- Tab lifecycle: 10 WebView2 instances, close/suspend/cleanup
- Profile isolation: cookies, document.cookie, localStorage, IndexedDB, CookieManager
- CDP: `DOM.getDocument`, trusted mouse click, `Input.insertText`, scroll gesture
- User intervention: OS-level `GetLastInputInfo` vs a CDP agent-like loop; stops
  the loop when physical input is detected
- 30-tab memory: total WebView2 process working set, browser/renderer/GPU/utility
  breakdown, before/after create/suspend/close, timestamps, environment
- DRM capability matrix: Widevine/PlayReady robustness, codec/resolution,
  `powerEfficient` (hardware acceleration), HDR media-query signals

## What is manual / unverified

- Actual Netflix/Disney+ playback (paid login/entitlement; **not attempted
  without explicit user consent**)
- Actual DRM playback resolution/codec/HDR (use a public Shaka test asset and
  record the result manually)
- Real Windows installer update failure/rollback (installer is Phase 2)
- DPAPI/Windows Hello keychain integration
- WinUI 3 runtime behavior and WebView2 visual hosting
- True per-tab memory mapping (the harness reports process-level data; mapping
  to individual tabs is approximate)
- License/legal review

## Notes

- WebView2 process memory includes all `msedgewebview2.exe` processes visible to
  the user. Close other WebView2 apps for cleaner numbers.
- The 30-tab probe can take 1–2 minutes and several GB of RAM. Run it on the
  target i5-7500/16GB machine.
- This project has **not** been built or executed in the Linux Phase 0.5
  environment. Its results only count after running on Windows.
