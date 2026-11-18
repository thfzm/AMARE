param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$StatusDir,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\gpui-test-focus.ps1"
Add-Type -AssemblyName System.Windows.Forms
New-Item -ItemType Directory -Force -Path $StatusDir, $DataDir, $OutDir | Out-Null
$script:results = [System.Collections.Generic.List[object]]::new()
function Send-Keys([string]$keys) { [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds 100 }
function Add-Result([string]$name, [string]$state, [string]$detail) {
    $script:results.Add([pscustomobject]@{ name = $name; result = $state; detail = $detail })
    Write-Host ("{0} {1}: {2}" -f $state, $name, $detail)
}
function Read-States {
    $states = @()
    foreach ($file in @(Get-ChildItem $StatusDir -Filter "status-*.json" -File -ErrorAction SilentlyContinue)) {
        try { $states += (Get-Content $file.FullName -Raw | ConvertFrom-Json) } catch { }
    }
    return $states
}
function Read-State([IntPtr]$hwnd) { return @(Read-States | Where-Object { [int64]$_.window_handle -eq $hwnd.ToInt64() } | Select-Object -First 1)[0] }
function Wait-States([int]$count, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $states = @(Read-States)
        if ($states.Count -ge $count) { return $states }
        Start-Sleep -Milliseconds 200
    }
    return @(Read-States)
}
function Focus-Address([IntPtr]$hwnd) {
    [void](Force-AmareForeground $hwnd)
    Send-GpuiShortcut $hwnd 0x4C
    Start-Sleep -Milliseconds 250
    $state = Read-State $hwnd
    if ($null -ne $state -and $null -ne $state.address_bounds) {
        $b = $state.address_bounds
        Send-RealClientClick $hwnd ([int]($b.x + $b.width / 2)) ([int]($b.y + $b.height / 2))
        Start-Sleep -Milliseconds 250
    }
    return (Read-State $hwnd)
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
    Remove-Item Env:AMARE_GPUI_OPEN_SECOND_WINDOW -ErrorAction SilentlyContinue
    $app = Start-Process $Exe -PassThru -RedirectStandardOutput (Join-Path $OutDir "shortcut-out.log") -RedirectStandardError (Join-Path $OutDir "shortcut-err.log")
    $states = Wait-States 1 25
    $hwnd = [IntPtr][int64]$states[0].window_handle
    $base = Read-State $hwnd
    $baseTabs = @($base.tabs).Count
    $baseTab = @($base.tabs | Where-Object { $_.id -eq $base.active_tab })[0]
    $baseNav = [int64]$baseTab.navigation_started

    function Click-Control([string]$label, [string]$control, [scriptblock]$changed) {
        $correlation = [guid]::NewGuid().ToString("N")
        $before = Read-State $hwnd
        $bounds = $null
        if ($null -ne $before.layout_bounds -and $null -ne $before.layout_bounds.toolbar_children) {
            $bounds = $before.layout_bounds.toolbar_children.$control
        }
        if ($null -eq $bounds) {
            $available = if ($null -ne $before.layout_bounds -and $null -ne $before.layout_bounds.toolbar_children) { @($before.layout_bounds.toolbar_children.PSObject.Properties.Name) -join ',' } else { '<no toolbar_children>' }
            $layoutKeys = if ($null -ne $before.layout_bounds) { @($before.layout_bounds.PSObject.Properties.Name) -join ',' } else { '<null>' }
            Add-Result "click-$label" "UNVERIFIED" "correlation=$correlation control=$control bounds missing available=$available layout_keys=$layoutKeys layout_bounds=$($before.layout_bounds | ConvertTo-Json -Compress -Depth 4)"
            return
        }
        [void](Force-AmareForeground $hwnd)
        $at = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        $mouseBefore = [int64]$before.mouse_down_count
        Send-RealClientClick $hwnd ([int]($bounds.x + $bounds.width / 2)) ([int]($bounds.y + $bounds.height / 2))
        Start-Sleep -Milliseconds 500
        $after = Read-State $hwnd
        $msgs = @($after.mouse_trace | Where-Object { [int64]$_.at_ms -ge [int64]$at })
        $down = @($msgs | Where-Object { [int]$_.message -eq 0x201 }).Count
        $up = @($msgs | Where-Object { [int]$_.message -eq 0x202 }).Count
        $hit = @($msgs | Where-Object { [int]$_.message -eq 0x84 }).Count
        $stateChanged = & $changed $before $after
        Add-Result "click-$label" ($(if ($stateChanged) { "PASS" } else { "UNVERIFIED" })) "correlation=$correlation control=$control bounds=$($bounds.x),$($bounds.y),$($bounds.width),$($bounds.height) mouse_delta=$([int64]$after.mouse_down_count - $mouseBefore) wm_hit=$hit wm_down=$down wm_up=$up focus_before=$($before.os_focus.keyboard_focus_class) focus_after=$($after.os_focus.keyboard_focus_class) panel=$($before.panel)->$($after.panel)"
    }
    Click-Control "new-tab" "toolbar.new_tab" { param($b, $a) @($a.tabs).Count -gt @($b.tabs).Count }
    Click-Control "settings" "toolbar.settings" { param($b, $a) $a.panel -eq "settings" }
    Click-Control "password" "toolbar.password" { param($b, $a) $a.panel -eq "passwords" }
    Click-Control "downloads" "toolbar.downloads" { param($b, $a) $a.panel -eq "downloads" }
    Click-Control "bookmarks" "toolbar.bookmarks" { param($b, $a) $a.panel -eq "bookmarks" }
    Click-Control "address" "toolbar.address" { param($b, $a) $a.address_focused -eq $true }

    $afterL = Focus-Address $hwnd
    Add-Result "shortcut-ctrl-l" ($(if ($afterL.address_focused -and @($afterL.selected_range).Count -eq 2) { "PASS" } else { "UNVERIFIED" })) "address_focused=$($afterL.address_focused) selected=$($afterL.selected_range -join ',') focus=$($afterL.os_focus.keyboard_focus_class)"

    [void](Focus-Address $hwnd)
    Send-GpuiShortcut $hwnd 0x54
    Start-Sleep -Milliseconds 500
    $afterT = Read-State $hwnd
    $tabsAfterT = @($afterT.tabs).Count
    Add-Result "shortcut-ctrl-t" ($(if ($tabsAfterT -gt $baseTabs) { "PASS" } else { "FAIL" })) "tabs=$baseTabs->$tabsAfterT active=$($afterT.active_tab)"

    $navTabBefore = @($afterT.tabs | Where-Object { $_.id -eq $afterT.active_tab })[0]
    $navBefore = [int64]$navTabBefore.navigation_started
    [void](Focus-Address $hwnd)
    Send-GpuiShortcut $hwnd 0x52
    Start-Sleep -Milliseconds 800
    $afterR = Read-State $hwnd
    $navTabAfter = @($afterR.tabs | Where-Object { $_.id -eq $afterR.active_tab })[0]
    Add-Result "shortcut-ctrl-r" ($(if ([int64]$navTabAfter.navigation_started -gt $navBefore) { "PASS" } else { "UNVERIFIED" })) "navigation_started=$navBefore->$($navTabAfter.navigation_started) loading=$($navTabAfter.loading)"

    $tabsBeforeW = @($afterR.tabs).Count
    [void](Focus-Address $hwnd)
    Send-GpuiShortcut $hwnd 0x57
    Start-Sleep -Milliseconds 700
    $afterW = Read-State $hwnd
    $tabsAfterW = @($afterW.tabs).Count
    Add-Result "shortcut-ctrl-w" ($(if ($tabsAfterW -lt $tabsBeforeW -or $afterW.last_action -match "CloseTab") { "PASS" } else { "UNVERIFIED" })) "tabs=$tabsBeforeW->$tabsAfterW last_action=$($afterW.last_action)"

    [void](Focus-Address $hwnd)
    Send-GpuiShortcut $hwnd 0x4E
    $states2 = Wait-States 2 10
    Add-Result "shortcut-ctrl-n" ($(if ($states2.Count -ge 2) { "PASS" } else { "FAIL" })) "windows=$($states2.Count) hwnds=$((@($states2 | ForEach-Object { $_.window_handle }) -join ','))"
    if ($states2.Count -ge 2) {
        $a = [IntPtr][int64]$states2[0].window_handle
        $b = [IntPtr][int64]$states2[1].window_handle
        $aFocus1 = Focus-Address $a
        $bFocus = Focus-Address $b
        $aFocus2 = Focus-Address $a
        Add-Result "multi-focus-a1" ($(if ($aFocus1.address_focused) { "PASS" } else { "UNVERIFIED" })) "a=0x$('{0:X}' -f $a.ToInt64()) selected=$($aFocus1.selected_range -join ',') focus=$($aFocus1.os_focus.keyboard_focus_class)"
        Add-Result "multi-focus-b" ($(if ($bFocus.address_focused) { "PASS" } else { "UNVERIFIED" })) "b=0x$('{0:X}' -f $b.ToInt64()) selected=$($bFocus.selected_range -join ',') focus=$($bFocus.os_focus.keyboard_focus_class)"
        Add-Result "multi-focus-a2" ($(if ($aFocus2.address_focused) { "PASS" } else { "UNVERIFIED" })) "a=0x$('{0:X}' -f $a.ToInt64()) selected=$($aFocus2.selected_range -join ',') focus=$($aFocus2.os_focus.keyboard_focus_class)"
        Send-ScanCodeText "alpha"
        Start-Sleep -Milliseconds 300
        $aTyped = Read-State $a
        [void](Force-AmareForeground $b)
        Send-GpuiShortcut $b 0x4C
        Send-Keys "^a"; Send-Keys "{BACKSPACE}"
        Send-ScanCodeText "beta"
        Start-Sleep -Milliseconds 300
        $bTyped = Read-State $b
        $aAfterB = Read-State $a
        Add-Result "multi-input-independence" ($(if ([string]$aTyped.address -eq "alpha" -and [string]$bTyped.address -eq "beta" -and [string]$aAfterB.address -eq "alpha") { "PASS" } else { "UNVERIFIED" })) "a=$($aTyped.address) b=$($bTyped.address) a_after_b=$($aAfterB.address)"
        [void][AmareTestFocus]::PostMessage($b, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 800
        $remaining = @(Read-States)
        Add-Result "multi-close-b" ($(if ($remaining.Count -eq 1 -and [int64]$remaining[0].window_handle -eq $a.ToInt64()) { "PASS" } else { "UNVERIFIED" })) "remaining=$($remaining.Count) hwnd=$(@($remaining | ForEach-Object { $_.window_handle }) -join ',')"
    }
} catch {
    Add-Result "shortcut-fatal" "FAIL" $_.Exception.Message
} finally {
    if ($null -ne $app -and -not $app.HasExited) { try { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue } catch { } }
    $summary = [pscustomobject]@{
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        results = $script:results
        pass = @($script:results | Where-Object result -eq "PASS").Count
        fail = @($script:results | Where-Object result -eq "FAIL").Count
        unverified = @($script:results | Where-Object result -eq "UNVERIFIED").Count
    }
    $summary | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $OutDir "gpui-shortcut-results.json") -Encoding UTF8
    Write-Host "GPUI shortcut summary PASS=$($summary.pass) FAIL=$($summary.fail) UNVERIFIED=$($summary.unverified)"
    if ($summary.fail -gt 0) { exit 1 }
}
