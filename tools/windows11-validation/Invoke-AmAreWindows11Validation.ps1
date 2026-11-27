param(
    [string]$PackageZip = "",
    [string]$OutputRoot = "",
    [string]$TestRoot = "",
    [switch]$AllowNonWindows11,
    [switch]$RunUiSmoke,
    [switch]$SkipUpdater,
    [switch]$SkipBackup
)

$ErrorActionPreference = "Stop"
$script:RunStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$script:Results = New-Object System.Collections.Generic.List[object]
$script:Failures = New-Object System.Collections.Generic.List[string]

function Write-Result {
    param([string]$Name, [string]$Status, [string]$Detail, [string]$Evidence = "")
    $item = [pscustomobject]@{
        name = $Name
        status = $Status
        detail = $Detail
        evidence = $Evidence
        at = (Get-Date).ToUniversalTime().ToString("o")
    }
    $script:Results.Add($item)
    $color = switch ($Status) {
        "PASS" { "Green" }
        "FAIL" { "Red" }
        "BLOCKED" { "Yellow" }
        default { "Gray" }
    }
    Write-Host ("{0} {1}: {2}" -f $Status, $Name, $Detail) -ForegroundColor $color
    if ($Status -eq "FAIL") { $script:Failures.Add($Name) }
}

function Invoke-Check {
    param([string]$Name, [scriptblock]$Body, [string]$Evidence = "")
    try {
        $detail = & $Body
        if ($null -eq $detail) { $detail = "ok" }
        if ($detail -is [array]) { $detail = ($detail | ForEach-Object { "$_" }) -join " " }
        Write-Result $Name "PASS" ([string]$detail) $Evidence
    }
    catch {
        Write-Result $Name "FAIL" $_.Exception.Message $Evidence
    }
}

