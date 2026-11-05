using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class Program
{
    private static readonly List<Result> Results = [];
    private static Process? _process;
    private static AutomationElement? _window;
    private static string? _exePath;
    private static long _startupMs;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;

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
            Check("window_created", () =>
            {
                Require(_window is not null, "main window not found");
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
            Check("address_navigation", NavigateToExample);
            Check("new_tab", CreateNewTab);
            Check("tab_switch_shortcut", SwitchTabShortcut);
            Check("tab_close_shortcut", CloseActiveTabShortcut);
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
            Check("multi_tab_stress", CreateTabsAndCheck);
            Check("session_restore", RestartAndCheckSession);
            Check("session_restore_forced", RestartAfterForcedKill);
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

    private static string CloseActiveTabShortcut()
    {
        var before = CountTabs();
        SetForegroundWindow(_process?.MainWindowHandle ?? IntPtr.Zero);
        SendKeys.SendWait("^w");
        WaitFor(() => CountTabs() < before, TimeSpan.FromSeconds(5), "tab close");
        return $"tab_close {before}->{CountTabs()}";
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
        Invoke(FindById("MenuButton") ?? throw new InvalidOperationException("MenuButton missing"));
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
        var cancel = FindByName("Cancel");
        if (cancel is not null)
        {
            Invoke(cancel);
        }
        return "Settings dialog opened";
    }

    private static string OpenAndCloseApiAiSettings()
    {
        Invoke(FindById("MenuButton") ?? throw new InvalidOperationException("MenuButton missing"));
        InvokeMenu("API AI settings…");
        WaitFor(() => FindByName("API AI settings") is not null, TimeSpan.FromSeconds(8), "API AI settings dialog");
        var cancel = FindByName("Cancel");
        if (cancel is not null)
        {
            Invoke(cancel);
        }
        WaitFor(() => FindByName("API AI settings") is null, TimeSpan.FromSeconds(8), "API AI settings dialog close");
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
        var close = FindByName("Close") ?? FindByName("Cancel");
        if (close is not null) Invoke(close);
        SendKeys.SendWait("{ESC}");
        return "API AI no-key guard opened";
    }

    private static string OpenAndCloseHistory()
    {
        Invoke(FindById("MenuButton") ?? throw new InvalidOperationException("MenuButton missing"));
        InvokeMenu("History");
        WaitFor(() => FindByName("History") is not null, TimeSpan.FromSeconds(8), "History dialog");
        var close = FindByName("Close");
        if (close is not null) Invoke(close);
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
        Invoke(FindById("DownloadsButton") ?? throw new InvalidOperationException("DownloadsButton missing"));
        WaitFor(() => FindByName("No downloads in this session") is not null
            || FindByName("Downloads") is not null, TimeSpan.FromSeconds(5), "Downloads menu");
        SendKeys.SendWait("{ESC}");
        return "Downloads menu opened";
    }

    private static string ResizeMaximizeRestore()
    {
        if (_window is null || !_window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
        {
            throw new InvalidOperationException("WindowPattern unavailable");
        }
        var window = (WindowPattern)pattern;
        if (!window.Current.CanMaximize) throw new InvalidOperationException("window cannot maximize");
        window.SetWindowVisualState(WindowVisualState.Maximized);
        Thread.Sleep(800);
        if (window.Current.WindowVisualState != WindowVisualState.Maximized)
        {
            throw new InvalidOperationException("window did not maximize");
        }
        window.SetWindowVisualState(WindowVisualState.Normal);
        Thread.Sleep(500);
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
        var close = FindByName("Close");
        if (close is not null)
        {
            Invoke(close);
        }
        WaitFor(() => FindByName("Update check") is null, TimeSpan.FromSeconds(8), "update check dialog close");
        return detail;
    }

    private static string OpenAndCloseDiagnostics()
    {
        Invoke(FindById("MenuButton") ?? throw new InvalidOperationException("MenuButton missing"));
        InvokeMenu("Diagnostics…");
        WaitFor(() => FindByName("Diagnostics") is not null, TimeSpan.FromSeconds(8), "diagnostics dialog");
        var close = FindByName("Close");
        if (close is not null)
        {
            Invoke(close);
        }
        WaitFor(() => FindByName("Diagnostics") is null, TimeSpan.FromSeconds(8), "diagnostics dialog close");
        return "diagnostics dialog opened";
    }

    private static string OpenAndCloseNewWindow()
    {
        if (_process is null) throw new InvalidOperationException("process missing");
        var before = WindowsForProcess(_process.Id);
        var beforeIds = before.Select(window => string.Join(",", window.GetRuntimeId())).ToHashSet();
        Invoke(FindById("MenuButton") ?? throw new InvalidOperationException("MenuButton missing"));
        InvokeMenu("New window");
        AutomationElement? created = null;
        WaitFor(() =>
        {
            var current = WindowsForProcess(_process.Id);
            created = current.FirstOrDefault(window => !beforeIds.Contains(string.Join(",", window.GetRuntimeId())));
            return created is not null;
        }, TimeSpan.FromSeconds(10), "new window");
        if (created is null) throw new InvalidOperationException("new window not found");
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
        return $"tabs={CountTabs()} working_set_mb={_process.WorkingSet64 / 1024 / 1024} create5_ms={started.ElapsedMilliseconds}";
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

    private static int CountTabs()
    {
        var list = FindById("TabList");
        if (list is null) return 0;
        return list.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count;
    }

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
