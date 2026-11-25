using System.Text.Json;
using Amare.Windows.Core;

namespace Amare.Interop.Tests;

internal static class Phase28Tests
{
    private const string Origin = "https://example.com";
    private const string LoginUrl = "https://example.com/login";
    private const string FormToken = "0123456789abcdef0123456789abcdef";
    private const string Username = "alice";

    public static void Register()
    {
        Program.Run("web_origin_exact_https_and_default_port", ExactOrigin);
        Program.Run("web_origin_scheme_port_host_subdomain_rejected", OriginMismatches);
        Program.Run("web_origin_idn_and_lookalike_normalized", IdnAndLookalike);
        Program.Run("web_origin_userinfo_and_trailing_dot_rejected", OriginRejectsUserinfo);
        Program.Run("web_origin_action_origin_exact", ActionOrigin);
        Program.Run("web_origin_same_document_url_exact", SameDocumentUrl);
        Program.Run("webview_vault_ready_token_generation", ReadyHandshake);
        Program.Run("webview_candidate_binds_source_token_and_generation", CandidateBoundary);
        Program.Run("webview_candidate_rejects_schema_and_duplicate", CandidateSchema);
        Program.Run("webview_candidate_rejects_cross_origin_and_url", CandidateOrigin);
        Program.Run("webview_detect_fill_and_secret_commands", Commands);
        Program.Run("webview_command_replay_and_wrong_op_rejected", CommandReplay);
        Program.Run("webview_command_requires_ready_and_context", CommandContext);
        Program.Run("webview_payload_and_field_limits", Limits);
        Program.Run("webview_script_has_no_auto_submit_or_secret_candidate", ScriptSafety);
    }

    private static string ExactOrigin()
    {
        Assert(WebOrigin.TryParse("https://example.com", out var origin), "parse");
        Assert(WebOrigin.TryParse("https://EXAMPLE.com:443/login", out var same), "parse default port");
        Assert(origin == same, "default port equality");
        Assert(origin.Value == "https://example.com", $"canonical={origin.Value}");
        return origin.Value;
    }

    private static string OriginMismatches()
    {
        Assert(WebOrigin.TryParse("https://example.com", out var origin), "parse");
        Assert(!WebOrigin.TryParse("http://example.com", out _), "scheme");
        Assert(WebOrigin.TryParse("https://example.com:444", out var port) && port != origin, "port");
        Assert(WebOrigin.TryParse("https://www.example.com", out var sub) && sub != origin, "subdomain");
        Assert(!WebOrigin.TryParse("https://example.com.evil.test", out var evil) || evil != origin, "lookalike");
        return "scheme/port/subdomain exact";
    }

    private static string IdnAndLookalike()
    {
        Assert(WebOrigin.TryParse("https://bücher.example", out var unicode), "unicode");
        Assert(WebOrigin.TryParse("https://xn--bcher-kva.example", out var punycode), "punycode");
        Assert(unicode == punycode, "IDN equivalence");
        Assert(!WebOrigin.TryParse("https://exаmple.com", out var cyrillic)
               || cyrillic != OriginOf("https://example.com"), "lookalike");
        return unicode.Value;
    }

    private static string OriginRejectsUserinfo()
    {
        Assert(!WebOrigin.TryParse("https://user:pass@example.com", out _), "userinfo");
        Assert(!WebOrigin.TryParse("https://example.com.", out _), "trailing dot");
        Assert(!WebOrigin.TryParse("https://example.com ", out _), "whitespace");
        return "userinfo/trailing dot/whitespace rejected";
    }

    private static string ActionOrigin()
    {
        var doc = OriginOf(Origin);
        Assert(WebOrigin.TryCreateActionOrigin("", doc, out var empty) && empty == doc, "empty");
        Assert(WebOrigin.TryCreateActionOrigin("https://example.com/login", doc, out var same) && same == doc, "same");
        Assert(!WebOrigin.TryCreateActionOrigin("https://evil.test/login", doc, out _), "cross");
        Assert(!WebOrigin.TryCreateActionOrigin("javascript:alert(1)", doc, out _), "javascript");
        return "action origin exact";
    }

