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

    private async Task<string> TestAiConnectionAsync(AiApiSettings settings, string? key)
    {
        if (settings.Mode == AiExecutionMode.Mock)
        {
            return "Mock mode works offline; no network request was sent.";
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            return "An API key is required for Actual API mode.";
        }
        try
        {
            var service = AiAssistantService.Create(settings, key);
            await service.ValidateAsync();
            return "Connection OK: the key and model were accepted.";
        }
        catch (AiException exception)
        {
            AppLog.Warn($"AI connection test failed kind={exception.Kind}");
            return exception.UserMessage;
        }
        catch (Exception exception)
        {
            AppLog.Warn($"AI connection test failed type={exception.GetType().Name}");
            return "Connection test failed.";
        }
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
                "API AI",
                "AMARE API assistant is disabled. Enable it in Settings → AI assistant.");
            return;
        }
        var host = ActiveHost;
        if (host?.View.CoreWebView2 is null)
        {
            await ShowMessageAsync("API AI", "Open a page before asking API AI.");
            return;
        }
        var existingKey = AiCredentialStore.GetKey();
        if (_aiApiSettings.Mode == AiExecutionMode.Actual
            && string.IsNullOrWhiteSpace(existingKey))
        {
            await ShowMessageAsync(
                "API AI",
                "Configure an API key first from Settings → AI assistant or API AI settings.");
            return;
        }

        var transcript = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 220,
            MaxHeight = 360,
            PlaceholderText = "The conversation appears here."
        };
        AutomationProperties.SetAutomationId(transcript, "AiTranscriptBox");
        var promptBox = new TextBox
        {
            Text = "Summarize this page.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80
        };
        AutomationProperties.SetAutomationId(promptBox, "AiPromptBox");
        var includeContext = new CheckBox
        {
            Content = "Include current page title, URL, and extracted article (explicit consent)",
            IsChecked = false
        };
        AutomationProperties.SetAutomationId(includeContext, "AiIncludePageContext");
        var contextStatus = new TextBlock
        {
            Text = "Page context: OFF",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        AutomationProperties.SetAutomationId(contextStatus, "AiContextStatus");
        var status = new TextBlock
        {
            Text = _aiApiSettings.Mode == AiExecutionMode.Mock
                ? "Mode: Mock (offline)."
                : "Mode: Actual OpenAI. Requests may incur charges on your account.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        AutomationProperties.SetAutomationId(status, "AiRequestStatus");
        var progress = new ProgressRing { IsActive = false, Width = 20, Height = 20 };
        var stopButton = new Button { Content = "Stop", IsEnabled = false };
        AutomationProperties.SetAutomationId(stopButton, "AiStopButton");
        var retryButton = new Button { Content = "Retry", IsEnabled = false };
        AutomationProperties.SetAutomationId(retryButton, "AiRetryButton");
        var copyButton = new Button { Content = "Copy conversation" };
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
                contextStatus.Text = "Page context: OFF";
                return;
            }
            if (active is null || active.Model.IsNewTabPage || active.View.CoreWebView2 is null)
            {
                contextStatus.Text = "Page context: unavailable on this page.";
                includeContext.IsChecked = false;
                return;
            }
            contextStatus.Text = $"Page context: ON — {active.Model.Title}";
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
            status.Text = "Conversation copied.";
        };

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        buttonPanel.Children.Add(stopButton);
        buttonPanel.Children.Add(retryButton);
        buttonPanel.Children.Add(copyButton);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "Page content is sent only when you enable the context checkbox before pressing Send. " +
                   "Page content is treated as untrusted data; it cannot change AMARE's security rules.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        panel.Children.Add(transcript);
        panel.Children.Add(new TextBlock { Text = "Question" });
        panel.Children.Add(promptBox);
        panel.Children.Add(includeContext);
        panel.Children.Add(contextStatus);
        panel.Children.Add(buttonPanel);
        panel.Children.Add(progress);
        panel.Children.Add(status);
        var dialog = new ContentDialog
        {
            Title = "API AI",
            Content = new ScrollViewer { MaxHeight = 620, Content = panel },
            PrimaryButtonText = "Send",
            SecondaryButtonText = "Clear conversation",
            CloseButtonText = "Close",
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
                    status.Text = "Enter a question first.";
                    return;
                }
                if (includeContext.IsChecked == true)
                {
                    var capturedHost = ActiveHost;
                    pageContext = await CaptureAiPageContextAsync();
                    if (pageContext is null || !ReferenceEquals(ActiveHost, capturedHost))
                    {
                        status.Text = "Page context could not be captured. The request was not sent.";
                        return;
                    }
                    Append($"Page context: {pageContext.Title}\n{pageContext.Url}");
                }
                history.Add(AiMessage.User(prompt));
                Append("You: " + prompt);
            }
            else if (lastRequest is null)
            {
                status.Text = "There is no failed request to retry.";
                return;
            }

            var request = retry && lastRequest is not null
                ? lastRequest
                : new AiRequest(history.ToArray(), pageContext);
            lastRequest = request;
            var requestHost = ActiveHost;
            if (requestHost is null)
            {
                status.Text = "No active page.";
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
                ? "Mock response generating…"
                : "Request sent to OpenAI…";
            Append("AMARE: ");
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
                        ? "Mock response complete. No network request was sent."
                        : "Response complete.";
                    retryButton.IsEnabled = false;
                }
            }
            catch (OperationCanceledException)
            {
                status.Text = "Request canceled.";
                retryButton.IsEnabled = lastRequest is not null;
            }
            catch (AiException exception)
            {
                AppLog.Warn($"AI request failed kind={exception.Kind}");
                status.Text = exception.UserMessage;
                Append($"[request failed] {exception.UserMessage}");
                retryButton.IsEnabled = lastRequest is not null;
            }
            catch (Exception exception)
            {
                AppLog.Warn($"AI request failed type={exception.GetType().Name}");
                status.Text = "The AI request failed.";
                Append("[request failed] The AI request failed.");
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
            status.Text = "Conversation cleared.";
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
        await dialog.ShowAsync();
    }

    private async Task ShowAiApiSettingsAsync()
    {
        var existingKey = AiCredentialStore.GetKey();
        var keyBox = new PasswordBox
        {
            PlaceholderText = existingKey is null
                ? "OpenAI API key"
                : "Configured — leave blank to keep the current key"
        };
        AutomationProperties.SetAutomationId(keyBox, "AiKeyBox");
        var keyStatus = new TextBlock
        {
            Text = existingKey is null
                ? "No API key configured."
                : "API key configured in Windows Credential Manager (masked).",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
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
            Content = "Enable AMARE API assistant",
            IsChecked = _aiApiSettings.Enabled
        };
        AutomationProperties.SetAutomationId(enabledBox, "AiEnabledBox");
        var modeCombo = new ComboBox
        {
            ItemsSource = new[] { "Mock (offline)", "Actual API" },
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
        var testButton = new Button { Content = "Test connection" };
        testButton.Click += async (_, _) =>
        {
            try
            {
                testStatus.Text = "Testing…";
                var provider = providerCombo.SelectedItem as AiProviderDescriptor
                               ?? AiProviderCatalog.All[0];
                var settings = new AiApiSettings(
                    modelBox.Text.Trim().Length == 0 ? provider.DefaultModel : modelBox.Text.Trim(),
                    baseUrlBox.Text.Trim().Length == 0 ? provider.BaseUrl : baseUrlBox.Text.Trim(),
                    provider.Id,
                    enabledBox.IsChecked == true,
                    modeCombo.SelectedIndex == 0 ? AiExecutionMode.Mock : AiExecutionMode.Actual);
                var key = keyBox.Password.Trim().Length > 0 ? keyBox.Password : existingKey;
                testStatus.Text = await TestAiConnectionAsync(settings, key);
                keyRejected = settings.Mode == AiExecutionMode.Actual
                              && testStatus.Text.StartsWith("The OpenAI API key was rejected", StringComparison.Ordinal);
            }
            catch (Exception exception)
            {
                testStatus.Text = $"Connection test failed: {exception.GetType().Name}";
            }
        };
        var deleteButton = new Button { Content = "Delete stored key" };
        AutomationProperties.SetAutomationId(deleteButton, "AiDeleteKeyButton");
        deleteButton.Click += (_, _) =>
        {
            if (AiCredentialStore.Delete())
            {
                keyStatus.Text = "API key deleted.";
                testStatus.Text = "Stored key deleted.";
            }
            else
            {
                testStatus.Text = "The stored key could not be deleted.";
            }
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "The API key is stored only in Windows Credential Manager and is never written to settings, logs, or the AI WebView.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(enabledBox);
        panel.Children.Add(new TextBlock { Text = "Mode" });
        panel.Children.Add(modeCombo);
        panel.Children.Add(new TextBlock { Text = "Provider" });
        panel.Children.Add(providerCombo);
        panel.Children.Add(new TextBlock { Text = "Model (editable)" });
        panel.Children.Add(modelBox);
        panel.Children.Add(new TextBlock { Text = "Base URL (HTTPS only)" });
        panel.Children.Add(baseUrlBox);
        panel.Children.Add(new TextBlock { Text = "API key" });
        panel.Children.Add(keyBox);
        panel.Children.Add(keyStatus);
        panel.Children.Add(testButton);
        panel.Children.Add(testStatus);
        panel.Children.Add(deleteButton);
        panel.Children.Add(new TextBlock
        {
            Text = "Actual API requests are sent to the configured endpoint and may incur charges on your API account. " +
                   "AMARE does not control your plan, rate limits, or billing.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        var dialog = new ContentDialog
        {
            Title = "API AI settings",
            Content = new ScrollViewer { MaxHeight = 620, Content = panel },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        if (keyRejected && keyBox.Password.Trim().Length > 0)
        {
            await ShowMessageAsync(
                "API AI settings",
                "The new API key was rejected. The previously stored key was not replaced.");
            return;
        }
        var selectedProvider = providerCombo.SelectedItem as AiProviderDescriptor
                               ?? AiProviderCatalog.All[0];
        var newKey = keyBox.Password.Trim();
        if (newKey.Length > 0 && !AiCredentialStore.Save(newKey))
        {
            await ShowMessageAsync("API AI settings", "Windows Credential Manager could not store the key.");
            return;
        }
        var settings = new AiApiSettings(
            modelBox.Text.Trim().Length == 0 ? selectedProvider.DefaultModel : modelBox.Text.Trim(),
            baseUrlBox.Text.Trim().Length == 0 ? selectedProvider.BaseUrl : baseUrlBox.Text.Trim(),
            selectedProvider.Id,
            enabledBox.IsChecked == true,
            modeCombo.SelectedIndex == 0 ? AiExecutionMode.Mock : AiExecutionMode.Actual);
        _aiApiSettings = settings;
        SaveAiApiSettings();
        StatusText.Text = settings.Mode == AiExecutionMode.Actual
                          && string.IsNullOrWhiteSpace(AiCredentialStore.GetKey())
            ? "AI settings saved. Actual API requests are blocked until a key is added."
            : "AI settings saved.";
    }

}
