param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$StatusDir,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\gpui-test-focus.ps1"
Add-Type -AssemblyName System.Windows.Forms

$script:app = $null
function Read-AllStates {
    $states = @()
    if (-not (Test-Path $StatusDir)) { return $states }
    foreach ($file in Get-ChildItem $StatusDir -Filter "status-*.json" -File -ErrorAction SilentlyContinue) {
        try {
            $state = Get-Content $file.FullName -Raw -ErrorAction Stop | ConvertFrom-Json
            if ($null -ne $state -and $state.pid -eq $script:app.Id) { $states += $state }
        } catch { }
    }
    return $states
}
function Wait-Initial {
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        $states = @(Read-AllStates)
        if ($states.Count -ge 1) { return $states[0] }
        Start-Sleep -Milliseconds 250
    }
    throw "initial status missing"
}
function Snapshot([string]$label) {
    $states = @(Read-AllStates)
    return [pscustomobject]@{
        label = $label
        windows = $states.Count
        states = $states
        captured_at = (Get-Date).ToUniversalTime().ToString("o")
    }
}

Remove-Item $StatusDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $StatusDir, $DataDir, (Split-Path $Output -Parent) | Out-Null
$env:AMARE_GPUI_DATA_DIR = $DataDir
$env:AMARE_GPUI_STATUS_DIR = $StatusDir
Remove-Item Env:AMARE_GPUI_STATUS -ErrorAction SilentlyContinue
Remove-Item Env:AMARE_GPUI_STATUS_WINDOW -ErrorAction SilentlyContinue
$env:AMARE_GPUI_START_URL = "about:blank"
$env:AMARE_GPUI_SELF_TEST = "0"
$env:AMARE_GPUI_RESTORE = "0"
$env:AMARE_GPUI_WINDOW_SIZE = "1044x788"
$env:AMARE_GPUI_DIAGNOSTICS = "1"

$steps = [System.Collections.Generic.List[object]]::new()
try {
    $script:app = Start-Process $Exe -PassThru
    $initial = Wait-Initial
    $hwnd = [IntPtr][int64]$initial.window_handle
    if ($hwnd -eq [IntPtr]::Zero) { throw "window handle missing" }
    [void](Force-AmareForeground $hwnd)
    Start-Sleep -Milliseconds 500
    $steps.Add((Snapshot "initial"))

    # OS-level keyboard Ctrl+T.
    [void](Force-AmareForeground $hwnd)
    [System.Windows.Forms.SendKeys]::SendWait("^t")
    Start-Sleep -Seconds 3
    $steps.Add((Snapshot "os-ctrl-t"))

    # Direct HWND mouse click on the new-tab toolbar button.
    $state = (Read-AllStates | Where-Object { $_.window_id -eq $initial.window_id } | Select-Object -First 1)
    if ($null -ne $state -and $state.source) { }
    Send-GpuiClick $hwnd 768 30
    Start-Sleep -Seconds 3
    $steps.Add((Snapshot "button-new-tab"))

    # OS-level keyboard Ctrl+N.
    [void](Force-AmareForeground $hwnd)
    [System.Windows.Forms.SendKeys]::SendWait("^n")
    Start-Sleep -Seconds 5
    $steps.Add((Snapshot "os-ctrl-n"))

    # Direct HWND mouse click on the new-window toolbar button.
    Send-GpuiClick $hwnd 813 30
    Start-Sleep -Seconds 5
    $steps.Add((Snapshot "button-new-window"))

    # Record foreground/focus at end.
    $foreground = [AmareTestFocus]::GetForegroundWindow()
    $result = [pscustomobject]@{
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        pid = $script:app.Id
        initial_hwnd = $hwnd.ToInt64()
        foreground_hwnd = $foreground.ToInt64()
        diagnostics_enabled = $true
        steps = $steps
    }
    $result | ConvertTo-Json -Depth 12 | Set-Content $Output -Encoding UTF8
    Write-Host "input diagnostics written: $Output"
} finally {
    if ($null -ne $script:app -and -not $script:app.HasExited) {
        try { Stop-Process -Id $script:app.Id -Force } catch {}
    }
}
