param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Status,
    [Parameter(Mandatory = $true)][string]$BaseUrl,
    [Parameter(Mandatory = $true)][string]$ArtifactDir
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AmareTab {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);
}
'@

$script:results = [System.Collections.Generic.List[object]]::new()
function Add-Result([string]$name, [string]$state, [string]$detail) {
    $script:results.Add([pscustomobject]@{ name = $name; result = $state; detail = $detail })
    Write-Host ("{0} {1}: {2}" -f $state, $name, $detail)
}
function Get-Rect([IntPtr]$hwnd) { $r = New-Object AmareTab+RECT; [void][AmareTab]::GetWindowRect($hwnd, [ref]$r); return $r }
function Get-Origin([IntPtr]$hwnd) { $p = New-Object AmareTab+POINT; [void][AmareTab]::ClientToScreen($hwnd, [ref]$p); return $p }
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
function Send-Keys([string]$keys) { [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds 150 }
function Click-At([IntPtr]$hwnd, [int]$x, [int]$y) {
    [void][AmareTab]::SetForegroundWindow($hwnd)
    [void][AmareTab]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 150
    [AmareTab]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [AmareTab]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}
function Click-Web([IntPtr]$hwnd, [int]$x, [int]$y) {
    $origin = Get-Origin $hwnd
    Click-At $hwnd ($origin.X + $x) ($origin.Y + 88 + $y)
}
function Shot([string]$name) {
    $r = Get-Rect $script:hwnd
    $w = [Math]::Max(1, $r.Right - $r.Left); $h = [Math]::Max(1, $r.Bottom - $r.Top)
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $path = Join-Path $ArtifactDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Add-Result ("screenshot-" + [IO.Path]::GetFileNameWithoutExtension($name)) "PASS" "$path ($w x $h)"
}
function Page([string]$tab) { return "$BaseUrl`?tab=$tab" }
function Get-TabState([string]$stateName, [string]$tab) {
    $state = Read-State
    if ($null -eq $state) { return $null }
    return $state.tabs | Where-Object { $_.id -eq $tab } | Select-Object -First 1
}
function Active-Tab { return (Read-State).active_tab }
function Wait-Tabs([int]$count, [int]$seconds, [string]$label) {
    return Wait-State { param($s) @($s.tabs).Count -eq $count } $seconds $label
}
function Navigate([string]$url, [int]$timeout = 20) {
    Send-Keys "^l"
    Start-Sleep -Milliseconds 200
    Send-Keys "^a"
    Send-Keys "{BACKSPACE}"
    Send-Keys $url
    Send-Keys "{ENTER}"
    return Wait-State { param($s) $s.source -eq $url } $timeout "navigation $url"
}
function Get-PageStateForTab([string]$tab) {
    $tabState = Get-TabState $null $tab
    if ($null -eq $tabState -or -not $tabState.title.StartsWith("AMARE_TEST:")) { return $null }
    return [Uri]::UnescapeDataString($tabState.title.Substring(11)) | ConvertFrom-Json
}
function Focus-TextInput([string]$tab) {
    Click-Web $script:hwnd 900 400
    for ($i = 0; $i -lt 5; $i++) {
        Send-Keys "{TAB}"
        Start-Sleep -Milliseconds 250
        $page = Get-PageStateForTab $tab
        if ($null -ne $page -and $page.active -eq "text") { return $page }
    }
    throw "Could not focus text input in tab $tab"
}
function Wait-Page([string]$tab, [scriptblock]$condition, [int]$seconds, [string]$label) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $tabState = Get-TabState $null $tab
        if ($null -ne $tabState -and $tabState.title.StartsWith("AMARE_TEST:")) {
            $obj = [Uri]::UnescapeDataString($tabState.title.Substring(11)) | ConvertFrom-Json
            if (& $condition $obj) { return $obj }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for tab $tab $label"
}
function Select-Tab([int]$id, [int]$timeout = 15) {
    $deadline = (Get-Date).AddSeconds($timeout)
    while ((Get-Date) -lt $deadline) {
        if ((Read-State).active_tab -eq $id) { return Read-State }
        Send-Keys "^{TAB}"
        Start-Sleep -Milliseconds 300
    }
    throw "Timed out selecting tab $id"
}
function Process-Metrics([string]$label) {
    $webviews = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue)
    $ws = 0; foreach ($p in $webviews) { try { $ws += [int64]$p.WorkingSet64 } catch {} }
    $app = Get-Process -Id $script:app.Id -ErrorAction SilentlyContinue
    $appWs = if ($null -ne $app) { [int64]$app.WorkingSet64 } else { 0 }
    Add-Result "metrics-$label" "PASS" "webview_processes=$($webviews.Count) webview_ws_mb=$([Math]::Round($ws / 1MB, 1)) app_ws_mb=$([Math]::Round($appWs / 1MB, 1))"
}

