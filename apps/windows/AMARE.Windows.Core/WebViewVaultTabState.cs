using System.Text;
using System.Text.Json;

namespace Amare.Windows.Core;

public sealed record WebViewVaultFormDescriptor(
    WebOrigin DocumentOrigin,
    string DocumentUrl,
    WebOrigin ActionOrigin,
    string FormToken,
    string Username,
    bool Ambiguous,
    long Generation);

public sealed record WebViewVaultLoginCandidate(
    WebOrigin Origin,
    string DocumentUrl,
    WebOrigin ActionOrigin,
    string FormToken,
    string Username,
    long Generation);

/// <summary>
/// Short-lived username/password read from the approved page field. C# strings
/// cannot be reliably zeroized; <see cref="Clear"/> is best-effort.
/// </summary>
public sealed class WebViewVaultSecret
{
    public WebViewVaultSecret(string username, string password)
    {
        Username = username;
        Password = password;
    }

    public string Username { get; }

    public string Password { get; private set; }

    public void Clear() => Password = string.Empty;
}

public enum WebViewVaultCommandKind
{
    Detect,
    ReadSecret,
    Fill
}

/// <summary>
/// Per-WebView trust state for the Vault/WebView2 bridge. It never stores a
/// password except inside a short-lived pending read result, and it binds every
/// message to the WebView instance, profile/Vault context, document URL,
/// native-issued document token, generation, and a one-use operation id.
/// </summary>
public sealed class WebViewVaultTabState
{
    public const int MaxMessageBytes = 16 * 1024;
    public const int MaxUsernameLength = 256;
    public const int MaxPasswordLength = 4096;
    public const int MaxTokenLength = 128;
    public const int MaxSeenMessages = 256;

    private readonly object _gate = new();
    private readonly HashSet<string> _candidateKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenMessages = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private PendingCommand? _pendingCommand;

    public long Generation { get; private set; }
    public WebOrigin? DocumentOrigin { get; private set; }
    public string? DocumentUrl { get; private set; }
    public string? DocumentToken { get; private set; }
    public bool Ready { get; private set; }
    public string WebViewId { get; private set; } = "";
    public string ProfileId { get; private set; } = "";
    public long VaultGeneration { get; private set; }

    public void SetContext(string profileId, long vaultGeneration, string webViewId)
    {
        lock (_gate)
        {
            ProfileId = profileId;
            VaultGeneration = vaultGeneration;
            WebViewId = webViewId;
        }
    }

    public bool IsContextCurrent(string profileId, long vaultGeneration)
    {
        lock (_gate)
        {
            return string.Equals(ProfileId, profileId, StringComparison.Ordinal)
                   && VaultGeneration == vaultGeneration;
        }
    }

    public bool IsCurrent(long generation, WebOrigin origin, string? documentUrl)
    {
        lock (_gate)
        {
            return Ready
                   && Generation == generation
                   && DocumentOrigin is { } current
                   && current == origin
                   && DocumentUrl is not null
                   && WebOrigin.SameDocumentUrl(DocumentUrl, documentUrl)
                   && DocumentToken is not null;
        }
    }

    public void OnNavigationStarting()
    {
        lock (_gate)
        {
            Generation++;
            DocumentOrigin = null;
            DocumentUrl = null;
            DocumentToken = null;
            Ready = false;
            ClearOperationsLocked();
        }
    }

    public bool TryBeginDocument(WebOrigin origin, string? documentUrl, out string token)
    {
        token = "";
        if (!WebOrigin.TryParse(documentUrl, out var parsed) || parsed != origin)
        {
            return false;
        }
        lock (_gate)
        {
            DocumentOrigin = origin;
            DocumentUrl = documentUrl;
            DocumentToken = RandomToken();
            Ready = false;
            ClearOperationsLocked();
            token = DocumentToken;
            return true;
        }
    }

    public void OnSameDocumentUrl(string? documentUrl)
    {
        lock (_gate)
        {
            if (!WebOrigin.TryParse(documentUrl, out var origin)
                || DocumentOrigin is not { } current
                || current != origin)
            {
                return;
            }
            DocumentUrl = documentUrl;
        }
    }