    private static string SameDocumentUrl()
    {
        Assert(WebOrigin.SameDocumentUrl(LoginUrl, LoginUrl), "same");
        Assert(WebOrigin.SameDocumentUrl(LoginUrl + "#a", LoginUrl + "#a"), "fragment");
        Assert(!WebOrigin.SameDocumentUrl(LoginUrl, LoginUrl + "?x=1"), "query");
        Assert(!WebOrigin.SameDocumentUrl(LoginUrl, "https://example.com/other"), "path");
        Assert(!WebOrigin.SameDocumentUrl(LoginUrl, "http://example.com/login"), "scheme");
        return "document URL exact";
    }

    private static string ReadyHandshake()
    {
        var state = NewState(out var token, out var generation);
        var ready = ReadyJson(token, generation);
        Assert(state.TryAcceptReady(ready, LoginUrl, out var readyToken), "ready");
        Assert(readyToken == token, "ready token");
        Assert(state.Ready, "ready state");
        Assert(!state.TryAcceptReady(ready, LoginUrl, out _), "replayed ready");
        return "ready handshake";
    }

    private static string CandidateBoundary()
    {
        var state = NewState(out var token, out var generation);
        AcceptReady(state, token, generation);
        var json = CandidateJson(token, generation, NewMsg(), Origin, Origin, FormToken, Username, false);
        Assert(state.TryAcceptLoginCandidate(json, LoginUrl, out var candidate), "candidate");
        Assert(candidate.Generation == generation && candidate.Origin == OriginOf(Origin), "candidate fields");
        Assert(!state.TryAcceptLoginCandidate(json, LoginUrl, out _), "duplicate message");
        Assert(!state.TryAcceptLoginCandidate(
            CandidateJson(token, generation, NewMsg(), Origin, Origin, FormToken, Username, false),
            LoginUrl,
            out _), "duplicate candidate key");
        state.OnNavigationStarting();
        Assert(!state.TryAcceptLoginCandidate(json, LoginUrl, out _), "stale");
        return "source/token/generation/duplicate enforced";
    }

    private static string CandidateSchema()
    {
        var state = NewState(out var token, out var generation);
        AcceptReady(state, token, generation);
        var withPassword = WithExtra(
            CandidateJson(token, generation, NewMsg(), Origin, Origin, FormToken, Username, false),
            "password", "leak");
        Assert(!state.TryAcceptLoginCandidate(withPassword, LoginUrl, out _), "unexpected field");
        Assert(!state.TryAcceptLoginCandidate(
            CandidateJson("ffffffffffffffffffffffffffffffff", generation, NewMsg(),
                Origin, Origin, FormToken, Username, false), LoginUrl, out _), "token");
        Assert(!state.TryAcceptLoginCandidate(
            CandidateJson(token, generation + 1, NewMsg(), Origin, Origin, FormToken, Username, false),
            LoginUrl, out _), "generation");
        Assert(!state.TryAcceptLoginCandidate(
            CandidateJson(token, generation, NewMsg(), Origin, Origin, FormToken, Username, true),
            LoginUrl, out _), "ambiguous");
        Assert(!state.TryAcceptLoginCandidate(
            CandidateJson(token, generation, NewMsg(), Origin, Origin, "short", Username, false),
            LoginUrl, out _), "form token");
        return "schema/token/generation/ambiguity rejected";
    }

    private static string CandidateOrigin()
    {
        var state = NewState(out var token, out var generation);
        AcceptReady(state, token, generation);
        Assert(!state.TryAcceptLoginCandidate(
            CandidateJson(token, generation, NewMsg(), Origin, "https://evil.test/login",
                FormToken, Username, false), LoginUrl, out _), "cross action");
        Assert(!state.TryAcceptLoginCandidate(
            CandidateJson(token, generation, NewMsg(), "https://example.com.evil.test", Origin,
                FormToken, Username, false), LoginUrl, out _), "origin");
        Assert(!state.TryAcceptLoginCandidate(
            CandidateJson(token, generation, NewMsg(), Origin, Origin, FormToken, Username, false),
            "https://example.com/other", out _), "different document URL");
        Assert(!state.TryAcceptLoginCandidate(
            CandidateJson(token, generation, NewMsg(), Origin, Origin, FormToken, Username, false),
            "https://example.com.evil.test/login", out _), "source origin");
        Assert(!state.TryAcceptLoginCandidate(
            CandidateJson(token, generation, NewMsg(), Origin, Origin, FormToken, Username, false),
            LoginUrl + "#fragment", out _), "different document URL with fragment");
        return "cross-origin/document URL rejected";
    }

