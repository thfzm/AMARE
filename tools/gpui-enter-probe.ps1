param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$StatusDir,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$Iterations = 5
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\gpui-test-focus.ps1"
Add-Type -AssemblyName System.Windows.Forms
New-Item -ItemType Directory -Force -Path $StatusDir, $DataDir, $OutDir | Out-Null
$script:results = [System.Collections.Generic.List[object]]::new()
function Send-Keys([string]$keys) { [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds 120 }
function Add-Result([string]$name, [string]$state, [string]$detail) {
    $script:results.Add([pscustomobject]@{ name = $name; result = $state; detail = $detail })
    Write-Host ("{0} {1}: {2}" -f $state, $name, $detail)
}
function Read-State {
    $file = Get-ChildItem $StatusDir -Filter "status-*.json" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $file) { return $null }
    try { return Get-Content $file.FullName -Raw | ConvertFrom-Json } catch { return $null }
}
function Wait-State([scriptblock]$test, [int]$seconds, [string]$label) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $state = Read-State
        if ($null -ne $state -and (& $test $state)) { return $state }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for $label"
}
function Start-ProbeApp([bool]$restore) {
    Remove-Item $StatusDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $StatusDir | Out-Null
    $env:AMARE_GPUI_DATA_DIR = $DataDir
    $env:AMARE_GPUI_STATUS_DIR = $StatusDir
    Remove-Item Env:AMARE_GPUI_STATUS -ErrorAction SilentlyContinue
    $env:AMARE_GPUI_START_URL = "about:blank"
    $env:AMARE_GPUI_SELF_TEST = "0"
    $env:AMARE_GPUI_RESTORE = if ($restore) { "1" } else { "0" }
    $env:AMARE_GPUI_DIAGNOSTICS = "1"
    $env:AMARE_GPUI_MESSAGE_TRACE = "1"
    $env:AMARE_GPUI_WINDOW_SIZE = "1044x788"
    $outLog = Join-Path $OutDir "probe-out.log"
    $errLog = Join-Path $OutDir "probe-err.log"
    $process = Start-Process $Exe -PassThru -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    $hwnd = [IntPtr]::Zero
    for ($i = 0; $i -lt 100; $i++) {
        Start-Sleep -Milliseconds 200
        $state = Read-State
        if ($null -ne $state -and $state.window_handle) {
            $hwnd = [IntPtr][int64]$state.window_handle
            break
        }
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw "probe window not found" }
    [void](Wait-State { param($s) $s.address_focused -or $s.tabs.Count -ge 1 } 20 "initial state")
    return @($process, $hwnd)
}
function Stop-ProbeApp($process, [IntPtr]$hwnd) {
    if ($null -ne $process -and -not $process.HasExited) {
        [void][AmareTestFocus]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        if (-not $process.WaitForExit(15000)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    Start-Sleep -Milliseconds 600
}
function Run-ProbeRound([string]$label, [IntPtr]$hwnd, [int]$index) {
    [void](Force-AmareForeground $hwnd)
    $clickUsed = $script:AmareForegroundClicked
    Send-GpuiShortcut $hwnd 0x4C
    Start-Sleep -Milliseconds 250
    Send-Keys "^a"; Send-Keys "{BACKSPACE}"
    Start-Sleep -Milliseconds 150
    $before = Read-State
    if ($null -eq $before) { throw "state missing before typing" }
    $keyBefore = [int64]$before.keystroke_total
    $enterKeyBefore = [int64]$before.address_enter_count
    $submitBefore = [int64]$before.address_submit_count
    Send-TextKeys "example"
    Start-Sleep -Milliseconds 300
    $typed = Read-State
    $typedOk = [string]$typed.address -eq "example"
    Add-Result "$label-$index-text" ($(if ($typedOk) { "PASS" } else { "UNVERIFIED" })) "address_len=$(([string]$typed.address).Length) expected=7 key_delta=$([int64]$typed.keystroke_total - $keyBefore) focus=$($typed.address_focused)"
    $preFocus = Get-AmareGuiFocus -WindowHandle $hwnd -GuiThreadId ([uint32]$typed.gui_thread_id)
    $pre = Read-State
    $enterAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    [void](Force-AmareForeground $hwnd)
    $enterClickUsed = $script:AmareForegroundClicked
    $foreground = [AmareTestFocus]::GetForegroundWindow()
    Add-Result "$label-$index-precondition" ($(if ($foreground -eq $hwnd -and $preFocus.focus_is_window) { "PASS" } else { "UNVERIFIED" })) "foreground=0x$('{0:X}' -f $foreground.ToInt64()) hwnd=0x$('{0:X}' -f $hwnd.ToInt64()) external_focus=0x$('{0:X}' -f $preFocus.focus_hwnd) external_class=$($preFocus.focus_class) window_active=$($pre.window_active) address_focused=$($pre.address_focused) click_used=$clickUsed/$enterClickUsed hook_tid=$($pre.message_trace_thread_id) gui_tid=$($pre.gui_thread_id) ime_open=$($pre.ime_open) layout=$($pre.keyboard_layout)"
    Send-GpuiShortcut $hwnd 0x0D -NoCtrl
    Start-Sleep -Milliseconds 500
    $after = Read-State
    $msgs = @($after.message_trace | Where-Object { [int64]$_.at_ms -ge [int64]$enterAt })
    $call = @($msgs | Where-Object { $_.hook -eq "call" -and [int]$_.message -eq 256 -and [int]$_.vk -eq 13 } | Select-Object -Last 1)[0]
    $get = @($msgs | Where-Object { $_.hook -eq "get" -and [int]$_.message -eq 256 -and [int]$_.vk -eq 13 } | Select-Object -Last 1)[0]
    $ll = @($msgs | Where-Object { $_.hook -eq "ll" -and [int]$_.vk -eq 13 } | Select-Object -Last 1)[0]
    Add-Result "$label-$index-win32" ($(if ($null -ne $call -or $null -ne $get) { "PASS" } else { "UNVERIFIED" })) "callwnd=$($null -ne $call) getmsg=$($null -ne $get) ll=$($null -ne $ll) ll_injected=$($ll.injected) fg=0x$('{0:X}' -f [int64]$ll.hwnd)"
    $key = @($after.keystroke_trace | Where-Object { $_.keystroke -match "enter" -and [int64]$_.at_ms -ge [int64]$enterAt } | Select-Object -Last 1)[0]
    Add-Result "$label-$index-gpui-key" ($(if ($null -ne $key) { "PASS" } else { "UNVERIFIED" })) "keystroke=$($key.keystroke) action=$($key.action) window_active=$($key.window_active)"
    $submitted = [int64]$after.address_submit_count -gt $submitBefore
    Add-Result "$label-$index-submit" ($(if ($submitted) { "PASS" } else { "UNVERIFIED" })) "submit_delta=$([int64]$after.address_submit_count - $submitBefore) enter_delta=$([int64]$after.address_enter_count - $enterKeyBefore) source=$($after.source)"
}

$app = $null
try {
    $started = Start-ProbeApp $false
    $app = $started[0]; $hwnd = $started[1]
    for ($i = 1; $i -le $Iterations; $i++) {
        Run-ProbeRound "probe-normal" $hwnd $i
    }
    Stop-ProbeApp $app $hwnd
    $app = $null
    $started = Start-ProbeApp $true
    $app = $started[0]; $hwnd = $started[1]
    [void](Wait-State { param($s) $s.session_summary -ne $null -or $s.address_focused } 20 "restored state")
    for ($i = 1; $i -le $Iterations; $i++) {
        Run-ProbeRound "probe-restore" $hwnd $i
    }
} catch {
    Add-Result "probe-fatal" "FAIL" $_.Exception.Message
} finally {
    if ($null -ne $app) { Stop-ProbeApp $app $hwnd }
    $summary = [pscustomobject]@{
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        results = $script:results
        pass = @($script:results | Where-Object result -eq "PASS").Count
        fail = @($script:results | Where-Object result -eq "FAIL").Count
        unverified = @($script:results | Where-Object result -eq "UNVERIFIED").Count
    }
    $summary | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $OutDir "gpui-enter-probe-results.json") -Encoding UTF8
    Write-Host "GPUI enter probe summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)"
    if ($summary.fail -gt 0) { exit 1 }
}
