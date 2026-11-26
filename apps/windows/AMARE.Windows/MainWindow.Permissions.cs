using System.Text.Json;
using Amare.Windows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Amare.Windows;

public sealed partial class MainWindow
{
    private bool _permissionDialogOpen;
    private bool _permissionRequestActive;
    private long _permissionRequestEpoch;
    private ContentDialog? _permissionDialog;
    private ContentDialog? _sitePermissionsDialog;

    private static void OnFramePermissionRequested(
        CoreWebView2Frame sender,
        CoreWebView2PermissionRequestedEventArgs args)
    {
        args.Handled = true;
        args.SavesInProfile = false;
        args.State = CoreWebView2PermissionState.Deny;
    }

    private static void AttachFramePermissionDeny(CoreWebView2Frame frame)
    {
        frame.PermissionRequested += OnFramePermissionRequested;
        frame.Destroyed += (_, _) =>
        {
            try
            {
                frame.PermissionRequested -= OnFramePermissionRequested;
            }
            catch
            {
                // The frame may already be gone.
            }
        };
        frame.FrameCreated += (_, args) => AttachFramePermissionDeny(args.Frame);
    }

    private static void OnDenyAllPermissionRequested(
        CoreWebView2 sender,
        CoreWebView2PermissionRequestedEventArgs args)
    {
        args.Handled = true;
        args.SavesInProfile = false;
        args.State = CoreWebView2PermissionState.Deny;
    }

    private static void AttachDenyAllPermissions(CoreWebView2 core)
    {
        core.PermissionRequested += OnDenyAllPermissionRequested;
        core.FrameCreated += (_, args) => AttachFramePermissionDeny(args.Frame);
    }

    private void AttachPermissionPolicy(BrowserTab host, CoreWebView2 core)
    {
        core.PermissionRequested += (sender, args) =>
        {
            // Never let WebView2 persist its own permission decision. AMARE's
            // per-profile store is the source of truth and the UI must match.
            args.SavesInProfile = false;
            _ = HandlePermissionRequestedAsync(host, core, args);
        };
        core.FrameCreated += (_, args) => AttachFramePermissionDeny(args.Frame);
    }

    private async Task HandlePermissionRequestedAsync(
        BrowserTab host,
        CoreWebView2 core,
        CoreWebView2PermissionRequestedEventArgs args)
    {
        args.SavesInProfile = false;
        try
        {
            using var deferral = args.GetDeferral();
            var state = CoreWebView2PermissionState.Deny;
            try
            {
                state = await ResolvePermissionDecisionAsync(host, core, args);
            }
            catch
            {
                state = CoreWebView2PermissionState.Deny;
            }
            try
            {
                args.SavesInProfile = false;
                args.State = state;
            }
            catch
            {
                // The request may have been invalidated by navigation/tab close.
            }
        }
        catch
        {
            try
            {
                args.State = CoreWebView2PermissionState.Deny;
            }
            catch
            {
                // Nothing else can be done.
            }
        }
    }

