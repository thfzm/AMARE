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
        core.PermissionRequested += (_, args) =>
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
        CoreWebView2Deferral? deferral = null;
        try
        {
            deferral = args.GetDeferral();
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
            return;
        }
        try
        {
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
        finally
        {
            deferral.Dispose();
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
        var kind = PermissionKindName(args.PermissionKind);
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
            if (stored is not null)
            {
                return string.Equals(stored.Decision, "allow", StringComparison.Ordinal)
                    ? CoreWebView2PermissionState.Allow
                    : CoreWebView2PermissionState.Deny;
            }
            if (_permissionDialogOpen || _permissionRequestEpoch != epoch)
            {
                return CoreWebView2PermissionState.Deny;
            }

            var (result, blockAlways) = await ShowPermissionRequestDialogAsync(origin, kind);
            if (_permissionRequestEpoch != epoch
                || host.Closed
                || !ReferenceEquals(host.View.CoreWebView2, core)
                || _context != context
                || _profileId != profileId
                || !ReferenceEquals(_vaultClient, client)
                || host.VaultState.Generation != documentGeneration
                || host.Model.IsNewTabPage
                || !WebOrigin.TryParse(core.Source, out var currentTop)
                || currentTop != origin)
            {
                return CoreWebView2PermissionState.Deny;
            }

            if (blockAlways)
            {
                var effective = await PersistPermissionDecisionAsync(
                    client, context, origin, kind, "deny");
                return effective == true
                    ? CoreWebView2PermissionState.Allow
                    : CoreWebView2PermissionState.Deny;
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
            Text = $"Site: {origin.Value}",
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(originText, "PermissionRequestOrigin");
        var kindText = new TextBlock
        {
            Text = $"Permission: {SitePermissionCatalog.DisplayName(kind)}",
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(kindText, "PermissionRequestKind");
        var explanation = new TextBlock
        {
            Text = "AMARE will not grant a sensitive permission without your choice. " +
                   "Closing this dialog denies only this request.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        var blockButton = new Button { Content = "Block always" };
        AutomationProperties.SetAutomationId(blockButton, "PermissionBlockAlwaysButton");
        content.Children.Add(originText);
        content.Children.Add(kindText);
        content.Children.Add(explanation);
        content.Children.Add(blockButton);

        var dialog = new ContentDialog
        {
            Title = "Permission request",
            Content = content,
            PrimaryButtonText = "Allow once",
            SecondaryButtonText = "Always allow",
            CloseButtonText = "Not now",
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
            StatusText.Text = "Site permissions are unavailable.";
            return;
        }
        var context = _context;
        var profileId = _profileId;

        var filters = new List<PermissionFilter>
        {
            new(null, "All permissions")
        };
        filters.AddRange(SitePermissionCatalog.Kinds.Select(kind =>
            new PermissionFilter(kind, SitePermissionCatalog.DisplayName(kind))));
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

        var allowButton = MakePermissionButton("Allow", "SitePermissionAllowButton");
        var blockButton = MakePermissionButton("Block", "SitePermissionBlockButton");
        var removeButton = MakePermissionButton("Remove", "SitePermissionRemoveButton");
        var resetSiteButton = MakePermissionButton("Reset site", "SitePermissionResetSiteButton");
        var clearAllButton = MakePermissionButton("Clear all", "SitePermissionClearAllButton");
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
            Text = "No saved site permissions for this profile.",
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
            Text = "Saved permissions are stored per profile and apply to exact HTTPS origins only.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        stack.Children.Add(filterBox);
        stack.Children.Add(list);
        stack.Children.Add(buttons);
        stack.Children.Add(emptyText);
        stack.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "Site permissions",
            Content = new ScrollViewer { Content = stack, MaxHeight = 520 },
            CloseButtonText = "Close",
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
            resetSiteButton.Content = "Reset site";
            clearAllButton.Content = "Clear all";
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
                        $"{permission.Origin} — {SitePermissionCatalog.DisplayName(permission.Kind)} — " +
                        $"{DecisionLabel(permission.Decision)}"))
                    .ToList();
                errorText.Visibility = Visibility.Collapsed;
                ApplyFilter();
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Site permission list failed: {exception.GetType().Name}");
                errorText.Text = "Site permissions could not be loaded.";
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
                ShowError("This permission changed elsewhere. The list was reloaded; review it and try again.");
                await ReloadAsync();
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Site permission update failed: {exception.GetType().Name}");
                ShowError("The permission could not be saved. The list was reloaded.");
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
                ShowError("This permission changed elsewhere. The list was reloaded.");
                await ReloadAsync();
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Site permission delete failed: {exception.GetType().Name}");
                ShowError("The permission could not be removed. The list was reloaded.");
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
                resetSiteButton.Content = "Confirm reset";
                ShowError($"Click Reset site again to remove every permission for {row.Dto.Origin}.");
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
                ShowError("The site permissions could not be reset.");
                await ReloadAsync();
            }
        }

        async Task ClearAllAsync()
        {
            if (!pendingClearAll)
            {
                ResetConfirmations();
                pendingClearAll = true;
                clearAllButton.Content = "Confirm clear";
                ShowError("Click Clear all again to remove every site permission for this profile.");
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
                ShowError("Site permissions could not be cleared.");
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
        await dialog.ShowAsync();
    }

    private static Button MakePermissionButton(string label, string automationId)
    {
        var button = new Button { Content = label, IsEnabled = false };
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private static string DecisionLabel(string decision) =>
        string.Equals(decision, "allow", StringComparison.Ordinal) ? "Allow" : "Block";
}
