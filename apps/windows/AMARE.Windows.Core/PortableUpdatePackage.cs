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
    public const string DefaultEntryPoint = "AMARE.Windows.exe";
    public const long MaxManifestBytes = 1L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Portable application packages contain DLLs, resources and data files.
    // Script/host files and installers are never part of a portable update.
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".msi", ".msp", ".mst", ".scr", ".pif", ".com",
        ".cpl", ".jar", ".reg", ".lnk", ".url", ".iso", ".img", ".vhd", ".vhdx",
        ".appx", ".msix", ".appxbundle", ".msixbundle"
    };

    private static readonly HashSet<string> DangerousExecutableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe",
        "mshta.exe", "rundll32.exe", "regsvr32.exe", "regedit.exe", "msiexec.exe",
        "certutil.exe", "bitsadmin.exe", "schtasks.exe", "wmic.exe", "forfiles.exe"
    };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static PackageValidationResult Validate(
        string packagePath,
        string? expectedVersion = null,
        string expectedArchitecture = DefaultArchitecture,
        string expectedEntryPoint = DefaultEntryPoint)
    {
        if (!File.Exists(packagePath))
        {
            return new PackageValidationResult(false, null, "Update package does not exist.");
        }
        try
        {
            var packageInfo = new FileInfo(packagePath);
            if (packageInfo.Length <= 0 || packageInfo.Length > UpdateDownloadPolicy.MaxPackageBytes)
            {
                return new PackageValidationResult(false, null,
                    "Update package size is outside the allowed range.");
            }
            using var archive = ZipFile.OpenRead(packagePath);
            if (archive.Entries.Count == 0
                || archive.Entries.Count > UpdateDownloadPolicy.MaxArchiveEntries)
            {
                return new PackageValidationResult(false, null,
                    "Update package contains an unsupported number of entries.");
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry? manifestEntry = null;
            long totalUncompressed = 0;
            foreach (var entry in archive.Entries)
            {
                var normalized = NormalizeEntryName(entry.FullName);
                if (normalized == null)
                {
                    return new PackageValidationResult(false, null,
                        $"Unsafe package path: {entry.FullName}");
                }
                if (!IsRegularFileOrDirectory(entry))
                {
                    return new PackageValidationResult(false, null,
                        $"Unsupported package entry type: {entry.FullName}");
                }
                totalUncompressed += Math.Max(0, entry.Length);
                if (totalUncompressed > UpdateDownloadPolicy.MaxUncompressedBytes)
                {
                    return new PackageValidationResult(false, null,
                        "Update package exceeds the uncompressed size limit.");
                }
                var key = normalized.TrimEnd('/');
                if (!names.Add(key))
                {
                    return new PackageValidationResult(false, null,
                        $"Duplicate package entry: {entry.FullName}");
                }
                var policyError = CheckEntryPolicy(key);
                if (policyError != null)
                {
                    return new PackageValidationResult(false, null, policyError);
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
            if (manifestEntry.Length > MaxManifestBytes)
            {
                return new PackageValidationResult(false, null,
                    "Package manifest is too large.");
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
            if (string.IsNullOrWhiteSpace(manifest.Runtime)
                || string.IsNullOrWhiteSpace(manifest.Architecture)
                || string.IsNullOrWhiteSpace(manifest.EntryPoint))
            {
                return new PackageValidationResult(false, null,
                    "Package manifest is missing required fields.");
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
            if (entryName.Contains('/'))
            {
                return new PackageValidationResult(false, null,
                    "Package entry point must be at the package root.");
            }
            var expectedEntryName = NormalizeEntryName(expectedEntryPoint);
            if (expectedEntryName == null
                || !entryName.Equals(expectedEntryName, StringComparison.OrdinalIgnoreCase))
            {
                return new PackageValidationResult(false, null,
                    $"Package entry point {manifest.EntryPoint} does not match {expectedEntryPoint}.");
            }
            if (!names.Contains(entryName))
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
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalUncompressed = 0;
        using var archive = ZipFile.OpenRead(packagePath);
        if (archive.Entries.Count == 0
            || archive.Entries.Count > UpdateDownloadPolicy.MaxArchiveEntries)
        {
            throw new InvalidDataException("Package contains an unsupported number of entries.");
        }
        foreach (var entry in archive.Entries)
        {
            var normalized = NormalizeEntryName(entry.FullName)
                ?? throw new InvalidDataException($"Unsafe package path: {entry.FullName}");
            if (!IsRegularFileOrDirectory(entry))
            {
                throw new InvalidDataException($"Unsupported package entry type: {entry.FullName}");
            }
            totalUncompressed += Math.Max(0, entry.Length);
            if (totalUncompressed > UpdateDownloadPolicy.MaxUncompressedBytes)
            {
                throw new InvalidDataException("Package exceeds the uncompressed size limit.");
            }
            var key = normalized.TrimEnd('/');
            if (!names.Add(key))
            {
                throw new InvalidDataException($"Duplicate package entry: {entry.FullName}");
            }
            var policyError = CheckEntryPolicy(key);
            if (policyError != null)
            {
                throw new InvalidDataException(policyError);
            }
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

    private static string? CheckEntryPolicy(string relativePath)
    {
        var segments = relativePath.Split('/');
        var fileName = segments[^1];
        if (fileName.Length == 0) return null;
        var extension = Path.GetExtension(fileName);
        if (DangerousExtensions.Contains(extension))
        {
            return $"Dangerous package file type is not allowed: {relativePath}";
        }
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length != 1)
            {
                return $"Unexpected executable location in package: {relativePath}";
            }
            if (DangerousExecutableNames.Contains(fileName))
            {
                return $"Dangerous executable is not allowed in a portable package: {relativePath}";
            }
        }
        return null;
    }

    private static string? NormalizeEntryName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 1024) return null;
        var normalized = value.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)) return null;
        if (normalized.Length == 0 || normalized.Contains(':')) return null;
        if (normalized.Any(ch => ch < 0x20)) return null;
        var segments = normalized.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (segment.Length == 0)
            {
                // Only a single trailing slash (directory marker) is allowed.
                if (index == segments.Length - 1) continue;
                return null;
            }
            if (segment is "." or "..") return null;
            if (segment.EndsWith(' ') || segment.EndsWith('.')) return null;
            var stem = segment.Split('.')[0];
            if (ReservedDeviceNames.Contains(stem)) return null;
        }
        if (normalized.StartsWith("../", StringComparison.Ordinal)
            || normalized.Contains("/../", StringComparison.Ordinal))
        {
            return null;
        }
        return normalized;
    }

    private static bool IsRegularFileOrDirectory(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode is 0 or 0x8000 or 0x4000;
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
