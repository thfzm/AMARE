using System.Text;
using System.Text.Json;
using Amare.Windows.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Amare.Windows;

public sealed partial class MainWindow
{
    private const string TestVaultHost = "amare-vault.test";
    private const string TestVaultRootEnv = "AMARE_TEST_VAULT_ROOT";

    private bool _webVaultDialogOpen;
    private PendingVaultCommand? _pendingVaultCommand;

    private sealed class PendingVaultCommand
    {
        public required string OperationId { get; init; }
        public required WebViewVaultCommandKind Kind { get; init; }
        public required TaskCompletionSource<object?> Completion { get; init; }
    }

    private void InvalidateWebViewVaultOperations()
    {
        foreach (var host in _hosts.Values)
        {
            host.VaultState.InvalidateOperations();
        }
        CancelVaultCommand(null);
    }

    private void OnWebViewVaultTabClosed(BrowserTab host)
    {
        CancelVaultCommand(host);
        host.VaultState.OnTabClosed();
    }

    private async Task HandleNewTabMessageAsync(
        BrowserTab host,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var json = args.TryGetWebMessageAsString();
            if (string.IsNullOrWhiteSpace(json)
                || Encoding.UTF8.GetByteCount(json) > WebViewVaultTabState.MaxMessageBytes)
            {
                return;
            }
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("token", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String
                || !string.Equals(tokenElement.GetString(), host.NewTabNonce, StringComparison.Ordinal)
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || !string.Equals(typeElement.GetString(), "search", StringComparison.Ordinal)
                || !root.TryGetProperty("query", out var queryElement)
                || queryElement.ValueKind != JsonValueKind.String)
            {
                return;
            }
            var query = queryElement.GetString() ?? "";
            if (query.Length > 512 || query.Any(char.IsControl))
            {
                return;
            }
            var resolution = AddressBarResolver.Resolve(query, _engineId);
            if (resolution.IsNavigation)
            {
                Navigate(host, resolution.Target);
            }
        }
        catch (Exception exception)
        {
            AppLog.Warn($"New tab action failed: {exception.GetType().Name}");
        }
        await Task.CompletedTask;
    }

    private async Task ConfigureWebViewVaultAsync(BrowserTab host)
    {
        var core = host.View.CoreWebView2;
        if (core is null)
        {
            host.VaultBridgeReady = false;
            return;
        }
        try
        {
            host.VaultState.SetContext(
                _profileId,
                _context is null ? 0 : (long)_context.Generation,
                host.WebViewId);
            if (!ApplyBuiltInAutofillPolicy(core))
            {
                host.VaultBridgeReady = false;
                StatusText.Text = "Password Vault is unavailable: WebView2 built-in autofill could not be disabled.";
                return;
            }
            ConfigureTestVaultHost(core);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(WebViewVaultScript.Script);
            host.VaultBridgeReady = false;
        }
        catch (Exception exception)
        {
            host.VaultBridgeReady = false;
            AppLog.Warn($"WebView2 vault bridge unavailable: {exception.GetType().Name}");
            StatusText.Text = "Password Vault is unavailable for this tab.";
        }
    }

    private static bool ApplyBuiltInAutofillPolicy(CoreWebView2 core)
    {
        try
        {
            // These are profile-wide settings. Setting both the profile and the
            // current WebView keeps every instance in the same profile aligned.
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Profile.IsPasswordAutosaveEnabled = false;
            core.Profile.IsGeneralAutofillEnabled = false;
            return true;
        }
        catch (Exception exception)
        {
            // Fail closed: the Vault bridge is not enabled when the built-in
            // password/autofill controls cannot be disabled.
            AppLog.Warn($"WebView2 built-in autofill policy failed: {exception.GetType().Name}");
            return false;
        }
    }

    private static void ConfigureTestVaultHost(CoreWebView2 core)
    {
        var root = Environment.GetEnvironmentVariable(TestVaultRootEnv);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }
        try
        {
            // Test-only HTTPS virtual host for UI smoke. Deny prevents other
            // sites from reading the assets; the top-level navigation still works.
            core.SetVirtualHostNameToFolderMapping(
                TestVaultHost,
                root,
                CoreWebView2HostResourceAccessKind.Deny);
        }
        catch (Exception exception)
        {
            AppLog.Warn($"WebView2 test vault host unavailable: {exception.GetType().Name}");
        }
    }

    private void InvalidateVaultForNavigation(BrowserTab host)
    {
        CancelVaultCommand(host);
        host.VaultState.OnNavigationStarting();
        host.VaultBridgeReady = false;
    }

    private void BeginVaultDocument(BrowserTab host, CoreWebView2 core)
    {
        host.VaultBridgeReady = false;
        host.VaultState.CancelPendingCommand();
        if (_context is null
            || host.Closed
            || host.ShowingNewTab
            || host.Model.IsNewTabPage
            || !ReferenceEquals(host.View.CoreWebView2, core)
            || !host.VaultState.IsContextCurrent(_profileId, (long)_context.Generation)
            || !WebOrigin.TryParse(core.Source, out var origin)
            || !host.VaultState.TryBeginDocument(origin, core.Source, out var token))
        {
            host.VaultState.OnDocumentFailed();
            return;
        }
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "amare_init",
                version = 1,
                token,
                generation = host.VaultState.Generation,
                origin = origin.Value
            }));
        }
        catch (Exception exception)
        {
            AppLog.Warn($"WebView2 vault init failed: {exception.GetType().Name}");
            host.VaultState.OnDocumentFailed();
        }
    }

    private async Task HandleVaultWebMessageAsync(
        BrowserTab host,
        CoreWebView2 core,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (host.Closed
            || !ReferenceEquals(host.View.CoreWebView2, core)
            || _context is null
            || !host.VaultState.IsContextCurrent(_profileId, (long)_context.Generation))
        {
            return;
        }
        var json = args.WebMessageAsJson;
        if (string.IsNullOrWhiteSpace(json)
            || Encoding.UTF8.GetByteCount(json) > WebViewVaultTabState.MaxMessageBytes)
        {
            return;
        }

        string? type = null;
        string? operationId = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            type = ReadString(root, "type");
            operationId = ReadString(root, "opId");
        }
        catch (JsonException)
        {
            return;
        }

        switch (type)
        {
            case "amare_ready":
                if (host.VaultState.TryAcceptReady(json, args.Source, out _))
                {
                    host.VaultBridgeReady = true;
                }
                return;
            case "amare_detect_result":
                if (operationId is not null
                    && host.VaultState.TryAcceptDetectResult(
                        json, args.Source, operationId, out var descriptor))
                {
                    CompleteVaultCommand(operationId, descriptor);
                }
                return;
            case "amare_secret":
                if (operationId is not null
                    && host.VaultState.TryAcceptSecret(
                        json, args.Source, operationId, out var secret))
                {
                    CompleteVaultCommand(operationId, secret);
                }
                return;
            case "amare_fill_result":
                if (operationId is not null
                    && host.VaultState.TryAcceptFillResult(
                        json, args.Source, operationId, out var ok))
                {
                    CompleteVaultCommand(operationId, ok);
                }
                return;
            case "amare_login_candidate":
                await HandleLoginCandidateAsync(host, core, json, args.Source);
                return;
        }
    }

    private async Task HandleLoginCandidateAsync(
        BrowserTab host,
        CoreWebView2 core,
        string json,
        string source)
    {
        VaultIdleMonitor.Touch();
        if (_context is null
            || _webVaultDialogOpen
            || !EnsureVaultClient()
            || _vaultClient is not { } vault)
        {
            AppLog.Info("WebView login candidate ignored: vault client unavailable");
            return;
        }
        AppLog.Info("WebView login candidate received");
        var context = _context;
        var profileId = _profileId;
        if (!host.VaultState.IsContextCurrent(profileId, (long)context.Generation))
        {
            return;
        }

        VaultStatusDto status;
        try
        {
            status = await vault.StatusAsync(context);
        }
        catch
        {
            StatusText.Text = "Password detected, but Vault status could not be checked.";
            return;
        }
        if (!status.Unlocked)
        {
            AppLog.Info("WebView login candidate deferred: vault locked");
            StatusText.Text = "Password detected. Unlock the Vault before saving it.";
            return;
        }
        if (!host.VaultState.TryAcceptLoginCandidate(json, source, out var candidate))
        {
            return;
        }

        _webVaultDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Save password?",
                Content = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Site: {candidate.Origin.Value}",
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = $"Username: {candidate.Username}",
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = "AMARE will only save after this confirmation. It will not submit the form or sign in automatically.",
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                },
                PrimaryButtonText = "Save",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            if (!VaultContextStillValid(host, core, vault, context, profileId, candidate.Generation,
                    candidate.Origin, candidate.DocumentUrl))
            {
                StatusText.Text = "The page changed before the password was saved. Nothing was saved.";
                return;
            }
            status = await vault.StatusAsync(context);
            if (!status.Unlocked)
            {
                StatusText.Text = "The Vault is locked. Nothing was saved.";
                return;
            }

            if (!TryBeginVaultCommand(host, WebViewVaultCommandKind.ReadSecret,
                    candidate.FormToken, out var operationId, out var completion)
                || !SendVaultCommand(host, core, "amare_read_secret", operationId,
                    new Dictionary<string, object?>
                    {
                        ["formToken"] = candidate.FormToken
                    }))
            {
                CancelVaultCommand(host);
                StatusText.Text = "The login form is not ready for capture. Nothing was saved.";
                return;
            }
            var result = await AwaitVaultCommandAsync(host, completion);
            if (result is not WebViewVaultSecret secret)
            {
                StatusText.Text = "The login form changed before the password could be read. Nothing was saved.";
                return;
            }
            try
            {
                if (!VaultContextStillValid(host, core, vault, context, profileId, candidate.Generation,
                        candidate.Origin, candidate.DocumentUrl)
                    || !string.Equals(secret.Username, candidate.Username, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(secret.Username)
                    || secret.Username.Length > WebViewVaultTabState.MaxUsernameLength
                    || secret.Username.Any(char.IsControl)
                    || string.IsNullOrEmpty(secret.Password)
                    || secret.Password.Length > WebViewVaultTabState.MaxPasswordLength
                    || secret.Password.Contains('\0'))
                {
                    StatusText.Text = "The login form changed before the password was saved. Nothing was saved.";
                    return;
                }

                var entries = await vault.ListAsync(context);
                var duplicate = entries.Any(entry =>
                    WebOrigin.TryParse(entry.Origin, out var existingOrigin)
                    && existingOrigin == candidate.Origin
                    && string.Equals(entry.Username, secret.Username, StringComparison.Ordinal));
                if (duplicate)
                {
                    StatusText.Text = "This account is already saved. Existing credentials were not overwritten.";
                    return;
                }

                await vault.CreateAsync(
                    context,
                    candidate.Origin.Value,
                    secret.Username,
                    secret.Password,
                    candidate.Origin.Value);
                StatusText.Text = "Password saved to Vault.";
                if (_vaultOpen)
                {
                    await LoadVaultEntriesAsync();
                }
            }
            finally
            {
                secret.Clear();
            }
        }
        catch (VaultException)
        {
            StatusText.Text = "Password could not be saved. The Vault rejected the operation.";
        }
        catch (Exception exception)
        {
            AppLog.Warn($"WebView2 password save failed: {exception.GetType().Name}");
            StatusText.Text = "Password could not be saved.";
        }
        finally
        {
            _webVaultDialogOpen = false;
        }
    }

    private async Task ShowVaultFillDialogAsync()
    {
        VaultIdleMonitor.Touch();
        if (_context is null || !EnsureVaultClient() || _vaultClient is not { } vault)
        {
            StatusText.Text = "Password Vault is not available.";
            return;
        }
        if (_webVaultDialogOpen)
        {
            return;
        }
        if (ActiveHost is not { } host || host.View.CoreWebView2 is not { } core)
        {
            StatusText.Text = "Open a web page before filling a password.";
            return;
        }
        var context = _context;
        var profileId = _profileId;
        if (!host.VaultBridgeReady
            || !host.VaultState.IsContextCurrent(profileId, (long)context.Generation)
            || !WebOrigin.TryParse(core.Source, out var origin))
        {
            StatusText.Text = "Password Vault is unavailable for this page.";
            return;
        }

        _webVaultDialogOpen = true;
        try
        {
            VaultStatusDto status;
            try
            {
                status = await vault.StatusAsync(context);
            }
            catch
            {
                StatusText.Text = "Vault status could not be checked.";
                return;
            }
            if (!status.Unlocked)
            {
                StatusText.Text = "Unlock the Vault before filling a password.";
                return;
            }

            if (!TryBeginVaultCommand(host, WebViewVaultCommandKind.Detect,
                    null, out var detectOperation, out var detectCompletion)
                || !SendVaultCommand(host, core, "amare_detect", detectOperation))
            {
                CancelVaultCommand(host);
                StatusText.Text = "The page could not be inspected for a login form.";
                return;
            }
            var detected = await AwaitVaultCommandAsync(host, detectCompletion);
            if (detected is not WebViewVaultFormDescriptor descriptor
                || descriptor.Ambiguous
                || descriptor.DocumentOrigin != origin
                || descriptor.Generation != host.VaultState.Generation)
            {
                StatusText.Text = "No single eligible login form was found on this page.";
                return;
            }

            IReadOnlyList<VaultEntryViewDto> entries;
            try
            {
                entries = await vault.ListAsync(context);
            }
            catch (Exception exception)
            {
                AppLog.Warn($"WebView2 fill list failed: {exception.GetType().Name}");
                StatusText.Text = "Vault entries could not be loaded.";
                return;
            }
            var exact = entries
                .Where(entry => WebOrigin.TryParse(entry.Origin, out var entryOrigin) && entryOrigin == origin)
                .Select(entry => new VaultFillChoice(entry, $"{entry.Username} — {entry.Title}"))
                .ToList();
            if (exact.Count == 0)
            {
                StatusText.Text = $"No saved account matches {origin.Value} exactly.";
                return;
            }

            var list = new ListView
            {
                ItemsSource = exact,
                DisplayMemberPath = nameof(VaultFillChoice.Label),
                SelectionMode = ListViewSelectionMode.Single,
                MaxHeight = 240
            };
            var dialog = new ContentDialog
            {
                Title = "Fill password",
                Content = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Current site: {origin.Value}",
                            TextWrapping = TextWrapping.Wrap
                        },
                        list,
                        new TextBlock
                        {
                            Text = "Only exact-origin accounts are listed. AMARE fills the visible form but never submits it.",
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                },
                PrimaryButtonText = "Fill",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = false,
                XamlRoot = RootGrid.XamlRoot
            };
            list.SelectionChanged += (_, _) =>
                dialog.IsPrimaryButtonEnabled = list.SelectedItem is VaultFillChoice;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary
                || list.SelectedItem is not VaultFillChoice choice)
            {
                return;
            }

            if (!VaultContextStillValid(host, core, vault, context, profileId,
                    descriptor.Generation, origin, descriptor.DocumentUrl))
            {
                StatusText.Text = "The page changed before fill. Nothing was filled.";
                return;
            }

            VaultEntryDto secret;
            try
            {
                secret = await vault.GetForFillAsync(context, origin.Value, choice.Entry.Id);
            }
            catch (VaultException)
            {
                StatusText.Text = "The Vault rejected the fill request. Nothing was filled.";
                return;
            }
            catch (Exception exception)
            {
                AppLog.Warn($"WebView2 fill lookup failed: {exception.GetType().Name}");
                StatusText.Text = "The Vault could not provide the fill entry.";
                return;
            }
            try
            {
                if (!WebOrigin.TryParse(secret.Origin, out var secretOrigin)
                    || secretOrigin != origin
                    || !VaultContextStillValid(host, core, vault, context, profileId,
                        descriptor.Generation, origin, descriptor.DocumentUrl))
                {
                    StatusText.Text = "The page changed before fill. Nothing was filled.";
                    return;
                }

                if (!TryBeginVaultCommand(host, WebViewVaultCommandKind.Fill,
                        descriptor.FormToken, out var fillOperation, out var fillCompletion)
                    || !SendVaultCommand(host, core, "amare_fill", fillOperation,
                        new Dictionary<string, object?>
                        {
                            ["formToken"] = descriptor.FormToken,
                            ["username"] = secret.Username,
                            ["password"] = secret.Password
                        }))
                {
                    CancelVaultCommand(host);
                    StatusText.Text = "The login form changed before fill. Nothing was filled.";
                    return;
                }
                var fillResult = await AwaitVaultCommandAsync(host, fillCompletion);
                StatusText.Text = fillResult is bool completed && completed
                    ? "Password filled. Press Sign in when you are ready."
                    : "The page did not accept the fill request. Nothing was submitted.";
            }
            catch (Exception exception)
            {
                AppLog.Warn($"WebView2 fill failed: {exception.GetType().Name}");
                StatusText.Text = "Password could not be filled.";
            }
            finally
            {
                secret.ClearPassword();
            }
        }
        finally
        {
            _webVaultDialogOpen = false;
        }
    }

    private bool VaultContextStillValid(
        BrowserTab host,
        CoreWebView2 core,
        AmareVaultClient vault,
        ProfileContextDto context,
        string profileId,
        long generation,
        WebOrigin origin,
        string documentUrl)
    {
        return !host.Closed
               && ReferenceEquals(host.View.CoreWebView2, core)
               && ReferenceEquals(_vaultClient, vault)
               && _context == context
               && _profileId == profileId
               && host.VaultState.IsContextCurrent(profileId, (long)context.Generation)
               && host.VaultState.IsCurrent(generation, origin, documentUrl);
    }

    private bool TryBeginVaultCommand(
        BrowserTab host,
        WebViewVaultCommandKind kind,
        string? formToken,
        out string operationId,
        out Task<object?> completion)
    {
        operationId = "";
        completion = Task.FromResult<object?>(null);
        if (_pendingVaultCommand is not null
            || !host.VaultState.TryBeginCommand(kind, formToken, out operationId))
        {
            return false;
        }
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingVaultCommand = new PendingVaultCommand
        {
            OperationId = operationId,
            Kind = kind,
            Completion = tcs
        };
        completion = tcs.Task;
        return true;
    }

    private bool SendVaultCommand(
        BrowserTab host,
        CoreWebView2 core,
        string type,
        string operationId,
        IReadOnlyDictionary<string, object?>? extra = null)
    {
        if (_context is null
            || !host.VaultState.IsContextCurrent(_profileId, (long)_context.Generation))
        {
            return false;
        }
        var token = host.VaultState.CurrentToken;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["version"] = 1,
            ["token"] = token,
            ["generation"] = host.VaultState.Generation,
            ["opId"] = operationId
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                payload[key] = value;
            }
        }
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Warn($"WebView2 vault command failed: {exception.GetType().Name}");
            return false;
        }
    }

    private void CompleteVaultCommand(string operationId, object? result)
    {
        var pending = _pendingVaultCommand;
        if (pending is null
            || !string.Equals(pending.OperationId, operationId, StringComparison.Ordinal))
        {
            return;
        }
        _pendingVaultCommand = null;
        pending.Completion.TrySetResult(result);
    }

    private async Task<object?> AwaitVaultCommandAsync(BrowserTab host, Task<object?> completion)
    {
        var finished = await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(5)));
        if (finished != completion)
        {
            host.VaultState.CancelPendingCommand();
            _pendingVaultCommand = null;
            return null;
        }
        return await completion;
    }

    private void CancelVaultCommand(BrowserTab? host)
    {
        host?.VaultState.CancelPendingCommand();
        var pending = _pendingVaultCommand;
        _pendingVaultCommand = null;
        pending?.Completion.TrySetResult(null);
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private async Task ShowClearWebViewDataDialogAsync()
    {
        if (ActiveHost is not { } host || host.View.CoreWebView2 is not { } core)
        {
            StatusText.Text = "Open a web page before clearing WebView2 saved data.";
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "Clear WebView2 saved passwords?",
            Content = "This clears WebView2 saved passwords and general autofill data for this profile. " +
                      "Cookies, browsing history and Vault entries are not cleared. This cannot be undone.",
            PrimaryButtonText = "Clear",
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
            await core.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.PasswordAutosave
                | CoreWebView2BrowsingDataKinds.GeneralAutofill);
            StatusText.Text = "WebView2 saved passwords and general autofill data were cleared for this profile.";
        }
        catch (Exception exception)
        {
            AppLog.Warn($"WebView2 data clear failed: {exception.GetType().Name}");
            StatusText.Text = "WebView2 saved data could not be cleared.";
        }
    }

    private sealed record VaultFillChoice(VaultEntryViewDto Entry, string Label);
}
