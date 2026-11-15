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
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
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
    $path = Join-Path $ScreenshotDir $name
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        try { if ([AmareSession]::IsIconic($script:hwnd)) { [void][AmareSession]::ShowWindow($script:hwnd, 9) } } catch {}
        Force-Focus $script:hwnd
        Start-Sleep -Milliseconds 400
        $rect = New-Object AmareSession+RECT
        [void][AmareSession]::GetWindowRect($script:hwnd, [ref]$rect)
        $w = [Math]::Max(1, $rect.Right - $rect.Left); $h = [Math]::Max(1, $rect.Bottom - $rect.Top)
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bmp.Size)
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        if ((Get-Item $path).Length -ge 1000) {
            Add-Result $name "PASS" "$path ($w x $h, $((Get-Item $path).Length) bytes)"
            return
        }
        Start-Sleep -Milliseconds 700
    }
    Add-Result $name "UNVERIFIED" "screenshot empty: $path"
    return
}
function Shot-Both([IntPtr]$left, [IntPtr]$right, [string]$name) {
    $r1 = New-Object AmareSession+RECT
    $r2 = New-Object AmareSession+RECT
    [void][AmareSession]::GetWindowRect($left, [ref]$r1)
    [void][AmareSession]::GetWindowRect($right, [ref]$r2)
    $w1 = [Math]::Max(1, $r1.Right - $r1.Left); $h1 = [Math]::Max(1, $r1.Bottom - $r1.Top)
    $w2 = [Math]::Max(1, $r2.Right - $r2.Left); $h2 = [Math]::Max(1, $r2.Bottom - $r2.Top)
    $b1 = New-Object System.Drawing.Bitmap($w1, $h1)
    $b2 = New-Object System.Drawing.Bitmap($w2, $h2)
    $g1 = [System.Drawing.Graphics]::FromImage($b1)
    $g2 = [System.Drawing.Graphics]::FromImage($b2)
    [void](Force-AmareForeground $left); Start-Sleep -Milliseconds 400
    $g1.CopyFromScreen($r1.Left, $r1.Top, 0, 0, $b1.Size)
    [void](Force-AmareForeground $right); Start-Sleep -Milliseconds 400
    $g2.CopyFromScreen($r2.Left, $r2.Top, 0, 0, $b2.Size)
    $canvasW = [Math]::Max($w1, $w2)
    $canvasH = $h1 + $h2 + 8
    $canvas = New-Object System.Drawing.Bitmap($canvasW, $canvasH)
    $gc = [System.Drawing.Graphics]::FromImage($canvas)
    $gc.Clear([System.Drawing.Color]::FromArgb(24, 28, 36))
    $gc.DrawImage($b1, 0, 0, $w1, $h1)
    $gc.DrawImage($b2, 0, $h1 + 8, $w2, $h2)
    $path = Join-Path $ScreenshotDir $name
    $canvas.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $gc.Dispose(); $canvas.Dispose(); $g1.Dispose(); $g2.Dispose(); $b1.Dispose(); $b2.Dispose()
    Add-Result $name "PASS" "$path (composite of two actual window captures, $w1 x $h1 + $w2 x $h2)"
}
function Navigate([string]$url, [int]$timeout = 20) {
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        Force-Focus $script:hwnd
        Click-Client $script:hwnd 420 70
        Send-Keys "^l"; Start-Sleep -Milliseconds 180
        Send-Keys "^a"; Send-Keys "{BACKSPACE}"
        Send-Keys $url; Send-Keys "{ENTER}"
        try {
            return Wait-State { param($s) $s.source -like "$url*" } $timeout "navigation $url"
        } catch {
            if ($attempt -eq 1) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}
function Ensure-Tabs([int]$target) {
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        $state = Read-State
        if (@($state.tabs).Count -ge $target) { return $state }
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
function Get-SessionWindows([System.Diagnostics.Process]$process) {
    return @([AmareSession]::Windows([int]$process.Id))
}
function Wait-SessionWindows([System.Diagnostics.Process]$process, [int]$count, [int]$seconds, [string]$label) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $windows = Get-SessionWindows $process
        if ($windows.Count -eq $count) { return $windows }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for $label (windows=$((Get-SessionWindows $process).Count))"
}
function Get-ProfileDb([string]$root) {
    return Get-ChildItem $root -Recurse -Filter amare.db -File |
        Where-Object { $_.FullName -match 'profiles' } |
        Select-Object -First 1
}
function Set-SessionJson([string]$dataRoot, [string]$json) {
    $db = Get-ProfileDb $dataRoot
    if ($null -eq $db) { throw "profile amare.db missing under $dataRoot" }
    $jsonPath = Join-Path $env:TEMP "amare-session-fixture.json"
    $py = Join-Path $env:TEMP "amare-set-session.py"
    Set-Content -Path $jsonPath -Value $json -Encoding UTF8
    @"
import sqlite3, sys
c = sqlite3.connect(sys.argv[1])
value = open(sys.argv[2], encoding='utf-8').read()
c.execute("INSERT OR REPLACE INTO settings(key, group_name, value_type, value_text, updated_at_ms) VALUES('gpui.session','browser','json',?,0)", (value,))
c.commit()
c.close()
"@ | Set-Content -Path $py -Encoding UTF8
    python $py $db.FullName $jsonPath
    if ($LASTEXITCODE -ne 0) { throw "failed to write session fixture" }
}
function Get-SavedSession([string]$dataRoot) {
    $db = Get-ProfileDb $dataRoot
    if ($null -eq $db) { return $null }
    $py = Join-Path $env:TEMP "amare-read-session.py"
    @"
import sqlite3, sys
c = sqlite3.connect(sys.argv[1])
row = c.execute("SELECT value_text FROM settings WHERE key='gpui.session'").fetchone()
c.close()
if row:
    print(row[0])
"@ | Set-Content -Path $py -Encoding UTF8
    $raw = python $py $db.FullName
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($raw -join ""))) { return $null }
    return (($raw -join "`n") | ConvertFrom-Json)
}
function Get-SessionWindowList($saved) {
    if ($null -eq $saved) { return @() }
    if ($saved.PSObject.Properties.Name -contains "windows") { return @($saved.windows) }
    return @($saved)
}
function Start-App([string]$data, [string]$restore = "1", [switch]$Maximize, [switch]$SecondWindow, [switch]$ForceNormal) {
    Remove-Item $Status -Force -ErrorAction SilentlyContinue
    $env:AMARE_GPUI_DATA_DIR = $data
    $env:AMARE_GPUI_STATUS = $Status
    $env:AMARE_GPUI_START_URL = "about:blank"
    $env:AMARE_GPUI_SELF_TEST = "0"
    $env:AMARE_GPUI_WINDOW_SIZE = "1044x788"
    $env:AMARE_GPUI_DIAGNOSTICS = "1"
    $env:AMARE_GPUI_RESTORE = $restore
    if ($SecondWindow) { $env:AMARE_GPUI_OPEN_SECOND_WINDOW = "1" } else { Remove-Item Env:AMARE_GPUI_OPEN_SECOND_WINDOW -ErrorAction SilentlyContinue }
    $process = Start-Process $Exe -PassThru
    [void](Wait-State { param($s) $null -ne $s.address } 60 "session app start")
    Start-Sleep -Seconds 2
    $process.Refresh(); $script:hwnd = $process.MainWindowHandle
    if ($script:hwnd -eq [IntPtr]::Zero) { throw "session app window missing" }
    [void](Force-AmareForeground $script:hwnd)
    if ($Maximize) {
        [void][AmareSession]::ShowWindow($script:hwnd, 3)
        $deadline = (Get-Date).AddSeconds(8)
        while (-not [AmareSession]::IsZoomed($script:hwnd) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
        if (-not [AmareSession]::IsZoomed($script:hwnd)) { throw "session app did not maximize" }
    } elseif ($ForceNormal) {
        [void][AmareSession]::ShowWindow($script:hwnd, 9)
    }
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
try {
    $serverReady = $false
    for ($i = 0; $i -lt 40; $i++) {
        try {
            $probe = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/fast" -UseBasicParsing -TimeoutSec 2
            if ($probe.StatusCode -eq 200) { $serverReady = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 300
    }
    if ($serverReady) {
        Add-Result "local-session-server-ready" "PASS" "port=$Port status=200"
    } else {
        Add-Result "local-session-server-ready" "FAIL" "port=$Port did not return 200"
        throw "local session server fixture unavailable"
    }
    $fast = "http://127.0.0.1:$Port/fast"
    $scroll = "http://127.0.0.1:$Port/scroll"
    $slow = "http://127.0.0.1:$Port/slow"
    $fail = "http://127.0.0.1:1/"

    # A. Normal windowed restore: three tabs, active URL, scroll, geometry.
    $app = Start-App $completeData -ForceNormal
    [void](Navigate $scroll 20)
    [void](Force-AmareForeground $script:hwnd)
    [void](Scroll-Page)
    [void](Ensure-Tabs 2); [void](Navigate $fast 20)
    [void](Ensure-Tabs 3); [void](Navigate $fast 20)
    Start-Sleep -Seconds 4
    $before = Read-State
    if ($null -eq $before.window_state) { throw "window_state missing before close" }
    Shot "05-session-before-close.png"
    Stop-App $app

    $app = Start-App $completeData
    [void](Wait-State { param($s) $null -ne $s.restore_progress -and $s.restore_progress.total -ge 3 } 20 "restore progress")
    [void](Wait-State { param($s) $s.restore_progress.finished -ge $s.restore_progress.total } 40 "restore completion")
    Start-Sleep -Seconds 1
    Shot "06-session-restore-complete.png"
    Add-Result "session-complete-restore" "PASS" "tabs=$(@((Read-State).tabs).Count)"
    [void](Wait-State {
        param($s)
        @($s.tabs | Where-Object { $_.webview -ne $true -and $null -eq $_.error }).Count -eq 0
    } 20 "restored WebView2 controllers")
    $restored = Read-State
    Add-Result "session-webview-attached" "PASS" "attached=$(@($restored.tabs | Where-Object { $_.webview -eq $true }).Count)"
    try {
        [void](Wait-State {
            param($s)
            $t = @($s.tabs | Where-Object { $_.id -eq $s.active_tab })[0]
            $null -ne $t -and -not [string]::IsNullOrWhiteSpace($s.source) -and $s.source -like "$($t.url)*"
        } 25 "active tab navigation source")
        $restored = Read-State
        $activeRestored = @($restored.tabs | Where-Object { $_.id -eq $restored.active_tab })[0]
        if ($restored.source -ne $before.source) {
            throw "active source changed: before=$($before.source) after=$($restored.source)"
        }
        Add-Result "session-active-tab-navigation" "PASS" "active=$($restored.active_tab) source=$($restored.source) tab_source=$($activeRestored.source)"
    } catch {
        Add-Result "session-active-tab-navigation" "UNVERIFIED" $_.Exception.Message
    }
    if ([AmareSession]::IsZoomed($script:hwnd)) {
        Add-Result "session-windowed-restore" "FAIL" "normal session restored as maximized"
    } else {
        Add-Result "session-windowed-restore" "PASS" "normal window restored windowed"
    }
    $bn = $before.window_state.normal
    $rn = $restored.window_state.normal
    if ($null -ne $bn -and $null -ne $rn) {
        $dw = [Math]::Abs([double]$bn.width - [double]$rn.width)
        $dh = [Math]::Abs([double]$bn.height - [double]$rn.height)
        if ($dw -le 2 -and $dh -le 2) {
            Add-Result "session-window-bounds" "PASS" "normal client $($bn.width)x$($bn.height) -> $($rn.width)x$($rn.height)"
        } else {
            Add-Result "session-window-bounds" "UNVERIFIED" "normal client $($bn.width)x$($bn.height) -> $($rn.width)x$($rn.height) (delta $dw x $dh)"
        }
        Add-Result "session-outer-client-distinction" "PASS" "outer=$($restored.window_state.outer.width)x$($restored.window_state.outer.height) client=$($restored.window_state.client.width)x$($restored.window_state.client.height) normal=$($rn.width)x$($rn.height) scale=$($restored.window_state.scale_factor)"
    } else {
        Add-Result "session-window-bounds" "UNVERIFIED" "window_state.normal missing"
    }
    Shot "16-session-windowed-restored.png"
    $scrollTab = @($restored.tabs | Where-Object { $_.url -like "$scroll*" })[0]
    if ($null -eq $scrollTab) {
        Add-Result "session-scroll-restore" "UNVERIFIED" "scroll tab URL not restored"
    } else {
        try {
            [void](Wait-State { param($s) @($s.tabs | Where-Object { $_.url -like "$scroll*" -and [int64]$_.scroll_y -gt 100 }).Count -ge 1 } 20 "scroll restore")
            $scrollNow = @((Read-State).tabs | Where-Object { $_.url -like "$scroll*" })[0]
            Add-Result "session-scroll-restore" "PASS" "scrollY=$($scrollNow.scroll_y) url=$($scrollNow.url)"
        } catch {
            Add-Result "session-scroll-restore" "UNVERIFIED" "scroll position was not restored"
        }
    }
    Stop-App $app

    # B. Maximized state: maximize before close, verify IsZoomed after restart.
    $maxData = Join-Path $DataDir "maximized"
    New-Item -ItemType Directory -Force -Path $maxData | Out-Null
    $app = Start-App $maxData -Maximize
    [void](Navigate $fast 20)
    Start-Sleep -Seconds 3
    $maxBefore = Read-State
    if ($null -eq $maxBefore.window_state -or -not $maxBefore.window_state.maximized) {
        Add-Result "session-maximized-save" "FAIL" "window_state.maximized was not true before close"
    } else {
        Add-Result "session-maximized-save" "PASS" "saved maximized=true normal=$($maxBefore.window_state.normal.width)x$($maxBefore.window_state.normal.height)"
    }
    Stop-App $app

    $app = Start-App $maxData
    [void](Wait-State { param($s) $null -ne $s.window_state } 20 "restored maximized state")
    Start-Sleep -Seconds 2
    $maxAfter = Read-State
    if ([AmareSession]::IsZoomed($script:hwnd) -and $maxAfter.window_state.maximized) {
        Add-Result "session-maximized-restore" "PASS" "IsZoomed=true maximized=true normal=$($maxAfter.window_state.normal.width)x$($maxAfter.window_state.normal.height)"
    } else {
        Add-Result "session-maximized-restore" "UNVERIFIED" "IsZoomed=$([AmareSession]::IsZoomed($script:hwnd)) maximized=$($maxAfter.window_state.maximized)"
    }
    Shot "12-session-maximized-restored.png"
    if ($null -ne $maxBefore.window_state -and $null -ne $maxAfter.window_state) {
        $bw = [double]$maxBefore.window_state.normal.width
        $bh = [double]$maxBefore.window_state.normal.height
        $rw = [double]$maxAfter.window_state.normal.width
        $rh = [double]$maxAfter.window_state.normal.height
        if ([Math]::Abs($bw - $rw) -le 2 -and [Math]::Abs($bh - $rh) -le 2) {
            Add-Result "session-maximized-normal-bounds" "PASS" "$($bw)x$($bh) -> $($rw)x$($rh)"
        } else {
            Add-Result "session-maximized-normal-bounds" "UNVERIFIED" "$($bw)x$($bh) -> $($rw)x$($rh)"
        }
    }
    Stop-App $app

    # C. Multi-window close/restore with explicit saved snapshot count.
    $multiData = Join-Path $DataDir "multi"
    New-Item -ItemType Directory -Force -Path $multiData | Out-Null
    $app = Start-App $multiData -SecondWindow
    $multiWindows = Wait-SessionWindows $app 2 25 "two initial windows"
    Start-Sleep -Seconds 3
    foreach ($window in (Get-SessionWindows $app)) {
        [void](Force-AmareForeground $window)
        Start-Sleep -Milliseconds 120
        [System.Windows.Forms.SendKeys]::SendWait("%{F4}")
        Start-Sleep -Milliseconds 120
    }
    if (-not $app.WaitForExit(25000)) { throw "multi-window session app did not exit" }
    Start-Sleep -Seconds 2
    $savedMulti = Get-SavedSession $multiData
    $savedWindows = @(Get-SessionWindowList $savedMulti)
    $savedTabs = (@($savedWindows | ForEach-Object { @($_.tabs).Count }) | Measure-Object -Sum).Sum
    if ($savedWindows.Count -eq 2) {
        Add-Result "session-multiwindow-saved" "PASS" "saved windows=2 tabs=$savedTabs revision=$($savedMulti.revision)"
    } else {
        Add-Result "session-multiwindow-saved" "FAIL" "saved windows=$($savedWindows.Count), expected 2 tabs=$savedTabs"
    }
    $app = Start-App $multiData
    try {
        $restoredWindows = Wait-SessionWindows $app 2 25 "restored multi-window session"
        Add-Result "multi-window-session-restore" "PASS" "restored windows=$($restoredWindows.Count)"
    } catch {
        $restoredWindows = Get-SessionWindows $app
        Add-Result "multi-window-session-restore" "UNVERIFIED" "restored windows=$($restoredWindows.Count), expected 2"
    }
    $liveRestored = @(Get-SessionWindows $app)
    if ($liveRestored.Count -ge 2) { Shot-Both $liveRestored[0] $liveRestored[1] "11-multi-window-session-restored.png" }
    foreach ($window in (Get-SessionWindows $app)) {
        [void](Force-AmareForeground $window)
        Start-Sleep -Milliseconds 120
        [System.Windows.Forms.SendKeys]::SendWait("%{F4}")
        Start-Sleep -Milliseconds 120
    }
    if (-not $app.WaitForExit(25000)) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }

    # D. Failure restore: one slow tab and one unreachable tab.
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

    # E. Corrupt/partial/empty session fallback from a copied profile DB.
    $corruptData = Join-Path $DataDir "corrupt"
    Copy-Item $completeData $corruptData -Recurse -Force
    Set-SessionJson $corruptData 'null'
    $app = Start-App $corruptData
    try {
        $errorState = Wait-State { param($s) $null -ne $s.session_error } 15 "corrupt session error"
        $safeWindows = Get-SessionWindows $app
        Add-Result "corrupt-session-fallback" "PASS" "error=$($errorState.session_error) windows=$($safeWindows.Count)"
        Shot "09-session-corrupt-fallback.png"
    } catch {
        Add-Result "corrupt-session-fallback" "UNVERIFIED" $_.Exception.Message
        try { Shot "09-session-corrupt-fallback.png" } catch {}
    }
    if (-not $app.HasExited) { try { Stop-App $app } catch { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue } }

    $partialData = Join-Path $DataDir "partial"
    Copy-Item $completeData $partialData -Recurse -Force
    $partialJson = '{"schema":2,"revision":9,"windows":[{"revision":1,"window_id":0,"x":0,"y":0,"width":1024,"height":720,"maximized":false,"active":0,"tabs":[{"url":"about:blank","title":"bad"}]},{"revision":2,"window_id":7,"x":10,"y":10,"width":1024,"height":720,"maximized":false,"active":9,"tabs":[{"url":"","title":"","scroll_x":-1,"scroll_y":-5}]}]}'
    Set-SessionJson $partialData $partialJson
    $app = Start-App $partialData
    try {
        $partial = Wait-State { param($s) $null -ne $s.session_error -and $null -ne $s.address } 15 "partial session fallback"
        $windowCount = (Get-SessionWindows $app).Count
        $active = @($partial.tabs | Where-Object { $_.id -eq $partial.active_tab })[0]
        if ($windowCount -ne 1 -or $active.url -ne "about:blank" -or [int]$partial.active_tab -ne [int]$active.id) {
            throw "partial fallback did not sanitize window/tab/active"
        }
        Add-Result "corrupt-metadata-fallback" "PASS" "windows=$windowCount active=$($partial.active_tab) url=$($active.url) error=$($partial.session_error)"
    } catch {
        Add-Result "corrupt-metadata-fallback" "UNVERIFIED" $_.Exception.Message
    }
    if (-not $app.HasExited) { try { Stop-App $app } catch { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue } }

    $emptyData = Join-Path $DataDir "empty"
    Copy-Item $completeData $emptyData -Recurse -Force
    Set-SessionJson $emptyData '[]'
    $app = Start-App $emptyData
    Start-Sleep -Seconds 2
    $emptyState = Read-State
    if ($null -ne $emptyState -and $null -eq $emptyState.session_error -and (Get-SessionWindows $app).Count -eq 1) {
        Add-Result "empty-session" "PASS" "one safe new window, no error banner"
    } else {
        Add-Result "empty-session" "UNVERIFIED" "empty session did not start cleanly"
    }
    Stop-App $app

    $dbCorruptData = Join-Path $DataDir "db-corrupt"
    Copy-Item $completeData $dbCorruptData -Recurse -Force
    $dbCorrupt = Get-ProfileDb $dbCorruptData
    if ($null -ne $dbCorrupt) { Set-Content -Path $dbCorrupt.FullName -Value "not a sqlite database" -NoNewline -Encoding ASCII }
    $app = Start-App $dbCorruptData
    try {
        $dbState = Wait-State { param($s) $null -ne $s.address } 20 "corrupt DB fallback"
        $windows = (Get-SessionWindows $app).Count
        Add-Result "corrupt-db-fallback" "PASS" "app started with corrupt DB; windows=$windows error=$($dbState.session_error)"
    } catch {
        Add-Result "corrupt-db-fallback" "UNVERIFIED" $_.Exception.Message
    }
    if (-not $app.HasExited) { try { Stop-App $app } catch { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue } }
    } catch {
    Add-Result "fatal" "FAIL" $_.Exception.Message
    try {
        $trace = (Read-State).diagnostics
        if ($null -ne $trace) { $trace | Select-Object -Last 40 | ForEach-Object { Write-Host "TRACE $_" } }
    } catch {}
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
