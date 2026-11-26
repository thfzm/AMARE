using System.Text;
using System.Text.Json;
using Amare.Windows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Amare.Windows;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _aiRequestCancellation;
    private bool _aiKeyRejected;
    private long _aiRequestGeneration;
    private string? _aiRequestProfileId;
    private Guid? _aiRequestHostId;

    private void CancelAiRequest()
    {
        _aiRequestGeneration++;
        var cancellation = _aiRequestCancellation;
        _aiRequestCancellation = null;
        try
        {
            cancellation?.Cancel();
            cancellation?.Dispose();
        }
        catch
        {
            // The request may already be finishing.
        }
    }

    private bool IsAiRequestCurrent(
        long generation,
        Guid hostId,
        string profileId,
        long contextGeneration)
    {
        return _aiRequestGeneration == generation
               && _aiRequestHostId == hostId
               && _aiRequestProfileId == profileId
               && _context is { } context
               && (long)context.Generation == contextGeneration
               && ActiveHost?.Model.Id == hostId;
    }

    private sealed record AiConnectionTestResult(string Message, AiErrorKind? ErrorKind);

    private static string LocalizedAiError(AiException exception) => exception.Kind switch
    {
        AiErrorKind.Disabled => L10n.T("ai.error.disabled"),
        AiErrorKind.MissingKey => L10n.T("ai.error.missingKey"),
        AiErrorKind.InvalidKey => L10n.T("ai.error.invalidKey"),
        AiErrorKind.RateLimit => exception.RetryAfter is { } retry
            ? L10n.T("ai.error.rateLimit") + $" ({Math.Max(1, (int)retry.TotalSeconds)}s)"
            : L10n.T("ai.error.rateLimit"),
        AiErrorKind.Quota => L10n.T("ai.error.quota"),
        AiErrorKind.Server => L10n.T("ai.error.server"),
        AiErrorKind.Timeout => L10n.T("ai.error.timeout"),
        AiErrorKind.Network => L10n.T("ai.error.network"),
        AiErrorKind.InvalidResponse => L10n.T("ai.error.invalidResponse"),
        AiErrorKind.InvalidConfiguration => L10n.T("ai.error.invalidConfiguration"),
        AiErrorKind.Canceled => L10n.T("ai.error.canceled"),
        _ => L10n.T("ai.error.unknown")
    };

    private async Task<AiConnectionTestResult> TestAiConnectionAsync(AiApiSettings settings, string? key)
    {
        if (settings.Mode == AiExecutionMode.Mock)
        {
            return new AiConnectionTestResult(L10n.T("ai.mockOffline"), null);
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            return new AiConnectionTestResult(L10n.T("ai.error.missingKey"), AiErrorKind.MissingKey);
        }
        try
        {
            var service = AiAssistantService.Create(settings, key);
            await service.ValidateAsync();
            return new AiConnectionTestResult(L10n.T("ai.connectionOk"), null);
        }
        catch (AiException exception)
        {
            AppLog.Warn($"AI connection test failed kind={exception.Kind}");
            return new AiConnectionTestResult(LocalizedAiError(exception), exception.Kind);
        }
        catch (Exception exception)
        {
            AppLog.Warn($"AI connection test failed type={exception.GetType().Name}");
            return new AiConnectionTestResult(L10n.T("ai.error.unknown"), AiErrorKind.Unknown);
        }
    }

    private void SetAiKeyRejected(bool rejected)
    {
        _aiKeyRejected = rejected;
        if (_aiApiSettings.KeyRejected == rejected)
        {
            return;
        }
        _aiApiSettings = _aiApiSettings with { KeyRejected = rejected };
        SaveAiApiSettings();
    }

    private async Task<AiPageContext?> CaptureAiPageContextAsync()
    {
        var host = ActiveHost;
        if (host?.View.CoreWebView2 is null || host.Model.IsNewTabPage)
        {
            return null;
        }
        try
        {
            var core = host.View.CoreWebView2;
            var title = JsonSerializer.Deserialize<string>(
                await core.ExecuteScriptAsync("document.title"))?.Trim();
            var url = host.Model.Url;
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }
            var raw = await core.ExecuteScriptAsync(ReaderExtractionScript.Script);
            var pageJson = JsonSerializer.Deserialize<string>(raw);
            var page = string.IsNullOrWhiteSpace(pageJson)
                ? null
                : JsonSerializer.Deserialize<ReaderExtractedPage>(
                    pageJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var text = page is null
                ? ""
                : string.Join("\n\n", page.Blocks
                    .Where(block => block.Tag != "img")
                    .Select(block => block.Text)
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            if (text.Length > 20000)
            {
                text = text[..20000];
            }
            if (!ReferenceEquals(ActiveHost, host))
            {
                return null;
            }
            return new AiPageContext(
                string.IsNullOrWhiteSpace(title) ? host.Model.Title : title,
                url,
                text);
        }
        catch (Exception exception)
        {
            AppLog.Warn($"AI page context extraction failed type={exception.GetType().Name}");
            return null;
        }
    }

    private async Task AskApiAiAsync()
    {
        if (!_aiApiSettings.Enabled)
        {
            await ShowMessageAsync(
                L10n.T("ai.title"),
                L10n.T("ai.disabled"));
            return;
        }
        var host = ActiveHost;
        if (host?.View.CoreWebView2 is null)
        {
            await ShowMessageAsync(L10n.T("ai.title"), L10n.T("ai.noPage"));
            return;
        }
        var existingKey = AiCredentialStore.GetKey();
        if (_aiApiSettings.Mode == AiExecutionMode.Actual
            && string.IsNullOrWhiteSpace(existingKey))
        {
            await ShowMessageAsync(
                L10n.T("ai.title"),
                L10n.T("ai.noKey"));
            return;
        }

        var transcript = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 220,
            MaxHeight = 360,
            PlaceholderText = L10n.T("ai.transcriptPlaceholder")
        };
        AutomationProperties.SetAutomationId(transcript, "AiTranscriptBox");
        var promptBox = new TextBox
        {
            Text = L10n.T("ai.prompt"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80
        };
        AutomationProperties.SetAutomationId(promptBox, "AiPromptBox");
        var includeContext = new CheckBox
        {
            Content = L10n.T("ai.context"),
            IsChecked = false
        };
        AutomationProperties.SetAutomationId(includeContext, "AiIncludePageContext");
        var contextStatus = new TextBlock
        {
            Text = L10n.T("ai.contextOff"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        AutomationProperties.SetAutomationId(contextStatus, "AiContextStatus");
        var status = new TextBlock
        {
            Text = _aiApiSettings.Mode == AiExecutionMode.Mock
                ? L10n.T("ai.mockMode")
                : L10n.T("ai.actualMode"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        AutomationProperties.SetAutomationId(status, "AiRequestStatus");
        var progress = new ProgressRing { IsActive = false, Width = 20, Height = 20 };
        var stopButton = new Button { Content = L10n.T("common.stop"), IsEnabled = false };
        AutomationProperties.SetAutomationId(stopButton, "AiStopButton");
        var retryButton = new Button { Content = L10n.T("common.retry"), IsEnabled = false };
        AutomationProperties.SetAutomationId(retryButton, "AiRetryButton");
        var copyButton = new Button { Content = L10n.T("ai.copyConversation") };
        AutomationProperties.SetAutomationId(copyButton, "AiCopyConversationButton");

        var history = new List<AiMessage>();
        AiRequest? lastRequest = null;
        var busy = false;

        void Append(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            transcript.Text += (transcript.Text.Length == 0 ? "" : "\n\n") + value;
            transcript.SelectionStart = transcript.Text.Length;
        }

        void UpdateContextStatus()
        {
            var active = ActiveHost;
            if (includeContext.IsChecked != true)
            {
                contextStatus.Text = L10n.T("ai.contextOff");
                return;
            }
            if (active is null || active.Model.IsNewTabPage || active.View.CoreWebView2 is null)
            {
                contextStatus.Text = L10n.T("ai.contextUnavailable");
                includeContext.IsChecked = false;
                return;
            }
            contextStatus.Text = L10n.T("ai.contextOn", active.Model.Title);
        }

        includeContext.Checked += (_, _) => UpdateContextStatus();
        includeContext.Unchecked += (_, _) => UpdateContextStatus();
        copyButton.Click += (_, _) =>
        {
            if (transcript.Text.Length == 0)
            {
                return;
            }
            var data = new DataPackage();
            data.SetText(transcript.Text);
            Clipboard.SetContent(data);
            status.Text = L10n.T("ai.copied");
        };

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        buttonPanel.Children.Add(stopButton);
        buttonPanel.Children.Add(retryButton);
        buttonPanel.Children.Add(copyButton);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = L10n.T("ai.contextPolicy"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        panel.Children.Add(transcript);
        panel.Children.Add(new TextBlock { Text = L10n.T("ai.question") });
        panel.Children.Add(promptBox);
        panel.Children.Add(includeContext);
        panel.Children.Add(contextStatus);
        panel.Children.Add(buttonPanel);
        panel.Children.Add(progress);
        panel.Children.Add(status);
        var dialog = new ContentDialog
        {
            Title = L10n.T("ai.title"),
            Content = new ScrollViewer { MaxHeight = 620, Content = panel },
            PrimaryButtonText = L10n.T("common.send"),
            SecondaryButtonText = L10n.T("common.clear"),
            CloseButtonText = L10n.T("common.close"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };

        async Task StartSendAsync(bool retry)
        {
            if (busy)
            {
                return;
            }
            AiPageContext? pageContext = null;
            string prompt = "";
            if (!retry)
            {
                prompt = promptBox.Text.Trim();
                if (prompt.Length == 0)
                {
                    status.Text = L10n.T("ai.noQuestion");
                    return;
                }
                if (includeContext.IsChecked == true)
                {
                    var capturedHost = ActiveHost;
                    pageContext = await CaptureAiPageContextAsync();
                    if (pageContext is null || !ReferenceEquals(ActiveHost, capturedHost))
                    {
                        status.Text = L10n.T("ai.contextFailed");
                        return;
                    }
                    Append(L10n.T("ai.contextSent", pageContext.Title, pageContext.Url));
                }
                history.Add(AiMessage.User(prompt));
                Append(L10n.T("ai.you", prompt));
            }
            else if (lastRequest is null)
            {
                status.Text = L10n.T("ai.noFailedRequest");
                return;
            }

            var request = retry && lastRequest is not null
                ? lastRequest
                : new AiRequest(history.ToArray(), pageContext);
            lastRequest = request;
            var requestHost = ActiveHost;
            if (requestHost is null)
            {
                status.Text = L10n.T("ai.noActivePage");
                return;
            }
            var hostId = requestHost.Model.Id;
            var profileId = _profileId;
            var contextGeneration = _context is null ? 0L : (long)_context.Generation;
            var generation = ++_aiRequestGeneration;
            _aiRequestHostId = hostId;
            _aiRequestProfileId = profileId;
            var cancellation = new CancellationTokenSource();
            _aiRequestCancellation = cancellation;
            busy = true;
            progress.IsActive = true;
            stopButton.IsEnabled = true;
            retryButton.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false;
            status.Text = _aiApiSettings.Mode == AiExecutionMode.Mock
                ? L10n.T("ai.mockGenerating")
                : L10n.T("ai.actualGenerating");
            Append(L10n.T("ai.assistant"));
            var assistant = new StringBuilder();
            try
            {
                var service = AiAssistantService.Create(_aiApiSettings, existingKey);
                await foreach (var chunk in service.StreamAsync(request, cancellation.Token))
                {
                    if (!IsAiRequestCurrent(generation, hostId, profileId, contextGeneration))
                    {
                        return;
                    }
                    if (chunk.Text.Length == 0)
                    {
                        continue;
                    }
                    assistant.Append(chunk.Text);
                    transcript.Text += chunk.Text;
                    transcript.SelectionStart = transcript.Text.Length;
                }
                if (IsAiRequestCurrent(generation, hostId, profileId, contextGeneration))
                {
                    if (assistant.Length > 0)
                    {
                        history.Add(AiMessage.Assistant(assistant.ToString()));
                    }
                    status.Text = _aiApiSettings.Mode == AiExecutionMode.Mock
                        ? L10n.T("ai.mockComplete")
                        : L10n.T("ai.complete");
                    retryButton.IsEnabled = false;
                }
            }
            catch (OperationCanceledException)
            {
                status.Text = L10n.T("ai.canceled");
                retryButton.IsEnabled = lastRequest is not null;
            }
            catch (AiException exception)
            {
                AppLog.Warn($"AI request failed kind={exception.Kind}");
                if (exception.Kind == AiErrorKind.InvalidKey)
                {
                    SetAiKeyRejected(true);
                }
                var localized = LocalizedAiError(exception);
                status.Text = localized;
                Append($"[request failed] {localized}");
                retryButton.IsEnabled = lastRequest is not null;
            }
            catch (Exception exception)
            {
                AppLog.Warn($"AI request failed type={exception.GetType().Name}");
                status.Text = L10n.T("ai.failed");
                Append("[request failed] " + L10n.T("ai.failed"));
                retryButton.IsEnabled = lastRequest is not null;
            }
            finally
            {
                if (ReferenceEquals(_aiRequestCancellation, cancellation))
                {
                    _aiRequestCancellation = null;
                }
                cancellation.Dispose();
                busy = false;
                progress.IsActive = false;
                stopButton.IsEnabled = false;
                dialog.IsPrimaryButtonEnabled = true;
            }
        }

        void ClearConversation()
        {
            CancelAiRequest();
            history.Clear();
            lastRequest = null;
            transcript.Text = "";
            retryButton.IsEnabled = false;
            status.Text = L10n.T("ai.cleared");
        }

        stopButton.Click += (_, _) =>
        {
            _aiRequestCancellation?.Cancel();
        };
        retryButton.Click += async (_, _) => await StartSendAsync(retry: true);
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            await StartSendAsync(retry: false);
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            ClearConversation();
        };
        dialog.Closed += (_, _) =>
        {
            CancelAiRequest();
        };
        LocalizationUi.ApplyDialog(dialog);
        await dialog.ShowAsync();
    }

    private async Task ShowAiApiSettingsAsync()
    {
        var existingKey = AiCredentialStore.GetKey();
        var keyBox = new PasswordBox
        {
            PlaceholderText = existingKey is null
                ? L10n.T("settings.aiKeyPlaceholderEmpty")
                : L10n.T("settings.aiKeyPlaceholder")
        };
        AutomationProperties.SetAutomationId(keyBox, "AiKeyBox");
        var keyStatus = new TextBlock
        {
            Text = existingKey is null
                ? L10n.T("settings.aiKeyNone")
                : _aiKeyRejected
                    ? L10n.T("settings.aiKeyRejected")
                    : L10n.T("settings.aiKeyConfigured"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        AutomationProperties.SetAutomationId(keyStatus, "AiKeyStatus");
        var keyRejected = false;
        keyBox.PasswordChanged += (_, _) => keyRejected = false;

        var providerCombo = new ComboBox
        {
            ItemsSource = AiProviderCatalog.All,
            DisplayMemberPath = nameof(AiProviderDescriptor.Name),
            SelectedItem = AiProviderCatalog.Resolve(_aiApiSettings.Provider)
        };
        AutomationProperties.SetAutomationId(providerCombo, "AiProviderBox");
        var enabledBox = new CheckBox
        {
            Content = L10n.T("settings.aiEnabled"),
            IsChecked = _aiApiSettings.Enabled
        };
        AutomationProperties.SetAutomationId(enabledBox, "AiEnabledBox");
        var modeCombo = new ComboBox
        {
            ItemsSource = new[]
            {
                L10n.T("settings.aiModeMock"),
                L10n.T("settings.aiModeActual")
            },
            SelectedIndex = _aiApiSettings.Mode == AiExecutionMode.Mock ? 0 : 1,
            MinWidth = 220
        };
        AutomationProperties.SetAutomationId(modeCombo, "AiModeBox");
        var modelBox = new TextBox
        {
            Text = _aiApiSettings.Model,
            PlaceholderText = AiApiClient.DefaultModel
        };
        AutomationProperties.SetAutomationId(modelBox, "AiModelBox");
        var baseUrlBox = new TextBox
        {
            Text = _aiApiSettings.BaseUrl,
            PlaceholderText = AiApiClient.DefaultBaseUrl
        };
        AutomationProperties.SetAutomationId(baseUrlBox, "AiBaseUrlBox");
        var testStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        AutomationProperties.SetAutomationId(testStatus, "AiTestStatus");
        var testButton = new Button { Content = L10n.T("settings.aiTest") };
        testButton.Click += async (_, _) =>
        {
            try
            {
                testStatus.Text = L10n.T("common.testing");
                var provider = providerCombo.SelectedItem as AiProviderDescriptor
                               ?? AiProviderCatalog.All[0];
                var settings = new AiApiSettings(
                    modelBox.Text.Trim().Length == 0 ? provider.DefaultModel : modelBox.Text.Trim(),
                    baseUrlBox.Text.Trim().Length == 0 ? provider.BaseUrl : baseUrlBox.Text.Trim(),
                    provider.Id,
                    enabledBox.IsChecked == true,
                    modeCombo.SelectedIndex == 0 ? AiExecutionMode.Mock : AiExecutionMode.Actual);
                var key = keyBox.Password.Trim().Length > 0 ? keyBox.Password : existingKey;
                var result = await TestAiConnectionAsync(settings, key);
                testStatus.Text = result.Message;
                keyRejected = settings.Mode == AiExecutionMode.Actual
                              && result.ErrorKind == AiErrorKind.InvalidKey;
                if (keyRejected)
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
                testStatus.Text = L10n.T("ai.connectionFailed", exception.GetType().Name);
            }
        };
        var deleteButton = new Button { Content = L10n.T("settings.aiDelete") };
        AutomationProperties.SetAutomationId(deleteButton, "AiDeleteKeyButton");
        deleteButton.Click += (_, _) =>
        {
            if (AiCredentialStore.Delete())
            {
                SetAiKeyRejected(false);
                keyStatus.Text = L10n.T("ai.settings.keyDeleted");
                testStatus.Text = L10n.T("ai.settings.keyDeleted");
            }
            else
            {
                testStatus.Text = L10n.T("ai.settings.keyDeleteFailed");
            }
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = L10n.T("ai.settings.info"),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(enabledBox);
        panel.Children.Add(new TextBlock { Text = L10n.T("settings.aiMode") });
        panel.Children.Add(modeCombo);
        panel.Children.Add(new TextBlock { Text = L10n.T("ai.settings.provider") });
        panel.Children.Add(providerCombo);
        panel.Children.Add(new TextBlock { Text = L10n.T("ai.settings.model") });
        panel.Children.Add(modelBox);
        panel.Children.Add(new TextBlock { Text = L10n.T("ai.settings.baseUrl") });
        panel.Children.Add(baseUrlBox);
        panel.Children.Add(new TextBlock { Text = L10n.T("ai.settings.key") });
        panel.Children.Add(keyBox);
        panel.Children.Add(keyStatus);
        panel.Children.Add(testButton);
        panel.Children.Add(testStatus);
        panel.Children.Add(deleteButton);
        panel.Children.Add(new TextBlock
        {
            Text = L10n.T("ai.settings.cost"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        var dialog = new ContentDialog
        {
            Title = L10n.T("ai.settings.title"),
            Content = new ScrollViewer { MaxHeight = 620, Content = panel },
            PrimaryButtonText = L10n.T("common.save"),
            CloseButtonText = L10n.T("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        LocalizationUi.ApplyDialog(dialog);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        if (keyRejected && keyBox.Password.Trim().Length > 0)
        {
            await ShowMessageAsync(
                L10n.T("ai.settings.title"),
                L10n.T("ai.settings.keyRejectedNotSaved"));
            return;
        }
        var selectedProvider = providerCombo.SelectedItem as AiProviderDescriptor
                               ?? AiProviderCatalog.All[0];
        var newKey = keyBox.Password.Trim();
        if (newKey.Length > 0 && !AiCredentialStore.Save(newKey))
        {
            await ShowMessageAsync(L10n.T("ai.settings.title"), L10n.T("ai.settings.keyStoreFailed"));
            return;
        }
        var keyChanged = newKey.Length > 0;
        _aiKeyRejected = keyChanged ? false : _aiKeyRejected;
        var settings = new AiApiSettings(
            modelBox.Text.Trim().Length == 0 ? selectedProvider.DefaultModel : modelBox.Text.Trim(),
            baseUrlBox.Text.Trim().Length == 0 ? selectedProvider.BaseUrl : baseUrlBox.Text.Trim(),
            selectedProvider.Id,
            enabledBox.IsChecked == true,
            modeCombo.SelectedIndex == 0 ? AiExecutionMode.Mock : AiExecutionMode.Actual,
            KeyRejected: _aiKeyRejected);
        _aiApiSettings = settings;
        SaveAiApiSettings();
        AppLog.Info($"AI settings saved mode={settings.Mode} keyConfigured={AiCredentialStore.GetKey() is not null}");
        StatusText.Text = settings.Mode == AiExecutionMode.Actual
                          && string.IsNullOrWhiteSpace(AiCredentialStore.GetKey())
            ? L10n.T("ai.settings.savedNoKey")
            : L10n.T("ai.settings.saved");
    }

}
