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
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetFocus();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
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
$script:deadline = (Get-Date).AddMinutes(3)
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
    while ((Get-Date) -lt $deadline -and (Get-Date) -lt $script:deadline) {
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
    $state = Get-State $hwnd
    if ($null -ne $state -and $null -ne $state.layout_bounds -and $null -ne $state.layout_bounds.window -and $null -ne $state.layout_bounds.toolbar_children) {
        $layoutWidth = [double]$state.layout_bounds.window.width
        $b = $state.layout_bounds.toolbar_children.$name
        if ($null -ne $b -and [Math]::Abs($layoutWidth - $width) -le 20) {
            Click-Client $hwnd ([int]($b.x + $b.width / 2)) ([int]($b.y + $b.height / 2))
            return $true
        }
    }
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
        if ((Get-Date) -gt $script:deadline) { return $false }
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
function Test-WindowStages([IntPtr]$hwnd, [string]$label, [int]$seconds, $initialState = $null) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $state = $null
    while ((Get-Date) -lt $deadline) {
        $state = Get-State $hwnd
        if ($null -eq $state) { $state = $initialState }
        if ($null -ne $state) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($null -eq $state) {
        Add-Result "$label-stage-hwnd" "FAIL" "status state not found for HWND"
        return $false
    }
    Add-Result "$label-stage-hwnd" "PASS" "window_id=$($state.window_id) hwnd=$($hwnd.ToInt64())"
    if (-not [AmareTitlebar]::IsWindow($hwnd)) {
        Add-Result "$label-stage-iswindow" "FAIL" "IsWindow=false"
        return $false
    }
    Add-Result "$label-stage-iswindow" "PASS" "IsWindow=true live_windows=$(@([AmareTitlebar]::Windows([int]$script:app.Id)).Count)"
    $attached = $false
    $idle = $false
    while ((Get-Date) -lt $deadline) {
        $state = Get-State $hwnd
        if ($null -eq $state) { $state = $initialState }
        if ($null -ne $state) {
            $tab = @($state.tabs | Where-Object { $_.id -eq $state.active_tab })[0]
            if ($null -ne $tab -and $tab.webview -eq $true) { $attached = $true }
            if ($null -ne $tab -and $tab.webview -eq $true -and $tab.loading -eq $false) { $idle = $true; break }
        }
        Start-Sleep -Milliseconds 200
    }
    if ($attached) { Add-Result "$label-stage-webview" "PASS" "webview=true" } else { Add-Result "$label-stage-webview" "FAIL" "webview controller not attached" }
    if ($idle) { Add-Result "$label-stage-navigation" "PASS" "loading=false" } else { Add-Result "$label-stage-navigation" "FAIL" "navigation not idle" }
    $proc = Get-Process -Id $script:app.Id -ErrorAction SilentlyContinue
    if ($null -ne $proc -and $proc.Responding) { Add-Result "$label-stage-responsive" "PASS" "process responding" } else { Add-Result "$label-stage-responsive" "FAIL" "process not responding" }
    if (-not $attached -or -not $idle -or $null -eq $proc -or -not $proc.Responding) { return $false }
    $beforeFg = [AmareTitlebar]::GetForegroundWindow()
    $beforeActive = [AmareTitlebar]::GetActiveWindow()
    $beforeFocus = [AmareTitlebar]::GetFocus()
    $show = [AmareTitlebar]::ShowWindow($hwnd, 9)
    $setFg = [AmareTitlebar]::SetForegroundWindow($hwnd)
    $setActive = [AmareTitlebar]::SetActiveWindow($hwnd)
    $setFocus = [AmareTitlebar]::SetFocus($hwnd)
    Start-Sleep -Milliseconds 200
    $afterFg = [AmareTitlebar]::GetForegroundWindow()
    $afterActive = [AmareTitlebar]::GetActiveWindow()
    $afterFocus = [AmareTitlebar]::GetFocus()
    $win32Detail = "show=$show setForeground=$setFg setActive=$($setActive.ToInt64()) setFocus=$($setFocus.ToInt64()) beforeFg=$($beforeFg.ToInt64()) afterFg=$($afterFg.ToInt64()) afterActive=$($afterActive.ToInt64()) afterFocus=$($afterFocus.ToInt64())"
    if ($afterFg -eq $hwnd) {
        Add-Result "$label-stage-win32-activate" "PASS" $win32Detail
    } else {
        Add-Result "$label-stage-win32-activate" "UNVERIFIED" $win32Detail
    }
    $state = Get-State $hwnd
    if ($null -eq $state -and $null -ne $initialState) { $state = $initialState }
    if ($null -ne $state) {
        if ($state.window_active -eq $true) {
            Add-Result "$label-stage-gpui-active" "PASS" "window_active=true address_focused=$($state.address_focused)"
        } else {
            Add-Result "$label-stage-gpui-active" "UNVERIFIED" "window_active=$($state.window_active) address_focused=$($state.address_focused)"
        }
        $guiThread = 0
        if ($null -ne $state.gui_thread_id) { $guiThread = [uint32]$state.gui_thread_id }
        $focus = Get-AmareGuiFocus -WindowHandle $hwnd -GuiThreadId $guiThread
        $focusDetail = "gui_tid=$($focus.gui_thread_id) focus=0x$('{0:X}' -f $focus.focus_hwnd) root=0x$('{0:X}' -f $focus.focus_root_hwnd) class=$($focus.focus_class) focus_is_window=$($focus.focus_is_window) focus_is_descendant=$($focus.focus_is_descendant)"
        if ($focus.focus_is_window) {
            Add-Result "$label-stage-os-focus" "PASS" $focusDetail
        } elseif ($focus.focus_is_descendant) {
            Add-Result "$label-stage-os-focus" "UNVERIFIED" "child window owns keyboard focus; $focusDetail"
        } else {
            Add-Result "$label-stage-os-focus" "UNVERIFIED" "focus is outside this window tree; $focusDetail"
        }
    } else {
        Add-Result "$label-stage-gpui-active" "UNVERIFIED" "state unavailable"
    }
    return $true
}
function Check-OtherWindow([IntPtr]$hwnd, [string]$step) {
    $valid = [AmareTitlebar]::IsWindow($hwnd)
    $live = @([AmareTitlebar]::Windows([int]$script:app.Id)).Count
    if ($valid) {
        Add-Result "hwnd-window2-$step" "PASS" "IsWindow=true live_windows=$live"
    } else {
        Add-Result "hwnd-window2-$step" "FAIL" "IsWindow=false live_windows=$live"
    }
    return $valid
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

function Collect-TitlebarCrashDiagnostics([string]$Reason) {
    $diagDir = Join-Path $ScreenshotDir "titlebar-diagnostics"
    New-Item -ItemType Directory -Force -Path $diagDir | Out-Null
    $lastStates = @(Read-AllStates | ForEach-Object {
        [pscustomobject]@{
            window_id = $_.window_id
            window_handle = $_.window_handle
            window_active = $_.window_active
            address_focused = $_.address_focused
            pid = $_.pid
            gui_thread_id = $_.gui_thread_id
            os_focus = $_.os_focus
            last_action = $_.last_action
            last_key = $_.last_key
            last_navigation_url = $_.last_navigation_url
            session_error = $_.session_error
            diagnostics = @($_.diagnostics | Select-Object -Last 40)
        }
    })
    $procInfo = $null
    if ($null -ne $script:app) {
        $hasExited = $script:app.HasExited
        $exitCode = $null
        $startTime = $null
        $exitTime = $null
        if ($hasExited) { $exitCode = $script:app.ExitCode }
        try { $startTime = $script:app.StartTime.ToUniversalTime().ToString("o") } catch { }
        if ($hasExited) { try { $exitTime = $script:app.ExitTime.ToUniversalTime().ToString("o") } catch { } }
        $procInfo = [pscustomobject]@{
            id = $script:app.Id
            has_exited = $hasExited
            exit_code = $exitCode
            start_time = $startTime
            exit_time = $exitTime
        }
    }
    $events = @()
    try {
        $events = @(Get-WinEvent -FilterHashtable @{ LogName = "Application"; StartTime = (Get-Date).AddMinutes(-20) } -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -match "amare-gpui" -or $_.ProviderName -match "Application Error|Windows Error Reporting" } |
            Select-Object -First 20 |
            ForEach-Object {
                [pscustomobject]@{
                    time = $_.TimeCreated.ToUniversalTime().ToString("o")
                    id = $_.Id
                    provider = $_.ProviderName
                    level = $_.LevelDisplayName
                    message = $_.Message
                }
            })
    } catch { }
    $werFiles = @()
    foreach ($root in @(
        (Join-Path $env:LOCALAPPDATA "Microsoft\Windows\WER\ReportArchive"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\Windows\WER\ReportQueue"),
        (Join-Path $env:ProgramData "Microsoft\Windows\WER\ReportArchive"),
        (Join-Path $env:ProgramData "Microsoft\Windows\WER\ReportQueue")
    )) {
        if (-not (Test-Path $root)) { continue }
        foreach ($file in @(Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-20) -and $_.Name -match "amare|Report.wer" } | Select-Object -First 20)) {
            $werFiles += $file.FullName
        }
    }
    $dumpFiles = @()
    if ($env:AMARE_WER_DUMP_DIR -and (Test-Path $env:AMARE_WER_DUMP_DIR)) {
        $dumpFiles = @(Get-ChildItem $env:AMARE_WER_DUMP_DIR -File -Recurse -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    }
    $assessment = if ($null -ne $procInfo -and $procInfo.has_exited) { Get-AmareExitAssessment -ExitCode $procInfo.exit_code } else { Get-AmareExitAssessment -ExitCode $null -ProcessHandleValid $false }
    $diag = [pscustomobject]@{
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        reason = $Reason
        exit = $assessment
        process = $procInfo
        last_success = @($script:results | Where-Object result -eq "PASS" | Select-Object -Last 1)
        results = $script:results
        states = $lastStates
        event_log = $events
        wer_files = $werFiles
        dump_files = $dumpFiles
        lifecycle = @(Get-ChildItem $StatusDir -Filter "lifecycle-*.log" -File -ErrorAction SilentlyContinue | ForEach-Object { @{ path = $_.FullName; lines = @(Get-Content $_.FullName -Tail 80) } })
        app_stdout = @(if (Test-Path $appStdout) { Get-Content $appStdout -Tail 80 })
        app_stderr = @(if (Test-Path $appStderr) { Get-Content $appStderr -Tail 120 })
    }
    $path = Join-Path $diagDir "titlebar-crash.json"
    $diag | ConvertTo-Json -Depth 12 | Set-Content $path -Encoding UTF8
    Write-Host "TITLEBAR crash diagnostics: $path"
    return $path
}

$script:app = $null
$appDiagDir = Join-Path $ScreenshotDir "titlebar-diagnostics"
New-Item -ItemType Directory -Force -Path $appDiagDir | Out-Null
$appStdout = Join-Path $appDiagDir "app-stdout.log"
$appStderr = Join-Path $appDiagDir "app-stderr.log"
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
    $script:app = Start-Process $Exe -PassThru -RedirectStandardOutput $appStdout -RedirectStandardError $appStderr
    $states = Wait-States 2 40 "two AMARE windows"
    $liveHandles = @([AmareTitlebar]::Windows([int]$script:app.Id))
    if ($liveHandles.Count -ge 2) {
        $handles = @($liveHandles | Sort-Object { $_.ToInt64() })
        Add-Result "window-handles" "PASS" "live_windows=$($liveHandles.Count) status_states=$($states.Count)"
    } else {
        $handles = @($states | Sort-Object window_id | ForEach-Object { [IntPtr][int64]$_.window_handle })
        Add-Result "window-handles" "UNVERIFIED" "live_windows=$($liveHandles.Count) status_states=$($states.Count)"
    }
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
        Write-Host "TITLEBAR window=$i handle=$($hwnd.ToInt64())"
        if (-not [AmareTitlebar]::IsWindow($hwnd)) {
            Add-Result "$label-handle" "FAIL" "original window handle is no longer valid"
            $live = @([AmareTitlebar]::Windows([int]$script:app.Id))
            if ($i -eq 1 -and $live.Count -ge 2) {
                Add-Result "window2-rediscovered" "UNVERIFIED" "original HWND invalid; continuing with a re-enumerated handle for evidence only"
                $hwnd = $live[1]
            } else {
                continue
            }
        }
        Write-Host "TITLEBAR $label readiness stages start"
        $initialState = @($states | Where-Object { [int64]$_.window_handle -eq $hwnd.ToInt64() })[0]
        $ready = Test-WindowStages $hwnd $label 15 $initialState
        Write-Host "TITLEBAR $label readiness stages end ready=$ready"
        if (-not $ready) {
            continue
        }
        Start-Sleep -Milliseconds 200
        $maximize = Try-Click-State $hwnd "window_max" { [AmareTitlebar]::IsZoomed($hwnd) } 4
        if ($maximize) {
            Add-Result "$label-titlebar-maximize" "PASS" "IsZoomed=true via real mouse"
            if ($i -eq 0 -and $handles.Count -ge 2) { [void](Check-OtherWindow $handles[1] "after-window1-maximize") }
        } else {
            Add-Result "$label-titlebar-maximize" "UNVERIFIED" "window did not maximize"
        }
        $restore = Try-Click-State $hwnd "window_max" { -not [AmareTitlebar]::IsZoomed($hwnd) } 4
        if ($restore) {
            Add-Result "$label-titlebar-restore" "PASS" "IsZoomed=false via real mouse"
            if ($i -eq 0 -and $handles.Count -ge 2) { [void](Check-OtherWindow $handles[1] "after-window1-restore") }
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
            if ($i -eq 0 -and $handles.Count -ge 2) { [void](Check-OtherWindow $handles[1] "after-window1-minimize") }
            [void][AmareTitlebar]::ShowWindow($hwnd, 9)
            if (Wait-StateBool $hwnd { -not [AmareTitlebar]::IsIconic($hwnd) } 6) {
                Add-Result "$label-minimize-restore" "PASS" "restored from minimized state"
                if ($i -eq 0 -and $handles.Count -ge 2) { [void](Check-OtherWindow $handles[1] "after-window1-minimize-restore") }
            } else {
                Add-Result "$label-minimize-restore" "UNVERIFIED" "window stayed minimized"
            }
        } else {
            Add-Result "$label-titlebar-minimize" "UNVERIFIED" "window did not minimize"
        }
    }

    foreach ($hwnd in $handles) {
        if (-not [AmareTitlebar]::IsWindow($hwnd)) { continue }
        [void][AmareTitlebar]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 300
    }
    if (-not $script:app.WaitForExit(25000)) {
        Stop-Process -Id $script:app.Id -Force -ErrorAction SilentlyContinue
        $assessment = Get-AmareExitAssessment -ExitCode $null -TimedOut $true
        Add-Result "titlebar-multiwindow-exit" $assessment.State "class=$($assessment.Classification) $($assessment.Detail)"
        [void](Collect-TitlebarCrashDiagnostics "exit-timeout")
    } else {
        $assessment = Get-AmareExitAssessment -ExitCode $script:app.ExitCode
        Add-Result "titlebar-multiwindow-exit" $assessment.State "class=$($assessment.Classification) $($assessment.Detail)"
        if ($assessment.State -ne "PASS") {
            [void](Collect-TitlebarCrashDiagnostics "exit-$($assessment.Classification)")
        }
    }
    Start-Sleep -Seconds 2
    $afterWebView = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue).Count
    if ($afterWebView -le $beforeWebView) {
        Add-Result "titlebar-webview2-cleanup" "PASS" "webview2=$beforeWebView->$afterWebView"
    } else {
        Add-Result "titlebar-webview2-cleanup" "UNVERIFIED" "webview2=$beforeWebView->$afterWebView"
    }
} catch {
    Add-Result "fatal" "FAIL" $_.Exception.Message
    try { [void](Collect-TitlebarCrashDiagnostics "harness-exception") } catch { Write-Host "TITLEBAR crash diagnostics failed: $($_.Exception.Message)" }
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
        blocked = @($script:results | Where-Object result -eq "BLOCKED").Count
        skipped = @($script:results | Where-Object result -eq "SKIPPED").Count
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ScreenshotDir "gpui-titlebar-results.json") -Encoding UTF8
    Write-Host "GPUI titlebar summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)"
    if ($summary.fail -gt 0) { exit 1 }
}
