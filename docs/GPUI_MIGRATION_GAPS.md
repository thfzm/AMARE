# GPUI migration gaps and P0/P1 plan (0.91.0)

This document records the WinUI/GPUI gap plan. It is not a claim that the
items are implemented.

## 0.91.0 status

- With the local fixture actually loaded, keyboard delivery passes in A/B/C
  scenarios, including after one Ctrl+R. The 0.90 post-reload loss is not
  reproduced and matches a fixture-load timing/race.
- Direct WebView2 Reload API and A/B independent navigation remain unverified.
- First-click 6/6 from 0.89 is retained but not rerun.
- Corrupt session: **14/14 PASS** in all three repeats.
- Three repeats: **11/13/13 FAIL**.
- AI/updater/site permissions remain not implemented.

## Status rules

- **Implemented/verified**: code exists and a Windows check passed.
- **Implemented/unverified**: code exists but the CI/user check did not pass.
- **Design only**: scope and constraints are agreed; no production code.
- **Not implemented**: no GPUI code.
- **Platform-specific**: needs a separate design.

WinUI remains the default package. GPUI is not switched to default in 0.91.0.

## P0 — browser stability (must be green before migration)

| Item | WinUI | GPUI 0.68 | Action |
|---|---|---|---|
| Tab create/close | verified | partial | fix 4th-tab CI state race |
| Address/search | verified | partial | keep input tests |
| Shortcuts | verified | partial | real-input + focus checks |
| Multi-window | verified | partial | 2nd/3rd/close tests |
| Session restore | verified | unverified | server readiness + navigation retry |
| WebView2 lifecycle | verified | partial | controller-attached status |
| Titlebar | verified | partial | layout-bound control clicks |
| Cross-window tab move | verified | unverified | add lifecycle test |

P0 exit criteria:

1. three consecutive GPUI runs with 0 FAIL on the same commit/environment,
2. no weakened assertions,
3. WinUI 48/48 and 143/0/9/1 maintained,
4. real Windows 11 user check of the P0 list.

## P1 — existing core features

### AI Web Panel

Design constraints:

- separate WebView2 panel, not a page-content extraction pipeline;
- page title/URL/article context copied only after an explicit user action;
- no automatic page-content upload;
- API keys stored with the existing protected store policy; never written to
  status JSON, logs, or screenshots;
- panel origin isolation from the browsing profile where WebView2 permits it.

Status: **not implemented in GPUI**; WinUI implementation is the reference.

### Updater

Design constraints:

- GitHub Releases metadata, never a silent background install;
- user approval before download/apply;
- SHA-256 verification and signed/unsigned state shown to the user;
- backup/rollback and crash recovery on failure;
- no public release is created by CI in 0.91.0.

Status: **not implemented in GPUI**; WinUI updater remains the reference.

### Site permissions

Design constraints:

- surface WebView2 `PermissionRequested` decisions per origin;
- show current decision and allow reset;
- never auto-grant camera/microphone/geolocation;
- preserve the existing WinUI read-only baseline until the GPUI UI exists.

Status: **read-only informational text only** in GPUI.

## P2 — quality

- UI polish and focus rings
- diagnostics: platform/version, window/tab lifecycle, focus/navigation states
- accessibility/UIA limitations of GPUI custom controls
- DPI, multi-monitor, Snap, taskbar and Alt+Tab checks
- long-running handle/memory checks

## Diagnostics policy

Status JSON may include:

- `platform`, `version`, `window_id`, `window_handle`, `pid`
- active tab id, tab ids/URLs/titles, loading/error, `webview` attached
- panel, settings category, navigation source
- shortcut/action trace and layout bounds
- WebView2 IME context

Must not include:

- passwords, API keys, vault secrets
- page body text or form values
- cookies or authorization headers

## Migration decision

GPUI can become the default only after:

1. P0 green for three consecutive runs,
2. Session Restore and tear-off/re-merge verified on a real Windows 11 PC,
3. P1 items either implemented or explicitly accepted as out of scope,
4. a real user validation report.

Until then WinUI remains the default.
