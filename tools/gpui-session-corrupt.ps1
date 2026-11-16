param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$ScreenshotDir
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AmareCorrupt {
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
function Read-State { if (-not (Test-Path $script:status)) { return $null }; try { return Get-Content $script:status -Raw -ErrorAction Stop | ConvertFrom-Json } catch { return $null } }
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
function Get-ProfileDb([string]$root) {
    return Get-ChildItem $root -Recurse -Filter amare.db -File |
        Where-Object { $_.FullName -match 'profiles' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}
function Invoke-SessionSql([string]$db, [string]$mode, [string]$value = "") {
    $py = Join-Path $env:TEMP "amare-corrupt-session.py"
    $valuePath = Join-Path $env:TEMP "amare-corrupt-session-value.txt"
    [IO.File]::WriteAllText($valuePath, $value, (New-Object System.Text.UTF8Encoding($false)))
    @"
import sqlite3, sys
db, mode, value_path = sys.argv[1], sys.argv[2], sys.argv[3]
value = open(value_path, encoding='utf-8').read()
c = sqlite3.connect(db)
if mode == 'set':
    c.execute("INSERT OR REPLACE INTO settings(key,group_name,value_type,value_text,updated_at_ms) VALUES('gpui.session','browser','json',?,0)", (value,))
elif mode == 'backup':
    row = c.execute("SELECT value_text FROM settings WHERE key='gpui.session.corrupt'").fetchone()
    print('' if row is None else row[0])
elif mode == 'backup_count':
    print(c.execute("SELECT count(*) FROM settings WHERE key='gpui.session.corrupt'").fetchone()[0])
c.commit(); c.close()
"@ | Set-Content -Path $py -Encoding UTF8
    $output = python $py $db $mode $valuePath
    if ($LASTEXITCODE -ne 0) { throw "session SQL $mode failed" }
    return (($output -join "`n").Trim())
}
function Stop-App($process, [IntPtr]$hwnd) {
    [void]([System.Windows.Forms.SendKeys]::SendWait("%{F4}"))
    if (-not $process.WaitForExit(20000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
}
function Start-Case([string]$data) {
    Remove-Item $script:status -Force -ErrorAction SilentlyContinue
    $env:AMARE_GPUI_DATA_DIR = $data
    $env:AMARE_GPUI_STATUS = $script:status
    $env:AMARE_GPUI_START_URL = "about:blank"
    $env:AMARE_GPUI_SELF_TEST = "0"
    $env:AMARE_GPUI_RESTORE = "1"
    $process = Start-Process $Exe -PassThru
    [void](Wait-State { param($s) $null -ne $s.address } 60 "app start")
    Start-Sleep -Milliseconds 1200
    $process.Refresh()
    $hwnd = $process.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) {
        $windows = @([AmareCorrupt]::Windows([int]$process.Id))
        if ($windows.Count -gt 0) { $hwnd = $windows[0] }
    }
    return @($process, $hwnd)
}

function New-ValidWindow([int]$id, [string]$url = "about:blank", [int]$active = 0) {
    return @{
        revision = 1
        window_id = $id
        x = 100
        y = 100
        width = 1024
        height = 720
        maximized = $false
        active = $active
        tabs = @(@{ url = $url; title = "case"; scroll_x = 0; scroll_y = 0 })
        saved_at_ms = 1
    }
}

$script:status = Join-Path $env:TEMP "amare-corrupt-status.json"
Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $DataDir, $ScreenshotDir | Out-Null
$base = Join-Path $DataDir "base"
New-Item -ItemType Directory -Force -Path $base | Out-Null
$app = $null
$hwnd = [IntPtr]::Zero
try {
    # Seed a real profile/session so every case starts from the same base.
    $started = Start-Case $base
    $app = $started[0]; $hwnd = $started[1]
    Start-Sleep -Seconds 3
    [void]([System.Windows.Forms.SendKeys]::SendWait("%{F4}"))
    if (-not $app.WaitForExit(20000)) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($null -eq (Get-ProfileDb $base)) { throw "base profile DB missing" }

    $valid = (New-ValidWindow 1 "about:blank") | ConvertTo-Json -Depth 8 -Compress
    $validTwo = @((New-ValidWindow 1 "about:blank"), (New-ValidWindow 2 "about:blank")) | ConvertTo-Json -Depth 8 -Compress
    $legacy = @(@{
        window_id = 1; x = 100; y = 100; width = 1024; height = 720; maximized = $false; active = 0;
        tabs = @(@{ url = "about:blank"; title = "legacy"; scroll_x = 0; scroll_y = 0 }); saved_at_ms = 1
    }) | ConvertTo-Json -Depth 8 -Compress
    $cases = @(
        @{ name = "empty-string"; json = ""; error = $true; backup = $true; windows = 1; tabs = 1 },
        @{ name = "truncated-json"; json = '[{"window_id":1'; error = $true; backup = $true; windows = 1; tabs = 1 },
        @{ name = "invalid-syntax"; json = '{"schema":2,'; error = $true; backup = $true; windows = 1; tabs = 1 },
        @{ name = "missing-fields"; json = '[{"window_id":1}]'; error = $true; backup = $true; windows = 1; tabs = 1 },
        @{ name = "unknown-field"; json = $valid.Replace('"saved_at_ms":1', '"saved_at_ms":1,"future_field":true'); error = $false; backup = $false; windows = 1; tabs = 1 },
        @{ name = "duplicate-window-id"; json = $validTwo.Replace('"window_id":2', '"window_id":1'); error = $true; backup = $true; windows = 1; tabs = 1 },
        @{ name = "invalid-url"; json = '[{"window_id":1,"x":100,"y":100,"width":1024,"height":720,"maximized":false,"active":0,"tabs":[{"url":"javascript:alert(1)","title":"bad","scroll_x":0,"scroll_y":0}],"saved_at_ms":1}]'; error = $true; backup = $true; windows = 1; tabs = 1 },
        @{ name = "active-out-of-range"; json = '[{"window_id":1,"x":100,"y":100,"width":1024,"height":720,"maximized":false,"active":99,"tabs":[{"url":"about:blank","title":"bad","scroll_x":0,"scroll_y":0}],"saved_at_ms":1}]'; error = $true; backup = $true; windows = 1; tabs = 1 },
        @{ name = "negative-scroll"; json = '[{"window_id":1,"x":100,"y":100,"width":1024,"height":720,"maximized":false,"active":0,"tabs":[{"url":"about:blank","title":"bad","scroll_x":-5,"scroll_y":-7}],"saved_at_ms":1}]'; error = $true; backup = $true; windows = 1; tabs = 1 },
        @{ name = "huge-scroll"; json = '[{"window_id":1,"x":100,"y":100,"width":1024,"height":720,"maximized":false,"active":0,"tabs":[{"url":"about:blank","title":"bad","scroll_x":0,"scroll_y":999999999}],"saved_at_ms":1}]'; error = $true; backup = $true; windows = 1; tabs = 1 },
        @{ name = "legacy-array"; json = $legacy; error = $false; backup = $false; windows = 1; tabs = 1 },
        @{ name = "mixed-valid-invalid"; json = '[{"window_id":0,"tabs":[]},{"window_id":7,"active":0,"tabs":[{"url":"about:blank","title":"ok"}]}]'; error = $true; backup = $true; windows = 1; tabs = 1 },
        @{ name = "empty-window-list"; json = '[]'; error = $false; backup = $false; windows = 1; tabs = 1 },
        @{ name = "normal-payload"; json = '{"schema":2,"revision":1,"windows":[' + $valid + ']}'; error = $false; backup = $false; windows = 1; tabs = 1 }
    )

    foreach ($case in $cases) {
        $dir = Join-Path $DataDir $case.name
        Copy-Item $base $dir -Recurse -Force
        $db = Get-ProfileDb $dir
        if ($null -eq $db) { Add-Result $case.name "FAIL" "profile DB missing"; continue }
        [void](Invoke-SessionSql $db.FullName "set" $case.json)
        $started = Start-Case $dir
        $app = $started[0]; $hwnd = $started[1]
        Start-Sleep -Seconds 1
        $state = Read-State
        $windowCount = @([AmareCorrupt]::Windows([int]$app.Id)).Count
        $tabCount = if ($null -ne $state) { @($state.tabs).Count } else { 0 }
        $hasError = $null -ne $state -and -not [string]::IsNullOrWhiteSpace([string]$state.session_error)
        $backup = Invoke-SessionSql $db.FullName "backup"
        $backupCount = [int](Invoke-SessionSql $db.FullName "backup_count")
        $ok = ($null -ne $state) -and (-not $app.HasExited) -and ($windowCount -eq $case.windows) -and ($tabCount -eq $case.tabs) -and ($hasError -eq $case.error)
        if ($case.backup) {
            $ok = $ok -and ($backupCount -eq 1) -and ($backup.Trim() -eq $case.json.Trim())
        } else {
            $ok = $ok -and ($backupCount -eq 0)
        }
        $detail = "windows=$windowCount tabs=$tabCount error=$hasError backupCount=$backupCount exit=$($app.HasExited)"
        if ($ok) { Add-Result $case.name "PASS" $detail } else { Add-Result $case.name "FAIL" $detail }
        [void]([System.Windows.Forms.SendKeys]::SendWait("%{F4}"))
        if (-not $app.WaitForExit(20000)) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Milliseconds 500
    }
} catch {
    Add-Result "fatal" "FAIL" $_.Exception.Message
} finally {
    if ($null -ne $app -and -not $app.HasExited) { try { $app.Kill() } catch {} }
    $summary = [pscustomobject]@{
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        results = $script:results
        pass = @($script:results | Where-Object result -eq "PASS").Count
        fail = @($script:results | Where-Object result -eq "FAIL").Count
        unverified = @($script:results | Where-Object result -eq "UNVERIFIED").Count
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ScreenshotDir "gpui-session-corrupt-results.json") -Encoding UTF8
    Write-Host "GPUI corrupt-session summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)"
    if ($summary.fail -gt 0) { exit 1 }
}
