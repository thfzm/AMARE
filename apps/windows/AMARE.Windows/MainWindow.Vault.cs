using System.Text;
using Amare.Windows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Amare.Windows;

public sealed partial class MainWindow
{
    private enum VaultUiState
    {
        Closed,
        Uninitialized,
        Locked,
        Loading,
        Unlocked,
        Error
    }

    private AmareVaultClient? _vaultClient;
    private VaultClipboard? _vaultClipboard;
    private string? _vaultClientProfile;
    private ulong _vaultClientGeneration;
    private VaultUiState _vaultState = VaultUiState.Closed;
    private bool _vaultOpen;
    private bool _vaultBusy;
    private bool _suppressVaultSelection;
    private List<VaultEntryViewDto> _vaultEntries = [];
    private VaultEntryViewDto? _vaultSelected;
    private VaultEntryViewDto? _vaultEditing;
    private bool _vaultRevealed;
    private CancellationTokenSource? _vaultOperation;

    private void WireVaultLifetime()
    {
        _vaultClipboard = new VaultClipboard(_dispatcher);
        VaultIdleMonitor.Start();
        VaultSessionMonitor.Start();
        VaultIdleMonitor.Expired += OnVaultIdleExpired;
        VaultSessionMonitor.Locked += OnVaultSessionLocked;
        VaultWindowCoordinator.LockRequested += OnVaultLockRequested;
        VaultWindowCoordinator.ProfileChanged += OnVaultProfileChanged;
        VaultWindowCoordinator.Unlocked += OnVaultUnlocked;
        RootGrid.PointerMoved += (_, _) => VaultIdleMonitor.Touch();
        RootGrid.PointerPressed += (_, _) => VaultIdleMonitor.Touch();
        RootGrid.KeyDown += (_, _) => VaultIdleMonitor.Touch();
        Activated += (_, _) => VaultIdleMonitor.Touch();
    }

    private void UnwireVaultLifetime()
    {
        VaultIdleMonitor.Expired -= OnVaultIdleExpired;
        VaultSessionMonitor.Locked -= OnVaultSessionLocked;
        VaultWindowCoordinator.LockRequested -= OnVaultLockRequested;
        VaultWindowCoordinator.ProfileChanged -= OnVaultProfileChanged;
        VaultWindowCoordinator.Unlocked -= OnVaultUnlocked;
    }

    private string VaultNativeRoot => Path.Combine(_appDataRoot, "data");

    private bool EnsureVaultClient()
    {
        if (_context is null)
        {
            return false;
        }
        if (_vaultClient is not null
            && _vaultClientProfile == _context.ProfileId
            && _vaultClientGeneration == _context.Generation)
        {
            return true;
        }
        DisposeVaultClient();
        try
        {
            _vaultClient = AmareVaultClient.Create(VaultNativeRoot);
            _vaultClientProfile = _context.ProfileId;
            _vaultClientGeneration = _context.Generation;
            return true;
        }
        catch (Exception exception) when (exception is VaultException or IOException or UnauthorizedAccessException)
        {
            _vaultClient = null;
            _vaultClientProfile = null;
            ShowVaultError(exception);
            return false;
        }
    }

    private void DisposeVaultClient()
    {
        InvalidateWebViewVaultOperations();
        _vaultClient?.Dispose();
        _vaultClient = null;
        _vaultClientProfile = null;
        _vaultClientGeneration = 0;
    }

