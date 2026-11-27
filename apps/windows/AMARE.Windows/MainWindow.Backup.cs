using System.Security.Cryptography;
using Amare.Windows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Amare.Windows;

public sealed partial class MainWindow
{
    private sealed record BackupListEntry(string FileName, string Display, string Path)
    {
        public override string ToString() => Display;
    }

    private CancellationTokenSource? _backupCancellation;
    private System.Diagnostics.Process? _restoreProcess;
    private bool _restoreCancelled;

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
        var vaultPasswordBox = new PasswordBox
        {
            PlaceholderText = L10n.T("backup.vaultPasswordPlaceholder"),
            MinWidth = 320
        };
        AutomationProperties.SetAutomationId(vaultPasswordBox, "BackupVaultPasswordBox");
        var vaultConfirmBox = new PasswordBox
        {
            PlaceholderText = L10n.T("backup.vaultPasswordConfirmPlaceholder"),
            MinWidth = 320
        };
        AutomationProperties.SetAutomationId(vaultConfirmBox, "BackupVaultPasswordConfirmBox");
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
        var restoreFullButton = new Button { Content = L10n.T("backup.restoreFull") };
        AutomationProperties.SetAutomationId(restoreFullButton, "BackupRestoreFullButton");
        var cancelButton = new Button { Content = L10n.T("common.cancel"), IsEnabled = false };
        AutomationProperties.SetAutomationId(cancelButton, "BackupCancelButton");
        cancelButton.Click += async (_, _) =>
        {
            _backupCancellation?.Cancel();
            var process = _restoreProcess;
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await backupClient.RecoverAsync(); } catch { }
                _restoreCancelled = true;
                status.Text = L10n.T("backup.restoreCancelled");
            }
        };

        var profileIds = new List<string> { _profileId };
        try
        {
            var page = await _profiles.ListAsync(100);
            foreach (var profile in page.Profiles)
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
        var sourceBox = new ComboBox { MinWidth = 220 };
        AutomationProperties.SetAutomationId(sourceBox, "BackupSourceProfileBox");
        var fullOpenProfileBox = new ComboBox { MinWidth = 220 };
        AutomationProperties.SetAutomationId(fullOpenProfileBox, "BackupFullOpenProfileBox");
        var manifestSummary = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MinHeight = 90,
            Text = ""
        };
        AutomationProperties.SetAutomationId(manifestSummary, "BackupManifestText");
        var backupList = new ListView { MinHeight = 90, MaxHeight = 160 };
        AutomationProperties.SetAutomationId(backupList, "BackupListBox");
        var refreshListButton = new Button { Content = L10n.T("backup.refreshList") };
        AutomationProperties.SetAutomationId(refreshListButton, "BackupRefreshListButton");

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
        var vaultPasswordRow = new StackPanel { Spacing = 6 };
        vaultPasswordRow.Children.Add(vaultPasswordBox);
        vaultPasswordRow.Children.Add(vaultConfirmBox);

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(profileText);
        stack.Children.Add(new TextBlock { Text = L10n.T("backup.sourceProfile") });
        stack.Children.Add(sourceBox);
        stack.Children.Add(new TextBlock { Text = L10n.T("backup.targetProfile") });
        stack.Children.Add(targetBox);
        stack.Children.Add(new TextBlock { Text = L10n.T("backup.fullOpenProfile") });
        stack.Children.Add(fullOpenProfileBox);
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
        stack.Children.Add(new TextBlock { Text = L10n.T("backup.vaultPassword") });
        stack.Children.Add(vaultPasswordRow);
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        buttonRow.Children.Add(createButton);
        buttonRow.Children.Add(verifyButton);
        buttonRow.Children.Add(cancelButton);
        stack.Children.Add(buttonRow);
        stack.Children.Add(new TextBlock { Text = L10n.T("backup.manifest") });
        stack.Children.Add(manifestSummary);
        stack.Children.Add(status);
        stack.Children.Add(shaText);
        stack.Children.Add(new TextBlock { Text = L10n.T("backup.list") });
        stack.Children.Add(backupList);
        stack.Children.Add(refreshListButton);

        var dialog = new ContentDialog
        {
            Title = L10n.T("backup.title"),
            Content = new ScrollViewer { MaxHeight = 640, Content = stack },
            CloseButtonText = L10n.T("common.close"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };

        void UpdateManifestUi(BackupOperationResult? result)
        {
            var manifest = result?.Manifest;
            if (manifest == null)
            {
                manifestSummary.Text = "";
                sourceBox.ItemsSource = null;
                fullOpenProfileBox.ItemsSource = null;
                restoreFullButton.IsEnabled = false;
                return;
            }
            var profiles = manifest.Profiles
                .Select(profile => $"{profile.Id} ({profile.Name}, v{profile.SchemaVersion})")
                .ToList();
            var dataKinds = string.Join(", ", manifest.DataKinds);
            var vault = manifest.ContainsVault
                ? $"{L10n.T("backup.manifestVault", result!.Vault.Sum(v => v.EntryCount))}"
                : L10n.T("backup.manifestNoVault");
            var builder = new System.Text.StringBuilder();
            builder.AppendLine($"{L10n.T("backup.manifestVersion")}: {manifest.FormatVersion} / AMARE {manifest.AppVersion}");
            builder.AppendLine($"{L10n.T("backup.manifestScope")}: {manifest.Scope}");
            builder.AppendLine($"{L10n.T("backup.manifestCreated")}: {DateTimeOffset.FromUnixTimeMilliseconds(manifest.CreatedAtMs):yyyy-MM-dd HH:mm:ss zzz}");
            builder.AppendLine($"{L10n.T("backup.manifestProfiles")}: {string.Join("; ", profiles)}");
            builder.AppendLine($"{L10n.T("backup.manifestData")}: {dataKinds}");
            builder.AppendLine(vault);
            builder.AppendLine($"{L10n.T("backup.manifestEncryption")}: {manifest.Encryption}");
            builder.AppendLine($"{L10n.T("backup.manifestFiles", manifest.Files.Count)}");
            try
            {
                var path = CurrentPath();
                if (File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    builder.AppendLine($"SHA-256: {Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()}");
                }
            }
            catch
            {
                // The helper still verifies the file; display is best effort.
            }
            manifestSummary.Text = builder.ToString().TrimEnd();
            var ids = manifest.Profiles.Select(profile => profile.Id).ToList();
            sourceBox.ItemsSource = ids;
            sourceBox.SelectedIndex = 0;
            fullOpenProfileBox.ItemsSource = ids;
            fullOpenProfileBox.SelectedIndex = 0;
            restoreFullButton.IsEnabled = manifest.Scope == "full";
        }

        void RefreshBackupList()
        {
            try
            {
                backupList.Items.Clear();
                var directory = Path.Combine(DataRoot, "backups");
                if (!Directory.Exists(directory)) return;
                foreach (var file in Directory.GetFiles(directory, "*.amarebak")
                             .OrderByDescending(File.GetLastWriteTimeUtc))
                {
                    var info = new FileInfo(file);
                    var sidecar = file + ".sha256";
                    var hash = File.Exists(sidecar)
                        ? File.ReadAllText(sidecar).Trim()
                        : L10n.T("backup.noSidecar");
                    if (hash.Length > 24) hash = hash[..24] + "…";
                    var display = $"{info.Name}  |  {info.Length:N0} B  |  {info.LastWriteTime:yyyy-MM-dd HH:mm}  |  {hash}  |  {L10n.T("backup.verifyToCheck")}";
                    backupList.Items.Add(new BackupListEntry(info.Name, display, info.FullName));
                }
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Backup list could not be refreshed: {exception.GetType().Name}");
            }
        }

        BackupOperationResult? lastVerified = null;
        pathBox.TextChanged += (_, _) =>
        {
            lastVerified = null;
            UpdateManifestUi(null);
        };

        void SetBusy(bool busy)
        {
            createButton.IsEnabled = !busy;
            verifyButton.IsEnabled = !busy;
            restoreButton.IsEnabled = !busy;
            restoreNewButton.IsEnabled = !busy;
            restoreFullButton.IsEnabled = !busy;
            refreshListButton.IsEnabled = !busy;
            fullOpenProfileBox.IsEnabled = !busy;
            sourceBox.IsEnabled = !busy;
            browseSave.IsEnabled = !busy;
            browseOpen.IsEnabled = !busy;
            cancelButton.IsEnabled = busy;
        }

        string CurrentPath() => pathBox.Text.Trim();
        string? CurrentPassword() =>
            passwordBox.Password.Length == 0 ? null : passwordBox.Password;
        string? CurrentVaultPassword() =>
            vaultPasswordBox.Password.Length == 0 ? null : vaultPasswordBox.Password;

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
                var result = await backupClient.VerifyAsync(
                    path, CurrentPassword(), CurrentVaultPassword(), cancellation.Token);
                status.Text = DescribeBackupResult(result);
                lastVerified = result.Valid ? result : null;
                UpdateManifestUi(lastVerified);
                if (backupList.SelectedItem is BackupListEntry entry
                    && string.Equals(Path.GetFullPath(entry.Path), Path.GetFullPath(path),
                        StringComparison.OrdinalIgnoreCase))
                {
                    var index = backupList.SelectedIndex;
                    var label = result.Valid ? "Valid" : result.Status;
                    var display = entry.Display.Replace(
                        L10n.T("backup.verifyToCheck"), label, StringComparison.Ordinal);
                    backupList.Items[index] = entry with { Display = display };
                }
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
            if (includeVaultBox.IsChecked == true && CurrentVaultPassword() == null)
            {
                status.Text = L10n.T("backup.vaultPasswordRequired");
                return;
            }
            if (passwordBox.Password != confirmBox.Password)
            {
                status.Text = L10n.T("backup.passwordMismatch");
                return;
            }
            if (vaultPasswordBox.Password != vaultConfirmBox.Password)
            {
                status.Text = L10n.T("backup.vaultPasswordMismatch");
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
                    path, _profileId, includeVaultBox.IsChecked == true,
                    CurrentPassword(), CurrentVaultPassword(),
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
                RefreshBackupList();
            }
            catch (Exception exception)
            {
                status.Text = L10n.T("backup.failed") + ": " + exception.Message;
            }
            finally
            {
                passwordBox.Password = "";
                confirmBox.Password = "";
                vaultPasswordBox.Password = "";
                vaultConfirmBox.Password = "";
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

        async Task StartRestoreAsync(bool asNew, bool fullRestore = false)
        {
            var verified = lastVerified;
            if (verified == null)
            {
                verified = await VerifyPathAsync();
                if (verified == null || !verified.Valid) return;
            }
            var manifest = verified.Manifest;
            if (manifest == null)
            {
                status.Text = L10n.T("backup.invalid");
                return;
            }
            if (manifest.ContainsVault && CurrentVaultPassword() == null)
            {
                status.Text = L10n.T("backup.vaultPasswordRequired");
                return;
            }
            string sourceId;
            string targetId;
            string warning;
            if (fullRestore)
            {
                if (manifest.Scope != "full")
                {
                    status.Text = L10n.T("backup.fullRequiresFullBackup");
                    return;
                }
                if (manifest.Profiles.Count == 0)
                {
                    status.Text = L10n.T("backup.invalid");
                    return;
                }
                sourceId = fullOpenProfileBox.SelectedItem as string ?? manifest.Profiles[0].Id;
                targetId = sourceId;
                warning = L10n.T("backup.confirmFull",
                    string.Join(", ", manifest.Profiles.Select(profile => profile.Id)),
                    sourceId);
            }
            else
            {
                if (manifest.Profiles.Count == 0)
                {
                    status.Text = L10n.T("backup.invalid");
                    return;
                }
                sourceId = sourceBox.SelectedItem as string ?? manifest.Profiles[0].Id;
                if (!manifest.Profiles.Any(profile => profile.Id == sourceId))
                {
                    status.Text = L10n.T("backup.invalid");
                    return;
                }
                targetId = targetBox.SelectedItem as string ?? _profileId;
                if (asNew)
                {
                    var newId = await PromptForNewProfileAsync();
                    if (newId == null) return;
                    targetId = newId;
                }
                warning = asNew
                    ? L10n.T("backup.confirmNew", sourceId, targetId)
                    : L10n.T("backup.confirmCurrent", targetId);
            }
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
                SetBusy(true);
                _restoreCancelled = false;
                status.Text = L10n.T("backup.restoreStarted");
                var appExe = Path.Combine(
                    AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                    "AMARE.Windows.exe");
                _restoreProcess = backupClient.StartRestore(
                    CurrentPath(), sourceId, targetId, appExe,
                    CurrentPassword(), CurrentVaultPassword(),
                    fullRestore: fullRestore, openProfileId: fullRestore ? sourceId : targetId);
                passwordBox.Password = "";
                confirmBox.Password = "";
                vaultPasswordBox.Password = "";
                vaultConfirmBox.Password = "";
                var deadline = DateTimeOffset.UtcNow.AddMinutes(30);
                while (!_restoreProcess.HasExited && DateTimeOffset.UtcNow < deadline)
                {
                    if (_restoreCancelled)
                    {
                        status.Text = L10n.T("backup.restoreCancelled");
                        return;
                    }
                    var progress = backupClient.ReadRestoreProgress();
                    if (progress != null)
                    {
                        status.Text = progress.Stage switch
                        {
                            "staging" => L10n.T("backup.stageStaging"),
                            "staged" => L10n.T("backup.stageStaged"),
                            "applying" => L10n.T("backup.stageApplying"),
                            "finalizing" => L10n.T("backup.stageFinalizing"),
                            "completed" => L10n.T("backup.stageCompleted"),
                            "failed" => progress.Detail,
                            _ => progress.Detail
                        };
                        if (progress.Stage == "staged")
                        {
                            cancelButton.IsEnabled = false;
                            status.Text = L10n.T("backup.applyingRestart");
                            await Task.Delay(500);
                            Close();
                            Application.Current.Exit();
                            return;
                        }
                    }
                    await Task.Delay(250);
                }
                if (_restoreCancelled)
                {
                    status.Text = L10n.T("backup.restoreCancelled");
                    return;
                }
                var result = backupClient.ReadRestoreResult();
                status.Text = result == null
                    ? L10n.T("backup.restoreFailed")
                    : result.Succeeded
                        ? L10n.T("backup.restoreStarted")
                        : $"{L10n.T("backup.restoreFailed")}: {result.Error}";
            }
            catch (Exception exception)
            {
                status.Text = L10n.T("backup.restoreFailed") + ": " + exception.Message;
            }
            finally
            {
                _restoreProcess = null;
                SetBusy(false);
            }
        }

        backupList.SelectionChanged += (_, _) =>
        {
            if (backupList.SelectedItem is BackupListEntry entry) pathBox.Text = entry.Path;
        };
        refreshListButton.Click += (_, _) => RefreshBackupList();
        RefreshBackupList();

        restoreButton.Click += async (_, _) => await StartRestoreAsync(false);
        restoreNewButton.Click += async (_, _) => await StartRestoreAsync(true);
        restoreFullButton.Click += async (_, _) => await StartRestoreAsync(false, fullRestore: true);

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

    internal async Task ApplyPendingOpenProfileAsync()
    {
        try
        {
            var path = Path.Combine(DataRoot, "backups", "open-profile.json");
            if (!File.Exists(path)) return;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            File.Delete(path);
            var id = document.RootElement.TryGetProperty("profile_id", out var value)
                ? value.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(id) || _profiles is null) return;
            if (!await _profiles.ExistsAsync(id)) return;
            _context = await _profiles.SwitchAsync(id);
            _profileId = _context.ProfileId;
        }
        catch (Exception exception)
        {
            AppLog.Warn($"Pending restore profile could not be opened: {exception.GetType().Name}");
        }
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
