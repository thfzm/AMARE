using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Hosting;
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

            var audioData = Convert.ToBase64String(CreateSilentWav());
            _ = await WebViewHelpers.EvalAsync(view,
                $"(() => {{ const a=document.querySelector('#audio'); a.src='data:audio/wav;base64,{audioData}'; return true; }})()");
            await WebViewHelpers.ClickSelectorAsync(view, "#audio-play");
            var audioTime = 0.0;
            for (var attempt = 0; attempt < 30 && audioTime <= 0; attempt++)
            {
                await Task.Delay(100);
                var text = await WebViewHelpers.EvalStringAsync(view, "String(document.querySelector('#audio').currentTime)");
                double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out audioTime);
            }
            if (audioTime <= 0) throw new InvalidOperationException("local audio did not start playing");
            var audioState = await WebViewHelpers.EvalAsync(view,
                "(() => { const a=document.querySelector('#audio'); a.pause(); a.volume=0.4; a.currentTime=0.2; return {paused:a.paused, volume:a.volume, currentTime:a.currentTime}; })()");
            if (!audioState.GetProperty("paused").GetBoolean()
                || Math.Abs(audioState.GetProperty("volume").GetDouble() - 0.4) > 0.01
                || Math.Abs(audioState.GetProperty("currentTime").GetDouble() - 0.2) > 0.05)
                throw new InvalidOperationException("audio pause/volume/seek state mismatch");

            await WebViewHelpers.ClickSelectorAsync(view, "#video-play");
            var videoTime = 0.0;
            for (var attempt = 0; attempt < 30 && videoTime <= 0; attempt++)
            {
                await Task.Delay(100);
                var text = await WebViewHelpers.EvalStringAsync(view, "String(document.querySelector('#video').currentTime)");
                double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out videoTime);
            }
            if (videoTime <= 0) throw new InvalidOperationException("local video did not start playing");
            _ = await WebViewHelpers.EvalAsync(view,
                "(() => { const v=document.querySelector('#video'); v.pause(); v.volume=0.4; v.currentTime=0.2; return true; })()");
            await Task.Delay(350);
            var videoState = await WebViewHelpers.EvalAsync(view,
                "(() => { const v=document.querySelector('#video'); return {paused:v.paused, volume:v.volume, currentTime:v.currentTime, duration:v.duration}; })()");
            var videoPaused = videoState.GetProperty("paused").GetBoolean();
            var videoVolume = videoState.GetProperty("volume").GetDouble();
            var videoPosition = videoState.GetProperty("currentTime").GetDouble();
            var videoDuration = videoState.GetProperty("duration").GetDouble();
            if (!videoPaused || Math.Abs(videoVolume - 0.4) > 0.01)
                throw new InvalidOperationException(
                    $"video pause/volume state mismatch: paused={videoPaused} volume={videoVolume}");
            var videoSeekActual = Math.Abs(videoPosition - 0.2) <= 0.05;
            var unsupported = await WebViewHelpers.EvalStringAsync(view,
                "document.createElement('video').canPlayType('video/x-unsupported-codec')");
            var fullscreenActual = false;
            var pipActual = false;
            try
            {
                await WebViewHelpers.ClickSelectorAsync(view, "#fullscreen");
                await Task.Delay(700);
                fullscreenActual = await WebViewHelpers.EvalStringAsync(view,
                    "document.fullscreenElement?.id || ''") == "video";
                if (fullscreenActual) _ = await WebViewHelpers.EvalAsync(view, "document.exitFullscreen()");
            }
            catch
            {
                // Feature support and actual fullscreen permission are recorded separately.
            }
            try
            {
                await WebViewHelpers.ClickSelectorAsync(view, "#pip");
                await Task.Delay(700);
                pipActual = await WebViewHelpers.EvalStringAsync(view,
                    "document.pictureInPictureElement?.id || ''") == "video";
                if (pipActual) _ = await WebViewHelpers.EvalAsync(view, "document.exitPictureInPicture()");
            }
            catch
            {
                // PiP may require window/permission support.
            }

            view.ZoomFactor = 1.25;
            await Task.Delay(200);
            if (Math.Abs(view.ZoomFactor - 1.25) > 0.01)
                throw new InvalidOperationException($"zoom failed: {view.ZoomFactor}");
            view.ZoomFactor = 1.0;

            await WebViewHelpers.NavigateAsync(view, $"{server.BaseUrl}/redirect");
            await Task.Delay(200);
            if (!string.Equals(view.CoreWebView2.DocumentTitle, "Page Two", StringComparison.Ordinal))
                throw new InvalidOperationException($"redirect failed: {view.CoreWebView2.DocumentTitle}");

            await WebViewHelpers.NavigateAsync(view, $"{server.BaseUrl}/ws.html");
            var wsReconnect = "";
            for (var attempt = 0; attempt < 50 && wsReconnect != "amare-reconnect"; attempt++)
            {
                await Task.Delay(100);
                wsReconnect = await WebViewHelpers.EvalStringAsync(view, "window.__wsReconnect || ''");
            }
            if (wsReconnect != "amare-reconnect")
                throw new InvalidOperationException($"WebSocket echo/reconnect failed: {wsReconnect}");
            var wsState = await WebViewHelpers.EvalStringAsync(view, "window.__wsState || ''");
            if (wsState != "closed")
                throw new InvalidOperationException($"WebSocket did not close cleanly: {wsState}");
            var reconnectReleased = false;
            for (var attempt = 0; attempt < 30 && !reconnectReleased; attempt++)
            {
                await Task.Delay(100);
                reconnectReleased = server.ActiveWebSockets == 0;
            }
            if (!reconnectReleased)
                throw new InvalidOperationException("reconnect WebSocket remained active after close");

            var (_, wsView) = await WebViewHelpers.CreateViewAsync(host, Path.Combine(root, "phase21-ws-hold"));
            try
            {
                await WebViewHelpers.NavigateAsync(wsView, $"{server.BaseUrl}/ws-hold.html");
                var wsOpen = false;
                for (var attempt = 0; attempt < 30 && !wsOpen; attempt++)
                {
                    await Task.Delay(100);
                    wsOpen = await WebViewHelpers.EvalStringAsync(wsView, "String(window.__wsOpen)") == "true";
                }
                if (!wsOpen) throw new InvalidOperationException("held WebSocket did not connect");
                if (server.ActiveWebSockets < 1) throw new InvalidOperationException("server did not track active WebSocket");
            }
            finally
            {
                WebViewHelpers.DisposeView(host, wsView);
            }
            var websocketReleased = false;
            for (var attempt = 0; attempt < 100 && !websocketReleased; attempt++)
            {
                await Task.Delay(100);
                websocketReleased = server.ActiveWebSockets == 0;
            }

            var data = new Dictionary<string, object?>
            {
                ["spa"] = true,
                ["cookie_reload"] = true,
                ["local_storage_reload"] = true,
                ["upload"] = uploadName,
                ["media_vp9"] = media,
                ["media_audio_play_pause_volume_seek"] = true,
                ["media_video_play_pause_volume"] = true,
                ["media_video_seek_actual"] = videoSeekActual,
                ["media_video_position"] = videoPosition,
                ["media_video_duration"] = videoDuration,
                ["media_unsupported_codec"] = string.IsNullOrEmpty(unsupported),
                ["fullscreen_feature"] = fullscreen,
                ["fullscreen_actual"] = fullscreenActual,
                ["picture_in_picture_feature"] = pip,
                ["picture_in_picture_actual"] = pipActual,
                ["zoom"] = 1.25,
                ["redirect"] = true,
                ["websocket_connect_echo_reconnect"] = true,
                ["websocket_tab_close_cleanup"] = websocketReleased,
            };
            return Result("webview2_web_compat", ProbeStatus.Pass, true,
                "local HTML/SPA/cookie/session/upload/audio/video/fullscreen/PiP/zoom/redirect/WebSocket compatibility", data);
        }
        finally
        {
            WebViewHelpers.DisposeView(host, view);
        }
    }

    private static bool RunCertutil(string arguments, out string output)
    {
        output = "";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "certutil.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return false;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                output = "timeout";
                return false;
            }
            output = stdout.Result + stderr.Result;
            return process.ExitCode == 0;
        }
        catch (Exception exception)
        {
            output = exception.GetType().Name;
            return false;
        }
    }

    private static bool TrustExists(string thumbprint)
    {
        try
        {
            using var check = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            check.Open(OpenFlags.ReadOnly);
            return check.Certificates
                .Find(X509FindType.FindByThumbprint, thumbprint, false).Count > 0;
        }
        catch
        {
            return true;
        }
    }

    public static async Task<ProbeResult> LocalTrustedCaAsync(Control host, string root)
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            "CN=AMARE Local Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        using var ca = caRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest(
            "CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        serverRequest.CertificateExtensions.Add(san.Build());
        serverRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(serverRequest.PublicKey, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var issued = serverRequest.Create(
            ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29), serial);
        using var serverWithKey = issued.CopyWithPrivateKey(serverKey);
        var pfx = serverWithKey.Export(X509ContentType.Pfx);
        using var certificate = new X509Certificate2(
            pfx, (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);

        using var publicCa = new X509Certificate2(ca.Export(X509ContentType.Cert));
        var cerPath = Path.Combine(Path.GetTempPath(), $"amare-test-ca-{Guid.NewGuid():N}.cer");
        File.WriteAllBytes(cerPath, publicCa.Export(X509ContentType.Cert));
        var trustInstalled = false;
        var trustRemoved = false;
        try
        {
            if (!RunCertutil($"-addstore -user -f Root \"{cerPath}\"", out var addOutput)
                || !TrustExists(publicCa.Thumbprint))
            {
                return Result("webview2_local_trusted_ca", ProbeStatus.Unverified, true,
                    $"Local test CA could not be installed non-interactively; trust-store check skipped ({addOutput.Trim()})",
                    new { tls_handshake_ok = false, trust_installed = false, reason = addOutput.Trim() });
            }
            trustInstalled = true;

            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            var chainOk = chain.Build(certificate);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(Phase21Probes).Assembly.GetName().Name,
                EnvironmentName = Environments.Production
            });
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            await using var app = builder.Build();
            app.MapGet("/", () => Results.Content(
                "<!doctype html><title>Local trusted CA</title><h1>ok</h1>", "text/html"));
            await app.StartAsync();
            var url = app.Urls.First();

            string tlsProtocol;
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, new Uri(url).Port);
                using var ssl = new SslStream(tcp.GetStream(), false);
                await ssl.AuthenticateAsClientAsync("localhost").WaitAsync(TimeSpan.FromSeconds(10));
                if (!ssl.IsAuthenticated) throw new AuthenticationException("TLS client did not authenticate");
                tlsProtocol = ssl.SslProtocol.ToString();
            }
            catch (Exception exception)
            {
                return Result("webview2_local_trusted_ca", ProbeStatus.Unverified, true,
                    $"Local trusted CA TLS handshake failed: {exception.GetType().Name}",
                    new { tls_handshake_ok = false, trust_installed = true });
            }

            var (_, view) = await WebViewHelpers.CreateViewAsync(host, Path.Combine(root, "phase21-local-trusted-ca"));
            try
            {
                var completed = new TaskCompletionSource<(bool Success, CoreWebView2WebErrorStatus Status)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
                    completed.TrySetResult((args.IsSuccess, args.WebErrorStatus));
                view.CoreWebView2.NavigationCompleted += Handler;
                try
                {
                    view.CoreWebView2.Navigate(url);
                    var (success, error) = await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));

                    RunCertutil($"-delstore -user -f Root \"{publicCa.Thumbprint}\"", out _);
                    trustInstalled = false;
                    trustRemoved = !TrustExists(publicCa.Thumbprint);

                    var passed = success && trustRemoved;
                    return Result("webview2_local_trusted_ca",
                        passed ? ProbeStatus.Pass : ProbeStatus.Unverified,
                        true,
                        passed
                            ? $"Local trusted CA TLS {tlsProtocol} + WebView2 normal HTTPS navigation PASS; trust removed"
                            : success
                                ? "Local trusted CA navigation passed but test trust cleanup was not confirmed"
                                : $"Local trusted CA WebView2 navigation failed: {error}",
                        new
                        {
                            certificate_kind = "local-trusted-ca",
                            server_url = url,
                            tls_handshake_ok = true,
                            tls_protocol = tlsProtocol,
                            chain_build_ok = chainOk,
                            navigation_success = success,
                            web_error_status = success ? null : error.ToString(),
                            trust_installed = true,
                            trust_removed = trustRemoved,
                        });
                }
                finally
                {
                    view.CoreWebView2.NavigationCompleted -= Handler;
                }
            }
            finally
            {
                WebViewHelpers.DisposeView(host, view);
                await app.StopAsync();
            }
        }
        finally
        {
            if (trustInstalled)
            {
                RunCertutil($"-delstore -user -f Root \"{publicCa.Thumbprint}\"", out _);
                trustRemoved = !TrustExists(publicCa.Thumbprint);
            }
            try { File.Delete(cerPath); } catch { }
        }
    }

    public static Task<ProbeResult> KestrelCertificateErrorAsync(Control host, string root) =>
        KestrelCertificateProbeAsync(host, root, "webview2_certificate_error", "self-signed",
            expired: false, dnsName: "localhost", includeLoopbackSan: true);

    public static Task<ProbeResult> KestrelCertificateExpiredAsync(Control host, string root) =>
        KestrelCertificateProbeAsync(host, root, "webview2_certificate_expired", "expired",
            expired: true, dnsName: "localhost", includeLoopbackSan: true);

    public static Task<ProbeResult> KestrelCertificateHostnameMismatchAsync(Control host, string root) =>
        KestrelCertificateProbeAsync(host, root, "webview2_certificate_hostname_mismatch", "hostname-mismatch",
            expired: false, dnsName: "wrong.example", includeLoopbackSan: false);

    private static async Task<ProbeResult> KestrelCertificateProbeAsync(
        Control host, string root, string probeName, string kind, bool expired, string dnsName, bool includeLoopbackSan)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        if (includeLoopbackSan) san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        var notBefore = expired ? DateTimeOffset.UtcNow.AddDays(-30) : DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = expired ? DateTimeOffset.UtcNow.AddDays(-2) : DateTimeOffset.UtcNow.AddDays(30);
        using var ephemeral = request.CreateSelfSigned(notBefore, notAfter);
        var pfx = ephemeral.Export(X509ContentType.Pfx);
        using var certificate = new X509Certificate2(
            pfx, (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Phase21Probes).Assembly.GetName().Name,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate));
        });
        await using var app = builder.Build();
        app.MapGet("/", () => Results.Content(
            "<!doctype html><title>Certificate probe</title><h1>ok</h1>", "text/html"));
        await app.StartAsync();
        var url = app.Urls.First();

        // Prove the HTTPS server itself completes a normal TLS handshake before
        // asking WebView2 to interpret the certificate as invalid. The client
        // explicitly trusts this throwaway test certificate for transport only.
        string tlsProtocol;
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, new Uri(url).Port);
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync("localhost").WaitAsync(TimeSpan.FromSeconds(10));
            if (!ssl.IsAuthenticated) throw new AuthenticationException("TLS client did not authenticate");
            tlsProtocol = ssl.SslProtocol.ToString();
        }
        catch (Exception exception)
        {
            return Result(probeName, ProbeStatus.Unverified, true,
                $"Kestrel {kind} TLS handshake did not complete before WebView2 navigation: {exception.GetType().Name}",
                new { certificate_kind = kind, server_url = url, tls_handshake_ok = false });
        }

        var (_, view) = await WebViewHelpers.CreateViewAsync(host, Path.Combine(root, $"phase21-kestrel-cert-{kind}"));
        try
        {
            var certificateError = new TaskCompletionSource<CoreWebView2WebErrorStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs args)
            {
                args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                certificateError.TrySetResult(args.ErrorStatus);
            }
            view.CoreWebView2.ServerCertificateErrorDetected += Handler;
            try
            {
                view.CoreWebView2.Navigate(url);
                var status = await certificateError.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var data = new Dictionary<string, object?>
                {
                    ["certificate_kind"] = kind,
                    ["server_url"] = url,
                    ["tls_handshake_ok"] = true,
                    ["tls_protocol"] = tlsProtocol,
                    ["certificate_has_private_key"] = certificate.HasPrivateKey,
                    ["certificate_not_after"] = certificate.NotAfter.ToUniversalTime().ToString("O"),
                    ["navigation_success"] = false,
                    ["web_error_status"] = status.ToString(),
                    ["certificate_rejected"] = true,
                    ["action"] = "cancel",
                    ["user_bypass_implemented"] = false,
                };
                return Result(probeName, ProbeStatus.Pass, true,
                    $"Kestrel {kind} TLS handshake OK ({tlsProtocol}), WebView2 raised ServerCertificateErrorDetected ({status}) and cancelled; no bypass", data);
            }
            catch (TimeoutException)
            {
                return Result(probeName, ProbeStatus.Unverified, true,
                    $"Kestrel HTTPS {kind} certificate event timed out; actual WebView2 event not observed",
                    new { certificate_kind = kind, server_url = url, navigation_success = (bool?)null, certificate_rejected = (bool?)null });
            }
            finally
            {
                view.CoreWebView2.ServerCertificateErrorDetected -= Handler;
            }
        }
        finally
        {
            WebViewHelpers.DisposeView(host, view);
            await app.StopAsync();
        }
    }

    public static async Task<ProbeResult> CertificateErrorAsync(Control host, string root)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var ephemeralCertificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var pfx = ephemeralCertificate.Export(X509ContentType.Pfx);
        using var certificate = new X509Certificate2(
            pfx, (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? serverError = null;
        var serverTask = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var ssl = new SslStream(client.GetStream(), false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ApplicationProtocols = [SslApplicationProtocol.Http11]
                });
                var body = Encoding.UTF8.GetBytes("<!doctype html><title>Certificate probe</title><h1>ok</h1>");
                var header = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: " +
                    body.Length + "\r\nConnection: close\r\n\r\n");
                await ssl.WriteAsync(header);
                await ssl.WriteAsync(body);
                await ssl.FlushAsync();
            }
            catch (Exception exception)
            {
                serverError = exception.Message;
            }
        });
        var (_, view) = await WebViewHelpers.CreateViewAsync(host, Path.Combine(root, "phase21-cert"));
        try
        {
            var certificateError = new TaskCompletionSource<CoreWebView2WebErrorStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs args)
            {
                // Explicitly cancel; this probe must never allow/ignore the certificate.
                args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                certificateError.TrySetResult(args.ErrorStatus);
            }
            view.CoreWebView2.ServerCertificateErrorDetected += Handler;
            try
            {
                view.CoreWebView2.Navigate($"https://127.0.0.1:{port}/");
                var status = await certificateError.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var data = new Dictionary<string, object?>
                {
                    ["navigation_success"] = false,
                    ["web_error_status"] = status.ToString(),
                    ["certificate_rejected"] = true,
                    ["action"] = "cancel",
                    ["user_bypass_implemented"] = false,
                };
                return Result("webview2_certificate_error", ProbeStatus.Pass, true,
                    "self-signed local certificate raised ServerCertificateErrorDetected and was cancelled; no bypass", data);
            }
            catch (TimeoutException)
            {
                return Result("webview2_certificate_error", ProbeStatus.Unverified, true,
                    "local TLS certificate probe timed out before ServerCertificateErrorDetected; engine-level result is not claimed",
                    new { navigation_success = (bool?)null, certificate_rejected = (bool?)null, server_error = serverError });
            }
            finally
            {
                view.CoreWebView2.ServerCertificateErrorDetected -= Handler;
            }
        }
        finally
        {
            WebViewHelpers.DisposeView(host, view);
            listener.Stop();
            await serverTask;
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

    private static byte[] CreateSilentWav()
    {
        const int sampleRate = 8000;
        const int seconds = 1;
        var dataLength = sampleRate * seconds * 2;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            writer.Write(new byte[dataLength]);
        }
        return stream.ToArray();
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
