# AMARE Windows 11 Manual Validation Checklist

Run this after `Invoke-AmAreWindows11Validation.ps1` on a real Windows 11 x64 PC.
Use only a dedicated test profile/test data. Do not use or delete the primary
user profile, Vault, backups or existing ZIP packages.

Record each item as `PASS`, `FAIL`, `UNVERIFIED` or `BLOCKED`, with the actual
Windows build, WebView2 version, DPI and display layout in `environment.json`.

## Priority and evidence rules

Use the self-contained ZIP by default. The script marks a framework-dependent ZIP
`BLOCKED` when .NET 8 is missing, and any ZIP `BLOCKED` when WebView2 Evergreen is
missing. Record every item as `PASS`/`FAIL`/`UNVERIFIED`/`BLOCKED`.

| Priority | IDs | Success evidence | Failure recovery |
|---|---|---|---|
| P0 | A1, A3, B1, B3, C1, C3, C5, G3, G4, G5, G7, G8, J2, J3, J9, K2, K4 | screenshot + app/helper log + `results.json` | keep test root; re-run in a new evidence dir; restore test profile from backup |
| P1 | B2, B4, C2, C4, C6, D1-D5, E1-E5, F1-F4, H1-H5, I1-I6, J4-J8, K1, K3, K5-K7, L1-L4 | screenshot + log + environment JSON | do not delete failing evidence; record exact OS/build/DPI/WebView2 |
| P2 | B5, E3, J10, L2-L4, Reader complex sites, UI density/font, diagnostics/crash dump | log + screenshot + package report | keep recovery/staging directories; record manual recovery steps |

**P0 must pass before calling the package usable.** A failure must be reported with
the original evidence; do not overwrite it with a later successful run.

**Data safety:** use only the validation test root created by the script. Never
select or delete the real user's `%LOCALAPPDATA%\AMARE` profile, Vault, backups or
existing ZIPs. The script sets `AMARE_DATA_ROOT`; verify `environment.json` and
`hosts/README.txt` before manual tests.

**Runtime:** self-contained ZIP includes .NET 8 runtime files; WebView2 Evergreen is
still required. The framework-dependent ZIP needs .NET 8 Runtime.

## A. First launch / shutdown

| ID | Procedure | Expected | Status |
|---|---|---|---|
| A1 | Extract the ZIP to a normal folder and launch `AMARE.Windows.exe` | window opens, no missing-runtime dialog |  |
| A2 | Open Diagnostics and copy all fields | version/WebView2/DPI/displays/log path shown |  |
| A3 | Close all windows normally, relaunch | no crash, session/tab policy consistent |  |
| A4 | Force-terminate and relaunch | recovery/session result is explicit, no silent data loss |  |

## B. Tabs / windows

| ID | Procedure | Expected | Status |
|---|---|---|---|
| B1 | Create/switch/close/reorder tabs | active tab and order correct |  |
| B2 | Drag tab outside the strip | tear-off/new-window result recorded (UNVERIFIED if not implemented) |  |
| B3 | Open two windows, move tabs A→B and B→A | counts and active tabs correct |  |
| B4 | Snap, maximize/restore, Alt+Tab | no layout corruption or lost tabs |  |
| B5 | 30 tabs, then close | memory returns, no freeze |  |

## C. Address / search / keyboard / IME

| ID | Procedure | Expected | Status |
|---|---|---|---|
| C1 | URL, search query, IDN URL | correct navigation/search |  |
| C2 | Ctrl+T/W/Tab/L/F, Alt+Left/Right | correct action, no focus trap |  |
| C3 | Korean IME composition in address bar | no premature navigation, no broken composition |  |
| C4 | Korean IME in page form and Vault fields | no duplication/loss |  |
| C5 | 100/125/150/200% DPI | no clipping, dialogs usable |  |
| C6 | Two monitors with different DPI | moving window preserves layout |  |

## D. WebView2 / downloads / popups

| ID | Procedure | Expected | Status |
|---|---|---|---|
| D1 | Navigate normal HTTPS, back/forward/reload/stop | correct content and history |  |
| D2 | Open popup/`window.open` | new tab/window opens, closes cleanly |  |
| D3 | Download a small file | save dialog/path works, file exists |  |
| D4 | Cancel/interrupt a download | no final partial file, UI stays usable |  |
| D5 | Self-signed/expired/hostname-mismatch site | warning/cancel, no automatic bypass |  |

## E. Bookmarks / history / sessions / downloads metadata

| ID | Procedure | Expected | Status |
|---|---|---|---|
| E1 | Add/edit/remove bookmark, restart | persisted |  |
| E2 | Visit pages, inspect history, clear | correct entries, clear scope |  |
| E3 | Open 7+ tabs and 2 windows, close, relaunch | tabs/windows restored |  |
| E4 | Force-kill then relaunch | recovery result is explicit |  |
| E5 | Download metadata across restart | correct records |  |

