using System.Runtime.InteropServices;
using Amare.Windows.Core;
using Microsoft.Web.WebView2.Core;

namespace Amare.Windows;

public sealed partial class MainWindow
{
    private const string TestLegacyPermissionEnv = "AMARE_TEST_LEGACY_PERMISSION";
    private static readonly TimeSpan PermissionAuditRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly object PermissionAuditSync = new();
    private static readonly Dictionary<string, PermissionAuditEntry> PermissionAudits =
        new(StringComparer.Ordinal);
    private static readonly HashSet<string> PermissionAuditSeeded =
        new(StringComparer.Ordinal);
    private static long _permissionAuditGeneration;

    private sealed record PermissionAuditResult(
        WebViewPermissionAuditState State,
        int LegacyCount,
        int ResetCount,
        int FailedCount,
        string? Error);

    private sealed class PermissionAuditEntry
    {
        public long Generation { get; init; }
        public WebViewPermissionAuditState State { get; set; } = WebViewPermissionAuditState.NotStarted;
        public required Task<PermissionAuditResult> Task { get; set; }
        public PermissionAuditResult? Result { get; set; }
        public DateTimeOffset FailedAt { get; set; }
    }

    /// <summary>
    /// Audits the WebView2 profile's persisted non-default permission settings
    /// before a page is allowed to navigate. The audit is keyed per AMARE
    /// profile (and once for the separate AI WebView2 profile), serialized
    /// across all windows in this process, and repeated on the next app start.
    /// </summary>
    private async Task<PermissionAuditResult> EnsurePermissionAuditAsync(
        CoreWebView2 core,
        string auditKey,
        bool useStore,
        bool allowTestSeed)
    {
        if (useStore && _context is not null)
        {
            _ = EnsureVaultClient();
        }
        var context = _context;
        var client = _vaultClient;

        Task<PermissionAuditResult> task;
        lock (PermissionAuditSync)
        {
            if (PermissionAudits.TryGetValue(auditKey, out var existing))
            {
                if (existing.State is WebViewPermissionAuditState.Auditing
                    or WebViewPermissionAuditState.Reconciled)
                {
                    task = existing.Task;
                }
                else if (existing.State == WebViewPermissionAuditState.Unsupported)
                {
                    return existing.Result!;
                }
                else if (existing.State == WebViewPermissionAuditState.Failed
                         && DateTimeOffset.UtcNow - existing.FailedAt < PermissionAuditRetryDelay)
                {
                    return existing.Result!;
                }
                else
                {
                    task = StartPermissionAuditLocked(
                        auditKey, core, useStore, allowTestSeed, context, client);
                }
            }
            else
            {
                task = StartPermissionAuditLocked(
                    auditKey, core, useStore, allowTestSeed, context, client);
            }
        }
        return await task;
    }

    private static Task<PermissionAuditResult> StartPermissionAuditLocked(
        string auditKey,
        CoreWebView2 core,
        bool useStore,
        bool allowTestSeed,
        ProfileContextDto? context,
        AmareVaultClient? client)
    {
        var generation = ++_permissionAuditGeneration;
        var tcs = new TaskCompletionSource<PermissionAuditResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new PermissionAuditEntry
        {
            Generation = generation,
            State = WebViewPermissionAuditState.Auditing,
            Task = tcs.Task
        };
        PermissionAudits[auditKey] = entry;
        _ = RunPermissionAuditAsync(
            auditKey, generation, core, useStore, allowTestSeed, context, client, tcs);
        return tcs.Task;
    }

    private static async Task RunPermissionAuditAsync(
        string auditKey,
        long generation,
        CoreWebView2 core,
        bool useStore,
        bool allowTestSeed,
        ProfileContextDto? context,
        AmareVaultClient? client,
        TaskCompletionSource<PermissionAuditResult> completion)
    {
        PermissionAuditResult result;
        try
        {
            if (allowTestSeed)
            {
                await SeedTestLegacyPermissionAsync(core, auditKey);
            }
            result = await AuditProfilePermissionsAsync(
                core, auditKey, useStore, context, client);
        }
        catch (Exception exception)
        {
            result = new PermissionAuditResult(
                WebViewPermissionAuditState.Failed, 0, 0, 0,
                $"{exception.GetType().Name}:0x{exception.HResult:X8}");
        }

        lock (PermissionAuditSync)
        {
            if (PermissionAudits.TryGetValue(auditKey, out var entry)
                && entry.Generation == generation)
            {
                entry.State = result.State;
                entry.Result = result;
                entry.FailedAt = result.State is WebViewPermissionAuditState.Failed
                    or WebViewPermissionAuditState.Unsupported
                    ? DateTimeOffset.UtcNow
                    : default;
            }
        }
        completion.TrySetResult(result);
    }

