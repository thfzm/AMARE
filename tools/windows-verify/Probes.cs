using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using System.Windows.Forms;

namespace WindowsVerify;

internal static class Probes
{
    private static ProbeResult Result(string name, string status, bool executed, string detail, object? data = null) =>
        new(name, status, executed, detail, DateTimeOffset.UtcNow, data);

    public static async Task<ProbeResult> ProfileIsolationAsync(Control host, string root)
    {
        var www = Path.Combine(root, "www");
        Directory.CreateDirectory(www);
        File.WriteAllText(Path.Combine(www, "storage.html"), """
            <!doctype html><html><body><h1>AMARE storage probe</h1></body></html>
            """);

        var (_, a) = await WebViewHelpers.CreateViewAsync(host, Path.Combine(root, "profile-a"));
        var (_, b) = await WebViewHelpers.CreateViewAsync(host, Path.Combine(root, "profile-b"));
        try
        {
            WebViewHelpers.SetVirtualHost(a.CoreWebView2, "amare.test", www);
            WebViewHelpers.SetVirtualHost(b.CoreWebView2, "amare.test", www);
            await WebViewHelpers.NavigateAsync(a, "https://amare.test/storage.html");
            await WebViewHelpers.NavigateAsync(b, "https://amare.test/storage.html");

            var setScript = """
                (async () => {
                  document.cookie = 'amare_session=secret; path=/; SameSite=Lax';
                  localStorage.setItem('amare_local', 'secret');
                  await new Promise((resolve, reject) => {
                    const req = indexedDB.open('amare_probe', 1);
                    req.onupgradeneeded = () => {
                      if (!req.result.objectStoreNames.contains('kv')) req.result.createObjectStore('kv');
                    };
                    req.onerror = () => reject(req.error);
                    req.onsuccess = () => {
                      const db = req.result;
                      const tx = db.transaction('kv', 'readwrite');
                      tx.objectStore('kv').put('secret', 'k');
                      tx.oncomplete = () => resolve(true);
                      tx.onerror = () => reject(tx.error);
                    };
                  });
                  return true;
                })()
                """;
            var readScript = """
                (async () => {
                  const idb = await new Promise((resolve) => {
                    const req = indexedDB.open('amare_probe', 1);
                    req.onerror = () => resolve(null);
                    req.onsuccess = () => {
                      const db = req.result;
                      if (!db.objectStoreNames.contains('kv')) return resolve(null);
                      const tx = db.transaction('kv', 'readonly');
                      const get = tx.objectStore('kv').get('k');
                      get.onsuccess = () => resolve(get.result ?? null);
                      get.onerror = () => resolve(null);
                    };
                  });
                  return {
                    cookie: document.cookie,
                    localStorage: localStorage.getItem('amare_local'),
                    indexedDb: idb
                  };
                })()
                """;

            _ = await WebViewHelpers.EvalAsync(a, setScript);
            await Task.Delay(300);
            var aState = await WebViewHelpers.EvalAsync(a, readScript);
            var bState = await WebViewHelpers.EvalAsync(b, readScript);
            var aCookies = await a.CoreWebView2.CookieManager.GetCookiesAsync("https://amare.test/");
            var bCookies = await b.CoreWebView2.CookieManager.GetCookiesAsync("https://amare.test/");

            var aCookie = aState.GetProperty("cookie").GetString() ?? "";
            var bCookie = bState.GetProperty("cookie").GetString() ?? "";
            var aLocal = aState.GetProperty("localStorage").ValueKind == JsonValueKind.String
                ? aState.GetProperty("localStorage").GetString() : null;
            var bLocal = bState.GetProperty("localStorage").ValueKind == JsonValueKind.String
                ? bState.GetProperty("localStorage").GetString() : null;
            var aIdb = aState.GetProperty("indexedDb").ValueKind == JsonValueKind.String
                ? aState.GetProperty("indexedDb").GetString() : null;
            var bIdb = bState.GetProperty("indexedDb").ValueKind == JsonValueKind.String
                ? bState.GetProperty("indexedDb").GetString() : null;
            var aCookieStore = aCookies.Any(c => c.Name == "amare_session");
            var bCookieStore = bCookies.Any(c => c.Name == "amare_session");

            if (!aCookie.Contains("amare_session", StringComparison.Ordinal)) throw new InvalidOperationException("profile A missing document.cookie");
            if (aLocal != "secret") throw new InvalidOperationException("profile A missing localStorage");
            if (aIdb != "secret") throw new InvalidOperationException("profile A missing IndexedDB");
            if (!aCookieStore) throw new InvalidOperationException("profile A missing CookieManager cookie");
            if (bCookie.Contains("amare_session", StringComparison.Ordinal)) throw new InvalidOperationException("profile B leaked document.cookie");
            if (bLocal is not null) throw new InvalidOperationException("profile B leaked localStorage");
            if (bIdb is not null) throw new InvalidOperationException("profile B leaked IndexedDB");
            if (bCookieStore) throw new InvalidOperationException("profile B leaked CookieManager cookie");

            var data = new Dictionary<string, object?>
            {
                ["profile_a"] = new { cookie = true, localStorage = true, indexedDb = true, cookieManager = true },
                ["profile_b"] = new { cookie = false, localStorage = false, indexedDb = false, cookieManager = false },
            };
            return Result("profile_isolation", ProbeStatus.Pass, true,
                "cookies, localStorage, IndexedDB, CookieManager are isolated between separate user data folders", data);
        }
        finally
        {
            WebViewHelpers.DisposeView(host, a);
            WebViewHelpers.DisposeView(host, b);
        }
    }