    public void OnDocumentFailed()
    {
        lock (_gate)
        {
            DocumentOrigin = null;
            DocumentUrl = null;
            DocumentToken = null;
            Ready = false;
            ClearOperationsLocked();
        }
    }

    public void InvalidateOperations()
    {
        lock (_gate)
        {
            ClearOperationsLocked();
        }
    }

    public void OnTabClosed()
    {
        lock (_gate)
        {
            Generation++;
            DocumentOrigin = null;
            DocumentUrl = null;
            DocumentToken = null;
            Ready = false;
            ClearOperationsLocked();
        }
    }

    public string? CurrentToken
    {
        get
        {
            lock (_gate)
            {
                return DocumentToken;
            }
        }
    }

    public bool TryAcceptReady(string? json, string? source, out string token)
    {
        token = "";
        if (!TryParseJson(json))
        {
            return false;
        }
        lock (_gate)
        {
            if (!TrySource(source)
                || !TryReadEnvelope(
                    json!,
                    out var root,
                    out var generation,
                    out var messageToken,
                    out var messageId,
                    "amare_ready"))
            {
                return false;
            }
            if (!HasExactProperties(root, "type", "version", "generation", "token", "msgId")
                || generation != Generation
                || !TokenEquals(messageToken)
                || !MarkMessageLocked(messageId))
            {
                return false;
            }
            Ready = true;
            token = messageToken;
            return true;
        }
    }

    public bool TryAcceptLoginCandidate(
        string? json,
        string? source,
        out WebViewVaultLoginCandidate candidate)
    {
        candidate = default!;
        if (!TryParseJson(json))
        {
            return false;
        }
        lock (_gate)
        {
            if (!TrySource(source)
                || !TryReadEnvelope(
                    json!,
                    out var root,
                    out var generation,
                    out var messageToken,
                    out var messageId,
                    "amare_login_candidate")
                || !HasExactProperties(
                    root,
                    "type", "version", "generation", "token", "msgId",
                    "origin", "actionOrigin", "formToken", "username", "ambiguous"))
            {
                return false;
            }
            if (!Ready
                || generation != Generation
                || !TokenEquals(messageToken)
                || !MarkMessageLocked(messageId)
                || !TryString(root, "origin", out var originText)
                || !TryString(root, "actionOrigin", out var actionText)
                || !TryString(root, "formToken", out var formToken)
                || !TryString(root, "username", out var username)
                || !TryBool(root, "ambiguous", out var ambiguous)
                || ambiguous
                || !IsSafeToken(formToken)
                || !IsSafeUsername(username)
                || !WebOrigin.TryParse(originText, out var origin)
                || DocumentOrigin is not { } doc
                || origin != doc
                || !WebOrigin.TryCreateActionOrigin(actionText, doc, out var action)
                || action != doc)
            {
                return false;
            }
            var key = $"{origin.Value}|{formToken}|{username}";
            if (!_candidateKeys.Add(key))
            {
                return false;
            }
            candidate = new WebViewVaultLoginCandidate(
                origin, DocumentUrl ?? "", action, formToken, username, Generation);
            return true;
        }
    }

    public bool TryBeginCommand(
        WebViewVaultCommandKind kind,
        string? formToken,
        out string operationId)
    {
        operationId = "";
        lock (_gate)
        {
            if (!Ready
                || DocumentOrigin is not { }
                || DocumentToken is null
                || _pendingCommand is not null)
            {
                return false;
            }
            if (kind is WebViewVaultCommandKind.ReadSecret or WebViewVaultCommandKind.Fill
                && !IsSafeToken(formToken))
            {
                return false;
            }
            operationId = RandomToken();
            _pendingCommand = new PendingCommand(
                operationId,
                kind,
                Generation,
                DocumentToken,
                formToken ?? "",
                DocumentOrigin.Value,
                DocumentUrl ?? "");
            return true;
        }
    }

    public void CancelPendingCommand()
    {
        lock (_gate)
        {
            _pendingCommand = null;
        }
    }

