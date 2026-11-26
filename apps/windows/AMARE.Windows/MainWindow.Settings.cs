using System.Text.Json;
using Amare.Windows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Amare.Windows;

public sealed partial class MainWindow
{
    private AppPreferences _appPreferences = new();
    private bool _saveHistory = true;
    private bool _passwordAutofillEnabled = true;
    private bool _passwordSavePromptEnabled = true;

    private string AppPreferencesPath => Path.Combine(_appDataRoot, "app-preferences.json");

    private void LoadAppPreferences()
    {
        try
        {
            if (!File.Exists(AppPreferencesPath))
            {
                return;
            }
            _appPreferences = JsonSerializer.Deserialize<AppPreferences>(
                File.ReadAllText(AppPreferencesPath)) ?? _appPreferences;
        }
        catch (Exception exception)
        {
            AppLog.Warn($"App preferences could not be loaded: {exception.GetType().Name}");
            _appPreferences = new AppPreferences();
        }
    }

    private bool SaveAppPreferences()
    {
        try
        {
            var temporary = AppPreferencesPath + ".tmp";
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(_appPreferences));
            File.Move(temporary, AppPreferencesPath, overwrite: true);
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error("App preferences could not be saved", exception);
            return false;
        }
    }

    private string ResolveDownloadDirectory()
    {
        var configured = _appPreferences.DownloadDirectory;
        if (!string.IsNullOrWhiteSpace(configured)
            && Path.IsPathFullyQualified(configured))
        {
            try
            {
                Directory.CreateDirectory(configured);
                return configured;
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Configured download folder is unavailable: {exception.GetType().Name}");
            }
        }
        var fallback = Path.Combine(_appDataRoot, "downloads");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private void ApplyThemeToAllWindows(string theme)
    {
        lock (WindowRegistryGate)
        {
            foreach (var reference in WindowRegistry)
            {
                if (reference.TryGetTarget(out var window))
                {
                    window.ApplyTheme(theme);
                }
            }
        }
    }

    private async Task ShowSettingsAsync()
    {
        if (_context is null || _settings is null)
        {
            return;
        }

        var languageBox = new ComboBox
        {
            ItemsSource = new[]
            {
                L10n.T("settings.languageSystem"),
                "English",
                "한국어"
            },
            SelectedIndex = _appPreferences.Language == L10n.Korean
                ? 2
                : _appPreferences.Language == L10n.English ? 1 : 0,
            MinWidth = 220
        };
        AutomationProperties.SetAutomationId(languageBox, "SettingsLanguageBox");
        var themeIds = new[] { "system", "light", "dark" };
        var themeBox = new ComboBox
        {
            ItemsSource = new[]
            {
                L10n.T("settings.themeSystem"),
                L10n.T("settings.themeLight"),
                L10n.T("settings.themeDark")
            },
            SelectedIndex = Math.Max(0, Array.IndexOf(themeIds, _themeId)),
            MinWidth = 220
        };
        AutomationProperties.SetAutomationId(themeBox, "SettingsThemeBox");
        var engineBox = new ComboBox
        {
            ItemsSource = SearchEngineCatalog.All,
            DisplayMemberPath = "Name",
            SelectedItem = SearchEngineCatalog.Resolve(_engineId),
            MinWidth = 220
        };
        AutomationProperties.SetAutomationId(engineBox, "SettingsEngineBox");
        var restoreBox = new CheckBox
        {
            Content = L10n.T("settings.restoreSession"),
            IsChecked = _restoreSession
        };
        AutomationProperties.SetAutomationId(restoreBox, "SettingsRestoreBox");
        var historyBox = new CheckBox
        {
            Content = L10n.T("settings.saveHistory"),
            IsChecked = _saveHistory
        };
        AutomationProperties.SetAutomationId(historyBox, "SettingsSaveHistoryBox");
        var autofillBox = new CheckBox
        {
            Content = L10n.T("settings.passwordAutofill"),
            IsChecked = _passwordAutofillEnabled
        };
        AutomationProperties.SetAutomationId(autofillBox, "SettingsPasswordAutofillBox");
        var savePromptBox = new CheckBox
        {
            Content = L10n.T("settings.passwordSavePrompt"),
            IsChecked = _passwordSavePromptEnabled
        };
        AutomationProperties.SetAutomationId(savePromptBox, "SettingsPasswordSaveBox");
        var downloadBox = new TextBox
        {
            Text = _appPreferences.DownloadDirectory,
            PlaceholderText = L10n.T("settings.defaultDownloadDirectory")
        };
        AutomationProperties.SetAutomationId(downloadBox, "SettingsDownloadPathBox");
        var checkOnStartupBox = new CheckBox
        {
            Content = L10n.T("settings.checkOnStartup"),
            IsChecked = _updatePreferences.CheckOnStartup
        };
        AutomationProperties.SetAutomationId(checkOnStartupBox, "SettingsCheckOnStartupBox");
        var channelBox = new ComboBox
        {
            ItemsSource = new[]
            {
                L10n.T("settings.channelStable"),
                L10n.T("settings.channelPrerelease")
            },
            SelectedIndex = _updatePreferences.Channel == UpdateChannel.Prerelease ? 1 : 0,
            MinWidth = 220
        };
        AutomationProperties.SetAutomationId(channelBox, "SettingsUpdateChannelBox");

        var existingKey = AiCredentialStore.GetKey();
        var aiKeyBox = new PasswordBox
        {
            PlaceholderText = existingKey is null
                ? L10n.T("settings.aiKeyPlaceholderEmpty")
                : L10n.T("settings.aiKeyPlaceholder")
        };
        AutomationProperties.SetAutomationId(aiKeyBox, "SettingsAiKeyBox");
        var aiEnabledBox = new CheckBox
        {
            Content = L10n.T("settings.aiEnabled"),
            IsChecked = _aiApiSettings.Enabled
        };
        AutomationProperties.SetAutomationId(aiEnabledBox, "SettingsAiEnabledBox");
        var aiModeBox = new ComboBox
        {
            ItemsSource = new[]
            {
                L10n.T("settings.aiModeMock"),
                L10n.T("settings.aiModeActual")
            },
            SelectedIndex = _aiApiSettings.Mode == AiExecutionMode.Mock ? 0 : 1,
            MinWidth = 220
        };
        AutomationProperties.SetAutomationId(aiModeBox, "SettingsAiModeBox");
        var aiModelBox = new TextBox
        {
            Text = _aiApiSettings.Model,
            PlaceholderText = AiApiClient.DefaultModel
        };
        AutomationProperties.SetAutomationId(aiModelBox, "SettingsAiModelBox");
        var aiKeyStatus = new TextBlock
        {
            Text = existingKey is null
                ? L10n.T("settings.aiKeyNone")
                : _aiKeyRejected
                    ? L10n.T("settings.aiKeyRejected")
                    : L10n.T("settings.aiKeyConfigured"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        AutomationProperties.SetAutomationId(aiKeyStatus, "SettingsAiKeyStatus");
        var aiTestStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        AutomationProperties.SetAutomationId(aiTestStatus, "SettingsAiTestStatus");
        var aiKeyRejected = false;
        aiKeyBox.PasswordChanged += (_, _) => aiKeyRejected = false;
        var aiTestButton = new Button { Content = L10n.T("settings.aiTest") };
        AutomationProperties.SetAutomationId(aiTestButton, "SettingsAiTestButton");
        aiTestButton.Click += async (_, _) =>
        {
            try
            {
                aiTestStatus.Text = L10n.T("common.testing");
                var settings = new AiApiSettings(
                    aiModelBox.Text.Trim().Length == 0 ? AiApiClient.DefaultModel : aiModelBox.Text.Trim(),
                    _aiApiSettings.BaseUrl,
                    _aiApiSettings.Provider,
                    aiEnabledBox.IsChecked == true,
                    aiModeBox.SelectedIndex == 0 ? AiExecutionMode.Mock : AiExecutionMode.Actual);
                var key = aiKeyBox.Password.Trim().Length > 0 ? aiKeyBox.Password : existingKey;
                var result = await TestAiConnectionAsync(settings, key);
                aiTestStatus.Text = result.Message;
                aiKeyRejected = settings.Mode == AiExecutionMode.Actual
                                && result.ErrorKind == AiErrorKind.InvalidKey;
                if (aiKeyRejected)
                {
                    SetAiKeyRejected(true);
                }
                else if (result.ErrorKind is null)
                {
                    SetAiKeyRejected(false);
                }
            }
            catch (Exception exception)
            {
                aiTestStatus.Text = L10n.T("common.failed") + ": " + exception.GetType().Name;
            }
        };
        var aiDeleteButton = new Button { Content = L10n.T("settings.aiDelete") };
        AutomationProperties.SetAutomationId(aiDeleteButton, "SettingsAiDeleteButton");
        aiDeleteButton.Click += (_, _) =>
        {
            if (AiCredentialStore.Delete())
            {
                SetAiKeyRejected(false);
                aiKeyStatus.Text = L10n.T("settings.aiKeyNone");
                aiTestStatus.Text = L10n.T("settings.aiDelete");
            }
            else
            {
                aiTestStatus.Text = L10n.T("settings.aiKeyRejected");
            }
        };

        var managePermissions = false;
        var manageVault = false;
        var clearWebPasswords = false;
        var checkUpdates = false;
        var permissionsButton = new Button
        {
            Content = L10n.T("settings.manageSitePermissions"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetAutomationId(permissionsButton, "ManageSitePermissionsButton");
        var vaultButton = new Button
        {
            Content = L10n.T("settings.openVault"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetAutomationId(vaultButton, "SettingsOpenVaultButton");
        var clearWebButton = new Button
        {
            Content = L10n.T("settings.clearWebPasswords"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetAutomationId(clearWebButton, "SettingsClearWebPasswordsButton");
        var checkNowButton = new Button { Content = L10n.T("settings.checkNow") };
        AutomationProperties.SetAutomationId(checkNowButton, "SettingsCheckNowButton");

        ContentDialog? settingsDialog = null;
        permissionsButton.Click += (_, _) =>
        {
            managePermissions = true;
            settingsDialog?.Hide();
        };
        vaultButton.Click += (_, _) =>
        {
            manageVault = true;
            settingsDialog?.Hide();
        };
        clearWebButton.Click += (_, _) =>
        {
            clearWebPasswords = true;
            settingsDialog?.Hide();
        };
        checkNowButton.Click += (_, _) =>
        {
            checkUpdates = true;
            settingsDialog?.Hide();
        };

        var stack = new StackPanel { Spacing = 8 };
        void Heading(string text, string? scope = null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = text,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 2)
            });
            if (!string.IsNullOrWhiteSpace(scope))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = scope,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });
            }
        }

        Heading(L10n.T("settings.general"), L10n.T("settings.scopeProfile"));
        stack.Children.Add(new TextBlock { Text = L10n.T("settings.language") });
        stack.Children.Add(languageBox);
        stack.Children.Add(new TextBlock
        {
            Text = L10n.T("settings.languageHint"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock { Text = L10n.T("settings.searchEngine") });
        stack.Children.Add(engineBox);
        stack.Children.Add(restoreBox);
        Heading(L10n.T("settings.appearance"), L10n.T("settings.scopeProfile"));
        stack.Children.Add(new TextBlock { Text = L10n.T("settings.theme") });
        stack.Children.Add(themeBox);
        Heading(L10n.T("settings.privacy"), L10n.T("settings.scopeProfile"));
        stack.Children.Add(historyBox);
        stack.Children.Add(autofillBox);
        stack.Children.Add(savePromptBox);
        stack.Children.Add(new TextBlock
        {
            Text = L10n.T("settings.sitePermissionsHint"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(permissionsButton);
        stack.Children.Add(vaultButton);
        stack.Children.Add(clearWebButton);
        Heading(L10n.T("settings.ai"), L10n.T("settings.scopeApp"));
        stack.Children.Add(aiEnabledBox);
        stack.Children.Add(new TextBlock { Text = L10n.T("settings.aiMode") });
        stack.Children.Add(aiModeBox);
        stack.Children.Add(new TextBlock { Text = L10n.T("settings.aiModel") });
        stack.Children.Add(aiModelBox);
        stack.Children.Add(new TextBlock { Text = L10n.T("settings.aiKey") });
        stack.Children.Add(aiKeyBox);
        stack.Children.Add(aiKeyStatus);
        stack.Children.Add(aiTestButton);
        stack.Children.Add(aiTestStatus);
        stack.Children.Add(aiDeleteButton);
        stack.Children.Add(new TextBlock
        {
            Text = L10n.T("settings.aiCost"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        Heading(L10n.T("settings.downloads"), L10n.T("settings.scopeApp"));
        stack.Children.Add(new TextBlock { Text = L10n.T("settings.downloadDirectory") });
        stack.Children.Add(downloadBox);
        stack.Children.Add(new TextBlock
        {
            Text = L10n.T("settings.downloadDirectoryHint"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });
        Heading(L10n.T("settings.updates"), L10n.T("settings.scopeApp"));
        stack.Children.Add(checkOnStartupBox);
        stack.Children.Add(new TextBlock { Text = L10n.T("settings.updateChannel") });
        stack.Children.Add(channelBox);
        stack.Children.Add(checkNowButton);

        settingsDialog = new ContentDialog
        {
            Title = L10n.T("settings.title"),
            Content = new ScrollViewer { MaxHeight = 680, Content = stack },
            PrimaryButtonText = L10n.T("settings.save"),
            CloseButtonText = L10n.T("settings.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        LocalizationUi.ApplyDialog(settingsDialog);
        var settingsResult = await settingsDialog.ShowAsync();
        if (managePermissions)
        {
            await ShowSitePermissionsAsync();
            return;
        }
        if (manageVault)
        {
            await OpenVaultPanelAsync();
            return;
        }
        if (clearWebPasswords)
        {
            await ShowClearWebViewDataDialogAsync();
            return;
        }
        if (checkUpdates)
        {
            await CheckForUpdatesAsync(true);
            return;
        }
        if (settingsResult != ContentDialogResult.Primary)
        {
            return;
        }

        var downloadPath = downloadBox.Text.Trim();
        if (downloadPath.Length > 0)
        {
            if (!Path.IsPathFullyQualified(downloadPath))
            {
                await ShowMessageAsync(L10n.T("settings.title"), L10n.T("settings.downloadPathInvalid"));
                return;
            }
            try
            {
                Directory.CreateDirectory(downloadPath);
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Download folder could not be created: {exception.GetType().Name}");
                await ShowMessageAsync(L10n.T("settings.title"), L10n.T("settings.downloadPathCreateFailed"));
                return;
            }
        }

        var newAiKey = aiKeyBox.Password.Trim();
        if (aiKeyRejected && newAiKey.Length > 0)
        {
            await ShowMessageAsync(
                L10n.T("settings.title"),
                L10n.T("settings.aiKeyRejected"));
            return;
        }
        if (newAiKey.Length > 0 && !AiCredentialStore.Save(newAiKey))
        {
            await ShowMessageAsync(L10n.T("settings.title"), L10n.T("settings.keySaveFailed"));
            return;
        }

        var language = languageBox.SelectedIndex switch
        {
            2 => L10n.Korean,
            1 => L10n.English,
            _ => L10n.SystemLanguage
        };
        var theme = themeIds[Math.Clamp(themeBox.SelectedIndex, 0, themeIds.Length - 1)];
        var engine = engineBox.SelectedItem as SearchEngineDescriptor
                     ?? SearchEngineCatalog.Resolve(_engineId);
        var restore = restoreBox.IsChecked == true;
        var saveHistory = historyBox.IsChecked == true;
        var autofill = autofillBox.IsChecked == true;
        var savePrompt = savePromptBox.IsChecked == true;
        var channel = channelBox.SelectedIndex == 1 ? UpdateChannel.Prerelease : UpdateChannel.Stable;
        var aiSettings = new AiApiSettings(
            aiModelBox.Text.Trim().Length == 0 ? AiApiClient.DefaultModel : aiModelBox.Text.Trim(),
            _aiApiSettings.BaseUrl,
            _aiApiSettings.Provider,
            aiEnabledBox.IsChecked == true,
            aiModeBox.SelectedIndex == 0 ? AiExecutionMode.Mock : AiExecutionMode.Actual,
            KeyRejected: newAiKey.Length > 0 ? false : _aiKeyRejected);

        try
        {
            await _settings.SetManyAsync(_context, new[]
            {
                ("theme", SettingValueDto.Text(theme)),
                ("engine", SettingValueDto.Text(engine.Id)),
                ("restore_session", SettingValueDto.Boolean(restore)),
                ("save_history", SettingValueDto.Boolean(saveHistory)),
                ("password_autofill_enabled", SettingValueDto.Boolean(autofill)),
                ("password_save_prompt_enabled", SettingValueDto.Boolean(savePrompt))
            });
        }
        catch (Exception exception)
        {
            AppLog.Error("Settings save failed", exception);
            StatusText.Text = L10n.T("settings.saveFailed");
            return;
        }

        _themeId = theme;
        _engineId = engine.Id;
        _restoreSession = restore;
        _saveHistory = saveHistory;
        _passwordAutofillEnabled = autofill;
        _passwordSavePromptEnabled = savePrompt;
        _aiApiSettings = aiSettings with { KeyRejected = newAiKey.Length > 0 ? false : _aiKeyRejected };
        _aiKeyRejected = _aiApiSettings.KeyRejected;
        var languageChanged = !string.Equals(_appPreferences.Language, language, StringComparison.OrdinalIgnoreCase);
        _appPreferences = _appPreferences with
        {
            Language = language,
            DownloadDirectory = downloadPath
        };
        _updatePreferences = _updatePreferences with
        {
            CheckOnStartup = checkOnStartupBox.IsChecked == true,
            Channel = channel
        };
        var appSaved = SaveAppPreferences();
        var updateSaved = SaveUpdatePreferences();
        var aiSaved = SaveAiApiSettings();
        ApplyThemeToAllWindows(theme);
        _initializingSettings = true;
        SearchEngineBox.SelectedItem = engine;
        _initializingSettings = false;
        if (!appSaved || !updateSaved || !aiSaved)
        {
            StatusText.Text = L10n.T("settings.partialSave");
            return;
        }
        StatusText.Text = languageChanged
            ? L10n.T("settings.languageRestart")
            : L10n.T("settings.saved");
    }
}
