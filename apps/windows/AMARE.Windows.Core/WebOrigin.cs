using System.Globalization;

namespace Amare.Windows.Core;

/// <summary>
/// Canonical HTTPS origin used by the Vault/WebView2 boundary. Comparison is
/// exact: scheme, IDN-normalized hostname and port must all match.
/// </summary>
public readonly record struct WebOrigin
{
    public const string RequiredScheme = "https";
    public const int DefaultPort = 443;

    public string Host { get; }
    public int Port { get; }

    private WebOrigin(string host, int port)
    {
        Host = host;
        Port = port;
    }

    public string Value =>
        Port == DefaultPort ? $"https://{FormatHost(Host)}" : $"https://{FormatHost(Host)}:{Port}";

    public override string ToString() => Value;

    public static bool TryParse(string? value, out WebOrigin origin)
    {
        origin = default;
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }
        if (!string.Equals(uri.Scheme, RequiredScheme, StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length != 0)
        {
            return false;
        }

        string host;
        if (uri.HostNameType == UriHostNameType.Dns)
        {
            if (uri.Host.EndsWith('.') || uri.IdnHost.EndsWith('.'))
            {
                return false;
            }
            try
            {
                host = new IdnMapping().GetAscii(uri.IdnHost).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return false;
            }
            if (!IsValidDnsName(host))
            {
                return false;
            }
        }
        else if (uri.HostNameType == UriHostNameType.IPv4)
        {
            host = uri.IdnHost.ToLowerInvariant();
        }
        else if (uri.HostNameType == UriHostNameType.IPv6)
        {
            host = uri.IdnHost.Trim('[', ']').ToLowerInvariant();
        }
        else
        {
            return false;
        }

        var port = uri.IsDefaultPort ? DefaultPort : uri.Port;
        if (port is <= 0 or > 65535 || host.Length == 0)
        {
            return false;
        }
        origin = new WebOrigin(host, port);
        return true;
    }

    public static bool TryCreateActionOrigin(
        string? action,
        WebOrigin documentOrigin,
        out WebOrigin actionOrigin)
    {
        actionOrigin = default;
        if (string.IsNullOrWhiteSpace(action))
        {
            actionOrigin = documentOrigin;
            return true;
        }
        if (!TryParse(action, out var parsed))
        {
            return false;
        }
        actionOrigin = parsed;
        return actionOrigin == documentOrigin;
    }

    /// <summary>
    /// Exact document URL comparison used together with the per-document token.
    /// Origin equality alone is not enough for the frame/document boundary.
    /// </summary>
    public static bool SameDocumentUrl(string? left, string? right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var a)
            || !Uri.TryCreate(right, UriKind.Absolute, out var b))
        {
            return false;
        }
        if (!a.Scheme.Equals(RequiredScheme, StringComparison.OrdinalIgnoreCase)
            || !b.Scheme.Equals(RequiredScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return Uri.Compare(
            a,
            b,
            UriComponents.AbsoluteUri,
            UriFormat.UriEscaped,
            StringComparison.Ordinal) == 0;
    }

    private static string FormatHost(string host) =>
        host.Contains(':') ? $"[{host}]" : host;

    private static bool IsValidDnsName(string host)
    {
        if (host.Length is 0 or > 253)
        {
            return false;
        }
        var labels = host.Split('.');
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63
                || label[0] == '-'
                || label[^1] == '-')
            {
                return false;
            }
            foreach (var ch in label)
            {
                if (!(ch is >= 'a' and <= 'z')
                    && !(ch is >= '0' and <= '9')
                    && ch != '-')
                {
                    return false;
                }
            }
        }
        return true;
    }
}