    public bool TryAcceptDetectResult(
        string? json,
        string? source,
        string operationId,
        out WebViewVaultFormDescriptor descriptor)
    {
        descriptor = default!;
        if (!TryParseJson(json))
        {
            return false;
        }
        lock (_gate)
        {
            if (!TrySource(source)
                || !TryReadEnvelope(
                    json!,
                    out var root,
                    out var generation,
                    out var messageToken,
                    out var messageId,
                    "amare_detect_result")
                || !HasExactProperties(
                    root,
                    "type", "version", "generation", "token", "msgId", "opId",
                    "ok", "origin", "actionOrigin", "formToken", "username",
                    "ambiguous", "hasPassword", "hasUsername")
                || !TryString(root, "opId", out var messageOperationId)
                || !string.Equals(messageOperationId, operationId, StringComparison.Ordinal)
                || !MatchesPendingLocked(
                    operationId, WebViewVaultCommandKind.Detect, messageToken, generation)
                || !MarkMessageLocked(messageId))
            {
                return false;
            }
            _pendingCommand = null;
            if (!TryBool(root, "ok", out var ok)
                || !TryBool(root, "hasPassword", out var hasPassword)
                || !TryBool(root, "hasUsername", out var hasUsername)
                || !TryBool(root, "ambiguous", out var ambiguous)
                || !TryString(root, "origin", out var originText)
                || !TryString(root, "actionOrigin", out var actionText)
                || !TryString(root, "formToken", out var formToken)
                || !TryString(root, "username", out var username)
                || !ok
                || !hasPassword
                || !WebOrigin.TryParse(originText, out var origin)
                || DocumentOrigin is not { } doc
                || origin != doc
                || !WebOrigin.TryCreateActionOrigin(actionText, doc, out var action)
                || action != doc
                || !IsSafeToken(formToken))
            {
                return false;
            }
            var ambiguousResult = ambiguous || !hasUsername || string.IsNullOrEmpty(username);
            if (!ambiguousResult && !IsSafeUsername(username))
            {
                return false;
            }
            descriptor = new WebViewVaultFormDescriptor(
                doc,
                DocumentUrl ?? "",
                action,
                formToken,
                username,
                ambiguousResult,
                Generation);
            return true;
        }
    }

    public bool TryAcceptSecret(
        string? json,
        string? source,
        string operationId,
        out WebViewVaultSecret secret)
    {
        secret = null!;
        if (!TryParseJson(json))
        {
            return false;
        }
        lock (_gate)
        {
            if (!TrySource(source)
                || !TryReadEnvelope(
                    json!,
                    out var root,
                    out var generation,
                    out var messageToken,
                    out var messageId,
                    "amare_secret")
                || !HasExactProperties(
                    root,
                    "type", "version", "generation", "token", "msgId", "opId",
                    "formToken", "username", "password")
                || !TryString(root, "opId", out var messageOperationId)
                || !string.Equals(messageOperationId, operationId, StringComparison.Ordinal)
                || !MatchesPendingLocked(
                    operationId, WebViewVaultCommandKind.ReadSecret, messageToken, generation)
                || !MarkMessageLocked(messageId))
            {
                return false;
            }
            var pending = _pendingCommand!;
            _pendingCommand = null;
            if (!TryString(root, "formToken", out var formToken)
                || !string.Equals(formToken, pending.FormToken, StringComparison.Ordinal)
                || !TryString(root, "username", out var username)
                || !TryString(root, "password", out var password)
                || !IsSafeUsername(username)
                || string.IsNullOrEmpty(password)
                || password.Length > MaxPasswordLength
                || password.Contains('\0'))
            {
                return false;
            }
            secret = new WebViewVaultSecret(username, password);
            return true;
        }
    }

    public bool TryAcceptFillResult(
        string? json,
        string? source,
        string operationId,
        out bool ok)
    {
        ok = false;
        if (!TryParseJson(json))
        {
            return false;
        }
        lock (_gate)
        {
            if (!TrySource(source)
                || !TryReadEnvelope(
                    json!,
                    out var root,
                    out var generation,
                    out var messageToken,
                    out var messageId,
                    "amare_fill_result")
                || !HasExactProperties(
                    root,
                    "type", "version", "generation", "token", "msgId", "opId", "ok")
                || !TryString(root, "opId", out var messageOperationId)
                || !string.Equals(messageOperationId, operationId, StringComparison.Ordinal)
                || !MatchesPendingLocked(
                    operationId, WebViewVaultCommandKind.Fill, messageToken, generation)
                || !MarkMessageLocked(messageId))
            {
                return false;
            }
            _pendingCommand = null;
            if (!TryBool(root, "ok", out var result))
            {
                return false;
            }
            ok = result;
            return true;
        }
    }

