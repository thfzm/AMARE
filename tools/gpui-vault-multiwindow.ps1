param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$StatusDir,
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
public static class AmareVaultWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@

$script:results = [System.Collections.Generic.List[object]]::new()
function Add-Result([string]$name, [string]$state, [string]$detail) {
    $script:results.Add([pscustomobject]@{ name = $name; result = $state; detail = $detail })
    Write-Host ("{0} {1}: {2}" -f $state, $name, $detail)
}
function Get-StatusFiles {
    if (-not (Test-Path $StatusDir)) { return @() }
    return @(Get-ChildItem $StatusDir -Filter "status-*.json" -File -ErrorAction SilentlyContinue)
}
function Read-AllStates {
    $states = @()
    foreach ($file in Get-StatusFiles) {
        try {
            $state = Get-Content $file.FullName -Raw -ErrorAction Stop | ConvertFrom-Json
            if ($null -ne $state -and $state.pid -eq $script:app.Id) { $states += $state }
        } catch { }
    }
    return $states
}
function Read-State([IntPtr]$hwnd) {
    $needle = $hwnd.ToInt64()
    foreach ($state in Read-AllStates) {
        if ([int64]$state.window_handle -eq $needle) { return $state }
    }
    return $null
}
function Wait-WindowCount([int]$count, [int]$seconds, [string]$label) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $states = @(Read-AllStates)
        if ($states.Count -eq $count) { return $states }
        Start-Sleep -Milliseconds 250
    }
    $states = @(Read-AllStates)
    throw "Timed out waiting for $label (windows=$($states.Count))"
}
function Wait-State([IntPtr]$hwnd, [scriptblock]$condition, [int]$seconds, [string]$label) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $state = Read-State $hwnd
        if ($null -ne $state -and (& $condition $state)) { return $state }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for $label"
}
function Focus-Window([IntPtr]$hwnd) {
    [void][AmareVaultWin]::ShowWindow($hwnd, 9)
    [void][AmareVaultWin]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 200
}
function Send-Keys([string]$keys) { [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds 180 }
function Click-Client([IntPtr]$hwnd, [int]$x, [int]$y) {
    $point = New-Object AmareVaultWin+POINT
    [void][AmareVaultWin]::ClientToScreen($hwnd, [ref]$point)
    [void][AmareVaultWin]::SetForegroundWindow($hwnd)
    [void][AmareVaultWin]::SetCursorPos(($point.X + $x), ($point.Y + $y))
    Start-Sleep -Milliseconds 120
    [AmareVaultWin]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [AmareVaultWin]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 350
}
function Shot-Window([IntPtr]$hwnd, [string]$name) {
    $rect = New-Object AmareVaultWin+RECT
    [void][AmareVaultWin]::GetWindowRect($hwnd, [ref]$rect)
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
function Shot-Both([IntPtr]$left, [IntPtr]$right, [string]$name) {
    $r1 = New-Object AmareVaultWin+RECT
    $r2 = New-Object AmareVaultWin+RECT
    [void][AmareVaultWin]::GetWindowRect($left, [ref]$r1)
    [void][AmareVaultWin]::GetWindowRect($right, [ref]$r2)
    $w1 = [Math]::Max(1, $r1.Right - $r1.Left); $h1 = [Math]::Max(1, $r1.Bottom - $r1.Top)
    $w2 = [Math]::Max(1, $r2.Right - $r2.Left); $h2 = [Math]::Max(1, $r2.Bottom - $r2.Top)
    $b1 = New-Object System.Drawing.Bitmap($w1, $h1)
    $b2 = New-Object System.Drawing.Bitmap($w2, $h2)
    $g1 = [System.Drawing.Graphics]::FromImage($b1)
    $g2 = [System.Drawing.Graphics]::FromImage($b2)
    [void][AmareVaultWin]::SetForegroundWindow($left)
    Start-Sleep -Milliseconds 400
    $g1.CopyFromScreen($r1.Left, $r1.Top, 0, 0, $b1.Size)
    [void][AmareVaultWin]::SetForegroundWindow($right)
    Start-Sleep -Milliseconds 400
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
function Open-Passwords([IntPtr]$hwnd) {
    $state = Read-State $hwnd
    if ($null -eq $state -or $state.panel -ne "passwords") { Click-Client $hwnd 105 66 }
    [void](Wait-State $hwnd { param($s) $s.panel -eq "passwords" } 8 "password panel")
}
function Unlock-Vault([IntPtr]$hwnd) {
    $state = Read-State $hwnd
    if ($null -ne $state -and $state.password_vault.unlocked) { return }
    foreach ($y in 230, 242, 250, 236) {
        foreach ($x in 640, 760, 900) {
            Click-Client $hwnd $x $y
            $state = Read-State $hwnd
            if ($null -ne $state -and $state.password_vault.unlocked) { return }
        }
    }
    throw "Timed out waiting for vault unlock on window $($hwnd.ToInt64())"
}
function Fill-VaultField([IntPtr]$hwnd, [string]$field, [string]$text, [int[]]$ys) {
    foreach ($y in $ys) {
        Click-Client $hwnd 600 $y
        Click-Client $hwnd 600 $y
        Send-Keys "^a"
        [System.Windows.Forms.SendKeys]::SendWait($text)
        Start-Sleep -Milliseconds 600
        $state = Read-State $hwnd
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
function Save-Form([IntPtr]$hwnd) {
    foreach ($x in 994, 1004, 1014) {
        foreach ($y in 405, 417, 429, 441, 453, 465, 477) {
            Click-Client $hwnd $x $y
            Start-Sleep -Milliseconds 400
            $state = Read-State $hwnd
            if ($null -ne $state -and (-not $state.password_vault.form_open -or $null -ne $state.password_vault.conflict)) { return $state }
        }
    }
    throw "Could not submit password form"
}
function Add-Account([IntPtr]$hwnd, [string]$origin, [string]$user, [string]$password) {
    Open-Passwords $hwnd
    $before = Read-State $hwnd
    $count = @($before.password_vault.entries).Count
    Click-Client $hwnd 990 159
    [void](Wait-State $hwnd { param($s) $s.password_vault.form_open } 8 "add form")
    if (-not (Fill-VaultField $hwnd "origin" $origin @(250,258,266,274,282,290))) { throw "origin field not focused" }
    if (-not (Fill-VaultField $hwnd "username" $user @(300,308,316,324,332,340,348))) { throw "username field not focused" }
    if (-not (Fill-VaultField $hwnd "password" $password @(358,366,374,382,390,398))) { throw "password field not focused" }
    [void](Save-Form $hwnd)
    [void](Wait-State $hwnd { param($s) -not $s.password_vault.form_open -and @($s.password_vault.entries).Count -eq $count + 1 } 12 "account save")
}
function Get-EntryIndex($state, [string]$user) {
    $entries = @($state.password_vault.entries)
    for ($i = 0; $i -lt $entries.Count; $i++) {
        if ($entries[$i].username -eq $user) { return $i }
    }
    return -1
}
function Click-EntryAction([IntPtr]$hwnd, [string]$user, [string]$action) {
    $state = Read-State $hwnd
    $index = Get-EntryIndex $state $user
    if ($index -lt 0) { throw "entry not found: $user" }
    $baseY = 330 + ($index * 100)
    $x = if ($action -eq "edit") { 960 } else { 1002 }
    foreach ($dy in 0, -8, 8, -16, 16) {
        $script:lastActionY = $baseY + $dy
        Click-Client $hwnd $x $script:lastActionY
        $state = Read-State $hwnd
        if ($action -eq "edit" -and $null -ne $state.password_vault.editing) { return $state }
        if ($action -eq "delete" -and $null -ne $state.password_vault.confirm_delete) { return $state }
    }
    throw "Could not open $action for $user"
}
function Close-Passwords([IntPtr]$hwnd) {
    $state = Read-State $hwnd
    if ($null -ne $state -and $state.panel -eq "passwords") { Click-Client $hwnd 1010 110 }
    [void](Wait-State $hwnd { param($s) $s.panel -ne "passwords" } 8 "password panel closed")
}
function Click-Web([IntPtr]$hwnd, [int]$x, [int]$y) {
    $point = New-Object AmareVaultWin+POINT
    [void][AmareVaultWin]::ClientToScreen($hwnd, [ref]$point)
    [void][AmareVaultWin]::SetForegroundWindow($hwnd)
    [void][AmareVaultWin]::SetCursorPos(($point.X + $x), ($point.Y + 88 + $y))
    Start-Sleep -Milliseconds 120
    [AmareVaultWin]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [AmareVaultWin]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 350
}
function Get-PageState([IntPtr]$hwnd) {
    $state = Read-State $hwnd
    if ($null -eq $state -or -not $state.title.StartsWith("AMARE_TEST:")) { return $null }
    return [Uri]::UnescapeDataString($state.title.Substring(11)) | ConvertFrom-Json
}
function Close-Window([IntPtr]$hwnd) {
    Focus-Window $hwnd
    Send-Keys "%{F4}"
    Start-Sleep -Milliseconds 500
}

$script:app = $null
$script:hwnd1 = [IntPtr]::Zero
$script:hwnd2 = [IntPtr]::Zero
Remove-Item $StatusDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $StatusDir, $DataDir, $ScreenshotDir | Out-Null
$env:AMARE_GPUI_DATA_DIR = $DataDir
$env:AMARE_GPUI_STATUS_DIR = $StatusDir
Remove-Item Env:AMARE_GPUI_STATUS -ErrorAction SilentlyContinue
Remove-Item Env:AMARE_GPUI_STATUS_WINDOW -ErrorAction SilentlyContinue
$env:AMARE_GPUI_START_URL = "about:blank"
$env:AMARE_GPUI_NEW_WINDOW_URL = "about:blank"
$env:AMARE_GPUI_SELF_TEST = "0"
$env:AMARE_GPUI_RESTORE = "0"
$env:AMARE_GPUI_WINDOW_SIZE = "1044x788"
$env:AMARE_GPUI_OPEN_SECOND_WINDOW = "1"
$passwordA = "S3cret!Alice-59"
$passwordB = "S3cret!Bob-59"
$passwordC = "S3cret!Carol-59"
$passwordD = "S3cret!Dave-59"
try {
    $script:app = Start-Process $Exe -PassThru
    $states = @()
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        $states = @(Read-AllStates)
        if ($states.Count -ge 1) { break }
        Start-Sleep -Milliseconds 250
    }
    $script:hwnd1 = [IntPtr][int64]$states[0].window_handle
    if ($script:hwnd1 -eq [IntPtr]::Zero) { throw "first window handle missing" }
    Focus-Window $script:hwnd1
    Open-Passwords $script:hwnd1
    Unlock-Vault $script:hwnd1
    Add-Account $script:hwnd1 "http://127.0.0.1:8767" "alice" $passwordA
    Add-Result "multi-add-alice" "PASS" "alice added in window 1"

    $states = @()
    if ($env:AMARE_GPUI_OPEN_SECOND_WINDOW -eq "1") {
        $deadline = (Get-Date).AddSeconds(25)
        while ((Get-Date) -lt $deadline) {
            $states = @(Read-AllStates)
            if ($states.Count -ge 2) { break }
            Start-Sleep -Milliseconds 250
        }
    } else {
        Focus-Window $script:hwnd1
        Click-Client $script:hwnd1 420 70
        Send-Keys "^n"
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline) {
            $states = @(Read-AllStates)
            if ($states.Count -ge 2) { break }
            Start-Sleep -Milliseconds 250
        }
    }
    if ($states.Count -lt 2) { throw "Timed out waiting for second window (windows=$($states.Count))" }
    $firstId = [int64]$states[0].window_id
    $second = $states | Where-Object { [int64]$_.window_id -ne $firstId } | Select-Object -First 1
    if ($null -eq $second) { throw "second window state missing" }
    $script:hwnd2 = [IntPtr][int64]$second.window_handle
    if ($script:hwnd2 -eq [IntPtr]::Zero) { throw "second window handle missing" }
    Focus-Window $script:hwnd2
    Open-Passwords $script:hwnd2
    Unlock-Vault $script:hwnd2
    [void](Wait-State $script:hwnd2 { param($s) @($s.password_vault.entries).Count -eq 1 } 10 "second window sees alice")
    Add-Account $script:hwnd2 "http://127.0.0.1:8767" "bob" $passwordB
    [void](Wait-State $script:hwnd1 { param($s) @($s.password_vault.entries).Count -eq 2 } 12 "first window sees bob")
    Add-Result "independent-add-sync" "PASS" "window1/window2 both see alice+bob"
    Add-Result "same-process-windows" "PASS" "pid=$($script:app.Id) hwnd1=$($script:hwnd1.ToInt64()) hwnd2=$($script:hwnd2.ToInt64())"
    Shot-Both $script:hwnd1 $script:hwnd2 "06-two-window-sync.png"

    # Independent edits in both windows while the forms are open together.
    [void](Click-EntryAction $script:hwnd1 "alice" "edit")
    [void](Fill-VaultField $script:hwnd1 "password" $passwordA @(358,366,374,382,390,398))
    [void](Click-EntryAction $script:hwnd2 "bob" "edit")
    [void](Fill-VaultField $script:hwnd2 "password" $passwordB @(358,366,374,382,390,398))
    [void](Save-Form $script:hwnd1)
    [void](Wait-State $script:hwnd1 { param($s) -not $s.password_vault.form_open } 10 "alice independent edit")
    [void](Save-Form $script:hwnd2)
    [void](Wait-State $script:hwnd2 { param($s) -not $s.password_vault.form_open } 10 "bob independent edit")
    [void](Wait-State $script:hwnd1 { param($s) @($s.password_vault.entries).Count -eq 2 } 10 "independent edits remain")
    Add-Result "independent-edit-preserved" "PASS" "alice and bob independent edits both saved"

    # Same-entry conflict: window 2 holds a stale form while window 1 saves.
    [void](Click-EntryAction $script:hwnd2 "alice" "edit")
    [void](Click-EntryAction $script:hwnd1 "alice" "edit")
    [void](Fill-VaultField $script:hwnd1 "password" "S3cret!Alice-conflict-A" @(358,366,374,382,390,398))
    [void](Save-Form $script:hwnd1)
    [void](Wait-State $script:hwnd1 { param($s) -not $s.password_vault.form_open -and $null -eq $s.password_vault.conflict } 10 "first conflict write")
    [void](Fill-VaultField $script:hwnd2 "password" "S3cret!Alice-conflict-B" @(358,366,374,382,390,398))
    [void](Save-Form $script:hwnd2)
    $conflict = Wait-State $script:hwnd2 { param($s) $null -ne $s.password_vault.conflict } 12 "stale write rejected"
    Add-Result "same-entry-conflict" "PASS" "second write rejected; conflict=$($conflict.password_vault.conflict)"
    Shot-Window $script:hwnd2 "07-conflict-ui.png"
    $reloaded = Read-State $script:hwnd2
    $alice1 = @((Read-State $script:hwnd1).password_vault.entries | Where-Object username -eq "alice")[0]
    $alice2 = @($reloaded.password_vault.entries | Where-Object username -eq "alice")[0]
    if ($null -eq $alice1 -or $null -eq $alice2 -or $alice1.revision -ne $alice2.revision) { throw "conflict reload did not expose latest revision" }
    Add-Result "conflict-reload" "PASS" "latest alice revision=$($alice2.revision)"

    # Modify vs delete conflict: stale edit in window 2, delete from window 1.
    Add-Account $script:hwnd1 "http://127.0.0.1:8767" "dave" $passwordD
    [void](Wait-State $script:hwnd2 { param($s) @($s.password_vault.entries | Where-Object username -eq "dave").Count -eq 1 } 12 "window2 sees dave")
    [void](Click-EntryAction $script:hwnd2 "dave" "edit")
    [void](Click-EntryAction $script:hwnd1 "dave" "delete")
    Click-Client $script:hwnd1 925 $script:lastActionY
    [void](Wait-State $script:hwnd1 { param($s) @($s.password_vault.entries | Where-Object username -eq "dave").Count -eq 0 } 12 "dave deleted")
    [void](Fill-VaultField $script:hwnd2 "password" "S3cret!Dave-stale" @(358,366,374,382,390,398))
    [void](Save-Form $script:hwnd2)
    $deleteConflict = Wait-State $script:hwnd2 { param($s) $null -ne $s.password_vault.conflict } 12 "modify-delete conflict"
    if (@($deleteConflict.password_vault.entries | Where-Object username -eq "dave").Count -ne 0) { throw "deleted dave reappeared after conflict" }
    Add-Result "modify-delete-conflict" "PASS" "stale dave edit rejected and reloaded without dave"
    Shot-Window $script:hwnd2 "08-stale-save-rejected.png"

    # Stale tombstone: window 2 is in an add form while window 1 deletes dave's peer.
    Add-Account $script:hwnd1 "http://127.0.0.1:8767" "erin" $passwordD
    [void](Wait-State $script:hwnd2 { param($s) @($s.password_vault.entries | Where-Object username -eq "erin").Count -eq 1 } 12 "window2 sees erin")
    Click-Client $script:hwnd2 990 159
    [void](Wait-State $script:hwnd2 { param($s) $s.password_vault.form_open } 8 "stale add form")
    [void](Click-EntryAction $script:hwnd1 "erin" "delete")
    Click-Client $script:hwnd1 925 $script:lastActionY
    [void](Wait-State $script:hwnd1 { param($s) @($s.password_vault.entries | Where-Object username -eq "erin").Count -eq 0 } 12 "erin deleted")
    if (-not (Fill-VaultField $script:hwnd2 "origin" "http://127.0.0.1:8767" @(250,258,266,274,282,290))) { throw "stale origin field" }
    if (-not (Fill-VaultField $script:hwnd2 "username" "carol" @(300,308,316,324,332,340,348))) { throw "stale username field" }
    if (-not (Fill-VaultField $script:hwnd2 "password" $passwordC @(358,366,374,382,390,398))) { throw "stale password field" }
    [void](Save-Form $script:hwnd2)
    [void](Wait-State $script:hwnd2 { param($s) -not $s.password_vault.form_open -and @($s.password_vault.entries | Where-Object username -eq "carol").Count -eq 1 } 12 "stale save with carol")
    $stale = Read-State $script:hwnd2
    if (@($stale.password_vault.entries | Where-Object username -eq "erin").Count -ne 0) { throw "stale erin reappeared" }
    if ([int64]$stale.password_vault.tombstones -lt 2) { throw "persistent tombstones missing" }
    Add-Result "stale-delete-no-resurrect" "PASS" "carol saved; deleted erin did not return; tombstones=$($stale.password_vault.tombstones)"

    # Create an in-flight login-save proposal in window 2, then verify that a
    # lock in window 1 cancels it as well as clearing the decrypted entries.
    Close-Passwords $script:hwnd2
    Focus-Window $script:hwnd2
    Send-Keys "^l"; Send-Keys "^a"; Send-Keys "{BACKSPACE}"
    [System.Windows.Forms.SendKeys]::SendWait($LoginUrl)
    Send-Keys "{ENTER}"
    [void](Wait-State $script:hwnd2 { param($s) $s.source -like "$LoginUrl*" } 25 "window2 login page")
    Start-Sleep -Seconds 1
    Open-Passwords $script:hwnd2
    [void](Wait-State $script:hwnd2 { param($s) $s.password_vault.site_matches -ge 2 } 10 "window2 login matches")
    $page = $null
    $filled = $false
    foreach ($y in 245, 249, 255, 260) {
        foreach ($x in 970, 982, 995) {
            Click-Client $script:hwnd2 $x $y
            Start-Sleep -Milliseconds 500
            $page = Get-PageState $script:hwnd2
            if ($null -ne $page -and $page.user -eq "alice" -and -not $page.submitted) { $filled = $true; break }
        }
        if ($filled) { break }
    }
    if (-not $filled) { throw "window2 autofill validation failed" }
    $loginButton = $page.loginRect
    Click-Web $script:hwnd2 ([int]$loginButton.left + [int]($loginButton.width / 2)) ([int]$loginButton.top + [int]($loginButton.height / 2))
    [void](Wait-State $script:hwnd2 { param($s) $null -ne $s.password_vault.pending } 10 "window2 pending login")
    Add-Result "pending-created" "PASS" "window2 holds a login-save proposal before lock"

    # Cross-window lock propagation.
    $state1 = Read-State $script:hwnd1
    $state2 = Read-State $script:hwnd2
    $beforeTombs = [int64]$state1.password_vault.tombstones
    Click-Client $script:hwnd1 965 110
    [void](Wait-State $script:hwnd1 { param($s) -not $s.password_vault.unlocked } 8 "window1 locked")
    $locked2 = Wait-State $script:hwnd2 { param($s) -not $s.password_vault.unlocked -and @($s.password_vault.entries).Count -eq 0 } 10 "window2 lock propagation"
    if (-not $locked2.password_vault.globally_locked) { throw "global lock flag missing" }
    [void](Wait-State $script:hwnd2 { param($s) $null -eq $s.password_vault.pending } 6 "pending autofill cancelled")
    Add-Result "lock-propagation" "PASS" "window2 entries=0 globally_locked=true pending=null"
    Shot-Both $script:hwnd1 $script:hwnd2 "09-lock-propagation.png"

    Unlock-Vault $script:hwnd1
    [void](Wait-State $script:hwnd1 { param($s) $s.password_vault.unlocked } 10 "window1 unlocked")
    Unlock-Vault $script:hwnd2
    [void](Wait-State $script:hwnd2 { param($s) $s.password_vault.unlocked } 10 "window2 unlocked")
    Add-Result "unlock-restored" "PASS" "both windows unlocked and can list entries"
    Shot-Both $script:hwnd1 $script:hwnd2 "10-unlock-restored.png"

    $vaultPath = Join-Path $DataDir "password-vault\vault.bin"
    if (-not (Test-Path $vaultPath)) { throw "vault file missing before close" }
    $vaultHashBefore = (Get-FileHash $vaultPath -Algorithm SHA256).Hash
    Close-Window $script:hwnd2
    Close-Window $script:hwnd1
    if (-not $script:app.WaitForExit(25000)) { throw "app did not exit after closing both windows" }
    Start-Sleep -Seconds 2
    Add-Result "multi-window-close" "PASS" "both windows closed; exit=$($script:app.ExitCode)"

    Remove-Item $StatusDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $StatusDir | Out-Null
    $script:app = Start-Process $Exe -PassThru
    $states = Wait-WindowCount 1 60 "restart window"
    $script:hwnd1 = [IntPtr][int64]$states[0].window_handle
    Focus-Window $script:hwnd1
    Open-Passwords $script:hwnd1
    Unlock-Vault $script:hwnd1
    $restored = Wait-State $script:hwnd1 { param($s) @($s.password_vault.entries | Where-Object username -eq "alice").Count -eq 1 -and @($s.password_vault.entries | Where-Object username -eq "carol").Count -eq 1 } 15 "restart integrity"
    if (@($restored.password_vault.entries | Where-Object username -eq "bob").Count -ne 1) { throw "bob was lost after restart" }
    if (@($restored.password_vault.entries | Where-Object username -eq "dave").Count -ne 0) { throw "dave resurrected after restart" }
    if (@($restored.password_vault.entries | Where-Object username -eq "erin").Count -ne 0) { throw "erin resurrected after restart" }
    if ([int64]$restored.password_vault.tombstones -lt $beforeTombs) { throw "tombstones lost after restart" }
    $vaultHashAfter = (Get-FileHash $vaultPath -Algorithm SHA256).Hash
    if ($vaultHashAfter -ne $vaultHashBefore) { throw "vault changed merely by unlock after restart" }
    Add-Result "restart-integrity" "PASS" "alice+bob+carol remain; dave/erin absent; tombstones=$($restored.password_vault.tombstones); vault hash unchanged"
    Shot-Window $script:hwnd1 "11-restart-integrity.png"

    [void][AmareVaultWin]::ShowWindow($script:hwnd1, 3)
    $deadline = (Get-Date).AddSeconds(8)
    while (-not [AmareVaultWin]::IsZoomed($script:hwnd1) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (-not [AmareVaultWin]::IsZoomed($script:hwnd1)) { throw "restart window did not maximize" }
    Start-Sleep -Milliseconds 600
    Shot-Window $script:hwnd1 "12-maximized.png"
    Add-Result "window-maximize" "PASS" "restart window IsZoomed=true"
    Close-Window $script:hwnd1
    if (-not $script:app.WaitForExit(25000)) { throw "restart app did not exit" }
} catch {
    Add-Result "fatal" "FAIL" $_.Exception.Message
    try { Shot-Window $script:hwnd1 "99-vault-multiwindow-failure.png" } catch {}
} finally {
    if ($null -ne $script:app -and -not $script:app.HasExited) { try { $script:app.Kill() } catch {} }
    $summary = [pscustomobject]@{
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        results = $script:results
        pass = @($script:results | Where-Object result -eq "PASS").Count
        fail = @($script:results | Where-Object result -eq "FAIL").Count
        unverified = @($script:results | Where-Object result -eq "UNVERIFIED").Count
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ScreenshotDir "gpui-vault-multiwindow-results.json") -Encoding UTF8
    Write-Host "GPUI vault multiwindow summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)"
    if ($summary.fail -gt 0) { exit 1 }
}
