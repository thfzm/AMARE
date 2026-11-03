namespace Amare.Windows.Core;

public enum NavigationDecision
{
    Allow,
    Block,
    External
}

public static class NavigationPolicy
{
    public static NavigationDecision Decide(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return NavigationDecision.Block;
        }
        return parsed.Scheme.ToLowerInvariant() switch
        {
            "http" or "https" or "about" or "amare" => NavigationDecision.Allow,
            "javascript" or "data" or "vbscript" or "file" => NavigationDecision.Block,
            _ => NavigationDecision.External
        };
    }

    public static bool CanOpenInNewTab(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && (parsed.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase));
}
