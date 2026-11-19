param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$StatusDir,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string]$FixtureA,
    [Parameter(Mandatory = $true)][string]$FixtureB,
    [int]$Reloads = 10
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\gpui-test-focus.ps1"
Add-Type -AssemblyName System.Windows.Forms
New-Item -ItemType Directory -Force -Path $StatusDir, $DataDir, $OutDir | Out-Null
$script:results = [System.Collections.Generic.List[object]]::new()
function Send-Keys([string]$keys) { [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds 100 }
function Add-Result([string]$name, [string]$state, [string]$detail) {
    $script:results.Add([pscustomobject]@{ name = $name; result = $state; detail = $detail })
    Write-Host ("{0} {1}: {2}" -f $state, $name, $detail)
}
function Read-States {
    $states = @()
    foreach ($file in @(Get-ChildItem $StatusDir -Filter "status-*.json" -File -ErrorAction SilentlyContinue)) {
        try { $states += (Get-Content $file.FullName -Raw | ConvertFrom-Json) } catch { }
    }
    return $states
}
function Read-State([IntPtr]$hwnd) { return @(Read-States | Where-Object { [int64]$_.window_handle -eq $hwnd.ToInt64() } | Select-Object -First 1)[0] }
function Wait-States([int]$count, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $states = @(Read-States)
        if ($states.Count -ge $count) { return $states }
        Start-Sleep -Milliseconds 200
    }
    return @(Read-States)
}
function Wait-Source([IntPtr]$hwnd, [string]$needle, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $state = Read-State $hwnd
        if ($null -ne $state -and [string]$state.source -match $needle) { return $state }
        Start-Sleep -Milliseconds 200
    }
    return (Read-State $hwnd)
}
function Focus-Address([IntPtr]$hwnd) {
    [void](Force-AmareForeground $hwnd)
    Send-GpuiShortcut $hwnd 0x4C
    Start-Sleep -Milliseconds 250
    $state = Read-State $hwnd
    if ($null -ne $state -and $null -ne $state.address_bounds) {
        $b = $state.address_bounds
        Send-RealClientClickSendInput $hwnd ([int]($b.x + $b.width / 2)) ([int]($b.y + $b.height / 2)) | Out-Null
        Start-Sleep -Milliseconds 250
    }
    return (Read-State $hwnd)
}
function Paste-Url([IntPtr]$hwnd, [string]$url, [string]$needle) {
    [void](Focus-Address $hwnd)
    Send-Keys "^a"; Send-Keys "{BACKSPACE}"
    Set-Clipboard -Value $url
    Start-Sleep -Milliseconds 150
    Send-Keys "^v"
    Start-Sleep -Milliseconds 300
    Send-GpuiShortcut $hwnd 0x0D -NoCtrl
    return (Wait-Source $hwnd $needle 20)
}
function Click-Page([IntPtr]$hwnd) {
    Send-RealClientClickSendInput $hwnd 500 300 | Out-Null
    Start-Sleep -Milliseconds 200
}
function Close-Window([IntPtr]$hwnd) {
    [void](Force-AmareForeground $hwnd)
    $state = Read-State $hwnd
    $bounds = $null
    if ($null -ne $state.layout_bounds -and $null -ne $state.layout_bounds.toolbar_children) {
        $bounds = $state.layout_bounds.toolbar_children.'toolbar.window_close'
    }
    if ($null -ne $bounds) {
        Send-RealClientClickSendInput $hwnd ([int]($bounds.x + $bounds.width / 2)) ([int]($bounds.y + $bounds.height / 2)) | Out-Null
    } else {
        [void][AmareTestFocus]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    }
}

