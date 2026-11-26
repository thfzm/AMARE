using Amare.Windows.Core;

namespace Amare.Interop.Tests;

internal static class Phase31Tests
{
    public static void Register()
    {
        Program.Run("permission_audit_exact_origin_policy", ExactOriginPolicy);
        Program.Run("permission_audit_fail_closed_policy", FailClosedPolicy);
        Program.Run("permission_audit_gate", AuditGate);
    }

    private static string ExactOriginPolicy()
    {
        var legacy = new LegacyPermissionSetting(
            "https://bücher.example", "camera", LegacyPermissionState.Allow);
        var idn = WebViewPermissionReconciliation.Plan(
            legacy,
            [
                new AmarePermissionPolicy(
                    "https://xn--bcher-kva.example", "camera", "allow")
            ]);
        Assert(idn.Action == PermissionReconciliationAction.ResetToDefault,
            "IDN legacy allow must be reset");
        Assert(idn.AmareDecision == "allow", "IDN policy did not match");

        var port = WebViewPermissionReconciliation.Plan(
            new LegacyPermissionSetting("https://example.com:8443", "camera", LegacyPermissionState.Allow),
            [new AmarePermissionPolicy("https://example.com", "camera", "allow")]);
        Assert(port.AmareDecision is null && port.Reason == "no_amare_policy",
            "port must be exact");

        var subdomain = WebViewPermissionReconciliation.Plan(
            new LegacyPermissionSetting("https://www.example.com", "camera", LegacyPermissionState.Deny),
            [new AmarePermissionPolicy("https://example.com", "camera", "allow")]);
        Assert(subdomain.AmareDecision is null && subdomain.Reason == "no_amare_policy",
            "subdomain must not inherit");

        var otherKind = WebViewPermissionReconciliation.Plan(
            new LegacyPermissionSetting("https://example.com", "microphone", LegacyPermissionState.Allow),
            [new AmarePermissionPolicy("https://example.com", "camera", "deny")]);
        Assert(otherKind.AmareDecision is null && otherKind.Reason == "no_amare_policy",
            "kind must be exact");

        var defaultPort = WebViewPermissionReconciliation.Plan(
            new LegacyPermissionSetting("https://example.com:443", "camera", LegacyPermissionState.Allow),
            [new AmarePermissionPolicy("https://example.com", "camera", "deny")]);
        Assert(defaultPort.AmareDecision == "deny", "default port must normalize");

        var scheme = WebViewPermissionReconciliation.Plan(
            new LegacyPermissionSetting("http://example.com", "camera", LegacyPermissionState.Allow),
            [new AmarePermissionPolicy("https://example.com", "camera", "allow")]);
        Assert(scheme.Reason == "invalid_origin_fail_closed",
            "HTTP legacy origin must fail closed");
        return "IDN/port/subdomain/kind/scheme exact matching PASS";
    }

    private static string FailClosedPolicy()
    {
        var noPolicy = WebViewPermissionReconciliation.Plan(
            new LegacyPermissionSetting("https://example.com", "notifications", LegacyPermissionState.Allow),
            []);
        Assert(noPolicy.Action == PermissionReconciliationAction.ResetToDefault
               && noPolicy.Reason == "no_amare_policy",
            "legacy allow without policy must reset");

        var denyPolicy = WebViewPermissionReconciliation.Plan(
            new LegacyPermissionSetting("https://example.com", "notifications", LegacyPermissionState.Allow),
            [new AmarePermissionPolicy("https://example.com", "notifications", "deny")]);
        Assert(denyPolicy.AmareDecision == "deny"
               && denyPolicy.Action == PermissionReconciliationAction.ResetToDefault,
            "AMARE deny must reset legacy allow");

        var allowPolicy = WebViewPermissionReconciliation.Plan(
            new LegacyPermissionSetting("https://example.com", "notifications", LegacyPermissionState.Deny),
            [new AmarePermissionPolicy("https://example.com", "notifications", "allow")]);
        Assert(allowPolicy.AmareDecision == "allow"
               && allowPolicy.Action == PermissionReconciliationAction.ResetToDefault,
            "AMARE allow must use the request path, not a WebView2 allow");

        var unknownKind = WebViewPermissionReconciliation.Plan(
            new LegacyPermissionSetting("https://example.com", "future_permission", LegacyPermissionState.Allow),
            []);
        Assert(unknownKind.Reason == "unknown_kind_fail_closed",
            "unknown kind must reset/fail closed");

        var unknownState = WebViewPermissionReconciliation.Plan(
            new LegacyPermissionSetting("https://example.com", "camera", LegacyPermissionState.Unknown),
            []);
        Assert(unknownState.Action == PermissionReconciliationAction.ResetToDefault,
            "unknown state must reset");

        var invalidPolicy = WebViewPermissionReconciliation.Plan(
            new LegacyPermissionSetting("https://example.com", "camera", LegacyPermissionState.Deny),
            [new AmarePermissionPolicy("https://example.com", "camera", "maybe")]);
        Assert(invalidPolicy.Reason == "invalid_amare_policy",
            "invalid AMARE policy must fail closed");

        var alreadyDefault = WebViewPermissionReconciliation.Plan(
            new LegacyPermissionSetting("https://example.com", "camera", LegacyPermissionState.Default),
            []);
        Assert(alreadyDefault.Action == PermissionReconciliationAction.KeepDefault,
            "default state must be left alone");
        return "legacy allow/deny, unknown kind/state, invalid policy fail closed PASS";
    }

    private static string AuditGate()
    {
        Assert(PermissionAuditGate.CanServePermissionRequests(WebViewPermissionAuditState.Reconciled),
            "reconciled must serve");
        foreach (var state in new[]
                 {
                     WebViewPermissionAuditState.NotStarted,
                     WebViewPermissionAuditState.Auditing,
                     WebViewPermissionAuditState.Failed,
                     WebViewPermissionAuditState.Unsupported
                 })
        {
            Assert(!PermissionAuditGate.CanServePermissionRequests(state),
                $"{state} must fail closed");
        }
        return "only Reconciled serves permission requests";
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
