param(
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [Parameter(Mandatory = $true)][string]$AppSource,
    [Parameter(Mandatory = $true)][string]$UpdaterExe,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$BackupExe
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Path $AppSource)) { throw "AppSource does not exist: $AppSource" }
if (-not (Test-Path $UpdaterExe)) { throw "UpdaterExe does not exist: $UpdaterExe" }
if ($BackupExe -and -not (Test-Path $BackupExe)) { throw "BackupExe does not exist: $BackupExe" }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$checks = New-Object System.Collections.Generic.List[object]
$utf8 = [System.Text.UTF8Encoding]::new($false)
$root = Join-Path $env:TEMP ("amare-portable-updater-e2e-" + [guid]::NewGuid().ToString("N"))
$stage = Join-Path $OutputDir "portable-updater-e2e-stage"
$zip = Join-Path $OutputDir "AMARE-$Version-portable-updater-e2e.zip"
$reportPath = Join-Path $OutputDir "portable-updater-e2e.json"

function Write-Utf8($Path, $Text) {
    [System.IO.File]::WriteAllText($Path, $Text, $utf8)
}

function Get-TreeHashes([string]$Root) {
    $map = @{}
    if (-not (Test-Path $Root)) { return $map }
    Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
        $relative = [System.IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
        if ($relative -eq "update-state.json" -or $relative -eq "update.lock" -or $relative -eq "update-health.json") { return }
        if ($relative.StartsWith("updates/", [StringComparison]::Ordinal)) { return }
        if ($relative.StartsWith("logs/updater.", [StringComparison]::OrdinalIgnoreCase)) { return }
        if ($relative.StartsWith("updater/", [StringComparison]::Ordinal)) { return }
        $map[$relative] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $map
}

function Assert-SameTree([string]$Root, $Expected, [string]$Stage) {
    $actual = Get-TreeHashes $Root
    if ($actual.Count -ne $Expected.Count) {
        throw "$Stage changed user file count: $($Expected.Count) -> $($actual.Count)"
    }
    foreach ($key in $Expected.Keys) {
        if (-not $actual.ContainsKey($key)) { throw "$Stage removed user file: $key" }
        if ($actual[$key] -ne $Expected[$key]) { throw "$Stage changed user file: $key" }
    }
}

function New-Fixture([string]$Root) {
    $app = Join-Path $Root "app"
    $data = Join-Path $Root "data"
    New-Item -ItemType Directory -Force -Path $app, $data | Out-Null
    Write-Utf8 (Join-Path $app "VERSION") "0.0.1`n"
    Write-Utf8 (Join-Path $app "AMARE.Windows.exe") "old-exe"
    Write-Utf8 (Join-Path $app "old-only.txt") "old"
    $userFiles = [ordered]@{
        "profiles/default/amare.db" = "sqlite-profile"
        "profiles/default/site-permissions.json" = '{"https://example.com":"allow"}'
        "vault/default.key" = "dpapi-protected-vault-key"
        "settings.json" = '{"theme":"dark"}'
        "app-preferences.json" = '{"language":"ko"}'
        "update-preferences.json" = '{"channel":"stable"}'
        "ai-api-settings.json" = '{"model":"gpt-test","provider":"openai"}'
        "bookmarks.db" = "bookmarks"
        "history.db" = "history"
        "downloads.json" = '[{"name":"file.zip"}]'
        "profile.db" = "profile"
    }
    foreach ($pair in $userFiles.GetEnumerator()) {
        $path = Join-Path $data ($pair.Key.Replace('/', [IO.Path]::DirectorySeparatorChar))
        New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
        Write-Utf8 $path $pair.Value
    }
    return [pscustomobject]@{ App = $app; Data = $data }
}

function New-PortablePackage([string]$EntrySource, [string]$ZipPath, [string]$StagePath) {
    if (Test-Path $StagePath) { Remove-Item $StagePath -Recurse -Force }
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    New-Item -ItemType Directory -Force -Path $StagePath | Out-Null
    Copy-Item (Join-Path $AppSource "*") -Destination $StagePath -Recurse -Force
    Copy-Item $UpdaterExe (Join-Path $StagePath "AMARE.Updater.exe") -Force
    if ($BackupExe) {
        Copy-Item $BackupExe (Join-Path $StagePath "AMARE.Backup.exe") -Force
    }
    if ($EntrySource) {
        Copy-Item $EntrySource (Join-Path $StagePath "AMARE.Windows.exe") -Force
    }
    Write-Utf8 (Join-Path $StagePath "VERSION") "$Version`n"
    $requiredFiles = if ($BackupExe) {
        @("VERSION", "AMARE.Windows.exe", "AMARE.Updater.exe", "AMARE.Backup.exe")
    } else {
        @("VERSION", "AMARE.Windows.exe", "AMARE.Updater.exe")
    }
    $manifest = [ordered]@{
        schema = 1
        product = "AMARE"
        version = $Version
        architecture = "win-x64"
        entryPoint = "AMARE.Windows.exe"
        runtime = "win-x64-self-contained"
        channel = "portable"
        requiredFiles = $requiredFiles
    }
    Write-Utf8 (Join-Path $StagePath "amare-update.json") ($manifest | ConvertTo-Json -Depth 5)
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $StagePath, $ZipPath, [System.IO.Compression.CompressionLevel]::Fastest, $false)
    return Get-FileHash $ZipPath -Algorithm SHA256
}

