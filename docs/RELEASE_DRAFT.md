# AMARE Release Draft Preparation

The release job never publishes a public release automatically. A draft is prepared from the exact CI artifacts and requires a human to review and publish it.

1. Run the Windows verification workflow and download `windows-verification-<run-id>`.
2. Confirm `AMARE-0.41.0-release-manifest.json` has the same commit and version as the tag.
3. Confirm `AMARE-0.41.0-win-x64.zip.sha256` matches the ZIP.
4. Optionally create a **draft** release:

```powershell
.\tools\prepare-github-release.ps1 -Version 0.41.0 -CreateDraft
```

5. Review the draft, assets, notes, and signing status in GitHub.
6. Do not publish until code signing and Windows 11 real-device checks are complete.

Tag rule: `v<major>.<minor>.<patch>` (for example `v0.41.0`). The updater selects non-draft stable releases unless the prerelease channel is selected.
