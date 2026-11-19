param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$StatusDir,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string]$Fixture,
    [int]$Trials = 3
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
        Start-Sleep -Milliseconds 200
    }
    return (Read-State $hwnd)
}
function Load-Fixture([IntPtr]$hwnd) {
    [void](Focus-Address $hwnd)
    Send-Keys "^a"; Send-Keys "{BACKSPACE}"
    Set-Clipboard -Value $Fixture
    Start-Sleep -Milliseconds 150
    Send-Keys "^v"
    Start-Sleep -Milliseconds 250
    Send-GpuiShortcut $hwnd 0x0D -NoCtrl
    return (Wait-Source $hwnd "fixture" 20)
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
function Key-Result([string]$label, [string]$key, [IntPtr]$hwnd, [scriptblock]$action, [scriptblock]$verify) {
    [void](Focus-Address $hwnd)
    $before = Read-State $hwnd
    $at = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    & $action
    Start-Sleep -Milliseconds 700
    $after = Read-State $hwnd
    $msgs = @($after.message_trace | Where-Object { [int64]$_.at_ms -ge [int64]$at })
    $keydown = @($msgs | Where-Object { [int]$_.message -eq 256 }).Count
    $trace = @($after.keystroke_trace | Where-Object { [int64]$_.at_ms -ge [int64]$at } | Select-Object -Last 1)[0]
    $ok = & $verify $before $after
    Add-Result "$label-$key" ($(if ($ok) { "PASS" } else { "UNVERIFIED" })) "keydown=$keydown action=$($trace.action) last_action=$($after.last_action) focus=$($after.os_focus.keyboard_focus_class) tabs=$(@($before.tabs).Count)->$(@($after.tabs).Count) reload=$($before.reload_count)->$($after.reload_count) source=$($after.source)"
}

$app = $null
try {
    Remove-Item $StatusDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $StatusDir | Out-Null
    $env:AMARE_GPUI_DATA_DIR = $DataDir
    $env:AMARE_GPUI_STATUS_DIR = $StatusDir
    Remove-Item Env:AMARE_GPUI_STATUS -ErrorAction SilentlyContinue
    $env:AMARE_GPUI_START_URL = "about:blank"
    $env:AMARE_GPUI_SELF_TEST = "0"
    $env:AMARE_GPUI_RESTORE = "0"
    $env:AMARE_GPUI_DIAGNOSTICS = "1"
    $env:AMARE_GPUI_MESSAGE_TRACE = "1"
    $env:AMARE_GPUI_WINDOW_SIZE = "1044x788"
    Remove-Item Env:AMARE_GPUI_OPEN_SECOND_WINDOW -ErrorAction SilentlyContinue
    $app = Start-Process $Exe -PassThru -RedirectStandardOutput (Join-Path $OutDir "keyboard-out.log") -RedirectStandardError (Join-Path $OutDir "keyboard-err.log")
    $states = Wait-States 1 25
    $hwnd = [IntPtr][int64]$states[0].window_handle
    $loaded = Load-Fixture $hwnd
    Add-Result "fixture-load" ($(if ([string]$loaded.source -match "fixture") { "PASS" } else { "UNVERIFIED" })) "source=$($loaded.source) title=$($loaded.title)"

    Key-Result "A" "ctrl-l" $hwnd { Send-GpuiShortcut $hwnd 0x4C } { param($b, $a) $a.address_focused -and @($a.selected_range).Count -eq 2 }
    Key-Result "A" "ctrl-t" $hwnd { Send-GpuiShortcut $hwnd 0x54 } { param($b, $a) @($a.tabs).Count -gt @($b.tabs).Count }
    Key-Result "A" "ctrl-r" $hwnd { Send-GpuiShortcut $hwnd 0x52 } { param($b, $a) [int64]$a.reload_count -gt [int64]$b.reload_count -and [int64]$a.reload_count -gt [int64]$b.reload_count }
    Key-Result "A" "ctrl-w" $hwnd { Send-GpuiShortcut $hwnd 0x57 } { param($b, $a) @($a.tabs).Count -lt @($b.tabs).Count }
    Key-Result "A" "text" $hwnd { Send-Keys "^a"; Send-Keys "{BACKSPACE}"; Send-ScanCodeText "alpha" } { param($b, $a) [string]$a.address -eq "alpha" }

    [void](Focus-Address $hwnd)
    Send-GpuiShortcut $hwnd 0x52
    Start-Sleep -Milliseconds 900
    $afterFirstReload = Read-State $hwnd
    Add-Result "B-first-reload" ($(if ([int64]$afterFirstReload.reload_count -gt [int64]$loaded.reload_count) { "PASS" } else { "UNVERIFIED" })) "reload=$($loaded.reload_count)->$($afterFirstReload.reload_count) nav=$(@($afterFirstReload.tabs | Where-Object { $_.id -eq $afterFirstReload.active_tab })[0].navigation_started) source=$($afterFirstReload.source)"
    Key-Result "B-after-reload" "ctrl-l" $hwnd { Send-GpuiShortcut $hwnd 0x4C } { param($b, $a) $a.address_focused }
    Key-Result "B-after-reload" "ctrl-t" $hwnd { Send-GpuiShortcut $hwnd 0x54 } { param($b, $a) @($a.tabs).Count -gt @($b.tabs).Count }
    Key-Result "B-after-reload" "text" $hwnd { Send-Keys "^a"; Send-Keys "{BACKSPACE}"; Send-ScanCodeText "beta" } { param($b, $a) [string]$a.address -eq "beta" }

    for ($i = 1; $i -le $Trials; $i++) {
        $state = Read-State $hwnd
        $tabs = @($state.tabs).Count
        $active = $state.active_tab
        Send-GpuiShortcut $hwnd 0x54
        Start-Sleep -Milliseconds 600
        $after = Read-State $hwnd
        Add-Result "C-interaction-$i" ($(if (@($after.tabs).Count -ne $tabs) { "PASS" } else { "UNVERIFIED" })) "tabs=$tabs->$(@($after.tabs).Count) active=$active->$($after.active_tab) source=$($after.source)"
    }
} catch {
    Add-Result "keyboard-fatal" "FAIL" $_.Exception.Message
} finally {
    if ($null -ne $app -and -not $app.HasExited) { try { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue } catch { } }
    $summary = [pscustomobject]@{
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        results = $script:results
        pass = @($script:results | Where-Object result -eq "PASS").Count
        fail = @($script:results | Where-Object result -eq "FAIL").Count
        unverified = @($script:results | Where-Object result -eq "UNVERIFIED").Count
    }
    $summary | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $OutDir "gpui-keyboard-results.json") -Encoding UTF8
    Write-Host "GPUI keyboard summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)"
    if ($summary.fail -gt 0) { exit 1 }
}
