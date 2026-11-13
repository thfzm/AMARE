param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$StatusDir,
    [Parameter(Mandatory = $true)][string]$DataDir,
    [Parameter(Mandatory = $true)][string]$Output,
    [Parameter(Mandatory = $true)][string]$ScreenshotDir
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\gpui-test-focus.ps1"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$script:app = $null
function Read-AllStates {
    $states = @()
    if (-not (Test-Path $StatusDir)) { return $states }
    foreach ($file in Get-ChildItem $StatusDir -Filter "status-*.json" -File -ErrorAction SilentlyContinue) {
        try {
            $state = Get-Content $file.FullName -Raw -ErrorAction Stop | ConvertFrom-Json
            if ($null -ne $state -and $state.pid -eq $script:app.Id) { $states += $state }
        } catch { }
    }
    return $states
}
function ElementInfo($element, [int]$depth) {
    try {
        $rect = $element.Current.BoundingRectangle
        $patterns = @($element.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName })
        return [pscustomobject]@{
            depth = $depth
            name = $element.Current.Name
            automation_id = $element.Current.AutomationId
            control_type = $element.Current.ControlType.ProgrammaticName
            class_name = $element.Current.ClassName
            bounds = @($rect.X, $rect.Y, $rect.Width, $rect.Height)
            is_enabled = $element.Current.IsEnabled
            is_offscreen = $element.Current.IsOffscreen
            is_keyboard_focusable = $element.Current.IsKeyboardFocusable
            native_window_handle = $element.Current.NativeWindowHandle
            patterns = $patterns
        }
    } catch { return $null }
}
function Walk($element, [int]$depth, [int]$maxDepth, $list) {
    if ($null -eq $element -or $depth -gt $maxDepth) { return }
    $info = ElementInfo $element $depth
    if ($null -ne $info) { $list.Add($info) }
    $child = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetFirstChild($element)
    while ($null -ne $child) {
        Walk $child ($depth + 1) $maxDepth $list
        $child = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetNextSibling($child)
    }
}

Remove-Item $StatusDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $StatusDir, $DataDir, (Split-Path $Output -Parent), $ScreenshotDir | Out-Null
$env:AMARE_GPUI_DATA_DIR = $DataDir
$env:AMARE_GPUI_STATUS_DIR = $StatusDir
Remove-Item Env:AMARE_GPUI_STATUS -ErrorAction SilentlyContinue
Remove-Item Env:AMARE_GPUI_STATUS_WINDOW -ErrorAction SilentlyContinue
$env:AMARE_GPUI_START_URL = "about:blank"
$env:AMARE_GPUI_SELF_TEST = "0"
$env:AMARE_GPUI_RESTORE = "0"
$env:AMARE_GPUI_WINDOW_SIZE = "1044x788"

try {
    $script:app = Start-Process $Exe -PassThru
    $deadline = (Get-Date).AddSeconds(60)
    $state = $null
    while ((Get-Date) -lt $deadline) {
        $states = @(Read-AllStates)
        if ($states.Count -ge 1) { $state = $states[0]; break }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $state) { throw "AMARE status missing" }
    $hwnd = [IntPtr][int64]$state.window_handle
    [void](Force-AmareForeground $hwnd)
    Start-Sleep -Milliseconds 600

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $elements = [System.Collections.Generic.List[object]]::new()
    if ($null -ne $root) { Walk $root 0 5 $elements }
    $beforeTabs = @($state.tabs).Count

    $newTab = $elements | Where-Object { $_.name -match "새 탭|new tab|new-tab" -or $_.automation_id -match "new-tab" } | Select-Object -First 1
    $newWindow = $elements | Where-Object { $_.name -match "새 창|new window|new-window" -or $_.automation_id -match "new-window" } | Select-Object -First 1
    $invokeResult = "not-found"
    if ($null -ne $newTab) {
        try {
            $element = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
            $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $newTab.automation_id)
            $target = $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $target) {
                $pattern = $null
                if ($target.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
                    $pattern.Invoke(); $invokeResult = "invoked"; Start-Sleep -Seconds 2
                } else { $invokeResult = "no-invoke-pattern" }
            } else { $invokeResult = "element-not-found-by-id" }
        } catch { $invokeResult = "error: $($_.Exception.Message)" }
    }
    $statesAfterInvoke = @(Read-AllStates)
    $tabsAfterInvoke = if ($statesAfterInvoke.Count -gt 0) { @($statesAfterInvoke[0].tabs).Count } else { -1 }

    $shot = Join-Path $ScreenshotDir "uia-amare.png"
    try {
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
        $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
    } catch { $shot = "" }

    $result = [pscustomobject]@{
        generated_at = (Get-Date).ToUniversalTime().ToString("o")
        hwnd = $hwnd.ToInt64()
        root_found = ($null -ne $root)
        element_count = $elements.Count
        elements = $elements
        new_tab_element = $newTab
        new_window_element = $newWindow
        invoke_result = $invokeResult
        tabs_before_invoke = $beforeTabs
        tabs_after_invoke = $tabsAfterInvoke
        screenshot = $shot
    }
    $result | ConvertTo-Json -Depth 10 | Set-Content $Output -Encoding UTF8
    Write-Host "UIA diagnostics written: $Output"
} finally {
    if ($null -ne $script:app -and -not $script:app.HasExited) { try { Stop-Process -Id $script:app.Id -Force } catch {} }
}
