# AMARE Windows 11 Validation

`Invoke-AmAreWindows11Validation.ps1` is the single entry point for a real
Windows 11 validation run. It never uses the normal `%LOCALAPPDATA%\AMARE`
profile: every app launch, backup, restore and updater test runs under a unique
temporary root via `AMARE_DATA_ROOT`.

Two ZIP types are supported and auto-detected:

- `AMARE-0.92.0-win-x64-self-contained-unsigned.zip` — recommended; includes
  .NET 8 runtime files, so a clean Windows 11 PC does not need a separate .NET
  installation.
- `AMARE-0.92.0-win-x64-unsigned.zip` — framework-dependent; requires .NET 8
  Runtime. The script records `BLOCKED` if it is missing.

Both still require **WebView2 Evergreen Runtime**. The script records `BLOCKED`
if WebView2 is missing.

## Usage

```powershell
# From the repository (recommended)
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\windows11-validation\Invoke-AmAreWindows11Validation.ps1 `
  -PackageZip .\artifacts\phase3-personal-package\AMARE-0.92.0-win-x64-self-contained-unsigned.zip

# From a folder containing the ZIP and this script
powershell -ExecutionPolicy Bypass -File .\Invoke-AmAreWindows11Validation.ps1 -PackageZip .\AMARE-0.92.0-win-x64-self-contained-unsigned.zip
```

No administrator elevation is requested. Preflight runs unelevated. If a check
requires elevation, it is recorded as `BLOCKED` instead of prompting.

## Evidence

Each run creates a new directory under `artifacts/windows11-validation/run-*`:

- `environment.json` — OS/build/arch/WebView2/.NET/DPI/display/admin/memory
- `package.json` — ZIP SHA-256, size, file count, required/forbidden entries
- `results.json` — PASS/FAIL/UNVERIFIED/BLOCKED/SKIPPED records
- `screenshots/` — app launch capture (best effort)
- `logs/` — app and helper logs
- `manual-checklist.md` — items requiring real human interaction
- `hosts/` — test root path and cleanup instructions

Existing evidence is never overwritten. Re-running creates a new run directory;
the first failing run is retained.

## Automated vs manual

Automated (attempted by the script):

- package integrity, manifest, required/forbidden files, package type and runtime prerequisites
- app launch/stay-alive/graceful close with isolated data root
- full backup create/verify and full restore to an empty test root
- single-profile backup/verify/restore to a separate test profile root
- updater success, wrong-hash rejection, locked-file rejection, launch-failure rollback
- evidence and SHA-256 collection
- self-contained runtime files (`hostfxr.dll`, `hostpolicy.dll`, `coreclr.dll`)
  or framework-dependent .NET 8 detection

Manual (must be performed by a person on Windows 11):

- Korean IME composition, DPI 125/150/200%, multi-monitor movement, Snap/Alt+Tab
- real WebView2 login save/fill, real session lock, 5-minute Vault idle lock
- real Vault cross-PC restore (different Windows account/PC)
- full/partial restore through the actual Settings UI including restart
- power-loss/forced-termination during restore/update
- AI actual provider, media/DRM, certificate trust-store prompts
- browser default registration and long-running soak

These are recorded in `manual-checklist.md` and must not be marked PASS by the
automation script.
