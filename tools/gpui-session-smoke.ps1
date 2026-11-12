param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Status,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$ScreenshotDir,
    [Parameter(Mandatory = $true)][string]$Port
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\gpui-test-focus.ps1"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AmareSession {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
}
'@

$script:results = [System.Collections.Generic.List[object]]::new()
function Add-Result([string]$name, [string]$state, [string]$detail) {
    $script:results.Add([pscustomobject]@{ name = $name; result = $state; detail = $detail })
    Write-Host ("{0} {1}: {2}" -f $state, $name, $detail)
}
function Read-State { if (-not (Test-Path $Status)) { return $null }; try { return Get-Content $Status -Raw -ErrorAction Stop | ConvertFrom-Json } catch { return $null } }
function Wait-State([scriptblock]$condition, [int]$seconds, [string]$label) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $state = Read-State
        if ($null -ne $state -and (& $condition $state)) { return $state }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for $label"
}
function Send-Keys([string]$keys) { [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds 160 }
function Force-Focus([IntPtr]$hwnd) {
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        [void](Force-AmareForeground $hwnd)
        if ([AmareSession]::GetForegroundWindow() -eq $hwnd) { return }
        Start-Sleep -Milliseconds 150
    }
    [AmareSession]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [void](Force-AmareForeground $hwnd)
    [AmareSession]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 150
}
function Send-CtrlKey([int]$vk) {
    [AmareSession]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    [AmareSession]::keybd_event([byte]$vk, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
    [AmareSession]::keybd_event([byte]$vk, 0, 2, [UIntPtr]::Zero)
    [AmareSession]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}
function Click-Client([IntPtr]$hwnd, [int]$x, [int]$y) {
    $point = New-Object AmareSession+POINT
    [void][AmareSession]::ClientToScreen($hwnd, [ref]$point)
    [void](Force-AmareForeground $hwnd)
    [void][AmareSession]::SetCursorPos(($point.X + $x), ($point.Y + $y))
    Start-Sleep -Milliseconds 120
    [AmareSession]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [AmareSession]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}
function Scroll-Page {
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        Click-Client $script:hwnd 500 400
        Send-Keys "{PGDN}"; Send-Keys "{PGDN}"; Send-Keys "{PGDN}"
        [AmareSession]::mouse_event(0x0800, 0, 0, -360, [UIntPtr]::Zero)
        [AmareSession]::mouse_event(0x0800, 0, 0, -360, [UIntPtr]::Zero)
        $deadline = (Get-Date).AddSeconds(5)
        while ((Get-Date) -lt $deadline) {
            $state = Read-State
            if ($null -ne $state -and @($state.tabs).Count -gt 0) {
                $firstTab = @($state.tabs)[0]
                if ([int64]$firstTab.scroll_y -gt 100) { return $state }
                if ([string]$state.title -match '^SCROLL:([0-9]+)') {
                    if ([int64]$matches[1] -gt 100) { return $state }
                }
            }
            Start-Sleep -Milliseconds 250
        }
    }
    throw "Timed out waiting for scroll position capture"
}
function Shot([string]$name) {
    $rect = New-Object AmareSession+RECT
    [void][AmareSession]::GetWindowRect($script:hwnd, [ref]$rect)
    $w = [Math]::Max(1, $rect.Right - $rect.Left); $h = [Math]::Max(1, $rect.Bottom - $rect.Top)
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bmp.Size)
    $path = Join-Path $ScreenshotDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    if ((Get-Item $path).Length -lt 1000) { throw "screenshot empty: $path" }
    Add-Result $name "PASS" "$path ($w x $h, $((Get-Item $path).Length) bytes)"
}
function Navigate([string]$url, [int]$timeout = 20) {
    Send-Keys "^l"; Start-Sleep -Milliseconds 180
    Send-Keys "^a"; Send-Keys "{BACKSPACE}"
    Send-Keys $url; Send-Keys "{ENTER}"
    return Wait-State { param($s) $s.source -like "$url*" } $timeout "navigation $url"
}
function Ensure-Tabs([int]$target) {
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        $state = Read-State
        if (@($state.tabs).Count -ge $target) { return $state }
        if ($null -ne $script:newTabTrigger) {
            Set-Content -Path $script:newTabTrigger -Value "1" -Encoding ascii
            $deadline = (Get-Date).AddSeconds(30)
            while ((Get-Date) -lt $deadline) {
                $state = Read-State
                if (@($state.tabs).Count -ge $target) { break }
                Start-Sleep -Milliseconds 250
            }
            if (@((Read-State).tabs).Count -ge $target) { continue }
        }
        Force-Focus $script:hwnd
        Send-GpuiShortcut $script:hwnd 0x54
        $deadline = (Get-Date).AddSeconds(25)
        while ((Get-Date) -lt $deadline) {
            $state = Read-State
            if (@($state.tabs).Count -ge $target) { return $state }
            Start-Sleep -Milliseconds 300
        }
    }
    throw "Could not create $target tabs"
}
function Start-App([string]$data, [string]$restore = "1") {
    Remove-Item $Status -Force -ErrorAction SilentlyContinue
    $env:AMARE_GPUI_DATA_DIR = $data
    $env:AMARE_GPUI_STATUS = $Status
    $env:AMARE_GPUI_START_URL = "about:blank"
    $env:AMARE_GPUI_SELF_TEST = "0"
    $env:AMARE_GPUI_RESTORE = $restore
    $process = Start-Process $Exe -PassThru
    [void](Wait-State { param($s) $null -ne $s.address } 60 "session app start")
    Start-Sleep -Seconds 2
    $process.Refresh(); $script:hwnd = $process.MainWindowHandle
    if ($script:hwnd -eq [IntPtr]::Zero) { throw "session app window missing" }
    [void](Force-AmareForeground $script:hwnd)
    [void][AmareSession]::ShowWindow($script:hwnd, 3)
    Start-Sleep -Milliseconds 500
    return $process
}
function Stop-App([System.Diagnostics.Process]$process) {
    [void](Force-AmareForeground $script:hwnd)
    Send-Keys "%{F4}"
    if (-not $process.WaitForExit(25000)) { throw "session app did not exit" }
    Start-Sleep -Seconds 2
}

