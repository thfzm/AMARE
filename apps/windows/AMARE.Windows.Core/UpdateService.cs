using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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

/// <summary>
/// SemVer 2.0 precedence with a practical Windows extension for four-part
/// file versions (major.minor.patch.revision). Prerelease identifiers are
/// compared by the SemVer rules, not as case-insensitive strings.
/// </summary>
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
        number = a.Revision.CompareTo(b.Revision);
        if (number != 0) return number;
        if (a.Pre == null && b.Pre == null) return 0;
        if (a.Pre == null) return 1;
        if (b.Pre == null) return -1;
        var leftParts = a.Pre.Split('.');
        var rightParts = b.Pre.Split('.');
        for (var index = 0; index < Math.Min(leftParts.Length, rightParts.Length); index++)
        {
            number = CompareIdentifier(leftParts[index], rightParts[index]);
            if (number != 0) return number;
        }
        return leftParts.Length.CompareTo(rightParts.Length);
    }

    public static bool TryParse(
        string? value,
        out (int Major, int Minor, int Patch, int Revision, string? Pre) version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V')) text = text[1..];
        if (text.Length == 0) return false;

        var buildSplit = text.Split('+', 2);
        if (buildSplit.Length == 2 && !IsValidIdentifierList(buildSplit[1], allowLeadingZeroNumeric: true))
        {
            return false;
        }
        var coreAndPre = buildSplit[0];
        var pre = (string?)null;
        var dash = coreAndPre.IndexOf('-');
        if (dash >= 0)
        {
            pre = coreAndPre[(dash + 1)..];
            coreAndPre = coreAndPre[..dash];
            if (!IsValidIdentifierList(pre, allowLeadingZeroNumeric: false)) return false;
        }

        var parts = coreAndPre.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        var numbers = new int[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length == 0 || !parts[index].All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(parts[index], out numbers[index]) || numbers[index] < 0) return false;
        }
        if (parts.Length == 4 && pre != null) return false; // Windows revision is not SemVer.
        version = (numbers[0], numbers[1], numbers[2], numbers[3], pre);
        return true;
    }

    private static bool IsValidIdentifierList(string value, bool allowLeadingZeroNumeric)
    {
        if (value.Length == 0) return false;
        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0) return false;
            if (!identifier.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')) return false;
            if (!allowLeadingZeroNumeric
                && identifier.All(char.IsAsciiDigit)
                && identifier.Length > 1
                && identifier[0] == '0')
            {
                return false;
            }
        }
        return true;
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            var trimmedLeft = left.TrimStart('0');
            var trimmedRight = right.TrimStart('0');
            if (trimmedLeft.Length == 0) trimmedLeft = "0";
            if (trimmedRight.Length == 0) trimmedRight = "0";
            var number = trimmedLeft.Length.CompareTo(trimmedRight.Length);
            if (number != 0) return number;
            return string.CompareOrdinal(trimmedLeft, trimmedRight);
        }
        if (leftNumeric) return -1;
        if (rightNumeric) return 1;
        return string.CompareOrdinal(left, right);
    }

    private static (int Major, int Minor, int Patch, int Revision, string? Pre) Parse(string value) =>
        TryParse(value, out var parsed) ? parsed : (0, 0, 0, 0, null);
}

/// <summary>
/// Download and package safety limits shared by the UI, the downloader and
/// the apply helper. These are integrity/robustness limits, not publisher
/// authentication.
/// </summary>
public static class UpdateDownloadPolicy
{
    public const long MaxPackageBytes = 1L * 1024 * 1024 * 1024;
    public const long MaxChecksumBytes = 64 * 1024;
    public const int MaxArchiveEntries = 20_000;
    public const long MaxUncompressedBytes = 8L * 1024 * 1024 * 1024;
    public const long MinimumFreeSpaceBytes = 64L * 1024 * 1024;

    private static readonly string[] AllowedAssetHostSuffixes =
    [
        "github.com",
        "githubusercontent.com"
    ];

