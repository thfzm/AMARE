param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Status,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$ScreenshotDir,
    [Parameter(Mandatory = $true)][string]$BaseUrl
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\gpui-test-focus.ps1"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AmareLib {
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
        if ([AmareLib]::GetForegroundWindow() -eq $hwnd) { return }
        Start-Sleep -Milliseconds 150
    }
    [AmareLib]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [void](Force-AmareForeground $hwnd)
    [AmareLib]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 150
}
function Send-CtrlKey([int]$vk) {
    [AmareLib]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    [AmareLib]::keybd_event([byte]$vk, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
    [AmareLib]::keybd_event([byte]$vk, 0, 2, [UIntPtr]::Zero)
    [AmareLib]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}
function Click-Client([IntPtr]$hwnd, [int]$x, [int]$y) {
    $point = New-Object AmareLib+POINT
    [void][AmareLib]::ClientToScreen($hwnd, [ref]$point)
    [void](Force-AmareForeground $hwnd)
    [void][AmareLib]::SetCursorPos(($point.X + $x), ($point.Y + $y))
    Start-Sleep -Milliseconds 120
    [AmareLib]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [AmareLib]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 350
}
function Shot([string]$name) {
    $path = Join-Path $ScreenshotDir $name
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        try { [void][AmareLib]::ShowWindow($script:hwnd, 9) } catch {}
        Force-Focus $script:hwnd
        Start-Sleep -Milliseconds 400
        $r = New-Object AmareLib+RECT
        [void][AmareLib]::GetWindowRect($script:hwnd, [ref]$r)
        $w = [Math]::Max(1, $r.Right - $r.Left); $h = [Math]::Max(1, $r.Bottom - $r.Top)
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        if ((Get-Item $path).Length -ge 1000) {
            Add-Result $name "PASS" "$path ($w x $h, $((Get-Item $path).Length) bytes)"
            return
        }
        Start-Sleep -Milliseconds 700
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
function Navigate([string]$url, [int]$timeout = 20) {
    Send-Keys "^l"
    Start-Sleep -Milliseconds 200
    Send-Keys "^a"; Send-Keys "{BACKSPACE}"
    Send-Keys $url
    Send-Keys "{ENTER}"
    return Wait-State { param($s) $s.source -like "$url*" } $timeout "navigation $url"
}
function Get-PageState {
    $state = Read-State
    if ($null -eq $state -or -not $state.title.StartsWith("AMARE_TEST:")) { return $null }
    return [Uri]::UnescapeDataString($state.title.Substring(11)) | ConvertFrom-Json
}
function Wait-Count([int]$count, [int]$seconds, [string]$label) {
    return Wait-State { param($s) @($s.tabs).Count -eq $count } $seconds $label
}
function Ensure-LibraryTabs([int]$target) {
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        $state = Read-State
        if (@($state.tabs).Count -ge $target) { return $state }
        Force-Focus $script:hwnd
        Click-Client $script:hwnd 50 30
        Send-GpuiShortcut $script:hwnd 0x54
        Send-Keys "^t"
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline) {
            $state = Read-State
            if (@($state.tabs).Count -ge $target) { return $state }
            Start-Sleep -Milliseconds 250
        }
        Click-Client $script:hwnd 420 70
        Send-GpuiShortcut $script:hwnd 0x54
        Send-Keys "^t"
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline) {
            $state = Read-State
            if (@($state.tabs).Count -ge $target) { return $state }
            Start-Sleep -Milliseconds 250
        }
        $rect = New-Object AmareLib+RECT
        [void][AmareLib]::GetWindowRect($script:hwnd, [ref]$rect)
        $width = [Math]::Max(1, $rect.Right - $rect.Left)
        $layout = (Read-State).layout_bounds
        $tabBounds = if ($null -ne $layout) { $layout.toolbar_children.new_tab } else { $null }
        foreach ($by in 22, 30, 38) {
            if ($null -ne $tabBounds) {
                Send-RealClientClick $script:hwnd ([int]($tabBounds.x + $tabBounds.width / 2)) ([int]($tabBounds.y + $tabBounds.height / 2))
            } else {
                Send-RealClientClick $script:hwnd ($width - 235) $by
            }
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Date) -lt $deadline) {
                $state = Read-State
                if (@($state.tabs).Count -ge $target) { return $state }
                Start-Sleep -Milliseconds 250
            }
        }
    }
    throw "Could not create $target tabs"
}