## F. Profiles / isolation

| ID | Procedure | Expected | Status |
|---|---|---|---|
| F1 | Create profile A/B, set different bookmarks/settings | isolated |  |
| F2 | Switch profile and inspect WebView2 cookies/localStorage | no cross-profile leak |  |
| F3 | Delete/reset one profile | other profile untouched |  |
| F4 | Multi-window same/different profile | no unexpected shared state |  |

## G. Password Vault / WebView2 capture/fill

| ID | Procedure | Expected | Status |
|---|---|---|---|
| G1 | Initialize Vault, add/edit/reveal/copy entry | correct, masked by default |  |
| G2 | Lock/unlock, wrong password | no plaintext leak, failed unlock preserved |  |
| G3 | Real HTTPS login page, submit credentials, Save | prompt only after submit; approved save exact origin |  |
| G4 | Reject save prompt | no entry created |  |
| G5 | Explicit Fill on exact-origin page | fields filled, no auto-submit/Enter/click |  |
| G6 | Fill on lookalike origin/HTTP/cross-origin iframe | rejected |  |
| G7 | 5-minute idle lock | Vault locks, no entry leak |  |
| G8 | Windows session lock | Vault locks, no entry leak |  |
| G9 | AI/Reader WebView | no Vault bridge or credential access |  |

## H. Site permissions

| ID | Procedure | Expected | Status |
|---|---|---|---|
| H1 | Camera/mic/notifications request | consent dialog, origin exact |  |
| H2 | Deny once/allow once/always allow/block | correct persistence |  |
| H3 | iframe/popup permission | correct frame/origin isolation |  |
| H4 | Reset site/all permissions | only selected scope removed |  |
| H5 | Legacy WebView2 allow audit | fail-closed behavior |  |

## I. AI panel / API / keys

| ID | Procedure | Expected | Status |
|---|---|---|---|
| I1 | AI panel open/close, Reader/New Tab interaction | no focus/navigation break |  |
| I2 | Mock mode offline | deterministic response, no network |  |
| I3 | Actual provider with real key | request succeeds, errors mapped |  |
| I4 | Wrong/revoked key | clear error, key not logged |  |
| I5 | Save/delete key via Credential Manager | masked, persists, deletion verified |  |
| I6 | Page-context consent | explicit consent, labeled untrusted |  |

## J. Backup / restore / Vault portability

| ID | Procedure | Expected | Status |
|---|---|---|---|
| J1 | Create full backup without Vault, verify | Valid, SHA-256 displayed |  |
| J2 | Restore full backup via Settings UI | confirmation, progress, restart, selected profile opens |  |
| J3 | Restore a single profile from full backup | other profiles unchanged |  |
| J4 | Restore as new profile | new profile contains backup data |  |
| J5 | Create backup with Vault, wrong Vault password | rejected before apply; existing Vault unchanged |  |
| J6 | Restore Vault backup on another Windows PC/account | Vault entries restored, Vault locked |  |
| J7 | Restore backup without Vault over existing profile | existing Vault preserved |  |
| J8 | Kill app during staging | journal cleanup, existing data preserved |  |
| J9 | Kill app during apply | restart shows recovery and restores old data |  |
| J10 | Power-loss simulation during apply (VM only) | recovery directory remains |  |

## K. Portable updater

| ID | Procedure | Expected | Status |
|---|---|---|---|
| K1 | Put a newer test ZIP on a local HTTPS/GitHub-like source | check shows version/notes |  |
| K2 | Download/verify/apply/restart | new version runs, data preserved |  |
| K3 | Wrong SHA-256 | rejected before apply |  |
| K4 | Launch failure/health timeout | old version restored |  |
| K5 | Locked app file | rejected before replacement |  |
| K6 | Interrupted apply then restart | recovery/rollback works |  |
| K7 | Repeat/downgrade attempt | rejected |  |

## L. Diagnostics / packaging

| ID | Procedure | Expected | Status |
|---|---|---|---|
| L1 | Logs contain no API key/Vault password/page content | PASS/FAIL |  |
| L2 | Package contains no profiles/vault keys/API keys/backups | PASS/FAIL |  |
| L3 | Unsigned status is disclosed | PASS/FAIL |  |
| L4 | .NET 8/WebView2 prerequisites documented | PASS/FAIL |  |

## Evidence

Attach screenshots, `results.json`, `environment.json`, `package.json`, app logs,
and any crash dump. Do not overwrite a failing run with a later successful run;
keep both directories.
