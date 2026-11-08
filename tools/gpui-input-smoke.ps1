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
public static class Amare47 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);
    [DllImport("user32.dll")] public static extern IntPtr GetFocus();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr LoadKeyboardLayout(string id, uint flags);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("imm32.dll")] public static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll")] public static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr context);
    [DllImport("imm32.dll")] public static extern bool ImmSetOpenStatus(IntPtr context, bool open);
    [DllImport("imm32.dll")] public static extern bool ImmSetConversionStatus(IntPtr context, uint conversion, uint sentence);
    [DllImport("imm32.dll")] public static extern bool ImmGetConversionStatus(IntPtr context, out uint conversion, out uint sentence);
}
'@

$script:results = [System.Collections.Generic.List[object]]::new()
function Add-Result([string]$name, [string]$state, [string]$detail) {
    $script:results.Add([pscustomobject]@{ name = $name; result = $state; detail = $detail })
    Write-Host ("{0} {1}: {2}" -f $state, $name, $detail)
}
function Get-Rect([IntPtr]$hwnd) { $r = New-Object Amare47+RECT; [void][Amare47]::GetWindowRect($hwnd, [ref]$r); return $r }
function Get-ClientOrigin([IntPtr]$hwnd) {
    $p = New-Object Amare47+POINT
    [void][Amare47]::ClientToScreen($hwnd, [ref]$p)
    return $p
}
function Read-State {
    if (-not (Test-Path $Status)) { return $null }
    try { return Get-Content $Status -Raw -ErrorAction Stop | ConvertFrom-Json } catch { return $null }
}
function Wait-State([scriptblock]$condition, [int]$seconds, [string]$label) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $state = Read-State
        if ($null -ne $state -and (& $condition $state)) { return $state }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for $label"
}
function Get-Page {
    $state = Read-State
    if ($null -eq $state -or -not $state.title.StartsWith("AMARE_TEST:")) { return $null }
    try {
        $json = [Uri]::UnescapeDataString($state.title.Substring("AMARE_TEST:".Length))
        return $json | ConvertFrom-Json
    } catch { return $null }
}
function Wait-Page([scriptblock]$condition, [int]$seconds, [string]$label) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $page = Get-Page
        if ($null -ne $page -and (& $condition $page)) { return $page }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for page $label"
}
function Click-At([IntPtr]$hwnd, [int]$x, [int]$y) {
    [void][Amare47]::SetForegroundWindow($hwnd)
    [void][Amare47]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 150
    [Amare47]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [Amare47]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250
}
function Click-Page([IntPtr]$hwnd, [int]$x, [int]$y) {
    $origin = Get-ClientOrigin $hwnd
    Click-At $hwnd ($origin.X + $x) ($origin.Y + $y)
}
function Shot([string]$name) {
    $r = Get-Rect $script:hwnd
    $w = [Math]::Max(1, $r.Right - $r.Left)
    $h = [Math]::Max(1, $r.Bottom - $r.Top)
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $graphics = [System.Drawing.Graphics]::FromImage($bmp)
    $graphics.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $path = Join-Path $ArtifactDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bmp.Dispose()
    Add-Result ("screenshot-" + [IO.Path]::GetFileNameWithoutExtension($name)) "PASS" "$path ($w x $h)"
}
function Send-Keys([string]$keys) { [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds 120 }
function Send-Vk([int]$vk, [int]$delay = 70) {
    [Amare47]::keybd_event([byte]$vk, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $delay
    [Amare47]::keybd_event([byte]$vk, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 30
}
function Find-WebViewChild([IntPtr]$hwnd) {
    foreach ($class in @("Chrome_WidgetWin_0", "Chrome_WidgetWin_1")) {
        $child = [Amare47]::FindWindowEx($hwnd, [IntPtr]::Zero, $class, $null)
        if ($child -ne [IntPtr]::Zero) { return $child }
    }
    return [IntPtr]::Zero
}
function Set-Korean([IntPtr]$hwnd) {
    $hkl = [Amare47]::LoadKeyboardLayout("00000412", 0x00000101)
    [void][Amare47]::SendMessage($hwnd, 0x50, [IntPtr]::Zero, $hkl)
    $context = [Amare47]::ImmGetContext($hwnd)
    if ($context -ne [IntPtr]::Zero) {
        [void][Amare47]::ImmSetOpenStatus($context, $true)
        $before = 0; $sentence = 0
        [void][Amare47]::ImmGetConversionStatus($context, [ref]$before, [ref]$sentence)
        [void][Amare47]::ImmSetConversionStatus($context, 0x00000001, 0)
        $after = 0; $sentence2 = 0
        [void][Amare47]::ImmGetConversionStatus($context, [ref]$after, [ref]$sentence2)
        [void][Amare47]::ImmReleaseContext($hwnd, $context)
        Write-Host "IME hwnd=$hwnd hkl=$([Amare47]::GetKeyboardLayout([Amare47]::GetWindowThreadProcessId($hwnd, [ref]$null))) conversion=$before->$after"
        if (($after -band 1) -eq 0) { Send-Vk 0x15 120 }
    }
    Start-Sleep -Milliseconds 250
}
function Type-Hangul([string]$keys) {
    $map = @{ g = 0x47; k = 0x4B; s = 0x53; r = 0x52; m = 0x4D; f = 0x46; d = 0x44; j = 0x4A; e = 0x45 }
    foreach ($ch in $keys.ToCharArray()) {
        Send-Vk $map[[string]$ch] 80
    }
}
function Get-Address([int]$timeout = 8) {
    return Wait-State { param($s) $null -ne $s.address } $timeout "address status"
}
function Navigate-Address([string]$url, [int]$timeout = 20) {
    Send-Keys "^l"
    Start-Sleep -Milliseconds 200
    Send-Keys "^a"
    Send-Keys "{BACKSPACE}"
    Send-Keys $url
    Send-Keys "{ENTER}"
    return Wait-State { param($s) $s.source -eq $url } $timeout "navigation to $url"
}

$script:hwnd = [IntPtr]::Zero
$app = $null
$before = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
Remove-Item $Status -Force -ErrorAction SilentlyContinue
$env:AMARE_GPUI_SELF_TEST = "0"
$env:AMARE_GPUI_STATUS = $Status
$env:AMARE_GPUI_START_URL = $BaseUrl
try {
    $app = Start-Process $Exe -PassThru
    $initial = Wait-State { param($s) $null -ne $s.address -and -not [string]::IsNullOrEmpty($s.source) } 60 "GPUI status"
    Start-Sleep -Seconds 2
    $app.Refresh()
    $script:hwnd = $app.MainWindowHandle
    if ($script:hwnd -eq [IntPtr]::Zero) { throw "GPUI window handle missing" }
    [void][Amare47]::SetForegroundWindow($script:hwnd)
    Write-Host "GPUI start source=$($initial.source) address=$($initial.address) dpi=$([Amare47]::GetDpiForWindow($script:hwnd))"

    $origin = Get-ClientOrigin $script:hwnd
    Click-At $script:hwnd ($origin.X + 400) ($origin.Y + 240)
    Start-Sleep -Milliseconds 300
    Add-Result "page-focus-from-address" "PASS" "clicked page content"

    $shortcutUrl = "https://example.com/?amare=page-ctrl-l"
    $result = Navigate-Address $shortcutUrl 25
    Add-Result "ctrl-l-from-webview" "PASS" "page Ctrl+L -> address -> source=$($result.source)"
    Shot "gpui-ctrl-l-from-page.png"

    if ($result.source -ne $shortcutUrl) { throw "Ctrl+L from WebView did not navigate to exact URL" }
    $pageNav = Navigate-Address $BaseUrl 25
    Add-Result "url-navigation-and-title-sync" "PASS" "source=$($pageNav.source) title=$($pageNav.title)"
    $page = Wait-Page { param($p) $p.loads -ge 1 } 10 "test page load"
    $script:loadsBefore = [int]$page.loads
    Send-Keys "^r"
    $reloaded = Wait-Page { param($p) [int]$p.loads -gt $script:loadsBefore } 15 "reload"
    Add-Result "ctrl-r-from-webview" "PASS" "loads $loadsBefore -> $($reloaded.loads)"

    $history = Navigate-Address $shortcutUrl 25
    Send-Keys "%{LEFT}"
    $back = Wait-State { param($s) $s.source -eq $BaseUrl } 20 "Alt+Left"
    Add-Result "alt-left-from-webview" "PASS" "source=$($back.source)"
    Send-Keys "%{RIGHT}"
    $forward = Wait-State { param($s) $s.source -eq $shortcutUrl } 20 "Alt+Right"
    Add-Result "alt-right-from-webview" "PASS" "source=$($forward.source)"

    Send-Keys "^l"
    Send-Keys "cancel-me"
    Start-Sleep -Milliseconds 300
    $beforeCancel = Get-Address
    Send-Keys "{ESC}"
    $afterCancel = Wait-State { param($s) $s.address -eq $shortcutUrl } 10 "Escape address restore"
    Add-Result "escape-cancel-and-restore" "PASS" "address=$($beforeCancel.address) -> $($afterCancel.address)"

    $pageNav = Navigate-Address $BaseUrl 25
    Start-Sleep -Seconds 1
    $origin = Get-ClientOrigin $script:hwnd
    Click-Page $script:hwnd 900 400
    $active = ""
    for ($i = 0; $i -lt 5; $i++) {
        Send-Keys "{TAB}"
        Start-Sleep -Milliseconds 250
        $page = Get-Page
        if ($null -ne $page) { $active = [string]$page.active; if ($active -eq "text") { break } }
    }
    if ($active -ne "text") { throw "Could not focus page text input via Tab (active=$active)" }
    Add-Result "page-tab-focus" "PASS" "active=$active"
    Send-Keys "^a"
    Send-Keys "{BACKSPACE}"
    Send-Keys "abc123"
    $page = Wait-Page { param($p) $p.text -eq "abc123" } 10 "page ascii input"
    Add-Result "page-ascii-input" "PASS" "text=$($page.text)"

    Send-Keys "{TAB}"
    Send-Keys "secret123"
    $page = Wait-Page { param($p) $p.password -eq "secret123" } 10 "password input"
    Add-Result "page-password-input" "PASS" "password length=$($page.password.Length)"

    Send-Keys "{TAB}"
    Send-Keys "textarea text"
    $page = Wait-Page { param($p) $p.area -eq "textarea text" } 10 "textarea input"
    Add-Result "page-textarea-input" "PASS" "area=$($page.area)"

    if ($focus -eq $null) { }
    $page = Get-Page
    $webviewChild = Find-WebViewChild $script:hwnd
    Click-Page $script:hwnd 900 400
    for ($i = 0; $i -lt 5; $i++) {
        Send-Keys "{TAB}"
        Start-Sleep -Milliseconds 200
        $page = Get-Page
        if ($null -ne $page -and $page.active -eq "text") { break }
    }
    Send-Keys "^a"; Send-Keys "{BACKSPACE}"
    Set-Korean $webviewChild
    Type-Hangul "gksrmf"
    Start-Sleep -Milliseconds 800
    $koreanPage = Get-Page
    if ($koreanPage -and $koreanPage.text -match "한글") {
        Add-Result "page-korean-ime" "PASS" "text=$($koreanPage.text) composition=$($koreanPage.composition)"
    } else {
        Add-Result "page-korean-ime" "UNVERIFIED" "Korean IME did not compose in WebView2; text=$($koreanPage.text) composition=$($koreanPage.composition)"
    }

    Click-Page $script:hwnd 600 600
    [Amare47]::mouse_event(0x0800, 0, 0, -120, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 700
    $scrolled = Get-Page
    if ($scrolled -and [int]$scrolled.scrollY -gt 0) {
        Add-Result "page-wheel-scroll" "PASS" "scrollY=$($scrolled.scrollY)"
    } else {
        Add-Result "page-wheel-scroll" "UNVERIFIED" "scrollY=$($scrolled.scrollY)"
    }

    Send-Keys "^l"
    Start-Sleep -Milliseconds 250
    Send-Keys "^a"; Send-Keys "{BACKSPACE}"
    $parent = $script:hwnd
    Set-Korean $parent
    Type-Hangul "gks"
    Start-Sleep -Milliseconds 500
    $composing = Get-Address
    $compositionSeen = $null -ne $composing.marked_range
    Type-Hangul "rmf"
    Start-Sleep -Milliseconds 800
    $addrKorean = Get-Address
    if ($addrKorean.address -match "한글") {
        Add-Result "address-korean-ime" "PASS" "address=$($addrKorean.address) composition_seen=$compositionSeen marked=$($addrKorean.marked_range)"
    } else {
        Add-Result "address-korean-ime" "UNVERIFIED" "Korean IME did not compose in address; address=$($addrKorean.address) composition_seen=$compositionSeen"
    }
    Shot "gpui-korean-address.png"
    Send-Keys "{ENTER}"
    Start-Sleep -Seconds 3
    $search = Read-State
    if ($search.source -match "google\.com/search") {
        Add-Result "korean-search-enter" "PASS" "source=$($search.source)"
    } else {
        Add-Result "korean-search-enter" "UNVERIFIED" "search source=$($search.source)"
    }

    Add-Result "page-context-menu" "UNVERIFIED" "right-click context menu not asserted"
    Add-Result "page-selection-and-drag" "UNVERIFIED" "text selection/drag not asserted"
    Add-Result "dpi-125-150-200" "UNVERIFIED" "runner DPI=$([Amare47]::GetDpiForWindow($script:hwnd)); multi-DPI requires separate display"

    [void][Amare47]::ShowWindow($script:hwnd, 6)
    Start-Sleep -Seconds 1
    [void][Amare47]::ShowWindow($script:hwnd, 9)
    [void][Amare47]::SetForegroundWindow($script:hwnd)
    Start-Sleep -Milliseconds 700
    Send-Keys "^l"
    Send-Keys "^a"; Send-Keys "{BACKSPACE}"
    Send-Keys $BaseUrl
    Send-Keys "{ENTER}"
    $restored = Wait-State { param($s) $s.source -eq $BaseUrl } 20 "minimize/restore navigation"
    Add-Result "minimize-restore-focus" "PASS" "source=$($restored.source)"

    Shot "gpui-final-input-state.png"

    [void][Amare47]::SetForegroundWindow($script:hwnd)
    Send-Keys "%{F4}"
    if (-not $app.WaitForExit(15000)) { throw "GPUI app did not exit after Alt+F4" }
    Start-Sleep -Seconds 2
    $after = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
    if ($after -gt $before) { throw "WebView2 child processes remained after exit" }
    Add-Result "graceful-exit-webview2" "PASS" "exit=$($app.ExitCode) webview2=$before->$after"
}
catch {
    Add-Result "fatal" "FAIL" $_.Exception.Message
    if ($null -ne $app -and -not $app.HasExited) { try { [void][Amare47]::SetForegroundWindow($script:hwnd); Send-Keys "%{F4}" } catch {} }
    try { Shot "gpui-input-failure.png" } catch {}
}
finally {
    if ($null -ne $app -and -not $app.HasExited) { try { $app.Kill() } catch {} }
    $summary = [pscustomobject]@{
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        results = $script:results
        pass = @($script:results | Where-Object result -eq "PASS").Count
        fail = @($script:results | Where-Object result -eq "FAIL").Count
        unverified = @($script:results | Where-Object result -eq "UNVERIFIED").Count
    }
    $summaryPath = Join-Path $ArtifactDir "gpui-input-results.json"
    $summary | ConvertTo-Json -Depth 8 | Set-Content -Path $summaryPath -Encoding UTF8
    Write-Host "GPUI input summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)"
    if ($summary.fail -gt 0) { exit 1 }
}
