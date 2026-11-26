using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amare.Windows.Core;

namespace Amare.Interop.Tests;

internal static class Phase34Tests
{
    public static void Register()
    {
        Program.Run("portable_update_semver_prerelease_ordering", SemVerOrdering);
        Program.Run("portable_update_channel_selection", ChannelSelection);
        Program.Run("portable_update_update_check_errors", CheckErrors);
        Program.Run("portable_update_manifest_and_entry_policy", ManifestPolicy);
        Program.Run("portable_update_download_url_and_size_policy", DownloadPolicy);
        Program.Run("portable_update_download_limits_and_hash", DownloadLimits);
        Program.Run("portable_update_preserves_all_user_data", PreservesAllUserData);
        Program.Run("portable_update_rollback_preserves_all_user_data", RollbackPreservesAllUserData);
        Program.Run("portable_update_staging_failure_never_touches_app", StagingFailureIsolation);
        Program.Run("portable_update_corrupt_backup_not_applied", CorruptBackupNotApplied);
        Program.Run("portable_update_recovery_attempt_limit", RecoveryAttemptLimit);
        Program.Run("portable_update_windows_credential_manager_untouched", CredentialManagerUntouched);
    }

    private static string SemVerOrdering()
    {
        (string Left, string Right)[] ordered =
        [
            ("1.0.0-alpha", "1.0.0-alpha.1"),
            ("1.0.0-alpha.1", "1.0.0-alpha.beta"),
            ("1.0.0-alpha.beta", "1.0.0-beta"),
            ("1.0.0-beta", "1.0.0-beta.2"),
            ("1.0.0-beta.2", "1.0.0-beta.11"),
            ("1.0.0-beta.11", "1.0.0-rc.1"),
            ("1.0.0-rc.1", "1.0.0"),
            ("0.92.0", "0.92.1"),
            ("0.92.0.0", "0.92.0.1")
        ];
        foreach (var pair in ordered)
        {
            Require(SemanticVersion.Compare(pair.Left, pair.Right) < 0,
                $"{pair.Left} should be lower than {pair.Right}");
            Require(SemanticVersion.Compare(pair.Right, pair.Left) > 0,
                $"{pair.Right} should be higher than {pair.Left}");
        }
        Require(SemanticVersion.Compare("1.0.0+build.1", "1.0.0+build.2") == 0,
            "build metadata must not affect precedence");
        Require(SemanticVersion.Compare("v1.0.0", "1.0.0") == 0, "v prefix");
        Require(!SemanticVersion.TryParse("1.0.0-01", out _), "numeric prerelease leading zero");
        Require(!SemanticVersion.TryParse("1.0.0-alpha..1", out _), "empty prerelease identifier");
        Require(!SemanticVersion.TryParse("1.0.0-alpha_1", out _), "invalid prerelease character");
        Require(!SemanticVersion.TryParse("1.2.3.4-beta", out _), "revision with prerelease");
        Require(SemanticVersion.TryParse("0.92.0.0", out var four) && four.Revision == 0,
            "four-part Windows version");
        return "SemVer 2.0 numeric prerelease ordering and Windows revision extension";
    }

    private static string ChannelSelection()
    {
        var releases = new[]
        {
            Release("v0.18.0", false),
            Release("v0.19.0", false),
            Release("v0.20.0", false),
            Release("v0.21.0-beta.2", true),
            Release("v0.21.0-beta.10", true)
        };
        var stable = GitHubReleaseClient.SelectUpdate("0.18.0", UpdateChannel.Stable, releases);
        Require(stable.Update?.Version == "0.20.0", "stable channel must ignore prereleases");
        var prerelease = GitHubReleaseClient.SelectUpdate("0.18.0", UpdateChannel.Prerelease, releases);
        Require(prerelease.Update?.Version == "0.21.0-beta.10",
            $"prerelease channel numeric ordering: {prerelease.Update?.Version}");
        var upToDate = GitHubReleaseClient.SelectUpdate("0.20.0", UpdateChannel.Stable, releases);
        Require(upToDate.Succeeded && !upToDate.UpdateAvailable, "no update available");
        var nextBeta = GitHubReleaseClient.SelectUpdate("0.21.0-beta.2", UpdateChannel.Prerelease, releases);
        Require(nextBeta.Update?.Version == "0.21.0-beta.10", "beta.10 must be newer than beta.2");
        return "Stable/Prerelease channel and SemVer selection";
    }

