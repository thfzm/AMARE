$ErrorActionPreference = "Stop"
if (-not ("AmareTestFocus" -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AmareTestFocus {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
'@
}
function Force-AmareForeground([IntPtr]$hwnd) {
    if ($hwnd -eq [IntPtr]::Zero) { return $false }
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        if ([AmareTestFocus]::GetForegroundWindow() -eq $hwnd) { return $true }
        $foreground = [AmareTestFocus]::GetForegroundWindow()
        $foregroundPid = 0
        $foregroundThread = [AmareTestFocus]::GetWindowThreadProcessId($foreground, [ref]$foregroundPid)
        $currentThread = [AmareTestFocus]::GetCurrentThreadId()
        $attached = $false
        try {
            if ($foregroundThread -ne 0 -and $foregroundThread -ne $currentThread) {
                $attached = [AmareTestFocus]::AttachThreadInput($currentThread, $foregroundThread, $true)
            }
            [void][AmareTestFocus]::BringWindowToTop($hwnd)
            [void][AmareTestFocus]::SetForegroundWindow($hwnd)
            [void][AmareTestFocus]::SetFocus($hwnd)
        } finally {
            if ($attached) {
                [void][AmareTestFocus]::AttachThreadInput($currentThread, $foregroundThread, $false)
            }
        }
        if ([AmareTestFocus]::GetForegroundWindow() -eq $hwnd) { return $true }
        Start-Sleep -Milliseconds 150
    }
    [AmareTestFocus]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [void][AmareTestFocus]::SetForegroundWindow($hwnd)
    [AmareTestFocus]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 150
    return ([AmareTestFocus]::GetForegroundWindow() -eq $hwnd)
}

function Send-GpuiShortcut([IntPtr]$hwnd, [int]$vk, [switch]$NoCtrl) {
    [void](Force-AmareForeground $hwnd)
    if (-not $NoCtrl) { [AmareTestFocus]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero) }
    [void][AmareTestFocus]::PostMessage($hwnd, 0x0100, [IntPtr]$vk, [IntPtr]1)
    [void][AmareTestFocus]::PostMessage($hwnd, 0x0101, [IntPtr]$vk, [IntPtr]1)
    if (-not $NoCtrl) { [AmareTestFocus]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero) }
    Start-Sleep -Milliseconds 300
}