    private static async Task<PermissionAuditResult> AuditProfilePermissionsAsync(
        CoreWebView2 core,
        string auditKey,
        bool useStore,
        ProfileContextDto? context,
        AmareVaultClient? client)
    {
        List<CoreWebView2PermissionSetting> settings;
        try
        {
            settings = (await core.Profile.GetNonDefaultPermissionSettingsAsync()).ToList();
        }
        catch (Exception exception) when (IsUnsupportedPermissionApi(exception))
        {
            AppLog.Warn($"Permission audit unsupported: {exception.GetType().Name}");
            return new PermissionAuditResult(
                WebViewPermissionAuditState.Unsupported, 0, 0, 0,
                $"{exception.GetType().Name}:0x{exception.HResult:X8}");
        }
        catch (Exception exception)
        {
            AppLog.Warn($"Permission audit read failed: {exception.GetType().Name}");
            return new PermissionAuditResult(
                WebViewPermissionAuditState.Failed, 0, 0, 0,
                $"{exception.GetType().Name}:0x{exception.HResult:X8}");
        }

        IReadOnlyList<AmarePermissionPolicy> policies = [];
        if (useStore)
        {
            if (context is null || client is null)
            {
                AppLog.Warn("Permission audit store unavailable");
                return new PermissionAuditResult(
                    WebViewPermissionAuditState.Failed, settings.Count, 0, 0,
                    "permission store unavailable");
            }
            try
            {
                var stored = await client.ListPermissionsAsync(context);
                policies = stored
                    .Select(permission => new AmarePermissionPolicy(
                        permission.Origin, permission.Kind, permission.Decision))
                    .ToList();
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Permission audit store read failed: {exception.GetType().Name}");
                return new PermissionAuditResult(
                    WebViewPermissionAuditState.Failed, settings.Count, 0, 0,
                    $"{exception.GetType().Name}:0x{exception.HResult:X8}");
            }
        }

        var reset = 0;
        var failed = 0;
        foreach (var setting in settings)
        {
            var kindName = PermissionKindName(setting.PermissionKind)
                           ?? $"unknown({(int)setting.PermissionKind})";
            var legacy = new LegacyPermissionSetting(
                setting.PermissionOrigin,
                kindName,
                MapLegacyPermissionState(setting.PermissionState));
            var plan = WebViewPermissionReconciliation.Plan(legacy, policies);
            if (plan.Action == PermissionReconciliationAction.KeepDefault)
            {
                continue;
            }
            try
            {
                await core.Profile.SetPermissionStateAsync(
                    setting.PermissionKind,
                    setting.PermissionOrigin,
                    CoreWebView2PermissionState.Default);
                reset++;
                AppLog.Info(
                    $"permission audit profile={auditKey} origin={NormalizeAuditOrigin(setting.PermissionOrigin)} " +
                    $"kind={kindName} webview={legacy.State} amare={plan.AmareDecision ?? "none"} " +
                    $"action=reset reason={plan.Reason} result=ok");
            }
            catch (Exception exception)
            {
                failed++;
                AppLog.Warn(
                    $"permission audit profile={auditKey} origin={NormalizeAuditOrigin(setting.PermissionOrigin)} " +
                    $"kind={kindName} webview={legacy.State} action=reset result=failed " +
                    $"error={exception.GetType().Name}:0x{exception.HResult:X8}");
            }
        }

        if (failed == 0)
        {
            try
            {
                var remaining = await core.Profile.GetNonDefaultPermissionSettingsAsync();
                if (remaining.Count > 0)
                {
                    failed = remaining.Count;
                    AppLog.Warn(
                        $"permission audit profile={auditKey} remaining={remaining.Count} result=failed");
                }
            }
            catch (Exception exception)
            {
                failed = 1;
                AppLog.Warn(
                    $"permission audit verify failed: {exception.GetType().Name}:0x{exception.HResult:X8}");
            }
        }

        var state = failed == 0
            ? WebViewPermissionAuditState.Reconciled
            : WebViewPermissionAuditState.Failed;
        AppLog.Info(
            $"permission audit profile={auditKey} legacy={settings.Count} reset={reset} " +
            $"failed={failed} state={state}");
        return new PermissionAuditResult(state, settings.Count, reset, failed,
            failed == 0 ? null : $"{failed} legacy setting(s) could not be reconciled");
    }

    private static bool IsUnsupportedPermissionApi(Exception exception) =>
        exception is NotSupportedException
        || exception is NotImplementedException
        || exception is MissingMethodException
        || (exception is COMException com
            && (uint)com.HResult is 0x80004001 // E_NOTIMPL
                or 0x80004002 // E_NOINTERFACE
                or 0x80070032); // ERROR_NOT_SUPPORTED