    private static string Commands()
    {
        var state = NewState(out var token, out var generation);
        AcceptReady(state, token, generation);
        Assert(state.TryBeginCommand(WebViewVaultCommandKind.Detect, null, out var detectOp), "detect begin");
        Assert(state.TryAcceptDetectResult(
            DetectJson(token, generation, NewMsg(), detectOp, Origin, Origin, FormToken, Username, false),
            LoginUrl, detectOp, out var descriptor), "detect result");
        Assert(!descriptor.Ambiguous && descriptor.Generation == generation, "descriptor");
        Assert(state.TryBeginCommand(WebViewVaultCommandKind.ReadSecret, FormToken, out var secretOp), "secret begin");
        Assert(state.TryAcceptSecret(
            SecretJson(token, generation, NewMsg(), secretOp, FormToken, Username, "s3cret"),
            LoginUrl, secretOp, out var secret), "secret result");
        Assert(secret.Username == Username && secret.Password == "s3cret", "secret fields");
        secret.Clear();
        Assert(secret.Password == "", "secret clear");
        Assert(state.TryBeginCommand(WebViewVaultCommandKind.Fill, FormToken, out var fillOp), "fill begin");
        Assert(state.TryAcceptFillResult(
            FillJson(token, generation, NewMsg(), fillOp, true), LoginUrl, fillOp, out var ok), "fill result");
        Assert(ok, "fill ok");
        return "detect/secret/fill commands";
    }

    private static string CommandReplay()
    {
        var state = NewState(out var token, out var generation);
        AcceptReady(state, token, generation);
        Assert(state.TryBeginCommand(WebViewVaultCommandKind.Fill, FormToken, out var op), "begin");
        var result = FillJson(token, generation, NewMsg(), op, true);
        Assert(state.TryAcceptFillResult(result, LoginUrl, op, out _), "first");
        Assert(!state.TryAcceptFillResult(result, LoginUrl, op, out _), "replay");
        Assert(state.TryBeginCommand(WebViewVaultCommandKind.Detect, null, out _), "detect");
        const string wrongOp = "ffffffffffffffffffffffffffffffff";
        Assert(!state.TryAcceptDetectResult(
            DetectJson(token, generation, NewMsg(), wrongOp,
                Origin, Origin, FormToken, Username, false), LoginUrl, wrongOp, out _), "wrong op");
        return "replay/wrong op rejected";
    }

    private static string CommandContext()
    {
        var state = new WebViewVaultTabState();
        state.SetContext("profile-a", 3, "webview-a");
        Assert(state.IsContextCurrent("profile-a", 3), "context");
        Assert(!state.IsContextCurrent("profile-b", 3), "profile mismatch");
        Assert(!state.IsContextCurrent("profile-a", 4), "generation mismatch");
        state.OnNavigationStarting();
        Assert(!state.TryBeginCommand(WebViewVaultCommandKind.Detect, null, out _), "not ready");
        state.SetContext("profile-b", 1, "webview-b");
        Assert(!state.IsContextCurrent("profile-a", 3), "changed context");
        return "context/ready required";
    }

    private static string Limits()
    {
        var state = NewState(out var token, out var generation);
        AcceptReady(state, token, generation);
        var huge = new string('a', WebViewVaultTabState.MaxMessageBytes + 1);
        Assert(!state.TryAcceptLoginCandidate(
            CandidateJson(token, generation, NewMsg(), Origin, Origin, FormToken, huge, false),
            LoginUrl, out _), "message size");
        Assert(!state.TryAcceptLoginCandidate(
            CandidateJson(token, generation, NewMsg(), Origin, Origin, FormToken,
                new string('b', WebViewVaultTabState.MaxUsernameLength + 1), false),
            LoginUrl, out _), "username limit");
        return "payload/field limits";
    }

