using System.Text.Json;
using Amare.Windows.Core;

namespace Amare.Interop.Tests;

internal static class Phase2Tests
{
    public static void Register()
    {
        Program.Run("address_bar_url_vs_search", () =>
        {
            var url = AddressBarResolver.Resolve("example.com/path", "duckduckgo");
            Assert(url.Kind == AddressInputKind.Url && url.Target == "https://example.com/path", url.Target);

            var local = AddressBarResolver.Resolve("localhost:5173", "duckduckgo");
            Assert(local.Kind == AddressInputKind.Url && local.Target.StartsWith("http://localhost", StringComparison.Ordinal), local.Target);

            var search = AddressBarResolver.Resolve("how to test a browser", "duckduckgo");
            Assert(search.Kind == AddressInputKind.Search, search.Kind.ToString());
            Assert(search.Target.StartsWith("https://duckduckgo.com/?q=", StringComparison.Ordinal), search.Target);

            var empty = AddressBarResolver.Resolve("   ", "duckduckgo");
            Assert(empty.Kind == AddressInputKind.Empty, empty.Kind.ToString());
            return "url/search/empty resolution";
        });

        Program.Run("search_engine_catalog_encodes_query", () =>
        {
            var url = SearchEngineCatalog.BuildSearchUrl("google", "한글 & test");
            Assert(url.StartsWith("https://www.google.com/search?q=", StringComparison.Ordinal), url);
            Assert(!url.Contains(" ", StringComparison.Ordinal), url);
            Assert(SearchEngineCatalog.Resolve("missing").Id == "duckduckgo", "fallback engine");
            return url;
        });

        Program.Run("navigation_policy", () =>
        {
            Assert(NavigationPolicy.Decide("https://example.com") == NavigationDecision.Allow, "https");
            Assert(NavigationPolicy.Decide("http://example.com") == NavigationDecision.Allow, "http");
            Assert(NavigationPolicy.Decide("about:blank") == NavigationDecision.Allow, "about");
            Assert(NavigationPolicy.Decide("amare://newtab") == NavigationDecision.Allow, "amare");
            Assert(NavigationPolicy.Decide("mailto:test@example.com") == NavigationDecision.External, "mailto");
            Assert(NavigationPolicy.Decide("javascript:alert(1)") == NavigationDecision.Block, "javascript");
            Assert(NavigationPolicy.Decide("file:///etc/passwd") == NavigationDecision.Block, "file");
            Assert(NavigationPolicy.Decide("not a url") == NavigationDecision.Block, "invalid");
            Assert(NavigationPolicy.CanOpenInNewTab("https://example.com"), "new tab https");
            Assert(!NavigationPolicy.CanOpenInNewTab("mailto:test@example.com"), "new tab mailto");
            return "scheme decisions";
        });

        Program.Run("profile_webview_path_is_confined", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "amare-test-appdata");
            var path = ProfilePathPolicy.GetWebViewDataFolder(root, "personal");
            Assert(path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal), path);
            foreach (var bad in new[] { "../escape", "a/b", ".", "..", "space id", "" })
            {
                Assert(!ProfilePathPolicy.IsSafeProfileId(bad), bad);
            }
            return path;
        });

        Program.Run("tab_manager_lifecycle", () =>
        {
            var manager = new TabManager();
            var first = manager.AddTab();
            var second = manager.AddTab("https://example.com", "Example");
            Assert(manager.Tabs.Count == 2, "tab count");
            Assert(manager.ActiveTab?.Id == second.Id, "active second");
            Assert(manager.Activate(first.Id), "activate first");
            Assert(manager.Move(second.Id, 0), "move");
            Assert(manager.Tabs[0].Id == second.Id, "moved order");
            Assert(!manager.Close(second.Id), "close one remains");
            Assert(manager.Close(first.Id), "last close creates replacement");
            Assert(manager.Tabs.Count == 1 && manager.ActiveTab is not null, "replacement tab");
            var snapshot = manager.Snapshot();
            Assert(snapshot.Tabs.Count == 1 && snapshot.ActiveIndex == 0, "snapshot");
            return "add/activate/move/close/snapshot";
        });

        Program.Run("download_policy_sanitizes_and_allocates", () =>
        {
            Assert(DownloadPolicy.SanitizeFileName("../../evil.txt") == ".._.._evil.txt", "path components");
            Assert(DownloadPolicy.SanitizeFileName("CON") == "_CON", "reserved name");
            var root = Path.Combine(Path.GetTempPath(), "amare-downloads");
            var path = DownloadPolicy.AllocatePath(root, "file.txt", candidate => candidate.EndsWith("file.txt", StringComparison.Ordinal));
            Assert(path.EndsWith("file (1).txt", StringComparison.Ordinal), path);
            Assert(path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal), path);
            return path;
        });

        Program.Run("new_tab_page_html_escapes_and_has_no_external_server", () =>
        {
            var html = NewTabPageBuilder.Build(
                [new HistoryEntryDto { Url = "https://example.com/?a=1&b=2", Title = "A <script>alert(1)</script>" }],
                "DuckDuckGo",
                "nonce-test");
            Assert(html.Contains("&lt;script&gt;", StringComparison.Ordinal), "title escaped");
            Assert(!html.Contains("<script>alert(1)</script>", StringComparison.Ordinal), "raw script absent");
            Assert(html.Contains("nonce-test", StringComparison.Ordinal), "nonce present");
            Assert(!html.Contains("http://amare", StringComparison.Ordinal), "no amare server");
            return $"html bytes={html.Length}";
        });

        Program.Run("clients_use_fake_bridge", () =>
        {
            var bridge = new FakeBridge();
            var profiles = new ProfileClient(bridge);
            var context = profiles.InitializeAsync().GetAwaiter().GetResult();
            Assert(context.ProfileId == "default" && context.Generation == 7, "context parse");

            var settings = new SettingsClient(bridge);
            var changes = settings.SetAsync(context, "theme", SettingValueDto.Text("dark")).GetAwaiter().GetResult();
            Assert(changes.ChangedCount == 1 && changes.Changes[0].New.AsText() == "dark", "settings parse");

            var search = new SearchClient(bridge);
            var page = search.QueryAsync(context, "needle", [SearchKind.LibraryItem], 5, 0).GetAwaiter().GetResult();
            Assert(page.Total == 1 && page.Items[0].SearchKind == SearchKind.LibraryItem, "search parse");
            Assert(bridge.Methods.Contains("search.query"), "search called");
            return string.Join(",", bridge.Methods);
        });

        Program.Run("native_services_bookmarks_and_history", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"amare-data-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                using var native = AmareNative.Create(root);
                var profiles = new ProfileClient(native);
                var context = profiles.InitializeAsync().GetAwaiter().GetResult();
                var bookmarks = new BookmarkClient(native);
                var created = bookmarks.CreateAsync(context, "AMARE", "https://example.com/amare").GetAwaiter().GetResult();
                if (created.Id <= 0 || created.Title != "AMARE") throw new InvalidOperationException("bookmark create");
                var page = bookmarks.ListAsync(context, "AMARE").GetAwaiter().GetResult();
                if (page.Items.Count != 1 || page.Items[0].Url != "https://example.com/amare")
                    throw new InvalidOperationException("bookmark list");
                if (!bookmarks.DeleteAsync(context, created.Id).GetAwaiter().GetResult())
                    throw new InvalidOperationException("bookmark delete");

                var history = new HistoryClient(native);
                history.RecordAsync(context, "https://example.com/amare", "AMARE history").GetAwaiter().GetResult();
                var historyPage = history.ListAsync(context, "AMARE").GetAwaiter().GetResult();
                if (historyPage.Items.Count != 1 || historyPage.Items[0].Url != "https://example.com/amare")
                    throw new InvalidOperationException("history list");
                if (history.ClearAsync(context).GetAwaiter().GetResult() != 1)
                    throw new InvalidOperationException("history clear");
                return "bookmark/history round-trip";
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        });

        Program.Run("native_services_profile_settings_session_search", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"amare-native-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                using var native = AmareNative.Create(root);
                var profiles = new ProfileClient(native);
                var context = profiles.InitializeAsync().GetAwaiter().GetResult();
                var settings = new SettingsClient(native);
                settings.SetAsync(context, "theme", SettingValueDto.Text("dark")).GetAwaiter().GetResult();
                var theme = settings.GetAsync(context, "theme").GetAwaiter().GetResult();
                Assert(theme.Value.AsText() == "dark", "theme round-trip");

                var session = new SessionClient(native);
                var saved = session.SaveAsync(context, null,
                    [
                        new SessionTabInput { Url = "https://example.com", Title = "Example" },
                        new SessionTabInput { Url = "https://example.org", Title = "Org", Pinned = true }
                    ],
                    1).GetAwaiter().GetResult();
                Assert(saved.Tabs.Count == 2 && saved.Session.ActiveTabId is not null, "session save");
                var latest = session.LatestAsync(context).GetAwaiter().GetResult();
                Assert(latest is not null && latest.Tabs.Count == 2, "session latest");
                Assert(latest!.Tabs[1].Pinned, "pinned state");

                var search = new SearchClient(native);
                var page = search.QueryAsync(context, "needle").GetAwaiter().GetResult();
                Assert(page.Total == 0, "empty search");

                profiles.CreateAsync("work", "Work").GetAwaiter().GetResult();
                var switched = profiles.SwitchAsync("work").GetAwaiter().GetResult();
                Assert(switched.Generation > context.Generation, "generation advanced");
                try
                {
                    settings.GetAsync(context, "theme").GetAwaiter().GetResult();
                    throw new InvalidOperationException("stale context was accepted");
                }
                catch (NativeException exception)
                {
                    Assert(exception.Error == NativeError.ProfileSwitched, $"unexpected error: {exception.Error}");
                }
                return $"profile={context.ProfileId} session={saved.Session.Id}";
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        });
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeBridge : INativeBridge
    {
        public List<string> Methods { get; } = [];

        public Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            Methods.Add(method);
            var json = method switch
            {
                "profile.initialize" => """{"profile_id":"default","generation":7}""",
                "settings.set" => """{"changes":[{"key":"theme","group":"appearance","old":null,"new":{"type":"text","value":"dark"},"source":"user"}],"changed_count":1}""",
                "search.query" => """{"profile_id":"default","query":"needle","items":[{"kind":"library_item","id":3,"title":"Needle","snippet":null,"updated_at_ms":1}],"total":1,"limit":5,"offset":0,"sort":"relevance"}""",
                _ => "null"
            };
            using var document = JsonDocument.Parse(json);
            return Task.FromResult(document.RootElement.Clone());
        }

        public void Dispose()
        {
        }
    }
}
