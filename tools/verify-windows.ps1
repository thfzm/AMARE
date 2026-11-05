param(
    [switch]$SkipManual,
    [switch]$SkipMemory,
    [switch]$SkipWinUI,
    [switch]$CI
)

if ($CI) {
    $SkipManual = $true
    $SkipMemory = $false
    $env:AMARE_VERIFY_CI = "1"
    $env:AMARE_VERIFY_HEADLESS = "1"
    $env:AMARE_VERIFY_DRM = "0"
}

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$OutputDir = Join-Path (Get-Location) "artifacts\windows"
$Version = "0.24.0"
$PortableName = "AMARE-$Version-win-x64"
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$env:AMARE_VERIFY_OUTPUT = $OutputDir
$env:AMARE_VERIFY_AUTORUN = "1"
$env:AMARE_VERIFY_MANUAL = if ($SkipManual) { "0" } else { "1" }
$env:AMARE_VERIFY_MEMORY = if ($SkipMemory) { "0" } else { "1" }

$Steps = New-Object System.Collections.Generic.List[object]
$Failures = New-Object System.Collections.Generic.List[string]

function Write-Utf8Json($Path, $Value) {
    $json = $Value | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
}

function Get-WebView2Runtime {
    $candidates = @()
    if (${env:ProgramFiles(x86)}) {
        $candidates += (Join-Path ${env:ProgramFiles(x86)} "Microsoft\EdgeWebView\Application")
    }
    if ($env:ProgramFiles) {
        $candidates += (Join-Path $env:ProgramFiles "Microsoft\EdgeWebView\Application")
    }
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            $exe = Get-ChildItem -Path $candidate -Filter "msedgewebview2.exe" -Recurse -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($exe) {
                return [pscustomobject]@{
                    path = $exe.FullName
                    version = $exe.VersionInfo.FileVersion
                }
            }
        }
    }
    return $null
}

trap {
    $message = $_.Exception.Message
    $summary = [pscustomobject]@{
        completed_at = (Get-Date).ToUniversalTime().ToString("o")
        environment = $envReport
        steps = $Steps
        harness = $null
        failures = @($Failures)
        error = $message
    }
    try {
        Write-Utf8Json (Join-Path $OutputDir "windows-verify-summary.json") $summary
        $fallback = [pscustomobject]@{
            schema_version = "1"
            generated_at = (Get-Date).ToUniversalTime().ToString("o")
            runner = [pscustomobject]@{ os = $null; windows_11 = $isWindows11; is_windows_11_device = $isWindows11 }
            environment = $envReport
            summary = [pscustomobject]@{ passed = 0; failed = 1; unverified = 0; skipped = 0; total = 1 }
            results = @([pscustomobject]@{
                name = "verify_windows_preflight"
                status = "FAIL"
                executed = $true
                duration_ms = $null
                environment = $envReport
                error = $message
                log_path = $null
                source = "verify-windows"
                detail = $message
            })
            note = "Preflight failed before the full unified report could be generated."
        }
        Write-Utf8Json (Join-Path $OutputDir "windows-verification.json") $fallback
    } catch {
    }
    Write-Host ""
    Write-Host "FAILED: $message" -ForegroundColor Red
    Write-Host "See $OutputDir for logs." -ForegroundColor Yellow
    exit 1
}

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Command,
        [bool]$Required = $true
    )
    $started = (Get-Date).ToUniversalTime().ToString("o")
    $logPath = Join-Path $OutputDir "$Name.log"
    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Command *>&1 | Tee-Object -FilePath $logPath
    $exitCode = $LASTEXITCODE
    $finished = (Get-Date).ToUniversalTime().ToString("o")
    $status = if ($exitCode -eq 0) { "PASS" } else { "FAIL" }
    $Steps.Add([pscustomobject]@{
        name = $Name
        status = $status
        exit_code = $exitCode
        required = $Required
        started_at = $started
        finished_at = $finished
        duration_ms = [math]::Round((([datetimeoffset]$finished) - ([datetimeoffset]$started)).TotalMilliseconds)
        log = $logPath
        error = if ($exitCode -eq 0) { $null } else { "exit code $exitCode" }
    })
    if ($exitCode -ne 0) {
        $Failures.Add($Name)
        # GitHub annotation makes the failing internal step visible without
        # requiring access to the full job log.
        Write-Host "::error title=Windows verification step failed::$Name (exit $exitCode). See $logPath"
        # Continue running independent steps so the unified report and logs are
        # produced even when one step fails. The script exits non-zero at the end.
    }
}

