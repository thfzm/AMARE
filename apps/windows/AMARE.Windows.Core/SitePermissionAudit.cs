namespace Amare.Windows.Core;

/// <summary>
/// Lifecycle of the per-profile WebView2 legacy permission audit. Permission
/// requests are only served while the profile is <see cref="Reconciled"/>.
/// </summary>
public enum WebViewPermissionAuditState
{
    NotStarted,
    Auditing,
    Reconciled,
    Failed,
    Unsupported
}

public enum LegacyPermissionState
{
    Default,
    Allow,
    Deny,
    Unknown
}

public sealed record LegacyPermissionSetting(
    string Origin,
    string Kind,
    LegacyPermissionState State);

public sealed record AmarePermissionPolicy(
    string Origin,
    string Kind,
    string Decision);

public enum PermissionReconciliationAction
{
    KeepDefault,
    ResetToDefault
}

/// <summary>
/// One audited legacy WebView2 permission setting and the AMARE decision. The
/// reconciliation action is deliberately limited to "reset to default": AMARE's
/// per-profile store and request handler must remain the only authority. A
/// persisted WebView2 allow/deny would bypass the handler and outlive later
/// changes to the AMARE policy.
/// </summary>
public sealed record PermissionReconciliationDecision(
    LegacyPermissionSetting Legacy,
    string? AmareDecision,
    PermissionReconciliationAction Action,
    string Reason);

public static class WebViewPermissionReconciliation
{
    /// <summary>
    /// Plans reconciliation for one non-default WebView2 setting. Exact origin
    /// matching follows <see cref="WebOrigin"/>: HTTPS only, normalized IDN,
    /// default port normalized, no subdomain inheritance.
    /// </summary>
    public static PermissionReconciliationDecision Plan(
        LegacyPermissionSetting legacy,
        IReadOnlyList<AmarePermissionPolicy> policies)
    {
        if (legacy.State == LegacyPermissionState.Default)
        {
            return new PermissionReconciliationDecision(
                legacy, null, PermissionReconciliationAction.KeepDefault, "already_default");
        }
        if (!SitePermissionCatalog.IsSupported(legacy.Kind))
        {
            // Unknown kinds are not mapped to the AMARE store. Removing the
            // legacy state is still required; if WebView2 cannot reset it the
            // audit fails closed.
            return new PermissionReconciliationDecision(
                legacy, null, PermissionReconciliationAction.ResetToDefault,
                "unknown_kind_fail_closed");
        }
        if (!WebOrigin.TryParse(legacy.Origin, out var legacyOrigin))
        {
            return new PermissionReconciliationDecision(
                legacy, null, PermissionReconciliationAction.ResetToDefault,
                "invalid_origin_fail_closed");
        }

        AmarePermissionPolicy? matched = null;
        foreach (var policy in policies)
        {
            if (!string.Equals(policy.Kind, legacy.Kind, StringComparison.Ordinal))
            {
                continue;
            }
            if (!WebOrigin.TryParse(policy.Origin, out var policyOrigin)
                || policyOrigin != legacyOrigin)
            {
                continue;
            }
            matched = policy;
            break;
        }

        if (matched is null)
        {
            return new PermissionReconciliationDecision(
                legacy, null, PermissionReconciliationAction.ResetToDefault,
                "no_amare_policy");
        }

        var decision = matched.Decision switch
        {
            "allow" => "allow",
            "deny" => "deny",
            _ => null
        };
        if (decision is null)
        {
            return new PermissionReconciliationDecision(
                legacy, null, PermissionReconciliationAction.ResetToDefault,
                "invalid_amare_policy");
        }
        return new PermissionReconciliationDecision(
            legacy,
            decision,
            PermissionReconciliationAction.ResetToDefault,
            decision == "allow"
                ? "amare_allow_requires_request_path"
                : "amare_deny");
    }
}

public static class PermissionAuditGate
{
    /// <summary>
    /// Only a completed, verified reconciliation may serve permission requests.
    /// NotStarted/Auditing/Failed/Unsupported all fail closed.
    /// </summary>
    public static bool CanServePermissionRequests(WebViewPermissionAuditState state) =>
        state == WebViewPermissionAuditState.Reconciled;
}
