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
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
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
function Fill-VaultField([string]$field, [string]$text, [int[]]$ys) {
    foreach ($y in $ys) {
        Click-Client $script:hwnd 600 $y
        Click-Client $script:hwnd 600 $y
        Send-Keys "^a"
        [System.Windows.Forms.SendKeys]::SendWait($text)
        Start-Sleep -Milliseconds 800
        $state = Read-State
        $value = switch ($field) {
            "origin" { $state.password_vault.form_origin }
            "username" { $state.password_vault.form_username }
            "password" { [string]$state.password_vault.form_password_len }
            default { "" }
        }
        $expected = if ($field -eq "password") { [string]$text.Length } else { $text }
        if ($value -eq $expected) { return $true }
    }
    return $false
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
function Unlock-Vault {
    foreach ($y in 230, 242, 250, 236) {
        foreach ($x in 640, 760, 900) {
            Click-Client $script:hwnd $x $y
            $state = Read-State
            if ($null -ne $state -and $state.password_vault.unlocked) { return }
        }
    }
    throw "Timed out waiting for vault unlock"
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
$secondApp = $null
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
    Send-Keys "^l"; Send-Keys "^a"; Send-Keys "{BACKSPACE}"
    [System.Windows.Forms.SendKeys]::SendWait("https://example.com/")
    Send-Keys "{ENTER}"
    [void](Wait-State { param($s) $s.source -like "https://example.com/*" } 25 "normal page")
    Start-Sleep -Milliseconds 700
    Shot "02-normal-page.png"
    Open-Passwords
    Shot "02-password-manager-panel.png"
    Unlock-Vault

    Click-Client $script:hwnd 990 159
    [void](Wait-State { param($s) $s.password_vault.form_open } 6 "add form")
    Shot "04-account-add.png"
    if (-not (Fill-VaultField "origin" "http://127.0.0.1:8767" @(250,258,266,274,282,290))) { throw "origin field not focused" }
    if (-not (Fill-VaultField "username" "alice" @(300,308,316,324,332,340,348))) { throw "username field not focused" }
    if (-not (Fill-VaultField "password" $passwordA @(358,366,374,382,390,398))) { throw "password field not focused" }
    Click-Client $script:hwnd 994 431
    [void](Wait-State { param($s) @($s.password_vault.entries).Count -eq 1 } 8 "first account saved")

    Click-Client $script:hwnd 990 159
    [void](Wait-State { param($s) $s.password_vault.form_open } 6 "second add form")
    if (-not (Fill-VaultField "origin" "http://127.0.0.1:8767" @(250,258,266,274,282,290))) { throw "origin field not focused (second)" }
    if (-not (Fill-VaultField "username" "bob" @(300,308,316,324,332,340,348))) { throw "username field not focused (second)" }
    if (-not (Fill-VaultField "password" $passwordB @(358,366,374,382,390,398))) { throw "password field not focused (second)" }
    Click-Client $script:hwnd 994 431
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

    $filled = $false
    $page = $null
    foreach ($y in 245, 249, 255, 260) {
        foreach ($x in 970, 982, 995) {
            Click-Client $script:hwnd $x $y
            Start-Sleep -Milliseconds 600
            $page = Get-PageState
            if ($null -ne $page -and $page.user -eq "alice" -and [int]$page.passwordLen -eq $passwordA.Length -and -not $page.submitted) {
                $filled = $true
                break
            }
        }
        if ($filled) { break }
    }
    if (-not $filled) { throw "autofill validation failed" }
    Add-Result "origin-scoped-autofill" "PASS" "user=alice passwordLen=$($passwordA.Length) submitted=false"

    $loginButton = $page.loginRect
    Click-Web $script:hwnd ([int]$loginButton.left + [int]($loginButton.width / 2)) ([int]$loginButton.top + [int]($loginButton.height / 2))
    [void](Wait-State { param($s) $null -ne $s.password_vault.pending } 8 "save proposal")
    Shot "08-save-proposal.png"
    Click-Client $script:hwnd 599 302
    [void](Wait-State { param($s) $null -eq $s.password_vault.pending } 8 "pending login saved")
    Add-Result "login-save-approval" "PASS" "pending cleared after explicit save"

    Close-Passwords
    function Navigate-NoCapture([string]$url, [string]$label, [string]$shot) {
        Send-Keys "^l"; Send-Keys "^a"; Send-Keys "{BACKSPACE}"
        [System.Windows.Forms.SendKeys]::SendWait($url)
        Send-Keys "{ENTER}"
        [void](Wait-State { param($s) $s.source -like "$url*" } 25 "$label page")
        Start-Sleep -Seconds 3
        $state = Read-State
        if ($null -ne $state.password_vault.pending) {
            throw "$label unexpectedly produced a credential capture"
        }
        Shot $shot
        Add-Result $label "PASS" "no credential capture; pending=null"
    }
    Navigate-NoCapture "http://127.0.0.1:8767/iframe-cross.html" "iframe-cross-origin-no-capture" "06-iframe-cross-origin.png"
    Navigate-NoCapture "http://127.0.0.1:8767/iframe-same.html" "iframe-same-origin-no-capture" "06b-iframe-same-origin.png"
    Navigate-NoCapture "http://127.0.0.1:8767/nested-iframe.html" "nested-iframe-no-capture" "06c-iframe-nested.png"
    Navigate-NoCapture "http://127.0.0.1:8767/action-cross.html" "action-cross-origin-rejected" "06d-action-cross-origin.png"
    Navigate-NoCapture "http://127.0.0.1:8767/action-change.html" "action-change-rejected" "06e-action-change.png"
    Click-Client $script:hwnd 966 66
    [void](Wait-State { param($s) $s.panel -eq "settings" } 8 "settings panel")
    Click-Client $script:hwnd 82 335
    [void](Wait-State { param($s) $s.settings_category -eq "privacy" } 8 "privacy category")
    Shot "11b-privacy-settings.png"
    Click-Client $script:hwnd 82 419
    [void](Wait-State { param($s) $s.settings_category -eq "permissions" } 8 "permissions category")
    Shot "10-site-permissions.png"
    Click-Client $script:hwnd 520 110
    [void](Wait-State { param($s) $s.panel -ne "settings" } 8 "settings closed")
    function Navigate-Cookie([string]$url, [string]$expected, [string]$label) {
        Send-Keys "^l"; Send-Keys "^a"; Send-Keys "{BACKSPACE}"
        [System.Windows.Forms.SendKeys]::SendWait($url)
        Send-Keys "{ENTER}"
        [void](Wait-State { param($s) $s.source -like "$url*" } 25 "$label page")
        Start-Sleep -Seconds 1
        $state = Wait-State { param($s) $s.title -like "AMARE_COOKIE:*" } 8 "$label title"
        if ($state.title -ne "AMARE_COOKIE:$expected") { throw "$label cookie expected $expected got $($state.title)" }
    }
    Navigate-Cookie "http://127.0.0.1:8767/cookie-test.html?set=1" "true" "cookie A set"
    Navigate-Cookie "http://localhost:8767/cookie-test.html?set=1" "true" "cookie B set"
    Shot "10a-cookie-before.png"
    Click-Client $script:hwnd 966 66
    [void](Wait-State { param($s) $s.panel -eq "settings" } 8 "settings for clear")
    Click-Client $script:hwnd 82 419
    [void](Wait-State { param($s) $s.settings_category -eq "permissions" } 8 "permissions for clear")
    Click-Client $script:hwnd 1000 435
    Click-Client $script:hwnd 1000 435
    [void](Wait-State { param($s) $s.site_data_cleared } 12 "site data clear")
    Add-Result "all-site-clear" "PASS" "ClearBrowsingData(ALL_SITE) completed"
    Shot "10c-site-data-cleared.png"
    Click-Client $script:hwnd 520 110
    [void](Wait-State { param($s) $s.panel -ne "settings" } 8 "settings closed after clear")
    Navigate-Cookie "http://127.0.0.1:8767/cookie-test.html" "false" "cookie A cleared"
    Navigate-Cookie "http://localhost:8767/cookie-test.html" "false" "cookie B cleared"
    Add-Result "all-site-cookie-scope" "PASS" "cookies for 127.0.0.1 and localhost removed"
    Shot "10b-cookie-after.png"
    Click-Client $script:hwnd 966 66
    [void](Wait-State { param($s) $s.panel -eq "settings" } 8 "settings final")
    Click-Client $script:hwnd 82 167
    [void](Wait-State { param($s) $s.settings_category -eq "appearance" } 8 "appearance category")
    Shot "11-settings.png"
    Shot "12-titlebar-maximized.png"

    $plaintextFound = $false
    foreach ($file in Get-ChildItem $DataDir -Recurse -File) {
        $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $memory = New-Object IO.MemoryStream
            $stream.CopyTo($memory)
            $text = [Text.Encoding]::UTF8.GetString($memory.ToArray())
            if ($text.Contains($passwordA) -or $text.Contains($passwordB)) { $plaintextFound = $true; break }
        } finally {
            $stream.Dispose()
        }
        if ($plaintextFound) { break }
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
    Start-Sleep -Seconds 2
    $app = Start-Process $Exe -PassThru
    [void](Wait-State { param($s) $null -ne $s.address } 60 "vault restart app")
    Start-Sleep -Seconds 2
    $app.Refresh(); $script:hwnd = $app.MainWindowHandle
    [void][AmarePassword]::SetForegroundWindow($script:hwnd)
    [void][AmarePassword]::ShowWindow($script:hwnd, 3)
    Start-Sleep -Seconds 1
    Open-Passwords
    Unlock-Vault
    [void](Wait-State { param($s) @($s.password_vault.entries).Count -eq 2 } 10 "vault restart unlock")
    Add-Result "vault-restart-dpapi" "PASS" "2 accounts restored after restart"
    $deleteX = 0
    $deleteY = 0
    $confirmed = $false
    foreach ($y in 420, 428, 436, 444) {
        foreach ($x in 990, 1000, 1010) {
            Click-Client $script:hwnd $x $y
            $state = Read-State
            if ($null -ne $state.password_vault.confirm_delete) {
                $deleteX = $x; $deleteY = $y; $confirmed = $true; break
            }
        }
        if ($confirmed) { break }
    }
    if (-not $confirmed) { throw "Timed out waiting for delete confirmation" }
    Shot "07-delete-confirm.png"
    Click-Client $script:hwnd 978 $deleteY
    [void](Wait-State { param($s) $null -eq $s.password_vault.confirm_delete -and @($s.password_vault.entries).Count -eq 2 } 8 "vault delete cancel")
    Add-Result "vault-delete-cancel" "PASS" "cancel kept both accounts"
    Click-Client $script:hwnd 997 $deleteY
    [void](Wait-State { param($s) $null -ne $s.password_vault.confirm_delete } 6 "vault delete reconfirm")
    Click-Client $script:hwnd 925 $deleteY
    [void](Wait-State { param($s) @($s.password_vault.entries).Count -eq 1 -and $null -eq $s.password_vault.confirm_delete } 8 "vault delete")
    Add-Result "vault-delete-confirm" "PASS" "confirmation shown and one account removed"
    Shot "07b-delete-complete.png"
    Send-Keys "%{F4}"
    if (-not $app.WaitForExit(20000)) { throw "deleted vault app did not exit" }
    Start-Sleep -Seconds 2
    $app = Start-Process $Exe -PassThru
    [void](Wait-State { param($s) $null -ne $s.address } 60 "deleted vault restart")
    Start-Sleep -Seconds 2
    $app.Refresh(); $script:hwnd = $app.MainWindowHandle
    [void][AmarePassword]::SetForegroundWindow($script:hwnd)
    [void][AmarePassword]::ShowWindow($script:hwnd, 3)
    Start-Sleep -Seconds 1
    Open-Passwords
    Unlock-Vault
    [void](Wait-State { param($s) @($s.password_vault.entries).Count -eq 1 } 10 "deleted vault persistence")
    Add-Result "vault-delete-restart-persistence" "PASS" "one account remains after restart"
    $lastConfirmed = $false
    $lastY = 0
    foreach ($y in 330, 338, 346) {
        Click-Client $script:hwnd 997 $y
        $state = Read-State
        if ($null -ne $state.password_vault.confirm_delete) { $lastY = $y; $lastConfirmed = $true; break }
    }
    if (-not $lastConfirmed) { throw "Timed out waiting for last-account delete confirmation" }
    Click-Client $script:hwnd 925 $lastY
    [void](Wait-State { param($s) @($s.password_vault.entries).Count -eq 0 -and $null -eq $s.password_vault.confirm_delete } 8 "last account delete")
    Add-Result "vault-last-account-empty" "PASS" "empty vault and empty-state UI"
    Shot "07c-vault-empty.png"
    Send-Keys "%{F4}"
    if (-not $app.WaitForExit(20000)) { throw "empty vault app did not exit" }
    Start-Sleep -Seconds 2
    $app = Start-Process $Exe -PassThru
    [void](Wait-State { param($s) $null -ne $s.address } 60 "empty vault restart")
    Start-Sleep -Seconds 2
    $app.Refresh(); $script:hwnd = $app.MainWindowHandle
    [void][AmarePassword]::SetForegroundWindow($script:hwnd)
    [void][AmarePassword]::ShowWindow($script:hwnd, 3)
    Start-Sleep -Seconds 1
    Open-Passwords
    Unlock-Vault
    [void](Wait-State { param($s) @($s.password_vault.entries).Count -eq 0 } 10 "empty vault persistence")
    Add-Result "vault-empty-restart-persistence" "PASS" "empty vault remains empty after restart"
    Send-Keys "%{F4}"
    if (-not $app.WaitForExit(20000)) { throw "empty persistence app did not exit" }
    Start-Sleep -Seconds 2

    $keyPath = Join-Path $DataDir "password-vault\vault.key"
    $vaultPath = Join-Path $DataDir "password-vault\vault.bin"
    $vaultBeforeKeyTest = (Get-FileHash $vaultPath -Algorithm SHA256).Hash
    Move-Item $keyPath "$keyPath.missing" -Force
    $app = Start-Process $Exe -PassThru
    [void](Wait-State { param($s) $null -ne $s.address } 60 "missing key app")
    Start-Sleep -Seconds 2
    $app.Refresh(); $script:hwnd = $app.MainWindowHandle
    [void][AmarePassword]::SetForegroundWindow($script:hwnd)
    [void][AmarePassword]::ShowWindow($script:hwnd, 3)
    Start-Sleep -Seconds 1
    Open-Passwords
    $missingKeyError = $false
    foreach ($y in 230, 242, 250) {
        foreach ($x in 640, 760, 900) {
            Click-Client $script:hwnd $x $y
            $state = Read-State
            if ($null -ne $state.password_vault.error) { $missingKeyError = $true; break }
        }
        if ($missingKeyError) { break }
    }
    if (-not $missingKeyError) { throw "missing key did not produce an error" }
    if ((Get-FileHash $vaultPath -Algorithm SHA256).Hash -ne $vaultBeforeKeyTest) { throw "vault changed while key was missing" }
    Add-Result "vault-key-missing-preserved" "PASS" "existing vault preserved when key missing"
    Send-Keys "%{F4}"
    if (-not $app.WaitForExit(20000)) { throw "missing key app did not exit" }
    Move-Item "$keyPath.missing" $keyPath -Force
    Start-Sleep -Seconds 2

    $beforeHash = (Get-FileHash $vaultPath -Algorithm SHA256).Hash
    $bytes = [IO.File]::ReadAllBytes($vaultPath)
    $bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 1
    [IO.File]::WriteAllBytes($vaultPath, $bytes)
    $corruptHash = (Get-FileHash $vaultPath -Algorithm SHA256).Hash
    if ($corruptHash -eq $beforeHash) { throw "failed to corrupt vault fixture" }
    $app = Start-Process $Exe -PassThru
    [void](Wait-State { param($s) $null -ne $s.address } 60 "corrupt vault app")
    Start-Sleep -Seconds 2
    $app.Refresh(); $script:hwnd = $app.MainWindowHandle
    [void][AmarePassword]::SetForegroundWindow($script:hwnd)
    [void][AmarePassword]::ShowWindow($script:hwnd, 3)
    Start-Sleep -Seconds 1
    Open-Passwords
    $corruptError = $false
    foreach ($y in 230, 242, 250) {
        foreach ($x in 640, 760, 900) {
            Click-Client $script:hwnd $x $y
            $state = Read-State
            if ($null -ne $state.password_vault.error) { $corruptError = $true; break }
        }
        if ($corruptError) { break }
    }
    if (-not $corruptError) { throw "Timed out waiting for corrupt vault error" }
    $afterHash = (Get-FileHash $vaultPath -Algorithm SHA256).Hash
    if ($afterHash -ne $corruptHash) { throw "corrupt vault was overwritten after unlock failure" }
    Add-Result "vault-corruption-preserved" "PASS" "original encrypted vault unchanged after failed unlock"
    Shot "09-dpapi-error.png"
    Close-Passwords
    Send-Keys "^l"; Send-Keys "^a"; Send-Keys "{BACKSPACE}"
    [System.Windows.Forms.SendKeys]::SendWait("http://127.0.0.1:8767/autotab.html")
    Send-Keys "{ENTER}"
    [void](Wait-State { param($s) @($s.tabs).Count -ge 2 } 15 "multi-tab final")
    Start-Sleep -Milliseconds 800
    Shot "03-multiple-tabs.png"
    $secondApp = Start-Process $Exe -PassThru
    Start-Sleep -Seconds 3
    [void][AmarePassword]::SetForegroundWindow($script:hwnd)
    ShotFull "04-multi-window.png"
    if ($null -ne $secondApp -and -not $secondApp.HasExited) { Stop-Process -Id $secondApp.Id -Force }
    Send-Keys "%{F4}"
    if (-not $app.WaitForExit(20000)) { throw "corrupt vault app did not exit" }
} catch {
    Add-Result "fatal" "FAIL" $_.Exception.Message
    try { Shot "99-password-failure.png" } catch {}
} finally {
    if ($null -ne $secondApp -and -not $secondApp.HasExited) { try { $secondApp.Kill() } catch {} }
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