function Invoke-Helper([string[]]$Arguments) {
    & $UpdaterExe @Arguments | Write-Host
    $code = $LASTEXITCODE
    return $code
}

function Add-Check([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        $checks.Add([pscustomobject]@{ name = $Name; status = "PASS"; detail = "" })
        Write-Host "PASS $Name"
    }
    catch {
        $checks.Add([pscustomobject]@{ name = $Name; status = "FAIL"; detail = $_.Exception.Message })
        Write-Host "FAIL $Name : $($_.Exception.Message)" -ForegroundColor Red
    }
}

$script:exitCode = 0
try {
    $zipInfo = New-PortablePackage -ZipPath $zip -StagePath $stage
    $hash = $zipInfo.Hash.ToLowerInvariant()
    $fileList = @(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
        $_.FullName.Substring($stage.Length).TrimStart('\', '/').Replace('\', '/')
    })

    Add-Check "zip_contains_entry_and_helper" {
        if (-not (Test-Path (Join-Path $stage "AMARE.Windows.exe"))) { throw "AMARE.Windows.exe missing" }
        if (-not (Test-Path (Join-Path $stage "AMARE.Updater.exe"))) { throw "AMARE.Updater.exe missing" }
        if ($BackupExe -and -not (Test-Path (Join-Path $stage "AMARE.Backup.exe"))) { throw "AMARE.Backup.exe missing" }
        if ($fileList -notcontains "amare-update.json") { throw "manifest missing" }
    }

    Add-Check "zip_does_not_contain_user_data" {
        if ($fileList -match '^(profiles|vault)/') { throw "user data path is inside the package" }
        if ($fileList -match '(^|/)settings\.json$') { throw "settings file is inside the package" }
    }

    Add-Check "helper_validates_package" {
        $code = Invoke-Helper @("validate", "--package", $zip, "--version", $Version, "--architecture", "win-x64")
        if ($code -ne 0) { throw "helper validate exited $code" }
    }

    Add-Check "apply_success_preserves_user_data" {
        $fx = New-Fixture (Join-Path $root "success")
        $before = Get-TreeHashes $fx.Data
        $code = Invoke-Helper @("apply", "--package", $zip, "--app-dir", $fx.App, "--data-dir", $fx.Data,
            "--version", $Version, "--sha256", $hash, "--architecture", "win-x64",
            "--entry", "AMARE.Windows.exe", "--no-launch")
        if ($code -ne 0) { throw "helper apply exited $code" }
        $installed = (Get-Content (Join-Path $fx.App "VERSION") -Raw).Trim()
        if ($installed -ne $Version) { throw "installed version is $installed" }
        if (-not (Test-Path (Join-Path $fx.App "AMARE.Updater.exe"))) { throw "helper was not installed" }
        if ((Get-Content (Join-Path $fx.App "AMARE.Windows.exe") -Raw) -eq "old-exe") {
            throw "entry point was not replaced"
        }
        $state = Get-Content (Join-Path $fx.Data "update-state.json") -Raw | ConvertFrom-Json
        if ($state.status -ne "succeeded") { throw "state is $($state.status)" }
        Assert-SameTree $fx.Data $before "apply"
    }

    Add-Check "duplicate_apply_rejected" {
        $fx = New-Fixture (Join-Path $root "duplicate")
        $code = Invoke-Helper @("apply", "--package", $zip, "--app-dir", $fx.App, "--data-dir", $fx.Data,
            "--version", $Version, "--sha256", $hash, "--architecture", "win-x64",
            "--entry", "AMARE.Windows.exe", "--no-launch")
        if ($code -ne 0) { throw "first apply exited $code" }
        $before = Get-TreeHashes $fx.Data
        $code = Invoke-Helper @("apply", "--package", $zip, "--app-dir", $fx.App, "--data-dir", $fx.Data,
            "--version", $Version, "--sha256", $hash, "--architecture", "win-x64",
            "--entry", "AMARE.Windows.exe", "--no-launch")
        if ($code -eq 0) { throw "duplicate apply was accepted" }
        if ((Get-Content (Join-Path $fx.App "VERSION") -Raw).Trim() -ne $Version) {
            throw "duplicate apply changed the version"
        }
        Assert-SameTree $fx.Data $before "duplicate"
    }

    Add-Check "wrong_hash_rejected_without_changes" {
        $fx = New-Fixture (Join-Path $root "hash")
        $appBefore = Get-TreeHashes $fx.App
        $dataBefore = Get-TreeHashes $fx.Data
        $code = Invoke-Helper @("apply", "--package", $zip, "--app-dir", $fx.App, "--data-dir", $fx.Data,
            "--version", $Version, "--sha256", ("0" * 64), "--architecture", "win-x64",
            "--entry", "AMARE.Windows.exe", "--no-launch")
        if ($code -eq 0) { throw "wrong hash was accepted" }
        Assert-SameTree $fx.App $appBefore "wrong hash app"
        Assert-SameTree $fx.Data $dataBefore "wrong hash data"
    }

    Add-Check "locked_app_file_fails_before_apply" {
        $fx = New-Fixture (Join-Path $root "locked")
        $appBefore = Get-TreeHashes $fx.App
        $dataBefore = Get-TreeHashes $fx.Data
        $lock = [System.IO.File]::Open((Join-Path $fx.App "AMARE.Windows.exe"),
            [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        try {
            $code = Invoke-Helper @("apply", "--package", $zip, "--app-dir", $fx.App, "--data-dir", $fx.Data,
                "--version", $Version, "--sha256", $hash, "--architecture", "win-x64",
                "--entry", "AMARE.Windows.exe", "--no-launch")
        }
        finally {
            $lock.Dispose()
        }
        if ($code -eq 0) { throw "locked application file was replaced" }
        Assert-SameTree $fx.App $appBefore "locked app"
        Assert-SameTree $fx.Data $dataBefore "locked data"
    }

    Add-Check "launch_failure_rolls_back" {
        $fx = New-Fixture (Join-Path $root "launch")
        $before = Get-TreeHashes $fx.Data
        $badZip = New-PortablePackage -EntrySource (Join-Path $env:SystemRoot "System32\cmd.exe") `
            -ZipPath (Join-Path $root "bad-launch.zip") -StagePath (Join-Path $root "bad-launch-stage")
        $badHash = (Get-FileHash $badZip.Path -Algorithm SHA256).Hash.ToLowerInvariant()
        try {
            $code = Invoke-Helper @("apply", "--package", $badZip.Path, "--app-dir", $fx.App,
                "--data-dir", $fx.Data, "--version", $Version, "--sha256", $badHash,
                "--architecture", "win-x64", "--entry", "AMARE.Windows.exe", "--health-timeout", "3")
            if ($code -eq 0) { throw "new version without health marker was accepted" }
            if ((Get-Content (Join-Path $fx.App "VERSION") -Raw).Trim() -ne "0.0.1") {
                throw "previous version was not restored"
            }
            if ((Get-Content (Join-Path $fx.App "AMARE.Windows.exe") -Raw).Trim() -ne "old-exe") {
                throw "previous executable was not restored"
            }
            $state = Get-Content (Join-Path $fx.Data "update-state.json") -Raw | ConvertFrom-Json
            if ($state.status -ne "rolled_back") { throw "state after launch failure is $($state.status)" }
            Assert-SameTree $fx.Data $before "launch rollback"
        }
        finally {
            Remove-Item $badZip.Path -Force -ErrorAction SilentlyContinue
        }
    }

    Add-Check "recovery_restores_previous_version" {
        $fx = New-Fixture (Join-Path $root "recover")
        $backup = Join-Path $root "recover-backup"
        New-Item -ItemType Directory -Force -Path $backup | Out-Null
        Copy-Item (Join-Path $fx.App "*") -Destination $backup -Recurse -Force
        $before = Get-TreeHashes $fx.Data
        $code = Invoke-Helper @("apply", "--package", $zip, "--app-dir", $fx.App, "--data-dir", $fx.Data,
            "--version", $Version, "--sha256", $hash, "--architecture", "win-x64",
            "--entry", "AMARE.Windows.exe", "--no-launch")
        if ($code -ne 0) { throw "apply before recovery exited $code" }
        $statePath = Join-Path $fx.Data "update-state.json"
        $state = Get-Content $statePath -Raw | ConvertFrom-Json
        $state.status = "applied"
        $state.previousVersion = "0.0.1"
        $state.backupDir = $backup
        $state.stagingDir = (Join-Path $fx.Data "updates\work\missing-staging")
        $state.recoveryAttempts = 0
        Write-Utf8 $statePath ($state | ConvertTo-Json -Depth 6)
        $code = Invoke-Helper @("recover", "--data-dir", $fx.Data, "--no-launch")
        if ($code -ne 0) { throw "recover exited $code" }
        if ((Get-Content (Join-Path $fx.App "VERSION") -Raw).Trim() -ne "0.0.1") {
            throw "recover did not restore the previous version"
        }
        if ((Get-Content (Join-Path $fx.App "AMARE.Windows.exe") -Raw).Trim() -ne "old-exe") {
            throw "recover did not restore the previous executable"
        }
        Assert-SameTree $fx.Data $before "recover"
    }

    Add-Check "corrupt_backup_is_not_applied" {
        $fx = New-Fixture (Join-Path $root "corrupt")
        $backup = Join-Path $root "corrupt-backup"
        New-Item -ItemType Directory -Force -Path $backup | Out-Null
        Copy-Item (Join-Path $fx.App "*") -Destination $backup -Recurse -Force
        $before = Get-TreeHashes $fx.Data
        $code = Invoke-Helper @("apply", "--package", $zip, "--app-dir", $fx.App, "--data-dir", $fx.Data,
            "--version", $Version, "--sha256", $hash, "--architecture", "win-x64",
            "--entry", "AMARE.Windows.exe", "--no-launch")
        if ($code -ne 0) { throw "apply before corrupt recovery exited $code" }
        Remove-Item (Join-Path $backup "AMARE.Windows.exe") -Force
        $statePath = Join-Path $fx.Data "update-state.json"
        $state = Get-Content $statePath -Raw | ConvertFrom-Json
        $state.status = "applied"
        $state.previousVersion = "0.0.1"
        $state.backupDir = $backup
        $state.stagingDir = (Join-Path $fx.Data "updates\work\missing-staging")
        $state.recoveryAttempts = 0
        Write-Utf8 $statePath ($state | ConvertTo-Json -Depth 6)
        $code = Invoke-Helper @("recover", "--data-dir", $fx.Data, "--no-launch")
        if ($code -eq 0) { throw "corrupt backup was reported as recovered" }
        if ((Get-Content (Join-Path $fx.App "VERSION") -Raw).Trim() -ne $Version) {
            throw "corrupt backup partially changed the installed version"
        }
        if (-not (Test-Path $backup)) { throw "corrupt backup was deleted" }
        Assert-SameTree $fx.Data $before "corrupt recovery"
    }

    Add-Check "recovery_attempt_limit" {
        $fx = New-Fixture (Join-Path $root "limit")
        $backup = Join-Path $root "limit-backup"
        New-Item -ItemType Directory -Force -Path $backup | Out-Null
        Copy-Item (Join-Path $fx.App "*") -Destination $backup -Recurse -Force
        $state = [ordered]@{
            schemaVersion = 1
            status = "failed"
            version = $Version
            previousVersion = "0.0.1"
            packagePath = $zip
            packageSha256 = $hash
            appDir = $fx.App
            dataDir = $fx.Data
            backupDir = $backup
            stagingDir = (Join-Path $fx.Data "updates\work\missing-staging")
            entryPoint = "AMARE.Windows.exe"
            healthMarkerPath = (Join-Path $fx.Data "update-health.json")
            startedUnixMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            updatedUnixMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            error = "simulated"
            recoveryAttempts = 3
        }
        Write-Utf8 (Join-Path $fx.Data "update-state.json") ($state | ConvertTo-Json -Depth 6)
        $code = Invoke-Helper @("recover", "--data-dir", $fx.Data, "--no-launch")
        if ($code -eq 0) { throw "recovery attempt limit did not block" }
        if ((Get-Content (Join-Path $fx.App "VERSION") -Raw).Trim() -ne "0.0.1") {
            throw "capped recovery changed the app"
        }
        if (-not (Test-Path $backup)) { throw "capped recovery deleted the backup" }
    }

    $result = [ordered]@{
        schema_version = 1
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        version = $Version
        package = [ordered]@{
            name = [IO.Path]::GetFileName($zip)
            size = (Get-Item $zip).Length
            sha256 = $hash
            files = $fileList
        }
        checks = $checks
        passed = @($checks | Where-Object { $_.status -eq "PASS" }).Count
        failed = @($checks | Where-Object { $_.status -eq "FAIL" }).Count
        real_windows_11_unverified = "The E2E runs on the CI Windows runner, not a physical Windows 11 device."
        signing = "NONE; SHA-256 is integrity only, not publisher authentication"
    }
    Write-Utf8 $reportPath ($result | ConvertTo-Json -Depth 8)
    Write-Host "Portable updater E2E: $($result.passed) passed, $($result.failed) failed"
    Write-Host "Package: $zip ($($result.package.size) bytes, SHA-256 $hash)"
    Write-Host "Report: $reportPath"
    if ($result.failed -gt 0) { $script:exitCode = 1 }
}
finally {
    if (Test-Path $root) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $script:exitCode