    private static string CheckErrors()
    {
        using var timeout = new FakeHandler(_ => throw new TaskCanceledException("timeout"));
        using var timeoutHttp = new HttpClient(timeout);
        var timedOut = new GitHubReleaseClient(timeoutHttp)
            .CheckAsync("0.92.0").GetAwaiter().GetResult();
        Require(!timedOut.Succeeded && timedOut.Error?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true,
            "timeout should be distinguished");

        using var server = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var serverHttp = new HttpClient(server);
        var serverError = new GitHubReleaseClient(serverHttp)
            .CheckAsync("0.92.0").GetAwaiter().GetResult();
        Require(!serverError.Succeeded && serverError.Error?.Contains("500", StringComparison.Ordinal) == true,
            "server error should include HTTP status");

        using var network = new FakeHandler(_ => throw new HttpRequestException("offline"));
        using var networkHttp = new HttpClient(network);
        var networkError = new GitHubReleaseClient(networkHttp)
            .CheckAsync("0.92.0").GetAwaiter().GetResult();
        Require(!networkError.Succeeded
            && networkError.Error?.Contains("Network error", StringComparison.OrdinalIgnoreCase) == true,
            "network error should be distinguished");

        using var malformed = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not json", Encoding.UTF8, "application/json")
        });
        using var malformedHttp = new HttpClient(malformed);
        var malformedError = new GitHubReleaseClient(malformedHttp)
            .CheckAsync("0.92.0").GetAwaiter().GetResult();
        Require(!malformedError.Succeeded
            && malformedError.Error?.Contains("invalid response", StringComparison.OrdinalIgnoreCase) == true,
            "invalid server response should be reported");

        using var cancelSource = new CancellationTokenSource();
        cancelSource.Cancel();
        var canceled = new GitHubReleaseClient(timeoutHttp)
            .CheckAsync("0.92.0", UpdateChannel.Stable, cancelSource.Token).GetAwaiter().GetResult();
        Require(!canceled.Succeeded && canceled.Error?.Contains("cancelled", StringComparison.OrdinalIgnoreCase) == true,
            "cancelled check should be reported");
        return "network/server/timeout/cancel error distinction";
    }

    private static string ManifestPolicy()
    {
        using var temp = new TempTree();
        var valid = CreatePackage(temp.Root, "0.93.0");
        Require(PortableUpdatePackage.Validate(valid, "0.93.0").Valid, "valid package");
        Require(!PortableUpdatePackage.Validate(valid, "0.93.0", "win-x64", "Other.exe").Valid,
            "entry point must match the requested executable");

        Require(!PortableUpdatePackage.Validate(
            CreatePackage(temp.Root, "0.93.0", schema: 2), "0.93.0").Valid,
            "unsupported schema");
        Require(!PortableUpdatePackage.Validate(
            CreatePackage(temp.Root, "0.93.0", product: "Other"), "0.93.0").Valid,
            "wrong product");
        Require(!PortableUpdatePackage.Validate(
            CreatePackage(temp.Root, "0.93.0", entryPoint: "bin/AMARE.Windows.exe"), "0.93.0").Valid,
            "nested entry point");
        Require(!PortableUpdatePackage.Validate(
            CreatePackage(temp.Root, "0.93.0", extraEntry: "runtime/tool.exe"), "0.93.0").Valid,
            "nested executable");
        Require(!PortableUpdatePackage.Validate(
            CreatePackage(temp.Root, "0.93.0", extraEntry: "cmd.exe"), "0.93.0").Valid,
            "dangerous root executable");
        Require(!PortableUpdatePackage.Validate(
            CreatePackage(temp.Root, "0.93.0", extraEntry: "setup.ps1"), "0.93.0").Valid,
            "script file");
        Require(!PortableUpdatePackage.Validate(
            CreatePackage(temp.Root, "0.93.0", extraEntry: "payload.msix"), "0.93.0").Valid,
            "installer file");
        Require(!PortableUpdatePackage.Validate(
            CreatePackage(temp.Root, "0.93.0", extraEntry: "CON.txt"), "0.93.0").Valid,
            "reserved device name");
        Require(!PortableUpdatePackage.Validate(
            CreatePackage(temp.Root, "0.93.0", extraEntry: "trailing."), "0.93.0").Valid,
            "trailing dot");
        Require(!PortableUpdatePackage.Validate(
            CreatePackage(temp.Root, "0.93.0", extraEntry: "../escape.txt"), "0.93.0").Valid,
            "path traversal");
        Require(!PortableUpdatePackage.Validate(
            CreatePackage(temp.Root, "0.93.0", duplicate: true), "0.93.0").Valid,
            "duplicate entry");
        Require(!PortableUpdatePackage.Validate(
            CreatePackage(temp.Root, "0.93.0", symlink: true), "0.93.0").Valid,
            "symlink entry");
        Require(!PortableUpdatePackage.Validate(
            CreatePackage(temp.Root, "0.93.0", oversizedManifest: true), "0.93.0").Valid,
            "oversized manifest");
        return "schema, entry point, executable/script, traversal, duplicate and manifest limits";
    }

    private static string DownloadPolicy()
    {
        Require(UpdateDownloadPolicy.TryGetSecureUri("https://github.com/a/b.zip", out _), "github HTTPS");
        Require(UpdateDownloadPolicy.TryGetSecureUri(
            "https://objects.githubusercontent.com/a/b.zip", out _), "githubusercontent HTTPS");
        Require(!UpdateDownloadPolicy.TryGetSecureUri("http://github.com/a/b.zip", out _), "HTTP rejected");
        Require(!UpdateDownloadPolicy.TryGetSecureUri("ftp://github.com/a/b.zip", out _), "FTP rejected");
        Require(!UpdateDownloadPolicy.TryGetSecureUri("https://evil.example/a/b.zip", out _), "arbitrary host rejected");
        Require(!UpdateDownloadPolicy.TryGetSecureUri("https://user:pass@github.com/a/b.zip", out _), "userinfo rejected");
        Require(!UpdateDownloadPolicy.TryGetSecureUri("https://192.168.1.1/a/b.zip", out _), "IP host rejected");
        Require(!UpdateDownloadPolicy.IsSafeFileName("../b.zip"), "path file name rejected");
        Require(!UpdateDownloadPolicy.IsSafeFileName("dir/b.zip"), "relative file name rejected");

        using var temp = new TempTree();
        Require(!UpdateDownloadPolicy.HasSufficientFreeSpace(temp.Root, long.MaxValue / 2),
            "insufficient disk space detection");

        var oversized = Update("https://github.com/a/b.zip", UpdateDownloadPolicy.MaxPackageBytes + 1);
        var result = new UpdateDownloader().DownloadAsync(oversized, temp.Root)
            .GetAwaiter().GetResult();
        Require(!result.Succeeded && result.Error?.Contains("limit", StringComparison.OrdinalIgnoreCase) == true,
            "oversized declared package rejected before download");

        var httpUpdate = Update("http://github.com/a/b.zip", 10);
        var httpResult = new UpdateDownloader().DownloadAsync(httpUpdate, temp.Root)
            .GetAwaiter().GetResult();
        Require(!httpResult.Succeeded && httpResult.Error?.Contains("HTTPS", StringComparison.OrdinalIgnoreCase) == true,
            "HTTP download rejected");

        var badName = Update("https://github.com/a/b.zip", 10) with
        {
            Package = new ReleaseAsset("../escape.zip", "https://github.com/a/b.zip", 10)
        };
        var badNameResult = new UpdateDownloader().DownloadAsync(badName, temp.Root)
            .GetAwaiter().GetResult();
        Require(!badNameResult.Succeeded, "unsafe package name rejected");
        return "HTTPS/GitHub host, size, safe name and disk space policy";
    }

    private static string DownloadLimits()
    {
        using var temp = new TempTree();
        var payload = Encoding.UTF8.GetBytes("portable-package-payload");
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var destination = Path.Combine(temp.Root, "downloads");

        using var goodHandler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
            {
                return Text($"{hash}  AMARE-0.93.0-win-x64.zip");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });
        using var goodHttp = new HttpClient(goodHandler);
        var good = new UpdateDownloader(goodHttp).DownloadAsync(Update(payload.Length), destination)
            .GetAwaiter().GetResult();
        Require(good.Succeeded && good.Sha256 == hash, good.Error);
        Require(!Directory.EnumerateFiles(destination, "*.part").Any(), "partial file left after success");

        using var checksumsHandler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".txt", StringComparison.Ordinal))
            {
                return Text($"{new string('1', 64)}  AMARE-0.93.0-unsigned.msix\n" +
                            $"{hash}  AMARE-0.93.0-win-x64.zip");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });
        using var checksumsHttp = new HttpClient(checksumsHandler);
        var checksumsUpdate = Update(payload.Length) with
        {
            Checksum = new ReleaseAsset("checksums.txt",
                "https://github.com/amare/test/checksums.txt", 200)
        };
        var checksums = new UpdateDownloader(checksumsHttp)
            .DownloadAsync(checksumsUpdate, Path.Combine(temp.Root, "checksums"))
            .GetAwaiter().GetResult();
        Require(checksums.Succeeded && checksums.Sha256 == hash,
            "checksums.txt must select the hash for the package file name");

        using var wrongHashHandler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
            {
                return Text(new string('0', 64));
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });
        using var wrongHashHttp = new HttpClient(wrongHashHandler);
        var wrong = new UpdateDownloader(wrongHashHttp)
            .DownloadAsync(Update(payload.Length), Path.Combine(temp.Root, "wrong"))
            .GetAwaiter().GetResult();
        Require(!wrong.Succeeded && wrong.Error?.Contains("SHA-256", StringComparison.OrdinalIgnoreCase) == true,
            "wrong hash rejected");
        Require(!Directory.Exists(Path.Combine(temp.Root, "wrong"))
            || !Directory.EnumerateFiles(Path.Combine(temp.Root, "wrong"), "*").Any(),
            "wrong hash file must be deleted");

        using var longHandler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal))
            {
                return Text($"{hash}  AMARE-0.93.0-win-x64.zip");
            }
            var content = new StreamContent(new MemoryStream(payload));
            content.Headers.ContentLength = null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var longHttp = new HttpClient(longHandler);
        var tooLong = new UpdateDownloader(longHttp)
            .DownloadAsync(Update(payload.Length - 4), Path.Combine(temp.Root, "toolong"))
            .GetAwaiter().GetResult();
        Require(!tooLong.Succeeded, "stream larger than declared size rejected");

        using var cancelSource = new CancellationTokenSource();
        cancelSource.Cancel();
        var canceled = new UpdateDownloader(goodHttp)
            .DownloadAsync(Update(payload.Length), Path.Combine(temp.Root, "cancel"),
                cancellationToken: cancelSource.Token).GetAwaiter().GetResult();
        Require(!canceled.Succeeded && canceled.Error?.Contains("cancelled", StringComparison.OrdinalIgnoreCase) == true,
            "cancellation reported");
        return "valid hash, wrong hash, oversized stream, cancellation and .part cleanup";
    }

    private static string PreservesAllUserData()
    {
        using var temp = new TempTree();
        var (app, data, package) = CreateInstallation(temp.Root, "0.93.0");
        var before = SnapshotUserData(data);
        var result = Apply(app, data, package, "0.93.0");
        Require(result.Succeeded, result.Error);
        Require(File.ReadAllText(Path.Combine(app, "AMARE.Windows.exe")) == "new-exe", "new app not applied");
        Require(File.ReadAllText(Path.Combine(app, "VERSION")).Trim() == "0.93.0", "VERSION not applied");
        RequireSameSnapshot(before, SnapshotUserData(data), "apply");
        Require(PortableUpdateStateStore.Load(data)?.Status == PortableUpdateStatus.Succeeded, "state");
        Require(!Directory.Exists(Path.Combine(data, "updates", "work")), "work directory not cleaned");
        return "profiles, Vault DB/key, site permissions, settings, history, bookmarks, downloads preserved";
    }

    private static string RollbackPreservesAllUserData()
    {
        using var temp = new TempTree();
        var (app, data, package) = CreateInstallation(temp.Root, "0.93.0");
        var before = SnapshotUserData(data);
        var result = Apply(app, data, package, "0.93.0", failAfterApply: true);
        Require(!result.Succeeded && result.RolledBack, "rollback expected");
        Require(File.ReadAllText(Path.Combine(app, "AMARE.Windows.exe")) == "old-exe", "old app not restored");
        Require(!File.Exists(Path.Combine(app, "runtime", "placeholder.txt")),
            "new-only file was left behind after rollback");
        RequireSameSnapshot(before, SnapshotUserData(data), "rollback");
        Require(PortableUpdateStateStore.Load(data)?.Status == PortableUpdateStatus.RolledBack, "rollback state");
        return "failed apply restored previous app and preserved every data file";
    }

    private static string StagingFailureIsolation()
    {
        using var temp = new TempTree();
        var (app, data, package) = CreateInstallation(temp.Root, "0.93.0",
            extraEntry: new string('a', 300) + ".txt");
        var before = SnapshotUserData(data);
        var appBefore = SnapshotTree(app);
        var result = Apply(app, data, package, "0.93.0");
        Require(!result.Succeeded && !result.RolledBack,
            "staging failure should be reported without a fake rollback");
        Require(result.Error?.Contains("No application files were changed", StringComparison.Ordinal) == true,
            $"staging failure message: {result.Error}");
        RequireSameSnapshot(appBefore, SnapshotTree(app), "staging failure app");
        RequireSameSnapshot(before, SnapshotUserData(data), "staging failure data");
        Require(!Directory.Exists(Path.Combine(data, "updates", "work")), "staging directory not cleaned");
        Require(PortableUpdateStateStore.Load(data)?.Status == PortableUpdateStatus.Failed, "failed state recorded");
        return "pre-apply extraction failure cleans staging and leaves app/data untouched";
    }

    private static string CorruptBackupNotApplied()
    {
        using var temp = new TempTree();
        var (app, data, package) = CreateInstallation(temp.Root, "0.93.0");
        var result = Apply(app, data, package, "0.93.0", crashAfterApply: true);
        Require(!result.Succeeded, "simulated crash should not report success");
        var state = PortableUpdateStateStore.Load(data)!;
        File.Delete(Path.Combine(state.BackupDir, state.EntryPoint));
        var before = SnapshotUserData(data);
        var recovered = PortableUpdateEngine.RecoverAsync(data, launchApp: false).GetAwaiter().GetResult();
        Require(!recovered.Succeeded, "corrupt backup must not report recovery success");
        Require(File.ReadAllText(Path.Combine(app, "AMARE.Windows.exe")) == "new-exe",
            "corrupt backup must not partially replace the app");
        Require(Directory.Exists(state.BackupDir), "corrupt backup must be preserved for inspection");
        RequireSameSnapshot(before, SnapshotUserData(data), "corrupt backup recovery");
        return "incomplete backup is detected before copying and is not deleted";
    }

    private static string RecoveryAttemptLimit()
    {
        using var temp = new TempTree();
        var (app, data, package) = CreateInstallation(temp.Root, "0.93.0");
        Apply(app, data, package, "0.93.0", crashAfterApply: true);
        var state = PortableUpdateStateStore.Load(data)!;
        var capped = state with
        {
            Status = PortableUpdateStatus.Failed,
            RecoveryAttempts = PortableUpdateEngine.MaxRecoveryAttempts,
            Error = "simulated recovery failure"
        };
        PortableUpdateStateStore.Save(data, capped);
        Require(!PortableUpdateRecovery.NeedsRecovery(data), "capped state must not auto-recover");
        var statePath = PortableUpdateStateStore.PathFor(data);
        var legacyJson = File.ReadAllText(statePath)
            .Replace($"\"RecoveryAttempts\": {PortableUpdateEngine.MaxRecoveryAttempts},", "", StringComparison.Ordinal);
        File.WriteAllText(statePath, legacyJson);
        var legacy = PortableUpdateStateStore.Load(data);
        Require(legacy?.RecoveryAttempts == 0, "legacy state without RecoveryAttempts must load as zero");
        PortableUpdateStateStore.Save(data, capped);
        var before = SnapshotUserData(data);
        var result = PortableUpdateEngine.RecoverAsync(data, launchApp: false).GetAwaiter().GetResult();
        Require(!result.Succeeded && result.Error?.Contains("limit", StringComparison.OrdinalIgnoreCase) == true,
            "recovery attempt limit must block further attempts");
        Require(File.ReadAllText(Path.Combine(app, "AMARE.Windows.exe")) == "new-exe", "capped recovery changed app");
        Require(Directory.Exists(state.BackupDir), "capped recovery deleted the backup");
        RequireSameSnapshot(before, SnapshotUserData(data), "capped recovery data");
        return "rollback/recovery attempt cap prevents infinite loops and keeps the backup";
    }

    private static string CredentialManagerUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "UNVERIFIED: Windows Credential Manager is not available on this host.";
        }
        var target = "AMARE.PortableUpdate.Preservation." + Guid.NewGuid().ToString("N");
        using var temp = new TempTree();
        var (app, data, package) = CreateInstallation(temp.Root, "0.93.0");
        const string secret = "credential-manager-preservation-secret";
        Require(CredentialWrite(target, secret), "could not write test credential");
        try
        {
            var result = Apply(app, data, package, "0.93.0");
            Require(result.Succeeded, result.Error);
            Require(CredentialRead(target) == secret,
                "portable update changed the Windows Credential Manager entry");
            return "Windows Credential Manager entry survived the portable apply";
        }
        finally
        {
            CredentialDelete(target);
        }
    }

    private static (string App, string Data, string Package) CreateInstallation(
        string root,
        string version,
        string? extraEntry = null)
    {
        var app = Path.Combine(root, "app");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(app, "VERSION"), "0.92.0\n");
        File.WriteAllText(Path.Combine(app, "AMARE.Windows.exe"), "old-exe");
        File.WriteAllText(Path.Combine(app, "old-only.txt"), "old");
        var files = new Dictionary<string, string>
        {
            ["profiles/default/amare.db"] = "sqlite-profile",
            ["profiles/default/site-permissions.json"] = "{\"https://example.com\":\"allow\"}",
            ["vault/default.key"] = "dpapi-protected-vault-key",
            ["settings.json"] = "{\"theme\":\"dark\"}",
            ["app-preferences.json"] = "{\"language\":\"ko\"}",
            ["update-preferences.json"] = "{\"channel\":\"stable\"}",
            ["ai-api-settings.json"] = "{\"model\":\"gpt-test\",\"provider\":\"openai\"}",
            ["bookmarks.db"] = "bookmarks",
            ["history.db"] = "history",
            ["downloads.json"] = "[{\"name\":\"file.zip\"}]",
            ["profile.db"] = "profile"
        };
        foreach (var pair in files)
        {
            var path = Path.Combine(data, pair.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, pair.Value);
        }
        return (app, data, CreatePackage(root, version, extraEntry: extraEntry));
    }

    private static string CreatePackage(
        string root,
        string version,
        int schema = 1,
        string product = "AMARE",
        string entryPoint = "AMARE.Windows.exe",
        string? extraEntry = null,
        bool duplicate = false,
        bool symlink = false,
        bool oversizedManifest = false)
    {
        var path = Path.Combine(root, $"AMARE-{version}-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddText(archive, "VERSION", version + "\n");
        AddText(archive, "AMARE.Windows.exe", "new-exe");
        AddText(archive, "AMARE.Updater.exe", "helper");
        AddText(archive, "runtime/placeholder.txt", "runtime");
        if (extraEntry != null)
        {
            AddText(archive, extraEntry, "extra");
        }
        if (duplicate)
        {
            AddText(archive, "duplicate.txt", "one");
            AddText(archive, "duplicate.txt", "two");
        }
        if (symlink)
        {
            var entry = archive.CreateEntry("link.txt");
            entry.ExternalAttributes = 0xA000 << 16;
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("target");
        }
        var manifest = new UpdatePackageManifest
        {
            Schema = schema,
            Product = product,
            Version = version,
            Architecture = "win-x64",
            EntryPoint = entryPoint,
            Runtime = "win-x64-self-contained",
            RequiredFiles = ["VERSION", "AMARE.Windows.exe", "AMARE.Updater.exe"]
        };
        var json = JsonSerializer.Serialize(manifest);
        if (oversizedManifest)
        {
            json += new string(' ', (int)PortableUpdatePackage.MaxManifestBytes + 1024);
        }
        AddText(archive, PortableUpdatePackage.ManifestName, json);
        return path;
    }

    private static AvailableUpdate Update(string packageUrl, long size)
    {
        return new AvailableUpdate(
            "0.93.0", "v0.93.0", "release", "notes", false, "https://github.com/amare/test",
            new ReleaseAsset("AMARE-0.93.0-win-x64.zip", packageUrl, size),
            new ReleaseAsset("AMARE-0.93.0-win-x64.zip.sha256",
                packageUrl.EndsWith(".zip", StringComparison.Ordinal) ? packageUrl + ".sha256" : packageUrl,
                80));
    }

    private static AvailableUpdate Update(long size) =>
        Update("https://github.com/amare/test/update.zip", size);

    private static ReleaseInfo Release(string tag, bool prerelease)
    {
        var asset = $"AMARE-{tag.TrimStart('v')}-win-x64.zip";
        return new ReleaseInfo(tag, tag, "notes", prerelease, false, "https://github.com/amare/test",
        [
            new ReleaseAsset(asset, $"https://github.com/amare/test/{asset}", 1234),
            new ReleaseAsset(asset + ".sha256", $"https://github.com/amare/test/{asset}.sha256", 65)
        ]);
    }

    private static PortableUpdateResult Apply(
        string app,
        string data,
        string package,
        string version,
        bool failAfterApply = false,
        bool crashAfterApply = false)
    {
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package))).ToLowerInvariant();
        return PortableUpdateEngine.ApplyAsync(new PortableUpdateRequest(
            package, app, data, version, hash,
            LaunchApp: false,
            FailAfterApply: failAfterApply,
            CrashAfterApply: crashAfterApply)).GetAwaiter().GetResult();
    }

    private static Dictionary<string, string> SnapshotUserData(string root)
    {
        // Updater metadata (state, lock, downloaded packages, helper log) lives
        // under the data root by design. It is not user data and is allowed to
        // change; every other file must stay byte-for-byte identical.
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative is "update-state.json" or "update.lock" or "update-health.json")
            {
                continue;
            }
            if (relative.StartsWith("updates/", StringComparison.Ordinal)
                || relative.StartsWith("logs/updater.", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("updater/", StringComparison.Ordinal))
            {
                continue;
            }
            result[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }
        return result;
    }

    private static Dictionary<string, string> SnapshotTree(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            result[Path.GetRelativePath(root, file)] =
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }
        return result;
    }

    private static void RequireSameSnapshot(
        Dictionary<string, string> expected,
        Dictionary<string, string> actual,
        string stage)
    {
        Require(expected.Count == actual.Count, $"{stage}: file count changed {expected.Count} -> {actual.Count}");
        foreach (var pair in expected)
        {
            Require(actual.TryGetValue(pair.Key, out var hash), $"{stage}: file disappeared {pair.Key}");
            Require(hash == pair.Value, $"{stage}: file changed {pair.Key}");
        }
    }

    private static void AddText(ZipArchive archive, string name, string value)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(value);
    }

    private static HttpResponseMessage Text(string value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "text/plain") };

    private static void Require(bool condition, string? message)
    {
        if (!condition) throw new InvalidOperationException(message ?? "assertion failed");
    }

    private sealed class TempTree : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "amare-portable-update", Guid.NewGuid().ToString("N"));
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

    // Windows Credential Manager P/Invoke, used only by the preservation test.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistSession = 1;
    private const int ErrorNotFound = 1168;

    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32")]
    private static extern void CredFree(IntPtr buffer);

    private static bool CredentialWrite(string target, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistSession,
                UserName = "amare-test"
            };
            return CredWrite(ref credential, 0);
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
        }
    }

    private static string? CredentialRead(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            return null;
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return string.Empty;
            var bytes = new byte[(int)credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.Unicode.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    private static void CredentialDelete(string target)
    {
        if (!CredDelete(target, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound) throw new InvalidOperationException($"CredDelete failed: {error}");
        }
    }
}