    public static async Task<ProbeResult> CdpInteractionAsync(Control host, string root)
    {
        var www = Path.Combine(root, "www");
        Directory.CreateDirectory(www);
        File.WriteAllText(Path.Combine(www, "interaction.html"), """
            <!doctype html><html><body style="margin:0">
              <button id="b" style="position:absolute;left:20px;top:20px;width:160px;height:48px"
                onclick="document.getElementById('out').textContent='clicked'">click me</button>
              <input id="i" style="position:absolute;left:20px;top:90px;width:300px;height:32px" />
              <div id="out" style="position:absolute;left:20px;top:140px">not-clicked</div>
              <div style="height:3000px"></div>
            </body></html>
            """);

        var (_, view) = await WebViewHelpers.CreateViewAsync(host, Path.Combine(root, "cdp"));
        try
        {
            WebViewHelpers.SetVirtualHost(view.CoreWebView2, "amare.test", www);
            await WebViewHelpers.NavigateAsync(view, "https://amare.test/interaction.html");

            var document = await view.CoreWebView2.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{\"depth\":1}");
            if (!document.Contains("\"root\"", StringComparison.Ordinal))
                throw new InvalidOperationException("DOM.getDocument returned no root");

            await WebViewHelpers.ClickSelectorAsync(view, "#b");
            var clicked = await WebViewHelpers.EvalStringAsync(view, "document.querySelector('#out').textContent");
            if (clicked != "clicked") throw new InvalidOperationException($"click failed: out={clicked}");

            await WebViewHelpers.ClickSelectorAsync(view, "#i");
            await WebViewHelpers.InsertTextAsync(view, "hello 한글");
            await Task.Delay(100);
            var value = await WebViewHelpers.EvalStringAsync(view, "document.querySelector('#i').value");
            if (value != "hello 한글") throw new InvalidOperationException($"text input failed: value={value}");

            await WebViewHelpers.ScrollAsync(view, 100, 300, -900);
            await Task.Delay(500);
            var scrollY = await WebViewHelpers.EvalStringAsync(view, "String(window.scrollY)");
            if (!double.TryParse(scrollY, out var y) || y <= 0)
                throw new InvalidOperationException($"scroll failed: window.scrollY={scrollY}");

            var data = new Dictionary<string, object?>
            {
                ["dom_document"] = true,
                ["trusted_click"] = true,
                ["text_input"] = value,
                ["scroll_y"] = y,
            };
            return Result("cdp_dom_click_input_scroll", ProbeStatus.Pass, true,
                "DOM.getDocument, CDP mouse click, Input.insertText, and scroll gesture all affected the page", data);
        }
        finally
        {
            WebViewHelpers.DisposeView(host, view);
        }
    }