    private static bool TryReadEnvelope(
        string json,
        out JsonElement root,
        out long generation,
        out string token,
        out string messageId,
        string expectedType)
    {
        using var document = JsonDocument.Parse(json);
        root = document.RootElement.Clone();
        generation = 0;
        token = "";
        messageId = "";
        return root.ValueKind == JsonValueKind.Object
               && TryString(root, "type", out var type)
               && string.Equals(type, expectedType, StringComparison.Ordinal)
               && TryInt(root, "version", out var version)
               && version == 1
               && TryLong(root, "generation", out generation)
               && TryString(root, "token", out token)
               && IsSafeToken(token)
               && TryString(root, "msgId", out messageId)
               && IsSafeMessageId(messageId);
    }

    private bool TrySource(string? source)
    {
        if (!WebOrigin.TryParse(source, out var sourceOrigin))
        {
            return false;
        }
        if (DocumentOrigin is not { } doc
            || doc != sourceOrigin
            || DocumentUrl is null
            || !WebOrigin.SameDocumentUrl(source, DocumentUrl))
        {
            return false;
        }
        return true;
    }

    private bool MatchesPendingLocked(
        string operationId,
        WebViewVaultCommandKind kind,
        string messageToken,
        long generation)
    {
        return _pendingCommand is { } pending
               && pending.Kind == kind
               && string.Equals(pending.OperationId, operationId, StringComparison.Ordinal)
               && pending.Generation == generation
               && pending.Generation == Generation
               && string.Equals(pending.DocumentToken, messageToken, StringComparison.Ordinal)
               && TokenEquals(messageToken);
    }

    private bool TokenEquals(string messageToken) =>
        !string.IsNullOrEmpty(DocumentToken)
        && string.Equals(DocumentToken, messageToken, StringComparison.Ordinal);

    private bool MarkMessageLocked(string messageId)
    {
        if (!IsSafeMessageId(messageId) || !_seenMessages.Add(messageId))
        {
            return false;
        }
        _seenOrder.Enqueue(messageId);
        while (_seenOrder.Count > MaxSeenMessages)
        {
            _seenMessages.Remove(_seenOrder.Dequeue());
        }
        return true;
    }

    private void ClearOperationsLocked()
    {
        _pendingCommand = null;
        _candidateKeys.Clear();
        _seenMessages.Clear();
        _seenOrder.Clear();
    }

    private static string RandomToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool TryParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)
            || Encoding.UTF8.GetByteCount(json) > MaxMessageBytes)
        {
            return false;
        }
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasExactProperties(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        var count = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (!expected.Contains(property.Name))
            {
                return false;
            }
            count++;
        }
        return count == names.Length
               && names.All(name => root.TryGetProperty(name, out _));
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString() ?? "";
        return true;
    }

    private static bool TryInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out value);
    }

    private static bool TryLong(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt64(out value);
    }

    private static bool TryBool(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var property)
            || (property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False))
        {
            return false;
        }
        value = property.GetBoolean();
        return true;
    }

    private static bool IsSafeMessageId(string? value) =>
        IsHex(value, 16, 64);

    private static bool IsSafeToken(string? value) =>
        IsHex(value, 16, MaxTokenLength);

    private static bool IsSafeOperationId(string? value) =>
        IsHex(value, 16, 64);

    private static bool IsHex(string? value, int min, int max)
    {
        if (value is null || value.Length < min || value.Length > max)
        {
            return false;
        }
        foreach (var ch in value)
        {
            if (!(ch is >= '0' and <= '9')
                && !(ch is >= 'a' and <= 'f')
                && !(ch is >= 'A' and <= 'F'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSafeUsername(string? value) =>
        value is not null
        && value.Length <= MaxUsernameLength
        && !value.Any(char.IsControl);

    private sealed record PendingCommand(
        string OperationId,
        WebViewVaultCommandKind Kind,
        long Generation,
        string DocumentToken,
        string FormToken,
        WebOrigin Origin,
        string DocumentUrl);
}
