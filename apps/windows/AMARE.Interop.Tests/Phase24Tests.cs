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
        Require(File.ReadAllText(Path.Combine(data, "profile.db")) == "user-data", "user data changed");
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
        return "download, checksum mismatch and cancellation";
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
        return "mock success, 401 redaction and SSE streaming";
    }

    private static PortableUpdateResult Apply(
        string root, string app, string data, string package,
        bool failAfterApply = false, bool crashAfterBackup = false)
    {
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))).ToLowerInvariant();
        return PortableUpdateEngine.ApplyAsync(new PortableUpdateRequest(
            package, app, data, "0.18.0", hash,
            LaunchApp: false,
            FailAfterApply: failAfterApply,
            CrashAfterBackup: crashAfterBackup)).GetAwaiter().GetResult();
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
        return (app, data, CreatePackage(root, version));
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
        if (traversal) AddText(archive, "../escape.txt", "bad");
        return path;
    }

    private static void AddText(ZipArchive archive, string name, string value)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(value);
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