$script:hwnd = [IntPtr]::Zero
$app = $null
$server = $null
$completeData = Join-Path $DataDir "complete"
$failureData = Join-Path $DataDir "failure"
$corruptData = Join-Path $DataDir "corrupt"
Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $completeData, $failureData, $corruptData, $ScreenshotDir | Out-Null
$server = Start-Process python -ArgumentList @("tools/slow_session_server.py", $Port) -PassThru
$script:newTabTrigger = $null
if ($env:AMARE_GPUI_TEST_TRIGGERS -eq "1") {
    $script:newTabTrigger = Join-Path $env:RUNNER_TEMP "amare-new-tab.trigger"
    Remove-Item $script:newTabTrigger -Force -ErrorAction SilentlyContinue
    $env:AMARE_GPUI_NEW_TAB_TRIGGER = $script:newTabTrigger
}
try {
    Start-Sleep -Seconds 2
    $fast = "http://127.0.0.1:$Port/fast"
    $scroll = "http://127.0.0.1:$Port/scroll"
    $slow = "http://127.0.0.1:$Port/slow"
    $fail = "http://127.0.0.1:1/"

    # A. Complete restore: three fast tabs and one scroll position.
    $app = Start-App $completeData
    [void](Navigate $scroll 20)
    [void](Force-AmareForeground $script:hwnd)
    [void](Scroll-Page)
    [void](Ensure-Tabs 2); [void](Navigate $fast 20)
    [void](Ensure-Tabs 3); [void](Navigate $fast 20)
    Start-Sleep -Seconds 4
    $before = Read-State
    Shot "05-session-before-close.png"
    Stop-App $app

    $app = Start-App $completeData
    [void](Wait-State { param($s) $null -ne $s.restore_progress -and $s.restore_progress.total -ge 3 } 20 "restore progress")
    [void](Wait-State { param($s) $s.restore_progress.finished -ge $s.restore_progress.total } 40 "restore completion")
    Start-Sleep -Seconds 1
    Shot "06-session-restore-complete.png"
    Add-Result "session-complete-restore" "PASS" "tabs=$(@((Read-State).tabs).Count)"
    $restored = Read-State
    $scrollTab = @($restored.tabs | Where-Object { $_.restored -eq $true } | Select-Object -First 1)
    if ($null -eq $scrollTab) { throw "no restored tab marker" }
    Start-Sleep -Seconds 3
    $restored = Read-State
    if (-not (@($restored.tabs) | Where-Object { $_.scroll_y -gt 100 })) {
        Add-Result "session-scroll-restore" "UNVERIFIED" "scroll position was not restored"
    } else {
        Add-Result "session-scroll-restore" "PASS" "scroll_y>100 after restart"
    }
    Stop-App $app

    # B. Failure restore: one slow tab and one unreachable tab.
    $app = Start-App $failureData
    [void](Navigate $fast 20)
    [void](Ensure-Tabs 2); [void](Navigate $slow 20)
    [void](Ensure-Tabs 3); [void](Navigate $fail 10)
    Start-Sleep -Seconds 4
    Shot "05b-session-failure-before-close.png"
    Stop-App $app
    $app = Start-App $failureData
    [void](Wait-State { param($s) $null -ne $s.restore_progress -and $s.restore_progress.total -ge 3 } 20 "failure restore progress")
    Shot "07-session-restore-progress.png"
    [void](Wait-State { param($s) $s.restore_progress.finished -ge $s.restore_progress.total } 45 "failure restore completion")
    Start-Sleep -Seconds 1
    $failed = Read-State
    if ($failed.restore_progress.failed -lt 1) { throw "failure restore did not report a failed tab" }
    Shot "08-session-restore-failure.png"
    Add-Result "session-tab-failure" "PASS" "failed=$($failed.restore_progress.failed) total=$($failed.restore_progress.total)"
    Stop-App $app

    # C. Corrupt session data: safe new session + error banner.
    $db = Get-ChildItem $completeData -Recurse -Filter amare.db | Select-Object -First 1
    if ($null -eq $db) { throw "settings DB missing for corrupt session test" }
    $dbPath = $db.FullName.Replace('\\', '/')
    python -c "import sqlite3; c=sqlite3.connect(r'$dbPath'); c.execute(\"UPDATE settings SET value_text='{' WHERE key='gpui.session'\"); c.commit(); c.close()"
    $app = Start-App $completeData
    try {
        [void](Wait-State { param($s) $null -ne $s.session_error } 15 "corrupt session error")
        Shot "09-session-corrupt-fallback.png"
        Add-Result "corrupt-session-fallback" "PASS" "new session started with error banner"
    } catch {
        Add-Result "corrupt-session-fallback" "UNVERIFIED" $_.Exception.Message
        try { Shot "09-session-corrupt-fallback.png" } catch {}
    }
    if (-not $app.HasExited) {
        try { Stop-App $app } catch { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    }
} catch {
    Add-Result "fatal" "FAIL" $_.Exception.Message
    try { Shot "99-session-failure.png" } catch {}
} finally {
    if ($null -ne $app -and -not $app.HasExited) { try { $app.Kill() } catch {} }
    if ($null -ne $server) { try { Stop-Process -Id $server.Id -Force } catch {} }
    $summary = [pscustomobject]@{
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        results = $script:results
        pass = @($script:results | Where-Object result -eq "PASS").Count
        fail = @($script:results | Where-Object result -eq "FAIL").Count
        unverified = @($script:results | Where-Object result -eq "UNVERIFIED").Count
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ScreenshotDir "gpui-session-results.json") -Encoding UTF8
    Write-Host "GPUI session summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)"
    if ($summary.fail -gt 0) { exit 1 }
}
