using Amare.Windows.Core;

namespace Amare.Interop.Tests;

internal static class Phase25Tests
{
    public static void Register(bool coreOnly)
    {
        Program.Run("session_window_restore_orders_and_selects_active", WindowRestoreOrder);
        Program.Run("session_window_ordinal_and_validation", WindowOrdinalAndValidation);
        if (coreOnly)
        {
            return;
        }
        Program.Run("native_session_windows_independent_roundtrip", NativeRoundtrip);
        Program.Run("native_session_windows_save_order_reversed", NativeReversedOrder);
        Program.Run("native_session_windows_legacy_single_window", NativeLegacySingleWindow);
        Program.Run("native_session_windows_concurrent_writers", NativeConcurrentWriters);
    }

    private static string WindowRestoreOrder()
    {
        var window = new SessionWindowDto
        {
            WindowId = "a",
            Ordinal = 1,
            ActiveTabId = 2,
            Tabs =
            [
                new SessionTabDto { Id = 1, Position = 2, Url = "https://2", Title = "2" },
                new SessionTabDto { Id = 2, Position = 1, Url = "https://1", Title = "1" },
                new SessionTabDto { Id = 3, Position = 1, Url = "https://3", Title = "3" }
            ]
        };
        var plan = SessionWindowRestorePlanner.Create(window)!;
        Assert(plan.Tabs.Select(tab => tab.Url).SequenceEqual(["https://1", "https://3", "https://2"]), "order");
        Assert(plan.ActiveIndex == 0, $"active={plan.ActiveIndex}");
        Assert(SessionWindowRestorePlanner.Create(null) is null, "null window");
        Assert(SessionWindowRestorePlanner.Create(new SessionWindowDto { WindowId = "empty" }) is null, "empty window");
        return string.Join(",", plan.Tabs.Select(tab => tab.Url));
    }

    private static string WindowOrdinalAndValidation()
    {
        var windows = new[]
        {
            new SessionWindowDto { WindowId = "b", Ordinal = 4 },
            new SessionWindowDto { WindowId = "a", Ordinal = 2 }
        };
        var ordered = SessionWindowRestorePlanner.Order(windows);
        Assert(ordered.Select(window => window.WindowId).SequenceEqual(["a", "b"]), "ordinal order");
        Assert(SessionWindowRestorePlanner.NextOrdinal(windows) == 5, "next ordinal");
        Assert(SessionWindowRestorePlanner.IsValidWindowId("a"), "valid id");
        Assert(!SessionWindowRestorePlanner.IsValidWindowId(""), "empty id");
        Assert(!SessionWindowRestorePlanner.IsValidWindowId(new string('x', 129)), "long id");
        Assert(!SessionWindowRestorePlanner.IsValidWindowId("a\nb"), "control id");
        return $"ordered={string.Join(",", ordered.Select(window => window.WindowId))} next=5";
    }

    private static string NativeRoundtrip()
    {
        using var root = new TempRoot();
        long sessionId;
        long firstActive;
        using (var native = AmareNative.Create(root.Path))
        {
            var context = new ProfileClient(native).InitializeAsync().GetAwaiter().GetResult();
            var sessions = new SessionClient(native);
            var first = sessions.SaveWindowAsync(context, null, new SessionWindowInput
            {
                WindowId = "window-a",
                Ordinal = 0,
                ActivePosition = 1,
                BoundsWidth = 1280,
                BoundsHeight = 720,
                Tabs =
                [
                    new SessionTabInput { Url = "https://a1.example", Title = "A1" },
                    new SessionTabInput { Url = "https://a2.example", Title = "A2", Pinned = true },
                    new SessionTabInput { Url = "https://a3.example", Title = "A3" }
                ]
            }).GetAwaiter().GetResult();
            sessionId = first.Session.Id;
            firstActive = first.Windows.Single().ActiveTabId!.Value;
            Assert(first.Windows.Count == 1 && first.Windows[0].Tabs.Count == 3, "A saved");

            var second = sessions.SaveWindowAsync(context, sessionId, new SessionWindowInput
            {
                WindowId = "window-b",
                Ordinal = 1,
                ActivePosition = 0,
                BoundsWidth = 1024,
                BoundsHeight = 768,
                Tabs =
                [
                    new SessionTabInput { Url = "https://b1.example", Title = "B1" },
                    new SessionTabInput { Url = "https://b2.example", Title = "B2" }
                ]
            }).GetAwaiter().GetResult();
            Assert(second.Session.Id == sessionId, "same session");
            Assert(second.Windows.Count == 2, "two windows");
            Assert(second.Windows.Single(window => window.WindowId == "window-a").Tabs.Count == 3, "A tabs");
            Assert(second.Windows.Single(window => window.WindowId == "window-b").Tabs.Count == 2, "B tabs");

            var latest = sessions.LatestWindowsAsync(context).GetAwaiter().GetResult()!;
            Assert(latest.Session.Id == sessionId, "latest session");
            Assert(latest.Windows.Select(window => window.WindowId).SequenceEqual(["window-a", "window-b"]), "window order");
            Assert(latest.Windows[0].ActiveTabId == firstActive, "A active");
            Assert(latest.Windows[0].BoundsWidth == 1280, "A bounds");
        }

        using (var reopened = AmareNative.Create(root.Path))
        {
            var context = new ProfileClient(reopened).InitializeAsync().GetAwaiter().GetResult();
            var sessions = new SessionClient(reopened);
            var latest = sessions.LatestWindowsAsync(context).GetAwaiter().GetResult()!;
            Assert(latest.Windows.Count == 2, "reopened two windows");
            Assert(latest.Windows.Sum(window => window.Tabs.Count) == 5, "reopened tabs");
            Assert(latest.Windows[1].Tabs.Select(tab => tab.Url).SequenceEqual(["https://b1.example", "https://b2.example"]), "B order");
        }
        return $"session={sessionId} windows=2 tabs=5";
    }