function Get-Sha256 {
    param([string]$Path)
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace("-", "").ToLowerInvariant() }
        finally { $sha.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-TreeHashes {
    param([string]$Root)
    $map = @{}
    if (-not (Test-Path -LiteralPath $Root)) { return $map }
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($rootFull.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
        $map[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $map
}

function Get-UserHashes {
    param([string]$Root)
    $map = @{}
    if (-not (Test-Path -LiteralPath $Root)) { return $map }
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($rootFull.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
        if ($relative -in @('update-state.json', 'update.lock', 'update-health.json')) { return }
        if ($relative.StartsWith('updates/') -or $relative.StartsWith('logs/updater.') -or $relative.StartsWith('updater/')) { return }
        $map[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $map
}

function Compare-TreeHashes {
    param($Expected, $Actual, [string]$Stage)
    if ($Expected.Count -ne $Actual.Count) {
        throw "$Stage changed file count: $($Expected.Count) -> $($Actual.Count)"
    }
    foreach ($key in $Expected.Keys) {
        if (-not $Actual.ContainsKey($key)) { throw "$Stage removed $key" }
        if ($Actual[$key] -ne $Expected[$key]) { throw "$Stage changed $key" }
    }
}

function Quote-Arg {
    param([string]$Value)
    return '"' + ($Value -replace '"', '\"') + '"'
}

function Invoke-External {
    param(
        [string]$Exe,
        [string[]]$Arguments,
        [int]$TimeoutSeconds = 180,
        [string]$StandardInput = ""
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.WorkingDirectory = Split-Path -Parent $Exe
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.Arguments = (($Arguments | ForEach-Object { Quote-Arg $_ }) -join " ")
    $process = [System.Diagnostics.Process]::Start($psi)
    if ($StandardInput.Length -gt 0) { $process.StandardInput.WriteLine($StandardInput) }
    $process.StandardInput.Close()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill() } catch { }
        throw "process timed out after $TimeoutSeconds seconds"
    }
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        StdOut = $stdout
        StdErr = $stderr
    }
}

function Start-TestApp {
    param([string]$Exe, [string]$DataRoot, [int]$WaitSeconds = 20)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.WorkingDirectory = Split-Path -Parent $Exe
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables["AMARE_DATA_ROOT"] = $DataRoot
    $process = [System.Diagnostics.Process]::Start($psi)
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    $windowSeen = $false
    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { break }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { $windowSeen = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 3
    return [pscustomobject]@{ Process = $process; WindowSeen = $windowSeen }
}

function Stop-TestApp {
    param($Process)
    if ($null -eq $Process) { return }
    try {
        $Process.Refresh()
        if (-not $Process.HasExited) {
            $Process.CloseMainWindow() | Out-Null
            if (-not $Process.WaitForExit(10000)) {
                Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }
    catch {
        try { Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
}

function Save-Screenshot {
    param([string]$Path)
    try {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bitmap = New-Object System.Drawing.Bitmap -ArgumentList $bounds.Width, $bounds.Height
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose()
        $bitmap.Dispose()
        return $Path
    }
    catch {
        return ""
    }
}

function New-UpdaterFixture {
    param([string]$Root)
    $app = Join-Path $Root "app"
    $data = Join-Path $Root "data"
    New-Item -ItemType Directory -Force -Path $app, $data | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $app "VERSION"), "0.0.1`n")
    [System.IO.File]::WriteAllText((Join-Path $app "AMARE.Windows.exe"), "old-exe")
    [System.IO.File]::WriteAllText((Join-Path $app "old-only.txt"), "old")
    foreach ($pair in @{
        "profiles/default/amare.db" = "profile"
        "vault/default.key" = "dpapi-key"
        "settings.json" = '{"theme":"dark"}'
        "bookmarks.db" = "bookmarks"
        "history.db" = "history"
    }.GetEnumerator()) {
        $path = Join-Path $data ($pair.Key.Replace('/', [IO.Path]::DirectorySeparatorChar))
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
        [System.IO.File]::WriteAllText($path, $pair.Value)
    }
    return [pscustomobject]@{ App = $app; Data = $data }
}

function New-BadUpdatePackage {
    param([string]$Source, [string]$Destination, [string]$Replacement)
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    $zip = [System.IO.Compression.ZipFile]::Open($Destination, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $zip.GetEntry("AMARE.Windows.exe")
        if ($null -eq $entry) { throw "AMARE.Windows.exe entry missing" }
        $entry.Delete()
        $newEntry = $zip.CreateEntry("AMARE.Windows.exe")
        $stream = $newEntry.Open()
        try {
            $bytes = [System.IO.File]::ReadAllBytes($Replacement)
            $stream.Write($bytes, 0, $bytes.Length)
        }
        finally { $stream.Dispose() }
    }
    finally { $zip.Dispose() }
    return Get-Sha256 $Destination
}

function Invoke-Helper {
    param([string]$Helper, [string[]]$Arguments, [int]$TimeoutSeconds = 180, [string]$StandardInput = "")
    return Invoke-External -Exe $Helper -Arguments $Arguments -TimeoutSeconds $TimeoutSeconds -StandardInput $StandardInput
}

# ---------- initialize evidence ----------
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "This validation package must be run on Windows 11."
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\windows11-validation"
}
if ([string]::IsNullOrWhiteSpace($TestRoot)) {
    $TestRoot = Join-Path $env:TEMP ("AMARE-Win11-Validation-" + [Guid]::NewGuid().ToString("N"))
}
$runDir = Join-Path $OutputRoot ("run-" + $script:RunStamp)
$screenshots = Join-Path $runDir "screenshots"
$logsDir = Join-Path $runDir "logs"
$hostsDir = Join-Path $runDir "hosts"
New-Item -ItemType Directory -Force -Path $runDir, $screenshots, $logsDir, $hostsDir | Out-Null
New-Item -ItemType Directory -Force -Path $TestRoot | Out-Null

if ([string]::IsNullOrWhiteSpace($PackageZip)) {
    $candidates = @(
        (Join-Path $PSScriptRoot "AMARE-0.92.0-win-x64-unsigned-validation.zip"),
        (Join-Path $PSScriptRoot "AMARE-0.92.0-win-x64-unsigned.zip"),
        (Join-Path $repoRoot "artifacts\phase3-personal-package\AMARE-0.92.0-win-x64-unsigned-validation.zip"),
        (Join-Path $repoRoot "artifacts\phase3-personal-package\AMARE-0.92.0-win-x64-unsigned.zip")
    )
    $PackageZip = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
$packageAvailable = -not [string]::IsNullOrWhiteSpace($PackageZip) -and (Test-Path -LiteralPath $PackageZip)

# ---------- environment ----------
$os = Get-CimInstance Win32_OperatingSystem
$build = [int]$os.BuildNumber
$isWindows11 = $build -ge 22000
$screen = $null
$displayCount = 0
try {
    Add-Type -AssemblyName System.Windows.Forms
    $screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $displayCount = [System.Windows.Forms.Screen]::AllScreens.Count
} catch { }
$webview2 = ""
$webviewCandidates = @()
if (${env:ProgramFiles(x86)}) { $webviewCandidates += (Join-Path ${env:ProgramFiles(x86)} "Microsoft\EdgeWebView\Application") }
if ($env:ProgramFiles) { $webviewCandidates += (Join-Path $env:ProgramFiles "Microsoft\EdgeWebView\Application") }
foreach ($candidate in $webviewCandidates) {
    if (Test-Path -LiteralPath $candidate) {
        $found = Get-ChildItem -LiteralPath $candidate -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending | Select-Object -First 1
        if ($found) { $webview2 = $found.Name; break }
    }
}
$dotnet8 = $false
$dotnetRoot = Join-Path $env:ProgramFiles "dotnet\shared\Microsoft.NETCore.App"
if (Test-Path -LiteralPath $dotnetRoot) {
    $dotnet8 = (@(Get-ChildItem -LiteralPath $dotnetRoot -Directory |
        Where-Object { $_.Name -match '^8\.' }).Count -gt 0)
}
$isAdmin = $false
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
} catch { }
$environment = [ordered]@{
    generated_at = (Get-Date).ToUniversalTime().ToString("o")
    os = $os.Caption
    version = $os.Version
    build = $build
    windows_11 = $isWindows11
    architecture = $env:PROCESSOR_ARCHITECTURE
    powershell = $PSVersionTable.PSVersion.ToString()
    webview2 = $webview2
    dotnet_8 = $dotnet8
    admin = $isAdmin
    displays = $displayCount
    virtual_screen = if ($screen) { [ordered]@{ x = $screen.X; y = $screen.Y; width = $screen.Width; height = $screen.Height } } else { $null }
    test_root = $TestRoot
    package = $PackageZip
}
$environment | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDir "environment.json") -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "manual-checklist.md") -Destination (Join-Path $runDir "manual-checklist.md") -Force
[System.IO.File]::WriteAllText(
    (Join-Path $hostsDir "README.txt"),
    "Test data root: $TestRoot`r`nThis directory contains only validation fixtures. It is not the user's AMARE profile.`r`nDelete it after evidence is collected.`r`n")

Write-Result "os_windows11" $(if ($isWindows11) { "PASS" } elseif ($AllowNonWindows11) { "UNVERIFIED" } else { "BLOCKED" }) "build=$build"
Write-Result "arch_x64" $(if ($env:PROCESSOR_ARCHITECTURE -match 'AMD64|x86_64') { "PASS" } else { "BLOCKED" }) $env:PROCESSOR_ARCHITECTURE
Write-Result "webview2_runtime" $(if ($webview2) { "PASS" } else { "BLOCKED" }) $(if ($webview2) { $webview2 } else { "not found" })
Write-Result "dotnet_8_runtime" $(if ($dotnet8) { "PASS" } else { "UNVERIFIED" }) $(if ($dotnet8) { "Microsoft.NETCore.App 8.x found" } else { "not found; self-contained ZIP may not require it" })
Write-Result "package_present" $(if ($packageAvailable) { "PASS" } else { "BLOCKED" }) $(if ($packageAvailable) { $PackageZip } else { "No AMARE validation ZIP found. Pass -PackageZip." })
Write-Result "admin_not_required" "PASS" "Validation runs unelevated; admin=$isAdmin"

if (-not $isWindows11 -and -not $AllowNonWindows11) {
    $script:Results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDir "results.json") -Encoding UTF8
    Write-Host "BLOCKED: Windows 11 build 22000+ is required. Use -AllowNonWindows11 only for Windows Server lab runs." -ForegroundColor Yellow
    exit 2
}
if (-not $packageAvailable) {
    $script:Results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDir "results.json") -Encoding UTF8
    exit 2
}

# ---------- package checks ----------
$packageHash = Get-Sha256 $PackageZip
$packageInfo = $null
Invoke-Check "package_sha256" {
    $sidecar = "$PackageZip.sha256"
    if (Test-Path -LiteralPath $sidecar) {
        $expected = ((Get-Content -LiteralPath $sidecar -Raw).Trim() -split '\s+')[0]
        if ($expected -ne $packageHash) { throw "sidecar SHA-256 mismatch" }
        "sha256=$packageHash (sidecar matched)"
    } else {
        "sha256=$packageHash (no sidecar; recorded only)"
    }
}
Invoke-Check "package_required_files" {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($PackageZip)
    try {
        $names = @($zip.Entries | ForEach-Object { $_.FullName })
        foreach ($required in @("AMARE.Windows.exe", "AMARE.Updater.exe", "AMARE.Backup.exe", "VERSION", "amare-update.json")) {
            if ($names -notcontains $required) { throw "missing $required" }
        }
        "entry=$($names -contains 'AMARE.Windows.exe') updater=$($names -contains 'AMARE.Updater.exe') backup=$($names -contains 'AMARE.Backup.exe')"
    }
    finally { $zip.Dispose() }
}
Invoke-Check "package_forbidden_user_data" {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($PackageZip)
    try {
        $forbidden = @($zip.Entries | Where-Object {
            $_.FullName -match '(?i)^(profiles|vault)/|\.amarebak$|\.key$|\.pdb$|(^|/)(ai-api-settings|downloads|app-preferences|update-preferences)\.json$'
        } | ForEach-Object { $_.FullName })
        if ($forbidden.Count -gt 0) { throw "forbidden entries: $($forbidden -join ', ')" }
        "no profile/vault/key/backup/PDB entries"
    }
    finally { $zip.Dispose() }
}
Invoke-Check "package_manifest" {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($PackageZip)
    try {
        $entry = $zip.GetEntry("amare-update.json")
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($manifest.schema -ne 1) { throw "unsupported schema $($manifest.schema)" }
        if ($manifest.product -ne "AMARE") { throw "wrong product" }
        if ($manifest.architecture -ne "win-x64") { throw "wrong architecture" }
        foreach ($required in $manifest.requiredFiles) {
            if (-not $zip.GetEntry($required)) { throw "manifest required file missing: $required" }
        }
        $script:PackageChannel = $manifest.channel
        $script:IsSelfContained = ($manifest.runtime -match 'self-contained') -or ($zip.GetEntry('hostfxr.dll') -and $zip.GetEntry('coreclr.dll'))
        $script:PackageInfo = [ordered]@{
            name = (Split-Path $PackageZip -Leaf)
            sha256 = $packageHash
            size_bytes = (Get-Item -LiteralPath $PackageZip).Length
            file_count = $zip.Entries.Count
            version = $manifest.version
            entry_point = $manifest.entryPoint
            signing = "UNSIGNED"
        }
        $script:PackageInfo | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runDir "package.json") -Encoding UTF8
        $script:PackageVersion = $manifest.version
        "version=$($manifest.version) architecture=$($manifest.architecture) files=$($zip.Entries.Count) channel=$($manifest.channel)"
    }
    finally { $zip.Dispose() }
}
Invoke-Check "package_type" {
    if ($script:IsSelfContained) { "self-contained" } else { "framework-dependent" }
}
Invoke-Check "package_runtime_prerequisite" {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($PackageZip)
    try {
        if ($script:IsSelfContained) {
            foreach ($required in @("hostfxr.dll", "hostpolicy.dll", "coreclr.dll")) {
                if (-not $zip.GetEntry($required)) { throw "self-contained runtime file missing: $required" }
            }
            "self-contained .NET runtime files present; .NET 8 system runtime not required"
        } else {
            $entry = $zip.GetEntry("AMARE.Windows.runtimeconfig.json")
            $reader = New-Object System.IO.StreamReader($entry.Open())
            try { $runtime = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            $framework = $runtime.runtimeOptions.framework
            if ($framework -and $framework.name -eq "Microsoft.NETCore.App" -and -not $dotnet8) {
                $script:RuntimeBlocked = $true
                throw "package is framework-dependent on Microsoft.NETCore.App $($framework.version); install .NET 8 or use the self-contained ZIP"
            }
            "framework=$($framework.name) $($framework.version); .NET 8 detected=$dotnet8"
        }
    }
    finally { $zip.Dispose() }
}

if ($script:RuntimeBlocked) {
    $script:Results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDir "results.json") -Encoding UTF8
    Write-Host "BLOCKED: framework-dependent package needs .NET 8. Use the self-contained ZIP for a clean Windows 11 PC." -ForegroundColor Yellow
    exit 2
}
if (-not $webview2) {
    $script:Results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDir "results.json") -Encoding UTF8
    Write-Host "BLOCKED: WebView2 Evergreen Runtime is required; install it and rerun." -ForegroundColor Yellow
    exit 2
}

