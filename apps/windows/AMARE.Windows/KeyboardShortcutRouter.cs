using System.Runtime.InteropServices;
using Amare.Windows.Core;
using Microsoft.UI.Dispatching;
using Windows.System;

namespace Amare.Windows;

/// <summary>
/// Routes browser shortcuts that are pressed while a native child window (WebView2)
/// owns the keyboard focus. WinUI KeyboardAccelerators only see keys delivered to
/// the XAML content island, so a WebView2 page or a WebView2-hosted dialog would
/// otherwise swallow Ctrl+T/L/N/W/R before the accelerators run.
///
/// This is a per-UI-thread WH_GETMESSAGE hook: it inspects the message before it is
/// dispatched to the focused HWND, resolves the browser command with the same
/// <see cref="Amare.Windows.Core.BrowserShortcutPolicy"/> used by the XAML
/// accelerators, and dispatches it through the owning MainWindow. Handled messages
/// are turned into WM_NULL so WebView2 cannot also act on them (for example
/// Ctrl+R would otherwise reload twice).
/// </summary>
internal static class KeyboardShortcutRouter
{
    private const int WH_GETMESSAGE = 3;
    private const uint PM_REMOVE = 0x0001;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_NULL = 0x0000;
    private const uint GA_ROOT = 2;
    private const uint GW_OWNER = 4;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    private delegate IntPtr GetMsgProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static readonly GetMsgProc HookProc = Callback;
    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, WeakReference<MainWindow>> Windows = [];
    private static IntPtr _hook;
    private static uint _hookThread;
    private static string _status = "not installed";

    public static string Status
    {
        get
        {
            lock (Gate)
            {
                return _status;
            }
        }
    }

    public static string Install(IntPtr hwnd, MainWindow window)
    {
        lock (Gate)
        {
            if (hwnd != IntPtr.Zero)
            {
                Windows[hwnd] = new WeakReference<MainWindow>(window);
            }
            if (_hook != IntPtr.Zero)
            {
                return _status;
            }
            _hookThread = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_GETMESSAGE, HookProc, IntPtr.Zero, _hookThread);
            _status = _hook != IntPtr.Zero
                ? $"installed thread={_hookThread} hwnd={hwnd.ToInt64()}"
                : $"failed win32={Marshal.GetLastWin32Error()}";
            return _status;
        }
    }

    private static MainWindow? Resolve(IntPtr root)
    {
        lock (Gate)
        {
            foreach (var stale in Windows
                         .Where(pair => !pair.Value.TryGetTarget(out _))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                Windows.Remove(stale);
            }
            return root != IntPtr.Zero && Windows.TryGetValue(root, out var reference)
                && reference.TryGetTarget(out var window)
                ? window
                : null;
        }
    }

    private static MainWindow? ResolveOwnerChain(IntPtr hwnd)
    {
        for (var depth = 0; depth < 8 && hwnd != IntPtr.Zero; depth++)
        {
            if (Resolve(hwnd) is { } window)
            {
                return window;
            }
            hwnd = GetWindow(hwnd, GW_OWNER);
        }
        return null;
    }

    private static MainWindow? ResolveForeground()
    {
        var foreground = GetAncestor(GetForegroundWindow(), GA_ROOT);
        return Resolve(foreground) ?? ResolveOwnerChain(foreground);
    }

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || (uint)wParam.ToInt64() != PM_REMOVE)
        {
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        try
        {
            var message = Marshal.PtrToStructure<NativeMessage>(lParam);
            if (message.Message != WM_KEYDOWN && message.Message != WM_SYSKEYDOWN)
            {
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            var root = GetAncestor(message.Hwnd, GA_ROOT);
            var window = Resolve(root)
                ?? ResolveOwnerChain(root)
                ?? ResolveForeground();
            if (window is null)
            {
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            var control = IsDown(VK_CONTROL);
            var shift = IsDown(VK_SHIFT);
            var alt = IsDown(VK_MENU);
            var key = ((VirtualKey)message.WParam.ToInt32()).ToString();
            var command = BrowserShortcutPolicy.Resolve(control, shift, alt, key);
            if (command is null)
            {
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            // Holding Ctrl+T / Ctrl+N must not create dozens of windows or tabs.
            var isRepeat = (message.LParam.ToInt64() & (1L << 30)) != 0;
            if (isRepeat && command is BrowserCommand.NewTab or BrowserCommand.NewWindow)
            {
                return CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            // Consume the key so WebView2 does not process it a second time.
            message.Message = WM_NULL;
            Marshal.StructureToPtr(message, lParam, false);

            var resolved = command.Value;
            window.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.High,
                () => window.HandleShortcut(resolved));
        }
        catch (Exception exception)
        {
            AppLog.Error("Keyboard shortcut hook failed", exception);
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsDown(int virtualKey) =>
        (GetKeyState(virtualKey) & 0x8000) != 0 || (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook, GetMsgProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
