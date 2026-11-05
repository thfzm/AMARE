using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amare.Windows.Core;

namespace Amare.Interop.Tests;

internal static class Phase24Tests
{
    public static void Register()
    {
        Program.Run("windows_0_18_update_package_validation", ValidatePackage);
        Program.Run("windows_0_18_update_apply_preserves_data", ApplyPreservesData);
        Program.Run("windows_0_18_update_rollback", RollbackOnFailure);
        Program.Run("windows_0_18_crash_recovery", CrashRecovery);
        Program.Run("windows_0_18_launch_failure_rolls_back", LaunchFailureRollsBack);
        Program.Run("windows_0_18_download_checksum_and_cancel", DownloadChecksumAndCancel);
        Program.Run("windows_0_18_ai_mock_errors_and_stream", AiMockErrorsAndStream);
        Program.Run("windows_0_19_update_manifest_failures", ManifestFailures);
        Program.Run("windows_0_19_update_rejects_repeat_and_downgrade", RejectRepeatAndDowngrade);
        Program.Run("windows_0_19_update_rejects_concurrent_helper", RejectConcurrentHelper);
        Program.Run("windows_0_19_update_health_marker_timeout", HealthMarkerTimeout);
        Program.Run("windows_0_19_update_crash_after_apply_recovery", CrashAfterApplyRecovery);
        Program.Run("windows_0_20_corrupt_update_state", CorruptUpdateState);
        Program.Run("windows_0_20_tab_manager_30_sequential", TabManager30);
        Program.Run("windows_0_20_ai_settings_roundtrip", AiSettingsRoundtrip);
    }

    private static string ValidatePackage()
    {
        using var temp = new TempTree();
        var package = CreatePackage(temp.Root, "0.18.0");
        var valid = PortableUpdatePackage.Validate(package, "0.18.0", "win-x64");
        Require(valid.Valid && valid.Manifest?.Version == "0.18.0", valid.Error);
        var wrongVersion = PortableUpdatePackage.Validate(package, "0.19.0", "win-x64");
        Require(!wrongVersion.Valid, "version mismatch should fail");
        var missing = CreatePackage(temp.Root, "0.18.0", includeManifest: false);
        Require(!PortableUpdatePackage.Validate(missing).Valid, "missing manifest should fail");
        var badZip = Path.Combine(temp.Root, "bad.zip");
        File.WriteAllText(badZip, "not a zip");
        Require(!PortableUpdatePackage.Validate(badZip).Valid, "corrupt zip should fail");
        var traversal = CreatePackage(temp.Root, "0.18.0", traversal: true);
        Require(!PortableUpdatePackage.Validate(traversal).Valid, "traversal should fail");
        return "manifest, version, corrupt and traversal validation";
    }

    private static string ApplyPreservesData()
    {
        using var temp = new TempTree();
        var (app, data, package) = CreateInstallation(temp.Root, "0.18.0");
        var result = Apply(temp.Root, app, data, package);
        Require(result.Succeeded, result.Error);
        Require(File.ReadAllText(Path.Combine(app, "AMARE.Windows.exe")) == "new-exe", "new entry not applied");
        Require(File.ReadAllText(Path.Combine(data, "profile.db")) == "user-data", "user profile changed");
        Require(File.ReadAllText(Path.Combine(data, "settings.json")) == "settings", "settings changed");
        Require(File.ReadAllText(Path.Combine(data, "bookmarks.db")) == "bookmarks", "bookmarks changed");
        Require(File.ReadAllText(Path.Combine(data, "history.db")) == "history", "history changed");
        Require(PortableUpdateStateStore.Load(data)?.Status == PortableUpdateStatus.Succeeded, "state not succeeded");
        Require(!Directory.Exists(Path.Combine(data, "updates", "work")), "work directory should be cleaned");
        return "real-filesystem apply and user-data preservation";
    }

    private static string RollbackOnFailure()
    {
        using var temp = new TempTree();
        var (app, data, package) = CreateInstallation(temp.Root, "0.18.0");
        var result = Apply(temp.Root, app, data, package, failAfterApply: true);
        Require(!result.Succeeded && result.RolledBack, "expected automatic rollback");
        Require(File.ReadAllText(Path.Combine(app, "AMARE.Windows.exe")) == "old-exe", "old entry not restored");
        Require(File.ReadAllText(Path.Combine(data, "profile.db")) == "user-data", "user data changed");
        Require(File.ReadAllText(Path.Combine(data, "bookmarks.db")) == "bookmarks", "bookmarks changed");
        Require(PortableUpdateStateStore.Load(data)?.Status == PortableUpdateStatus.RolledBack, "state not rolled back");
        return "failed replace restored previous app and preserved data";
    }