    private async Task<CoreWebView2PermissionState> ResolvePermissionDecisionAsync(
        BrowserTab host,
        CoreWebView2 core,
        CoreWebView2PermissionRequestedEventArgs args)
    {
        if (_context is null
            || host.Closed
            || host.Model.IsNewTabPage
            || host.ShowingNewTab
            || _permissionDialogOpen
            || _permissionRequestActive
            || !ReferenceEquals(host.View.CoreWebView2, core))
        {
            return CoreWebView2PermissionState.Deny;
        }
        if (!PermissionAuditGate.CanServePermissionRequests(
                PermissionAuditStateFor(_profileId)))
        {
            // The startup reconciliation has not verified that legacy WebView2
            // persisted permissions no longer bypass the AMARE store. Never
            // serve a prompt/allow until that audit is Reconciled.
            AppLog.Warn("Site permission denied: legacy WebView2 permission audit is not reconciled");
            return CoreWebView2PermissionState.Deny;
        }
        var kind = PermissionKindName(args.PermissionKind);
        AppLog.Info($"Site permission requested kind={args.PermissionKind} userInitiated={args.IsUserInitiated}");
        if (kind is null
            || !WebOrigin.TryParse(args.Uri, out var origin)
            || !WebOrigin.TryParse(core.Source, out var topLevel)
            || origin != topLevel)
        {
            // Unknown kinds, insecure origins, opaque/internal documents and
            // third-party frame requests are denied without a prompt.
            return CoreWebView2PermissionState.Deny;
        }
        if (!EnsureVaultClient() || _vaultClient is not { } client)
        {
            return CoreWebView2PermissionState.Deny;
        }

        _permissionRequestActive = true;
        var context = _context;
        var profileId = _profileId;
        var documentGeneration = host.VaultState.Generation;
        var epoch = _permissionRequestEpoch;
        try
        {
            SitePermissionDto? stored;
            try
            {
                stored = await client.GetPermissionAsync(context, origin.Value, kind);
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Site permission lookup failed: {exception.GetType().Name}");
                return CoreWebView2PermissionState.Deny;
            }
            if (!PermissionRequestIsCurrent(
                    host, core, context, profileId, client, documentGeneration, epoch, origin))
            {
                return CoreWebView2PermissionState.Deny;
            }
            if (stored is not null)
            {
                return string.Equals(stored.Decision, "allow", StringComparison.Ordinal)
                    ? CoreWebView2PermissionState.Allow
                    : CoreWebView2PermissionState.Deny;
            }
            if (_permissionDialogOpen)
            {
                return CoreWebView2PermissionState.Deny;
            }

            var (result, blockAlways) = await ShowPermissionRequestDialogAsync(origin, kind);
            if (!PermissionRequestIsCurrent(
                    host, core, context, profileId, client, documentGeneration, epoch, origin))
            {
                return CoreWebView2PermissionState.Deny;
            }

            if (blockAlways)
            {
                // The user explicitly blocked this request. Even if another
                // window changed the stored row during the dialog, this request
                // must not become an allow. Persist the block on a best-effort
                // basis, then deny the current request unconditionally.
                await PersistPermissionDecisionAsync(client, context, origin, kind, "deny");
                return CoreWebView2PermissionState.Deny;
            }
            return result switch
            {
                ContentDialogResult.Primary => CoreWebView2PermissionState.Allow,
                ContentDialogResult.Secondary => await PersistPermissionDecisionAsync(
                    client, context, origin, kind, "allow") == true
                    ? CoreWebView2PermissionState.Allow
                    : CoreWebView2PermissionState.Deny,
                _ => CoreWebView2PermissionState.Deny
            };
        }
        finally
        {
            _permissionRequestActive = false;
        }
    }

    /// <summary>
    /// Revalidates every asynchronous boundary of a permission request. A
    /// navigation, tab close, profile switch, dialog cancellation or WebView
    /// replacement invalidates the decision and forces a deny.
    /// </summary>
    private bool PermissionRequestIsCurrent(
        BrowserTab host,
        CoreWebView2 core,
        ProfileContextDto context,
        string profileId,
        AmareVaultClient client,
        long documentGeneration,
        long epoch,
        WebOrigin origin)
    {
        return _permissionRequestEpoch == epoch
            && !host.Closed
            && ReferenceEquals(host.View.CoreWebView2, core)
            && ReferenceEquals(_context, context)
            && _profileId == profileId
            && ReferenceEquals(_vaultClient, client)
            && host.VaultState.Generation == documentGeneration
            && !host.Model.IsNewTabPage
            && !host.ShowingNewTab
            && WebOrigin.TryParse(core.Source, out var currentTop)
            && currentTop == origin;
    }

    private async Task<bool?> PersistPermissionDecisionAsync(
        AmareVaultClient client,
        ProfileContextDto context,
        WebOrigin origin,
        string kind,
        string decision)
    {
        try
        {
            await client.SetPermissionAsync(
                context, origin.Value, kind, decision, "persistent", 0);
            return string.Equals(decision, "allow", StringComparison.Ordinal);
        }
        catch (VaultException exception) when (exception.Code == VaultErrorCode.PermissionConflict)
        {
            // Another window changed the same origin/kind. Use the store's
            // current decision instead of overwriting it.
            try
            {
                var existing = await client.GetPermissionAsync(context, origin.Value, kind);
                if (existing is null)
                {
                    return null;
                }
                return string.Equals(existing.Decision, "allow", StringComparison.Ordinal);
            }
            catch
            {
                return null;
            }
        }
        catch (Exception exception)
        {
            AppLog.Warn($"Site permission save failed: {exception.GetType().Name}");
            StatusText.Text = "Site permission could not be saved. The request was denied.";
            return null;
        }
    }

