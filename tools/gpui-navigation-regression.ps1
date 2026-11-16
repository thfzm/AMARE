param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$StatusDir,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$ScreenshotDir
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\gpui-test-focus.ps1"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AmareNav {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    public static IntPtr[] Windows(int processId) {
        var result = new System.Collections.Generic.List<IntPtr>();
        EnumWindows((hwnd, lparam) => {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            var title = new System.Text.StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);
            if (pid == (uint)processId && IsWindowVisible(hwnd) && title.ToString() == "AMARE") { result.Add(hwnd); }
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
}
'@

$localPagePath = (Resolve-Path (Join-Path $PSScriptRoot "..\apps\amare-gpui\test-page\index.html")).Path
$BaseUrl = ([Uri]::new($localPagePath)).AbsoluteUri
if (-not (Test-Path $localPagePath)) { throw "navigation fixture missing: $localPagePath" }
$script:results = [System.Collections.Generic.List[object]]::new()
function Add-Result([string]$name, [string]$state, [string]$detail) {
    $script:results.Add([pscustomobject]@{ name = $name; result = $state; detail = $detail })
    Write-Host ("{0} {1}: {2}" -f $state, $name, $detail)
}
function Read-AllStates {
    $states = @()
    if (-not (Test-Path $StatusDir)) { return $states }
    foreach ($file in @(Get-ChildItem $StatusDir -Filter "status-*.json" -File -ErrorAction SilentlyContinue)) {
        try {
            $state = Get-Content $file.FullName -Raw -ErrorAction Stop | ConvertFrom-Json
            if ($null -ne $state -and $state.pid -eq $script:app.Id) { $states += $state }
        } catch { }
    }
    return $states
}
function Read-State([IntPtr]$hwnd) {
    foreach ($state in Read-AllStates) {
        if ([int64]$state.window_handle -eq $hwnd.ToInt64()) { return $state }
    }
    return $null
}
function Wait-States([int]$count, [int]$seconds, [string]$label) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $states = @(Read-AllStates)
        if ($states.Count -eq $count) { return $states }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for $label (states=$(@(Read-AllStates).Count))"
}
function Wait-StatesAtLeast([int]$count, [int]$seconds, [string]$label) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $states = @(Read-AllStates)
        if ($states.Count -ge $count) { return $states }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for $label (states=$(@(Read-AllStates).Count))"
}
function Wait-State([IntPtr]$hwnd, [scriptblock]$condition, [int]$seconds, [string]$label) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $state = Read-State $hwnd
        if ($null -ne $state -and (& $condition $state)) { return $state }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for $label"
}
function Send-Keys([string]$keys) { [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds 120 }
function Click-Client([IntPtr]$hwnd, [int]$x, [int]$y) {
    [void](Force-AmareForeground $hwnd)
    $point = New-Object AmareTestFocus+POINT
    [void][AmareTestFocus]::ClientToScreen($hwnd, [ref]$point)
    [void][AmareTestFocus]::SetCursorPos(($point.X + $x), ($point.Y + $y))
    Start-Sleep -Milliseconds 100
    [AmareTestFocus]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [AmareTestFocus]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250
}
function Click-Address([IntPtr]$hwnd, [switch]$Fallback) {
    $state = Read-State $hwnd
    if (-not $Fallback -and $null -ne $state -and $null -ne $state.address_bounds) {
        $b = $state.address_bounds
        Click-Client $hwnd ([int]($b.x + $b.width / 2)) ([int]($b.y + $b.height / 2))
    } else {
        Click-Client $hwnd 420 $(if ($Fallback) { 105 } else { 70 })
    }
}
function Focus-Address([IntPtr]$hwnd, [switch]$SkipClick) {
    if (-not $SkipClick) { Click-Address $hwnd }
    Send-GpuiShortcut $hwnd 0x4C
    try {
        [void](Wait-State $hwnd { param($s) @($s.selected_range).Count -eq 2 -and [int]$s.selected_range[0] -eq 0 -and [int]$s.selected_range[1] -eq ([string]$s.address).Length } 6 "address selected")
        return $true
    } catch {
        Click-Client $hwnd 420 105
        Send-GpuiShortcut $hwnd 0x4C
        try {
            [void](Wait-State $hwnd { param($s) @($s.selected_range).Count -eq 2 -and [int]$s.selected_range[0] -eq 0 -and [int]$s.selected_range[1] -eq ([string]$s.address).Length } 6 "address selected retry")
            return $true
        } catch { return $false }
    }
}
function Submit-Url([IntPtr]$hwnd, [string]$url, [int]$seconds = 25, [switch]$SkipClick) {
    $state = Read-State $hwnd
    $mouseBefore = [int64]$state.mouse_down_count
    $keyBefore = [int64]$state.key_down_count
    $requestBefore = [int64]$state.navigation_requests
    $submitBefore = [int64]$state.address_submit_count
    $textBefore = [int64]$state.address_text_input_count
    $enterBefore = [int64]$state.address_enter_count
    $focusGainedBefore = [int64]$state.address_focus_gained_count
    $activeTab = @($state.tabs | Where-Object { $_.id -eq $state.active_tab })[0]
    $startedBefore = if ($null -ne $activeTab) { [int64]$activeTab.navigation_started } else { 0 }
    $selected = Focus-Address $hwnd -SkipClick:$SkipClick
    $state = Read-State $hwnd
    Add-Result "input-delivery-$($script:case)" "PASS" "mouse_delta=$([int64]$state.mouse_down_count - $mouseBefore) selected=$selected focused=$($state.address_focused) foreground=$([AmareTestFocus]::GetForegroundWindow() -eq $hwnd)"
    Send-Keys "^a"; Send-Keys "{BACKSPACE}"
    Send-Keys $url
    try {
        [void](Wait-State $hwnd { param($s) [int64]$s.address_text_input_count -gt [int64]$textBefore } 6 "address text input")
    } catch {
        $now = Read-State $hwnd
        Add-Result "text-input-$($script:case)" "FAIL" "address_text_input_count=$($now.address_text_input_count) focused=$($now.address_focused) selected=$($now.selected_range -join ',')"
        return $null
    }
    Add-Result "text-input-$($script:case)" "PASS" "text_count=$((Read-State $hwnd).address_text_input_count) focus_gained_delta=$([int64](Read-State $hwnd).address_focus_gained_count - $focusGainedBefore)"
    Send-Keys "{ENTER}"
    try {
        [void](Wait-State $hwnd { param($s) [int64]$s.address_enter_count -gt [int64]$enterBefore } 6 "address enter event")
    } catch {
        $now = Read-State $hwnd
        Add-Result "enter-event-$($script:case)" "FAIL" "address_enter_count=$($now.address_enter_count) focused=$($now.address_focused)"
        return $null
    }
    Add-Result "enter-event-$($script:case)" "PASS" "enter_count=$((Read-State $hwnd).address_enter_count)"
    $submitOk = $false
    for ($submitAttempt = 0; $submitAttempt -lt 2; $submitAttempt++) {
        try {
            [void](Wait-State $hwnd { param($s) [int64]$s.address_submit_count -gt [int64]$submitBefore } 6 "address submit callback")
            $submitOk = $true
            break
        } catch {
            if ($submitAttempt -eq 1) { break }
            Add-Result "submit-callback-$($script:case)" "UNVERIFIED" "first Enter not observed; retrying explicit address click"
            Click-Address $hwnd -Fallback
            Send-GpuiShortcut $hwnd 0x4C
            Send-Keys "^a"; Send-Keys "{BACKSPACE}"
            Send-Keys $url
            Send-Keys "{ENTER}"
        }
    }
    if (-not $submitOk) {
        Add-Result "submit-callback-$($script:case)" "FAIL" "Enter/submit callback not observed after two explicit submits"
        return $null
    }
    Add-Result "submit-callback-$($script:case)" "PASS" "submit_count=$((Read-State $hwnd).address_submit_count) result=$((Read-State $hwnd).last_submit_result)"
    try {
        [void](Wait-State $hwnd { param($s) [int64]$s.navigation_requests -gt [int64]$requestBefore } 10 "navigation request")
        $startedSeen = $false
        try {
            [void](Wait-State $hwnd {
                param($s)
                $t = @($s.tabs | Where-Object { $_.id -eq $s.active_tab })[0]
                $null -ne $t -and [int64]$t.navigation_started -gt [int64]$startedBefore
            } 10 "NavigationStarting")
            $startedSeen = $true
        } catch { }
        $pendingSeen = $false
        try {
            [void](Wait-State $hwnd { param($s) @($s.tabs | Where-Object { $_.id -eq $s.active_tab -and $null -ne $_.pending_navigation }).Count -ge 1 } 2 "pending navigation")
            $pendingSeen = $true
        } catch { }
        $done = Wait-State $hwnd { param($s) $s.source -like "$url*" -and @($s.tabs | Where-Object { $_.id -eq $s.active_tab })[0].loading -eq $false } $seconds "navigation complete"
        $doneTab = @($done.tabs | Where-Object { $_.id -eq $done.active_tab })[0]
        Add-Result "navigation-$($script:case)" "PASS" "source=$($done.source) loading=false pending_seen=$pendingSeen started_seen=$startedSeen nav_started=$($doneTab.navigation_started) nav_completed=$($doneTab.navigation_completed) requests=$($done.navigation_requests) submit_result=$($done.last_submit_result)"
        return $done
    } catch {
        $state = Read-State $hwnd
        Add-Result "navigation-$($script:case)" "FAIL" "source=$($state.source) requests=$($state.navigation_requests) pending=$(@($state.tabs | Where-Object { $_.id -eq $state.active_tab })[0].pending_navigation)"
        return $null
    }
}
function Start-App([string]$data, [switch]$SecondWindow) {
    Remove-Item $StatusDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $StatusDir, $data | Out-Null
    $env:AMARE_GPUI_DATA_DIR = $data
    $env:AMARE_GPUI_STATUS_DIR = $StatusDir
    Remove-Item Env:AMARE_GPUI_STATUS -ErrorAction SilentlyContinue
    $env:AMARE_GPUI_START_URL = "about:blank"
    $env:AMARE_GPUI_SELF_TEST = "0"
    $env:AMARE_GPUI_RESTORE = "1"
    $env:AMARE_GPUI_DIAGNOSTICS = "1"
    $env:AMARE_GPUI_WINDOW_SIZE = "1044x788"
    if ($SecondWindow) { $env:AMARE_GPUI_OPEN_SECOND_WINDOW = "1" } else { Remove-Item Env:AMARE_GPUI_OPEN_SECOND_WINDOW -ErrorAction SilentlyContinue }
    $outLog = Join-Path $env:TEMP ("amare-nav-" + [guid]::NewGuid().ToString("N") + ".out.log")
    $errLog = Join-Path $env:TEMP ("amare-nav-" + [guid]::NewGuid().ToString("N") + ".err.log")
    $process = Start-Process $Exe -PassThru -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    $script:lastNavErr = $errLog
    $script:app = $process
    $count = if ($SecondWindow) { 2 } else { 1 }
    $states = Wait-StatesAtLeast $count 60 "app windows"
    $hwnd = [IntPtr][int64]$states[0].window_handle
    [void](Force-AmareForeground $hwnd)
    return @($process, $hwnd)
}
function Stop-App($process, [IntPtr]$hwnd) {
    foreach ($window in @([AmareNav]::Windows([int]$process.Id))) {
        [void][AmareNav]::PostMessage($window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    }
    if (-not $process.WaitForExit(15000)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 600
}

$script:app = $null
$script:hwnd = [IntPtr]::Zero
$script:case = "init"
Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $DataDir, $ScreenshotDir | Out-Null
$base = Join-Path $DataDir "nav"
$app = $null
try {
    $started = Start-App $base
    $script:app = $started[0]; $script:hwnd = $started[1]
    [void](Wait-State $script:hwnd { param($s) @($s.tabs | Where-Object { $_.id -eq $s.active_tab -and $_.webview -eq $true }).Count -eq 1 } 20 "initial controller")

    # 1. Immediate submit after creating a new tab (controller not attached yet).
    $script:case = "new-tab-immediate"
    Send-GpuiShortcut $script:hwnd 0x54
    [void](Wait-State $script:hwnd { param($s) @($s.tabs).Count -ge 2 } 10 "new tab")
    [void](Submit-Url $script:hwnd "$BaseUrl`?nav=immediate" 25 -SkipClick)

    # 2. Rapid consecutive submits: the last URL must win.
    $script:case = "rapid-submit"
    $last = "$BaseUrl`?nav=last"
    [void](Submit-Url $script:hwnd "$BaseUrl`?nav=first" 10)
    [void](Submit-Url $script:hwnd $last 25)
    try {
        [void](Wait-State $script:hwnd { param($s) $s.source -eq $last -and @($s.tabs | Where-Object { $_.id -eq $s.active_tab })[0].loading -eq $false } 20 "last URL wins")
        Add-Result "navigation-last-url-wins" "PASS" "source=$last"
    } catch {
        Add-Result "navigation-last-url-wins" "FAIL" "source=$((Read-State $script:hwnd).source)"
    }

    # 3. New window first navigation.
    $script:case = "new-window-first"
    $secondCreated = $false
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        Send-GpuiShortcut $script:hwnd 0x4E
        try { $states = Wait-StatesAtLeast 2 8 "second window"; $secondCreated = $true; break } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $secondCreated) { $states = @(Read-AllStates) }
    $second = $states | Where-Object { [int64]$_.window_handle -ne $script:hwnd.ToInt64() } | Select-Object -First 1
    if ($null -ne $second) {
        $secondHwnd = [IntPtr][int64]$second.window_handle
        [void](Submit-Url $secondHwnd "$BaseUrl`?nav=newwindow" 25)
    } else {
        Add-Result "navigation-new-window-first" "FAIL" "second window missing"
    }

    # 4. Restore first navigation.
    Stop-App $script:app $script:hwnd
    $script:case = "restore-first"
    $started = Start-App $base
    $script:app = $started[0]; $script:hwnd = $started[1]
    [void](Submit-Url $script:hwnd "$BaseUrl`?nav=restored" 30)
    Stop-App $script:app $script:hwnd

    # 5. Resave/restart after a sanitized corrupt fixture.
    $corruptDir = Join-Path $DataDir "corrupt-resave"
    Copy-Item $base $corruptDir -Recurse -Force
    $db = Get-ChildItem $corruptDir -Recurse -Filter amare.db -File | Where-Object { $_.FullName -match 'profiles' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $py = Join-Path $env:TEMP "amare-nav-corrupt.py"
    @"
import sqlite3, sys
c = sqlite3.connect(sys.argv[1])
c.execute("INSERT OR REPLACE INTO settings(key,group_name,value_type,value_text,updated_at_ms) VALUES('gpui.session','browser','json','[{\"window_id\":1}]',0)")
c.commit(); c.close()
"@ | Set-Content -Path $py -Encoding UTF8
    python $py $db.FullName
    if ($LASTEXITCODE -ne 0) { throw "corrupt fixture write failed" }
    $script:case = "corrupt-resave"
    $started = Start-App $corruptDir
    $script:app = $started[0]; $script:hwnd = $started[1]
    [void](Wait-State $script:hwnd { param($s) $null -ne $s.session_error } 15 "sanitized session")
    [void](Submit-Url $script:hwnd "$BaseUrl`?nav=resaved" 30)
    Stop-App $script:app $script:hwnd
    $started = Start-App $corruptDir
    $script:app = $started[0]; $script:hwnd = $started[1]
    try {
        [void](Wait-State $script:hwnd { param($s) $null -eq $s.session_error } 15 "resaved session clean")
        Add-Result "corrupt-resave-restart" "PASS" "sanitized session resaved and restarted without error"
    } catch {
        Add-Result "corrupt-resave-restart" "UNVERIFIED" "session_error=$((Read-State $script:hwnd).session_error)"
    }
} catch {
    Add-Result "fatal" "FAIL" $_.Exception.Message
    if (Test-Path $script:lastNavErr) { Get-Content $script:lastNavErr -Tail 80 | ForEach-Object { Write-Host "STDERR $_" } }
} finally {
    if ($null -ne $script:app -and -not $script:app.HasExited) { try { $script:app.Kill() } catch {} }
    $summary = [pscustomobject]@{
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        results = $script:results
        pass = @($script:results | Where-Object result -eq "PASS").Count
        fail = @($script:results | Where-Object result -eq "FAIL").Count
        unverified = @($script:results | Where-Object result -eq "UNVERIFIED").Count
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ScreenshotDir "gpui-navigation-results.json") -Encoding UTF8
    Write-Host "GPUI navigation summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)"
    if ($summary.fail -gt 0) { exit 1 }
}
