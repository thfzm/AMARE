using System.IO.Compression;
using System.Text.Json;

namespace Amare.Windows.Core;

public sealed class UpdatePackageManifest
{
    public int Schema { get; init; }
    public string Product { get; init; } = "";
    public string Version { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string EntryPoint { get; init; } = "";
    public string Runtime { get; init; } = "";
    public string? Channel { get; init; }
    public IReadOnlyList<string>? RequiredFiles { get; init; }
}

public sealed record PackageValidationResult(
    bool Valid,
    UpdatePackageManifest? Manifest,
    string? Error);

public static class PortableUpdatePackage
{
    public const string ManifestName = "amare-update.json";
    public const string ProductName = "AMARE";
    public const int CurrentSchema = 1;
    public const string DefaultArchitecture = "win-x64";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static PackageValidationResult Validate(
        string packagePath,
        string? expectedVersion = null,
        string expectedArchitecture = DefaultArchitecture)
    {
        if (!File.Exists(packagePath))
        {
            return new PackageValidationResult(false, null, "Update package does not exist.");
        }
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry? manifestEntry = null;
            foreach (var entry in archive.Entries)
            {
                var normalized = NormalizeEntryName(entry.FullName);
                if (normalized == null)
                {
                    return new PackageValidationResult(false, null,
                        $"Unsafe package path: {entry.FullName}");
                }
                if (IsSymlink(entry))
                {
                    return new PackageValidationResult(false, null,
                        $"Symbolic links are not allowed: {entry.FullName}");
                }
                var key = normalized.TrimEnd('/');
                if (!names.Add(key))
                {
                    return new PackageValidationResult(false, null,
                        $"Duplicate package entry: {entry.FullName}");
                }
                if (key.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
                {
                    manifestEntry = entry;
                }
            }
            if (manifestEntry == null)
            {
                return new PackageValidationResult(false, null,
                    $"Package manifest {ManifestName} is missing.");
            }
            using var reader = new StreamReader(manifestEntry.Open());
            var manifest = JsonSerializer.Deserialize<UpdatePackageManifest>(reader.ReadToEnd(), JsonOptions);
            if (manifest == null)
            {
                return new PackageValidationResult(false, null, "Package manifest is invalid JSON.");
            }
            if (manifest.Schema != CurrentSchema)
            {
                return new PackageValidationResult(false, null,
                    $"Unsupported package schema: {manifest.Schema}.");
            }
            if (!manifest.Product.Equals(ProductName, StringComparison.Ordinal))
            {
                return new PackageValidationResult(false, null,
                    $"Unexpected package product: {manifest.Product}.");
            }
            if (!SemanticVersion.TryParse(manifest.Version, out _))
            {
                return new PackageValidationResult(false, null,
                    $"Invalid package version: {manifest.Version}.");
            }
            if (expectedVersion != null
                && SemanticVersion.Compare(manifest.Version, expectedVersion) != 0)
            {
                return new PackageValidationResult(false, null,
                    $"Package version {manifest.Version} does not match release {expectedVersion}.");
            }
            if (!NormalizeArchitecture(manifest.Architecture)
                .Equals(NormalizeArchitecture(expectedArchitecture), StringComparison.Ordinal))
            {
                return new PackageValidationResult(false, null,
                    $"Package architecture {manifest.Architecture} does not match {expectedArchitecture}.");
            }
            var entryName = NormalizeEntryName(manifest.EntryPoint);
            if (entryName == null || entryName.EndsWith('/'))
            {
                return new PackageValidationResult(false, null, "Package entry point is unsafe.");
            }
            if (!names.Contains(entryName.TrimEnd('/')))
            {
                return new PackageValidationResult(false, null,
                    $"Package entry point is missing: {manifest.EntryPoint}.");
            }
            foreach (var required in manifest.RequiredFiles ?? [])
            {
                var requiredName = NormalizeEntryName(required);
                if (requiredName == null || !names.Contains(requiredName.TrimEnd('/')))
                {
                    return new PackageValidationResult(false, null,
                        $"Required package file is missing: {required}.");
                }
            }
            return new PackageValidationResult(true, manifest, null);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException
            or UnauthorizedAccessException)
        {
            return new PackageValidationResult(false, null, $"Package validation failed: {exception.Message}");
        }
    }

    public static void ExtractValidated(
        string packagePath,
        string destinationDirectory,
        UpdatePackageManifest manifest)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            var normalized = NormalizeEntryName(entry.FullName)
                ?? throw new InvalidDataException($"Unsafe package path: {entry.FullName}");
            var relative = normalized.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(destinationDirectory, relative));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Package entry escaped destination: {entry.FullName}");
            }
            if (normalized.EndsWith('/') || string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                var mode = (UnixFileMode)((entry.ExternalAttributes >> 16) & 0x1FF);
                if (mode != 0)
                {
                    try { File.SetUnixFileMode(target, mode); } catch { }
                }
            }
        }
        var entryPath = Path.Combine(destinationDirectory,
            manifest.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(entryPath))
        {
            throw new InvalidDataException("Package entry point was not extracted.");
        }
    }

    private static string? NormalizeEntryName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)) return null;
        if (normalized.Length == 0 || normalized.Contains(':')) return null;
        var segments = normalized.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0) continue;
            if (segment is "." or "..") return null;
        }
        if (normalized.StartsWith("../", StringComparison.Ordinal)
            || normalized.Contains("/../", StringComparison.Ordinal))
        {
            return null;
        }
        return normalized;
    }

    private static bool IsSymlink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode == 0xA000;
    }

    private static string NormalizeArchitecture(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "x64" or "amd64" or "win-x64" => "win-x64",
            "arm64" or "aarch64" or "win-arm64" => "win-arm64",
            _ => normalized
        };
    }
}