$script:hwnd = [IntPtr]::Zero
$script:app = $null
$before = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
Remove-Item $Status -Force -ErrorAction SilentlyContinue
$env:AMARE_GPUI_SELF_TEST = "0"
$env:AMARE_GPUI_STATUS = $Status
$env:AMARE_GPUI_START_URL = (Page "A")
$env:AMARE_GPUI_NEW_TAB_URL = (Page "NEW")
try {
    $script:app = Start-Process $Exe -PassThru
    $initial = Wait-State { param($s) @($s.tabs).Count -eq 1 -and $s.source -like "*tab=A*" } 60 "initial tab"
    Start-Sleep -Seconds 2
    $script:app.Refresh()
    $script:hwnd = $script:app.MainWindowHandle
    if ($script:hwnd -eq [IntPtr]::Zero) { throw "GPUI window missing" }
    [void][AmareTab]::SetForegroundWindow($script:hwnd)
    Add-Result "initial-tab" "PASS" "id=$($initial.active_tab) source=$($initial.source)"
    Process-Metrics "1-tab"
    $orig = Get-Origin $script:hwnd
    Click-At $script:hwnd ($orig.X + 20) ($orig.Y + 22)
    Send-Keys "^t"
    $two = Wait-Tabs 2 20 "second tab via Ctrl+T"
    Add-Result "new-tab-ctrl-t" "PASS" "tabs=$(@($two.tabs).Count) active=$($two.active_tab)"
    $bId = $two.active_tab
    $navB = Navigate (Page "B") 25
    Add-Result "new-tab-navigation" "PASS" "tab=$bId source=$($navB.source)"
    Send-Keys "^t"; $three = Wait-Tabs 3 20 "third tab"
    $cId = $three.active_tab
    $navC = Navigate (Page "C") 25
    Add-Result "tab-c-navigation" "PASS" "tab=$cId source=$($navC.source)"
    # New tab button actual click.
    $rect = Get-Rect $script:hwnd
    $orig = Get-Origin $script:hwnd
    Click-At $script:hwnd ($rect.Right - 28) ($orig.Y + 22)
    $four = Wait-Tabs 4 20 "new-tab button"
    Add-Result "new-tab-button" "PASS" "tabs=$(@($four.tabs).Count)"
    $navD = Navigate (Page "D") 25
    # Five tabs + metrics.
    Send-Keys "^t"; $five = Wait-Tabs 5 20 "fifth tab"
    $eId = $five.active_tab
    $navE = Navigate (Page "E") 25
    Add-Result "five-tabs" "PASS" "tabs=5 active=$eId"
    Process-Metrics "5-tabs"
    # Ten tabs.
    for ($i = 6; $i -le 10; $i++) {
        Send-Keys "^t"
        [void](Wait-Tabs $i 20 "tab $i")
        [void](Navigate (Page ([string]$i)) 25)
    }
    $ten = Wait-Tabs 10 25 "ten tabs"
    Add-Result "ten-tabs" "PASS" "tabs=$(@($ten.tabs).Count)"
    Process-Metrics "10-tabs"
    Shot "gpui-tabs-10.png"

    # Select tab A by cycle, then preserve input/scroll, switch to B and back.
    $aId = @($ten.tabs)[0].id
    [void](Select-Tab $aId)
    $aState = Read-State
    if ($aState.source -notlike "*tab=A*") { throw "tab A source mismatch: $($aState.source)" }
    [void](Focus-TextInput $aId)
    Send-Keys "^a"; Send-Keys "{BACKSPACE}"; Send-Keys "TAB-A-STATE"
    $aPage = Wait-Page $aId { param($p) $p.text -eq "TAB-A-STATE" } 10 "tab A input"
    [AmareTab]::mouse_event(0x0800, 0, 0, -360, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
    $aScrolled = Wait-Page $aId { param($p) [int]$p.scrollY -gt 0 } 8 "tab A scroll"
    Add-Result "tab-a-state" "PASS" "text=$($aPage.text) scrollY=$($aScrolled.scrollY)"
    [void](Select-Tab $bId)
    $bState = Read-State
    [void](Focus-TextInput $bId)
    Send-Keys "^a"; Send-Keys "{BACKSPACE}"; Send-Keys "TAB-B-STATE"
    [void](Wait-Page $bId { param($p) $p.text -eq "TAB-B-STATE" } 10 "tab B input")
    [void](Select-Tab $aId)
    $aAgain = Wait-Page $aId { param($p) $p.text -eq "TAB-A-STATE" -and [int]$p.scrollY -gt 0 } 10 "tab A restore"
    Add-Result "tab-state-preserved" "PASS" "tab A text=$($aAgain.text) scrollY=$($aAgain.scrollY)"
    [void](Select-Tab $bId)
    $bAgain = Wait-Page $bId { param($p) $p.text -eq "TAB-B-STATE" } 10 "tab B restore"
    Add-Result "tab-b-state-preserved" "PASS" "tab B text=$($bAgain.text)"

    # Per-tab history: navigate A, back should not change B.
    [void](Select-Tab $aId)
    [void](Navigate (Page "A2") 25)
    [void](Select-Tab $bId)
    Send-Keys "%{LEFT}"
    Start-Sleep -Seconds 1
    $bHist = Read-State
    if ($bHist.source -like "*tab=A2*") { throw "tab B received tab A history" }
    Add-Result "tab-history-independent" "PASS" "tab B source=$($bHist.source)"

    # Close active B via Ctrl+W; A remains.
    Send-Keys "^w"
    $afterClose = Wait-State { param($s) @($s.tabs).Count -eq 9 } 20 "tab close"
    Add-Result "tab-close-ctrl-w" "PASS" "tabs=$(@($afterClose.tabs).Count)"
    [void](Select-Tab $aId)
    $aAfterClose = Wait-State { param($s) $s.active_tab -eq $aId -and $s.source -like "*tab=A2*" } 10 "tab A after close"
    Add-Result "tab-survives-other-close" "PASS" "tab A source=$($aAfterClose.source)"

    # Reduce to two tabs, test tab click and the close button with stable geometry.
    while (@((Read-State).tabs).Count -gt 2) {
        Send-Keys "^w"
        Start-Sleep -Milliseconds 700
    }
    $twoState = Wait-State { param($s) @($s.tabs).Count -eq 2 } 20 "two tabs remaining"
    $firstId = @($twoState.tabs)[0].id
    $orig = Get-Origin $script:hwnd
    Click-At $script:hwnd ($orig.X + 55) ($orig.Y + 22)
    $clickedTab = Wait-State { param($s) $s.active_tab -eq $firstId } 10 "tab click"
    Add-Result "tab-click-activation" "PASS" "active=$($clickedTab.active_tab)"
    Shot "gpui-tabs-two.png"
    Click-At $script:hwnd ($orig.X + 144) ($orig.Y + 22)
    $afterButton = Wait-State { param($s) @($s.tabs).Count -eq 1 } 15 "tab close button"
    Add-Result "tab-close-button" "PASS" "tabs=$(@($afterButton.tabs).Count)"
    Shot "gpui-tabs-after-close.png"

    # Close the last tab; it should exit the browser.
    Send-Keys "^w"
    if (-not $script:app.WaitForExit(20000)) { throw "browser did not exit on last tab close" }
    Start-Sleep -Seconds 2
    $after = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
    if ($after -gt $before) { throw "WebView2 processes remained after all tabs closed" }
    Add-Result "last-tab-close-exits" "PASS" "exit=$($script:app.ExitCode) webview2=$before->$after"
}
catch {
    Add-Result "fatal" "FAIL" $_.Exception.Message
    try { Shot "gpui-tabs-failure.png" } catch {}
}
finally {
    if ($null -ne $script:app -and -not $script:app.HasExited) { try { $script:app.Kill() } catch {} }
    $summary = [pscustomobject]@{ generated_at=(Get-Date).ToUniversalTime().ToString("o"); results=$script:results; pass=@($script:results|? result -eq "PASS").Count; fail=@($script:results|? result -eq "FAIL").Count; unverified=@($script:results|? result -eq "UNVERIFIED").Count }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ArtifactDir "gpui-tab-results.json") -Encoding UTF8
    Write-Host "GPUI tab summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)"
    if ($summary.fail -gt 0) { exit 1 }
}
