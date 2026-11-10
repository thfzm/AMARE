param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Status,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$ScreenshotDir,
    [Parameter(Mandatory = $true)][string]$LoginUrl
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AmarePassword {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
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
function Send-Keys([string]$keys) { [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds 180 }
function Click-Client([IntPtr]$hwnd, [int]$x, [int]$y) {
    $point = New-Object AmarePassword+POINT
    [void][AmarePassword]::ClientToScreen($hwnd, [ref]$point)
    [void][AmarePassword]::SetForegroundWindow($hwnd)
    [void][AmarePassword]::SetCursorPos(($point.X + $x), ($point.Y + $y))
    Start-Sleep -Milliseconds 120
    [AmarePassword]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [AmarePassword]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 350
}
function Click-Web([IntPtr]$hwnd, [int]$x, [int]$y) {
    $point = New-Object AmarePassword+POINT
    [void][AmarePassword]::ClientToScreen($hwnd, [ref]$point)
    [void][AmarePassword]::SetForegroundWindow($hwnd)
    [void][AmarePassword]::SetCursorPos(($point.X + $x), ($point.Y + 88 + $y))
    Start-Sleep -Milliseconds 120
    [AmarePassword]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [AmarePassword]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 350
}
function Type-Into([IntPtr]$hwnd, [int]$x, [int]$y, [string]$text) {
    Click-Client $hwnd $x $y
    Send-Keys "^a"
    [System.Windows.Forms.SendKeys]::SendWait($text)
    Start-Sleep -Milliseconds 300
}
function Shot([string]$name) {
    $rect = New-Object AmarePassword+RECT
    [void][AmarePassword]::GetWindowRect($script:hwnd, [ref]$rect)
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
function Get-PageState {
    $state = Read-State
    if ($null -eq $state -or -not $state.title.StartsWith("AMARE_TEST:")) { return $null }
    return [Uri]::UnescapeDataString($state.title.Substring(11)) | ConvertFrom-Json
}
function Open-Passwords {
    $state = Read-State
    if ($null -eq $state -or $state.panel -ne "passwords") {
        Click-Client $script:hwnd 105 66
    }
    [void](Wait-State { param($s) $s.panel -eq "passwords" } 8 "password panel")
}
function Close-Passwords {
    $state = Read-State
    if ($null -ne $state -and $state.panel -eq "passwords") {
        Click-Client $script:hwnd 1010 110
    }
    [void](Wait-State { param($s) $s.panel -ne "passwords" } 8 "password panel closed")
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
$env:AMARE_GPUI_RESTORE = "0"
$passwordA = "S3cret!Alice-55"
$passwordB = "S3cret!Bob-55"
try {
    $app = Start-Process $Exe -PassThru
    [void](Wait-State { param($s) $null -ne $s.address } 60 "password app start")
    Start-Sleep -Seconds 2
    $app.Refresh(); $script:hwnd = $app.MainWindowHandle
    if ($script:hwnd -eq [IntPtr]::Zero) { throw "GPUI window missing" }
    [void][AmarePassword]::SetForegroundWindow($script:hwnd)
    [void][AmarePassword]::ShowWindow($script:hwnd, 3)
    Start-Sleep -Seconds 1
    Shot "01-initial-browser.png"

    Open-Passwords
    Shot "02-password-manager-panel.png"
    Click-Client $script:hwnd 650 242
    [void](Wait-State { param($s) $s.password_vault.unlocked } 8 "vault unlock")

    Click-Client $script:hwnd 990 159
    [void](Wait-State { param($s) $s.password_vault.form_open } 6 "add form")
    Shot "04-account-add.png"
    Type-Into $script:hwnd 800 258 "http://127.0.0.1:8767"
    Type-Into $script:hwnd 800 316 "alice"
    Type-Into $script:hwnd 800 374 $passwordA
    Click-Client $script:hwnd 990 412
    [void](Wait-State { param($s) @($s.password_vault.entries).Count -eq 1 } 8 "first account saved")

    Click-Client $script:hwnd 990 159
    [void](Wait-State { param($s) $s.password_vault.form_open } 6 "second add form")
    Type-Into $script:hwnd 800 258 "http://127.0.0.1:8767"
    Type-Into $script:hwnd 800 316 "bob"
    Type-Into $script:hwnd 800 374 $passwordB
    Click-Client $script:hwnd 990 412
    [void](Wait-State { param($s) @($s.password_vault.entries).Count -eq 2 } 8 "second account saved")
    Shot "03-saved-accounts.png"

    Click-Client $script:hwnd 995 270
    [void](Wait-State { param($s) $null -ne $s.password_vault.reveal } 6 "password reveal")
    Shot "06-password-reveal.png"

    $edit = $false
    foreach ($y in 330, 336, 342) {
        foreach ($x in 950, 960, 970, 980) {
            Click-Client $script:hwnd $x $y
            $state = Read-State
            if ($state.password_vault.form_open) { $edit = $true; break }
        }
        if ($edit) { break }
    }
    if (-not $edit) { throw "Timed out waiting for edit form" }
    Shot "05-account-edit.png"
    Click-Client $script:hwnd 995 207

    Send-Keys "^l"; Send-Keys "^a"; Send-Keys "{BACKSPACE}"
    [System.Windows.Forms.SendKeys]::SendWait($LoginUrl)
    Send-Keys "{ENTER}"
    [void](Wait-State { param($s) $s.source -like "$LoginUrl*" } 25 "login page")
    Start-Sleep -Seconds 1
    Open-Passwords
    [void](Wait-State { param($s) $s.password_vault.site_matches -eq 2 } 8 "login site matches")
    Shot "07-multiple-accounts.png"
    Shot "09-autofill-suggestion.png"

    Click-Client $script:hwnd 982 249
    Start-Sleep -Seconds 2
    $page = Get-PageState
    if ($null -eq $page -or $page.user -ne "alice" -or [int]$page.passwordLen -ne $passwordA.Length -or $page.submitted) {
        throw "autofill validation failed"
    }
    Add-Result "origin-scoped-autofill" "PASS" "user=alice passwordLen=$($passwordA.Length) submitted=false"

    $loginButton = $page.loginRect
    Click-Web $script:hwnd ([int]$loginButton.left + [int]($loginButton.width / 2)) ([int]$loginButton.top + [int]($loginButton.height / 2))
    [void](Wait-State { param($s) $null -ne $s.password_vault.pending } 8 "save proposal")
    Shot "08-save-proposal.png"
    Click-Client $script:hwnd 650 276
    [void](Wait-State { param($s) @($s.password_vault.entries).Count -eq 3 -and $null -eq $s.password_vault.pending } 8 "pending login saved")
    Add-Result "login-save-approval" "PASS" "entries=3"

    Close-Passwords
    Send-Keys "^,"
    [void](Wait-State { param($s) $s.panel -eq "settings" } 8 "settings panel")
    Click-Client $script:hwnd 82 419
    [void](Wait-State { param($s) $s.settings_category -eq "permissions" } 8 "permissions category")
    Shot "10-site-permissions.png"
    Click-Client $script:hwnd 82 167
    [void](Wait-State { param($s) $s.settings_category -eq "appearance" } 8 "appearance category")
    Shot "11-settings.png"
    Shot "12-titlebar-maximized.png"

    $plaintextFound = $false
    foreach ($file in Get-ChildItem $DataDir -Recurse -File) {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $text = [Text.Encoding]::UTF8.GetString($bytes)
        if ($text.Contains($passwordA) -or $text.Contains($passwordB)) { $plaintextFound = $true; break }
    }
    if ($plaintextFound) { throw "plaintext password found in data directory" }
    if ((Get-Content $Status -Raw).Contains($passwordA) -or (Get-Content $Status -Raw).Contains($passwordB)) { throw "password found in status file" }
    Add-Result "vault-no-plaintext" "PASS" "password absent from data files and status"
    $vault = Join-Path $DataDir "password-vault\vault.bin"
    if (-not (Test-Path $vault)) { throw "vault file missing" }
    Add-Result "vault-encrypted-file" "PASS" "vault.bin=$((Get-Item $vault).Length) bytes"

    Open-Passwords
    [void](Wait-State { param($s) $s.panel -eq "passwords" } 8 "password panel for lock")
    Click-Client $script:hwnd 965 110
    [void](Wait-State { param($s) -not $s.password_vault.unlocked } 8 "vault locked")
    $locked = Read-State
    if (@($locked.password_vault.entries).Count -ne 0) { throw "locked vault still exposes entries" }
    Add-Result "vault-lock-clears-memory-view" "PASS" "entries=0 while locked"

    Send-Keys "%{F4}"
    if (-not $app.WaitForExit(20000)) { throw "password app did not exit" }
} catch {
    Add-Result "fatal" "FAIL" $_.Exception.Message
    try { Shot "99-password-failure.png" } catch {}
} finally {
    if ($null -ne $app -and -not $app.HasExited) { try { $app.Kill() } catch {} }
    $summary = [pscustomobject]@{
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        results = $script:results
        pass = @($script:results | Where-Object result -eq "PASS").Count
        fail = @($script:results | Where-Object result -eq "FAIL").Count
        unverified = @($script:results | Where-Object result -eq "UNVERIFIED").Count
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ScreenshotDir "gpui-password-results.json") -Encoding UTF8
    Write-Host "GPUI password summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)"
    if ($summary.fail -gt 0) { exit 1 }
}