    private static LegacyPermissionState MapLegacyPermissionState(
        CoreWebView2PermissionState state) => state switch
    {
        CoreWebView2PermissionState.Default => LegacyPermissionState.Default,
        CoreWebView2PermissionState.Allow => LegacyPermissionState.Allow,
        CoreWebView2PermissionState.Deny => LegacyPermissionState.Deny,
        _ => LegacyPermissionState.Unknown
    };

    private static string NormalizeAuditOrigin(string origin) =>
        WebOrigin.TryParse(origin, out var parsed) ? parsed.Value : "invalid";

    /// <summary>
    /// Test-only hook used by the Windows UI smoke to create the same kind of
    /// legacy WebView2 setting that an older AMARE build could have left behind.
    /// It is inert unless the test virtual-host root is configured.
    /// </summary>
    private static async Task SeedTestLegacyPermissionAsync(
        CoreWebView2 core,
        string auditKey)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestVaultRootEnv)))
        {
            return;
        }
        var raw = Environment.GetEnvironmentVariable(TestLegacyPermissionEnv);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }
        lock (PermissionAuditSync)
        {
            if (!PermissionAuditSeeded.Add(auditKey))
            {
                return;
            }
        }
        var parts = raw.Split('|');
        if (parts.Length != 3)
        {
            return;
        }
        var kind = PermissionKindFromName(parts[0]);
        var state = parts[2] switch
        {
            "allow" => CoreWebView2PermissionState.Allow,
            "deny" => CoreWebView2PermissionState.Deny,
            _ => (CoreWebView2PermissionState?)null
        };
        if (kind is null || state is null)
        {
            return;
        }
        try
        {
            await core.Profile.SetPermissionStateAsync(kind.Value, parts[1], state.Value);
            AppLog.Info(
                $"permission audit test seed profile={auditKey} origin={NormalizeAuditOrigin(parts[1])} " +
                $"kind={parts[0]} state={parts[2]}");
        }
        catch (Exception exception)
        {
            AppLog.Warn(
                $"permission audit test seed failed: {exception.GetType().Name}:0x{exception.HResult:X8}");
        }
    }

    private static CoreWebView2PermissionKind? PermissionKindFromName(string kind) => kind switch
    {
        "microphone" => CoreWebView2PermissionKind.Microphone,
        "camera" => CoreWebView2PermissionKind.Camera,
        "geolocation" => CoreWebView2PermissionKind.Geolocation,
        "notifications" => CoreWebView2PermissionKind.Notifications,
        "other_sensors" => CoreWebView2PermissionKind.OtherSensors,
        "clipboard_read" => CoreWebView2PermissionKind.ClipboardRead,
        "multiple_automatic_downloads" => CoreWebView2PermissionKind.MultipleAutomaticDownloads,
        "file_read_write" => CoreWebView2PermissionKind.FileReadWrite,
        "autoplay" => CoreWebView2PermissionKind.Autoplay,
        "local_fonts" => CoreWebView2PermissionKind.LocalFonts,
        "midi_system_exclusive_messages" => CoreWebView2PermissionKind.MidiSystemExclusiveMessages,
        "window_management" => CoreWebView2PermissionKind.WindowManagement,
        "persistent_storage" => CoreWebView2PermissionKind.PersistentStorage,
        _ => null
    };

    private static WebViewPermissionAuditState PermissionAuditStateFor(string auditKey)
    {
        lock (PermissionAuditSync)
        {
            return PermissionAudits.TryGetValue(auditKey, out var entry)
                ? entry.State
                : WebViewPermissionAuditState.NotStarted;
        }
    }

    private static string PermissionAuditSummary(string auditKey)
    {
        lock (PermissionAuditSync)
        {
            if (!PermissionAudits.TryGetValue(auditKey, out var entry))
            {
                return "Legacy WebView2 audit: Not started. Permission requests are blocked.";
            }
            return entry.State switch
            {
                WebViewPermissionAuditState.Reconciled =>
                    $"Legacy WebView2 audit: Reconciled ({entry.Result?.ResetCount ?? 0} legacy setting(s) reset).",
                WebViewPermissionAuditState.Auditing =>
                    "Legacy WebView2 audit: Auditing. Permission requests are blocked.",
                WebViewPermissionAuditState.Unsupported =>
                    "Legacy WebView2 audit: Unsupported. Permission requests are blocked until a supported WebView2 runtime is available.",
                WebViewPermissionAuditState.Failed =>
                    "Legacy WebView2 audit: Failed. Permission requests are blocked; the audit will retry on the next app start.",
                _ =>
                    "Legacy WebView2 audit: Not started. Permission requests are blocked."
            };
        }
    }

    private static void ForgetPermissionAudit(string auditKey)
    {
        lock (PermissionAuditSync)
        {
            PermissionAudits.Remove(auditKey);
            PermissionAuditSeeded.Remove(auditKey);
        }
    }
}