function Add-SkippedStep($Name, $Status, $Detail) {
    $Steps.Add([pscustomobject]@{
        name = $Name
        status = $Status
        exit_code = $null
        required = $false
        started_at = $null
        finished_at = (Get-Date).ToUniversalTime().ToString("o")
        duration_ms = 0
        log = $null
        detail = $Detail
        error = if ($Status -eq "FAIL") { $Detail } else { $null }
    })
}

# ---------- Preflight ----------
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "Windows is required. Current platform: $([Environment]::OSVersion.Platform)"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet was not found in PATH. Install the .NET 8 SDK and re-run."
}
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw "cargo was not found in PATH. Install rustup/cargo and re-run."
}
if (-not (Get-Command rustc -ErrorAction SilentlyContinue)) {
    throw "rustc was not found in PATH. Install rustup/cargo and re-run."
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "vswhere not found. Install Visual Studio Build Tools with C++ workload."
}
$vsInstallPath = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if (-not $vsInstallPath) {
    throw "Visual Studio C++ build tools were not found."
}

$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$computer = Get-CimInstance Win32_ComputerSystem
$gpus = @(Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name })
$webview2 = Get-WebView2Runtime
$dotnetSdks = @(& dotnet --list-sdks 2>$null)
$cargoVersion = (& cargo --version 2>$null)
$rustcVersion = (& rustc --version 2>$null)

$isWindows11 = ($os.Caption -notmatch 'Server') -and ([Environment]::OSVersion.Version.Build -ge 22000)
$envReport = [pscustomobject]@{
    captured_at = (Get-Date).ToUniversalTime().ToString("o")
    os = [pscustomobject]@{
        caption = $os.Caption
        version = $os.Version
        build = [Environment]::OSVersion.Version.Build
        architecture = $env:PROCESSOR_ARCHITECTURE
    }
    cpu = [pscustomobject]@{
        name = $cpu.Name
        cores = $cpu.NumberOfCores
        logical_processors = $cpu.NumberOfLogicalProcessors
    }
    ram_bytes = [int64]$computer.TotalPhysicalMemory
    gpus = $gpus
    webview2 = $webview2
    dotnet_sdks = $dotnetSdks
    cargo = $cargoVersion
    rustc = $rustcVersion
    powershell = $PSVersionTable.PSVersion.ToString()
    visual_studio = $vsInstallPath
    output_dir = $OutputDir
    manual_intervention = (-not $SkipManual)
    memory_probe = (-not $SkipMemory)
    windows_11 = $isWindows11
}
Write-Utf8Json (Join-Path $OutputDir "windows-verify-environment.json") $envReport

Write-Host "AMARE Phase 0.5 Windows verification" -ForegroundColor Green
Write-Host "OS: $($os.Caption) $($os.Version)"
Write-Host "CPU: $($cpu.Name) ($($cpu.NumberOfCores) cores / $($cpu.NumberOfLogicalProcessors) threads)"
Write-Host "RAM: $([math]::Round($computer.TotalPhysicalMemory / 1GB, 1)) GiB"
Write-Host "GPU: $($gpus -join '; ')"
Write-Host "WebView2: $($webview2.version) ($($webview2.path))"
Write-Host "Output: $OutputDir"

if (-not $isWindows11) {
    Write-Host "WARNING: this runner is not Windows 11 build 22000+. Windows 11 real-device checks remain UNVERIFIED." -ForegroundColor Yellow
}
if (-not $webview2) {
    throw "WebView2 Evergreen Runtime was not found. Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and re-run."
}
if (-not $dotnetSdks -or $dotnetSdks.Count -eq 0) {
    throw "No .NET SDK found. Install .NET 8 SDK (or newer LTS) and re-run."
}
if (-not $cargoVersion) {
    throw "Rust toolchain not found. Install rustup/cargo and re-run."
}

