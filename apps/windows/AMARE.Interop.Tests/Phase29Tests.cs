using System.Text;
using Amare.Windows.Core;

namespace Amare.Interop.Tests;

internal static class Phase29Tests
{
    public static void Register()
    {
        Program.Run("new_tab_document_policy_accepts_exact_payload", AcceptsExactPayload);
        Program.Run("new_tab_document_policy_rejects_foreign_payloads", RejectsForeignPayloads);
        Program.Run("internal_document_navigation_is_one_shot", OneShot);
        Program.Run("navigation_policy_keeps_data_family_blocked", DataFamilyBlocked);
    }

    private static string AcceptsExactPayload()
    {
        var html = NewTabPageBuilder.Build([], "DuckDuckGo", new string('a', 32));
        Assert(NewTabDocumentPolicy.Matches($"data:text/html;charset=utf-8,{html}", html), "raw");
        Assert(NewTabDocumentPolicy.Matches($"data:text/html,{html}", html), "raw no charset");
        Assert(NewTabDocumentPolicy.Matches(
            $"data:text/html;charset=utf-8,{Uri.EscapeDataString(html)}", html), "percent");
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
        Assert(NewTabDocumentPolicy.Matches($"data:text/html;charset=utf-8;base64,{base64}", html), "base64");
        Assert(NewTabDocumentPolicy.Matches($"DATA:TEXT/HTML;CHARSET=UTF-8,{html}", html), "case");
        return "exact raw/escaped/base64 payloads accepted";
    }

    private static string RejectsForeignPayloads()
    {
        var html = NewTabPageBuilder.Build([], "DuckDuckGo", new string('a', 32));
        AssertFalse($"data:text/html;charset=utf-8,{html}<script>x</script>", html, "appended");
        AssertFalse($"data:text/html;charset=utf-8,{html[..100]}", html, "truncated");
        AssertFalse("data:text/html;charset=utf-8,<h1>external</h1>", html, "other html");
        AssertFalse("data:image/png;base64,iVBORw0KGgo=", html, "image");
        AssertFalse("data:text/plain;charset=utf-8,hello", html, "plain");
        AssertFalse("data:text/html;charset=utf-8;foo=bar," + html, html, "unknown parameter");
        AssertFalse("data:text/html;charset=utf-8;base64,%%%", html, "invalid base64");
        AssertFalse("javascript:alert(1)", html, "javascript");
        AssertFalse("file:///etc/passwd", html, "file");
        AssertFalse("blob:https://example.com/id", html, "blob");
        AssertFalse("https://example.com/", html, "https");
        AssertFalse(null, html, "null uri");
        AssertFalse("data:text/html;charset=utf-8," + html, null, "null expected");
        AssertFalse("data:text/html", html, "no payload");
        AssertFalse("data:text/html;charset=utf-8," + html + " ", html, "trailing space");
        return "foreign/prefix-only payloads rejected";
    }

    private static string OneShot()
    {
        var html = NewTabPageBuilder.Build([], "DuckDuckGo", new string('b', 32));
        var navigation = new InternalDocumentNavigation();
        Assert(!navigation.HasPending, "initially empty");

        navigation.Arm(html);
        Assert(navigation.HasPending, "armed");
        Assert(navigation.TryAccept($"data:text/html;charset=utf-8,{html}"), "first accepted");
        Assert(!navigation.HasPending, "consumed");
        Assert(!navigation.TryAccept($"data:text/html;charset=utf-8,{html}"), "second rejected");

        navigation.Arm(html);
        Assert(!navigation.TryAccept("data:text/html;charset=utf-8,<h1>other</h1>"), "foreign rejected");
        Assert(!navigation.TryAccept($"data:text/html;charset=utf-8,{html}"), "consumed after failure");

        navigation.Arm(html);
        navigation.Disarm();
        Assert(!navigation.TryAccept($"data:text/html;charset=utf-8,{html}"), "disarmed");

        var threw = false;
        try
        {
            navigation.Arm("");
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        Assert(threw, "empty arm rejected");
        return "per-instance provenance is one-shot and fail-closed";
    }

    private static string DataFamilyBlocked()
    {
        Assert(NavigationPolicy.Decide("data:text/html,<h1>x</h1>") == NavigationDecision.Block, "data");
        Assert(NavigationPolicy.Decide("javascript:alert(1)") == NavigationDecision.Block, "javascript");
        Assert(NavigationPolicy.Decide("vbscript:msgbox(1)") == NavigationDecision.Block, "vbscript");
        Assert(NavigationPolicy.Decide("file:///etc/passwd") == NavigationDecision.Block, "file");
        Assert(NavigationPolicy.Decide("blob:https://example.com/id") == NavigationDecision.External, "blob unchanged");
        Assert(NavigationPolicy.Decide("https://example.com/") == NavigationDecision.Allow, "https");
        Assert(!NavigationPolicy.CanOpenInNewTab("data:text/html,x"), "data new tab");
        Assert(!NavigationPolicy.CanOpenInNewTab("blob:https://example.com/id"), "blob new tab");
        return "general data-family policy unchanged";
    }

    private static void AssertFalse(string? uri, string? html, string name)
    {
        if (NewTabDocumentPolicy.Matches(uri, html))
        {
            throw new InvalidOperationException($"unexpected acceptance: {name}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
