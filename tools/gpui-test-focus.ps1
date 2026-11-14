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
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
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
    if ([AmareTestFocus]::GetForegroundWindow() -eq $hwnd) { return $true }

    # A real mouse click supplies the user-input token Windows requires for
    # foreground activation. PostMessage clicks do not.
    [void][AmareTestFocus]::ShowWindow($hwnd, 9)
    $rect = New-Object AmareTestFocus+RECT
    if ([AmareTestFocus]::GetWindowRect($hwnd, [ref]$rect)) {
        $x = [int](($rect.Left + $rect.Right) / 2)
        $y = $rect.Top + 12
        [void][AmareTestFocus]::SetCursorPos($x, $y)
        Start-Sleep -Milliseconds 80
        [AmareTestFocus]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [AmareTestFocus]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 250
        [void][AmareTestFocus]::SetForegroundWindow($hwnd)
    }
    return ([AmareTestFocus]::GetForegroundWindow() -eq $hwnd)
}

function Send-GpuiShortcut([IntPtr]$hwnd, [int]$vk, [switch]$NoCtrl) {
    # Use real keyboard input rather than PostMessage. PostMessage does not
    # update the thread modifier state, so GPUI could see Ctrl+T without Ctrl.
    [void](Force-AmareForeground $hwnd)
    if (-not $NoCtrl) { [AmareTestFocus]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero) }
    [AmareTestFocus]::keybd_event([byte]$vk, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [AmareTestFocus]::keybd_event([byte]$vk, 0, 2, [UIntPtr]::Zero)
    if (-not $NoCtrl) { [AmareTestFocus]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero) }
    Start-Sleep -Milliseconds 350
}

function Send-GpuiClick([IntPtr]$hwnd, [int]$x, [int]$y) {
    [void](Force-AmareForeground $hwnd)
    $lp = [IntPtr](($y -shl 16) -bor ($x -band 0xFFFF))
    [void][AmareTestFocus]::PostMessage($hwnd, 0x0200, [IntPtr]0, $lp)
    Start-Sleep -Milliseconds 60
    [void][AmareTestFocus]::PostMessage($hwnd, 0x0201, [IntPtr]1, $lp)
    Start-Sleep -Milliseconds 80
    [void][AmareTestFocus]::PostMessage($hwnd, 0x0202, [IntPtr]0, $lp)
    Start-Sleep -Milliseconds 350
}

function Send-RealClientClick([IntPtr]$hwnd, [int]$x, [int]$y) {
    [void](Force-AmareForeground $hwnd)
    $point = New-Object AmareTestFocus+POINT
    [void][AmareTestFocus]::ClientToScreen($hwnd, [ref]$point)
    [void][AmareTestFocus]::SetCursorPos(($point.X + $x), ($point.Y + $y))
    Start-Sleep -Milliseconds 120
    [AmareTestFocus]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [AmareTestFocus]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 400
}