    public static bool TryGetSecureUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)) return false;
        if (!parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(parsed.Host)) return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo)) return false;
        if (IPAddress.TryParse(parsed.Host, out _)) return false;
        if (!IsAllowedAssetHost(parsed.Host)) return false;
        uri = parsed;
        return true;
    }

    public static bool IsAllowedAssetHost(string host)
    {
        var normalized = host.TrimEnd('.').ToLowerInvariant();
        foreach (var suffix in AllowedAssetHostSuffixes)
        {
            if (normalized.Equals(suffix, StringComparison.Ordinal)
                || normalized.EndsWith("." + suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsSafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > 255) return false;
        if (name.IndexOfAny(['/', '\\', ':', '\0']) >= 0) return false;
        if (name is "." or "..") return false;
        return name == Path.GetFileName(name);
    }

    public static long RequiredFreeBytes(long declaredSize)
    {
        if (declaredSize <= 0) return MinimumFreeSpaceBytes;
        var tenPercent = declaredSize / 10;
        return declaredSize + Math.Max(MinimumFreeSpaceBytes, tenPercent);
    }

    public static bool HasSufficientFreeSpace(string directory, long requiredBytes)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return false;
            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace >= requiredBytes;
        }
        catch
        {
            return false;
        }
    }
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
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AMARE", "0.52"));
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new UpdateCheckResult(false, false, currentVersion, null, "Update check was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(false, false, currentVersion, null,
                "Update check timed out. Check your network connection and try again.");
        }
        catch (HttpRequestException exception)
        {
            return new UpdateCheckResult(false, false, currentVersion, null,
                $"Network error while checking for updates: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return new UpdateCheckResult(false, false, currentVersion, null,
                $"Update server returned an invalid response: {exception.Message}");
        }
        catch (Exception exception)
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
        var assets = candidate.Assets
            .Where(asset => UpdateDownloadPolicy.IsSafeFileName(asset.Name)
                && UpdateDownloadPolicy.TryGetSecureUri(asset.DownloadUrl, out _))
            .ToArray();
        var package = assets.FirstOrDefault(asset =>
            asset.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
            ?? assets.FirstOrDefault(asset =>
                asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && !asset.Name.Contains("msix", StringComparison.OrdinalIgnoreCase));
        if (package == null)
        {
            return new UpdateCheckResult(true, true, currentVersion, null,
                "The latest release does not contain a portable Windows ZIP.");
        }
        var checksum = assets.FirstOrDefault(asset =>
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
    private readonly long _maxDownloadBytes;

    public UpdateDownloader(HttpClient? http = null, long maxDownloadBytes = UpdateDownloadPolicy.MaxPackageBytes)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _maxDownloadBytes = maxDownloadBytes > 0 ? maxDownloadBytes : UpdateDownloadPolicy.MaxPackageBytes;
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
        if (!UpdateDownloadPolicy.TryGetSecureUri(update.Package.DownloadUrl, out _))
        {
            return new UpdateDownloadResult(false, null, null, 0,
                "Update package URL must be an HTTPS GitHub release URL.");
        }
        if (!UpdateDownloadPolicy.TryGetSecureUri(update.Checksum.DownloadUrl, out _))
        {
            return new UpdateDownloadResult(false, null, null, 0,
                "Update checksum URL must be an HTTPS GitHub release URL.");
        }
        if (!UpdateDownloadPolicy.IsSafeFileName(update.Package.Name))
        {
            return new UpdateDownloadResult(false, null, null, 0,
                "Update package name is not a safe file name.");
        }
        if (update.Package.Size <= 0)
        {
            return new UpdateDownloadResult(false, null, null, 0,
                "Release does not provide a usable package size.");
        }
        if (update.Package.Size > _maxDownloadBytes)
        {
            return new UpdateDownloadResult(false, null, null, 0,
                $"Update package exceeds the {_maxDownloadBytes:N0} byte download limit.");
        }
        var requiredSpace = UpdateDownloadPolicy.RequiredFreeBytes(update.Package.Size);
        if (!UpdateDownloadPolicy.HasSufficientFreeSpace(destinationDirectory, requiredSpace))
        {
            return new UpdateDownloadResult(false, null, null, 0,
                "Not enough disk space to download and stage the update.");
        }

        string? partPath = null;
        string? finalPath = null;
        try
        {
            var expected = await ReadChecksumAsync(update.Checksum.DownloadUrl, cancellationToken);
            if (expected == null)
            {
                return new UpdateDownloadResult(false, null, null, 0,
                    "Could not read a valid SHA-256 checksum from the release.");
            }
            Directory.CreateDirectory(destinationDirectory);
            finalPath = Path.Combine(destinationDirectory, update.Package.Name);
            partPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".part";
            using var response = await _http.GetAsync(
                update.Package.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } finalUri
                && !UpdateDownloadPolicy.TryGetSecureUri(finalUri.ToString(), out _))
            {
                return new UpdateDownloadResult(false, null, null, 0,
                    "Update download was redirected to a non-HTTPS or untrusted host.");
            }
            if (response.Content.Headers.ContentLength is { } declaredLength
                && (declaredLength > _maxDownloadBytes || declaredLength != update.Package.Size))
            {
                return new UpdateDownloadResult(false, null, null, 0,
                    "Downloaded file size does not match the release metadata.");
            }
            var total = response.Content.Headers.ContentLength ?? update.Package.Size;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(
                partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += read;
                    if (received > _maxDownloadBytes || received > update.Package.Size)
                    {
                        throw new InvalidDataException(
                            $"Update package exceeds the {_maxDownloadBytes:N0} byte download limit or the declared size.");
                    }
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Invoke(received, total);
                }
            }
            var info = new FileInfo(partPath);
            if (info.Length != update.Package.Size)
            {
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
                return new UpdateDownloadResult(false, null, actual, info.Length,
                    "SHA-256 mismatch; downloaded file was deleted.");
            }
            File.Move(partPath, finalPath, overwrite: true);
            partPath = null;
            return new UpdateDownloadResult(true, finalPath, actual, info.Length, null);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(partPath);
            return new UpdateDownloadResult(false, null, null, 0, "Download cancelled.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or FormatException or InvalidDataException or UnauthorizedAccessException)
        {
            TryDeleteFile(partPath);
            return new UpdateDownloadResult(false, null, null, 0, $"Download failed: {exception.Message}");
        }
        finally
        {
            TryDeleteFile(partPath);
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
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } finalUri
            && !UpdateDownloadPolicy.TryGetSecureUri(finalUri.ToString(), out _))
        {
            return null;
        }
        if (response.Content.Headers.ContentLength is { } length
            && length > UpdateDownloadPolicy.MaxChecksumBytes)
        {
            return null;
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var buffer = new char[UpdateDownloadPolicy.MaxChecksumBytes];
        var read = await reader.ReadBlockAsync(buffer, cancellationToken);
        var text = new string(buffer, 0, read);
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 64 && token.All(Uri.IsHexDigit)) return token.ToLowerInvariant();
        }
        return null;
    }
}
