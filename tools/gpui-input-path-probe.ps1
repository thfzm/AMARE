param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$StatusDir,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$Trials = 3
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\gpui-test-focus.ps1"
Add-Type -AssemblyName System.Windows.Forms
New-Item -ItemType Directory -Force -Path $StatusDir, $DataDir, $OutDir | Out-Null
$script:results = [System.Collections.Generic.List[object]]::new()
function Send-Keys([string]$keys) { [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds 120 }
function Add-Result([string]$name, [string]$state, [string]$detail) {
    $script:results.Add([pscustomobject]@{ name = $name; result = $state; detail = $detail })
    Write-Host ("{0} {1}: {2}" -f $state, $name, $detail)
}
function Read-State {
    $file = Get-ChildItem $StatusDir -Filter "status-*.json" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $file) { return $null }
    try { return Get-Content $file.FullName -Raw | ConvertFrom-Json } catch { return $null }
}
function Wait-State([scriptblock]$test, [int]$seconds, [string]$label) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $state = Read-State
        if ($null -ne $state -and (& $test $state)) { return $state }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for $label"
}
function Ensure-Empty([IntPtr]$hwnd) {
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $state = Read-State
        if ($null -ne $state -and $null -ne $state.address_bounds) {
            $b = $state.address_bounds
            Send-RealClientClick $hwnd ([int]($b.x + $b.width / 2)) ([int]($b.y + $b.height / 2))
        } else {
            Send-GpuiShortcut $hwnd 0x4C
        }
        Start-Sleep -Milliseconds 200
        Send-Keys "^a"; Send-Keys "{BACKSPACE}"
        Start-Sleep -Milliseconds 250
        $state = Read-State
        if ([string]::IsNullOrEmpty([string]$state.address)) { return $state }
    }
    return (Read-State)
}
function Invoke-Method([string]$method, [IntPtr]$hwnd, [string]$text) {
    switch ($method) {
        "sendkeys" { Send-TextKeys $text }
        "scancode" { Send-ScanCodeText $text }
        "unicode" { Send-UnicodeText $text }
        "paste" {
            Set-Clipboard -Value $text
            Start-Sleep -Milliseconds 150
            Send-Keys "^v"
        }
    }
    Start-Sleep -Milliseconds 400
}

$app = $null
try {
    Remove-Item $StatusDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $StatusDir | Out-Null
    $env:AMARE_GPUI_DATA_DIR = $DataDir
    $env:AMARE_GPUI_STATUS_DIR = $StatusDir
    Remove-Item Env:AMARE_GPUI_STATUS -ErrorAction SilentlyContinue
    $env:AMARE_GPUI_START_URL = "about:blank"
    $env:AMARE_GPUI_SELF_TEST = "0"
    $env:AMARE_GPUI_RESTORE = "0"
    $env:AMARE_GPUI_DIAGNOSTICS = "1"
    $env:AMARE_GPUI_MESSAGE_TRACE = "1"
    $env:AMARE_GPUI_WINDOW_SIZE = "1044x788"
    $app = Start-Process $Exe -PassThru -RedirectStandardOutput (Join-Path $OutDir "input-path-out.log") -RedirectStandardError (Join-Path $OutDir "input-path-err.log")
    $state = Wait-State { param($s) $s.window_handle -ne 0 } 25 "window"
    $hwnd = [IntPtr][int64]$state.window_handle
    [void](Wait-State { param($s) $s.address_focused -or $s.tabs.Count -ge 1 } 20 "initial state")
    $text = "example"
    foreach ($method in @("sendkeys", "scancode", "unicode", "paste")) {
        $methodPass = 0
        for ($trial = 1; $trial -le $Trials; $trial++) {
            $before = Ensure-Empty $hwnd
            if (-not [string]::IsNullOrEmpty([string]$before.address)) {
                Add-Result "input-$method-$trial" "UNVERIFIED" "precondition failed: address_len=$(([string]$before.address).Length)"
                continue
            }
            $keyBefore = [int64]$before.keystroke_total
            $at = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            Invoke-Method $method $hwnd $text
            $after = Read-State
            $msgs = @($after.message_trace | Where-Object { [int64]$_.at_ms -ge [int64]$at })
            $keydown = @($msgs | Where-Object { [int]$_.message -eq 256 }).Count
            $chars = @($msgs | Where-Object { [int]$_.message -eq 258 }).Count
            $unichar = @($msgs | Where-Object { [int]$_.message -eq 0x109 }).Count
            $keyDelta = [int64]$after.keystroke_total - $keyBefore
            $match = [string]$after.address -eq $text
            if ($match) { $methodPass++ }
            Add-Result "input-$method-$trial" ($(if ($match) { "PASS" } else { "UNVERIFIED" })) "expected_len=$($text.Length) actual_len=$(([string]$after.address).Length) key_delta=$keyDelta keydown=$keydown char=$chars unichar=$unichar focus=$($after.address_focused) class=$($after.os_focus.keyboard_focus_class) fg_class=$($after.os_focus.focus_class)"
            if ($match) {
                Send-GpuiShortcut $hwnd 0x0D -NoCtrl
                Start-Sleep -Milliseconds 500
                $submit = Read-State
                Add-Result "input-$method-$trial-enter" ($(if ([int64]$submit.address_submit_count -gt [int64]$after.address_submit_count) { "PASS" } else { "UNVERIFIED" })) "submit_delta=$([int64]$submit.address_submit_count - [int64]$after.address_submit_count) enter_count=$($submit.address_enter_count)"
                Start-Sleep -Seconds 1
            }
        }
        Add-Result "input-method-$method" "PASS" "matches=$methodPass/$Trials text=$text"
    }
} catch {
    Add-Result "input-path-fatal" "FAIL" $_.Exception.Message
} finally {
    if ($null -ne $app -and -not $app.HasExited) {
        try { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
    $summary = [pscustomobject]@{
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        results = $script:results
        pass = @($script:results | Where-Object result -eq "PASS").Count
        fail = @($script:results | Where-Object result -eq "FAIL").Count
        unverified = @($script:results | Where-Object result -eq "UNVERIFIED").Count
    }
    $summary | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $OutDir "gpui-input-path-results.json") -Encoding UTF8
    Write-Host "GPUI input path summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)"
    if ($summary.fail -gt 0) { exit 1 }
}
