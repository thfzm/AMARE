namespace Amare.Windows.Core;

public enum AddressInputKind
{
    Empty,
    Url,
    Search
}

public sealed record AddressResolution(AddressInputKind Kind, string Target)
{
    public bool IsNavigation => Kind != AddressInputKind.Empty;
}

public static class AddressBarResolver
{
    public static AddressResolution Resolve(string? input, string? searchEngineId)
    {
        var value = input?.Trim() ?? "";
        if (value.Length == 0)
        {
            return new AddressResolution(AddressInputKind.Empty, "");
        }
        if (value.Any(char.IsWhiteSpace))
        {
            return Search(value, searchEngineId);
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && IsAddressScheme(absolute.Scheme))
        {
            return new AddressResolution(AddressInputKind.Url, absolute.AbsoluteUri);
        }

        if (LooksLikeHost(value))
        {
            var scheme = IsLocalHost(value) ? "http://" : "https://";
            if (Uri.TryCreate(scheme + value, UriKind.Absolute, out var uri))
            {
                return new AddressResolution(AddressInputKind.Url, uri.AbsoluteUri);
            }
        }

        return Search(value, searchEngineId);
    }

    private static AddressResolution Search(string value, string? searchEngineId) =>
        new(AddressInputKind.Search, SearchEngineCatalog.BuildSearchUrl(searchEngineId, value));

    private static bool IsAddressScheme(string scheme) =>
        scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
        || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
        || scheme.Equals("about", StringComparison.OrdinalIgnoreCase)
        || scheme.Equals("amare", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeHost(string value)
    {
        var hostPart = value.Split('/', 2)[0];
        if (hostPart.Length == 0)
        {
            return false;
        }
        if (hostPart.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (hostPart.StartsWith("[", StringComparison.Ordinal) && hostPart.Contains(']'))
        {
            return true;
        }
        if (Uri.CheckHostName(hostPart.Split(':', 2)[0]) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return hostPart.Contains('.') || hostPart.Contains(':')
                || hostPart.StartsWith("[", StringComparison.Ordinal);
        }
        return false;
    }

    private static bool IsLocalHost(string value) =>
        value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("127.", StringComparison.Ordinal)
        || value.StartsWith("[::1]", StringComparison.Ordinal);
}
