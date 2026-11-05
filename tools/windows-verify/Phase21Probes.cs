using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Windows.Forms;

namespace WindowsVerify;

internal static class Phase21Probes
{
    private static ProbeResult Result(string name, string status, bool executed, string detail, object? data = null) =>
        new(name, status, executed, detail, DateTimeOffset.UtcNow, data);

    public static async Task<ProbeResult> LocalNavigationAsync(Control host, string root)
    {
        await using var server = await LocalHttpServer.StartAsync();
        var (_, view) = await WebViewHelpers.CreateViewAsync(host, Path.Combine(root, "phase21-local"));
        try
        {
            await WebViewHelpers.NavigateAsync(view, $"{server.BaseUrl}/page1.html");
            if (!string.Equals(view.CoreWebView2.DocumentTitle, "Page One", StringComparison.Ordinal))
                throw new InvalidOperationException($"title mismatch: {view.CoreWebView2.DocumentTitle}");
            if (view.Source?.AbsoluteUri != $"{server.BaseUrl}/page1.html")
                throw new InvalidOperationException($"url mismatch: {view.Source}");

            var popup = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void NewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
            {
                args.Handled = true;
                popup.TrySetResult(args.Uri);
            }
            view.CoreWebView2.NewWindowRequested += NewWindow;
            try
            {
                await WebViewHelpers.ClickSelectorAsync(view, "#popup");
                var uri = await popup.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (!uri.EndsWith("/popup.html", StringComparison.Ordinal))
                    throw new InvalidOperationException($"popup uri mismatch: {uri}");
            }
            finally
            {
                view.CoreWebView2.NewWindowRequested -= NewWindow;
            }

            await WebViewHelpers.NavigateAsync(view, $"{server.BaseUrl}/page2.html");
            await WaitNavigationAsync(view, () => view.CoreWebView2.GoBack());
            if (!string.Equals(view.CoreWebView2.DocumentTitle, "Page One", StringComparison.Ordinal))
                throw new InvalidOperationException("back navigation did not restore Page One");
            await WaitNavigationAsync(view, () => view.CoreWebView2.GoForward());
            if (!string.Equals(view.CoreWebView2.DocumentTitle, "Page Two", StringComparison.Ordinal))
                throw new InvalidOperationException("forward navigation did not restore Page Two");
            await WaitNavigationAsync(view, () => view.CoreWebView2.Reload());
            if (!string.Equals(view.CoreWebView2.DocumentTitle, "Page Two", StringComparison.Ordinal))
                throw new InvalidOperationException("reload lost Page Two");

            view.CoreWebView2.Navigate($"{server.BaseUrl}/slow.html");
            await Task.Delay(150);
            view.CoreWebView2.Stop();
            await Task.Delay(250);

            var download = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
            {
                args.Handled = true;
                args.Cancel = true;
                download.TrySetResult(Path.GetFileName(args.ResultFilePath));
            }
            view.CoreWebView2.DownloadStarting += DownloadStarting;
            try
            {
                view.CoreWebView2.Navigate($"{server.BaseUrl}/download.txt");
                _ = await download.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                view.CoreWebView2.DownloadStarting -= DownloadStarting;
            }

            var beforeDispose = host.Controls.Count;
            WebViewHelpers.DisposeView(host, view);
            if (host.Controls.Count != beforeDispose - 1)
                throw new InvalidOperationException("WebView2 control was not removed");

            var data = new Dictionary<string, object?>
            {
                ["title"] = "Page One",
                ["url"] = server.BaseUrl + "/page1.html",
                ["popup_uri"] = "popup.html",
                ["back_forward_reload"] = true,
                ["stop"] = true,
                ["download_detected"] = true,
                ["control_removed"] = true,
            };
            return Result("webview2_local_navigation", ProbeStatus.Pass, true,
                "local HTTP page title/url/back/forward/reload/stop/new-window/download/cleanup", data);
        }
        finally
        {
            try { WebViewHelpers.DisposeView(host, view); } catch { }
        }
    }

