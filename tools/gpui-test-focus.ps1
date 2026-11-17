$ErrorActionPreference = "Stop"
. "$PSScriptRoot\gpui-exit-code.ps1"
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
    [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int maxCount);
}
'@
}

function Get-AmareGuiFocus {
    param([IntPtr]$WindowHandle, [uint32]$GuiThreadId)
    $info = New-Object AmareTestFocus+GUITHREADINFO
    $info.cbSize = [uint32][System.Runtime.InteropServices.Marshal]::SizeOf([type][AmareTestFocus+GUITHREADINFO])
    $ok = $false
    if ($GuiThreadId -ne 0) {
        $ok = [AmareTestFocus]::GetGUIThreadInfo($GuiThreadId, [ref]$info)
    }
    $focus = if ($ok) { $info.hwndFocus } else { [IntPtr]::Zero }
    $root = if ($focus -ne [IntPtr]::Zero) { [AmareTestFocus]::GetAncestor($focus, 2) } else { [IntPtr]::Zero }
    $class = ""
    if ($focus -ne [IntPtr]::Zero) {
        $buf = New-Object System.Text.StringBuilder 256
        [void][AmareTestFocus]::GetClassName($focus, $buf, $buf.Capacity)
        $class = $buf.ToString()
    }
    [pscustomobject]@{
        ok = $ok
        gui_thread_id = $GuiThreadId
        focus_hwnd = $focus.ToInt64()
        focus_root_hwnd = $root.ToInt64()
        focus_class = $class
        focus_is_window = ($focus -eq $WindowHandle)
        focus_is_descendant = ($focus -ne [IntPtr]::Zero -and $root -eq $WindowHandle)
    }
}

function Send-TextKeys {
    param([string]$Text, [int]$DelayMs = 15)
    $special = @{
        '+' = '{+}'; '^' = '{^}'; '%' = '{%}'; '~' = '{~}';
        '(' = '{(}'; ')' = '{)}'; '{' = '{{}'; '}' = '{}}';
        '[' = '{[}'; ']' = '{]}'
    }
    foreach ($ch in $Text.ToCharArray()) {
        $token = [string]$ch
        if ($special.ContainsKey($token)) { $token = $special[$token] }
        [System.Windows.Forms.SendKeys]::SendWait($token)
        if ($DelayMs -gt 0) { Start-Sleep -Milliseconds $DelayMs }
    }
    Start-Sleep -Milliseconds 120
}
function Force-AmareForeground([IntPtr]$hwnd) {
    $script:AmareForegroundClicked = $false
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
        $script:AmareForegroundClicked = $true
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
    # SendKeys is the input path proven to reach this GPUI build in the Windows
    # runner (raw keybd_event sometimes loses the Ctrl state before GPUI reads it).
    [void](Force-AmareForeground $hwnd)
    $named = @{ 0x54 = "t"; 0x4E = "n"; 0x57 = "w"; 0x52 = "r"; 0x4C = "l" }
    if ($named.ContainsKey($vk)) {
        if ($NoCtrl) { [System.Windows.Forms.SendKeys]::SendWait($named[$vk]) }
        else { [System.Windows.Forms.SendKeys]::SendWait("^" + $named[$vk]) }
        Start-Sleep -Milliseconds 350
        return
    }
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
