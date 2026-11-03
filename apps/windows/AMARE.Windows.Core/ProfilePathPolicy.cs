namespace Amare.Windows.Core;

public static class ProfilePathPolicy
{
    public static bool IsSafeProfileId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id is "." or "..")
        {
            return false;
        }
        return id.All(ch =>
            char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');
    }

    public static string GetWebViewDataFolder(string appDataRoot, string profileId)
    {
        if (!IsSafeProfileId(profileId))
        {
            throw new ArgumentException("Unsafe profile id.", nameof(profileId));
        }
        var baseRoot = Path.GetFullPath(Path.Combine(appDataRoot, "webview2"));
        var candidate = Path.GetFullPath(Path.Combine(baseRoot, profileId));
        var prefix = baseRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Profile path escapes the WebView2 root.", nameof(profileId));
        }
        return candidate;
    }
}