    private static string CrashRecovery()
    {
        using var temp = new TempTree();
        var (app, data, package) = CreateInstallation(temp.Root, "0.18.0");
        var result = Apply(temp.Root, app, data, package, crashAfterBackup: true);
        Require(!result.Succeeded && File.ReadAllText(Path.Combine(app, "AMARE.Windows.exe")) == "old-exe",
            "crash simulation changed app before recovery");
        Require(PortableUpdateRecovery.NeedsRecovery(data), "interrupted state should be recoverable");
        var recovered = PortableUpdateEngine.RecoverAsync(data, launchApp: false).GetAwaiter().GetResult();
        Require(recovered.Succeeded, recovered.Error);
        Require(File.ReadAllText(Path.Combine(data, "profile.db")) == "user-data", "recovery changed user data");
        Require(PortableUpdateStateStore.Load(data)?.Status == PortableUpdateStatus.RolledBack, "recovery state");
        return "interrupted backup recovery";
    }

    private static string LaunchFailureRollsBack()
    {
        using var temp = new TempTree();
        var (app, data, package) = CreateInstallation(temp.Root, "0.18.0");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))).ToLowerInvariant();
        var result = PortableUpdateEngine.ApplyAsync(new PortableUpdateRequest(
            package, app, data, "0.18.0", hash,
            LaunchApp: true, RelaunchAfterRollback: false, HealthTimeoutSeconds: 5)).GetAwaiter().GetResult();
        Require(!result.Succeeded && result.RolledBack, "failed launch should roll back");
        Require(File.ReadAllText(Path.Combine(app, "AMARE.Windows.exe")) == "old-exe", "old app not restored");
        Require(File.ReadAllText(Path.Combine(data, "profile.db")) == "user-data", "user data changed");
        return "new-version launch failure restored previous app";
    }

    private static string DownloadChecksumAndCancel()
    {
        using var temp = new TempTree();
        var payload = Encoding.UTF8.GetBytes("portable-update-payload");
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var update = new AvailableUpdate(
            "0.18.0", "v0.18.0", "release", "notes", false, "https://example.invalid",
            new ReleaseAsset("AMARE-0.18.0-win-x64.zip", "https://packages.invalid/update.zip", payload.Length),
            new ReleaseAsset("AMARE-0.18.0-win-x64.zip.sha256", "https://packages.invalid/update.zip.sha256", 80));
        using var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(".sha256", StringComparison.Ordinal))
                return Text($"{hash}  AMARE-0.18.0-win-x64.zip");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });
        using var http = new HttpClient(handler);
        var downloader = new UpdateDownloader(http);
        var destination = Path.Combine(temp.Root, "downloads");
        var ok = downloader.DownloadAsync(update, destination).GetAwaiter().GetResult();
        Require(ok.Succeeded && File.Exists(ok.FilePath), ok.Error);
        Require(ok.Sha256 == hash, "hash mismatch");

        var badUpdate = update with
        {
            Checksum = new ReleaseAsset("checksums.txt", "https://packages.invalid/bad.sha256", 80)
        };
        using var badHandler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(".sha256", StringComparison.Ordinal))
                return Text(new string('0', 64));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });
        using var badHttp = new HttpClient(badHandler);
        var bad = new UpdateDownloader(badHttp).DownloadAsync(badUpdate, Path.Combine(temp.Root, "bad")).GetAwaiter().GetResult();
        Require(!bad.Succeeded, "checksum mismatch should fail");

        using var cancelSource = new CancellationTokenSource();
        cancelSource.Cancel();
        var canceled = downloader.DownloadAsync(update, Path.Combine(temp.Root, "cancel"),
            cancellationToken: cancelSource.Token).GetAwaiter().GetResult();
        Require(!canceled.Succeeded && !string.IsNullOrWhiteSpace(canceled.Error), "cancel should fail cleanly");
        var cancelDestination = Path.Combine(temp.Root, "cancel-during-download");
        using var cancelHandler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(".sha256", StringComparison.Ordinal))
                return Text($"{hash}  AMARE-0.18.0-win-x64.zip");
            throw new OperationCanceledException("cancel during download");
        });
        using var cancelHttp = new HttpClient(cancelHandler);
        var during = new UpdateDownloader(cancelHttp).DownloadAsync(update, cancelDestination).GetAwaiter().GetResult();
        Require(!during.Succeeded, "cancel during download should fail");
        if (Directory.Exists(cancelDestination))
        {
            Require(!Directory.EnumerateFiles(cancelDestination, "*.part").Any(), "partial file was not cleaned");
        }
        return "download, checksum mismatch, cancellation and partial cleanup";
    }

    private static string AiMockErrorsAndStream()
    {
        const string key = "secret-test-key";
        var settings = new AiApiSettings("gpt-test", "https://api.invalid/v1", AiProviderCatalog.OpenAiId);
        using var successHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"content":"hello"}}]}""", Encoding.UTF8, "application/json")
        });
        var answer = AiApiClient.CompleteAsync(key, settings, "hi", null, successHandler).GetAwaiter().GetResult();
        Require(answer == "hello", "success response parse");

        using var authHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent($$"""{"error":"invalid key {{key}}"}""", Encoding.UTF8, "application/json")
        });
        try
        {
            AiApiClient.CompleteAsync(key, settings, "hi", null, authHandler).GetAwaiter().GetResult();
            throw new InvalidOperationException("401 should throw");
        }
        catch (InvalidOperationException exception)
        {
            Require(!exception.Message.Contains(key, StringComparison.Ordinal), "API key leaked in error");
        }

        using var streamHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"hel\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
        });
        var chunks = new List<string>();
        var enumerator = AiApiClient.StreamAsync(key, settings, "hi", null, streamHandler)
            .GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                chunks.Add(enumerator.Current);
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        Require(string.Concat(chunks) == "hello", "stream chunks");

        using var rateHandler = new FakeHandler(_ => new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent("{\"error\":\"rate limited\"}", Encoding.UTF8, "application/json")
        });
        try
        {
            AiApiClient.CompleteAsync(key, settings, "hi", null, rateHandler).GetAwaiter().GetResult();
            throw new InvalidOperationException("429 should throw");
        }
        catch (InvalidOperationException exception)
        {
            Require(exception.Message.Contains("429", StringComparison.Ordinal), "429 status not surfaced");
        }

        using var malformedHandler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"unexpected\":true}", Encoding.UTF8, "application/json")
        });
        var malformed = AiApiClient.CompleteAsync(key, settings, "hi", null, malformedHandler).GetAwaiter().GetResult();
        Require(malformed.Contains("empty response", StringComparison.OrdinalIgnoreCase), "malformed response handling");

        using var networkHandler = new FakeHandler(_ => throw new HttpRequestException("offline"));
        try
        {
            AiApiClient.CompleteAsync(key, settings, "hi", null, networkHandler).GetAwaiter().GetResult();
            throw new InvalidOperationException("network error should throw");
        }
        catch (HttpRequestException exception)
        {
            Require(!exception.Message.Contains(key, StringComparison.Ordinal), "key leaked in network error");
        }

        using var canceledStream = new CancellationTokenSource();
        canceledStream.Cancel();
        try
        {
            AiApiClient.StreamAsync(key, settings, "hi", null, streamHandler, canceledStream.Token)
                .GetAsyncEnumerator().MoveNextAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException("canceled stream should throw");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        Require(AiProviderCatalog.Resolve("openai-compatible").Id == AiProviderCatalog.CompatibleId,
            "provider selection");
        try
        {
            AiApiClient.CompleteAsync("", settings, "hi", null, successHandler).GetAwaiter().GetResult();
            throw new InvalidOperationException("empty key should be rejected");
        }
        catch (InvalidOperationException exception)
        {
            Require(exception.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase), "empty key guard");
        }
        return "mock success, 401/429/network errors, redaction, SSE, cancel and provider selection";
    }

    private static string ManifestFailures()
    {
        using var temp = new TempTree();
        var malformed = Path.Combine(temp.Root, "malformed.zip");
        using (var archive = ZipFile.Open(malformed, ZipArchiveMode.Create))
        {
            AddText(archive, PortableUpdatePackage.ManifestName, "{ not valid json");
            AddText(archive, "AMARE.Windows.exe", "new-exe");
            AddText(archive, "VERSION", "0.18.0");
        }
        Require(!PortableUpdatePackage.Validate(malformed).Valid, "malformed manifest should fail");
        var wrongArch = Path.Combine(temp.Root, "wrong-arch.zip");
        using (var archive = ZipFile.Open(wrongArch, ZipArchiveMode.Create))
        {
            var manifest = new UpdatePackageManifest
            {
                Schema = 1, Product = "AMARE", Version = "0.18.0",
                Architecture = "win-arm64", EntryPoint = "AMARE.Windows.exe"
            };
            AddText(archive, PortableUpdatePackage.ManifestName, JsonSerializer.Serialize(manifest));
            AddText(archive, "AMARE.Windows.exe", "new-exe");
        }
        Require(!PortableUpdatePackage.Validate(wrongArch, "0.18.0", "win-x64").Valid, "arch mismatch should fail");
        return "malformed manifest and architecture rejection";
    }

    private static string RejectRepeatAndDowngrade()
    {
        using var temp = new TempTree();
        var (app, data, package) = CreateInstallation(temp.Root, "0.18.0");
        var first = Apply(temp.Root, app, data, package);
        Require(first.Succeeded, first.Error);
        var repeated = Apply(temp.Root, app, data, package);
        Require(!repeated.Succeeded && repeated.Error?.Contains("not newer", StringComparison.OrdinalIgnoreCase) == true,
            "repeat update should be rejected");
        var downgrade = CreatePackage(temp.Root, "0.17.0");
        var old = Apply(temp.Root, app, data, downgrade, version: "0.17.0");
        Require(!old.Succeeded && old.Error?.Contains("not newer", StringComparison.OrdinalIgnoreCase) == true,
            "downgrade should be rejected");
        Require(File.ReadAllText(Path.Combine(app, "VERSION")).Trim() == "0.18.0", "installed version changed");
        return "repeat and downgrade rejected";
    }

    private static string RejectConcurrentHelper()
    {
        using var temp = new TempTree();
        var (app, data, package) = CreateInstallation(temp.Root, "0.18.0");
        var lockPath = Path.Combine(data, "update.lock");
        Directory.CreateDirectory(data);
        using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var result = Apply(temp.Root, app, data, package);
        Require(!result.Succeeded && result.Error?.Contains("already running", StringComparison.OrdinalIgnoreCase) == true,
            "concurrent update should be rejected");
        Require(File.ReadAllText(Path.Combine(app, "AMARE.Windows.exe")) == "old-exe", "app changed under lock");
        return "concurrent helper lock rejected";
    }

    private static string HealthMarkerTimeout()
    {
        using var temp = new TempTree();
        var (app, data, package) = CreateProcessPackage(temp.Root, "0.18.0");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))).ToLowerInvariant();
        var result = PortableUpdateEngine.ApplyAsync(new PortableUpdateRequest(
            package, app, data, "0.18.0", hash,
            LaunchApp: true, RelaunchAfterRollback: false, HealthTimeoutSeconds: 2)).GetAwaiter().GetResult();
        Require(!result.Succeeded && result.RolledBack, "missing health marker should roll back");
        Require(File.ReadAllText(Path.Combine(app, "AMARE.Windows.exe")) == "old-exe", "old app not restored");
        return "missing health marker timed out and rolled back";
    }

    private static string CrashAfterApplyRecovery()
    {
        using var temp = new TempTree();
        var (app, data, package) = CreateInstallation(temp.Root, "0.18.0");
        var result = Apply(temp.Root, app, data, package, crashAfterApply: true);
        Require(!result.Succeeded, "crash after apply should not report success");
        Require(File.ReadAllText(Path.Combine(app, "AMARE.Windows.exe")) == "new-exe", "new files not staged");
        var recovered = PortableUpdateEngine.RecoverAsync(data, launchApp: false).GetAwaiter().GetResult();
        Require(recovered.Succeeded, recovered.Error);
        Require(File.ReadAllText(Path.Combine(app, "AMARE.Windows.exe")) == "old-exe", "old app not restored");
        Require(File.ReadAllText(Path.Combine(data, "bookmarks.db")) == "bookmarks", "bookmarks changed");
        return "crash after apply recovered previous version";
    }

    private static string CorruptUpdateState()
    {
        using var temp = new TempTree();
        File.WriteAllText(Path.Combine(temp.Root, PortableUpdateStateStore.FileName), "{ invalid json");
        Require(PortableUpdateStateStore.Load(temp.Root) is null, "corrupt state should be ignored");
        Require(!PortableUpdateRecovery.NeedsRecovery(temp.Root), "corrupt state must not trigger recovery");
        return "corrupt update state ignored without data changes";
    }

    private static string TabManager30()
    {
        var manager = new TabManager();
        for (var i = 0; i < 30; i++)
        {
            manager.AddTab($"https://example.com/{i}", $"Tab {i}");
        }
        Require(manager.Tabs.Count == 30 && manager.ActiveTab is not null, "30 tabs not created");
        foreach (var tab in manager.Tabs.ToArray())
        {
            manager.Close(tab.Id);
        }
        Require(manager.Tabs.Count == 1, "last tab should be replaced");
        Require(manager.Diagnostics().Created == 31 && manager.Diagnostics().Closed == 30, "tab diagnostics");
        return "30 sequential tabs with last-tab replacement";
    }

    private static string AiSettingsRoundtrip()
    {
        var settings = new AiApiSettings("gpt-test", "https://api.example/v1", AiProviderCatalog.CompatibleId);
        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<AiApiSettings>(json);
        Require(loaded?.Provider == AiProviderCatalog.CompatibleId, "provider not persisted");
        Require(loaded?.Model == "gpt-test" && loaded.BaseUrl == "https://api.example/v1", "settings not persisted");
        return "provider/model/base URL JSON round-trip";
    }

    private static PortableUpdateResult Apply(
        string root, string app, string data, string package,
        bool failAfterApply = false, bool crashAfterBackup = false, bool crashAfterApply = false,
        string version = "0.18.0")
    {
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))).ToLowerInvariant();
        return PortableUpdateEngine.ApplyAsync(new PortableUpdateRequest(
            package, app, data, version, hash,
            LaunchApp: false,
            FailAfterApply: failAfterApply,
            CrashAfterBackup: crashAfterBackup,
            CrashAfterApply: crashAfterApply)).GetAwaiter().GetResult();
    }

    private static (string App, string Data, string Package) CreateInstallation(string root, string version)
    {
        var app = Path.Combine(root, "app");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(app, "VERSION"), "0.17.0\n");
        File.WriteAllText(Path.Combine(app, "AMARE.Windows.exe"), "old-exe");
        File.WriteAllText(Path.Combine(app, "old-only.txt"), "old");
        File.WriteAllText(Path.Combine(data, "profile.db"), "user-data");
        File.WriteAllText(Path.Combine(data, "settings.json"), "settings");
        File.WriteAllText(Path.Combine(data, "bookmarks.db"), "bookmarks");
        File.WriteAllText(Path.Combine(data, "history.db"), "history");
        return (app, data, CreatePackage(root, version));
    }

    private static (string App, string Data, string Package) CreateProcessPackage(string root, string version)
    {
        var app = Path.Combine(root, "app");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(app, "VERSION"), "0.17.0\n");
        File.WriteAllText(Path.Combine(app, "AMARE.Windows.exe"), "old-exe");
        File.WriteAllText(Path.Combine(data, "profile.db"), "user-data");
        var package = Path.Combine(root, $"AMARE-{version}-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(package, ZipArchiveMode.Create);
        AddText(archive, "VERSION", version + "\n");
        AddBytes(archive, "AMARE.Windows.exe", ProcessBytes(), 0x81ED);
        AddText(archive, "AMARE.Updater.exe", "helper");
        AddManifest(archive, version);
        return (app, data, package);
    }

    private static byte[] ProcessBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            return File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        }
        var script = "#!/bin/sh\nsleep 30\n";
        return Encoding.UTF8.GetBytes(script);
    }

    private static string CreatePackage(
        string root,
        string version,
        bool includeManifest = true,
        bool traversal = false)
    {
        var path = Path.Combine(root, $"AMARE-{version}-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddText(archive, "VERSION", version + "\n");
        AddText(archive, "AMARE.Windows.exe", "new-exe");
        AddText(archive, "AMARE.Updater.exe", "helper");
        AddText(archive, "runtime/placeholder.txt", "runtime");
        if (includeManifest)
        {
            AddManifest(archive, version);
        }
        if (traversal) AddText(archive, "../escape.txt", "bad");
        return path;
    }

    private static void AddManifest(ZipArchive archive, string version)
    {
        var manifest = new UpdatePackageManifest
        {
            Schema = 1,
            Product = "AMARE",
            Version = version,
            Architecture = "win-x64",
            EntryPoint = "AMARE.Windows.exe",
            Runtime = "win-x64-self-contained",
            RequiredFiles = ["VERSION", "AMARE.Windows.exe", "AMARE.Updater.exe"]
        };
        AddText(archive, PortableUpdatePackage.ManifestName, JsonSerializer.Serialize(manifest));
    }

    private static void AddText(ZipArchive archive, string name, string value)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(value);
    }

    private static void AddBytes(ZipArchive archive, string name, byte[] value, int unixMode = 0)
    {
        var entry = archive.CreateEntry(name);
        if (unixMode != 0) entry.ExternalAttributes = unixMode << 16;
        using var stream = entry.Open();
        stream.Write(value);
    }

    private static HttpResponseMessage Text(string value, string mediaType = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, mediaType) };

    private static void Require(bool condition, string? message)
    {
        if (!condition) throw new InvalidOperationException(message ?? "assertion failed");
    }

    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "amare-018-tests", Guid.NewGuid().ToString("N"));
        public TempTree() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