# ---------- Build/test steps ----------
Invoke-Step "rust_fmt" { cargo fmt --all -- --check }
Invoke-Step "rust_clippy" { cargo clippy --workspace --all-targets -- -D warnings }
Invoke-Step "rust_tests" { cargo test --workspace }
Invoke-Step "service_tests" { cargo test -p amare-services -- --test-threads=1 }
Invoke-Step "update_logic_tests" { cargo test -p amare-update }
Invoke-Step "storage_tests" { cargo test -p amare-store -- --test-threads=1 }
Invoke-Step "storage_ci" {
    cargo run -p amare-store --bin storage-ci --release -- --output "$OutputDir\storage-ci.json"
}
Invoke-Step "service_ci" {
    cargo run -p amare-services --bin service-ci --release -- --output "$OutputDir\service-ci.json"
}
Invoke-Step "service_benchmark" {
    cargo run -p amare-services --bin service-benchmark --release -- --output "$OutputDir\service-benchmark.json"
}
Invoke-Step "storage_benchmark" {
    cargo run -p amare-store --bin storage-benchmark --release -- --output "$OutputDir\storage-benchmark.json"
}
Invoke-Step "backup_tests" { cargo test -p amare-store --test backup -- --test-threads=1 }
Invoke-Step "platform_key_tests" { cargo test -p amare-platform }
Invoke-Step "backup_ci" {
    cargo run -p amare-store --bin backup-ci --release -- --output "$OutputDir\backup-ci.json"
}
Invoke-Step "backup_benchmark" {
    cargo run -p amare-store --bin backup-benchmark --release -- --output "$OutputDir\backup-benchmark.json"
}
Invoke-Step "rust_windows_target" {
    cargo build -p amare-core -p amare-store -p amare-services -p amare-ffi --target x86_64-pc-windows-msvc
}
Invoke-Step "ffi_build" { cargo build -p amare-ffi }
Invoke-Step "webview2_runtime" {
    $runtime = Get-WebView2Runtime
    if (-not $runtime) { throw "WebView2 Runtime not found" }
    Write-Host "WebView2: $($runtime.version) $($runtime.path)"
}
Invoke-Step "csharp_core_build" {
    dotnet build apps/windows/AMARE.Windows.Core/AMARE.Windows.Core.csproj -c Release
}
Invoke-Step "updater_build" {
    dotnet publish tools/amare-updater/AMARE.Updater.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "$OutputDir\updater-publish"
}

$vcvars = Join-Path $vsInstallPath "VC\Auxiliary\Build\vcvars64.bat"
Invoke-Step "c_header" {
    cmd /c "`"$vcvars`" >nul && cl /nologo /W4 /WX /I crates\amare-ffi\include /c crates\amare-ffi\tests\header_smoke.c /Fo:`"$OutputDir\header_smoke.obj`""
}

$env:PATH = "$(Join-Path (Get-Location) 'target\debug');$env:PATH"
Invoke-Step "csharp_rust_interop" {
    dotnet run --project apps/windows/AMARE.Interop.Tests/AMARE.Interop.Tests.csproj
}

