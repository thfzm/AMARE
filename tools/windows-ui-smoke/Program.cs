using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Forms;
using WindowsVerify;

internal static class Program
{
    private static readonly List<Result> Results = [];
    private static Dictionary<ushort, string>? _objectTypeNames;
    private static Process? _process;
    private static AutomationElement? _window;
    private static string? _exePath;
    private static long _startupMs;

    [StructLayout(LayoutKind.Explicit, Size = 0x68)]
    private struct ObjectTypeInformation
    {
        [FieldOffset(0x00)] public ushort NameLength;
        [FieldOffset(0x02)] public ushort NameMaximumLength;
        [FieldOffset(0x08)] public IntPtr NameBuffer;
        [FieldOffset(0x5A)] public byte TypeIndex;
    }

    private static Dictionary<ushort, string> ObjectTypeNames()
    {
        var result = new Dictionary<ushort, string>();
        var length = 1 << 16;
        IntPtr buffer = IntPtr.Zero;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            buffer = Marshal.AllocHGlobal(length);
            var status = NtQueryObject(IntPtr.Zero, 3, buffer, length, out var returned);
            if (status == unchecked((int)0xC0000004) && returned > length)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                length = returned;
                continue;
            }
            if (status != 0)
            {
                Marshal.FreeHGlobal(buffer);
                return result;
            }
            try
            {
                var number = (uint)Marshal.ReadInt32(buffer);
                var entry = IntPtr.Add(buffer, 8);
                for (uint i = 0; i < number; i++)
                {
                    var info = Marshal.PtrToStructure<ObjectTypeInformation>(entry);
                    if (info.NameLength > 0 && info.NameBuffer != IntPtr.Zero)
                    {
                        var name = Marshal.PtrToStringUni(info.NameBuffer, info.NameLength / 2) ?? "";
                        if (!string.IsNullOrWhiteSpace(name)) result[info.TypeIndex] = name;
                    }
                    var stride = 0x68 + info.NameMaximumLength;
                    stride = (stride + 7) & ~7;
                    entry = IntPtr.Add(entry, stride);
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemHandleEntry
    {
        public IntPtr Object;
        public IntPtr ProcessId;
        public IntPtr Handle;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returned);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr info, int length, out int returned);
    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle,
        IntPtr targetProcess, out IntPtr targetHandle, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern uint GetProcessId(IntPtr process);

    private static string HandleSnapshot(int pid)
    {
        try
        {
            var length = 1 << 20;
            IntPtr buffer = IntPtr.Zero;
            var status = -1;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                buffer = Marshal.AllocHGlobal(length);
                status = NtQuerySystemInformation(64, buffer, length, out var returned);
                if (status == unchecked((int)0xC0000004) && returned > length)
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                    length = returned;
                    continue;
                }
                break;
            }
            if (buffer == IntPtr.Zero) return "";
            try
            {
                if (status != 0) return "";
                var count = Marshal.ReadIntPtr(buffer).ToInt64();
                var entrySize = Marshal.SizeOf<SystemHandleEntry>();
                var entryAddress = IntPtr.Add(buffer, 16);
                var counts = new Dictionary<ushort, int>();
                var samples = new Dictionary<ushort, IntPtr>();
                for (long i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<SystemHandleEntry>(IntPtr.Add(entryAddress, (int)(i * entrySize)));
                    if (entry.ProcessId.ToInt64() != pid) continue;
                    counts[entry.ObjectTypeIndex] = counts.GetValueOrDefault(entry.ObjectTypeIndex) + 1;
                    samples.TryAdd(entry.ObjectTypeIndex, entry.Handle);
                }
                var target = OpenProcess(0x0040, false, pid);
                try
                {
                    var names = new Dictionary<ushort, string>();
                    var dynamicTypes = _objectTypeNames ??= ObjectTypeNames();
                    foreach (var (typeIndex, _) in samples)
                    {
                        if (dynamicTypes.TryGetValue(typeIndex, out var dynamicName))
                        {
                            names[typeIndex] = dynamicName;
                        }
                    }
                    foreach (var (typeIndex, sourceHandle) in samples)
                    {
                        if (names.ContainsKey(typeIndex) || names.Count >= 8) continue;
                        if (target == IntPtr.Zero
                            || !DuplicateHandle(target, sourceHandle, GetCurrentProcess(), out var duplicate, 0, false, 0x00000002))
                        {
                            names[typeIndex] = $"type{typeIndex}";
                            continue;
                        }
                        try
                        {
                            var typeName = QueryTypeName(duplicate);
                            names[typeIndex] = string.IsNullOrWhiteSpace(typeName) ? $"type{typeIndex}" : typeName;
                        }
                        finally
                        {
                            CloseHandle(duplicate);
                        }
                    }
                    var processType = names
                        .FirstOrDefault(pair => string.Equals(pair.Value, "Process", StringComparison.OrdinalIgnoreCase)).Key;
                    var processTargets = new Dictionary<string, int>();
                    if (processType != 0)
                    {
                        var processEntries = 0;
                        for (long i = 0; i < count; i++)
                        {
                            var entry = Marshal.PtrToStructure<SystemHandleEntry>(IntPtr.Add(entryAddress, (int)(i * entrySize)));
                            if (entry.ProcessId.ToInt64() != pid || entry.ObjectTypeIndex != processType) continue;
                            processEntries++;
                            if (target == IntPtr.Zero
                                || !DuplicateHandle(target, entry.Handle, GetCurrentProcess(), out var processHandle, 0, false, 0x00000002))
                            {
                                continue;
                            }
                            try
                            {
                                var targetPid = (int)GetProcessId(processHandle);
                                var key = targetPid == 0 ? "unknown" : ProcessName(targetPid);
                                processTargets[key] = processTargets.GetValueOrDefault(key) + 1;
                            }
                            finally
                            {
                                CloseHandle(processHandle);
                            }
                        }
                    }
                    var typeSummary = string.Join(",", counts
                        .OrderByDescending(pair => pair.Value)
                        .Take(8)
                        .Select(pair => $"{names.GetValueOrDefault(pair.Key, $"type{pair.Key}")}={pair.Value}"));
                    var processSummary = string.Join(",", processTargets
                        .OrderByDescending(pair => pair.Value)
                        .Take(10)
                        .Select(pair => $"{pair.Key}={pair.Value}"));
                    return typeSummary + (processSummary.Length == 0 ? "" : $";procs=[{processSummary}]");
                }
                finally
                {
                    if (target != IntPtr.Zero) CloseHandle(target);
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return "";
        }
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return $"{process.ProcessName}({pid})";
        }
        catch
        {
            return $"exited({pid})";
        }
    }