    private static string NativeReversedOrder()
    {
        using var root = new TempRoot();
        using var native = AmareNative.Create(root.Path);
        var context = new ProfileClient(native).InitializeAsync().GetAwaiter().GetResult();
        var sessions = new SessionClient(native);
        var b = sessions.SaveWindowAsync(context, null, Window("b", 1, 2)).GetAwaiter().GetResult();
        var a = sessions.SaveWindowAsync(context, b.Session.Id, Window("a", 0, 3)).GetAwaiter().GetResult();
        Assert(a.Session.Id == b.Session.Id, "one session");
        Assert(a.Windows.Count == 2, "two windows");
        Assert(a.Windows[0].WindowId == "a" && a.Windows[0].Tabs.Count == 3, "A first");
        Assert(a.Windows[1].WindowId == "b" && a.Windows[1].Tabs.Count == 2, "B second");
        return "b-then-a both preserved";
    }

    private static string NativeLegacySingleWindow()
    {
        using var root = new TempRoot();
        using var native = AmareNative.Create(root.Path);
        var context = new ProfileClient(native).InitializeAsync().GetAwaiter().GetResult();
        var sessions = new SessionClient(native);
        var legacy = sessions.SaveAsync(context, null,
            [
                new SessionTabInput { Url = "https://legacy1.example", Title = "Legacy 1" },
                new SessionTabInput { Url = "https://legacy2.example", Title = "Legacy 2" }
            ], 1).GetAwaiter().GetResult();
        var latest = sessions.LatestWindowsAsync(context).GetAwaiter().GetResult()!;
        Assert(latest.Session.Id == legacy.Session.Id, "legacy session id");
        Assert(latest.Windows.Count == 1 && latest.Windows[0].WindowId == "main", "main window");
        Assert(latest.Windows[0].Tabs.Count == 2, "legacy tabs");
        Assert(latest.Windows[0].ActiveTabId == legacy.Session.ActiveTabId, "legacy active");
        var old = sessions.LatestAsync(context).GetAwaiter().GetResult()!;
        Assert(old.Tabs.Count == 2, "legacy latest still works");
        return "legacy main window preserved";
    }

    private static string NativeConcurrentWriters()
    {
        using var root = new TempRoot();
        long sessionId;
        using (var native = AmareNative.Create(root.Path))
        {
            var context = new ProfileClient(native).InitializeAsync().GetAwaiter().GetResult();
            sessionId = new SessionClient(native)
                .SaveWindowAsync(context, null, Window("seed", 0, 1))
                .GetAwaiter().GetResult().Session.Id;
        }

        using var aNative = AmareNative.Create(root.Path);
        using var bNative = AmareNative.Create(root.Path);
        var aContext = new ProfileClient(aNative).InitializeAsync().GetAwaiter().GetResult();
        var bContext = new ProfileClient(bNative).InitializeAsync().GetAwaiter().GetResult();
        var a = Task.Run(() => new SessionClient(aNative).SaveWindowAsync(aContext, sessionId, Window("a", 1, 3)));
        var b = Task.Run(() => new SessionClient(bNative).SaveWindowAsync(bContext, sessionId, Window("b", 2, 2)));
        Task.WaitAll(a, b);
        var latest = new SessionClient(aNative).LatestWindowsAsync(aContext).GetAwaiter().GetResult()!;
        Assert(latest.Windows.Count == 3, "seed + a + b");
        Assert(latest.Windows.Single(window => window.WindowId == "a").Tabs.Count == 3, "a tabs");
        Assert(latest.Windows.Single(window => window.WindowId == "b").Tabs.Count == 2, "b tabs");
        return "concurrent writers preserved both windows";
    }

    private static SessionWindowInput Window(string id, long ordinal, int tabCount) => new()
    {
        WindowId = id,
        Ordinal = ordinal,
        ActivePosition = 0,
        Tabs = Enumerable.Range(1, tabCount)
            .Select(index => new SessionTabInput
            {
                Url = $"https://{id}{index}.example",
                Title = $"{id.ToUpperInvariant()}{index}"
            })
            .ToArray()
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"amare-session-window-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
