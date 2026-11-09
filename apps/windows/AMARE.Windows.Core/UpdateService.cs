using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Amare.Windows.Core;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);
public sealed record ReleaseInfo(
    string TagName,
    string Name,
    string Notes,
    bool Prerelease,
    bool Draft,
    string PageUrl,
    IReadOnlyList<ReleaseAsset> Assets);

public sealed record AvailableUpdate(
    string Version,
    string TagName,
    string Name,
    string Notes,
    bool Prerelease,
    string PageUrl,
    ReleaseAsset Package,
    ReleaseAsset? Checksum);

public sealed record UpdateCheckResult(
    bool Succeeded,
    bool UpdateAvailable,
    string CurrentVersion,
    AvailableUpdate? Update,
    string? Error);

public sealed record UpdateDownloadResult(
    bool Succeeded,
    string? FilePath,
    string? Sha256,
    long Bytes,
    string? Error);

public enum UpdateChannel
{
    Stable,
    Prerelease,
}

public static class SemanticVersion
{
    public static int Compare(string left, string right)
    {
        var a = Parse(left);
        var b = Parse(right);
        var number = a.Major.CompareTo(b.Major);
        if (number != 0) return number;
        number = a.Minor.CompareTo(b.Minor);
        if (number != 0) return number;
        number = a.Patch.CompareTo(b.Patch);
        if (number != 0) return number;
        if (a.Pre == null && b.Pre == null) return 0;
        if (a.Pre == null) return 1;
        if (b.Pre == null) return -1;
        return string.Compare(a.Pre, b.Pre, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParse(string? value, out (int Major, int Minor, int Patch, string? Pre) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim().TrimStart('v', 'V');
        var buildSplit = text.Split('+', 2)[0];
        var pre = (string?)null;
        var dash = buildSplit.IndexOf('-');
        if (dash >= 0)
        {
            pre = buildSplit[(dash + 1)..];
            buildSplit = buildSplit[..dash];
        }
        var parts = buildSplit.Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || (parts.Length > 2 && !int.TryParse(parts[2], out _)))
        {
            return false;
        }
        var patch = parts.Length > 2 ? int.Parse(parts[2]) : 0;
        version = (major, minor, patch, pre);
        return true;
    }

    private static (int Major, int Minor, int Patch, string? Pre) Parse(string value) =>
        TryParse(value, out var parsed) ? parsed : (0, 0, 0, null);
}

public sealed class GitHubReleaseClient
{
    public const string Owner = "thfzm";
    public const string Repository = "AMARE";
    public static readonly Uri ReleasesApi = new($"https://api.github.com/repos/{Owner}/{Repository}/releases?per_page=20");
    private readonly HttpClient _http;

