# Failure Recovery Guide

Use only the isolated validation test root. Never delete or edit the real
`%LOCALAPPDATA%\AMARE` profile, Vault, backups or existing ZIPs.

## WebView2 missing

- Result: `BLOCKED webview2_runtime`.
- Action: install Microsoft Edge WebView2 Evergreen Runtime, then rerun the same
  command. No admin elevation is required for the validation script itself;
  the WebView2 installer may request it.
- Do not treat the run as passed until the script completes.

## .NET 8 missing with the framework-dependent ZIP

- Result: `BLOCKED package_runtime_prerequisite`.
- Action: use the self-contained ZIP in this bundle. It includes the .NET 8
  runtime and VC++ runtime. The framework-dependent ZIP is not the handoff
  default.

## App launch failure

- Result: `FAIL app_launch_isolated` or `FAIL self_contained_clean_launch`.
- Keep the run directory and `logs/`.
- Check `app_startup_error.log` under the test root and Windows Event Viewer.
- Do not switch to the real user profile to reproduce.

## Backup/restore failure before apply

- The helper reports `failed`/`preparing`/`prepared`.
- The target test profile was not changed.
- Keep `updates/work/`, `backups/`, and any `restore-journal.json`.
- Re-run only in a new evidence directory.

## Restore interrupted during apply

- On next launch the app calls the backup helper `recover`.
- The old test profile is restored from the recovery directory.
- If recovery fails, keep the recovery directory and `restore-journal.json` and
  report them; do not delete them.

## Updater failure/rollback

- The updater helper reports `rolled_back` or `failed`.
- The test app directory should contain the old `VERSION` and `AMARE.Windows.exe`.
- If rollback fails, keep `update-state.json`, `update.lock`, `updates/work/`,
  and logs. Do not delete them.
- Never run destructive updater tests against the real user profile.

## Forced termination / session lock

- Use only the test profile and test data root.
- After a forced termination, relaunch and record whether recovery is offered.
- For Windows session lock, lock/unlock normally; the account must remain usable.
- If the app is left running, close only the validation instance, not other
  applications.

## Cleanup

- After evidence collection, the test root may be deleted manually.
- Do not delete evidence run directories.
- Do not delete existing user backups or ZIPs.
- Do not delete recovery/staging directories after a failure until the
  maintainer has reviewed them.

## Return to maintainer

Return the entire `run-<timestamp>-<id>` folder plus the manual checklist and any
unexpected screenshots/logs. Do not include real Vault passwords or API keys.
