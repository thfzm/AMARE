using System.Text;

namespace Amare.Windows.Core;

/// <summary>
/// Per-WebView, one-shot provenance for the local New Tab document.
///
/// WebView2 implements <c>NavigateToString</c> as a <c>data:text/html</c>
/// navigation, and NavigationPolicy intentionally blocks <c>data:</c>. The New
/// Tab exception must never be based on a URL prefix: the pending document is
/// armed immediately before <c>NavigateToString</c>, consumed by the first
/// NavigationStarting event, and accepted only when the navigation payload is
/// byte-for-byte the document AMARE generated for that WebView instance.
/// </summary>
public sealed class InternalDocumentNavigation
{
    private readonly object _gate = new();
    private string? _pendingHtml;

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pendingHtml is not null;
            }
        }
    }

    /// <summary>Arms the next navigation as an AMARE-generated document.</summary>
    public void Arm(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            throw new ArgumentException("Internal document HTML is required.", nameof(html));
        }
        lock (_gate)
        {
            _pendingHtml = html;
        }
    }

    /// <summary>Drops any pending document without accepting a navigation.</summary>
    public void Disarm()
    {
        lock (_gate)
        {
            _pendingHtml = null;
        }
    }

    /// <summary>
    /// Consumes the pending document and reports whether this navigation is
    /// exactly that document. The pending state is cleared even when the check
    /// fails so an exception can never be reused (fail closed).
    /// </summary>
    public bool TryAccept(string? navigationUri)
    {
        string? pending;
        lock (_gate)
        {
            pending = _pendingHtml;
            _pendingHtml = null;
        }
        return NewTabDocumentPolicy.Matches(navigationUri, pending);
    }
}

/// <summary>
/// Validates the exact local document payload. No URL string prefix can qualify
/// a navigation; the decoded payload must equal the expected HTML in full.
/// </summary>
public static class NewTabDocumentPolicy
{
    private static readonly string[] AllowedHeaders =
    [
        "data:text/html",
        "data:text/html;charset=utf-8",
        "data:text/html;base64",
        "data:text/html;charset=utf-8;base64"
    ];

    public static bool Matches(string? navigationUri, string? expectedHtml)
    {
        if (string.IsNullOrEmpty(navigationUri) || string.IsNullOrEmpty(expectedHtml))
        {
            return false;
        }
        if (!navigationUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var separator = navigationUri.IndexOf(',');
        if (separator <= 0 || separator == navigationUri.Length - 1)
        {
            return false;
        }

        var header = navigationUri[..separator];
        if (!AllowedHeaders.Any(allowed => header.Equals(allowed, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var payload = navigationUri[(separator + 1)..];
        // Reject implausible payload sizes before allocating/decoding. The
        // largest supported encoding of a UTF-16 char is percent-escaped UTF-8
        // (12 chars); this is a DoS bound, not a security decision.
        if (payload.Length < expectedHtml.Length
            || payload.Length > (long)expectedHtml.Length * 12 + 256)
        {
            return false;
        }

        if (header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                return string.Equals(decoded, expectedHtml, StringComparison.Ordinal);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        if (string.Equals(payload, expectedHtml, StringComparison.Ordinal))
        {
            return true;
        }
        try
        {
            return string.Equals(Uri.UnescapeDataString(payload), expectedHtml, StringComparison.Ordinal);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }
}
