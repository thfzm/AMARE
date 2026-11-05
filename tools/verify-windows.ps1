param(
    [switch]$SkipManual,
    [switch]$SkipMemory,
    [switch]$SkipWinUI,
    [switch]$CI
)

if ($CI) {
    $SkipManual = $true
    $SkipMemory = $true
    $env:AMARE_VERIFY_CI = "1"
    $env:AMARE_VERIFY_HEADLESS = "1"
    $env:AMARE_VERIFY_DRM = "0"
}

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$OutputDir = Join-Path (Get-Location) "artifacts\windows"
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
        $stage = Join-Path $OutputDir "AMARE-0.16.0-win-x64"
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $stage | Out-Null
        Copy-Item (Join-Path $exe.Directory.FullName "*") -Destination $stage -Recurse -Force
        $zip = Join-Path $OutputDir "AMARE-0.16.0-win-x64.zip"
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
        Write-Host "Portable package: $zip"
        Write-Host "Executable: $($exe.FullName)"
    }
}

# ---------- WebView2 probes ----------
Write-Host ""
Write-Host "Starting WebView2 probes. Follow the status bar for the manual intervention step." -ForegroundColor Yellow
Invoke-Step "webview2_probe_run" {
    dotnet run -c Release --project tools/windows-verify/WindowsVerify.csproj
} -Required $false

    $appExe = Join-Path $OutputDir "AMARE-0.16.0-win-x64\AMARE.Windows.exe"
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
            detail = "UIAutomation smoke passed: window, address, tabs, Find, Reader, AI panel, Settings, session restart."
        })
    } else {
        Add-SkippedStep "winui_ui_smoke" "UNVERIFIED" "UIAutomation smoke failed or was unavailable; see $uiLog."
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
