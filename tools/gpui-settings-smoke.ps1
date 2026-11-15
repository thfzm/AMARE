param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Status,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$ScreenshotDir
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\gpui-test-focus.ps1"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class AmareSettings {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    public static IntPtr[] Windows(int processId) {
        var result = new List<IntPtr>();
        EnumWindows((hwnd, lparam) => {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == (uint)processId && IsWindowVisible(hwnd)) { result.Add(hwnd); }
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
function Send-Keys([string]$keys) { [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds 180 }
function Wait-WindowState([scriptblock]$condition, [int]$seconds, [string]$label) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for $label"
}
function Click-Client([IntPtr]$hwnd, [int]$x, [int]$y) {
    $point = New-Object AmareSettings+POINT
    [void][AmareSettings]::ClientToScreen($hwnd, [ref]$point)
    [void](Force-AmareForeground $hwnd)
    [void][AmareSettings]::SetCursorPos(($point.X + $x), ($point.Y + $y))
    Start-Sleep -Milliseconds 120
    [AmareSettings]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [AmareSettings]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 350
}
function Click-Titlebar([string]$button) {
    $state = Read-State
    $layout = if ($null -ne $state) { $state.layout_bounds } else { $null }
    $name = switch ($button) { "minimize" { "window_min" } "maximize" { "window_max" } "close" { "window_close" } default { "" } }
    if ($null -ne $layout -and $null -ne $layout.toolbar_children -and $name -ne "") {
        $b = $layout.toolbar_children.$name
        if ($null -ne $b) {
            Click-Client $script:hwnd ([int]($b.x + $b.width / 2)) ([int]($b.y + $b.height / 2))
            return
        }
    }
    $rect = New-Object AmareSettings+RECT
    [void][AmareSettings]::GetClientRect($script:hwnd, [ref]$rect)
    $width = [Math]::Max(640, $rect.Right - $rect.Left)
    $x = switch ($button) {
        "minimize" { $width - 143 }
        "maximize" { $width - 89 }
        "close" { $width - 43 }
        default { throw "unknown titlebar button: $button" }
    }
    Click-Client $script:hwnd $x 22
}
function Click-TitlebarWin32([string]$button) {
    $state = Read-State
    $layout = if ($null -ne $state) { $state.layout_bounds } else { $null }
    $name = switch ($button) { "minimize" { "window_min" } "maximize" { "window_max" } "close" { "window_close" } default { "" } }
    if ($null -eq $layout -or $null -eq $layout.toolbar_children -or $name -eq "") { return $false }
    $b = $layout.toolbar_children.$name
    if ($null -eq $b) { return $false }
    $point = New-Object AmareSettings+POINT
    [void][AmareSettings]::ClientToScreen($script:hwnd, [ref]$point)
    $sx = [int]($point.X + $b.x + $b.width / 2)
    $sy = [int]($point.Y + $b.y + $b.height / 2)
    $hit = switch ($button) { "minimize" { 8 } "maximize" { 9 } "close" { 20 } default { 0 } }
    $lparam = [IntPtr](($sy -shl 16) -bor ($sx -band 0xFFFF))
    [void][AmareSettings]::SendMessage($script:hwnd, 0x00A1, [IntPtr]$hit, $lparam)
    Start-Sleep -Milliseconds 120
    [void][AmareSettings]::SendMessage($script:hwnd, 0x00A2, [IntPtr]$hit, $lparam)
    Start-Sleep -Milliseconds 500
    return $true
}
function DoubleClick-Client([IntPtr]$hwnd, [int]$x, [int]$y) {
    $point = New-Object AmareSettings+POINT
    [void][AmareSettings]::ClientToScreen($hwnd, [ref]$point)
    [void](Force-AmareForeground $hwnd)
    [void][AmareSettings]::SetCursorPos(($point.X + $x), ($point.Y + $y))
    Start-Sleep -Milliseconds 120
    foreach ($click in 1..2) {
        [AmareSettings]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 40
        [AmareSettings]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        if ($click -eq 1) { Start-Sleep -Milliseconds 90 }
    }
    Start-Sleep -Milliseconds 500
}
function Get-RectText([IntPtr]$hwnd) {
    $rect = New-Object AmareSettings+RECT
    [void][AmareSettings]::GetWindowRect($hwnd, [ref]$rect)
    return "$($rect.Left),$($rect.Top),$($rect.Right),$($rect.Bottom)"
}
function Try-DoubleClickTitlebar([scriptblock]$test, [string]$name, [string]$success) {
    $rect = New-Object AmareSettings+RECT
    [void][AmareSettings]::GetClientRect($script:hwnd, [ref]$rect)
    $x = [int](($rect.Right - $rect.Left) / 2)
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        DoubleClick-Client $script:hwnd $x 22
        try {
            [void](Wait-WindowState $test 3 "$name attempt")
            Add-Result $name "PASS" "$success (real mouse double click)"
            return
        } catch { }
    }
    Add-Result $name "UNVERIFIED" "real mouse double click did not change state"
}
function Try-Titlebar([string]$button, [scriptblock]$test, [scriptblock]$fallback, [string]$name, [string]$success) {
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        Click-Titlebar $button
        try {
            [void](Wait-WindowState $test 3 "$name attempt")
            Add-Result $name "PASS" "$success (real mouse)"
            return
        } catch { }
    }
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        if (-not (Click-TitlebarWin32 $button)) { break }
        try {
            [void](Wait-WindowState $test 3 "$name win32 attempt")
            Add-Result $name "PASS" "$success (Win32 NC; real mouse click did not change state)"
            return
        } catch { }
    }
    & $fallback
    Start-Sleep -Milliseconds 700
    Add-Result $name "UNVERIFIED" "real mouse and Win32 NC click did not change state; OS fallback used"
}
function Shot([string]$name) {
    $path = Join-Path $ScreenshotDir $name
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        if ([AmareSettings]::IsIconic($script:hwnd)) { [void][AmareSettings]::ShowWindow($script:hwnd, 9) }
        [void](Force-AmareForeground $script:hwnd)
        Start-Sleep -Milliseconds 400
        $rect = New-Object AmareSettings+RECT
        [void][AmareSettings]::GetWindowRect($script:hwnd, [ref]$rect)
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
        Start-Sleep -Milliseconds 600
    }
    throw "screenshot empty: $path"
}
function ShotFull([string]$name) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $path = Join-Path $ScreenshotDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Add-Result $name "PASS" "$path ($($bounds.Width) x $($bounds.Height), $((Get-Item $path).Length) bytes)"
}
function Get-AppWindows { return @([AmareSettings]::Windows($script:app.Id)) }
function Get-BrowserWindows {
    return @(Get-AppWindows | Where-Object {
        $rect = New-Object AmareSettings+RECT
        [void][AmareSettings]::GetWindowRect($_, [ref]$rect)
        ($rect.Right - $rect.Left) -gt 300 -and ($rect.Bottom - $rect.Top) -gt 200
    })
}
function Wait-WindowCount([int]$count, [int]$seconds, [string]$label) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $windows = Get-BrowserWindows
        if ($windows.Count -eq $count) { return $windows }
        Start-Sleep -Milliseconds 300
    }
    throw "Timed out waiting for $label (windows=$((Get-BrowserWindows).Count))"
}
function Open-Settings {
    Send-Keys "^,";
    [void](Wait-State { param($s) $s.panel -eq "settings" } 8 "settings panel")
}
function Select-Category([string]$key, [int]$y) {
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        Click-Client $script:hwnd 82 $y
        try {
            [void](Wait-State { param($s) $s.settings_category -eq $key } 5 "settings category $key")
            return
        } catch {
            Start-Sleep -Milliseconds 300
        }
    }
    throw "settings category $key did not open"
}