    private static string QueryTypeName(IntPtr handle)
    {
        var buffer = Marshal.AllocHGlobal(1024);
        try
        {
            var status = NtQueryObject(handle, 2, buffer, 1024, out _);
            if (status != 0) return "";
            var length = Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer, 8);
            return text == IntPtr.Zero || length <= 0 ? "" : Marshal.PtrToStringUni(text, length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseMove = 0x0001;
    private const uint MouseAbsolute = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    private sealed record Result(string Name, bool Passed, string Detail);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: WindowsUiSmoke <AMARE.Windows.exe>");
            return 2;
        }
        var exe = Path.GetFullPath(args[0]);
        _exePath = exe;
        try
        {
            Launch(exe);
            if (Environment.GetEnvironmentVariable("AMARE_UI_LIFECYCLE") == "1")
            {
                Check("lifecycle_ab_to_j", LifecycleExperiment);
                foreach (var result in Results)
                {
                    Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {result.Name}: {result.Detail}");
                }
                TryClose();
                return Results.All(result => result.Passed) ? 0 : 1;
            }
            Check("window_created", () =>
            {
                Require(_window is not null, "main window not found");
                PrepareScreenshotWindow();
                return $"AutomationElement main window found; startup_ms={_startupMs}";
            });
            Check("initial_ui", () =>
            {
                try
                {
                    WaitFindById("AddressBar", TimeSpan.FromSeconds(20));
                    WaitFindById("NewTabButton", TimeSpan.FromSeconds(10));
                    WaitFindById("MenuButton", TimeSpan.FromSeconds(10));
                    return "address/new-tab/menu controls present";
                }
                catch
                {
                    DumpTree();
                    throw;
                }
            });
            Check("custom_title_bar", CustomTitleBar);
            Check("address_navigation", NavigateToExample);
            Check("new_tab", CreateNewTab);
            Check("tab_switch_shortcut", SwitchTabShortcut);
            Check("tab_close_shortcut", CloseActiveTabShortcut);
            Check("websocket_tab_close", CloseTabReleasesWebSocket);
            Check("tab_drag_reorder", DragFirstTabToLast);
            Check("tab_drag_last_to_first", () => DragTabCombination(2, 0));
            Check("tab_drag_first_to_middle", () => DragTabCombination(0, 1));
            Check("tab_drag_middle_to_last", () => DragTabCombination(1, 2));
            Check("tab_drag_middle_to_first", () => DragTabCombination(1, 0));
            Check("tab_drag_middle_to_other_middle", () => DragTabWithCount(1, 2, 4, true));
            Check("tab_drag_same_position", () => DragTabWithCount(0, 0, 3, false));
            Check("tab_drag_cancel", DragTabCancel);
            Check("tab_drag_after_close", DragTabAfterClose);
            Check("certificate_error_self_signed_ui", () => CertificateErrorUi("self-signed", "보안 인증서 오류"));
            Check("certificate_error_expired_ui", () => CertificateErrorUi("expired", "유효기간이 만료"));
            Check("certificate_error_hostname_mismatch_ui", () => CertificateErrorUi("hostname-mismatch", "호스트 이름이 요청 주소와 일치하지"));
            Check("certificate_error_retry", () => CertificateErrorUi("self-signed", "보안 인증서 오류", retry: true));
            Check("certificate_error_back", CertificateErrorBack);
            Check("certificate_error_other_tab", CertificateErrorOtherTab);
            Check("find_in_page", OpenFindAndSearch);
            CloseFind();
            Check("reader_mode", OpenAndCloseReader);
            Check("ai_panel", OpenAndCloseAiPanel);
            Check("settings_dialog", OpenAndCloseSettings);
            Check("api_ai_settings_dialog", OpenAndCloseApiAiSettings);
            Check("api_ai_no_key", OpenAndCloseApiAiNoKey);
            Check("update_check_dialog", OpenAndCloseUpdateCheck);
            Check("diagnostics_dialog", OpenAndCloseDiagnostics);
            Check("history_dialog", OpenAndCloseHistory);
            Check("bookmarks_menu", OpenAndCloseBookmarks);
            Check("downloads_menu", OpenAndCloseDownloads);
            Check("window_resize_maximize", ResizeMaximizeRestore);
            Check("new_window_open_close", OpenAndCloseNewWindow);
            Check("new_window_button", OpenAndCloseNewWindowButton);
            Check("new_window_shortcut", OpenAndCloseNewWindowShortcut);
            Check("last_tab_recovery", LastTabRecovery);
            Check("webview_focus_shortcuts", WebViewFocusShortcuts);
            Check("cross_window_tab_move", CrossWindowTabMove);
            Check("tear_off_tab", TearOffTab);
            Check("tab_churn", TabChurn);
            Check("multi_tab_stress", CreateTabsAndCheck);
            Check("session_restore", RestartAndCheckSession);
            Check("session_restore_forced", RestartAfterForcedKill);
            if (int.TryParse(Environment.GetEnvironmentVariable("AMARE_UI_STRESS_SECONDS"), out var stressSeconds)
                && stressSeconds > 0)
            {
                Check("stress_long", () => StressLoop(stressSeconds));
            }
            Check("websocket_browser_close", CloseWindowReleasesWebSockets);
        }
        catch (Exception exception)
        {
            Results.Add(new Result("ui_smoke_fatal", false, exception.ToString()));
        }
        finally
        {
            TryClose();
        }

        foreach (var result in Results)
        {
            Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {result.Name}: {result.Detail}");
        }
        var output = Environment.GetEnvironmentVariable("AMARE_UI_SMOKE_RESULTS");
        if (!string.IsNullOrWhiteSpace(output))
        {
            File.WriteAllText(output, JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
        }
        return Results.All(result => result.Passed) ? 0 : 1;
    }

    private static void Launch(string exe)
    {
        var directory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory;
        var started = Stopwatch.StartNew();
        _process = Process.Start(new ProcessStartInfo(exe)
        {
            WorkingDirectory = directory,
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Could not start AMARE.Windows.exe");
        WaitFor(() =>
        {
            _process.Refresh();
            return _process.MainWindowHandle != IntPtr.Zero;
        }, TimeSpan.FromSeconds(20), "main window handle");
        started.Stop();
        _startupMs = started.ElapsedMilliseconds;
        _process.Refresh();
        SetForegroundWindow(_process.MainWindowHandle);
        _window = AutomationElement.FromHandle(_process.MainWindowHandle);
        Thread.Sleep(1500);
    }

    private static void Check(string name, Func<string> action)
    {
        try
        {
            Results.Add(new Result(name, true, action()));
        }
        catch (Exception exception)
        {
            Results.Add(new Result(name, false, exception.Message));
        }
    }

    private static string CustomTitleBar()
    {
        if (_window is null) throw new InvalidOperationException("window missing");
        var windowRect = _window.Current.BoundingRectangle;
        var title = WaitFindById("TitleBarTitle", TimeSpan.FromSeconds(10));
        var titleTop = title.Current.BoundingRectangle.Top - windowRect.Top;
        Require(titleTop < 12, $"AMARE title strip is not at the window top (offset={titleTop:F1}px)");
        var list = WaitFindById("TabList", TimeSpan.FromSeconds(10));
        var tabsTop = list.Current.BoundingRectangle.Top - windowRect.Top;
        Require(tabsTop < 50, $"native title bar still occupies {tabsTop:F1}px above the tab strip");
        CaptureWindow("01-initial-custom-titlebar");
        return $"AMARE title strip at {titleTop:F1}px; tab strip at {tabsTop:F1}px; native title bar removed";
    }

    private static string NavigateToExample()
    {
        var started = Stopwatch.StartNew();
        var edit = FindAddressEdit();
        SetValue(edit, "https://example.com/");
        var go = WaitFindById("GoButton", TimeSpan.FromSeconds(5));
        Invoke(go);
        WaitFor(() => _process?.MainWindowTitle.Contains("Example Domain", StringComparison.OrdinalIgnoreCase) == true,
            TimeSpan.FromSeconds(15), "Example Domain title");
        started.Stop();
        Thread.Sleep(4000);
        return $"{_process!.MainWindowTitle}; navigation_ms={started.ElapsedMilliseconds}";
    }

    private static string CreateNewTab()
    {
        var before = CountTabs();
        Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
        WaitFor(() => CountTabs() > before, TimeSpan.FromSeconds(5), "new tab count");
        Thread.Sleep(500);
        NavigateToExample();
        return $"tabs {before}->{CountTabs()}";
    }

    private static string DragFirstTabToSecond()
    {
        var list = FindById("TabList") ?? throw new InvalidOperationException("TabList missing");
        var before = list.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().ToArray();
        Require(before.Length >= 2, "need two tabs for drag");
        var first = before[0];
        var second = before[1];
        var firstRuntime = first.GetRuntimeId();
        var a = first.Current.BoundingRectangle;
        var b = second.Current.BoundingRectangle;
        var startX = (int)(a.X + Math.Min(28, a.Width / 3));
        var startY = (int)(a.Y + a.Height / 2);
        var endX = (int)(b.X + Math.Min(28, b.Width / 3));
        var endY = (int)(b.Y + b.Height / 2);
        SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
        SetCursorPos(startX, startY);
        Thread.Sleep(300);
        mouse_event(MouseLeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(350);
        for (var i = 1; i <= 20; i++)
        {
            SetCursorPos(startX + (endX - startX) * i / 20, startY + (endY - startY) * i / 20);
            Thread.Sleep(100);
        }
        Thread.Sleep(300);
        mouse_event(MouseLeftUp, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(1200);
        var after = list.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().ToArray();
        Require(after.Length >= 2, "tabs disappeared after drag");
        var moved = !after[0].GetRuntimeId().SequenceEqual(firstRuntime);
        if (!moved) throw new InvalidOperationException("tab order did not change");
        return "first tab reordered by drag";
    }

    private static string SwitchTabShortcut()
    {
        var before = SelectedTabRuntimeId();
        if (string.IsNullOrEmpty(before)) throw new InvalidOperationException("tab selection not exposed");
        SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
        SendKeys.SendWait("^{TAB}");
        WaitFor(() => SelectedTabRuntimeId() != before, TimeSpan.FromSeconds(5), "tab switch");
        return "ctrl+tab switched active tab";
    }

    private static string SelectedTabRuntimeId()
    {
        var list = FindById("TabList");
        if (list is null || !list.TryGetCurrentPattern(SelectionPattern.Pattern, out var pattern)) return "";
        var selection = ((SelectionPattern)pattern).Current.GetSelection();
        return selection.Length == 0 ? "" : string.Join(",", selection[0].GetRuntimeId());
    }

    private static string CloseTabReleasesWebSocket()
    {
        var server = LocalHttpServer.StartAsync().GetAwaiter().GetResult();
        try
        {
            Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
            WaitFor(() => CountTabs() >= 2, TimeSpan.FromSeconds(5), "websocket tab");
            var edit = FindAddressEdit();
            SetValue(edit, server.BaseUrl + "/ws-hold.html");
            Invoke(WaitFindById("GoButton", TimeSpan.FromSeconds(5)));
            WaitFor(() => server.ActiveWebSockets >= 1, TimeSpan.FromSeconds(15), "websocket connection");
            var before = CountTabs();
            SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
            SendKeys.SendWait("^w");
            WaitFor(() => CountTabs() < before, TimeSpan.FromSeconds(5), "websocket tab close");
            WaitFor(() => server.ActiveWebSockets == 0, TimeSpan.FromSeconds(15), "websocket release");
            return "tab close released WebSocket";
        }
        finally
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static string CloseWindowReleasesWebSockets()
    {
        var server = LocalHttpServer.StartAsync().GetAwaiter().GetResult();
        try
        {
            Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
            WaitFor(() => CountTabs() >= 2, TimeSpan.FromSeconds(5), "browser websocket tab");
            var edit = FindAddressEdit();
            SetValue(edit, server.BaseUrl + "/ws-hold.html");
            Invoke(WaitFindById("GoButton", TimeSpan.FromSeconds(5)));
            WaitFor(() => server.ActiveWebSockets >= 1, TimeSpan.FromSeconds(15), "browser websocket connection");
            TryClose(graceful: true);
            WaitFor(() => server.ActiveWebSockets == 0, TimeSpan.FromSeconds(15), "browser websocket release");
            return "window close released WebSocket";
        }
        finally
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static string DragFirstTabToLast()
    {
        for (var i = 0; i < 2; i++)
        {
            Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
        }
        WaitFor(() => CountTabs() >= 3, TimeSpan.FromSeconds(8), "drag tabs");
        var list = FindById("TabList") ?? throw new InvalidOperationException("TabList missing");
        var items = list.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().ToArray();
        Require(items.Length >= 3, "need three tabs for drag");
        if (items[0].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
        {
            ((SelectionItemPattern)selection).Select();
            Thread.Sleep(300);
        }
        var first = items[0];
        var last = items[^1];
        var firstRuntime = first.GetRuntimeId();
        var firstRect = first.Current.BoundingRectangle;
        var lastRect = last.Current.BoundingRectangle;
        var startX = (int)(firstRect.X + Math.Min(24, firstRect.Width / 3));
        var startY = (int)(firstRect.Y + firstRect.Height / 2);
        var endX = (int)(lastRect.X + Math.Max(12, lastRect.Width - 20));
        var endY = (int)(lastRect.Y + lastRect.Height / 2);
        SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
        MoveMouseAbsolute(startX, startY);
        Thread.Sleep(250);
        SendMouseButton(MouseLeftDown);
        Thread.Sleep(300);
        for (var i = 1; i <= 20; i++)
        {
            MoveMouseAbsolute(startX + (endX - startX) * i / 20, startY + (endY - startY) * i / 20);
            Thread.Sleep(80);
        }
        Thread.Sleep(250);
        SendMouseButton(MouseLeftUp);
        Thread.Sleep(1000);

        items = list.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().ToArray();
        Require(items.Length >= 3, "tabs disappeared after drag");
        if (items[0].GetRuntimeId().SequenceEqual(firstRuntime))
            throw new InvalidOperationException("native tab order did not change");
        while (CountTabs() > 1)
        {
            SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
            SendKeys.SendWait("^w");
            Thread.Sleep(400);
        }
        NavigateToExample();
        return "native pointer drag reordered first tab to the end";
    }

    private static void MoveMouseAbsolute(int x, int y)
    {
        var width = Math.Max(1, GetSystemMetrics(0));
        var height = Math.Max(1, GetSystemMetrics(1));
        var input = new Input
        {
            Type = 0,
            Mouse = new MouseInput
            {
                Dx = x * 65535 / width,
                Dy = y * 65535 / height,
                Flags = MouseMove | MouseAbsolute
            }
        };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static void SendMouseButton(uint flag)
    {
        var input = new Input { Type = 0, Mouse = new MouseInput { Flags = flag } };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static string DragTabCombination(int sourceIndex, int targetIndex) =>
        DragTabWithCount(sourceIndex, targetIndex, 3, true);

    private static string DragTabWithCount(int sourceIndex, int targetIndex, int count, bool expectChange)
    {
        for (var i = CountTabs(); i < count; i++)
        {
            Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
            Thread.Sleep(250);
        }
        WaitFor(() => CountTabs() >= count, TimeSpan.FromSeconds(8), "drag combination tabs");
        var list = FindById("TabList") ?? throw new InvalidOperationException("TabList missing");
        var items = list.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().ToArray();
        Require(items.Length >= count, "need enough tabs for drag combination");
        var before = items.Select(item => item.GetRuntimeId()).ToArray();
        if (items[sourceIndex].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
            ((SelectionItemPattern)selection).Select();
        Thread.Sleep(250);
        var sourceRect = items[sourceIndex].Current.BoundingRectangle;
        var targetRect = items[targetIndex].Current.BoundingRectangle;
        var startX = (int)(sourceRect.X + Math.Min(24, sourceRect.Width / 3));
        var startY = (int)(sourceRect.Y + sourceRect.Height / 2);
        var endX = targetIndex < sourceIndex
            ? (int)(targetRect.X + Math.Min(12, targetRect.Width / 4))
            : (int)(targetRect.X + Math.Max(12, targetRect.Width - 20));
        var endY = (int)(targetRect.Y + targetRect.Height / 2);
        SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
        MoveMouseAbsolute(startX, startY);
        Thread.Sleep(250);
        SendMouseButton(MouseLeftDown);
        Thread.Sleep(300);
        for (var i = 1; i <= 20; i++)
        {
            MoveMouseAbsolute(startX + (endX - startX) * i / 20, startY + (endY - startY) * i / 20);
            Thread.Sleep(80);
        }
        Thread.Sleep(250);
        SendMouseButton(MouseLeftUp);
        Thread.Sleep(900);
        items = list.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().ToArray();
        Require(items.Length == count, "drag combination changed tab count");
        var after = items.Select(item => item.GetRuntimeId()).ToArray();
        var changed = !before.Zip(after, (a, b) => a.SequenceEqual(b)).All(same => same);
        if (expectChange && !changed)
            throw new InvalidOperationException($"tab drag {sourceIndex}->{targetIndex} did not change order");
        if (!expectChange && changed)
            throw new InvalidOperationException($"tab drag {sourceIndex}->{targetIndex} changed order unexpectedly");
        while (CountTabs() > 1)
        {
            SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
            SendKeys.SendWait("^w");
            Thread.Sleep(350);
        }
        NavigateToExample();
        return expectChange
            ? $"native drag {sourceIndex}->{targetIndex} changed order"
            : $"native drag {sourceIndex}->{targetIndex} preserved order";
    }

    private static string CloseActiveTabShortcut()
    {
        var before = CountTabs();
        SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
        SendKeys.SendWait("^w");
        WaitFor(() => CountTabs() < before, TimeSpan.FromSeconds(5), "tab close");
        return $"tab_close {before}->{CountTabs()}";
    }

    private static string DragTabCancel()
    {
        for (var i = CountTabs(); i < 3; i++)
        {
            Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
            Thread.Sleep(250);
        }
        WaitFor(() => CountTabs() >= 3, TimeSpan.FromSeconds(8), "cancel drag tabs");
        var list = FindById("TabList") ?? throw new InvalidOperationException("TabList missing");
        var items = list.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().ToArray();
        Require(items.Length >= 3, "need three tabs for cancel drag");
        var before = items.Select(item => item.GetRuntimeId()).ToArray();
        var source = items[1].Current.BoundingRectangle;
        var target = items[0].Current.BoundingRectangle;
        var startX = (int)(source.X + Math.Min(24, source.Width / 3));
        var startY = (int)(source.Y + source.Height / 2);
        var endX = (int)(target.X + Math.Min(12, target.Width / 4));
        var endY = (int)(target.Y + target.Height / 2);
        SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
        MoveMouseAbsolute(startX, startY);
        Thread.Sleep(250);
        SendMouseButton(MouseLeftDown);
        Thread.Sleep(300);
        for (var i = 1; i <= 10; i++)
        {
            MoveMouseAbsolute(startX + (endX - startX) * i / 10, startY + (endY - startY) * i / 10);
            Thread.Sleep(70);
        }
        MoveMouseAbsolute(endX, endY + 120);
        Thread.Sleep(250);
        SendMouseButton(MouseLeftUp);
        Thread.Sleep(700);
        items = list.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().ToArray();
        Require(items.Length == 3, "cancel drag changed tab count");
        var after = items.Select(item => item.GetRuntimeId()).ToArray();
        if (!before.Zip(after, (a, b) => a.SequenceEqual(b)).All(same => same))
            throw new InvalidOperationException("cancel drag changed tab order");
        while (CountTabs() > 1)
        {
            SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
            SendKeys.SendWait("^w");
            Thread.Sleep(350);
        }
        NavigateToExample();
        return "native drag cancelled outside tab strip without reorder";
    }

    private static string DragTabAfterClose()
    {
        for (var i = CountTabs(); i < 4; i++)
        {
            Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
            Thread.Sleep(200);
        }
        WaitFor(() => CountTabs() >= 4, TimeSpan.FromSeconds(8), "drag-after-close tabs");
        SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
        SendKeys.SendWait("^w");
        WaitFor(() => CountTabs() >= 3, TimeSpan.FromSeconds(8), "drag-after-close count");
        return DragTabWithCount(0, 2, 3, true);
    }

    private static string TabChurn()
    {
        var baseline = CountTabs();
        for (var i = 0; i < 6; i++)
        {
            Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
            WaitFor(() => CountTabs() > baseline, TimeSpan.FromSeconds(5), "churn create");
            SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
            SendKeys.SendWait("^w");
            WaitFor(() => CountTabs() == baseline, TimeSpan.FromSeconds(5), "churn close");
        }
        NavigateToExample();
        return $"tab churn preserved tab count={CountTabs()}";
    }

    private static string CertificateErrorOtherTab()
    {
        var server = CertificateTestServer.StartAsync("self-signed").GetAwaiter().GetResult();
        try
        {
            NavigateToExample();
            var before = CountTabs();
            Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
            WaitFor(() => CountTabs() > before, TimeSpan.FromSeconds(5), "certificate other tab");
            var edit = FindAddressEdit();
            SetValue(edit, server.Url + "/other-tab");
            Invoke(WaitFindById("GoButton", TimeSpan.FromSeconds(5)));
            var dialog = WaitForElement(() => FindByName("보안 인증서 오류"), TimeSpan.FromSeconds(20));
            var close = dialog.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "닫기"))
                ?? throw new InvalidOperationException("certificate other-tab close missing");
            Invoke(close);
            WaitFor(() => FindByName("보안 인증서 오류") is null, TimeSpan.FromSeconds(8), "certificate other-tab dialog close");
            SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
            SendKeys.SendWait("^w");
            WaitFor(() => CountTabs() == before, TimeSpan.FromSeconds(8), "certificate other-tab close tab");
            WaitFor(() => _process?.MainWindowTitle.Contains("Example Domain", StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(15), "certificate other-tab independence");
            return "certificate error tab closed while other normal tab remained usable";
        }
        finally
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            NavigateToExample();
        }
    }

    private static string CertificateErrorUi(string kind, string expectedText, bool retry = false)
    {
        var server = CertificateTestServer.StartAsync(kind).GetAwaiter().GetResult();
        try
        {
            var before = CountTabs();
            Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
            WaitFor(() => CountTabs() > before, TimeSpan.FromSeconds(5), "certificate tab");
            var edit = FindAddressEdit();
            var path = "?source=amare-ui-smoke&padding=" + new string('x', 220);
            SetValue(edit, server.Url + "/" + path);
            Invoke(WaitFindById("GoButton", TimeSpan.FromSeconds(5)));
            var dialog = WaitForElement(() => FindByName("보안 인증서 오류"), TimeSpan.FromSeconds(20));
            var requestText = dialog.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>()
                .Select(element => element.Current.Name)
                .FirstOrDefault(name => name.Contains("요청 주소:", StringComparison.Ordinal))
                ?? throw new InvalidOperationException("certificate dialog did not show request URL");
            if (!requestText.Contains(server.Url, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("certificate dialog request URL mismatch");
            if (!requestText.Contains(expectedText, StringComparison.Ordinal))
                throw new InvalidOperationException($"certificate dialog did not contain expected text: {expectedText}");
            if (!requestText.Contains("자동 진행하지 않았습니다", StringComparison.Ordinal))
                throw new InvalidOperationException("certificate dialog omitted cancel explanation");
            var close = dialog.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "닫기"))
                ?? throw new InvalidOperationException("certificate dialog close button missing");
            var retryButton = dialog.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "다시 시도"));
            if (retryButton is null) throw new InvalidOperationException("certificate retry button missing");
            if (retry)
            {
                Invoke(retryButton);
                Thread.Sleep(1800);
                dialog = WaitForElement(() => FindByName("보안 인증서 오류"), TimeSpan.FromSeconds(20));
                close = dialog.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "닫기"))
                    ?? throw new InvalidOperationException("certificate retry dialog close button missing");
            }
            Invoke(close);
            WaitFor(() => FindByName("보안 인증서 오류") is null, TimeSpan.FromSeconds(8), "certificate dialog close");
            SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
            SendKeys.SendWait("^w");
            WaitFor(() => CountTabs() <= before, TimeSpan.FromSeconds(8), "certificate tab close");
            return $"actual {kind} certificate dialog shown with URL, message, cancel, and {(retry ? "retry" : "close")}";
        }
        finally
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static string CertificateErrorBack()
    {
        var server = CertificateTestServer.StartAsync("self-signed").GetAwaiter().GetResult();
        try
        {
            NavigateToExample();
            var before = CountTabs();
            var edit = FindAddressEdit();
            SetValue(edit, server.Url + "/back-test");
            Invoke(WaitFindById("GoButton", TimeSpan.FromSeconds(5)));
            var dialog = WaitForElement(() => FindByName("보안 인증서 오류"), TimeSpan.FromSeconds(20));
            var back = dialog.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "뒤로"))
                ?? throw new InvalidOperationException("certificate back button missing");
            Invoke(back);
            WaitFor(() => _process?.MainWindowTitle.Contains("Example Domain", StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(15), "certificate back navigation");
            if (CountTabs() != before) throw new InvalidOperationException("certificate back changed tab count");
            return "certificate error back returned to previous page";
        }
        finally
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
            NavigateToExample();
        }
    }

    private static AutomationElement WaitForElement(Func<AutomationElement?> find, TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < timeout)
        {
            if (find() is { } element) return element;
            Thread.Sleep(250);
        }
        throw new TimeoutException("UI element not found");
    }

    private static string OpenFindAndSearch()
    {
        Invoke(FindById("MenuButton") ?? throw new InvalidOperationException("MenuButton missing"));
        InvokeMenu("Find in page");
        var box = WaitFindById("FindBox", TimeSpan.FromSeconds(5));
        SetValue(box, "Domain");
        Invoke(WaitFindById("FindGoButton", TimeSpan.FromSeconds(5)));
        WaitFor(() =>
        {
            var count = FindById("FindCount")?.Current.Name ?? "";
            return System.Text.RegularExpressions.Regex.IsMatch(count, "^[1-9][0-9]*/[1-9][0-9]*$");
        }, TimeSpan.FromSeconds(10), "find result count");
        return FindById("FindCount")?.Current.Name ?? "";
    }

    private static void CloseFind()
    {
        if (FindById("FindCloseButton") is { } close)
        {
            Invoke(close);
        }
    }

    private static string OpenAndCloseReader()
    {
        SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
        Invoke(WaitFindById("MenuButton", TimeSpan.FromSeconds(8)));
        InvokeMenu("Reader Mode");
        try
        {
            WaitFor(() =>
            {
                var close = FindById("ReaderCloseButton");
                return close is not null && !close.Current.IsOffscreen;
            }, TimeSpan.FromSeconds(30), "Reader close button visibility");
        }
        catch
        {
            DumpTree();
            throw;
        }
        var title = FindById("ReaderTitleText")?.Current.Name ?? "";
        Invoke(FindById("ReaderCloseButton") ?? throw new InvalidOperationException("ReaderCloseButton missing"));
        return string.IsNullOrWhiteSpace(title) ? "reader opened/closed" : $"reader={title}";
    }

    private static string OpenAndCloseAiPanel()
    {
        Invoke(FindById("AiButton") ?? throw new InvalidOperationException("AiButton missing"));
        try
        {
            WaitFor(() =>
            {
                var close = FindById("AiCloseButton");
                return close is not null && !close.Current.IsOffscreen;
            }, TimeSpan.FromSeconds(30), "AI close button visibility");
        }
        catch
        {
            DumpTree();
            throw;
        }
        Invoke(FindById("AiCloseButton") ?? throw new InvalidOperationException("AiCloseButton missing"));
        return "AI panel opened/closed";
    }

    private static string OpenAndCloseSettings()
    {
        Invoke(FindById("MenuButton") ?? throw new InvalidOperationException("MenuButton missing"));
        InvokeMenu("Settings");
        WaitFor(() => FindByName("Settings") is not null, TimeSpan.FromSeconds(5), "Settings dialog");
        CaptureWindow("02-settings");
        EscapeDialog("Settings");
        return "Settings dialog opened";
    }

    private static string OpenAndCloseApiAiSettings()
    {
        Invoke(FindById("MenuButton") ?? throw new InvalidOperationException("MenuButton missing"));
        InvokeMenu("API AI settings…");
        WaitFor(() => FindByName("API AI settings") is not null, TimeSpan.FromSeconds(8), "API AI settings dialog");
        EscapeDialog("API AI settings");
        return "API AI settings dialog opened";
    }

    private static string OpenAndCloseApiAiNoKey()
    {
        Invoke(FindById("MenuButton") ?? throw new InvalidOperationException("MenuButton missing"));
        InvokeMenu("Ask API AI…");
        WaitFor(() =>
        {
            return FindByName("API AI") is not null || FindByName("Ask API AI") is not null;
        }, TimeSpan.FromSeconds(8), "API AI no-key dialog");
        EscapeDialog("API AI", "Ask API AI");
        return "API AI no-key guard opened";
    }

    private static string OpenAndCloseHistory()
    {
        Invoke(FindById("MenuButton") ?? throw new InvalidOperationException("MenuButton missing"));
        InvokeMenu("History");
        WaitFor(() => FindByName("History") is not null, TimeSpan.FromSeconds(8), "History dialog");
        EscapeDialog("History");
        return "History dialog opened";
    }

    private static string OpenAndCloseBookmarks()
    {
        Invoke(FindById("BookmarksButton") ?? throw new InvalidOperationException("BookmarksButton missing"));
        WaitFor(() =>
        {
            return FindByName("Bookmark current page") is not null
                || FindByName("Remove current page bookmark") is not null;
        }, TimeSpan.FromSeconds(5), "Bookmarks menu");
        SendKeys.SendWait("{ESC}");
        return "Bookmarks menu opened";
    }

    private static string OpenAndCloseDownloads()
    {
        SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
        Invoke(WaitFindById("DownloadsButton", TimeSpan.FromSeconds(8)));
        WaitFor(() => FindByName("No downloads in this session") is not null
            || FindByName("Downloads") is not null, TimeSpan.FromSeconds(5), "Downloads menu");
        SendKeys.SendWait("{ESC}");
        return "Downloads menu opened";
    }

    private static string ResizeMaximizeRestore()
    {
        if (_process is null) throw new InvalidOperationException("process missing");
        var handle = _process.MainWindowHandle;
        SetForegroundWindow(handle);
        ShowWindow(handle, 3);
        Thread.Sleep(1500);
        var style = (long)GetWindowLongPtr(handle, -16);
        if ((style & 0x01000000L) == 0)
        {
            throw new InvalidOperationException("window did not maximize");
        }
        Thread.Sleep(400);
        CaptureWindow("06-maximized");
        ShowWindow(handle, 9);
        Thread.Sleep(800);
        CaptureWindow("07-restored");
        return "maximized and restored";
    }

    private static string OpenAndCloseUpdateCheck()
    {
        Invoke(FindById("MenuButton") ?? throw new InvalidOperationException("MenuButton missing"));
        InvokeMenu("Check for updates…");
        WaitFor(() => FindByName("Update check") is not null, TimeSpan.FromSeconds(30), "update check dialog");
        var detail = FindByName("AMARE is up to date.") is not null
            ? "manual check: up to date (GitHub Releases empty)"
            : "manual check dialog opened";
        EscapeDialog("Update check");
        return detail;
    }

    private static string OpenAndCloseDiagnostics()
    {
        Invoke(FindById("MenuButton") ?? throw new InvalidOperationException("MenuButton missing"));
        InvokeMenu("Diagnostics…");
        WaitFor(() => FindByName("Diagnostics") is not null, TimeSpan.FromSeconds(8), "diagnostics dialog");
        EscapeDialog("Diagnostics");
        return "diagnostics dialog opened";
    }

    private static string OpenAndCloseNewWindow()
    {
        if (_process is null) throw new InvalidOperationException("process missing");
        var before = WindowsForProcess(_process.Id);
        var beforeIds = before.Select(window => string.Join(",", window.GetRuntimeId())).ToHashSet();
        SetForegroundWindow(_process.MainWindowHandle);
        Invoke(WaitFindById("MenuButton", TimeSpan.FromSeconds(8)));
        InvokeMenu("New window");
        AutomationElement? created = null;
        WaitFor(() =>
        {
            var current = WindowsForProcess(_process.Id);
            created = current.FirstOrDefault(window => !beforeIds.Contains(string.Join(",", window.GetRuntimeId())));
            return created is not null;
        }, TimeSpan.FromSeconds(10), "new window");
        if (created is null) throw new InvalidOperationException("new window not found");
        Thread.Sleep(800);
        CaptureDesktop("04-two-windows");
        if (created.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
        {
            ((WindowPattern)pattern).Close();
        }
        WaitFor(() => WindowsForProcess(_process.Id).Length <= before.Length, TimeSpan.FromSeconds(10), "new window close");
        SetForegroundWindow(_process.MainWindowHandle);
        return "new window opened and closed";
    }

    private static AutomationElement[] WindowsForProcess(int processId) =>
        AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId))
            .Cast<AutomationElement>()
            .Where(window => window.Current.ControlType == ControlType.Window)
            .ToArray();

    private static string OpenAndCloseNewWindowButton()
    {
        if (_process is null) throw new InvalidOperationException("process missing");
        var before = WindowsForProcess(_process.Id).Length;
        SetForegroundWindow(_process.MainWindowHandle);
        Invoke(WaitFindById("NewWindowButton", TimeSpan.FromSeconds(8)));
        WaitFor(() => WindowsForProcess(_process.Id).Length > before, TimeSpan.FromSeconds(10), "new window button");
        var windows = WindowsForProcess(_process.Id);
        foreach (var candidate in windows)
        {
            if (candidate.Current.NativeWindowHandle == _process.MainWindowHandle.ToInt64()) continue;
            if (candidate.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
            {
                ((WindowPattern)pattern).Close();
            }
        }
        WaitFor(() => WindowsForProcess(_process.Id).Length <= before, TimeSpan.FromSeconds(10), "new window button close");
        SetForegroundWindow(_process.MainWindowHandle);
        return "NewWindowButton opened and closed a window";
    }

    private static string OpenAndCloseNewWindowShortcut()
    {
        if (_process is null) throw new InvalidOperationException("process missing");
        var before = WindowsForProcess(_process.Id).Length;
        SetForegroundWindow(_process.MainWindowHandle);
        SendKeys.SendWait("^n");
        WaitFor(() => WindowsForProcess(_process.Id).Length > before, TimeSpan.FromSeconds(10), "ctrl+n new window");
        var windows = WindowsForProcess(_process.Id);
        foreach (var candidate in windows)
        {
            if (candidate.Current.NativeWindowHandle == _process.MainWindowHandle.ToInt64()) continue;
            if (candidate.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
            {
                ((WindowPattern)pattern).Close();
            }
        }
        WaitFor(() => WindowsForProcess(_process.Id).Length <= before, TimeSpan.FromSeconds(10), "ctrl+n window close");
        SetForegroundWindow(_process.MainWindowHandle);
        return "Ctrl+N opened and closed a window";
    }

    private static string LastTabRecovery()
    {
        if (_process is null) throw new InvalidOperationException("process missing");
        SetForegroundWindow(_process.MainWindowHandle);
        var guard = 0;
        while (CountTabs() > 1 && guard++ < 20)
        {
            var before = CountTabs();
            SendKeys.SendWait("^w");
            WaitFor(() => CountTabs() < before, TimeSpan.FromSeconds(5), "close extra tab");
            Thread.Sleep(150);
        }
        Require(CountTabs() == 1, "expected exactly one tab before last-tab close");
        SendKeys.SendWait("^w");
        Thread.Sleep(1500);
        Require(CountTabs() == 1, "last tab close must leave one tab");
        var edit = FindAddressEdit();
        SetValue(edit, "https://example.com/");
        Invoke(WaitFindById("GoButton", TimeSpan.FromSeconds(5)));
        WaitFor(() => _process.MainWindowTitle.Contains("Example Domain", StringComparison.OrdinalIgnoreCase) == true,
            TimeSpan.FromSeconds(20), "recovered last tab navigation");
        CaptureWindow("05-last-tab-recovered");
        return "last tab reset to a navigable New Tab";
    }

    private static void ClickWebViewContent()
    {
        if (_process is null || _window is null) throw new InvalidOperationException("window missing");
        var rect = _window.Current.BoundingRectangle;
        SetForegroundWindow(_process.MainWindowHandle);
        SetCursorPos((int)(rect.X + rect.Width / 2), (int)(rect.Y + 260));
        Thread.Sleep(250);
        mouse_event(MouseLeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(80);
        mouse_event(MouseLeftUp, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(500);
    }

    private static string WebViewFocusShortcuts()
    {
        if (_process is null) throw new InvalidOperationException("process missing");
        NavigateToExample();
        var before = CountTabs();
        ClickWebViewContent();
        SendKeys.SendWait("^t");
        WaitFor(() => CountTabs() > before, TimeSpan.FromSeconds(8), "ctrl+t from webview");
        var afterTab = CountTabs();
        ClickWebViewContent();
        SendKeys.SendWait("^l");
        WaitFor(() =>
        {
            var address = FindAddressEdit();
            return address is not null && address.Current.HasKeyboardFocus;
        }, TimeSpan.FromSeconds(5), "ctrl+l from webview");
        var windowsBefore = WindowsForProcess(_process.Id).Length;
        ClickWebViewContent();
        SendKeys.SendWait("^n");
        WaitFor(() => WindowsForProcess(_process.Id).Length > windowsBefore, TimeSpan.FromSeconds(10), "ctrl+n from webview");
        foreach (var candidate in WindowsForProcess(_process.Id))
        {
            if (candidate.Current.NativeWindowHandle == _process.MainWindowHandle.ToInt64()) continue;
            if (candidate.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
            {
                ((WindowPattern)pattern).Close();
            }
        }
        WaitFor(() => WindowsForProcess(_process.Id).Length <= windowsBefore, TimeSpan.FromSeconds(10), "ctrl+n webview window close");
        SetForegroundWindow(_process.MainWindowHandle);
        while (CountTabs() > 1)
        {
            SendKeys.SendWait("^w");
            Thread.Sleep(250);
        }
        return $"webview focus ctrl+t tabs {before}->{afterTab}, ctrl+l focus, ctrl+n window";
    }

    private static string CrossWindowTabMove()
    {
        if (_process is null || _window is null) throw new InvalidOperationException("source window missing");
        var sourceWindow = _window;
        var beforeWindows = WindowsForProcess(_process.Id);
        SetForegroundWindow(_process.MainWindowHandle);
        Invoke(WaitFindById("MenuButton", TimeSpan.FromSeconds(8)));
        InvokeMenu("New window");
        AutomationElement? targetWindow = null;
        WaitFor(() =>
        {
            var current = WindowsForProcess(_process.Id);
            targetWindow = current.FirstOrDefault(window =>
                !beforeWindows.Any(before => before.GetRuntimeId().SequenceEqual(window.GetRuntimeId())));
            return targetWindow is not null;
        }, TimeSpan.FromSeconds(10), "target window");
        if (targetWindow is null) throw new InvalidOperationException("target window not found");
        var targetHandle = (IntPtr)targetWindow.Current.NativeWindowHandle;
        var sourceHandle = (IntPtr)sourceWindow.Current.NativeWindowHandle;
        try
        {
            ShowWindow(targetHandle, 9); // SW_RESTORE
            ShowWindow(sourceHandle, 9);
            var screenWidth = GetSystemMetrics(0);
            var paneWidth = Math.Max(380, (screenWidth - 80) / 2);
            MoveWindow(sourceHandle, 20, 40, paneWidth, 640, true);
            MoveWindow(targetHandle, 20 + paneWidth + 20, 40, paneWidth, 640, true);
            Thread.Sleep(900);
            targetWindow = AutomationElement.FromHandle(targetHandle);
            sourceWindow = AutomationElement.FromHandle(sourceHandle);
            WaitFor(() => CountTabs(sourceWindow) >= 1 && CountTabs(targetWindow) >= 1, TimeSpan.FromSeconds(10), "cross-window tabs");
            var sourceItems = FindByIdIn(sourceWindow, "TabList")?.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().ToArray() ?? [];
            var targetItems = FindByIdIn(targetWindow, "TabList")?.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().ToArray() ?? [];
            Require(sourceItems.Length >= 1 && targetItems.Length >= 1, "cross-window tab elements missing");
            sourceItems[0].SetFocus();
            if (sourceItems[0].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
                ((SelectionItemPattern)selection).Select();
            Thread.Sleep(500);
            sourceWindow = AutomationElement.FromHandle(sourceHandle);
            targetWindow = AutomationElement.FromHandle(targetHandle);
            sourceItems = FindByIdIn(sourceWindow, "TabList")?.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().ToArray() ?? [];
            targetItems = FindByIdIn(targetWindow, "TabList")?.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().ToArray() ?? [];
            Require(sourceItems.Length >= 1 && targetItems.Length >= 1, "cross-window tab elements missing after focus");
            var sourceRect = sourceItems[0].Current.BoundingRectangle;
            var targetRect = targetItems[0].Current.BoundingRectangle;
            var startX = (int)(sourceRect.X + 24);
            var startY = (int)(sourceRect.Y + sourceRect.Height / 2);
            var endX = (int)(targetRect.X + Math.Min(40, targetRect.Width / 2));
            var endY = (int)(targetRect.Y + targetRect.Height / 2);
            SetForegroundWindow(sourceHandle);
            Thread.Sleep(300);
            MoveMouseAbsolute(startX, startY);
            Thread.Sleep(300);
            SendMouseButton(MouseLeftDown);
            Thread.Sleep(300);
            for (var i = 1; i <= 25; i++)
            {
                MoveMouseAbsolute(startX + (endX - startX) * i / 25, startY + (endY - startY) * i / 25);
                Thread.Sleep(90);
            }
            Thread.Sleep(300);
            SendMouseButton(MouseLeftUp);
            WaitFor(() => CountTabs(targetWindow) == 2, TimeSpan.FromSeconds(15), "cross-window target count");
            WaitFor(() => CountTabs(sourceWindow) == 1, TimeSpan.FromSeconds(15), "cross-window source count");
            var firstMove = $"target={CountTabs(targetWindow)} source={CountTabs(sourceWindow)}";

            sourceWindow = AutomationElement.FromHandle(sourceHandle);
            targetWindow = AutomationElement.FromHandle(targetHandle);
            var reverseItems = FindByIdIn(targetWindow, "TabList")?.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().ToArray() ?? [];
            var reverseTargets = FindByIdIn(sourceWindow, "TabList")?.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().ToArray() ?? [];
            Require(reverseItems.Length >= 1 && reverseTargets.Length >= 1, "reverse transfer tabs missing");
            reverseItems[0].SetFocus();
            if (reverseItems[0].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var reverseSelection))
                ((SelectionItemPattern)reverseSelection).Select();
            Thread.Sleep(400);
            sourceWindow = AutomationElement.FromHandle(sourceHandle);
            targetWindow = AutomationElement.FromHandle(targetHandle);
            reverseItems = FindByIdIn(targetWindow, "TabList")?.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().ToArray() ?? [];
            reverseTargets = FindByIdIn(sourceWindow, "TabList")?.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().ToArray() ?? [];
            Require(reverseItems.Length >= 1 && reverseTargets.Length >= 1, "reverse transfer tabs missing after focus");
            var reverseSourceRect = reverseItems[0].Current.BoundingRectangle;
            var reverseTargetRect = reverseTargets[0].Current.BoundingRectangle;
            var reverseStartX = (int)(reverseSourceRect.X + Math.Min(24, reverseSourceRect.Width / 3));
            var reverseStartY = (int)(reverseSourceRect.Y + reverseSourceRect.Height / 2);
            var reverseEndX = (int)(reverseTargetRect.X + Math.Min(12, reverseTargetRect.Width / 4));
            var reverseEndY = (int)(reverseTargetRect.Y + reverseTargetRect.Height / 2);
            SetForegroundWindow(targetHandle);
            Thread.Sleep(300);
            MoveMouseAbsolute(reverseStartX, reverseStartY);
            Thread.Sleep(300);
            SendMouseButton(MouseLeftDown);
            Thread.Sleep(300);
            for (var i = 1; i <= 25; i++)
            {
                MoveMouseAbsolute(
                    reverseStartX + (reverseEndX - reverseStartX) * i / 25,
                    reverseStartY + (reverseEndY - reverseStartY) * i / 25);
                Thread.Sleep(90);
            }
            Thread.Sleep(300);
            SendMouseButton(MouseLeftUp);
            WaitFor(() => CountTabs(sourceWindow) == 2, TimeSpan.FromSeconds(15), "reverse cross-window source count");
            targetWindow = AutomationElement.FromHandle(targetHandle);
            WaitFor(() => CountTabs(targetWindow) == 1, TimeSpan.FromSeconds(15), "reverse cross-window target count");
            return $"cross-window forward {firstMove}; reverse target={CountTabs(targetWindow)} source={CountTabs(sourceWindow)}";
        }
        finally
        {
            try
            {
                var currentTarget = AutomationElement.FromHandle(targetHandle);
                if (currentTarget.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
                    ((WindowPattern)pattern).Close();
            }
            catch { }
            WaitFor(() => WindowsForProcess(_process.Id).Length <= beforeWindows.Length, TimeSpan.FromSeconds(10), "target close");
            SetForegroundWindow(sourceHandle);
        }
    }

    private static string TearOffTab()
    {
        if (_process is null || _window is null) throw new InvalidOperationException("source window missing");
        for (var i = CountTabs(); i < 3; i++)
        {
            Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
            Thread.Sleep(250);
        }
        WaitFor(() => CountTabs() >= 3, TimeSpan.FromSeconds(8), "tear-off tabs");
        NavigateToExample();
        var sourceHandle = _process.MainWindowHandle;
        MoveWindow(sourceHandle, 20, 40, 640, 620, true);
        ShowWindow(sourceHandle, 9);
        SetForegroundWindow(sourceHandle);
        Thread.Sleep(500);
        var sourceWindow = AutomationElement.FromHandle(sourceHandle);
        var list = FindByIdIn(sourceWindow, "TabList") ?? throw new InvalidOperationException("TabList missing");
        var items = list.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().ToArray();
        Require(items.Length >= 3, "need three tabs for tear-off");
        var source = items[1];
        if (source.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
            ((SelectionItemPattern)selection).Select();
        Thread.Sleep(300);
        var sourceRect = source.Current.BoundingRectangle;
        var beforeWindows = WindowsForProcess(_process.Id);
        var startX = (int)(sourceRect.X + Math.Min(24, sourceRect.Width / 3));
        var startY = (int)(sourceRect.Y + sourceRect.Height / 2);
        var endX = Math.Min(GetSystemMetrics(0) - 40, (int)sourceRect.X + 700);
        var endY = (int)sourceRect.Y + 220;
        SetForegroundWindow(sourceHandle);
        MoveMouseAbsolute(startX, startY);
        Thread.Sleep(250);
        SendMouseButton(MouseLeftDown);
        Thread.Sleep(300);
        for (var i = 1; i <= 24; i++)
        {
            MoveMouseAbsolute(startX + (endX - startX) * i / 24, startY + (endY - startY) * i / 24);
            Thread.Sleep(80);
        }
        Thread.Sleep(300);
        SendMouseButton(MouseLeftUp);
        AutomationElement? created = null;
        try
        {
            WaitFor(() =>
            {
                var current = WindowsForProcess(_process.Id);
                created = current.FirstOrDefault(window =>
                    !beforeWindows.Any(before => before.GetRuntimeId().SequenceEqual(window.GetRuntimeId())));
                return created is not null;
            }, TimeSpan.FromSeconds(15), "tear-off window");
            if (created is null) throw new InvalidOperationException("tear-off window not found");
            var targetTabs = CountTabs(created);
            if (targetTabs != 1) throw new InvalidOperationException($"tear-off target tabs={targetTabs}");
            if (CountTabs(sourceWindow) < 2) throw new InvalidOperationException("tear-off source tab lost");
            var targetHandle = (IntPtr)created.Current.NativeWindowHandle;
            var sourceWindowForMerge = AutomationElement.FromHandle(sourceHandle);
            var targetWindowForMerge = AutomationElement.FromHandle(targetHandle);
            DragTabBetweenWindows(targetWindowForMerge, sourceWindowForMerge);
            WaitFor(() => CountTabs(sourceWindowForMerge) == 3, TimeSpan.FromSeconds(15), "tear-off re-merge source");
            WaitFor(() => CountTabs(targetWindowForMerge) <= 1, TimeSpan.FromSeconds(15), "tear-off re-merge target");
            if (created.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
                ((WindowPattern)pattern).Close();
            WaitFor(() => WindowsForProcess(_process.Id).Length <= beforeWindows.Length,
                TimeSpan.FromSeconds(10), "tear-off window close");
            SetForegroundWindow(sourceHandle);
            return $"tear-off target tabs=1 source tabs=2; re-merge source tabs={CountTabs(sourceWindowForMerge)}";
        }
        finally
        {
            if (created is not null)
            {
                try
                {
                    if (created.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
                        ((WindowPattern)pattern).Close();
                }
                catch { }
            }
        }
    }

    private static void DragTabBetweenWindows(AutomationElement from, AutomationElement to)
    {
        if (!from.TryGetCurrentPattern(WindowPattern.Pattern, out _) && from.Current.NativeWindowHandle == 0)
            throw new InvalidOperationException("source window unavailable");
        var fromHandle = (IntPtr)from.Current.NativeWindowHandle;
        var toHandle = (IntPtr)to.Current.NativeWindowHandle;
        var fromList = FindByIdIn(from, "TabList") ?? throw new InvalidOperationException("source TabList missing");
        var toList = FindByIdIn(to, "TabList") ?? throw new InvalidOperationException("target TabList missing");
        var sourceItems = fromList.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().ToArray();
        var targetItems = toList.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().ToArray();
        Require(sourceItems.Length >= 1 && targetItems.Length >= 1, "cross-window tab elements missing");
        sourceItems[0].SetFocus();
        if (sourceItems[0].TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
            ((SelectionItemPattern)selection).Select();
        Thread.Sleep(400);
        sourceItems = fromList.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().ToArray();
        targetItems = toList.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().ToArray();
        Require(sourceItems.Length >= 1 && targetItems.Length >= 1, "cross-window tab elements missing after focus");
        var sourceRect = sourceItems[0].Current.BoundingRectangle;
        var targetRect = targetItems[0].Current.BoundingRectangle;
        var startX = (int)(sourceRect.X + Math.Min(24, sourceRect.Width / 3));
        var startY = (int)(sourceRect.Y + sourceRect.Height / 2);
        var endX = (int)(targetRect.X + Math.Min(12, targetRect.Width / 4));
        var endY = (int)(targetRect.Y + targetRect.Height / 2);
        SetForegroundWindow(fromHandle);
        Thread.Sleep(300);
        MoveMouseAbsolute(startX, startY);
        Thread.Sleep(300);
        SendMouseButton(MouseLeftDown);
        Thread.Sleep(300);
        for (var i = 1; i <= 25; i++)
        {
            MoveMouseAbsolute(startX + (endX - startX) * i / 25, startY + (endY - startY) * i / 25);
            Thread.Sleep(90);
        }
        Thread.Sleep(300);
        SendMouseButton(MouseLeftUp);
        Thread.Sleep(1200);
        _ = fromHandle;
        _ = toHandle;
    }

    private static AutomationElement? FindByIdIn(AutomationElement window, string id) =>
        window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static int CountTabs(AutomationElement window)
    {
        var list = FindByIdIn(window, "TabList");
        if (list is null) return 0;
        return list.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count;
    }

    private static string CreateTabsAndCheck()
    {
        var started = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
        {
            Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
        }
        WaitFor(() => CountTabs() >= 6, TimeSpan.FromSeconds(8), "six tabs");
        started.Stop();
        _process!.Refresh();
        CaptureWindow("03-multiple-tabs");
        return $"tabs={CountTabs()} working_set_mb={_process.WorkingSet64 / 1024 / 1024} create5_ms={started.ElapsedMilliseconds}";
    }

    private static string LifecycleExperiment()
    {
        var records = new List<object>();
        var started = DateTimeOffset.UtcNow;
        var output = Environment.GetEnvironmentVariable("AMARE_LIFECYCLE_RESULTS");
        void Snap(string stage, string note = "")
        {
            try
            {
                _process?.Refresh();
                var snapshot = new
                {
                    stage,
                    note,
                    timestamp = DateTimeOffset.UtcNow,
                    pid = _process?.Id ?? 0,
                    handles = _process?.HasExited == false ? _process.HandleCount : 0,
                    working_set_mb = _process?.HasExited == false ? _process.WorkingSet64 / 1024 / 1024 : 0,
                    tabs = _process?.HasExited == false ? CountTabs() : -1,
                    webview2_processes = System.Diagnostics.Process.GetProcessesByName("msedgewebview2").Length,
                    types = _process?.HasExited == false && _process is not null ? HandleSnapshot(_process.Id) : "exited"
                };
                records.Add(snapshot);
                Console.WriteLine($"{stage}: handles={snapshot.handles} tabs={snapshot.tabs} ws={snapshot.working_set_mb}MB types=[{snapshot.types}]");
            }
            catch (Exception exception)
            {
                records.Add(new { stage, note, timestamp = DateTimeOffset.UtcNow, error = exception.GetType().Name });
            }
        }
        Snap("A_start");
        for (var minute = 1; minute <= 10; minute++)
        {
            Thread.Sleep(TimeSpan.FromMinutes(1));
            Snap($"B_idle_{minute}m");
        }
        Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
        WaitFor(() => CountTabs() >= 2, TimeSpan.FromSeconds(8), "lifecycle tab 1");
        Snap("C_tab_1");
        while (CountTabs() < 10)
        {
            Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
            Thread.Sleep(250);
        }
        Snap("D_tabs_10");
        while (CountTabs() > 1)
        {
            SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
            SendKeys.SendWait("^w");
            Thread.Sleep(200);
        }
        Snap("E_tabs_closed_1");
        while (CountTabs() < 30)
        {
            Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
            Thread.Sleep(200);
        }
        Snap("F_tabs_30");
        while (CountTabs() > 1)
        {
            SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
            SendKeys.SendWait("^w");
            Thread.Sleep(180);
        }
        Snap("G_tabs_closed_all");
        Thread.Sleep(TimeSpan.FromMinutes(1));
        Snap("H_wait_1m");
        Thread.Sleep(TimeSpan.FromMinutes(5));
        Snap("I_wait_5m");
        TryClose(graceful: true);
        Thread.Sleep(1000);
        Snap("J_shutdown", $"process_exited={_process?.HasExited}");
        var summary = $"lifecycle {DateTimeOffset.UtcNow - started} records={records.Count}";
        if (!string.IsNullOrWhiteSpace(output))
        {
            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                started_at = started,
                finished_at = DateTimeOffset.UtcNow,
                records
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return summary;
    }

    private static string RestartAndCheckSession()
    {
        var tabsBefore = CountTabs();
        Require(tabsBefore > 0, "no tabs before restart");
        var started = Stopwatch.StartNew();
        TryClose(graceful: true);
        WaitFor(() => _process?.HasExited == true, TimeSpan.FromSeconds(15), "process exit");
        started.Stop();
        var closeMs = started.ElapsedMilliseconds;
        _process = null;
        _window = null;
        started.Restart();
        Launch(_exePath!);
        WaitFor(() => CountTabs() > 0, TimeSpan.FromSeconds(15), "restored tabs");
        started.Stop();
        return $"tabs_before={tabsBefore} tabs_after={CountTabs()} close_ms={closeMs} restore_ms={started.ElapsedMilliseconds}";
    }

    private static string StressLoop(int seconds)
    {
        var baselineTabs = Math.Max(1, CountTabs());
        var started = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(seconds);
        var samples = new List<string>();
        var nextSample = TimeSpan.Zero;
        var cycle = 0;
        while (started.Elapsed < deadline)
        {
            cycle++;
            SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
            if (cycle % 5 == 0)
            {
                SendKeys.SendWait("^l");
                Thread.Sleep(100);
                SendKeys.SendWait("https://example.com/{ENTER}");
                Thread.Sleep(1800);
            }
            else
            {
                SendKeys.SendWait("^t");
                Thread.Sleep(300);
                SendKeys.SendWait("^w");
                Thread.Sleep(300);
            }
            if (cycle % 7 == 0)
            {
                SendKeys.SendWait("^+{TAB}");
                Thread.Sleep(150);
            }
            if (started.Elapsed >= nextSample)
            {
                nextSample = started.Elapsed + TimeSpan.FromSeconds(60);
                _process?.Refresh();
                // Sample tab count only once per minute. Per-cycle UIAutomation
                // queries are a separate harness cost and are intentionally
                // excluded from this run.
                var tabs = CountTabs();
                var webViewProcesses = System.Diagnostics.Process.GetProcessesByName("msedgewebview2").Length;
                var handleTypes = _process is null ? "" : HandleSnapshot(_process.Id);
                samples.Add($"{started.Elapsed.TotalMinutes:F0}m ws={(_process?.WorkingSet64 ?? 0) / 1024 / 1024}MB " +
                    $"handles={_process?.HandleCount ?? 0} threads={_process?.Threads.Count ?? 0} wv={webViewProcesses} tabs={tabs} types=[{handleTypes}]");
            }
            Thread.Sleep(700);
        }
        while (CountTabs() > baselineTabs)
        {
            SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
            SendKeys.SendWait("^w");
            Thread.Sleep(250);
        }
        return $"stress {seconds}s cycles={cycle} baseline_tabs={baselineTabs} samples=[{string.Join("; ", samples)}]";
    }

    private static string RestartAfterForcedKill()
    {
        var tabsBefore = CountTabs();
        Require(tabsBefore > 0, "no tabs before forced restart");
        Thread.Sleep(1200);
        _process!.Kill(entireProcessTree: true);
        WaitFor(() => _process?.HasExited == true, TimeSpan.FromSeconds(15), "forced process exit");
        _process = null;
        _window = null;
        Launch(_exePath!);
        WaitFor(() => CountTabs() > 0, TimeSpan.FromSeconds(15), "forced restore tabs");
        return $"tabs_before={tabsBefore} tabs_after={CountTabs()}";
    }

    private static void TryClose(bool graceful = false)
    {
        try
        {
            if (graceful && _window is not null)
            {
                if (_window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
                {
                    ((WindowPattern)pattern).Close();
                    Thread.Sleep(500);
                }
            }
            if (_process is { HasExited: false })
            {
                if (!_process.WaitForExit(8000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch { }
    }

    private static AutomationElement FindAddressEdit()
    {
        var direct = FindById("AddressBar");
        if (direct is not null)
        {
            var edit = direct.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (edit is not null) return edit;
        }
        var allEdits = AutomationElement.RootElement.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        foreach (AutomationElement edit in allEdits)
        {
            if (edit.Current.IsOffscreen) continue;
            return edit;
        }
        throw new InvalidOperationException("address edit not found");
    }

    private static AutomationElement? FindById(string id) =>
        _window?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static void DumpTree()
    {
        Console.WriteLine("=== UIAUTOMATION TREE ===");
        if (_window is null)
        {
            Console.WriteLine("window is null");
            return;
        }
        var all = _window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var count = 0;
        foreach (AutomationElement element in all)
        {
            if (count++ >= 200) break;
            try
            {
                Console.WriteLine($"id={element.Current.AutomationId} name={element.Current.Name} " +
                    $"type={element.Current.ControlType.ProgrammaticName} offscreen={element.Current.IsOffscreen}");
            }
            catch (ElementNotAvailableException)
            {
                break;
            }
        }
        Console.WriteLine($"=== END TREE count={count} ===");
    }

    private static AutomationElement WaitFindById(string id, TimeSpan timeout)
    {
        AutomationElement? found = null;
        WaitFor(() =>
        {
            found = FindById(id);
            return found is not null;
        }, timeout, id);
        return found!;
    }

    private static AutomationElement? FindByName(string name) =>
        AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));

    private static void PrepareScreenshotWindow()
    {
        if (_process is null)
        {
            return;
        }
        try
        {
            MoveWindow(_process.MainWindowHandle, 20, 20, 1044, 788, true);
            Thread.Sleep(800);
        }
        catch
        {
            // Screenshot window sizing is best effort and must not fail the suite.
        }
    }

    private static void CaptureWindow(string name)
    {
        try
        {
            var directory = Environment.GetEnvironmentVariable("AMARE_UI_SCREENSHOT_DIR");
            if (string.IsNullOrWhiteSpace(directory) || _window is null)
            {
                return;
            }
            Directory.CreateDirectory(directory);
            var bounds = _window.Current.BoundingRectangle;
            var width = Math.Max(1, (int)Math.Round(bounds.Width));
            var height = Math.Max(1, (int)Math.Round(bounds.Height));
            using var bitmap = new Bitmap(width, height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen((int)Math.Round(bounds.X), (int)Math.Round(bounds.Y), 0, 0, bitmap.Size);
            bitmap.Save(Path.Combine(directory, name + ".png"), ImageFormat.Png);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"WARN screenshot {name}: {exception.Message}");
        }
    }

    private static void CaptureDesktop(string name)
    {
        try
        {
            var directory = Environment.GetEnvironmentVariable("AMARE_UI_SCREENSHOT_DIR");
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }
            Directory.CreateDirectory(directory);
            var bounds = SystemInformation.VirtualScreen;
            using var bitmap = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bitmap.Size);
            bitmap.Save(Path.Combine(directory, name + ".png"), ImageFormat.Png);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"WARN screenshot {name}: {exception.Message}");
        }
    }

    private static void EscapeDialog(params string[] titles)
    {
        SendKeys.SendWait("{ESC}");
        WaitFor(
            () => titles.All(title => FindByName(title) is null),
            TimeSpan.FromSeconds(8),
            $"dialog close ({string.Join(", ", titles)})");
    }

    private static void InvokeMenu(string name)
    {
        WaitFor(() => FindByName(name) is not null, TimeSpan.FromSeconds(5), $"menu {name}");
        Invoke(FindByName(name) ?? throw new InvalidOperationException($"menu item {name} missing"));
    }

    private static void Invoke(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            Thread.Sleep(300);
            return;
        }
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
        {
            ((SelectionItemPattern)selection).Select();
            Thread.Sleep(300);
            return;
        }
        element.SetFocus();
        SendKeys.SendWait("{ENTER}");
        Thread.Sleep(300);
    }

    private static void SetValue(AutomationElement element, string value)
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
        {
            throw new InvalidOperationException("element has no ValuePattern");
        }
        ((ValuePattern)pattern).SetValue(value);
        Thread.Sleep(200);
    }

    private static int CountTabs() => CountTabs(_window!);

    private static void WaitFor(Func<bool> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(200);
        }
        throw new TimeoutException($"Timed out waiting for {description}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
