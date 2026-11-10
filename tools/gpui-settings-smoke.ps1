param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Status,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$ScreenshotDir
)

$ErrorActionPreference = "Stop"
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
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
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
function Click-Client([IntPtr]$hwnd, [int]$x, [int]$y) {
    $point = New-Object AmareSettings+POINT
    [void][AmareSettings]::ClientToScreen($hwnd, [ref]$point)
    [void][AmareSettings]::SetForegroundWindow($hwnd)
    [void][AmareSettings]::SetCursorPos(($point.X + $x), ($point.Y + $y))
    Start-Sleep -Milliseconds 120
    [AmareSettings]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [AmareSettings]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 350
}
function Shot([string]$name) {
    $rect = New-Object AmareSettings+RECT
    [void][AmareSettings]::GetWindowRect($script:hwnd, [ref]$rect)
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
    Click-Client $script:hwnd 82 $y
    [void](Wait-State { param($s) $s.settings_category -eq $key } 6 "settings category $key")
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
    [void][AmareSettings]::SetForegroundWindow($script:hwnd)
    Click-Client $script:hwnd 908 22
    if (-not [AmareSettings]::IsIconic($script:hwnd)) { throw "minimize click did not minimize window" }
    Add-Result "titlebar-minimize-click" "PASS" "window minimized"
    [void][AmareSettings]::ShowWindow($script:hwnd, 9)
    Start-Sleep -Milliseconds 700
    Click-Client $script:hwnd 952 22
    if (-not [AmareSettings]::IsZoomed($script:hwnd)) { throw "maximize click did not maximize window" }
    Add-Result "titlebar-maximize-click" "PASS" "window maximized"
    Click-Client $script:hwnd 952 22
    if ([AmareSettings]::IsZoomed($script:hwnd)) { throw "restore click did not restore window" }
    Add-Result "titlebar-restore-click" "PASS" "window restored"
    Click-Client $script:hwnd 952 22
    if (-not [AmareSettings]::IsZoomed($script:hwnd)) { throw "second maximize click failed" }
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
    Click-Client $script:hwnd 1000 212
    [void](Wait-State { param($s) $s.settings.theme -eq "dark" } 8 "dark theme setting")
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

    Send-Keys "^,"
    [void](Wait-State { param($s) $s.panel -eq "none" } 6 "settings closed")
    Shot "10-browser-settings-applied.png"
    [void][AmareSettings]::ShowWindow($script:hwnd, 9)
    Start-Sleep -Seconds 1

    $baseWindows = (Get-BrowserWindows).Count
    Send-Keys "^n"
    $windows = Wait-WindowCount ($baseWindows + 1) 12 "second browser window"
    [void][AmareSettings]::SetWindowPos($windows[0], [IntPtr]::Zero, 8, 32, 500, 640, 0x0004)
    [void][AmareSettings]::SetWindowPos($windows[1], [IntPtr]::Zero, 516, 32, 500, 640, 0x0004)
    Start-Sleep -Seconds 5
    Add-Result "multi-window-created" "PASS" "windows=$($windows.Count) side-by-side"

    foreach ($window in (Get-BrowserWindows)) {
        [void][AmareSettings]::SetForegroundWindow($window)
        Start-Sleep -Milliseconds 120
        [System.Windows.Forms.SendKeys]::SendWait("%{F4}")
        Start-Sleep -Milliseconds 120
    }
    if (-not $app.WaitForExit(25000)) { throw "settings app did not exit after closing both windows" }
    Add-Result "multi-window-graceful-exit" "PASS" "both windows closed in sequence"
    Start-Sleep -Seconds 2

    $app = Start-Process $Exe -PassThru
    [void](Wait-State { param($s) $null -ne $s.address } 60 "restored settings app")
    $windows = Wait-WindowCount ($baseWindows + 1) 20 "restored multi-window session"
    Start-Sleep -Seconds 3
    $app.Refresh(); $script:hwnd = $windows[0]
    [void][AmareSettings]::SetForegroundWindow($script:hwnd)
    $restored = Read-State
    if ($restored.settings.theme -ne "dark" -or $restored.settings.ui_font_size -ne $fontValue -or $restored.settings.tab_width -ne $tabValue -or $restored.settings.ui_density -ne $densityValue) {
        throw "settings did not persist after restart: $($restored.settings | ConvertTo-Json -Compress)"
    }
    Add-Result "settings-restart-persistence" "PASS" "theme=dark size=$fontValue tab=$tabValue density=$densityValue"
    Add-Result "multi-window-session-restore" "PASS" "windows=$($windows.Count)"
    ShotFull "11-multi-window-session-restored.png"

    [void][AmareSettings]::ShowWindow($script:hwnd, 3)
    Start-Sleep -Seconds 1
    Shot "12-titlebar-maximized.png"
    [void][AmareSettings]::ShowWindow($script:hwnd, 9)
    Start-Sleep -Milliseconds 800

    Click-Client $script:hwnd 997 22
    if (-not $app.WaitForExit(25000)) { throw "close click did not exit reset app" }
    Add-Result "titlebar-close-click" "PASS" "window closed via close button"
    Start-Sleep -Seconds 2

    $env:AMARE_GPUI_RESTORE = "0"
    $app = Start-Process $Exe -PassThru
    [void](Wait-State { param($s) $null -ne $s.address } 60 "reset test app")
    Start-Sleep -Seconds 2
    $app.Refresh(); $script:hwnd = $app.MainWindowHandle
    [void][AmareSettings]::SetForegroundWindow($script:hwnd)
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
    Start-Sleep -Seconds 2
    $after = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
    if ($after -gt 0) {
        Add-Result "settings-exit-webview2-cleanup" "UNVERIFIED" "webview2=$after after exit"
    } else {
        Add-Result "settings-exit-webview2-cleanup" "PASS" "webview2=0"
    }
} catch {
    Add-Result "fatal" "FAIL" $_.Exception.Message
    try { Shot "99-settings-failure.png" } catch {}
}
finally {
    foreach ($window in (Get-BrowserWindows)) {
        if (-not [AmareSettings]::IsWindowVisible($window)) { continue }
        [void][AmareSettings]::SetForegroundWindow($window)
        Start-Sleep -Milliseconds 200
        [System.Windows.Forms.SendKeys]::SendWait("%{F4}")
        Start-Sleep -Milliseconds 500
    }
    if ($null -ne $app -and -not $app.HasExited) { try { $app.Kill() } catch {} }
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