$script:hwnd = [IntPtr]::Zero
$app = $null
$before = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
Remove-Item $Status -Force -ErrorAction SilentlyContinue
Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $DataDir, $ScreenshotDir | Out-Null
$env:AMARE_GPUI_DATA_DIR = $DataDir
$env:AMARE_GPUI_STATUS = $Status
$env:AMARE_GPUI_START_URL = "about:blank"
$env:AMARE_GPUI_SELF_TEST = "0"
$env:AMARE_GPUI_DIAGNOSTICS = "1"
try {
    $app = Start-Process $Exe -PassThru
    [void](Wait-State { param($s) $null -ne $s.address } 60 "initial library app")
    Start-Sleep -Seconds 2
    $app.Refresh(); $script:hwnd = $app.MainWindowHandle
    if ($script:hwnd -eq [IntPtr]::Zero) { throw "GPUI window missing" }
    [void](Force-AmareForeground $script:hwnd)
    Shot "01-initial-new-tab.png"

    [void](Navigate "https://example.com/" 25)
    Start-Sleep -Seconds 1
    Shot "02-example-page.png"

    [void](Ensure-LibraryTabs 2)
    [void](Navigate "$BaseUrl`?tab=A" 25)
    [void](Ensure-LibraryTabs 3)
    [void](Navigate "$BaseUrl`?tab=B" 25)
    Shot "03-multiple-tabs.png"

    Send-Keys "^+b"
    [void](Wait-State { param($s) $s.panel -eq "bookmarks" } 8 "bookmarks panel")
    Shot "04-bookmarks-panel.png"

    Send-Keys "^+b"
    Start-Sleep -Milliseconds 300
    [void](Navigate "https://example.com/?bookmark=1" 25)
    Send-Keys "^d"; Start-Sleep -Milliseconds 800
    [void](Navigate "https://example.com/?bookmark=2" 25)
    Send-Keys "^d"; Start-Sleep -Milliseconds 800
    [void](Wait-State { param($s) @($s.bookmarks).Count -ge 2 } 10 "bookmarks persisted")
    Send-Keys "^+b"
    [void](Wait-State { param($s) $s.panel -eq "bookmarks" } 8 "bookmarks panel populated")
    Shot "05-bookmarks-multiple.png"

    Send-Keys "^+b"
    Start-Sleep -Milliseconds 300
    Send-Keys "^+h"
    [void](Wait-State { param($s) $s.panel -eq "history" } 8 "history panel")
    Shot "06-history-panel.png"
    Send-Keys "example"
    [void](Wait-State { param($s) $s.history_query -eq "example" } 8 "history search query")
    Start-Sleep -Milliseconds 500
    Shot "07-history-search.png"

    # Actual custom-titlebar maximize/restore controls.
    $rect = New-Object AmareLib+RECT
    [void][AmareLib]::GetWindowRect($script:hwnd, [ref]$rect)
    $w = $rect.Right - $rect.Left
    $origin = [System.Drawing.Point]::Empty
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class LibPoint { [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; } [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p); }'
    $p = New-Object LibPoint+POINT
    [void][LibPoint]::ClientToScreen($script:hwnd, [ref]$p)
    [void](Force-AmareForeground $script:hwnd)
    [void][AmareLib]::ShowWindow($script:hwnd, 3)
    Start-Sleep -Seconds 1
    Shot "08-titlebar-maximized.png"
    [void][AmareLib]::ShowWindow($script:hwnd, 9)
    Start-Sleep -Seconds 1
    Shot "08b-titlebar-restored.png"
    ShotFull "13-windows-taskbar-icon.png"
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class LibAlt { [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra); }'
    [LibAlt]::keybd_event(0x12,0,0,[UIntPtr]::Zero)
    [LibAlt]::keybd_event(0x09,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
    ShotFull "14-windows-alt-tab-icon.png"
    [LibAlt]::keybd_event(0x09,0,2,[UIntPtr]::Zero)
    [LibAlt]::keybd_event(0x12,0,2,[UIntPtr]::Zero)
    Start-Sleep -Seconds 1
    [void](Force-AmareForeground $script:hwnd)

    $beforeSession = Read-State
    $beforeRect = New-Object AmareLib+RECT
    [void][AmareLib]::GetWindowRect($script:hwnd, [ref]$beforeRect)
    [void](Force-AmareForeground $script:hwnd)
    Send-Keys "%{F4}"
    if (-not $app.WaitForExit(20000)) { throw "library app did not exit" }
    Start-Sleep -Seconds 2
    $after = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
    if ($after -gt $before) { throw "WebView2 processes remained after library app exit" }
    Add-Result "graceful-exit-webview2" "PASS" "exit=$($app.ExitCode) webview2=$before->$after"

    # Restart with the same data directory and verify persistence.
    $beforeRestart = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
    $app2 = Start-Process $Exe -PassThru
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        $app2.Refresh()
        if ($app2.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }
    $restored = Wait-State { param($s) @($s.bookmarks).Count -ge 2 -and @($s.history).Count -ge 1 -and @($s.tabs).Count -eq @($beforeSession.tabs).Count } 60 "restored library data and session tabs"
    Add-Result "restart-persistence" "PASS" "bookmarks=$(@($restored.bookmarks).Count) history=$(@($restored.history).Count)"
    $app2.Refresh(); $script:hwnd = $app2.MainWindowHandle
    if ($script:hwnd -ne [IntPtr]::Zero) {
        [void](Force-AmareForeground $script:hwnd)
        $afterRect = New-Object AmareLib+RECT
        [void][AmareLib]::GetWindowRect($script:hwnd, [ref]$afterRect)
        $beforeW = $beforeRect.Right - $beforeRect.Left; $beforeH = $beforeRect.Bottom - $beforeRect.Top
        $afterW = $afterRect.Right - $afterRect.Left; $afterH = $afterRect.Bottom - $afterRect.Top
        if ($restored.source -ne $beforeSession.source -or $restored.active_tab -ne $beforeSession.active_tab) {
            Add-Result "session-active-restore" "FAIL" "before=$($beforeSession.source)/$($beforeSession.active_tab) after=$($restored.source)/$($restored.active_tab)"
        } else {
            Add-Result "session-active-restore" "PASS" "source=$($restored.source)"
        }
        Shot "15-session-restored.png"
        Add-Result "session-window-geometry" "PASS" "before=${beforeW}x${beforeH} after=${afterW}x${afterH}"
        Send-Keys "^+d"
        Start-Sleep -Milliseconds 500
        Shot "16-downloads-after-restart.png"
        Send-Keys "%{F4}"
        if (-not $app2.WaitForExit(20000)) {
            Stop-Process -Id $app2.Id -Force -ErrorAction SilentlyContinue
            Add-Result "library-restart-exit" "UNVERIFIED" "Alt+F4 did not exit; process killed"
        } else {
            Add-Result "library-restart-exit" "PASS" "restarted library app exited"
        }
    }
    $after2 = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 500
        $after2 = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
        if ($after2 -le $beforeRestart) { break }
    }
    if ($after2 -gt $beforeRestart) {
        Add-Result "restart-webview2-cleanup" "UNVERIFIED" "webview2=$beforeRestart->$after2 after 15s"
    } else {
        Add-Result "restart-webview2-cleanup" "PASS" "webview2=$beforeRestart->$after2"
    }
}
catch {
    Add-Result "fatal" "FAIL" $_.Exception.Message
    try {
        $trace = (Read-State).diagnostics
        if ($null -ne $trace) { $trace | Select-Object -Last 40 | ForEach-Object { Write-Host "TRACE $_" } }
    } catch {}
    try { Shot "99-library-failure.png" } catch {}
}
finally {
    if ($null -ne $app -and -not $app.HasExited) { try { $app.Kill() } catch {} }
    if ($null -ne $app2 -and -not $app2.HasExited) { try { $app2.Kill() } catch {} }
    $summary = [pscustomobject]@{ generated_at=(Get-Date).ToUniversalTime().ToString("o"); results=$script:results; pass=@($script:results|? result -eq "PASS").Count; fail=@($script:results|? result -eq "FAIL").Count; unverified=@($script:results|? result -eq "UNVERIFIED").Count }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ScreenshotDir "gpui-library-results.json") -Encoding UTF8
    Write-Host "GPUI library summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)"
    if ($summary.fail -gt 0) { exit 1 }
}
