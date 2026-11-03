namespace Amare.Windows.Core;

public static class DownloadPolicy
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string SanitizeFileName(string? suggested)
    {
        var name = (suggested ?? "").Trim();
        name = name.Replace('\\', '_').Replace('/', '_');
        foreach (var invalid in new[] { ':', '*', '?', '"', '<', '>', '|' })
        {
            name = name.Replace(invalid, '_');
        }
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0)
        {
            name = "download";
        }
        var stem = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(stem))
        {
            name = "_" + name;
        }
        if (name.Length > 180)
        {
            var extension = Path.GetExtension(name);
            var maxStem = Math.Max(1, 180 - extension.Length);
            name = name[..Math.Min(stem.Length, maxStem)] + extension;
        }
        return name.Length == 0 ? "download" : name;
    }

    public static string AllocatePath(string downloadsDirectory, string? suggested, Func<string, bool>? exists = null)
    {
        if (string.IsNullOrWhiteSpace(downloadsDirectory))
        {
            throw new ArgumentException("Downloads directory is required.", nameof(downloadsDirectory));
        }
        exists ??= File.Exists;
        var root = Path.GetFullPath(downloadsDirectory);
        var name = SanitizeFileName(suggested);
        var candidate = Path.GetFullPath(Path.Combine(root, name));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Download path escapes the downloads directory.");
        }
        if (!exists(candidate))
        {
            return candidate;
        }
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 1; index < 10_000; index++)
        {
            candidate = Path.Combine(root, $"{stem} ({index}){extension}");
            if (!exists(candidate))
            {
                return candidate;
            }
        }
        throw new IOException("Could not allocate a unique download file name.");
    }
}