    private static string ScriptSafety()
    {
        var script = WebViewVaultScript.Script;
        Assert(script.Contains("window.top !== window", StringComparison.Ordinal), "top-level guard");
        Assert(script.Contains("amare_init", StringComparison.Ordinal), "init");
        Assert(script.Contains("amare_read_secret", StringComparison.Ordinal), "read secret");
        Assert(script.Contains("amare_fill_result", StringComparison.Ordinal), "fill ack");
        Assert(!script.Contains("form.submit(", StringComparison.Ordinal), "submit");
        Assert(!script.Contains("requestSubmit(", StringComparison.Ordinal), "requestSubmit");
        Assert(!script.Contains(".click(", StringComparison.Ordinal), "click");
        Assert(!script.Contains("KeyboardEvent", StringComparison.Ordinal), "keyboard event");
        Assert(!script.Contains("keydown", StringComparison.Ordinal), "keydown");
        Assert(!script.Contains("window.__amareVault", StringComparison.Ordinal), "global object");
        var candidateIndex = script.IndexOf("amare_login_candidate", StringComparison.Ordinal);
        Assert(candidateIndex >= 0, "candidate message");
        var candidateBlock = script.Substring(candidateIndex, Math.Min(420, script.Length - candidateIndex));
        Assert(!candidateBlock.Contains("password", StringComparison.OrdinalIgnoreCase), "candidate password leak");
        return "no auto submit/click/keyboard/password candidate";
    }

    private static WebViewVaultTabState NewState(out string token, out long generation)
    {
        var state = new WebViewVaultTabState();
        state.SetContext("profile-a", 1, "webview-a");
        state.OnNavigationStarting();
        Assert(state.TryBeginDocument(OriginOf(Origin), LoginUrl, out token), "begin document");
        generation = state.Generation;
        return state;
    }

    private static void AcceptReady(WebViewVaultTabState state, string token, long generation)
    {
        Assert(state.TryAcceptReady(ReadyJson(token, generation), LoginUrl, out _), "ready");
        Assert(state.Ready, "is ready");
    }

    private static string ReadyJson(string token, long generation) =>
        JsonSerializer.Serialize(new
        {
            type = "amare_ready", version = 1, generation, token, msgId = NewMsg()
        });

    private static string CandidateJson(
        string token, long generation, string msgId, string origin, string action,
        string formToken, string username, bool ambiguous) =>
        JsonSerializer.Serialize(new
        {
            type = "amare_login_candidate", version = 1, generation, token, msgId,
            origin, actionOrigin = action, formToken, username, ambiguous
        });

    private static string DetectJson(
        string token, long generation, string msgId, string opId, string origin,
        string action, string formToken, string username, bool ambiguous) =>
        JsonSerializer.Serialize(new
        {
            type = "amare_detect_result", version = 1, generation, token, msgId, opId,
            ok = true, origin, actionOrigin = action, formToken, username, ambiguous,
            hasPassword = true, hasUsername = true
        });

    private static string SecretJson(
        string token, long generation, string msgId, string opId,
        string formToken, string username, string password) =>
        JsonSerializer.Serialize(new
        {
            type = "amare_secret", version = 1, generation, token, msgId, opId,
            formToken, username, password
        });

    private static string FillJson(
        string token, long generation, string msgId, string opId, bool ok) =>
        JsonSerializer.Serialize(new
        {
            type = "amare_fill_result", version = 1, generation, token, msgId, opId, ok
        });

    private static string WithExtra(string json, string name, string value) =>
        json[..^1] + $",\"{name}\":\"{value}\"}}";

    private static string NewMsg() => Guid.NewGuid().ToString("N");

    private static WebOrigin OriginOf(string value)
    {
        if (!WebOrigin.TryParse(value, out var origin))
        {
            throw new InvalidOperationException($"origin parse failed: {value}");
        }
        return origin;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
