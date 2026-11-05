using System.Text.Json;
using Amare.Windows.Core;

namespace Amare.Interop.Tests;

internal static class Phase21Tests
{
    public static void Register()
    {
        RegisterProfileCleanup();
        RegisterSessionPolicies();
        RegisterClosedTabsAndShortcuts();
        RegisterTabDiagnostics();
        RegisterDownloadAndNewTab();
        RegisterAppLog();
    }

    private static void RegisterProfileCleanup()
    {
        Program.Run("cleanup_removes_profile_folder", () =>
        {
            using var root = new TempRoot();
            var cleanup = new ProfileDataCleanup(root.Path);
            var folder = ProfilePathPolicy.GetWebViewDataFolder(root.Path, "alpha");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "cookie"), "x");
            var result = cleanup.CleanupAsync("alpha").GetAwaiter().GetResult();
            Assert(result.Removed && !result.Pending, "cleanup result");
            Assert(!Directory.Exists(folder), "folder removed");
            Assert(cleanup.ReadPendingAsync().GetAwaiter().GetResult().Count == 0, "pending empty");
            return "folder removed";
        });

        Program.Run("cleanup_missing_folder_is_success", () =>
        {
            using var root = new TempRoot();
            var cleanup = new ProfileDataCleanup(root.Path);
            var result = cleanup.CleanupAsync("missing").GetAwaiter().GetResult();
            Assert(result.Removed && !result.Pending && result.Attempts == 1, "missing folder");
            return "idempotent";
        });

        Program.Run("cleanup_retries_then_succeeds", () =>
        {
            using var root = new TempRoot();
            var cleanup = new ProfileDataCleanup(root.Path);
            var folder = ProfilePathPolicy.GetWebViewDataFolder(root.Path, "retry");
            Directory.CreateDirectory(folder);
            var calls = 0;
            var result = cleanup.CleanupAsync("retry", 3,
                _ =>
                {
                    calls++;
                    if (calls == 1) throw new IOException("locked");
                    Directory.Delete(folder, true);
                    return Task.CompletedTask;
                },
                _ => TimeSpan.Zero).GetAwaiter().GetResult();
            Assert(result.Removed && result.Attempts == 2 && calls == 2, $"attempts={result.Attempts}");
            return $"attempts={result.Attempts}";
        });

        Program.Run("cleanup_failure_persists_pending_and_resume_clears", () =>
        {
            using var root = new TempRoot();
            var cleanup = new ProfileDataCleanup(root.Path);
            var folder = ProfilePathPolicy.GetWebViewDataFolder(root.Path, "locked");
            Directory.CreateDirectory(folder);
            var failed = cleanup.CleanupAsync("locked", 2,
                _ => throw new IOException("locked"),
                _ => TimeSpan.Zero).GetAwaiter().GetResult();
            Assert(!failed.Removed && failed.Pending, "pending result");
            Assert(cleanup.ReadPendingAsync().GetAwaiter().GetResult().SequenceEqual(["locked"]), "pending list");
            Assert(Directory.Exists(folder), "folder preserved");
            var resumed = cleanup.ResumePendingAsync(2, _ =>
            {
                Directory.Delete(folder, true);
                return Task.CompletedTask;
            }, _ => TimeSpan.Zero).GetAwaiter().GetResult();
            Assert(resumed.Count == 1 && resumed[0].Removed, "resume removed");
            Assert(cleanup.ReadPendingAsync().GetAwaiter().GetResult().Count == 0, "pending cleared");
            return "pending resume";
        });

        Program.Run("cleanup_one_profile_does_not_touch_other", () =>
        {
            using var root = new TempRoot();
            var cleanup = new ProfileDataCleanup(root.Path);
            var a = ProfilePathPolicy.GetWebViewDataFolder(root.Path, "a");
            var b = ProfilePathPolicy.GetWebViewDataFolder(root.Path, "b");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            cleanup.CleanupAsync("a").GetAwaiter().GetResult();
            Assert(!Directory.Exists(a), "a removed");
            Assert(Directory.Exists(b), "b preserved");
            return "profile-scoped cleanup";
        });

        Program.Run("cleanup_rejects_unsafe_profile_id", () =>
        {
            using var root = new TempRoot();
            var cleanup = new ProfileDataCleanup(root.Path);
            try
            {
                cleanup.CleanupAsync("../escape").GetAwaiter().GetResult();
                throw new InvalidOperationException("unsafe id accepted");
            }
            catch (ArgumentException)
            {
                return "rejected";
            }
        });

        Program.Run("cleanup_corrupt_pending_state_is_ignored", () =>
        {
            using var root = new TempRoot();
            var cleanup = new ProfileDataCleanup(root.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(cleanup.StatePath)!);
            File.WriteAllText(cleanup.StatePath, "{not json");
            var pending = cleanup.ReadPendingAsync().GetAwaiter().GetResult();
            Assert(pending.Count == 0, "corrupt state ignored");
            return "corrupt state";
        });

        Program.Run("cleanup_resume_keeps_only_still_failing_profiles", () =>
        {
            using var root = new TempRoot();
            var cleanup = new ProfileDataCleanup(root.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(cleanup.StatePath)!);
            File.WriteAllText(cleanup.StatePath, JsonSerializer.Serialize(new[] { "a", "b" }));
            var a = ProfilePathPolicy.GetWebViewDataFolder(root.Path, "a");
            var b = ProfilePathPolicy.GetWebViewDataFolder(root.Path, "b");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            var results = cleanup.ResumePendingAsync(1, folder =>
            {
                if (folder == a) throw new IOException("still locked");
                Directory.Delete(folder, true);
                return Task.CompletedTask;
            }, _ => TimeSpan.Zero).GetAwaiter().GetResult();
            Assert(results.Count == 2, "two results");
            Assert(results.Single(result => result.ProfileId == "a").Pending, "a pending");
            Assert(results.Single(result => result.ProfileId == "b").Removed, "b removed");
            Assert(cleanup.ReadPendingAsync().GetAwaiter().GetResult().SequenceEqual(["a"]), "only a pending");
            return "mixed resume";
        });

        Program.Run("cleanup_pending_state_is_sorted_and_deduplicated", () =>
        {
            using var root = new TempRoot();
            var cleanup = new ProfileDataCleanup(root.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(cleanup.StatePath)!);
            File.WriteAllText(cleanup.StatePath, JsonSerializer.Serialize(new[] { "b", "a", "b", "../bad" }));
            var pending = cleanup.ReadPendingAsync().GetAwaiter().GetResult();
            Assert(pending.SequenceEqual(["a", "b"]), string.Join(",", pending));
            return string.Join(",", pending);
        });
    }

    private static void RegisterSessionPolicies()
    {
        Program.Run("restore_null_or_empty_session", () =>
        {
            Assert(SessionRestorePlanner.Create(null) is null, "null");
            var empty = new SessionStateDto { Session = new SessionDto { Id = 1 }, Tabs = [] };
            Assert(SessionRestorePlanner.Create(empty) is null, "empty");
            return "no session";
        });

        Program.Run("restore_orders_tabs_and_selects_active", () =>
        {
            var state = Session("""[{"id":2,"position":2,"url":"https://2"},{"id":1,"position":1,"url":"https://1"},{"id":3,"position":1,"url":"https://3"}]""", 1);
            var plan = SessionRestorePlanner.Create(state)!;
            Assert(plan.Tabs.Select(tab => tab.Url).SequenceEqual(["https://1", "https://3", "https://2"]), "order");
            Assert(plan.ActiveIndex == 0, $"active={plan.ActiveIndex}");
            return string.Join(",", plan.Tabs.Select(tab => tab.Url));
        });

        Program.Run("restore_missing_active_defaults_to_first", () =>
        {
            var state = Session("""[{"id":2,"position":1}]""", 99);
            var plan = SessionRestorePlanner.Create(state)!;
            Assert(plan.ActiveIndex == 0, "default active");
            return "default";
        });

        Program.Run("restore_blank_url_and_title_fallback", () =>
        {
            var state = Session("""[{"id":1,"position":1,"url":"","title":""}]""", 1);
            var plan = SessionRestorePlanner.Create(state)!;
            Assert(plan.Tabs[0].Url == "amare://newtab" && plan.Tabs[0].Title == "New Tab", "fallback");
            return "fallback";
        });

        Program.Run("restore_preserves_pinned_and_does_not_mutate_input", () =>
        {
            var state = Session("""[{"id":1,"position":1,"url":"https://a","title":"A","pinned":true}]""", 1);
            var originalUrl = state.Tabs[0].Url;
            var plan = SessionRestorePlanner.Create(state)!;
            Assert(plan.Tabs[0].Pinned, "pinned preserved");
            Assert(state.Tabs[0].Url == originalUrl, "input unchanged");
            return "pinned/input";
        });

        Program.Run("restore_caps_tab_count", () =>
        {
            var state = Session("""[{"id":1,"position":1},{"id":2,"position":2},{"id":3,"position":3}]""", 1);
            var plan = SessionRestorePlanner.Create(state, 2)!;
            Assert(plan.Tabs.Count == 2, "cap");
            return "cap";
        });

        Program.Run("restore_rejects_invalid_cap", () =>
        {
            try
            {
                SessionRestorePlanner.Create(Session("""[{"id":1,"position":1}]""", 1), 0);
                throw new InvalidOperationException("invalid cap accepted");
            }
            catch (ArgumentOutOfRangeException)
            {
                return "rejected";
            }
        });

        Program.Run("session_retry_policy", () =>
        {
            Assert(SessionSaveRetryPolicy.ShouldRetry(1, new IOException()), "transient retry");
            Assert(!SessionSaveRetryPolicy.ShouldRetry(3, new IOException()), "max attempts");
            Assert(!SessionSaveRetryPolicy.ShouldRetry(1, new OperationCanceledException()), "cancel no retry");
            Assert(!SessionSaveRetryPolicy.ShouldRetry(1, new ObjectDisposedException("native")), "disposed no retry");
            Assert(SessionSaveRetryPolicy.DelayFor(1) == TimeSpan.FromMilliseconds(100), "delay 1");
            Assert(SessionSaveRetryPolicy.DelayFor(2) == TimeSpan.FromMilliseconds(200), "delay 2");
            Assert(SessionSaveRetryPolicy.DelayFor(99) == TimeSpan.FromMilliseconds(1000), "delay cap");
            return "retry policy";
        });
    }

    private static void RegisterClosedTabsAndShortcuts()
    {
        Program.Run("closed_tab_history_is_lifo", () =>
        {
            var history = new ClosedTabHistory(3);
            history.Push(new TabSnapshot("https://a", "A", false));
            history.Push(new TabSnapshot("https://b", "B", true));
            Assert(history.Count == 2, "count");
            Assert(history.Pop()?.Title == "B", "last");
            Assert(history.Pop()?.Title == "A", "first");
            Assert(history.Pop() is null, "empty");
            return "lifo";
        });

        Program.Run("closed_tab_history_evicts_oldest", () =>
        {
            var history = new ClosedTabHistory(2);
            history.Push(new TabSnapshot("https://a", "A", false));
            history.Push(new TabSnapshot("https://b", "B", false));
            history.Push(new TabSnapshot("https://c", "C", false));
            Assert(history.Snapshot().Select(item => item.Title).SequenceEqual(["C", "B"]), "eviction");
            return "eviction";
        });

        Program.Run("closed_tab_history_clear_and_capacity", () =>
        {
            var history = new ClosedTabHistory(2);
            history.Push(new TabSnapshot("https://a", "A", false));
            history.Clear();
            Assert(history.Count == 0, "clear");
            try
            {
                _ = new ClosedTabHistory(0);
                throw new InvalidOperationException("zero capacity accepted");
            }
            catch (ArgumentOutOfRangeException)
            {
                return "validated";
            }
        });

        Program.Run("shortcut_control_combinations", () =>
        {
            Assert(BrowserShortcutPolicy.Resolve(true, false, false, "l") == BrowserCommand.FocusAddressBar, "ctrl+l");
            Assert(BrowserShortcutPolicy.Resolve(true, false, false, "T") == BrowserCommand.NewTab, "ctrl+t");
            Assert(BrowserShortcutPolicy.Resolve(true, false, false, "w") == BrowserCommand.CloseTab, "ctrl+w");
            Assert(BrowserShortcutPolicy.Resolve(true, true, false, "T") == BrowserCommand.RestoreClosedTab, "ctrl+shift+t");
            Assert(BrowserShortcutPolicy.Resolve(true, false, false, "r") == BrowserCommand.Reload, "ctrl+r");
            Assert(BrowserShortcutPolicy.Resolve(true, false, false, "f") == BrowserCommand.Find, "ctrl+f");
            Assert(BrowserShortcutPolicy.Resolve(true, false, false, "Tab") == BrowserCommand.NextTab, "ctrl+tab");
            Assert(BrowserShortcutPolicy.Resolve(true, true, false, "Tab") == BrowserCommand.PreviousTab, "ctrl+shift+tab");
            return "control shortcuts";
        });

        Program.Run("shortcut_is_case_insensitive_and_shift_only_noop", () =>
        {
            Assert(BrowserShortcutPolicy.Resolve(true, false, false, "L") == BrowserCommand.FocusAddressBar, "uppercase");
            Assert(BrowserShortcutPolicy.Resolve(false, true, false, "T") is null, "shift only");
            return "case/noop";
        });

        Program.Run("shortcut_alt_navigation_and_conflicts", () =>
        {
            Assert(BrowserShortcutPolicy.Resolve(false, false, true, "Left") == BrowserCommand.Back, "alt+left");
            Assert(BrowserShortcutPolicy.Resolve(false, false, true, "Right") == BrowserCommand.Forward, "alt+right");
            Assert(BrowserShortcutPolicy.Resolve(true, false, true, "L") is null, "ctrl+alt conflict");
            Assert(BrowserShortcutPolicy.Resolve(false, false, false, "L") is null, "plain key");
            Assert(BrowserShortcutPolicy.Resolve(true, true, false, "R") is null, "ctrl+shift+r unsupported");
            return "alt/conflicts";
        });
    }

    private static void RegisterTabDiagnostics()
    {
        Program.Run("tab_diagnostics_counts_lifecycle", () =>
        {
            var manager = new TabManager();
            var first = manager.AddTab();
            var second = manager.AddTab("https://example.com");
            var diagnostics = manager.Diagnostics();
            Assert(diagnostics.Created == 2 && diagnostics.Closed == 0 && diagnostics.Live == 2, "created");
            manager.Close(second.Id);
            diagnostics = manager.Diagnostics();
            Assert(diagnostics.Created == 2 && diagnostics.Closed == 1 && diagnostics.Live == 1, "closed");
            Assert(manager.Close(first.Id), "last replacement");
            diagnostics = manager.Diagnostics();
            Assert(diagnostics.Created == 3 && diagnostics.Closed == 2 && diagnostics.Live == 1, "replacement");
            return $"created={diagnostics.Created} closed={diagnostics.Closed} live={diagnostics.Live}";
        });

        Program.Run("tab_close_non_active_keeps_active", () =>
        {
            var manager = new TabManager();
            var first = manager.AddTab();
            var second = manager.AddTab("https://example.com");
            var third = manager.AddTab("https://example.org");
            Assert(manager.ActiveTab?.Id == third.Id, "third active");
            manager.Close(second.Id);
            Assert(manager.ActiveTab?.Id == third.Id, "active preserved");
            Assert(manager.Tabs.Count == 2, "two remain");
            return "non-active close";
        });

        Program.Run("tab_close_active_selects_neighbor", () =>
        {
            var manager = new TabManager();
            var first = manager.AddTab();
            var second = manager.AddTab("https://example.com");
            var third = manager.AddTab("https://example.org");
            manager.Activate(second.Id);
            manager.Close(second.Id);
            Assert(manager.ActiveTab?.Id == third.Id, "neighbor selected");
            Assert(!manager.Activate(Guid.NewGuid()), "unknown tab rejected");
            return "active close";
        });

        Program.Run("tab_clear_updates_closed_count", () =>
        {
            var manager = new TabManager();
            manager.AddTab();
            manager.AddTab();
            manager.Clear();
            var diagnostics = manager.Diagnostics();
            Assert(diagnostics.Live == 0 && diagnostics.Closed == 2, "clear");
            return "clear";
        });

        Program.Run("tab_pin_state_is_preserved_in_snapshot", () =>
        {
            var manager = new TabManager();
            var tab = manager.AddTab("https://example.com", "Example");
            manager.SetPinned(tab.Id, true);
            var snapshot = manager.Snapshot();
            Assert(snapshot.Tabs[0].Pinned, "pinned");
            return "pinned";
        });
    }

    private static void RegisterAppLog()
    {
        Program.Run("app_log_creates_and_appends", () =>
        {
            using var root = new TempRoot();
            AppLog.Initialize(root.Path);
            AppLog.Info("first");
            AppLog.Warn("second");
            AppLog.Error("third", new InvalidOperationException("failure"));
            Assert(AppLog.LogPath is not null && File.Exists(AppLog.LogPath), "log file");
            var text = File.ReadAllText(AppLog.LogPath!);
            Assert(text.Contains("first") && text.Contains("second") && text.Contains("third"), "entries");
            Assert(text.Contains("[WARN]") && text.Contains("[ERROR]"), "levels");
            return AppLog.LogPath!;
        });

        Program.Run("app_log_rejects_empty_root", () =>
        {
            try
            {
                AppLog.Initialize(" ");
                throw new InvalidOperationException("empty root accepted");
            }
            catch (ArgumentException)
            {
                return "validated";
            }
        });
    }

    private static void RegisterDownloadAndNewTab()
    {
        Program.Run("download_sanitizes_windows_separators", () =>
        {
            Assert(DownloadPolicy.SanitizeFileName("a:b\\c.txt") == "a_b_c.txt", "separators");
            Assert(DownloadPolicy.SanitizeFileName("  ...  ") == "download", "empty");
            return "sanitized";
        });

        Program.Run("download_allocates_multiple_uniques", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "amare-downloads-phase21");
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(root, "file.txt"),
                Path.Combine(root, "file (1).txt")
            };
            var path = DownloadPolicy.AllocatePath(root, "file.txt", candidate => taken.Contains(candidate));
            Assert(path.EndsWith("file (2).txt", StringComparison.Ordinal), path);
            return path;
        });

        Program.Run("new_tab_page_empty_state_and_invalid_sites", () =>
        {
            var html = NewTabPageBuilder.Build(
                [new HistoryEntryDto { Url = "javascript:alert(1)", Title = "bad" }],
                "DuckDuckGo",
                "nonce");
            Assert(html.Contains("will appear here", StringComparison.Ordinal), "empty state");
            Assert(!html.Contains("javascript:alert", StringComparison.Ordinal), "invalid site filtered");
            return "empty state";
        });
    }

    private static SessionStateDto Session(string tabsJson, long activeTabId)
    {
        var tabs = JsonSerializer.Deserialize<List<SessionTabDto>>(
            tabsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return new SessionStateDto
        {
            Session = new SessionDto { Id = 1, ActiveTabId = activeTabId },
            Tabs = tabs
        };
    }

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
                $"amare-phase21-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