    public GitHubReleaseClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AMARE", "0.48"));
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        UpdateChannel channel = UpdateChannel.Stable,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(ReleasesApi, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
            {
                return new UpdateCheckResult(false, false, currentVersion, null,
                    "GitHub API rate limit reached. Try again later.");
            }
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, false, currentVersion, null,
                    $"GitHub Releases returned HTTP {(int)response.StatusCode}.");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new UpdateCheckResult(false, false, currentVersion, null,
                    "GitHub Releases response was not a release list.");
            }
            var releases = document.RootElement.EnumerateArray()
                .Select(ParseRelease)
                .Where(release => release != null)
                .Select(release => release!)
                .ToArray();
            return SelectUpdate(currentVersion, channel, releases);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(false, false, currentVersion, null, "Update check was cancelled.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new UpdateCheckResult(false, false, currentVersion, null,
                $"Update check failed: {exception.Message}");
        }
    }

    public static UpdateCheckResult SelectUpdate(
        string currentVersion,
        UpdateChannel channel,
        IReadOnlyList<ReleaseInfo> releases)
    {
        var candidate = releases
            .Where(release => !release.Draft)
            .Where(release => channel == UpdateChannel.Prerelease || !release.Prerelease)
            .Where(release => SemanticVersion.TryParse(release.TagName, out _))
            .OrderByDescending(release => release.TagName, Comparer<string>.Create(SemanticVersion.Compare))
            .FirstOrDefault(release => SemanticVersion.Compare(release.TagName, currentVersion) > 0);
        if (candidate == null)
        {
            return new UpdateCheckResult(true, false, currentVersion, null, null);
        }
        var package = candidate.Assets.FirstOrDefault(asset =>
            asset.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
            ?? candidate.Assets.FirstOrDefault(asset =>
                asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && !asset.Name.Contains("msix", StringComparison.OrdinalIgnoreCase));
        if (package == null)
        {
            return new UpdateCheckResult(true, true, currentVersion, null,
                "The latest release does not contain a portable Windows ZIP.");
        }
        var checksum = candidate.Assets.FirstOrDefault(asset =>
            asset.Name.Equals(package.Name + ".sha256", StringComparison.OrdinalIgnoreCase)
            || asset.Name.EndsWith("checksums.txt", StringComparison.OrdinalIgnoreCase));
        return new UpdateCheckResult(true, true, currentVersion, new AvailableUpdate(
            Version: candidate.TagName.TrimStart('v', 'V'),
            TagName: candidate.TagName,
            Name: candidate.Name,
            Notes: candidate.Notes,
            Prerelease: candidate.Prerelease,
            PageUrl: candidate.PageUrl,
            Package: package,
            Checksum: checksum), null);
    }

    private static ReleaseInfo? ParseRelease(JsonElement element)
    {
        try
        {
            var tag = element.GetProperty("tag_name").GetString();
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var name = element.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
            var notes = element.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var prerelease = element.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            var draft = element.TryGetProperty("draft", out var d) && d.GetBoolean();
            var page = element.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            var assets = new List<ReleaseAsset>();
            if (element.TryGetProperty("assets", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in array.EnumerateArray())
                {
                    var assetName = asset.GetProperty("name").GetString() ?? "";
                    var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                    var size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    assets.Add(new ReleaseAsset(assetName, url, size));
                }
            }
            return new ReleaseInfo(tag, name, notes, prerelease, draft, page, assets);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public sealed class UpdateDownloader
{
    private readonly HttpClient _http;

    public UpdateDownloader(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        AvailableUpdate update,
        string destinationDirectory,
        Action<long, long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (update.Checksum == null)
        {
            return new UpdateDownloadResult(false, null, null, 0,
                "Release does not provide a SHA-256 checksum; update is blocked.");
        }
        string? partPath = null;
        try
        {
            var expected = await ReadChecksumAsync(update.Checksum.DownloadUrl, cancellationToken);
            if (expected == null)
            {
                return new UpdateDownloadResult(false, null, null, 0,
                    "Could not read a valid SHA-256 checksum from the release.");
            }
            Directory.CreateDirectory(destinationDirectory);
            var fileName = Path.GetFileName(update.Package.Name);
            var finalPath = Path.Combine(destinationDirectory, fileName);
            partPath = finalPath + ".part";
            using var response = await _http.GetAsync(
                update.Package.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.Package.Size;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = File.Create(partPath))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    progress?.Invoke(received, total);
                }
            }
            var info = new FileInfo(partPath);
            if (update.Package.Size > 0 && info.Length != update.Package.Size)
            {
                File.Delete(partPath);
                return new UpdateDownloadResult(false, null, null, info.Length,
                    "Downloaded file size does not match the release metadata.");
            }
            string actual;
            await using (var file = File.OpenRead(partPath))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
            }
            if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual), Convert.FromHexString(expected)))
            {
                File.Delete(partPath);
                return new UpdateDownloadResult(false, null, actual, info.Length,
                    "SHA-256 mismatch; downloaded file was deleted.");
            }
            File.Move(partPath, finalPath, overwrite: true);
            return new UpdateDownloadResult(true, finalPath, actual, info.Length, null);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(partPath);
            return new UpdateDownloadResult(false, null, null, 0, "Download cancelled.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or FormatException)
        {
            TryDeleteFile(partPath);
            return new UpdateDownloadResult(false, null, null, 0, $"Download failed: {exception.Message}");
        }
    }

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (path != null && File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A stale .part file is cleaned by the next download attempt.
        }
    }

    private async Task<string?> ReadChecksumAsync(string url, CancellationToken cancellationToken)
    {
        var text = await _http.GetStringAsync(url, cancellationToken);
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 64 && token.All(Uri.IsHexDigit)) return token.ToLowerInvariant();
        }
        return null;
    }
}