    private async Task<(ContentDialogResult Result, bool BlockAlways)>
        ShowPermissionRequestDialogAsync(WebOrigin origin, string kind)
    {
        var blockAlways = false;
        var content = new StackPanel { Spacing = 8 };
        var originText = new TextBlock
        {
            Text = L10n.T("permissions.origin", origin.Value),
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(originText, "PermissionRequestOrigin");
        var kindText = new TextBlock
        {
            Text = L10n.T("permissions.kind", SitePermissionCatalog.LocalizedDisplayName(kind)),
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(kindText, "PermissionRequestKind");
        var explanation = new TextBlock
        {
            Text = L10n.T("permissions.requestExplanation"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        var blockButton = new Button { Content = L10n.T("permissions.blockAlways") };
        AutomationProperties.SetAutomationId(blockButton, "PermissionBlockAlwaysButton");
        content.Children.Add(originText);
        content.Children.Add(kindText);
        content.Children.Add(explanation);
        content.Children.Add(blockButton);

        var dialog = new ContentDialog
        {
            Title = L10n.T("permissions.requestTitle"),
            Content = content,
            PrimaryButtonText = L10n.T("permissions.allowOnce"),
            SecondaryButtonText = L10n.T("permissions.alwaysAllow"),
            CloseButtonText = L10n.T("permissions.notNow"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        blockButton.Click += (_, _) =>
        {
            blockAlways = true;
            dialog.Hide();
        };
        _permissionDialog = dialog;
        _permissionDialogOpen = true;
        try
        {
            LocalizationUi.ApplyDialog(dialog);
            var result = await dialog.ShowAsync();
            return (result, blockAlways);
        }
        catch
        {
            return (ContentDialogResult.None, false);
        }
        finally
        {
            _permissionDialogOpen = false;
            _permissionDialog = null;
        }
    }

    private void CancelPendingPermissionRequest()
    {
        _permissionRequestEpoch++;
        try
        {
            _permissionDialog?.Hide();
        }
        catch
        {
            // The dialog may already be closed.
        }
    }

    private void CancelSitePermissionsDialog()
    {
        try
        {
            _sitePermissionsDialog?.Hide();
        }
        catch
        {
            // The dialog may already be closed.
        }
    }

    private static string? PermissionKindName(CoreWebView2PermissionKind kind) => kind switch
    {
        CoreWebView2PermissionKind.Microphone => "microphone",
        CoreWebView2PermissionKind.Camera => "camera",
        CoreWebView2PermissionKind.Geolocation => "geolocation",
        CoreWebView2PermissionKind.Notifications => "notifications",
        CoreWebView2PermissionKind.OtherSensors => "other_sensors",
        CoreWebView2PermissionKind.ClipboardRead => "clipboard_read",
        CoreWebView2PermissionKind.MultipleAutomaticDownloads => "multiple_automatic_downloads",
        CoreWebView2PermissionKind.FileReadWrite => "file_read_write",
        CoreWebView2PermissionKind.Autoplay => "autoplay",
        CoreWebView2PermissionKind.LocalFonts => "local_fonts",
        CoreWebView2PermissionKind.MidiSystemExclusiveMessages => "midi_system_exclusive_messages",
        CoreWebView2PermissionKind.WindowManagement => "window_management",
        CoreWebView2PermissionKind.PersistentStorage => "persistent_storage",
        _ => null
    };

    private sealed record PermissionFilter(string? Kind, string Label);

    private sealed record PermissionRow(SitePermissionDto Dto, string Label);

    private async Task ShowSitePermissionsAsync()
    {
        if (_context is null || !EnsureVaultClient() || _vaultClient is not { } client)
        {
            StatusText.Text = L10n.T("permissions.unavailable");
            return;
        }
        var context = _context;
        var profileId = _profileId;

        var filters = new List<PermissionFilter>
        {
            new(null, L10n.T("permissions.filterAll"))
        };
        filters.AddRange(SitePermissionCatalog.Kinds.Select(kind =>
            new PermissionFilter(kind, SitePermissionCatalog.LocalizedDisplayName(kind))));
        var filterBox = new ComboBox
        {
            ItemsSource = filters,
            DisplayMemberPath = nameof(PermissionFilter.Label),
            SelectedIndex = 0,
            MinWidth = 220
        };
        AutomationProperties.SetAutomationId(filterBox, "SitePermissionsFilter");

        var list = new ListView
        {
            DisplayMemberPath = nameof(PermissionRow.Label),
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 320,
            MinHeight = 120
        };
        AutomationProperties.SetAutomationId(list, "SitePermissionsList");

        var allowButton = MakePermissionButton(L10n.T("permissions.allow"), "SitePermissionAllowButton");
        var blockButton = MakePermissionButton(L10n.T("permissions.block"), "SitePermissionBlockButton");
        var removeButton = MakePermissionButton(L10n.T("permissions.remove"), "SitePermissionRemoveButton");
        var resetSiteButton = MakePermissionButton(L10n.T("permissions.resetSite"), "SitePermissionResetSiteButton");
        var clearAllButton = MakePermissionButton(L10n.T("permissions.clearAll"), "SitePermissionClearAllButton");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var button in new[]
                 {
                     allowButton, blockButton, removeButton, resetSiteButton, clearAllButton
                 })
        {
            buttons.Children.Add(button);
        }

        var emptyText = new TextBlock
        {
            Text = L10n.T("permissions.empty"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetAutomationId(emptyText, "SitePermissionsEmptyText");
        var errorText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetAutomationId(errorText, "SitePermissionsErrorText");

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = L10n.T("permissions.intro"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        var auditText = new TextBlock
        {
            Text = PermissionAuditSummary(_profileId),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        AutomationProperties.SetAutomationId(auditText, "SitePermissionAuditState");
        stack.Children.Add(auditText);
        stack.Children.Add(filterBox);
        stack.Children.Add(list);
        stack.Children.Add(buttons);
        stack.Children.Add(emptyText);
        stack.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = L10n.T("permissions.title"),
            Content = new ScrollViewer { Content = stack, MaxHeight = 520 },
            CloseButtonText = L10n.T("common.close"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };

        var rows = new List<PermissionRow>();
        string? pendingResetOrigin = null;
        var pendingClearAll = false;
        void ResetConfirmations()
        {
            pendingResetOrigin = null;
            pendingClearAll = false;
            resetSiteButton.Content = L10n.T("permissions.resetSite");
            clearAllButton.Content = L10n.T("permissions.clearAll");
        }
        void UpdateButtons()
        {
            var hasSelection = list.SelectedItem is PermissionRow;
            allowButton.IsEnabled = hasSelection;
            blockButton.IsEnabled = hasSelection;
            removeButton.IsEnabled = hasSelection;
            resetSiteButton.IsEnabled = hasSelection;
            clearAllButton.IsEnabled = rows.Count > 0;
        }
        void ApplyFilter()
        {
            var selected = (filterBox.SelectedItem as PermissionFilter)?.Kind;
            var visible = selected is null
                ? rows
                : rows.Where(row => row.Dto.Kind == selected).ToList();
            list.ItemsSource = visible;
            emptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateButtons();
        }
        async Task ReloadAsync()
        {
            try
            {
                var permissions = await client.ListPermissionsAsync(context);
                rows = permissions
                    .Select(permission => new PermissionRow(
                        permission,
                        $"{permission.Origin} — {SitePermissionCatalog.LocalizedDisplayName(permission.Kind)} — " +
                        $"{DecisionLabel(permission.Decision)}"))
                    .ToList();
                errorText.Visibility = Visibility.Collapsed;
                ApplyFilter();
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Site permission list failed: {exception.GetType().Name}");
                errorText.Text = L10n.T("permissions.loadFailed");
                errorText.Visibility = Visibility.Visible;
            }
        }

        void ShowError(string message)
        {
            errorText.Text = message;
            errorText.Visibility = Visibility.Visible;
        }

        async Task UpdateDecisionAsync(string decision)
        {
            if (list.SelectedItem is not PermissionRow row)
            {
                return;
            }
            try
            {
                await client.SetPermissionAsync(
                    context,
                    row.Dto.Origin,
                    row.Dto.Kind,
                    decision,
                    "persistent",
                    row.Dto.Revision);
                errorText.Visibility = Visibility.Collapsed;
                await ReloadAsync();
            }
            catch (VaultException exception) when (exception.Code == VaultErrorCode.PermissionConflict)
            {
                ShowError(L10n.T("permissions.changedElsewhere"));
                await ReloadAsync();
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Site permission update failed: {exception.GetType().Name}");
                ShowError(L10n.T("permissions.saveFailedReload"));
                await ReloadAsync();
            }
        }

        async Task RemoveSelectedAsync()
        {
            if (list.SelectedItem is not PermissionRow row)
            {
                return;
            }
            try
            {
                await client.DeletePermissionAsync(
                    context, row.Dto.Origin, row.Dto.Kind, row.Dto.Revision);
                errorText.Visibility = Visibility.Collapsed;
                await ReloadAsync();
            }
            catch (VaultException exception) when (exception.Code == VaultErrorCode.PermissionConflict)
            {
                ShowError(L10n.T("permissions.changedElsewhere"));
                await ReloadAsync();
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Site permission delete failed: {exception.GetType().Name}");
                ShowError(L10n.T("permissions.deleteFailedReload"));
                await ReloadAsync();
            }
        }

        async Task ResetSelectedSiteAsync()
        {
            if (list.SelectedItem is not PermissionRow row)
            {
                return;
            }
            if (pendingResetOrigin != row.Dto.Origin)
            {
                ResetConfirmations();
                pendingResetOrigin = row.Dto.Origin;
                resetSiteButton.Content = L10n.T("permissions.reset");
                ShowError(L10n.T("permissions.resetConfirm", row.Dto.Origin));
                return;
            }
            ResetConfirmations();
            try
            {
                await client.ClearOriginPermissionsAsync(context, row.Dto.Origin);
                errorText.Visibility = Visibility.Collapsed;
                await ReloadAsync();
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Site permission origin reset failed: {exception.GetType().Name}");
                ShowError(L10n.T("permissions.saveFailedReload"));
                await ReloadAsync();
            }
        }

        async Task ClearAllAsync()
        {
            if (!pendingClearAll)
            {
                ResetConfirmations();
                pendingClearAll = true;
                clearAllButton.Content = L10n.T("permissions.clear");
                ShowError(L10n.T("permissions.clearConfirm"));
                return;
            }
            ResetConfirmations();
            try
            {
                await client.ClearAllPermissionsAsync(context);
                errorText.Visibility = Visibility.Collapsed;
                await ReloadAsync();
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Site permission clear failed: {exception.GetType().Name}");
                ShowError(L10n.T("permissions.saveFailedReload"));
                await ReloadAsync();
            }
        }

        allowButton.Click += async (_, _) => await UpdateDecisionAsync("allow");
        blockButton.Click += async (_, _) => await UpdateDecisionAsync("deny");
        removeButton.Click += async (_, _) => await RemoveSelectedAsync();
        resetSiteButton.Click += async (_, _) => await ResetSelectedSiteAsync();
        clearAllButton.Click += async (_, _) => await ClearAllAsync();
        list.SelectionChanged += (_, _) =>
        {
            ResetConfirmations();
            UpdateButtons();
        };
        filterBox.SelectionChanged += (_, _) =>
        {
            ResetConfirmations();
            ApplyFilter();
        };
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_sitePermissionsDialog, dialog))
            {
                _sitePermissionsDialog = null;
            }
        };

        await ReloadAsync();
        if (_context != context || _profileId != profileId)
        {
            return;
        }
        _sitePermissionsDialog = dialog;
        LocalizationUi.ApplyDialog(dialog);
        await dialog.ShowAsync();
    }

    private static Button MakePermissionButton(string label, string automationId)
    {
        var button = new Button { Content = label, IsEnabled = false };
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private static string DecisionLabel(string decision) =>
        string.Equals(decision, "allow", StringComparison.Ordinal)
            ? L10n.T("permissions.allow")
            : L10n.T("permissions.block");
}
