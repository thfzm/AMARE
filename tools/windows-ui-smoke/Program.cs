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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

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
                return "AutomationElement main window found";
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
            Check("find_in_page", OpenFindAndSearch);
            CloseFind();
            Check("reader_mode", OpenAndCloseReader);
            Check("ai_panel", OpenAndCloseAiPanel);
            Check("settings_dialog", OpenAndCloseSettings);
            Check("multi_tab_stress", CreateTabsAndCheck);
            Check("session_restore", RestartAndCheckSession);
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
        var edit = FindAddressEdit();
        SetValue(edit, "https://example.com/");
        var go = WaitFindById("GoButton", TimeSpan.FromSeconds(5));
        Invoke(go);
        WaitFor(() => _process?.MainWindowTitle.Contains("Example Domain", StringComparison.OrdinalIgnoreCase) == true,
            TimeSpan.FromSeconds(15), "Example Domain title");
        Thread.Sleep(1500);
        return _process!.MainWindowTitle;
    }

    private static string CreateNewTab()
    {
        var before = CountTabs();
        Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
        WaitFor(() => CountTabs() > before, TimeSpan.FromSeconds(5), "new tab count");
        return $"tabs {before}->{CountTabs()}";
    }

    private static string OpenFindAndSearch()
    {
        Invoke(FindById("MenuButton") ?? throw new InvalidOperationException("MenuButton missing"));
        InvokeMenu("Find in page");
        var box = WaitFindById("FindBox", TimeSpan.FromSeconds(5));
        SetValue(box, "Example Domain");
        box.SetFocus();
        SendKeys.SendWait("{ENTER}");
        WaitFor(() =>
        {
            var count = FindById("FindCount")?.Current.Name ?? "";
            return count.Length > 0 && count != "0/0";
        }, TimeSpan.FromSeconds(5), "find result count");
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
        WaitFor(() =>
        {
            var close = FindById("ReaderCloseButton");
            return close is not null && !close.Current.IsOffscreen;
        }, TimeSpan.FromSeconds(10), "Reader close button visibility");
        var title = FindById("ReaderTitleText")?.Current.Name ?? "";
        Invoke(FindById("ReaderCloseButton") ?? throw new InvalidOperationException("ReaderCloseButton missing"));
        return string.IsNullOrWhiteSpace(title) ? "reader opened/closed" : $"reader={title}";
    }

    private static string OpenAndCloseAiPanel()
    {
        Invoke(FindById("AiButton") ?? throw new InvalidOperationException("AiButton missing"));
        WaitFor(() =>
        {
            var close = FindById("AiCloseButton");
            return close is not null && !close.Current.IsOffscreen;
        }, TimeSpan.FromSeconds(8), "AI close button visibility");
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

    private static string CreateTabsAndCheck()
    {
        for (var i = 0; i < 5; i++)
        {
            Invoke(FindById("NewTabButton") ?? throw new InvalidOperationException("NewTabButton missing"));
        }
        WaitFor(() => CountTabs() >= 6, TimeSpan.FromSeconds(8), "six tabs");
        _process!.Refresh();
        return $"tabs={CountTabs()} working_set_mb={_process.WorkingSet64 / 1024 / 1024}";
    }

    private static string RestartAndCheckSession()
    {
        var tabsBefore = CountTabs();
        Require(tabsBefore > 0, "no tabs before restart");
        TryClose(graceful: true);
        WaitFor(() => _process?.HasExited == true, TimeSpan.FromSeconds(15), "process exit");
        _process = null;
        _window = null;
        Launch(_exePath!);
        WaitFor(() => CountTabs() > 0, TimeSpan.FromSeconds(15), "restored tabs");
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