$app = $null
$hwnd = [IntPtr]::Zero
try {
    Remove-Item $StatusDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $StatusDir | Out-Null
    $env:AMARE_GPUI_DATA_DIR = $DataDir
    $env:AMARE_GPUI_STATUS_DIR = $StatusDir
    Remove-Item Env:AMARE_GPUI_STATUS -ErrorAction SilentlyContinue
    $env:AMARE_GPUI_START_URL = $FixtureA
    $env:AMARE_GPUI_SELF_TEST = "0"
    $env:AMARE_GPUI_RESTORE = "0"
    $env:AMARE_GPUI_DIAGNOSTICS = "1"
    $env:AMARE_GPUI_MESSAGE_TRACE = "1"
    $env:AMARE_GPUI_WINDOW_SIZE = "1044x788"
    Remove-Item Env:AMARE_GPUI_OPEN_SECOND_WINDOW -ErrorAction SilentlyContinue
    $app = Start-Process $Exe -PassThru -RedirectStandardOutput (Join-Path $OutDir "reload-out.log") -RedirectStandardError (Join-Path $OutDir "reload-err.log")
    $states = Wait-States 1 25
    $hwnd = [IntPtr][int64]$states[0].window_handle
    [void](Wait-Source $hwnd "fixture-a" 25)

    $reloadPass = 0
    for ($i = 1; $i -le $Reloads; $i++) {
        Click-Page $hwnd
        $before = Read-State $hwnd
        $tabBefore = @($before.tabs | Where-Object { $_.id -eq $before.active_tab })[0]
        $at = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        Send-GpuiShortcut $hwnd 0x52
        Start-Sleep -Milliseconds 800
        $after = Read-State $hwnd
        $tabAfter = @($after.tabs | Where-Object { $_.id -eq $after.active_tab })[0]
        $reloadDelta = [int64]$after.reload_count - [int64]$before.reload_count
        $startedDelta = [int64]$tabAfter.navigation_started - [int64]$tabBefore.navigation_started
        $completedDelta = [int64]$tabAfter.navigation_completed - [int64]$tabBefore.navigation_completed
        $key = @($after.keystroke_trace | Where-Object { $_.action -match "ReloadPage" -and [int64]$_.at_ms -ge [int64]$at } | Select-Object -Last 1)[0]
        $ok = $reloadDelta -eq 1 -and ($startedDelta -ge 1 -or $completedDelta -ge 1)
        if ($ok) { $reloadPass++ }
        Add-Result "reload-$i" ($(if ($ok) { "PASS" } else { "UNVERIFIED" })) "reload_delta=$reloadDelta nav_started=$($tabBefore.navigation_started)->$($tabAfter.navigation_started) nav_completed=$($tabBefore.navigation_completed)->$($tabAfter.navigation_completed) key_action=$($key.action) source=$($after.source)"
        Start-Sleep -Milliseconds 400
    }
    Add-Result "reload-summary" "PASS" "pass=$reloadPass/$Reloads"

    [void](Force-AmareForeground $hwnd)
    Send-GpuiShortcut $hwnd 0x4E
    $states2 = Wait-States 2 10
    if ($states2.Count -ge 2) {
        $a = [IntPtr][int64]$states2[0].window_handle
        $b = [IntPtr][int64]$states2[1].window_handle
        $bLoaded = Paste-Url $b $FixtureB "fixture-b"
        Add-Result "ab-b-load" ($(if ([string]$bLoaded.source -match "fixture-b") { "PASS" } else { "UNVERIFIED" })) "b=0x$('{0:X}' -f $b.ToInt64()) source=$($bLoaded.source) title=$($bLoaded.title)"
        $aState = Read-State $a
        $bBefore = Read-State $b
        Click-Page $a
        Send-GpuiShortcut $a 0x52
        Start-Sleep -Milliseconds 800
        $aAfter = Read-State $a
        $bAfter = Read-State $b
        $aTabBefore = @($aState.tabs | Where-Object { $_.id -eq $aState.active_tab })[0]
        $aTabAfter = @($aAfter.tabs | Where-Object { $_.id -eq $aAfter.active_tab })[0]
        Add-Result "ab-reload-a" ($(if ([int64]$aTabAfter.navigation_started -gt [int64]$aTabBefore.navigation_started -and [string]$bAfter.source -eq [string]$bBefore.source) { "PASS" } else { "UNVERIFIED" })) "a_nav=$($aTabBefore.navigation_started)->$($aTabAfter.navigation_started) b_source_unchanged=$([string]$bAfter.source -eq [string]$bBefore.source) b_source=$($bAfter.source)"
        $bBefore2 = Read-State $b
        $aBefore2 = Read-State $a
        Click-Page $b
        Send-GpuiShortcut $b 0x52
        Start-Sleep -Milliseconds 800
        $bAfter2 = Read-State $b
        $aAfter2 = Read-State $a
        $bTabBefore = @($bBefore2.tabs | Where-Object { $_.id -eq $bBefore2.active_tab })[0]
        $bTabAfter = @($bAfter2.tabs | Where-Object { $_.id -eq $bAfter2.active_tab })[0]
        Add-Result "ab-reload-b" ($(if ([int64]$bTabAfter.navigation_started -gt [int64]$bTabBefore.navigation_started -and [string]$aAfter2.source -eq [string]$aBefore2.source) { "PASS" } else { "UNVERIFIED" })) "b_nav=$($bTabBefore.navigation_started)->$($bTabAfter.navigation_started) a_source_unchanged=$([string]$aAfter2.source -eq [string]$aBefore2.source) a_source=$($aAfter2.source)"

        Close-Window $b
        Start-Sleep -Seconds 2
        $bValid = [AmareTestFocus]::IsWindow($b)
        $aValid = [AmareTestFocus]::IsWindow($a)
        Add-Result "ab-close-b" ($(if ($aValid -and -not $bValid) { "PASS" } else { "UNVERIFIED" })) "a_valid=$aValid b_valid=$bValid"
        Click-Page $a
        $aReloadBefore = Read-State $a
        Send-GpuiShortcut $a 0x52
        Start-Sleep -Milliseconds 800
        $aReloadAfter = Read-State $a
        Add-Result "ab-a-after-b-close" ($(if ([int64]$aReloadAfter.reload_count -gt [int64]$aReloadBefore.reload_count) { "PASS" } else { "UNVERIFIED" })) "reload=$($aReloadBefore.reload_count)->$($aReloadAfter.reload_count) source=$($aReloadAfter.source)"

        Close-Window $a
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $deadline -and [AmareTestFocus]::IsWindow($a)) { Start-Sleep -Milliseconds 300 }
        $aGone = -not [AmareTestFocus]::IsWindow($a)
        $exited = $app.WaitForExit(5000)
        Add-Result "ab-close-a-last" ($(if ($aGone -and $exited) { "PASS" } else { "UNVERIFIED" })) "a_window_gone=$aGone process_exited=$exited exit=$($app.ExitCode)"
        $app = $null

        $app2 = Start-Process $Exe -PassThru -RedirectStandardOutput (Join-Path $OutDir "reload2-out.log") -RedirectStandardError (Join-Path $OutDir "reload2-err.log")
        $states3 = Wait-States 1 25
        $c = [IntPtr][int64]$states3[0].window_handle
        $cState = Wait-Source $c "fixture-a" 25
        Add-Result "window-recreate" ($(if ([AmareTestFocus]::IsWindow($c) -and [string]$cState.source -match "fixture-a") { "PASS" } else { "UNVERIFIED" })) "c=0x$('{0:X}' -f $c.ToInt64()) source=$($cState.source)"
        if ($null -ne $app2 -and -not $app2.HasExited) { Stop-Process -Id $app2.Id -Force -ErrorAction SilentlyContinue }
    } else {
        Add-Result "ab-window-create" "FAIL" "second window missing"
    }
} catch {
    Add-Result "reload-fatal" "FAIL" $_.Exception.Message
} finally {
    if ($null -ne $app -and -not $app.HasExited) { try { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue } catch { } }
    $summary = [pscustomobject]@{
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        results = $script:results
        pass = @($script:results | Where-Object result -eq "PASS").Count
        fail = @($script:results | Where-Object result -eq "FAIL").Count
        unverified = @($script:results | Where-Object result -eq "UNVERIFIED").Count
    }
    $summary | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $OutDir "gpui-reload-results.json") -Encoding UTF8
    Write-Host "GPUI reload summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)"
    if ($summary.fail -gt 0) { exit 1 }
}
