using Amare.Windows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Amare.Windows;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _backupCancellation;

    private string DataRoot =>
        Path.Combine(_appDataRoot, "data");

    private BackupClient CreateBackupClient() =>
        new(DataRoot, appRoot: _appDataRoot);

    private async Task ShowBackupRestoreAsync()
    {
        if (_context is null || _profiles is null)
        {
            StatusText.Text = "Profile data is not available.";
            return;
        }
        var backupClient = CreateBackupClient();
        if (!BackupClient.IsHelperAvailable(backupClient.HelperPath))
        {
            await ShowMessageAsync(
                L10n.T("backup.title"),
                L10n.T("backup.helperMissing"));
            return;
        }

        var defaultPath = Path.Combine(
            DataRoot,
            "backups",
            $"AMARE-{_profileId}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.amarebak");
        var pathBox = new TextBox
        {
            Text = defaultPath,
            PlaceholderText = L10n.T("backup.pathPlaceholder"),
            MinWidth = 420
        };
        AutomationProperties.SetAutomationId(pathBox, "BackupPathBox");
        var browseSave = new Button { Content = L10n.T("backup.browseSave") };
        AutomationProperties.SetAutomationId(browseSave, "BackupBrowseSaveButton");
        var browseOpen = new Button { Content = L10n.T("backup.browseOpen") };
        AutomationProperties.SetAutomationId(browseOpen, "BackupBrowseOpenButton");
        browseSave.Click += async (_, _) =>
        {
            var selected = await PickBackupSaveAsync(pathBox.Text);
            if (!string.IsNullOrWhiteSpace(selected)) pathBox.Text = selected;
        };
        browseOpen.Click += async (_, _) =>
        {
            var selected = await PickBackupOpenAsync();
            if (!string.IsNullOrWhiteSpace(selected)) pathBox.Text = selected;
        };

        var includeVaultBox = new CheckBox
        {
            Content = L10n.T("backup.includeVault"),
            IsChecked = false
        };
        AutomationProperties.SetAutomationId(includeVaultBox, "BackupIncludeVaultBox");
        var passwordBox = new PasswordBox
        {
            PlaceholderText = L10n.T("backup.passwordPlaceholder"),
            MinWidth = 320
        };
        AutomationProperties.SetAutomationId(passwordBox, "BackupPasswordBox");
        var confirmBox = new PasswordBox
        {
            PlaceholderText = L10n.T("backup.passwordConfirmPlaceholder"),
            MinWidth = 320
        };
        AutomationProperties.SetAutomationId(confirmBox, "BackupPasswordConfirmBox");
        var encryptionHint = new TextBlock
        {
            Text = L10n.T("backup.encryptionHint"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        var vaultHint = new TextBlock
        {
            Text = L10n.T("backup.vaultHint"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        var status = new TextBox
        {
            Text = L10n.T("backup.ready"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            IsReadOnly = true,
            AcceptsReturn = true,
            MinHeight = 54
        };
        AutomationProperties.SetAutomationId(status, "BackupStatusText");
        var shaText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            IsTextSelectionEnabled = true
        };
        AutomationProperties.SetAutomationId(shaText, "BackupSha256Text");

        var createButton = new Button { Content = L10n.T("backup.create") };
        AutomationProperties.SetAutomationId(createButton, "BackupCreateButton");
        var verifyButton = new Button { Content = L10n.T("backup.verify") };
        AutomationProperties.SetAutomationId(verifyButton, "BackupVerifyButton");
        var restoreButton = new Button { Content = L10n.T("backup.restoreCurrent") };
        AutomationProperties.SetAutomationId(restoreButton, "BackupRestoreButton");
        var restoreNewButton = new Button { Content = L10n.T("backup.restoreNew") };
        AutomationProperties.SetAutomationId(restoreNewButton, "BackupRestoreAsNewButton");
        var cancelButton = new Button { Content = L10n.T("common.cancel"), IsEnabled = false };
        AutomationProperties.SetAutomationId(cancelButton, "BackupCancelButton");
        cancelButton.Click += (_, _) => _backupCancellation?.Cancel();

        var profileIds = new List<string> { _profileId };
        try
        {
            var page = await _profiles.ListAsync(100);
            foreach (var profile in page.Items)
            {
                if (!profileIds.Contains(profile.Id)) profileIds.Add(profile.Id);
            }
        }
        catch
        {
            // The current profile remains a valid explicit target.
        }
        var profileText = new TextBlock
        {
            Text = $"Profile: {_profileId}",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(profileText, "BackupProfileText");
        var targetBox = new ComboBox
        {
            ItemsSource = profileIds,
            SelectedItem = _profileId,
            MinWidth = 220
        };
        AutomationProperties.SetAutomationId(targetBox, "BackupTargetProfileBox");

        var pathRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        pathRow.Children.Add(pathBox);
        pathRow.Children.Add(browseSave);
        pathRow.Children.Add(browseOpen);
        var passwordRow = new StackPanel { Spacing = 6 };
        passwordRow.Children.Add(passwordBox);
        passwordRow.Children.Add(confirmBox);

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(profileText);
        stack.Children.Add(new TextBlock { Text = L10n.T("backup.targetProfile") });
        stack.Children.Add(targetBox);
        stack.Children.Add(new TextBlock
        {
            Text = L10n.T("backup.scopeHint"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock { Text = L10n.T("backup.path") });
        stack.Children.Add(pathRow);
        stack.Children.Add(includeVaultBox);
        stack.Children.Add(vaultHint);
        stack.Children.Add(new TextBlock { Text = L10n.T("backup.password") });
        stack.Children.Add(passwordRow);
        stack.Children.Add(encryptionHint);
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        buttonRow.Children.Add(createButton);
        buttonRow.Children.Add(verifyButton);
        buttonRow.Children.Add(cancelButton);
        stack.Children.Add(buttonRow);
        stack.Children.Add(status);
        stack.Children.Add(shaText);

        var dialog = new ContentDialog
        {
            Title = L10n.T("backup.title"),
            Content = new ScrollViewer { MaxHeight = 640, Content = stack },
            CloseButtonText = L10n.T("common.close"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };

        void SetBusy(bool busy)
        {
            createButton.IsEnabled = !busy;
            verifyButton.IsEnabled = !busy;
            restoreButton.IsEnabled = !busy;
            restoreNewButton.IsEnabled = !busy;
            browseSave.IsEnabled = !busy;
            browseOpen.IsEnabled = !busy;
            cancelButton.IsEnabled = busy;
        }

        BackupOperationResult? lastVerified = null;
        string CurrentPath() => pathBox.Text.Trim();
        string? CurrentPassword() =>
            passwordBox.Password.Length == 0 ? null : passwordBox.Password;

        async Task<BackupOperationResult?> VerifyPathAsync()
        {
            var path = CurrentPath();
            if (path.Length == 0)
            {
                status.Text = L10n.T("backup.pathRequired");
                return null;
            }
            SetBusy(true);
            status.Text = L10n.T("backup.verifying");
            using var cancellation = new CancellationTokenSource();
            _backupCancellation = cancellation;
            try
            {
                var result = await backupClient.VerifyAsync(path, CurrentPassword(), cancellation.Token);
                status.Text = DescribeBackupResult(result);
                lastVerified = result.Valid ? result : null;
                return result;
            }
            catch (OperationCanceledException)
            {
                status.Text = L10n.T("backup.cancelled");
                return null;
            }
            catch (Exception exception)
            {
                status.Text = L10n.T("backup.failed") + ": " + exception.Message;
                return null;
            }
            finally
            {
                _backupCancellation = null;
                SetBusy(false);
            }
        }

        createButton.Click += async (_, _) =>
        {
            if (includeVaultBox.IsChecked == true && CurrentPassword() == null)
            {
                status.Text = L10n.T("backup.vaultPasswordRequired");
                return;
            }
            if (passwordBox.Password != confirmBox.Password)
            {
                status.Text = L10n.T("backup.passwordMismatch");
                return;
            }
            var path = CurrentPath();
            if (path.Length == 0)
            {
                status.Text = L10n.T("backup.pathRequired");
                return;
            }
            if (File.Exists(path))
            {
                var overwrite = await new ContentDialog
                {
                    Title = L10n.T("backup.title"),
                    Content = L10n.T("backup.overwrite"),
                    PrimaryButtonText = L10n.T("backup.overwriteYes"),
                    CloseButtonText = L10n.T("common.cancel"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = RootGrid.XamlRoot
                }.ShowAsync();
                if (overwrite != ContentDialogResult.Primary) return;
            }
            SetBusy(true);
            status.Text = L10n.T("backup.creating");
            using var cancellation = new CancellationTokenSource();
            _backupCancellation = cancellation;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var result = await backupClient.CreateAsync(
                    path, _profileId, includeVaultBox.IsChecked == true, CurrentPassword(),
                    cancellationToken: cancellation.Token);
                if (!result.Succeeded)
                {
                    status.Text = result.Error ?? L10n.T("backup.failed");
                    shaText.Text = "";
                    return;
                }
                status.Text = L10n.T("backup.created", result.SizeBytes ?? 0);
                shaText.Text = $"SHA-256: {result.Sha256}";
                lastVerified = null;
            }
            catch (Exception exception)
            {
                status.Text = L10n.T("backup.failed") + ": " + exception.Message;
            }
            finally
            {
                passwordBox.Password = "";
                confirmBox.Password = "";
                _backupCancellation = null;
                SetBusy(false);
            }
        };

        verifyButton.Click += async (_, _) =>
        {
            var result = await VerifyPathAsync();
            if (result is { Valid: true, Manifest: not null })
            {
                shaText.Text = result.Manifest.ContainsVault
                    ? L10n.T("backup.vaultEntries", result.Vault.Sum(v => v.EntryCount))
                    : "";
            }
        };

        async Task StartRestoreAsync(bool asNew)
        {
            var verified = lastVerified;
            if (verified == null)
            {
                verified = await VerifyPathAsync();
                if (verified == null || !verified.Valid) return;
            }
            var manifest = verified.Manifest;
            if (manifest == null || manifest.Profiles.Count != 1)
            {
                status.Text = L10n.T("backup.singleProfileOnly");
                return;
            }
            var sourceId = manifest.Profiles[0].Id;
            string targetId = targetBox.SelectedItem as string ?? _profileId;
            if (asNew)
            {
                var newId = await PromptForNewProfileAsync();
                if (newId == null) return;
                targetId = newId;
            }
            if (manifest.ContainsVault && CurrentPassword() == null)
            {
                status.Text = L10n.T("backup.vaultPasswordRequired");
                return;
            }
            var warning = asNew
                ? L10n.T("backup.confirmNew", sourceId, targetId)
                : L10n.T("backup.confirmCurrent", targetId);
            var confirmed = await new ContentDialog
            {
                Title = L10n.T("backup.restoreTitle"),
                Content = warning,
                PrimaryButtonText = L10n.T("backup.restoreConfirm"),
                CloseButtonText = L10n.T("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            }.ShowAsync();
            if (confirmed != ContentDialogResult.Primary) return;
            try
            {
                var appExe = Path.Combine(
                    AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                    "AMARE.Windows.exe");
                _ = backupClient.StartRestore(
                    CurrentPath(), sourceId, targetId, appExe, CurrentPassword());
                status.Text = L10n.T("backup.restoreStarted");
                passwordBox.Password = "";
                confirmBox.Password = "";
                await Task.Delay(300);
                Close();
                Application.Current.Exit();
            }
            catch (Exception exception)
            {
                status.Text = L10n.T("backup.restoreFailed") + ": " + exception.Message;
            }
        }

        restoreButton.Click += async (_, _) => await StartRestoreAsync(false);
        restoreNewButton.Click += async (_, _) => await StartRestoreAsync(true);

        var restoreRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        restoreRow.Children.Add(restoreButton);
        restoreRow.Children.Add(restoreNewButton);
        stack.Children.Add(restoreRow);

        await dialog.ShowAsync();
    }

    internal async Task RecoverInterruptedRestoreAsync()
    {
        try
        {
            var client = CreateBackupClient();
            if (!BackupClient.IsHelperAvailable(client.HelperPath)) return;
            var result = await client.RecoverAsync();
            if (result.Status == "ok" && result.Recovered)
            {
                AppLog.Warn("Interrupted profile restore was rolled back before startup.");
            }
        }
        catch (Exception exception)
        {
            AppLog.Warn($"Interrupted restore recovery failed: {exception.GetType().Name}");
        }
    }

    internal void ShowPendingRestoreResult()
    {
        try
        {
            var path = CreateBackupClient().RestoreResultPath;
            if (!File.Exists(path)) return;
            var result = System.Text.Json.JsonSerializer.Deserialize<BackupOperationResult>(
                File.ReadAllText(path), JsonDefaults.Options);
            File.Delete(path);
            if (result == null) return;
            StatusText.Text = result.Succeeded
                ? L10n.T("backup.restoreCompleted", string.Join(",", result.RestoredProfiles))
                : $"{L10n.T("backup.restoreFailed")}: {result.Error}";
        }
        catch (Exception exception)
        {
            AppLog.Warn($"Restore result could not be read: {exception.GetType().Name}");
        }
    }

    private async Task<string?> PromptForNewProfileAsync()
    {
        var idBox = new TextBox { PlaceholderText = "profile-id" };
        AutomationProperties.SetAutomationId(idBox, "BackupNewProfileIdBox");
        var nameBox = new TextBox { PlaceholderText = "Profile name" };
        AutomationProperties.SetAutomationId(nameBox, "BackupNewProfileNameBox");
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = L10n.T("backup.newProfileId") });
        panel.Children.Add(idBox);
        panel.Children.Add(new TextBlock { Text = L10n.T("backup.newProfileName") });
        panel.Children.Add(nameBox);
        var dialog = new ContentDialog
        {
            Title = L10n.T("backup.restoreNew"),
            Content = panel,
            PrimaryButtonText = L10n.T("common.create"),
            CloseButtonText = L10n.T("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var id = idBox.Text.Trim().ToLowerInvariant();
        var name = nameBox.Text.Trim();
        if (id.Length == 0 || name.Length == 0 || _profiles is null)
        {
            await ShowMessageAsync(L10n.T("backup.title"), L10n.T("backup.newProfileInvalid"));
            return null;
        }
        try
        {
            await _profiles.CreateAsync(id, name);
            return id;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(L10n.T("backup.title"),
                L10n.T("backup.newProfileFailed") + ": " + exception.Message);
            return null;
        }
    }

    private string DescribeBackupResult(BackupOperationResult result)
    {
        if (result.Valid && result.Manifest is { } manifest)
        {
            var vault = manifest.ContainsVault
                ? L10n.T("backup.manifestVault", result.Vault.Sum(v => v.EntryCount))
                : L10n.T("backup.manifestNoVault");
            var warning = result.Status == "ValidWithWarnings"
                ? " " + L10n.T("backup.legacyWarning")
                : "";
            return L10n.T(
                "backup.valid",
                manifest.AppVersion,
                string.Join(",", manifest.Profiles.Select(p => p.Id)),
                vault) + warning;
        }
        return result.Status switch
        {
            "InvalidPassword" => L10n.T("backup.invalidPassword"),
            "UnsupportedVersion" => L10n.T("backup.unsupportedVersion"),
            "IncompatibleSchema" => L10n.T("backup.incompatibleSchema"),
            "UnsafeArchive" => L10n.T("backup.unsafeArchive"),
            "Corrupt" => L10n.T("backup.corrupt"),
            _ => result.Error ?? L10n.T("backup.invalid")
        };
    }

    private async Task<string?> PickBackupSaveAsync(string suggestedPath)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedPath),
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeChoices.Add("AMARE backup", new List<string> { ".amarebak" });
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> PickBackupOpenAsync()
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add(".amarebak");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch
        {
            return null;
        }
    }
}