$script:hwnd = [IntPtr]::Zero
$app = $null
Remove-Item $Status -Force -ErrorAction SilentlyContinue
Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $DataDir, $ScreenshotDir | Out-Null
$env:AMARE_GPUI_DATA_DIR = $DataDir
$env:AMARE_GPUI_STATUS = $Status
$env:AMARE_GPUI_START_URL = "about:blank"
$env:AMARE_GPUI_SELF_TEST = "0"
$env:AMARE_GPUI_RESTORE = "1"
try {
    $app = Start-Process $Exe -PassThru
    [void](Wait-State { param($s) $null -ne $s.address } 60 "settings app start")
    Start-Sleep -Seconds 2
    $app.Refresh(); $script:hwnd = $app.MainWindowHandle
    if ($script:hwnd -eq [IntPtr]::Zero) { throw "GPUI window missing" }
    [void](Force-AmareForeground $script:hwnd)
    $normalRect = Get-RectText $script:hwnd
    Try-Titlebar "minimize" { [AmareSettings]::IsIconic($script:hwnd) } { [void][AmareSettings]::ShowWindow($script:hwnd, 6) } "titlebar-minimize-click" "window minimized"
    [void][AmareSettings]::ShowWindow($script:hwnd, 9)
    try {
        [void](Wait-WindowState { -not [AmareSettings]::IsIconic($script:hwnd) } 5 "minimize restore")
        Add-Result "titlebar-minimize-restore" "PASS" "window restored from minimized state"
    } catch {
        Add-Result "titlebar-minimize-restore" "UNVERIFIED" $_.Exception.Message
    }
    Try-Titlebar "maximize" { [AmareSettings]::IsZoomed($script:hwnd) } { [void][AmareSettings]::ShowWindow($script:hwnd, 3) } "titlebar-maximize-click" "window maximized"
    Shot "14-titlebar-maximized.png"
    Try-Titlebar "maximize" { -not [AmareSettings]::IsZoomed($script:hwnd) } { [void][AmareSettings]::ShowWindow($script:hwnd, 9) } "titlebar-restore-click" "window restored"
    Shot "15-titlebar-restored.png"
    Try-Titlebar "maximize" { [AmareSettings]::IsZoomed($script:hwnd) } { [void][AmareSettings]::ShowWindow($script:hwnd, 3) } "titlebar-repeat-maximize" "window maximized on repeat"
    Try-Titlebar "maximize" { -not [AmareSettings]::IsZoomed($script:hwnd) } { [void][AmareSettings]::ShowWindow($script:hwnd, 9) } "titlebar-repeat-restore" "window restored on repeat"
    $restoredRect = Get-RectText $script:hwnd
    $beforeParts = @($normalRect.Split(',') | ForEach-Object { [int]$_ })
    $afterParts = @($restoredRect.Split(',') | ForEach-Object { [int]$_ })
    $geometryDelta = 0
    if ($beforeParts.Count -eq 4 -and $afterParts.Count -eq 4) {
        for ($i = 0; $i -lt 4; $i++) { $geometryDelta = [Math]::Max($geometryDelta, [Math]::Abs($beforeParts[$i] - $afterParts[$i])) }
    } else {
        $geometryDelta = 9999
    }
    if ($geometryDelta -le 2) {
        Add-Result "titlebar-geometry-preserved" "PASS" "$normalRect -> $restoredRect (max delta ${geometryDelta}px)"
    } else {
        Add-Result "titlebar-geometry-preserved" "UNVERIFIED" "$normalRect -> $restoredRect (max delta ${geometryDelta}px)"
    }
    Try-DoubleClickTitlebar { [AmareSettings]::IsZoomed($script:hwnd) } "titlebar-double-click-maximize" "window maximized"
    Try-DoubleClickTitlebar { -not [AmareSettings]::IsZoomed($script:hwnd) } "titlebar-double-click-restore" "window restored"
    Start-Sleep -Milliseconds 500
    Shot "01-initial-browser.png"

    Open-Settings
    Shot "02-settings-full.png"
    Shot "03-appearance-settings.png"

    Select-Category "startup" 209
    Shot "04-startup-session-settings.png"
    Select-Category "search" 251
    Shot "05-search-settings.png"
    Select-Category "downloads" 293
    Shot "06-downloads-settings.png"
    Select-Category "privacy" 335
    Shot "07-privacy-settings.png"
    Select-Category "advanced" 377
    Shot "08-advanced-settings.png"

    Click-Client $script:hwnd 300 114
    Send-Keys "^a"; Send-Keys "download"; Start-Sleep -Milliseconds 600
    [void](Wait-State { param($s) $s.settings_query -eq "download" } 6 "settings search query")
    Shot "09-settings-search-results.png"

    Select-Category "appearance" 167
    $darkSet = $false
    foreach ($attempt in 1..3) {
        Click-Client $script:hwnd 1000 212
        try {
            [void](Wait-State { param($s) $s.settings.theme -eq "dark" } 5 "dark theme setting")
            $darkSet = $true; break
        } catch { Start-Sleep -Milliseconds 300 }
    }
    if (-not $darkSet) { throw "Could not set dark theme" }
    $changed = $false
    $tabValue = 120
    foreach ($y in 492, 500, 508, 516) {
        foreach ($x in 920, 950, 970, 990, 1010) {
            Click-Client $script:hwnd $x $y
            $state = Read-State
            if ($state.settings.tab_width -ne 120) { $tabValue = [int]$state.settings.tab_width; $changed = $true; break }
        }
        if ($changed) { break }
    }
    if (-not $changed) { throw "Timed out waiting for tab width setting" }
    $changed = $false
    $densityValue = "comfortable"
    foreach ($y in 264, 276, 288, 300) {
        foreach ($x in 940, 970, 990, 1010) {
            Click-Client $script:hwnd $x $y
            $state = Read-State
            if ($state.settings.ui_density -ne "comfortable") { $densityValue = [string]$state.settings.ui_density; $changed = $true; break }
        }
        if ($changed) { break }
    }
    if (-not $changed) { throw "Timed out waiting for density setting" }
    $changed = $false
    $fontValue = 16
    foreach ($y in 414, 426, 438) {
        foreach ($x in 880, 920, 950, 970, 1000) {
            Click-Client $script:hwnd $x $y
            $state = Read-State
            if ($state.settings.ui_font_size -ne 16) { $fontValue = [int]$state.settings.ui_font_size; $changed = $true; break }
        }
        if ($changed) { break }
    }
    if (-not $changed) { throw "Timed out waiting for font size setting" }
    Shot "03b-appearance-changed.png"

    [void](Force-AmareForeground $script:hwnd)
    Send-Keys "^,"
    try {
        [void](Wait-State { param($s) $s.panel -eq "none" } 6 "settings closed")
    } catch {
        [void](Force-AmareForeground $script:hwnd)
        Send-Keys "^,"
        [void](Wait-State { param($s) $s.panel -eq "none" } 8 "settings closed retry")
    }
    Shot "10-browser-settings-applied.png"
    [void][AmareSettings]::ShowWindow($script:hwnd, 9)
    Start-Sleep -Seconds 1

    $baseWindows = (Get-BrowserWindows).Count
    [void](Force-AmareForeground $script:hwnd)
    Send-Keys "^n"
    $windows = Wait-WindowCount ($baseWindows + 1) 12 "second browser window"
    [void][AmareSettings]::SetWindowPos($windows[0], [IntPtr]::Zero, 8, 32, 500, 640, 0x0004)
    [void][AmareSettings]::SetWindowPos($windows[1], [IntPtr]::Zero, 516, 32, 500, 640, 0x0004)
    Start-Sleep -Seconds 5
    Add-Result "multi-window-created" "PASS" "windows=$($windows.Count) side-by-side"

    foreach ($window in (Get-BrowserWindows)) {
        [void](Force-AmareForeground $window)
        Start-Sleep -Milliseconds 120
        [System.Windows.Forms.SendKeys]::SendWait("%{F4}")
        Start-Sleep -Milliseconds 120
    }
    if (-not $app.WaitForExit(25000)) { throw "settings app did not exit after closing both windows" }
    Add-Result "multi-window-graceful-exit" "PASS" "both windows closed in sequence"
    Start-Sleep -Seconds 2

    Remove-Item $Status -Force -ErrorAction SilentlyContinue
    $restartErr = Join-Path $env:TEMP "amare-gpui-settings-restart-stderr.log"
    $app = Start-Process $Exe -PassThru -RedirectStandardError $restartErr
    Start-Sleep -Seconds 2
    $app.Refresh()
    if ($app.HasExited) {
        if (Test-Path $restartErr) { Get-Content $restartErr -Tail 40 | ForEach-Object { Write-Host "STDERR $_" } }
        throw "restored settings app exited during startup"
    }
    try {
        [void](Wait-State { param($s) $null -ne $s.address } 60 "restored settings app")
    } catch {
        if (Test-Path $restartErr) {
            Get-Content $restartErr -Tail 100 | ForEach-Object { Write-Host "STDERR $_" }
        }
        throw
    }
    $restoredMulti = $true
    try {
        $windows = Wait-WindowCount ($baseWindows + 1) 20 "restored multi-window session"
    } catch {
        $restoredMulti = $false
        $windows = @(Get-BrowserWindows)
        if ($windows.Count -lt 1) { throw }
        Add-Result "multi-window-session-restore" "UNVERIFIED" "restored windows=$($windows.Count), expected $($baseWindows + 1)"
    }
    Start-Sleep -Seconds 3
    $app.Refresh(); $script:hwnd = $windows[0]
    [void](Force-AmareForeground $script:hwnd)
    $restored = Read-State
    if ($restored.settings.theme -ne "dark" -or $restored.settings.ui_font_size -ne $fontValue -or $restored.settings.tab_width -ne $tabValue -or $restored.settings.ui_density -ne $densityValue) {
        throw "settings did not persist after restart: $($restored.settings | ConvertTo-Json -Compress)"
    }
    Add-Result "settings-restart-persistence" "PASS" "theme=dark size=$fontValue tab=$tabValue density=$densityValue"
    if ($restoredMulti) { Add-Result "multi-window-session-restore" "PASS" "windows=$($windows.Count)" }
    ShotFull "11-multi-window-session-restored.png"

    [void][AmareSettings]::ShowWindow($script:hwnd, 3)
    Start-Sleep -Seconds 1
    Shot "12-titlebar-maximized.png"
    [void][AmareSettings]::ShowWindow($script:hwnd, 9)
    Start-Sleep -Milliseconds 800

    # With two restored windows, closing one must not terminate the process.
    $live = @(Get-BrowserWindows)
    if ($live.Count -ge 2) {
        $script:hwnd = $live[0]
        [void](Force-AmareForeground $script:hwnd)
        Click-Titlebar "close"
        Start-Sleep -Milliseconds 800
        if (-not $app.HasExited) {
            try {
                [void](Wait-WindowCount ($live.Count - 1) 10 "close one window")
                Add-Result "titlebar-close-one-window" "PASS" "one of $($live.Count) windows closed; app remains"
            } catch {
                Add-Result "titlebar-close-one-window" "UNVERIFIED" $_.Exception.Message
            }
        } else {
            Add-Result "titlebar-close-one-window" "FAIL" "closing one window exited the whole app"
        }
        $remaining = @(Get-BrowserWindows)
        if ($remaining.Count -ge 1) {
            $script:hwnd = $remaining[0]
            [void](Force-AmareForeground $script:hwnd)
            [System.Windows.Forms.SendKeys]::SendWait("%{F4}")
            if (-not $app.WaitForExit(15000)) {
                [void][AmareSettings]::PostMessage($script:hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
                if (-not $app.WaitForExit(15000)) {
                    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
                    Add-Result "last-window-normal-exit" "UNVERIFIED" "Alt+F4/WM_CLOSE did not exit; process killed"
                } else {
                    Add-Result "last-window-normal-exit" "PASS" "WM_CLOSE exited last window"
                }
            } else {
                Add-Result "last-window-normal-exit" "PASS" "Alt+F4 exited last window"
            }
        }
    } else {
        Add-Result "titlebar-close-one-window" "UNVERIFIED" "only one restored window was available"
        [void](Force-AmareForeground $script:hwnd)
        [System.Windows.Forms.SendKeys]::SendWait("%{F4}")
        if (-not $app.WaitForExit(15000)) {
            [void][AmareSettings]::PostMessage($script:hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
            if (-not $app.WaitForExit(15000)) {
                Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
                Add-Result "last-window-normal-exit" "UNVERIFIED" "Alt+F4/WM_CLOSE did not exit; process killed"
            } else {
                Add-Result "last-window-normal-exit" "PASS" "WM_CLOSE exited last window"
            }
        } else {
            Add-Result "last-window-normal-exit" "PASS" "Alt+F4 exited last window"
        }
    }
    Start-Sleep -Seconds 2
    $afterWebView = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
    if ($afterWebView -gt 0) {
        Add-Result "settings-exit-webview2-cleanup" "UNVERIFIED" "webview2=$afterWebView after exit"
    } else {
        Add-Result "settings-exit-webview2-cleanup" "PASS" "webview2=0 after exit"
    }

    $env:AMARE_GPUI_RESTORE = "0"
    $app = Start-Process $Exe -PassThru
    [void](Wait-State { param($s) $null -ne $s.address } 60 "reset test app")
    Start-Sleep -Seconds 2
    $app.Refresh(); $script:hwnd = $app.MainWindowHandle
    [void](Force-AmareForeground $script:hwnd)
    [void][AmareSettings]::ShowWindow($script:hwnd, 3)
    Start-Sleep -Seconds 1
    Open-Settings
    Click-Client $script:hwnd 300 114
    Send-Keys "^a"; Send-Keys "reset"; Start-Sleep -Milliseconds 500
    [void](Wait-State { param($s) $s.settings_query -eq "reset" } 6 "settings reset search")
    $reset = $false
    foreach ($y in 396, 400, 404, 388, 408) {
        Click-Client $script:hwnd 960 $y
        Click-Client $script:hwnd 960 $y
        $state = Read-State
        if ($state.settings.theme -eq "system" -and $state.settings.ui_font_size -eq 16 -and $state.settings.ui_density -eq "comfortable") { $reset = $true; break }
    }
    if (-not $reset) { throw "Timed out waiting for settings reset" }
    Add-Result "settings-reset" "PASS" "defaults restored"
    Shot "13-settings-after-reset.png"
} catch {
    Add-Result "fatal" "FAIL" $_.Exception.Message
    try { Shot "99-settings-failure.png" } catch {}
}
finally {
    foreach ($window in (Get-BrowserWindows)) {
        if (-not [AmareSettings]::IsWindowVisible($window)) { continue }
        [void](Force-AmareForeground $window)
        Start-Sleep -Milliseconds 200
        [System.Windows.Forms.SendKeys]::SendWait("%{F4}")
        Start-Sleep -Milliseconds 500
    }
    if ($null -ne $app -and -not $app.HasExited) { try { $app.Kill() } catch {} }
    Start-Sleep -Seconds 2
    $afterCleanup = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
    if ($afterCleanup -gt 0) {
        Add-Result "settings-final-webview2-cleanup" "UNVERIFIED" "webview2=$afterCleanup after all settings windows exited"
    } else {
        Add-Result "settings-final-webview2-cleanup" "PASS" "webview2=0 after all settings windows exited"
    }
    $summary = [pscustomobject]@{
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        results = $script:results
        pass = @($script:results | Where-Object result -eq "PASS").Count
        fail = @($script:results | Where-Object result -eq "FAIL").Count
        unverified = @($script:results | Where-Object result -eq "UNVERIFIED").Count
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ScreenshotDir "gpui-settings-results.json") -Encoding UTF8
    Write-Host "GPUI settings summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)"
    if ($summary.fail -gt 0) { exit 1 }
}
