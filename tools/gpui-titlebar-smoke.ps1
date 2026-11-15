param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$StatusDir,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$ScreenshotDir
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\gpui-test-focus.ps1"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AmareTitlebar {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
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
function Read-AllStates {
    $states = @()
    if (-not (Test-Path $StatusDir)) { return $states }
    foreach ($file in @(Get-ChildItem $StatusDir -Filter "status-*.json" -File -ErrorAction SilentlyContinue)) {
        try {
            $state = Get-Content $file.FullName -Raw -ErrorAction Stop | ConvertFrom-Json
            if ($null -ne $state -and $state.pid -eq $script:app.Id) { $states += $state }
        } catch { }
    }
    return $states
}
function Wait-States([int]$count, [int]$seconds, [string]$label) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $states = @(Read-AllStates)
        if ($states.Count -eq $count) { return $states }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for $label (states=$(@(Read-AllStates).Count))"
}
function Get-State([IntPtr]$hwnd) {
    return @(Read-AllStates | Where-Object { [int64]$_.window_handle -eq $hwnd.ToInt64() })[0]
}
function Click-Client([IntPtr]$hwnd, [int]$x, [int]$y) {
    $point = New-Object AmareTitlebar+POINT
    [void][AmareTitlebar]::ClientToScreen($hwnd, [ref]$point)
    [void](Force-AmareForeground $hwnd)
    [void][AmareTitlebar]::SetCursorPos(($point.X + $x), ($point.Y + $y))
    Start-Sleep -Milliseconds 150
    [AmareTitlebar]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [AmareTitlebar]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 350
}
function Get-TitlebarButton([IntPtr]$hwnd, [string]$name) {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $state = Get-State $hwnd
        if ($null -ne $state -and $null -ne $state.layout_bounds -and $null -ne $state.layout_bounds.toolbar_children) {
            $b = $state.layout_bounds.toolbar_children.$name
            if ($null -ne $b) { return $b }
        }
        [void](Force-AmareForeground $hwnd)
        Start-Sleep -Milliseconds 250
    }
    return $null
}
function Click-Titlebar([IntPtr]$hwnd, [string]$name) {
    # Derive the button from the live client width. The status layout can lag
    # one paint behind a maximize/restore transition; clicking a stale
    # maximized-width coordinate can land in the other window.
    $rect = New-Object AmareTitlebar+RECT
    if (-not [AmareTitlebar]::GetClientRect($hwnd, [ref]$rect)) { return $false }
    $width = $rect.Right - $rect.Left
    if ($width -lt 300) { return $false }
    $x = switch ($name) {
        "window_min" { $width - 143 }
        "window_max" { $width - 89 }
        "window_close" { $width - 43 }
        default { return $false }
    }
    Click-Client $hwnd $x 22
    return $true
}
function Try-Click-State([IntPtr]$hwnd, [string]$button, [scriptblock]$test, [int]$seconds) {
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        [void](Force-AmareForeground $hwnd)
        Start-Sleep -Milliseconds 200
        if (Click-Titlebar $hwnd $button) {
            if (Wait-StateBool $hwnd $test $seconds) { return $true }
        }
    }
    return $false
}
function Wait-StateBool([IntPtr]$hwnd, [scriptblock]$test, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $test) { return $true }
        Start-Sleep -Milliseconds 120
    }
    return $false
}
function Shot([IntPtr]$hwnd, [string]$name) {
    $rect = New-Object AmareTitlebar+RECT
    [void][AmareTitlebar]::GetWindowRect($hwnd, [ref]$rect)
    $w = [Math]::Max(1, $rect.Right - $rect.Left); $h = [Math]::Max(1, $rect.Bottom - $rect.Top)
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bmp.Size)
    $path = Join-Path $ScreenshotDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Add-Result $name "PASS" "$path ($w x $h, $((Get-Item $path).Length) bytes)"
}