Invoke-Check "package_isolation_marker" {
    if ($script:PackageChannel -ne "portable-unsigned-validation") {
        throw "package channel '$($script:PackageChannel)' does not include the AMARE_DATA_ROOT validation isolation marker; refusing destructive tests"
    }
    "channel=$($script:PackageChannel); AMARE_DATA_ROOT isolation required"
}
if ($script:PackageChannel -ne "portable-unsigned-validation") {
    $script:Results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDir "results.json") -Encoding UTF8
    Write-Host "BLOCKED: package lacks the validation isolation marker. Rebuild with tools/verify-windows.ps1 personal_package." -ForegroundColor Yellow
    exit 2
}

$extractRoot = Join-Path $TestRoot "app"
Expand-Archive -LiteralPath $PackageZip -DestinationPath $extractRoot -Force
$appExe = Join-Path $extractRoot "AMARE.Windows.exe"
$updaterExe = Join-Path $extractRoot "AMARE.Updater.exe"
$backupExe = Join-Path $extractRoot "AMARE.Backup.exe"
$appData = Join-Path $TestRoot "appdata"
$appRootForBackup = $appData
$dataRoot = Join-Path $appData "data"
$appProcess = $null

# ---------- app launch / close ----------
Invoke-Check "app_launch_isolated" {
    $started = Start-TestApp -Exe $appExe -DataRoot $appData -WaitSeconds 25
    $script:AppProcess = $started.Process
    if ($started.Process.HasExited) {
        throw "AMARE.Windows.exe exited during startup (exit $($started.Process.ExitCode))"
    }
    $shot = Save-Screenshot (Join-Path $screenshots "app-launch.png")
    $detail = "process alive; window=$($started.WindowSeen); data_root=$appData"
    if ($shot) { $detail += "; screenshot=screenshots/app-launch.png" }
    $detail
}
Stop-TestApp $script:AppProcess
$script:AppProcess = $null
Invoke-Check "app_data_root_isolated" {
    if (-not (Test-Path -LiteralPath (Join-Path $dataRoot "amare.db"))) {
        throw "test data root was not created under AMARE_DATA_ROOT"
    }
    if (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA "AMARE\data")) {
        "test root created; user LOCALAPPDATA\AMARE was not used by this check"
    } else {
        "test root created"
    }
}