    public static async Task<ProbeResult> UserInterventionAsync(
        Control host,
        string root,
        Action<string> status)
    {
        var www = Path.Combine(root, "www");
        Directory.CreateDirectory(www);
        File.WriteAllText(Path.Combine(www, "intervention.html"), """
            <!doctype html><html><body style="margin:0">
              <button id="b" style="position:absolute;left:20px;top:20px;width:160px;height:48px">agent tick</button>
              <div id="out" style="position:absolute;left:20px;top:90px">0</div>
            </body></html>
            """);

        var (_, view) = await WebViewHelpers.CreateViewAsync(host, Path.Combine(root, "intervention"));
        try
        {
            WebViewHelpers.SetVirtualHost(view.CoreWebView2, "amare.test", www);
            await WebViewHelpers.NavigateAsync(view, "https://amare.test/intervention.html");

            // Control phase: CDP input must not look like OS-level user input.
            var baseline = NativeInput.LastInputTick();
            for (var i = 0; i < 6; i++)
            {
                await WebViewHelpers.ClickSelectorAsync(view, "#b");
            }
            if (NativeInput.LastInputTick() != baseline)
            {
                return Result("user_intervention_detection", ProbeStatus.Unverified, true,
                    "OS last-input tick changed during the CDP control phase; could not distinguish CDP input from user input. "
                    + "Retry without touching the mouse/keyboard during the control phase.");
            }

            MessageBox.Show(
                "Click OK, then move the mouse or press a key in the AMARE window during the 6-second probe.\n\nDo not touch input before clicking OK.",
                "AMARE user-intervention probe",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            status("Intervene now: move the mouse or press a key in the AMARE window (6 seconds).");
            await Task.Delay(1000);
            baseline = NativeInput.LastInputTick();
            var ticks = 0;
            var detected = false;
            for (var i = 0; i < 40; i++)
            {
                await WebViewHelpers.ClickSelectorAsync(view, "#b");
                ticks++;
                if (NativeInput.LastInputTick() != baseline)
                {
                    detected = true;
                    break;
                }
                await Task.Delay(150);
            }

            if (detected)
            {
                status("User intervention detected; agent loop stopped.");
                var data = new Dictionary<string, object?>
                {
                    ["intervention_detected"] = true,
                    ["agent_stopped"] = true,
                    ["ticks_before_stop"] = ticks,
                    ["detection_method"] = "GetLastInputInfo (OS-level input) vs CDP agent loop",
                };
                return Result("user_intervention_detection", ProbeStatus.Pass, true,
                    "OS-level user input was detected during the CDP agent loop and the loop stopped", data);
            }

            status("No user intervention detected in the 6-second window (UNVERIFIED).");
            return Result("user_intervention_detection", ProbeStatus.Unverified, true,
                "no OS-level user input detected; run interactively and move/click/type during the prompt");
        }
        finally
        {
            WebViewHelpers.DisposeView(host, view);
        }
    }

    public static async Task<ProbeResult> Memory30TabsAsync(
        Control host,
        string root,
        Action<string> status)
    {
        status("30-tab memory probe: creating tabs may take 1–2 minutes...");
        var before = ProcessProbe.Capture();
        var env = await WebViewHelpers.CreateEnvironmentAsync(Path.Combine(root, "memory-profile"));
        var views = new List<WebView2>();
        try
        {
            var html = "<!doctype html><html><body>" +
                       string.Concat(Enumerable.Range(0, 500).Select(i => $"<div>probe {i}</div>")) +
                       "</body></html>";
            var url = "data:text/html;charset=utf-8," + Uri.EscapeDataString(html);
            for (var i = 0; i < 30; i++)
            {
                var view = await WebViewHelpers.CreateViewWithEnvironmentAsync(host, env);
                views.Add(view);
            }
            await Task.Delay(1000);
            var afterEmpty = ProcessProbe.Capture();

            foreach (var view in views)
            {
                await WebViewHelpers.NavigateAsync(view, url);
            }
            await Task.Delay(5000);
            var afterCreate = ProcessProbe.Capture();

            foreach (var view in views)
            {
                view.Visible = false;
            }
            await Task.Delay(1000);
            var afterBackground = ProcessProbe.Capture();

            foreach (var view in views)
            {
                try { await view.CoreWebView2.TrySuspendAsync(); }
                catch { /* best effort */ }
            }
            await Task.Delay(3000);
            var afterSuspend = ProcessProbe.Capture();

            foreach (var view in views)
            {
                WebViewHelpers.DisposeView(host, view);
            }
            views.Clear();
            await Task.Delay(5000);
            var afterClose = ProcessProbe.Capture();

            var averageRenderer = afterCreate.RendererBytes / 30.0;
            var reclaimed = afterClose.TotalWorkingSetBytes < afterCreate.TotalWorkingSetBytes;
            var report = new MemoryReport(
                30,
                before,
                afterEmpty,
                afterCreate,
                afterBackground,
                afterSuspend,
                afterClose,
                averageRenderer,
                reclaimed,
                "WebView2 totals include browser/renderer/GPU/utility. TestToolBytes is WindowsVerify; AmareMainBytes is AMARE.Windows if running. Other WebView2 apps can be included; per-tab renderer mapping is approximate.");
            var data = new Dictionary<string, object?> { ["memory"] = report };
            var detail = $"empty={afterEmpty.TotalWorkingSetBytes / 1024 / 1024}MB " +
                         $"content={afterCreate.TotalWorkingSetBytes / 1024 / 1024}MB " +
                         $"background={afterBackground.TotalWorkingSetBytes / 1024 / 1024}MB " +
                         $"after_suspend={afterSuspend.TotalWorkingSetBytes / 1024 / 1024}MB " +
                         $"after_close={afterClose.TotalWorkingSetBytes / 1024 / 1024}MB " +
                         $"webview_browser={afterCreate.BrowserBytes / 1024 / 1024}MB " +
                         $"renderer={afterCreate.RendererBytes / 1024 / 1024}MB " +
                         $"gpu={afterCreate.GpuBytes / 1024 / 1024}MB " +
                         $"utility={afterCreate.UtilityBytes / 1024 / 1024}MB " +
                         $"test_tool={afterCreate.TestToolBytes / 1024 / 1024}MB " +
                         $"amare_main={afterCreate.AmareMainBytes / 1024 / 1024}MB " +
                         $"avg_renderer_per_tab={averageRenderer / 1024 / 1024:F1}MB " +
                         $"reclaimed={reclaimed}";
            return Result("memory_30_tabs", reclaimed ? ProbeStatus.Pass : ProbeStatus.Fail, true, detail, data);
        }
        finally
        {
            foreach (var view in views) WebViewHelpers.DisposeView(host, view);
        }
    }

    public static async Task<ProbeResult> DrmMatrixAsync(Control host, string root)
    {
        var www = Path.Combine(root, "www");
        Directory.CreateDirectory(www);
        File.WriteAllText(Path.Combine(www, "drm.html"), "<!doctype html><html><body>DRM probe</body></html>");

        var (_, view) = await WebViewHelpers.CreateViewAsync(host, Path.Combine(root, "drm"));
        try
        {
            WebViewHelpers.SetVirtualHost(view.CoreWebView2, "amare.test", www);
            await WebViewHelpers.NavigateAsync(view, "https://amare.test/drm.html");
            var js = """
                (async () => {
                  const result = {
                    keySystems: {},
                    codecs: {},
                    hdr: {},
                    playback: { attempted: false, reason: 'manual/public asset test required' }
                  };

                  const config = (robustness) => [{
                    initDataTypes: ['cenc'],
                    distinctiveIdentifier: 'optional',
                    persistentState: 'optional',
                    videoCapabilities: [{ contentType: 'video/mp4; codecs="avc1.42E01E"', robustness }]
                  }];

                  for (const [name, key] of [['widevine', 'com.widevine.alpha'], ['playready', 'com.microsoft.playready']]) {
                    result.keySystems[name] = {};
                    for (const robustness of ['SW_SECURE_CRYPTO', 'SW_SECURE_DECODE', 'HW_SECURE_DECODE']) {
                      try {
                        await navigator.requestMediaKeySystemAccess(key, config(robustness));
                        result.keySystems[name][robustness] = true;
                      } catch (e) {
                        result.keySystems[name][robustness] = false;
                      }
                    }
                  }

                  const codecs = [
                    ['h264', 'video/mp4; codecs="avc1.640028"'],
                    ['hevc', 'video/mp4; codecs="hvc1.1.6.L120.90"'],
                    ['vp9', 'video/webm; codecs="vp09.00.10.08"'],
                    ['av1', 'video/mp4; codecs="av01.0.08M.08"']
                  ];
                  const resolutions = [['720p', 1280, 720], ['1080p', 1920, 1080], ['2160p', 3840, 2160]];
                  for (const [name, type] of codecs) {
                    result.codecs[name] = {};
                    for (const [label, width, height] of resolutions) {
                      try {
                        const info = await navigator.mediaCapabilities.decodingInfo({
                          type: 'media-source',
                          video: { contentType: type, width, height, bitrate: 8000000, framerate: 30 }
                        });
                        result.codecs[name][label] = {
                          supported: info.supported,
                          smooth: info.smooth,
                          powerEfficient: info.powerEfficient
                        };
                      } catch (e) {
                        result.codecs[name][label] = { supported: false, error: String(e) };
                      }
                    }
                  }

                  result.hdr = {
                    dynamicRangeHigh: matchMedia('(dynamic-range: high)').matches,
                    colorGamutP3: matchMedia('(color-gamut: p3)').matches,
                    screenColorDepth: screen.colorDepth
                  };
                  return result;
                })()
                """;
            var matrix = await WebViewHelpers.EvalAsync(view, js);
            var data = JsonSerializer.Deserialize<Dictionary<string, object?>>(matrix.GetRawText());
            var keySystems = matrix.GetProperty("keySystems");
            var hasKeySystem = new[] { "widevine", "playready" }.Any(name =>
                keySystems.TryGetProperty(name, out var item) && item.EnumerateObject().Any(p => p.Value.GetBoolean()));
            if (!hasKeySystem)
                throw new InvalidOperationException("No Widevine or PlayReady key system was accepted");

            var detail = "capability matrix collected; actual service playback is manual (see drm_playback_manual)";
            return Result("drm_capability_matrix", ProbeStatus.Pass, true, detail, data);
        }
        finally
        {
            WebViewHelpers.DisposeView(host, view);
        }
    }

    public static ProbeResult UpdateInstaller() =>
        Result("windows_installer_update_flow", ProbeStatus.Unverified, false,
            "Rust transaction tests pass (amare-update), but no Windows installer exists yet. " +
            "Actual install failure, data preservation, and rollback must be tested after Phase 2 packaging.");

    public static ProbeResult WindowsKeychain() =>
        Result("windows_dpapi_keychain", ProbeStatus.Unverified, false,
            "Encryption logic is tested in Rust; DPAPI/Windows Hello integration is not implemented/tested.");

    public static ProbeResult PaidDrmServices() =>
        Result("netflix_disney_paid_services", ProbeStatus.Skipped, false,
            "Not attempted. Requires explicit user consent and test accounts; no paid login is performed automatically.");

    public static ProbeResult DrmPlaybackManual() =>
        Result("drm_playback_manual", ProbeStatus.Unverified, false,
            "Manual step: use a public Shaka/Widevine test asset and record playback success, resolution, codec, hardware acceleration, HDR.");
}