$script:app = $null
Remove-Item $StatusDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $StatusDir, $DataDir, $ScreenshotDir | Out-Null
$env:AMARE_GPUI_DATA_DIR = $DataDir
$env:AMARE_GPUI_STATUS_DIR = $StatusDir
Remove-Item Env:AMARE_GPUI_STATUS -ErrorAction SilentlyContinue
$env:AMARE_GPUI_START_URL = "about:blank"
$env:AMARE_GPUI_RESTORE = "0"
$env:AMARE_GPUI_SELF_TEST = "0"
$env:AMARE_GPUI_DIAGNOSTICS = "1"
$env:AMARE_GPUI_WINDOW_SIZE = "1044x788"
$env:AMARE_GPUI_OPEN_SECOND_WINDOW = "1"
Get-Process msedgewebview2 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$beforeWebView = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
try {
    $script:app = Start-Process $Exe -PassThru
    $states = Wait-States 2 40 "two AMARE windows"
    $handles = @($states | Sort-Object window_id | ForEach-Object { [IntPtr][int64]$_.window_handle })
    if ($handles.Count -ge 2) {
        [void][AmareTitlebar]::SetWindowPos($handles[0], [IntPtr]::Zero, 8, 32, 500, 640, 0x0004)
        [void][AmareTitlebar]::SetWindowPos($handles[1], [IntPtr]::Zero, 516, 32, 500, 640, 0x0004)
        Start-Sleep -Seconds 2
    }
    $rects = @{}
    foreach ($hwnd in $handles) {
        $rect = New-Object AmareTitlebar+RECT
        [void][AmareTitlebar]::GetWindowRect($hwnd, [ref]$rect)
        $rects[$hwnd.ToInt64()] = "$($rect.Left),$($rect.Top),$($rect.Right),$($rect.Bottom)"
    }

    for ($i = 0; $i -lt $handles.Count; $i++) {
        $hwnd = $handles[$i]
        $label = "window$($i + 1)"
        if (-not [AmareTitlebar]::IsWindow($hwnd)) {
            Add-Result "$label-handle" "FAIL" "window handle is no longer valid"
            continue
        }
        [void](Force-AmareForeground $hwnd)
        Start-Sleep -Milliseconds 300
        $maximize = Try-Click-State $hwnd "window_max" { [AmareTitlebar]::IsZoomed($hwnd) } 4
        if ($maximize) {
            Add-Result "$label-titlebar-maximize" "PASS" "IsZoomed=true via real mouse"
            Shot $hwnd ("0$($i + 1)-$label-maximized.png")
        } else {
            Add-Result "$label-titlebar-maximize" "UNVERIFIED" "window did not maximize"
        }
        $restore = Try-Click-State $hwnd "window_max" { -not [AmareTitlebar]::IsZoomed($hwnd) } 4
        if ($restore) {
            Add-Result "$label-titlebar-restore" "PASS" "IsZoomed=false via real mouse"
            Shot $hwnd ("1$($i + 1)-$label-restored.png")
        } else {
            Add-Result "$label-titlebar-restore" "UNVERIFIED" "window did not restore"
        }
        $rect = New-Object AmareTitlebar+RECT
        [void][AmareTitlebar]::GetWindowRect($hwnd, [ref]$rect)
        $after = "$($rect.Left),$($rect.Top),$($rect.Right),$($rect.Bottom)"
        if ($rects[$hwnd.ToInt64()] -eq $after) {
            Add-Result "$label-geometry-preserved" "PASS" "$($rects[$hwnd.ToInt64()]) -> $after"
        } else {
            Add-Result "$label-geometry-preserved" "UNVERIFIED" "$($rects[$hwnd.ToInt64()]) -> $after"
        }
        $minimize = Try-Click-State $hwnd "window_min" { [AmareTitlebar]::IsIconic($hwnd) } 4
        if ($minimize) {
            Add-Result "$label-titlebar-minimize" "PASS" "IsIconic=true via real mouse"
            [void][AmareTitlebar]::ShowWindow($hwnd, 9)
            if (Wait-StateBool $hwnd { -not [AmareTitlebar]::IsIconic($hwnd) } 6) {
                Add-Result "$label-minimize-restore" "PASS" "restored from minimized state"
            } else {
                Add-Result "$label-minimize-restore" "UNVERIFIED" "window stayed minimized"
            }
        } else {
            Add-Result "$label-titlebar-minimize" "UNVERIFIED" "window did not minimize"
        }
    }

    foreach ($hwnd in $handles) {
        [void](Force-AmareForeground $hwnd)
        Start-Sleep -Milliseconds 120
        [System.Windows.Forms.SendKeys]::SendWait("%{F4}")
        Start-Sleep -Milliseconds 200
    }
    if (-not $script:app.WaitForExit(25000)) { throw "titlebar test app did not exit" }
    Add-Result "titlebar-multiwindow-exit" "PASS" "both windows closed; exit=$($script:app.ExitCode)"
    Start-Sleep -Seconds 2
    $afterWebView = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
    if ($afterWebView -le $beforeWebView) {
        Add-Result "titlebar-webview2-cleanup" "PASS" "webview2=$beforeWebView->$afterWebView"
    } else {
        Add-Result "titlebar-webview2-cleanup" "UNVERIFIED" "webview2=$beforeWebView->$afterWebView"
    }
} catch {
    Add-Result "fatal" "FAIL" $_.Exception.Message
    try {
        $state = @(Read-AllStates)[0]
        if ($null -ne $state) { $state.diagnostics | Select-Object -Last 30 | ForEach-Object { Write-Host "TRACE $_" } }
    } catch {}
} finally {
    if ($null -ne $script:app -and -not $script:app.HasExited) { try { $script:app.Kill() } catch {} }
    $summary = [pscustomobject]@{
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        results = $script:results
        pass = @($script:results | Where-Object result -eq "PASS").Count
        fail = @($script:results | Where-Object result -eq "FAIL").Count
        unverified = @($script:results | Where-Object result -eq "UNVERIFIED").Count
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ScreenshotDir "gpui-titlebar-results.json") -Encoding UTF8
    Write-Host "GPUI titlebar summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)"
    if ($summary.fail -gt 0) { exit 1 }
}