if (-not $SkipBackup) {
    Invoke-Check "backup_create_verify_full" {
        $backup = Join-Path $TestRoot "backup\full.amarebak"
        New-Item -ItemType Directory -Force -Path (Split-Path $backup -Parent) | Out-Null
        $result = Join-Path $TestRoot "backup\create.json"
        $r = Invoke-Helper $backupExe @(
            "create", "--root", $dataRoot, "--out", $backup, "--include-root",
            "--app-root", $appRootForBackup, "--result", $result
        )
        if ($r.ExitCode -ne 0) { throw "create failed: $($r.StdErr)" }
        $verifyResult = Join-Path $TestRoot "backup\verify.json"
        $v = Invoke-Helper $backupExe @(
            "verify", "--root", $dataRoot, "--backup", $backup, "--result", $verifyResult
        )
        if ($v.ExitCode -ne 0) { throw "verify failed: $($v.StdErr)" }
        $json = Get-Content -LiteralPath $verifyResult -Raw | ConvertFrom-Json
        if ($json.status -notin @("Valid", "ValidWithWarnings")) { throw "verify status $($json.status)" }
        $script:FullBackupPath = $backup
        $script:FullBackupManifest = $json.manifest
        "status=$($json.status); profiles=$($json.manifest.profiles.Count)"
    }
    Invoke-Check "backup_restore_full_isolated" {
        if (-not $script:FullBackupPath) { throw "full backup missing" }
        $targetRoot = Join-Path $TestRoot "full-restore\data"
        $restoreResult = Join-Path $TestRoot "full-restore\restore.json"
        New-Item -ItemType Directory -Force -Path (Split-Path $restoreResult -Parent) | Out-Null
        $r = Invoke-Helper $backupExe @(
            "restore", "--root", $targetRoot, "--backup", $script:FullBackupPath,
            "--mode", "full", "--no-relaunch", "--result", $restoreResult
        )
        if ($r.ExitCode -ne 0) { throw "full restore failed: $($r.StdErr)" }
        if (-not (Test-Path -LiteralPath (Join-Path $targetRoot "amare.db"))) {
            throw "restored root DB missing"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $targetRoot "profiles"))) {
            throw "restored profiles directory missing"
        }
        "full restore wrote root DB and profiles to isolated target"
    }
    Invoke-Check "backup_restore_single_profile" {
        if (-not $script:FullBackupManifest) { throw "full manifest missing" }
        $sourceProfile = $script:FullBackupManifest.profiles[0].id
        $single = Join-Path $TestRoot "backup\single.amarebak"
        $r = Invoke-Helper $backupExe @(
            "create", "--root", $dataRoot, "--out", $single, "--profiles", $sourceProfile,
            "--app-root", $appRootForBackup, "--result", (Join-Path $TestRoot "backup\single-create.json")
        )
        if ($r.ExitCode -ne 0) { throw "single backup failed: $($r.StdErr)" }
        # initialize a separate target root with the app
        $targetAppData = Join-Path $TestRoot "partial-appdata"
        $started = Start-TestApp -Exe $appExe -DataRoot $targetAppData -WaitSeconds 25
        Stop-TestApp $started.Process
        $targetData = Join-Path $targetAppData "data"
        $restoreResult = Join-Path $TestRoot "partial-restore\restore.json"
        New-Item -ItemType Directory -Force -Path (Split-Path $restoreResult -Parent) | Out-Null
        $r = Invoke-Helper $backupExe @(
            "restore", "--root", $targetData, "--backup", $single, "--mode", "profile",
            "--map", "$($sourceProfile):default", "--no-relaunch", "--result", $restoreResult
        )
        if ($r.ExitCode -ne 0) { throw "partial restore failed: $($r.StdErr)" }
        if (-not (Test-Path -LiteralPath (Join-Path $targetData "profiles\default\amare.db"))) {
            throw "partial restore target DB missing"
        }
        "source=$sourceProfile target=default"
    }
    Invoke-Check "backup_wrong_password_rejected" {
        $encrypted = Join-Path $TestRoot "backup\encrypted.amarebak"
        $r = Invoke-Helper $backupExe @(
            "create", "--root", $dataRoot, "--out", $encrypted,
            "--profiles", $script:FullBackupManifest.profiles[0].id,
            "--app-root", $appRootForBackup, "--password-stdin",
            "--result", (Join-Path $TestRoot "backup\encrypted-create.json")
        ) -StandardInput "validation-pass"
        if ($r.ExitCode -ne 0) { throw "encrypted create failed: $($r.StdErr)" }
        $verify = Join-Path $TestRoot "backup\encrypted-verify.json"
        $r = Invoke-Helper $backupExe @(
            "verify", "--root", $dataRoot, "--backup", $encrypted,
            "--password-stdin", "--result", $verify
        ) -StandardInput "wrong-pass"
        if ($r.ExitCode -ne 0) { throw "verify command failed: $($r.StdErr)" }
        $json = Get-Content -LiteralPath $verify -Raw | ConvertFrom-Json
        if ($json.status -ne "InvalidPassword") { throw "expected InvalidPassword, got $($json.status)" }
        "wrong password rejected"
    }
    Write-Result "backup_vault_policy" "UNVERIFIED" "Vault-included backup and cross-PC Vault restore require manual test with a disposable test Vault."
}

