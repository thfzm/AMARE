# Expected Results (example only)

This is an **example** of the result format, not a claim that a real Windows 11
run has passed. Actual results are written to
`artifacts/windows11-validation/run-<timestamp>-<id>/results.json`.

## Typical self-contained run on a Windows 11 PC with WebView2

```text
PASS os_windows11: build=22631
PASS arch_x64: AMD64
PASS webview2_runtime: 1xx.x.x.x
PASS package_present: ...\AMARE-0.92.0-win-x64-self-contained-unsigned.zip
PASS admin_not_required: admin=False
PASS package_sha256: sha256=24d123... (sidecar matched)
PASS package_required_files: entry=True updater=True backup=True
PASS package_forbidden_user_data: no profile/vault/key/backup/PDB entries
PASS package_manifest: version=0.92.0 architecture=win-x64 files=527
PASS bundle_manifest: app_head=7c7c6c3; package_sha256 matched; script_sha256 matched
PASS package_type: self-contained
PASS package_runtime_prerequisite: self-contained .NET runtime files present; .NET 8 system runtime not required
PASS dotnet_8_runtime: not required; self-contained runtime included
PASS package_winui_resources: App.xbf, MainWindow.xbf, AMARE.Windows.pri present
PASS package_vc_runtime: vcruntime140.dll present
PASS package_isolation_marker: channel=portable-unsigned-validation
PASS app_launch_isolated: process alive; window=True; data_root=...
PASS app_data_root_isolated: test root created
PASS self_contained_runtime_independence: app launched without system .NET 8 detected
PASS backup_create_verify_full: status=Valid; profiles=1
PASS backup_restore_full_isolated: full restore wrote root DB and profiles
PASS backup_restore_single_profile: source=default target=default
PASS backup_wrong_password_rejected: wrong password rejected
UNVERIFIED backup_vault_policy: require manual test with disposable test Vault
PASS updater_apply_preserves_data: version=0.92.0; state=succeeded
PASS updater_wrong_hash_rejected: wrong hash rejected before apply
PASS updater_locked_file_rejected: locked file rejected before apply
PASS updater_launch_failure_rollback: launch failure rolled back
PASS updater_concurrent_rejected: concurrent update rejected
PASS real_user_data_isolated: real AMARE data root timestamp unchanged
```

## Possible non-PASS results

```text
BLOCKED webview2_runtime: not found
  → Install WebView2 Evergreen Runtime, then rerun.

BLOCKED package_runtime_prerequisite: framework-dependent package needs .NET 8
  → Use the self-contained ZIP, or install .NET 8.

FAIL app_launch_isolated: AMARE.Windows.exe exited during startup (exit ...)
  → Keep evidence; check logs/ and app_startup_error.log; do not use real profile.

FAIL backup_restore_*: ...
  → Keep recovery/staging directories; report to the maintainer.

UNVERIFIED self_contained_runtime_independence:
  → .NET 8 was installed on the test PC; true clean-PC independence is not proven.
```

## Evidence

```text
run-<timestamp>-<id>/
  environment.json
  package.json
  results.json
  manual-checklist.md
  screenshots/app-launch.png
  logs/app-logs/
  logs/native-logs/
  hosts/README.txt
  README.md
```

Do not delete a failing run. Re-running creates a new directory and preserves the
first failure.