    private async void VaultPrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        await VaultPrimaryActionAsync();
    }

    private async Task VaultPrimaryActionAsync()
    {
        if (_vaultBusy || _context is null)
        {
            return;
        }
        if (!EnsureVaultClient())
        {
            return;
        }
        if (_vaultState == VaultUiState.Uninitialized)
        {
            await RunVaultOperationAsync(async () =>
            {
                var status = await _vaultClient!.InitializeAsync(_context);
                ApplyVaultStatus(status);
                await LoadVaultEntriesAsync();
                VaultWindowCoordinator.NotifyUnlocked(_windowId, _profileId);
                StatusText.Text = "Password vault created";
            });
        }
        else if (_vaultState == VaultUiState.Locked || _vaultState == VaultUiState.Error)
        {
            await RunVaultOperationAsync(async () =>
            {
                var status = await _vaultClient!.UnlockAsync(_context);
                ApplyVaultStatus(status);
                await LoadVaultEntriesAsync();
                VaultWindowCoordinator.NotifyUnlocked(_windowId, _profileId);
                StatusText.Text = "Password vault unlocked";
            });
        }
        else
        {
            await RefreshVaultStateAsync();
        }
    }

    private async Task RefreshVaultStateAsync()
    {
        if (!_vaultOpen || _context is null || !EnsureVaultClient())
        {
            return;
        }
        await RunVaultOperationAsync(async () =>
        {
            var status = await _vaultClient!.StatusAsync(_context);
            ApplyVaultStatus(status);
            if (status.Unlocked)
            {
                await LoadVaultEntriesAsync();
            }
        }, showLoading: false);
    }

    private void ApplyVaultStatus(VaultStatusDto status)
    {
        AppLog.Info($"Vault status applied initialized={status.Initialized} unlocked={status.Unlocked} " +
                    $"entries={status.EntryCount} generation={status.Generation}");
        VaultErrorText.Visibility = Visibility.Collapsed;
        VaultLoadingRing.IsActive = false;
        VaultLoadingRing.Visibility = Visibility.Collapsed;
        VaultDetailPanel.Visibility = Visibility.Collapsed;
        VaultEditorPanel.Visibility = Visibility.Collapsed;
        ClearVaultSecret();
        _vaultEntries = status.Unlocked
            ? _vaultEntries
            : [];
        if (!status.Unlocked)
        {
            VaultEntryList.ItemsSource = null;
            VaultEmptyText.Visibility = Visibility.Collapsed;
        }

        if (!status.Initialized)
        {
            _vaultState = VaultUiState.Uninitialized;
            VaultStatusText.Text = L10n.T("vault.notInitialized");
            VaultPrimaryActionButton.Content = "Create vault";
            VaultPrimaryActionButton.IsEnabled = true;
            VaultLockButton.IsEnabled = false;
            VaultAddButton.IsEnabled = false;
            VaultBackupButton.IsEnabled = false;
            VaultLockedTitle.Text = L10n.T("vault.create");
            VaultLockedDetail.Text =
                "The vault is protected by Windows user credentials. The key is stored separately from the database.";
            VaultLockedPanel.Visibility = Visibility.Visible;
        }
        else if (!status.Unlocked)
        {
            _vaultState = VaultUiState.Locked;
            VaultStatusText.Text = L10n.T("vault.lockedCount", status.Generation);
            VaultPrimaryActionButton.Content = "Unlock";
            VaultPrimaryActionButton.IsEnabled = true;
            VaultLockButton.IsEnabled = false;
            VaultAddButton.IsEnabled = false;
            VaultBackupButton.IsEnabled = false;
            VaultLockedTitle.Text = "Vault is locked";
            VaultLockedDetail.Text =
                "Unlock with Windows protection to view saved accounts. Passwords are never shown in the list.";
            VaultLockedPanel.Visibility = Visibility.Visible;
        }
        else
        {
            _vaultState = VaultUiState.Unlocked;
            VaultStatusText.Text = L10n.T("vault.unlockedCount", status.EntryCount);
            VaultPrimaryActionButton.Content = "Refresh";
            VaultPrimaryActionButton.IsEnabled = true;
            VaultLockButton.IsEnabled = true;
            VaultAddButton.IsEnabled = true;
            VaultBackupButton.IsEnabled = true;
            VaultLockedPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadVaultEntriesAsync()
    {
        if (_context is null || _vaultClient is null)
        {
            return;
        }
        var entries = (await _vaultClient.ListAsync(_context)).ToList();
        _vaultEntries = entries;
        _suppressVaultSelection = true;
        VaultEntryList.ItemsSource = entries;
        _suppressVaultSelection = false;
        VaultEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (entries.Count == 0)
        {
            _vaultSelected = null;
            VaultDetailPanel.Visibility = Visibility.Collapsed;
            ClearVaultSecret();
            return;
        }
        var keep = _vaultSelected is null
            ? null
            : entries.FirstOrDefault(entry => entry.Id == _vaultSelected.Id);
        var select = keep ?? entries[0];
        VaultEntryList.SelectedItem = select;
        ShowVaultDetail(select);
    }

    private void VaultEntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressVaultSelection || VaultEntryList.SelectedItem is not VaultEntryViewDto entry)
        {
            return;
        }
        ShowVaultDetail(entry);
    }

    private void ShowVaultDetail(VaultEntryViewDto entry)
    {
        _vaultSelected = entry;
        ClearVaultSecret();
        VaultEditorPanel.Visibility = Visibility.Collapsed;
        VaultLockedPanel.Visibility = Visibility.Collapsed;
        VaultDetailTitle.Text = string.IsNullOrWhiteSpace(entry.Title) ? entry.Origin : entry.Title;
        VaultDetailOrigin.Text = entry.Origin;
        VaultDetailUsername.Text = entry.Username;
        VaultDetailPassword.Password = "";
        VaultDetailPassword.PasswordRevealMode = PasswordRevealMode.Hidden;
        VaultDetailPassword.IsEnabled = true;
        VaultDetailPassword.IsHitTestVisible = false;
        VaultDetailPassword.IsTabStop = false;
        VaultRevealStateText.Text = L10n.T("vault.passwordHidden");
        VaultRevealButton.Content = "Reveal";
        VaultRevealedState(false);
        VaultDetailPanel.Visibility = Visibility.Visible;
    }

    private void ClearVaultSecret()
    {
        _vaultRevealed = false;
        VaultDetailPassword.Password = "";
        VaultDetailPassword.PasswordRevealMode = PasswordRevealMode.Hidden;
        VaultRevealStateText.Text = L10n.T("vault.passwordHidden");
        VaultRevealButton.Content = "Reveal";
    }

    private void VaultRevealedState(bool revealed)
    {
        _vaultRevealed = revealed;
        VaultRevealButton.Content = revealed ? "Hide" : "Reveal";
        VaultRevealStateText.Text = revealed ? L10n.T("vault.passwordVisible") : L10n.T("vault.passwordHidden");
    }

    private async void VaultReveal_Click(object sender, RoutedEventArgs e)
    {
        if (_vaultBusy || _context is null || _vaultClient is null || _vaultSelected is null)
        {
            return;
        }
        if (_vaultRevealed)
        {
            ClearVaultSecret();
            return;
        }
        await RunVaultOperationAsync(async () =>
        {
            var secret = await _vaultClient.GetAsync(_context, _vaultSelected.Id);
            try
            {
                VaultDetailPassword.Password = secret.Password;
                VaultDetailPassword.PasswordRevealMode = PasswordRevealMode.Visible;
                VaultRevealedState(true);
            }
            finally
            {
                secret.ClearPassword();
            }
        });
    }

    private async void VaultCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_vaultBusy || _context is null || _vaultClient is null || _vaultSelected is null)
        {
            return;
        }
        await RunVaultOperationAsync(async () =>
        {
            var secret = await _vaultClient.GetAsync(_context, _vaultSelected.Id);
            try
            {
                _vaultClipboard?.Copy(secret.Password);
                StatusText.Text = "Password copied. Clipboard cleanup is best-effort after 30 seconds.";
            }
            finally
            {
                secret.ClearPassword();
            }
        });
    }

    private async void VaultAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_vaultState != VaultUiState.Unlocked)
        {
            return;
        }
        _vaultEditing = null;
        VaultLockedPanel.Visibility = Visibility.Collapsed;
        VaultDetailPanel.Visibility = Visibility.Visible;
        VaultEditorTitle.Text = L10n.T("vault.addAccount");
        VaultSiteBox.Text = "";
        VaultOriginBox.Text = "";
        VaultUsernameBox.Text = "";
        VaultPasswordInput.Password = "";
        VaultShowPasswordCheck.IsChecked = false;
        VaultPasswordInput.PasswordRevealMode = PasswordRevealMode.Hidden;
        VaultEditorPanel.Visibility = Visibility.Visible;
        VaultIdleMonitor.Touch();
        await Task.CompletedTask;
    }

    private async void VaultEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_vaultBusy || _context is null || _vaultClient is null || _vaultSelected is null)
        {
            return;
        }
        await RunVaultOperationAsync(async () =>
        {
            var secret = await _vaultClient.GetAsync(_context, _vaultSelected.Id);
            try
            {
                _vaultEditing = _vaultSelected;
                VaultEditorTitle.Text = L10n.T("vault.editAccount");
                VaultSiteBox.Text = _vaultSelected.Title;
                VaultOriginBox.Text = _vaultSelected.Origin;
                VaultUsernameBox.Text = _vaultSelected.Username;
                VaultPasswordInput.Password = secret.Password;
                VaultShowPasswordCheck.IsChecked = false;
                VaultPasswordInput.PasswordRevealMode = PasswordRevealMode.Hidden;
                VaultEditorPanel.Visibility = Visibility.Visible;
            }
            finally
            {
                secret.ClearPassword();
            }
        });
    }

    private void VaultShowPassword_Changed(object sender, RoutedEventArgs e)
    {
        VaultPasswordInput.PasswordRevealMode = VaultShowPasswordCheck.IsChecked == true
            ? PasswordRevealMode.Visible
            : PasswordRevealMode.Hidden;
    }

    private async void VaultSave_Click(object sender, RoutedEventArgs e)
    {
        if (_vaultBusy || _context is null || _vaultClient is null)
        {
            return;
        }
        var title = VaultSiteBox.Text.Trim();
        var origin = VaultOriginBox.Text.Trim();
        var username = VaultUsernameBox.Text.Trim();
        var password = VaultPasswordInput.Password;
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(username) || password.Length == 0)
        {
            ShowVaultMessage("Origin, username and password are required.", error: true);
            return;
        }
        var editing = _vaultEditing;
        await RunVaultOperationAsync(async () =>
        {
            if (editing is null)
            {
                var created = await _vaultClient.CreateAsync(_context, origin, username, password, title);
                _vaultSelected = created;
            }
            else
            {
                var updated = await _vaultClient.UpdateAsync(
                    _context, editing.Id, editing.Revision, origin, username, password, title);
                _vaultSelected = updated;
            }
            VaultEditorPanel.Visibility = Visibility.Collapsed;
            VaultPasswordInput.Password = "";
            _vaultEditing = null;
            await LoadVaultEntriesAsync();
            StatusText.Text = editing is null ? "Account added" : "Account updated";
        }, preserveEditorOnConflict: true);
    }

    private void VaultCancelEdit_Click(object sender, RoutedEventArgs e)
    {
        VaultEditorPanel.Visibility = Visibility.Collapsed;
        VaultPasswordInput.Password = "";
        _vaultEditing = null;
    }

    private async void VaultDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_vaultBusy || _context is null || _vaultClient is null || _vaultSelected is null)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "Delete account?",
            Content = "The saved account will be removed from this vault. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        await RunVaultOperationAsync(async () =>
        {
            await _vaultClient.DeleteAsync(_context, _vaultSelected.Id, _vaultSelected.Revision);
            _vaultSelected = null;
            await LoadVaultEntriesAsync();
            StatusText.Text = "Account deleted";
        }, preserveEditorOnConflict: true);
    }

    private async void VaultLock_Click(object sender, RoutedEventArgs e)
    {
        await LockVaultAsync(broadcast: true, reason: "Vault locked by user.");
    }

    private async void VaultClose_Click(object sender, RoutedEventArgs e)
    {
        await CloseVaultPanelAsync();
    }

    private async Task OpenVaultPanelAsync()
    {
        AppLog.Info("Vault panel requested");
        if (_vaultOpen)
        {
            await CloseVaultPanelAsync();
            return;
        }
        CloseFind();
        CloseReader();
        CloseAiPanel();
        WebViewHost.Visibility = Visibility.Collapsed;
        VaultHost.Visibility = Visibility.Visible;
        _vaultOpen = true;
        AppLog.Info("Vault panel visible");
        VaultIdleMonitor.Touch();
        if (!EnsureVaultClient())
        {
            AppLog.Warn("Vault client creation failed");
            _vaultState = VaultUiState.Error;
            VaultStatusText.Text = L10n.T("vault.unavailable");
            VaultPrimaryActionButton.IsEnabled = false;
            VaultLockedPanel.Visibility = Visibility.Visible;
            VaultLockedTitle.Text = L10n.T("vault.unavailableTitle");
            VaultLockedDetail.Text = L10n.T("vault.unavailableDetail");
            return;
        }
        AppLog.Info("Vault client ready");
        await RefreshVaultStateAsync();
    }

    private async Task CloseVaultPanelAsync()
    {
        if (!_vaultOpen)
        {
            return;
        }
        // Hiding the panel must not lock the native vault: password capture and
        // explicit fill happen while the user browses. Idle/session/manual lock,
        // profile switch and app shutdown still lock it.
        _vaultOpen = false;
        VaultHost.Visibility = Visibility.Collapsed;
        WebViewHost.Visibility = Visibility.Visible;
        VaultEditorPanel.Visibility = Visibility.Collapsed;
        VaultDetailPassword.Password = "";
        VaultPasswordInput.Password = "";
        VaultIdleMonitor.Touch();
        ClearVaultSecret();
        await Task.CompletedTask;
    }

    private async Task LockVaultAsync(bool broadcast, string reason)
    {
        if (_vaultClient is not null && _context is not null)
        {
            try
            {
                await _vaultClient.LockAsync(_context);
            }
            catch (VaultException exception) when (exception.Code != VaultErrorCode.Unsupported)
            {
                ShowVaultError(exception);
            }
            catch (Exception exception)
            {
                ShowVaultError(exception);
            }
        }
        _vaultState = VaultUiState.Locked;
        _vaultEntries = [];
        _vaultSelected = null;
        _vaultEditing = null;
        VaultEntryList.ItemsSource = null;
        VaultDetailPanel.Visibility = Visibility.Collapsed;
        VaultEditorPanel.Visibility = Visibility.Collapsed;
        VaultEmptyText.Visibility = Visibility.Collapsed;
        VaultDetailPassword.Password = "";
        VaultPasswordInput.Password = "";
        ClearVaultSecret();
        if (_vaultClient is not null)
        {
            VaultLockedPanel.Visibility = Visibility.Visible;
            VaultLockedTitle.Text = "Vault is locked";
            VaultLockedDetail.Text = reason;
            VaultPrimaryActionButton.Content = "Unlock";
            VaultPrimaryActionButton.IsEnabled = true;
            VaultLockButton.IsEnabled = false;
            VaultAddButton.IsEnabled = false;
            VaultBackupButton.IsEnabled = false;
            VaultStatusText.Text = L10n.T("vault.lockedShort");
        }
        if (_vaultClipboard is not null)
        {
            await _vaultClipboard.ClearIfOwnedAsync();
        }
        InvalidateWebViewVaultOperations();
        if (broadcast)
        {
            VaultWindowCoordinator.RequestLock(_windowId);
        }
    }

    private async Task UnlockFromPeerAsync()
    {
        if (_context is null || !EnsureVaultClient())
        {
            return;
        }
        await RunVaultOperationAsync(async () =>
        {
            var status = await _vaultClient!.UnlockAsync(_context);
            ApplyVaultStatus(status);
            await LoadVaultEntriesAsync();
        }, showLoading: false);
    }

    private async Task RunVaultOperationAsync(
        Func<Task> action,
        bool showLoading = true,
        bool preserveEditorOnConflict = false)
    {
        if (_vaultBusy)
        {
            return;
        }
        _vaultBusy = true;
        VaultIdleMonitor.Touch();
        if (showLoading)
        {
            VaultLoadingRing.IsActive = true;
            VaultLoadingRing.Visibility = Visibility.Visible;
        }
        VaultErrorText.Visibility = Visibility.Collapsed;
        _vaultOperation?.Cancel();
        _vaultOperation = new CancellationTokenSource();
        try
        {
            await action();
        }
        catch (VaultException exception)
        {
            if (exception.Code == VaultErrorCode.Conflict)
            {
                ShowVaultMessage(
                    "This account changed in another window. The latest list was reloaded; review before saving again.",
                    error: true);
                await LoadVaultEntriesAsync();
                if (preserveEditorOnConflict && _vaultSelected is not null)
                {
                    _vaultEditing = _vaultEntries.FirstOrDefault(entry => entry.Id == _vaultSelected.Id);
                    VaultEditorPanel.Visibility = Visibility.Visible;
                }
            }
            else
            {
                ShowVaultError(exception);
            }
        }
        catch (Exception exception)
        {
            ShowVaultError(exception);
        }
        finally
        {
            _vaultBusy = false;
            VaultLoadingRing.IsActive = false;
            VaultLoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowVaultError(Exception exception)
    {
        var code = exception is VaultException vault ? vault.Code : VaultErrorCode.Unsupported;
        AppLog.Warn($"Vault operation error code={code} type={exception.GetType().Name}");
        var message = exception is VaultException vaultError
            ? VaultMessage(vaultError.Code)
            : "The vault operation failed.";
        ShowVaultMessage(message, error: true);
    }

    private static string VaultMessage(VaultErrorCode code) => code switch
    {
        VaultErrorCode.Locked => "The vault is locked.",
        VaultErrorCode.Auth => "The vault could not be authenticated.",
        VaultErrorCode.Corrupt => "The vault data is damaged. Existing data was not overwritten.",
        VaultErrorCode.Conflict => "The account changed in another window. Reload the latest data before saving.",
        VaultErrorCode.Unsupported => "The vault is not available on this system.",
        VaultErrorCode.KeyUnavailable => "The Windows-protected vault key is unavailable.",
        VaultErrorCode.Profile => "The active profile context has changed. Reopen the vault.",
        VaultErrorCode.NotFound => "The account no longer exists.",
        VaultErrorCode.OriginMismatch => "The account origin does not match this site.",
        _ => "The vault operation failed."
    };

    private void ShowVaultMessage(string message, bool error)
    {
        VaultErrorText.Text = message;
        VaultErrorText.Visibility = Visibility.Visible;
        if (error)
        {
            StatusText.Text = message;
        }
    }

    private async Task ResetVaultForProfileChangeAsync(bool broadcast = true)
    {
        CancelAiRequest();
        CancelPendingPermissionRequest();
        CancelSitePermissionsDialog();
        if (_vaultClient is not null && _context is not null)
        {
            try
            {
                await _vaultClient.LockAsync(_context);
            }
            catch
            {
                // Handle disposal still drops native state.
            }
        }
        DisposeVaultClient();
        _vaultEntries = [];
        _vaultSelected = null;
        _vaultEditing = null;
        _vaultState = VaultUiState.Closed;
        _vaultOpen = false;
        VaultHost.Visibility = Visibility.Collapsed;
        WebViewHost.Visibility = Visibility.Visible;
        VaultEntryList.ItemsSource = null;
        VaultDetailPanel.Visibility = Visibility.Collapsed;
        VaultEditorPanel.Visibility = Visibility.Collapsed;
        VaultDetailPassword.Password = "";
        VaultPasswordInput.Password = "";
        ClearVaultSecret();
        _vaultClipboard?.Forget();
        if (broadcast)
        {
            VaultWindowCoordinator.RequestLock(_windowId);
        }
    }

    private async Task OnVaultIdleExpiredAsync()
    {
        VaultIdleMonitor.Touch();
        if (_vaultState == VaultUiState.Unlocked)
        {
            await LockVaultAsync(broadcast: true, reason: "Vault locked after 5 minutes of inactivity.");
        }
    }

    private void OnVaultIdleExpired()
    {
        _dispatcher.TryEnqueue(() => _ = OnVaultIdleExpiredAsync());
    }

    private void OnVaultSessionLocked()
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_vaultState == VaultUiState.Unlocked)
            {
                _ = LockVaultAsync(broadcast: true, reason: "Vault locked because Windows session locked.");
            }
        });
    }

    private void OnVaultLockRequested(Guid sender)
    {
        if (sender == _windowId)
        {
            return;
        }
        _dispatcher.TryEnqueue(() =>
        {
            if (_vaultState == VaultUiState.Unlocked)
            {
                _ = LockVaultAsync(broadcast: false, reason: "Vault locked by another window.");
            }
        });
    }

    private void OnVaultProfileChanged(Guid sender)
    {
        if (sender == _windowId)
        {
            return;
        }
        _dispatcher.TryEnqueue(() => _ = ResetVaultForProfileChangeAsync(broadcast: false));
    }

    private void OnVaultUnlocked(Guid sender, string profileId)
    {
        if (sender == _windowId || profileId != _profileId)
        {
            return;
        }
        _dispatcher.TryEnqueue(() => _ = UnlockFromPeerAsync());
    }

    private void TryLockVaultForShutdown()
    {
        try
        {
            if (_vaultClient is not null && _context is not null)
            {
                var lockTask = Task.Run(() => _vaultClient.LockAsync(_context));
                lockTask.Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            // Dispose still releases the native handle.
        }
        DisposeVaultClient();
        _vaultClipboard?.Forget();
    }

    private async void VaultBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_vaultState != VaultUiState.Unlocked)
        {
            return;
        }
        var menu = new MenuFlyout();
        var export = new MenuFlyoutItem { Text = "Export encrypted backup…" };
        export.Click += async (_, _) => await ShowExportBackupDialogAsync();
        var validate = new MenuFlyoutItem { Text = "Validate backup…" };
        validate.Click += async (_, _) => await ShowValidateBackupDialogAsync();
        var import = new MenuFlyoutItem { Text = "Import backup…" };
        import.Click += async (_, _) => await ShowImportBackupDialogAsync();
        menu.Items.Add(export);
        menu.Items.Add(validate);
        menu.Items.Add(import);
        menu.ShowAt(VaultBackupButton);
        await Task.CompletedTask;
    }

    private string DefaultVaultBackupPath =>
        Path.Combine(_appDataRoot, "vault-backups", $"vault-{DateTime.UtcNow:yyyyMMdd-HHmmss}.amarevault");

    private async Task ShowExportBackupDialogAsync()
    {
        if (_context is null || _vaultClient is null)
        {
            return;
        }
        var password = new PasswordBox { PlaceholderText = "Backup password" };
        var confirm = new PasswordBox { PlaceholderText = "Confirm password" };
        var path = new TextBox { Text = DefaultVaultBackupPath };
        AutomationProperties.SetAutomationId(password, "VaultBackupPassword");
        AutomationProperties.SetAutomationId(confirm, "VaultBackupPasswordConfirm");
        AutomationProperties.SetAutomationId(path, "VaultBackupPath");
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "Encrypted backup password (required)" });
        stack.Children.Add(password);
        stack.Children.Add(confirm);
        stack.Children.Add(new TextBlock { Text = "File path" });
        stack.Children.Add(path);
        stack.Children.Add(new TextBlock
        {
            Text = "The file contains only encrypted vault data. The Windows key is not included.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11
        });
        var dialog = new ContentDialog
        {
            Title = "Export encrypted backup",
            Content = stack,
            PrimaryButtonText = "Export",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        if (password.Password.Length == 0 || password.Password != confirm.Password)
        {
            ShowVaultMessage("Backup passwords are empty or do not match.", error: true);
            return;
        }
        await RunVaultOperationAsync(async () =>
        {
            var backup = await _vaultClient.ExportBackupAsync(_context, password.Password);
            var bytes = Convert.FromHexString(backup.PayloadHex);
            var target = path.Text.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new VaultException(VaultErrorCode.InvalidArgument, "A backup path is required.");
            }
            var directory = Path.GetDirectoryName(Path.GetFullPath(target));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var temp = target + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, target, overwrite: true);
            StatusText.Text = $"Encrypted backup exported: {Path.GetFileName(target)}";
        });
        password.Password = "";
        confirm.Password = "";
    }

    private async Task ShowValidateBackupDialogAsync()
    {
        var path = new TextBox { Text = DefaultVaultBackupPath };
        var password = new PasswordBox { PlaceholderText = "Backup password" };
        AutomationProperties.SetAutomationId(path, "VaultBackupPath");
        AutomationProperties.SetAutomationId(password, "VaultBackupPassword");
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "Backup file path" });
        stack.Children.Add(path);
        stack.Children.Add(new TextBlock { Text = "Backup password" });
        stack.Children.Add(password);
        var dialog = new ContentDialog
        {
            Title = "Validate encrypted backup",
            Content = stack,
            PrimaryButtonText = "Validate",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        try
        {
            var bytes = await File.ReadAllBytesAsync(path.Text.Trim());
            var summary = await _vaultClient!.ValidateBackupAsync(
                _context!, Convert.ToHexString(bytes), password.Password);
            StatusText.Text =
                $"Backup valid: {summary.EntryCount} accounts, {summary.TombstoneCount} deleted, format {summary.Format}";
        }
        catch (Exception exception) when (exception is VaultException or IOException or ArgumentException)
        {
            ShowVaultError(exception);
        }
        finally
        {
            password.Password = "";
        }
    }

    private async Task ShowImportBackupDialogAsync()
    {
        var path = new TextBox { Text = DefaultVaultBackupPath };
        var password = new PasswordBox { PlaceholderText = "Backup password" };
        AutomationProperties.SetAutomationId(path, "VaultBackupPath");
        AutomationProperties.SetAutomationId(password, "VaultBackupPassword");
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = "Import replaces the current vault after validation. Export a current backup first if needed.",
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock { Text = "Backup file path" });
        stack.Children.Add(path);
        stack.Children.Add(new TextBlock { Text = "Backup password" });
        stack.Children.Add(password);
        var dialog = new ContentDialog
        {
            Title = "Import encrypted backup",
            Content = stack,
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        try
        {
            var bytes = await File.ReadAllBytesAsync(path.Text.Trim());
            await _vaultClient!.ImportBackupAsync(
                _context!, Convert.ToHexString(bytes), password.Password);
            await RefreshVaultStateAsync();
            StatusText.Text = "Encrypted backup imported";
        }
        catch (Exception exception) when (exception is VaultException or IOException or ArgumentException)
        {
            ShowVaultError(exception);
        }
        finally
        {
            password.Password = "";
        }
    }
}