if (-not $SkipUpdater) {
    Invoke-Check "updater_apply_preserves_data" {
        $fixture = New-UpdaterFixture (Join-Path $TestRoot "updater-success")
        $before = Get-UserHashes $fixture.Data
        $version = if ($script:PackageVersion) { $script:PackageVersion } else { "0.92.0" }
        $r = Invoke-Helper $updaterExe @(
            "apply", "--package", $PackageZip, "--app-dir", $fixture.App,
            "--data-dir", $fixture.Data, "--version", $version, "--sha256", $packageHash,
            "--architecture", "win-x64", "--entry", "AMARE.Windows.exe", "--no-launch"
        )
        if ($r.ExitCode -ne 0) { throw "updater failed: $($r.StdErr)" }
        if ((Get-Content -LiteralPath (Join-Path $fixture.App "VERSION") -Raw).Trim() -ne $version) {
            throw "installed VERSION mismatch"
        }
        Compare-TreeHashes $before (Get-UserHashes $fixture.Data) "updater apply"
        $state = Get-Content -LiteralPath (Join-Path $fixture.Data "update-state.json") -Raw | ConvertFrom-Json
        if ($state.status -ne "succeeded") { throw "update state $($state.status)" }
        "version=$version; state=succeeded"
    }
    Invoke-Check "updater_wrong_hash_rejected" {
        $fixture = New-UpdaterFixture (Join-Path $TestRoot "updater-hash")
        $before = Get-UserHashes $fixture.Data
        $r = Invoke-Helper $updaterExe @(
            "apply", "--package", $PackageZip, "--app-dir", $fixture.App,
            "--data-dir", $fixture.Data, "--version", $version,
            "--sha256", ("0" * 64), "--architecture", "win-x64",
            "--entry", "AMARE.Windows.exe", "--no-launch"
        )
        if ($r.ExitCode -eq 0) { throw "wrong hash was accepted" }
        if ((Get-Content -LiteralPath (Join-Path $fixture.App "VERSION") -Raw).Trim() -ne "0.0.1") {
            throw "wrong hash changed the app"
        }
        Compare-TreeHashes $before (Get-UserHashes $fixture.Data) "wrong hash"
        "wrong hash rejected before apply"
    }
    Invoke-Check "updater_locked_file_rejected" {
        $fixture = New-UpdaterFixture (Join-Path $TestRoot "updater-locked")
        $before = Get-UserHashes $fixture.Data
        $lock = [System.IO.File]::Open((Join-Path $fixture.App "AMARE.Windows.exe"),
            [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        try {
            $r = Invoke-Helper $updaterExe @(
                "apply", "--package", $PackageZip, "--app-dir", $fixture.App,
                "--data-dir", $fixture.Data, "--version", $version,
                "--sha256", $packageHash, "--architecture", "win-x64",
                "--entry", "AMARE.Windows.exe", "--no-launch"
            )
        }
        finally { $lock.Dispose() }
        if ($r.ExitCode -eq 0) { throw "locked app file was replaced" }
        if ((Get-Content -LiteralPath (Join-Path $fixture.App "AMARE.Windows.exe") -Raw).Trim() -ne "old-exe") {
            throw "locked fixture changed"
        }
        Compare-TreeHashes $before (Get-UserHashes $fixture.Data) "locked file"
        "locked file rejected before apply"
    }
    Invoke-Check "updater_launch_failure_rollback" {
        $bad = Join-Path $TestRoot "updater-bad\bad.zip"
        New-Item -ItemType Directory -Force -Path (Split-Path $bad -Parent) | Out-Null
        $badHash = New-BadUpdatePackage $PackageZip $bad (Join-Path $env:SystemRoot "System32\cmd.exe")
        $fixture = New-UpdaterFixture (Join-Path $TestRoot "updater-rollback")
        $before = Get-UserHashes $fixture.Data
        $r = Invoke-Helper $updaterExe @(
            "apply", "--package", $bad, "--app-dir", $fixture.App,
            "--data-dir", $fixture.Data, "--version", $version,
            "--sha256", $badHash, "--architecture", "win-x64",
            "--entry", "AMARE.Windows.exe", "--health-timeout", "3"
        )
        if ($r.ExitCode -eq 0) { throw "launch failure was accepted" }
        if ((Get-Content -LiteralPath (Join-Path $fixture.App "VERSION") -Raw).Trim() -ne "0.0.1") {
            throw "previous version was not restored"
        }
        Compare-TreeHashes $before (Get-UserHashes $fixture.Data) "launch rollback"
        $state = Get-Content -LiteralPath (Join-Path $fixture.Data "update-state.json") -Raw | ConvertFrom-Json
        if ($state.status -ne "rolled_back") { throw "state $($state.status)" }
        "launch failure rolled back"
    }
    Invoke-Check "updater_concurrent_rejected" {
        $fixture = New-UpdaterFixture (Join-Path $TestRoot "updater-lock")
        $lock = [System.IO.File]::Open((Join-Path $fixture.Data "update.lock"),
            [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        try {
            $r = Invoke-Helper $updaterExe @(
                "apply", "--package", $PackageZip, "--app-dir", $fixture.App,
                "--data-dir", $fixture.Data, "--version", $version,
                "--sha256", $packageHash, "--architecture", "win-x64",
                "--entry", "AMARE.Windows.exe", "--no-launch"
            )
        }
        finally { $lock.Dispose() }
        if ($r.ExitCode -eq 0) { throw "concurrent helper was accepted" }
        "concurrent update rejected"
    }
}

if ($RunUiSmoke) {
    Invoke-Check "ui_smoke" {
        $project = Join-Path $repoRoot "tools\windows-ui-smoke\WindowsUiSmoke.csproj"
        $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $dotnet -or -not (Test-Path -LiteralPath $project)) {
            throw "UI smoke requires the repository and .NET SDK."
        }
        $env:AMARE_DATA_ROOT = $appData
        $uiResults = Join-Path $runDir "winui_ui_smoke_results.json"
        $env:AMARE_UI_SMOKE_RESULTS = $uiResults
        try {
            $r = Invoke-External -Exe $dotnet.Source -Arguments @(
                "run", "--project", $project, "-c", "Release", "--", $appExe
            ) -TimeoutSeconds 1800
        }
        finally { Remove-Item Env:\AMARE_DATA_ROOT -ErrorAction SilentlyContinue }
        if ($r.ExitCode -ne 0) { throw "UI smoke exit $($r.ExitCode)" }
        "UI smoke completed; results=$uiResults"
    }
}

# ---------- evidence ----------
try {
    foreach ($path in @((Join-Path $appData "logs"), (Join-Path $appData "data\logs"))) {
        if (Test-Path -LiteralPath $path) {
            $leaf = if ($path -like "*data\logs") { "native-logs" } else { "app-logs" }
            Copy-Item -LiteralPath $path -Destination (Join-Path $logsDir $leaf) -Recurse -Force
        }
    }
} catch { }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "README.md") -Destination (Join-Path $runDir "README.md") -Force
$summary = [ordered]@{
    run = $runDir
    package = $PackageZip
    package_sha256 = $packageHash
    passed = @($script:Results | Where-Object { $_.status -eq "PASS" }).Count
    failed = @($script:Results | Where-Object { $_.status -eq "FAIL" }).Count
    unverified = @($script:Results | Where-Object { $_.status -eq "UNVERIFIED" }).Count
    blocked = @($script:Results | Where-Object { $_.status -eq "BLOCKED" }).Count
    skipped = @($script:Results | Where-Object { $_.status -eq "SKIPPED" }).Count
    results = $script:Results
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runDir "results.json") -Encoding UTF8
Write-Host ""
Write-Host ("Evidence: {0}" -f $runDir) -ForegroundColor Cyan
Write-Host ("PASS={0} FAIL={1} UNVERIFIED={2} BLOCKED={3}" -f $summary.passed, $summary.failed, $summary.unverified, $summary.blocked)
if ($summary.failed -gt 0) { exit 1 }
if ($summary.blocked -gt 0) { exit 2 }
exit 0