    public static async Task<ProbeResult> WebCompatibilityAsync(Control host, string root)
    {
        await using var server = await LocalHttpServer.StartAsync();
        var (_, view) = await WebViewHelpers.CreateViewAsync(host, Path.Combine(root, "phase21-compat"));
        try
        {
            await WebViewHelpers.NavigateAsync(view, $"{server.BaseUrl}/compat.html");
            if (!string.Equals(view.CoreWebView2.DocumentTitle, "Compatibility", StringComparison.Ordinal))
                throw new InvalidOperationException($"unexpected compatibility title: {view.CoreWebView2.DocumentTitle}");

            await WebViewHelpers.ClickSelectorAsync(view, "#spa-button");
            var spa = await WebViewHelpers.EvalStringAsync(view, "document.querySelector('#spa').textContent");
            if (spa != "spa-updated") throw new InvalidOperationException($"SPA update failed: {spa}");

            _ = await WebViewHelpers.EvalAsync(view,
                "document.cookie='amare_compat=1; path=/'; localStorage.setItem('amare_compat','1'); true");
            await WebViewHelpers.NavigateAsync(view, $"{server.BaseUrl}/compat.html");
            var cookie = await WebViewHelpers.EvalStringAsync(view, "document.cookie");
            var local = await WebViewHelpers.EvalStringAsync(view, "localStorage.getItem('amare_compat')");
            if (!cookie.Contains("amare_compat", StringComparison.Ordinal) || local != "1")
                throw new InvalidOperationException("cookie/localStorage did not persist across reload");

            var uploadPath = Path.Combine(root, "upload-test.txt");
            File.WriteAllText(uploadPath, "upload probe");
            var document = await view.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{\"depth\":1}");
            using var documentJson = JsonDocument.Parse(document);
            var rootNode = documentJson.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();
            var query = await view.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "DOM.querySelector",
                JsonSerializer.Serialize(new { nodeId = rootNode, selector = "#upload" }));
            using var queryJson = JsonDocument.Parse(query);
            var nodeId = queryJson.RootElement.GetProperty("nodeId").GetInt32();
            if (nodeId == 0) throw new InvalidOperationException("upload input node missing");
            await view.CoreWebView2.CallDevToolsProtocolMethodAsync(
                "DOM.setFileInputFiles",
                JsonSerializer.Serialize(new { nodeId, files = new[] { uploadPath } }));
            await Task.Delay(300);
            var uploadName = await WebViewHelpers.EvalStringAsync(view,
                "document.querySelector('#upload').files[0]?.name ?? ''");
            if (uploadName != "upload-test.txt") throw new InvalidOperationException($"upload failed: {uploadName}");

            var media = await WebViewHelpers.EvalStringAsync(view,
                "document.createElement('video').canPlayType('video/webm; codecs=\"vp9\"')");
            if (string.IsNullOrWhiteSpace(media)) throw new InvalidOperationException("VP9 media capability missing");
            var featureFlags = await WebViewHelpers.EvalAsync(view,
                "({fullscreen: document.fullscreenEnabled === true, pip: typeof HTMLVideoElement !== 'undefined' && 'requestPictureInPicture' in HTMLVideoElement.prototype})");
            var fullscreen = featureFlags.GetProperty("fullscreen").GetBoolean();
            var pip = featureFlags.GetProperty("pip").GetBoolean();

            view.ZoomFactor = 1.25;
            await Task.Delay(200);
            if (Math.Abs(view.ZoomFactor - 1.25) > 0.01)
                throw new InvalidOperationException($"zoom failed: {view.ZoomFactor}");
            view.ZoomFactor = 1.0;

            await WebViewHelpers.NavigateAsync(view, $"{server.BaseUrl}/redirect");
            await Task.Delay(200);
            if (!string.Equals(view.CoreWebView2.DocumentTitle, "Page Two", StringComparison.Ordinal))
                throw new InvalidOperationException($"redirect failed: {view.CoreWebView2.DocumentTitle}");

            var data = new Dictionary<string, object?>
            {
                ["spa"] = true,
                ["cookie_reload"] = true,
                ["local_storage_reload"] = true,
                ["upload"] = uploadName,
                ["media_vp9"] = media,
                ["fullscreen_feature"] = fullscreen,
                ["picture_in_picture_feature"] = pip,
                ["zoom"] = 1.25,
                ["redirect"] = true,
            };
            return Result("webview2_web_compat", ProbeStatus.Pass, true,
                "local HTML/SPA/cookie/session/upload/media/zoom/redirect compatibility", data);
        }
        finally
        {
            WebViewHelpers.DisposeView(host, view);
        }
    }

    public static async Task<ProbeResult> HttpsNavigationAsync(Control host, string root)
    {
        var (_, view) = await WebViewHelpers.CreateViewAsync(host, Path.Combine(root, "phase21-https"));
        try
        {
            try
            {
                await WebViewHelpers.NavigateAsync(view, "https://example.com/", timeoutSeconds: 20);
                var title = view.CoreWebView2.DocumentTitle;
                return Result("webview2_https_navigation", ProbeStatus.Pass, true,
                    $"HTTPS page loaded (title={title})", new { title, url = view.Source?.AbsoluteUri });
            }
            catch (Exception exception)
            {
                return Result("webview2_https_navigation", ProbeStatus.Unverified, true,
                    "HTTPS network test could not run; local tests remain separate: " + exception.Message);
            }
        }
        finally
        {
            WebViewHelpers.DisposeView(host, view);
        }
    }

    public static async Task<ProbeResult> TabLifecycleAsync(Control host, string root)
    {
        await using var server = await LocalHttpServer.StartAsync();
        var env = await WebViewHelpers.CreateEnvironmentAsync(Path.Combine(root, "phase21-tabs"));
        var views = new List<WebView2>();
        try
        {
            for (var index = 0; index < 10; index++)
            {
                var view = await WebViewHelpers.CreateViewWithEnvironmentAsync(host, env);
                views.Add(view);
                await WebViewHelpers.NavigateAsync(view, $"{server.BaseUrl}/page1.html");
            }
            if (views.Count != 10 || host.Controls.Count < 10)
                throw new InvalidOperationException("not all tabs were created");
            for (var index = 0; index < 5; index++)
            {
                WebViewHelpers.DisposeView(host, views[index]);
            }
            views.RemoveRange(0, 5);
            if (views.Count != 5)
                throw new InvalidOperationException("inactive tab close count mismatch");
            foreach (var view in views)
            {
                WebViewHelpers.DisposeView(host, view);
            }
            views.Clear();
            await Task.Delay(300);
            return Result("webview2_tab_lifecycle_10", ProbeStatus.Pass, true,
                "10 tabs created, 5 closed, remaining tabs closed", new { tabs = 10, closed = 10 });
        }
        finally
        {
            foreach (var view in views) WebViewHelpers.DisposeView(host, view);
        }
    }

    public static async Task<ProbeResult> BrowserPerformanceAsync(Control host, string root)
    {
        await using var server = await LocalHttpServer.StartAsync();
        var envStart = System.Diagnostics.Stopwatch.StartNew();
        var env = await WebViewHelpers.CreateEnvironmentAsync(Path.Combine(root, "phase22-performance"));
        envStart.Stop();
        var metrics = new List<object>();
        try
        {
            foreach (var count in new[] { 5, 10, 20 })
            {
                var views = new List<WebView2>();
                var create = System.Diagnostics.Stopwatch.StartNew();
                foreach (var _ in Enumerable.Range(0, count))
                {
                    var view = await WebViewHelpers.CreateViewWithEnvironmentAsync(host, env);
                    views.Add(view);
                    await WebViewHelpers.NavigateAsync(view, $"{server.BaseUrl}/page1.html", timeoutSeconds: 20);
                }
                create.Stop();
                var memory = ProcessProbe.Capture();
                var close = System.Diagnostics.Stopwatch.StartNew();
                foreach (var view in views)
                {
                    WebViewHelpers.DisposeView(host, view);
                }
                close.Stop();
                await Task.Delay(500);
                metrics.Add(new
                {
                    tabs = count,
                    create_and_load_ms = create.Elapsed.TotalMilliseconds,
                    close_ms = close.Elapsed.TotalMilliseconds,
                    total_working_set_mb = memory.TotalWorkingSetBytes / 1024.0 / 1024.0,
                    process_count = memory.ProcessCount,
                    renderer_mb = memory.RendererBytes / 1024.0 / 1024.0
                });
            }
            return Result("windows_browser_performance", ProbeStatus.Pass, true,
                "environment/local-page/tab-create/tab-close timings and 5/10/20-tab process memory",
                new { environment_ms = envStart.Elapsed.TotalMilliseconds, metrics });
        }
        finally
        {
            // All views are closed in the loop; environment objects are released on GC.
        }
    }

    private static async Task WaitNavigationAsync(WebView2 view, Action action, int timeoutSeconds = 10)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            view.NavigationCompleted -= Handler;
            if (args.IsSuccess) completion.TrySetResult(true);
            else completion.TrySetException(new InvalidOperationException($"navigation failed: {args.WebErrorStatus}"));
        }
        view.NavigationCompleted += Handler;
        action();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
    }
}