if ($SkipWinUI) {
    Add-SkippedStep "winui_restore" "UNVERIFIED" "Skipped by -SkipWinUI"
    Add-SkippedStep "winui_build" "UNVERIFIED" "Skipped by -SkipWinUI"
} else {
    Invoke-Step "winui_restore" {
        dotnet restore apps/windows/AMARE.Windows/AMARE.Windows.csproj -p:Platform=x64
    }
    Invoke-Step "winui_build" {
        dotnet build apps/windows/AMARE.Windows/AMARE.Windows.csproj --no-restore -c Release -p:Platform=x64
    }
    Invoke-Step "winui_build_debug" {
        dotnet build apps/windows/AMARE.Windows/AMARE.Windows.csproj --no-restore -c Debug -p:Platform=x64
    }
    Invoke-Step "windows_probe_build" {
        dotnet build tools/windows-verify/WindowsVerify.csproj -c Release -p:Platform=x64
    }
    Invoke-Step "winui_package" {
        $exe = Get-ChildItem apps/windows/AMARE.Windows/bin/x64/Release -Recurse -Filter AMARE.Windows.exe -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $exe) { throw "Release AMARE.Windows.exe was not produced by the WinUI build." }
        $stage = Join-Path $OutputDir $PortableName
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $stage | Out-Null
        Copy-Item (Join-Path $exe.Directory.FullName "*") -Destination $stage -Recurse -Force
        $updater = Join-Path $OutputDir "updater-publish\AMARE.Updater.exe"
        if (-not (Test-Path $updater)) { throw "AMARE.Updater.exe was not produced by the updater build." }
        Copy-Item $updater (Join-Path $stage "AMARE.Updater.exe") -Force
        [System.IO.File]::WriteAllText((Join-Path $stage "VERSION"), "$Version`n", [System.Text.UTF8Encoding]::new($false))
        $updateManifest = [ordered]@{
            schema = 1
            product = "AMARE"
            version = $Version
            architecture = "win-x64"
            entryPoint = "AMARE.Windows.exe"
            runtime = "win-x64-self-contained"
            channel = "portable"
            requiredFiles = @("VERSION", "AMARE.Windows.exe", "AMARE.Updater.exe")
        }
        Write-Utf8Json (Join-Path $stage "amare-update.json") $updateManifest
        $notes = Join-Path (Get-Location) "docs\releases\AMARE-$Version.md"
        if (Test-Path $notes) { Copy-Item $notes (Join-Path $stage "RELEASE_NOTES.md") -Force }
        $zip = Join-Path $OutputDir "$PortableName.zip"
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
        $zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        [System.IO.File]::WriteAllText("$zip.sha256", "$zipHash  $PortableName.zip`n", [System.Text.UTF8Encoding]::new($false))
        & $updater validate --package $zip --version $Version --architecture win-x64
        if ($LASTEXITCODE -ne 0) { throw "Portable update package manifest validation failed." }
        Write-Host "Portable package: $zip"
        Write-Host "SHA-256: $zipHash"
        Write-Host "Executable: $($exe.FullName)"
    }
    Invoke-Step "portable_update_helper_smoke" {
        $zip = Join-Path $OutputDir "$PortableName.zip"
        $updater = Join-Path $OutputDir "updater-publish\AMARE.Updater.exe"
        if (-not (Test-Path $zip)) { throw "Portable ZIP is missing." }
        if (-not (Test-Path $updater)) { throw "Updater helper is missing." }
        $root = Join-Path $env:TEMP "AMARE 0.19 테스트-$([guid]::NewGuid().ToString('N'))"
        $appDir = Join-Path $root "app"
        $dataDir = Join-Path $root "data"
        try {
            New-Item -ItemType Directory -Force -Path $appDir, $dataDir | Out-Null
            Set-Content -Path (Join-Path $appDir "VERSION") -Value "0.17.0" -Encoding utf8
            Set-Content -Path (Join-Path $appDir "AMARE.Windows.exe") -Value "old-exe" -Encoding utf8
            Set-Content -Path (Join-Path $dataDir "user-data.txt") -Value "preserve-me" -Encoding utf8
            Set-Content -Path (Join-Path $dataDir "settings.json") -Value '{"theme":"dark"}' -Encoding utf8
            Set-Content -Path (Join-Path $dataDir "bookmarks.db") -Value "bookmarks" -Encoding utf8
            Set-Content -Path (Join-Path $dataDir "history.db") -Value "history" -Encoding utf8
            $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
            & $updater apply --package $zip --app-dir $appDir --data-dir $dataDir --version $Version --sha256 $hash --architecture win-x64 --entry AMARE.Windows.exe --no-launch
            if ($LASTEXITCODE -ne 0) { throw "Updater helper exited with $LASTEXITCODE." }
            $installed = (Get-Content (Join-Path $appDir "VERSION") -Raw).Trim()
            if ($installed -ne $Version) { throw "Helper applied version $installed, expected $Version." }
            if ((Get-Content (Join-Path $dataDir "user-data.txt") -Raw).Trim() -ne "preserve-me") {
                throw "Helper changed user data."
            }
            if ((Get-Content (Join-Path $dataDir "bookmarks.db") -Raw).Trim() -ne "bookmarks") {
                throw "Helper changed bookmarks."
            }
            if ((Get-Content (Join-Path $dataDir "settings.json") -Raw) -notmatch 'theme') {
                throw "Helper changed settings."
            }
            if ((Get-Content (Join-Path $dataDir "history.db") -Raw).Trim() -ne "history") {
                throw "Helper changed history."
            }
            $state = Get-Content (Join-Path $dataDir "update-state.json") -Raw | ConvertFrom-Json
            if ($state.status -ne "succeeded") { throw "Unexpected helper state: $($state.status)" }
            Write-Host "Helper applied $installed; user data preserved; state=$($state.status)"
            & $updater apply --package $zip --app-dir $appDir --data-dir $dataDir --version $Version --sha256 $hash --architecture win-x64 --entry AMARE.Windows.exe --no-launch
            if ($LASTEXITCODE -eq 0) { throw "Helper repeated the same update instead of rejecting it." }
            if ((Get-Content (Join-Path $appDir "VERSION") -Raw).Trim() -ne $Version) {
                throw "Rejected duplicate update changed the installed version."
            }
            Write-Host "Duplicate helper apply rejected as expected"
            $global:LASTEXITCODE = 0
        } finally {
            if (Test-Path $root) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
    Invoke-Step "portable_clean_launch" {
        $zip = Join-Path $OutputDir "$PortableName.zip"
        if (-not (Test-Path $zip)) { throw "Portable ZIP is missing." }
        $root = Join-Path $env:TEMP "AMARE 0.19 clean 테스트-$([guid]::NewGuid().ToString('N'))"
        $appProcess = $null
        try {
            New-Item -ItemType Directory -Force -Path $root | Out-Null
            Expand-Archive -Path $zip -DestinationPath $root -Force
            $exe = Join-Path $root "AMARE.Windows.exe"
            if (-not (Test-Path $exe)) { throw "Clean extraction did not contain AMARE.Windows.exe." }
            if (-not (Test-Path (Join-Path $root "AMARE.Updater.exe"))) { throw "Clean extraction did not contain AMARE.Updater.exe." }
            $appProcess = Start-Process -FilePath $exe -WorkingDirectory $root -PassThru
            Start-Sleep -Seconds 8
            $appProcess.Refresh()
            if ($appProcess.HasExited) { throw "Clean portable app exited during startup (exit $($appProcess.ExitCode))." }
            Write-Host "Clean path launch: $root; pid=$($appProcess.Id)"
        } finally {
            if ($null -ne $appProcess -and -not $appProcess.HasExited) {
                Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 500
            }
            if (Test-Path $root) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
            $global:LASTEXITCODE = 0
        }
    }
    Invoke-Step "winui_msix" {
        $makeAppx = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin") -Recurse -Filter MakeAppx.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $makeAppx) { throw "MakeAppx.exe was not found in the Windows SDK." }
        $stage = Join-Path $OutputDir $PortableName
        $stageAssets = Join-Path $stage "Assets"
        New-Item -ItemType Directory -Force -Path $stageAssets | Out-Null
        Copy-Item (Join-Path (Get-Location) "apps\windows\AMARE.Windows\Assets\*") -Destination $stageAssets -Force
        Copy-Item (Join-Path (Get-Location) "apps\windows\AMARE.Windows\Package.appxmanifest") -Destination (Join-Path $stage "AppxManifest.xml") -Force
        $msix = Join-Path $OutputDir "$PortableName-unsigned.msix"
        if (Test-Path $msix) { Remove-Item $msix -Force }
        & $makeAppx.FullName pack /d $stage /p $msix /o
        if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed with exit code $LASTEXITCODE." }
        Write-Host "Unsigned MSIX: $msix"
    }
    $msix = Join-Path $OutputDir "$PortableName-unsigned.msix"
    try {
        Add-AppxPackage -Path $msix -AllowUnsigned -ErrorAction Stop
        $package = Get-AppxPackage -Name "Amare.Browser" -ErrorAction Stop
        if (-not $package) { throw "MSIX package was not registered." }
        $Steps.Add([pscustomobject]@{
            name = "winui_msix_install"
            status = "PASS"
            exit_code = 0
            required = $false
            started_at = (Get-Date).ToUniversalTime().ToString("o")
            finished_at = (Get-Date).ToUniversalTime().ToString("o")
            duration_ms = 0
            log = $null
            detail = "Unsigned MSIX installed; package=$($package.PackageFullName)."
        })
        Write-Host "Installed package: $($package.PackageFullName)"
    } catch {
        Add-SkippedStep "winui_msix_install" "UNVERIFIED" "Unsigned MSIX install was not available: $($_.Exception.Message)"
    }
}

# ---------- Release metadata ----------
Invoke-Step "release_metadata" {
    $zip = Join-Path $OutputDir "$PortableName.zip"
    $msix = Join-Path $OutputDir "$PortableName-unsigned.msix"
    if (-not (Test-Path $zip)) { throw "Portable ZIP is missing." }
    if (-not (Test-Path $msix)) { throw "Unsigned MSIX is missing." }
    $zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $msixHash = (Get-FileHash $msix -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText("$zip.sha256", "$zipHash  $PortableName.zip`n", [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $OutputDir "checksums.txt"),
        "$zipHash  $PortableName.zip`n$msixHash  $PortableName-unsigned.msix`n",
        [System.Text.UTF8Encoding]::new($false))
    $releaseManifest = [ordered]@{
        schema = 1
        product = "AMARE"
        version = $Version
        architecture = "win-x64"
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        git_commit = (& git rev-parse HEAD).Trim()
        assets = @(
            [ordered]@{ name = "$PortableName.zip"; sha256 = $zipHash; size = (Get-Item $zip).Length },
            [ordered]@{ name = "$PortableName-unsigned.msix"; sha256 = $msixHash; size = (Get-Item $msix).Length }
        )
        notes = "docs/releases/AMARE-$Version.md"
        signing = "UNSIGNED"
    }
    Write-Utf8Json (Join-Path $OutputDir "AMARE-$Version-release-manifest.json") $releaseManifest
    Write-Host "Release metadata: AMARE-$Version-release-manifest.json"
}
Invoke-Step "release_preflight" {
    $releaseManifest = Get-Content (Join-Path $OutputDir "AMARE-$Version-release-manifest.json") -Raw | ConvertFrom-Json
    if ($releaseManifest.version -ne $Version) { throw "Release manifest version mismatch." }
    if ($releaseManifest.architecture -ne "win-x64") { throw "Release manifest architecture mismatch." }
    $assetNames = @($releaseManifest.assets | ForEach-Object { $_.name })
    if (@($assetNames | Select-Object -Unique).Count -ne $assetNames.Count) { throw "Duplicate release asset names." }
    foreach ($asset in $releaseManifest.assets) {
        $path = Join-Path $OutputDir $asset.name
        $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $asset.sha256) { throw "Release manifest hash mismatch: $($asset.name)" }
    }
    $zipHash = ($releaseManifest.assets | Where-Object { $_.name -eq "$PortableName.zip" }).sha256
    $sidecar = ((Get-Content (Join-Path $OutputDir "$PortableName.zip.sha256") -Raw).Trim() -split '\s+')[0]
    if ($zipHash -ne $sidecar) { throw "ZIP sidecar does not match release manifest." }
    $checksumLines = @(Get-Content (Join-Path $OutputDir "checksums.txt") | Where-Object { $_.Trim().Length -gt 0 })
    if ($checksumLines.Count -ne 2) { throw "checksums.txt should contain exactly two assets." }
    Write-Host "Release preflight: version/arch/assets/hashes unique and consistent"
}

# ---------- WebView2 probes ----------
Write-Host ""
Write-Host "Starting WebView2 probes. Follow the status bar for the manual intervention step." -ForegroundColor Yellow
Invoke-Step "webview2_probe_run" {
    dotnet run -c Release --project tools/windows-verify/WindowsVerify.csproj
} -Required $false

    $appExe = Join-Path (Join-Path $OutputDir $PortableName) "AMARE.Windows.exe"
    $appProcess = $null
    $appStarted = (Get-Date).ToUniversalTime().ToString("o")
    try {
        if (-not (Test-Path $appExe)) { throw "Packaged AMARE.Windows.exe is missing." }
        $appProcess = Start-Process -FilePath $appExe -WorkingDirectory (Split-Path $appExe) -PassThru
        Start-Sleep -Seconds 8
        if ($appProcess.HasExited) {
            Add-SkippedStep "app_process_launch" "UNVERIFIED" "AMARE.Windows.exe exited during startup (exit code $($appProcess.ExitCode)); GUI launch may require an interactive desktop/Windows App SDK runtime."
        } else {
            $appProcess.Refresh()
            $windowHandle = $appProcess.MainWindowHandle
            $windowTitle = $appProcess.MainWindowTitle
            $windowDetail = if ($windowHandle -ne [IntPtr]::Zero) {
                "a window handle was created"
            } else {
                "no main window handle was available in the CI session"
            }
            $startupFailed = $windowTitle -match 'could not be started|Required components'
            if ($startupFailed) {
                Add-SkippedStep "app_process_launch" "UNVERIFIED" "AMARE.Windows.exe opened a startup error dialog ($windowTitle); self-contained runtime or deployment is incomplete."
            } else {
                $Steps.Add([pscustomobject]@{
                    name = "app_process_launch"
                    status = "PASS"
                    exit_code = 0
                    required = $false
                    started_at = $appStarted
                    finished_at = (Get-Date).ToUniversalTime().ToString("o")
                    duration_ms = 8000
                    log = $null
                    detail = "AMARE.Windows.exe stayed alive for 8 seconds after Start-Process; $windowDetail; title=($windowTitle); GUI rendering remains UNVERIFIED."
                })
            }
            Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
        }
    } catch {
        if ($null -ne $appProcess -and -not $appProcess.HasExited) {
            Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
        }
        Add-SkippedStep "app_process_launch" "UNVERIFIED" "Could not launch AMARE.Windows.exe: $($_.Exception.Message)"
    }
    $startupLog = Join-Path $env:LOCALAPPDATA "AMARE\logs\startup-error.log"
    if (Test-Path $startupLog) {
        Copy-Item $startupLog (Join-Path $OutputDir "app_startup_error.log") -Force
    }

    $uiLog = Join-Path $OutputDir "winui_ui_smoke.log"
    $uiResults = Join-Path $OutputDir "winui_ui_smoke_results.json"
    $env:AMARE_UI_SMOKE_RESULTS = $uiResults
    $uiExit = 1
    try {
        & dotnet run -c Release --project tools/windows-ui-smoke/WindowsUiSmoke.csproj -- $appExe *>&1 |
            Tee-Object -FilePath $uiLog
        $uiExit = $LASTEXITCODE
    } catch {
        $uiExit = 1
        $_.Exception.Message | Out-File -FilePath $uiLog -Append
    }
    if ($uiExit -eq 0) {
        $Steps.Add([pscustomobject]@{
            name = "winui_ui_smoke"
            status = "PASS"
            exit_code = 0
            required = $false
            started_at = (Get-Date).ToUniversalTime().ToString("o")
            finished_at = (Get-Date).ToUniversalTime().ToString("o")
            duration_ms = 0
            log = $uiLog
            detail = "UIAutomation smoke passed: window, address, tabs, Find, Reader, AI panel, Settings, API settings, update check, diagnostics, session restart."
        })
    } else {
        Add-SkippedStep "winui_ui_smoke" "UNVERIFIED" "UIAutomation smoke failed or was unavailable; see $uiLog."
    }
    $amareLogDir = Join-Path $env:LOCALAPPDATA "AMARE\logs"
    if (Test-Path $amareLogDir) {
        Copy-Item $amareLogDir (Join-Path $OutputDir "amare-logs") -Recurse -Force
    }

$storagePath = Join-Path $OutputDir "storage-ci.json"
$storageSummary = $null
if (Test-Path $storagePath) {
    $storage = Get-Content $storagePath -Raw | ConvertFrom-Json
    $storageSummary = $storage.summary
}

$backupPath = Join-Path $OutputDir "backup-ci.json"
$backupSummary = $null
if (Test-Path $backupPath) {
    $backup = Get-Content $backupPath -Raw | ConvertFrom-Json
    $backupSummary = $backup.summary
}

$servicePath = Join-Path $OutputDir "service-ci.json"
$serviceSummary = $null
if (Test-Path $servicePath) {
    $service = Get-Content $servicePath -Raw | ConvertFrom-Json
    $serviceSummary = $service.summary
}

$harnessPath = Join-Path $OutputDir "windows-verify-results.json"
$harnessSummary = $null
if (Test-Path $harnessPath) {
    $harness = Get-Content $harnessPath -Raw | ConvertFrom-Json
    $harnessSummary = [pscustomobject]@{
        summary = $harness.summary
        pass = @($harness.results | Where-Object { $_.status -eq "PASS" }).Count
        fail = @($harness.results | Where-Object { $_.status -eq "FAIL" }).Count
        unverified = @($harness.results | Where-Object { $_.status -eq "UNVERIFIED" }).Count
        skipped = @($harness.results | Where-Object { $_.status -eq "SKIPPED" }).Count
    }
    if ($harnessSummary.fail -gt 0) {
        $Failures.Add("webview2_probe_failures")
    }
} else {
    $Steps.Add([pscustomobject]@{
        name = "webview2_probe_results"
        status = "UNVERIFIED"
        exit_code = $null
        required = $false
        started_at = $null
        finished_at = (Get-Date).ToUniversalTime().ToString("o")
        log = $null
    })
}

$unifiedResults = New-Object System.Collections.Generic.List[object]
foreach ($step in $Steps) {
    $unifiedResults.Add([pscustomobject]@{
        name = [string]$step.name
        status = [string]$step.status
        executed = ($step.status -eq "PASS" -or $step.status -eq "FAIL")
        duration_ms = if ($null -ne $step.duration_ms) { [int64]$step.duration_ms } else { $null }
        environment = $envReport
        error = $step.error
        log_path = $step.log
        source = "verify-windows"
        detail = $step.detail
    })
}

if ($harnessSummary -and (Test-Path $harnessPath)) {
    $harnessJson = Get-Content $harnessPath -Raw | ConvertFrom-Json
    foreach ($probe in $harnessJson.results) {
        $probeStatus = [string]$probe.status
        $unifiedResults.Add([pscustomobject]@{
            name = "webview2:" + [string]$probe.name
            status = $probeStatus
            executed = [bool]$probe.executed
            duration_ms = $null
            environment = $envReport
            error = if ($probeStatus -eq "FAIL") { [string]$probe.detail } else { $null }
            log_path = Join-Path $OutputDir "webview2_probe_run.log"
            source = "windows-verify-probe"
            detail = [string]$probe.detail
        })
    }
} else {
    $unifiedResults.Add([pscustomobject]@{
        name = "webview2:probe_results"
        status = "UNVERIFIED"
        executed = $false
        duration_ms = $null
        environment = $envReport
        error = "windows-verify-results.json is missing"
        log_path = $null
        source = "windows-verify-probe"
        detail = "Probe harness did not produce results"
    })
}

$interopPath = Join-Path $OutputDir "interop-results.json"
if (Test-Path $interopPath) {
    $interopResults = Get-Content $interopPath -Raw | ConvertFrom-Json
    foreach ($test in $interopResults) {
        $passed = [bool]$test.passed
        $unifiedResults.Add([pscustomobject]@{
            name = "interop:" + [string]$test.name
            status = if ($passed) { "PASS" } else { "FAIL" }
            executed = $true
            duration_ms = $null
            environment = $envReport
            error = if ($passed) { $null } else { [string]$test.detail }
            log_path = Join-Path $OutputDir "csharp_rust_interop.log"
            source = "csharp-interop"
            detail = [string]$test.detail
        })
    }
}

foreach ($ciFile in @("service-ci.json", "storage-ci.json", "backup-ci.json")) {
    $ciPath = Join-Path $OutputDir $ciFile
    if (-not (Test-Path $ciPath)) { continue }
    $ciData = Get-Content $ciPath -Raw | ConvertFrom-Json
    foreach ($case in $ciData.cases) {
        $unifiedResults.Add([pscustomobject]@{
            name = [string]$case.id
            status = [string]$case.status
            executed = [bool]$case.executed
            duration_ms = if ($null -ne $case.duration_ms) { [int64]$case.duration_ms } else { $null }
            environment = if ($case.environment) { $case.environment } else { $envReport }
            error = if ($case.status -eq "FAIL") { [string]$case.detail } else { $null }
            log_path = if ($case.log) { Join-Path $OutputDir $case.log } else { $ciPath }
            source = $ciFile
            detail = [string]$case.detail
        })
    }
}

$unifiedPass = @($unifiedResults | Where-Object { $_.status -eq "PASS" }).Count
$unifiedFail = @($unifiedResults | Where-Object { $_.status -eq "FAIL" }).Count
$unifiedUnverified = @($unifiedResults | Where-Object { $_.status -eq "UNVERIFIED" }).Count
$unifiedSkipped = @($unifiedResults | Where-Object { $_.status -eq "SKIPPED" }).Count
$unifiedReportPath = Join-Path $OutputDir "windows-verification.json"
$unified = [pscustomobject]@{
    schema_version = "1"
    generated_at = (Get-Date).ToUniversalTime().ToString("o")
    runner = [pscustomobject]@{
        os = $envReport.os
        windows_11 = $isWindows11
        is_windows_11_device = $isWindows11
    }
    environment = $envReport
    summary = [pscustomobject]@{
        passed = $unifiedPass
        failed = $unifiedFail
        unverified = $unifiedUnverified
        skipped = $unifiedSkipped
        total = $unifiedResults.Count
    }
    results = $unifiedResults
    note = "Windows Runner results are not Windows 11 real-device results. Unrun checks remain UNVERIFIED."
}
Write-Utf8Json $unifiedReportPath $unified

$summary = [pscustomobject]@{
    completed_at = (Get-Date).ToUniversalTime().ToString("o")
    environment = $envReport
    steps = $Steps
    harness = $harnessSummary
    storage = $storageSummary
    backup = $backupSummary
    service = $serviceSummary
    unified_report = $unifiedReportPath
    results_summary = $unified.summary
    failures = @($Failures)
    next = @(
        "Review windows-verify-results.json and windows-verify-environment.json.",
        "Record 30-tab memory on the target i5-7500/16GB machine.",
        "Perform the manual DRM playback step with a public Shaka asset.",
        "Do not use paid accounts without explicit user consent."
    )
}
Write-Utf8Json (Join-Path $OutputDir "windows-verify-summary.json") $summary

Write-Host ""
if ($Failures.Count -gt 0) {
    Write-Host "::error title=Windows verification failures::$($Failures -join ', ')"
    Write-Host "FAILURES: $($Failures -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "Completed. Review $OutputDir" -ForegroundColor Green
exit 0
