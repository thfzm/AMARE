param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Status,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$ScreenshotDir,
    [Parameter(Mandatory = $true)][string]$BaseUrl
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AmareLib {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
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
function Shot([string]$name) {
    $r = New-Object AmareLib+RECT
    [void][AmareLib]::GetWindowRect($script:hwnd, [ref]$r)
    $w = [Math]::Max(1, $r.Right - $r.Left); $h = [Math]::Max(1, $r.Bottom - $r.Top)
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
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
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $state = Read-State
        if (@($state.tabs).Count -ge $target) { return $state }
        [void][AmareLib]::SetForegroundWindow($script:hwnd)
        Send-Keys "^t"
        $deadline = (Get-Date).AddSeconds(8)
        while ((Get-Date) -lt $deadline) {
            $state = Read-State
            if (@($state.tabs).Count -ge $target) { return $state }
            Start-Sleep -Milliseconds 300
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
try {
    $app = Start-Process $Exe -PassThru
    [void](Wait-State { param($s) $null -ne $s.address } 60 "initial library app")
    Start-Sleep -Seconds 2
    $app.Refresh(); $script:hwnd = $app.MainWindowHandle
    if ($script:hwnd -eq [IntPtr]::Zero) { throw "GPUI window missing" }
    [void][AmareLib]::SetForegroundWindow($script:hwnd)
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
    [void][AmareLib]::SetForegroundWindow($script:hwnd)
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
    [void][AmareLib]::SetForegroundWindow($script:hwnd)

    [void][AmareLib]::SetForegroundWindow($script:hwnd)
    Send-Keys "%{F4}"
    if (-not $app.WaitForExit(20000)) { throw "library app did not exit" }
    Start-Sleep -Seconds 2
    $after = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
    if ($after -gt $before) { throw "WebView2 processes remained after library app exit" }
    Add-Result "graceful-exit-webview2" "PASS" "exit=$($app.ExitCode) webview2=$before->$after"

    # Restart with the same data directory and verify persistence.
    $beforeRestart = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
    $app2 = Start-Process $Exe -PassThru
    $restored = Wait-State { param($s) @($s.bookmarks).Count -ge 2 -and @($s.history).Count -ge 1 } 60 "restored library data"
    Add-Result "restart-persistence" "PASS" "bookmarks=$(@($restored.bookmarks).Count) history=$(@($restored.history).Count)"
    $app2.Refresh(); $script:hwnd = $app2.MainWindowHandle
    if ($script:hwnd -ne [IntPtr]::Zero) {
        [void][AmareLib]::SetForegroundWindow($script:hwnd)
        Send-Keys "%{F4}"
        if (-not $app2.WaitForExit(20000)) { throw "restarted library app did not exit" }
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
