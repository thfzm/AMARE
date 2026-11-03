using System.Text.Json;
using Amare.Windows.Core;
using Microsoft.UI.Dispatching;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace Amare.Windows;

public sealed partial class MainWindow : Window
{
    private readonly TabManager _tabs = new();
    private readonly Dictionary<Guid, BrowserTab> _hosts = [];
    private readonly List<DownloadRecord> _downloads = [];
    private readonly ClosedTabHistory _closedTabs = new(20);
    private readonly SemaphoreSlim _sessionSaveGate = new(1, 1);
    private readonly DispatcherQueue _dispatcher;
    private readonly string _appDataRoot;
    private readonly ProfileDataCleanup _profileCleanup;

    private AmareNative? _native;
    private ProfileClient? _profiles;
    private SettingsClient? _settings;
    private SearchClient? _search;
    private SessionClient? _sessions;
    private HistoryClient? _history;
    private ProfileContextDto? _context;
    private CoreWebView2Environment? _environment;
    private DispatcherQueueTimer? _saveTimer;
    private string _profileId = "";
    private string _engineId = SearchEngineCatalog.DefaultId;
    private bool _restoreSession = true;
    private bool _initializingSettings;
    private bool _started;
    private long? _sessionId;
    private string? _lastSessionFingerprint;

    private sealed class BrowserTab
    {
        public required BrowserTabModel Model { get; init; }
        public required WebView2 View { get; init; }
        public required string NewTabNonce { get; init; }
        public bool ShowingNewTab { get; set; }
        public bool Closed { get; set; }
    }

    private sealed record DownloadRecord(string FileName, string Path);

    public MainWindow()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMARE");
        _profileCleanup = new ProfileDataCleanup(_appDataRoot);
        AppLog.Initialize(_appDataRoot);
        AppLog.Info("MainWindow created");
        TabList.ItemsSource = _tabs.Tabs;
        SearchEngineBox.ItemsSource = SearchEngineCatalog.All;
        RegisterKeyboardAccelerators();
        Closed += MainWindow_Closed;
    }

    private void RegisterKeyboardAccelerators()
    {
        AddAccelerator(VirtualKey.L, VirtualKeyModifiers.Control, BrowserCommand.FocusAddressBar);
        AddAccelerator(VirtualKey.T, VirtualKeyModifiers.Control, BrowserCommand.NewTab);
        AddAccelerator(VirtualKey.W, VirtualKeyModifiers.Control, BrowserCommand.CloseTab);
        AddAccelerator(VirtualKey.T, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, BrowserCommand.RestoreClosedTab);
        AddAccelerator(VirtualKey.R, VirtualKeyModifiers.Control, BrowserCommand.Reload);
        AddAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, BrowserCommand.Back);
        AddAccelerator(VirtualKey.Right, VirtualKeyModifiers.Menu, BrowserCommand.Forward);
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, BrowserCommand command)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (sender, args) =>
        {
            args.Handled = true;
            _ = ExecuteShortcutAsync(command);
        };
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private async Task ExecuteShortcutAsync(BrowserCommand command)
    {
        try
        {
            switch (command)
            {
                case BrowserCommand.FocusAddressBar:
                    AddressBar.Focus(FocusState.Programmatic);
                    AddressBar.SelectAll();
                    break;
                case BrowserCommand.NewTab:
                    await CreateTabAsync("amare://newtab", "New Tab", false, activate: true);
                    break;
                case BrowserCommand.CloseTab:
                    if (_tabs.ActiveTab is { } active)
                    {
                        await CloseTabAsync(active.Id);
                    }
                    break;
                case BrowserCommand.RestoreClosedTab:
                    await RestoreClosedTabAsync();
                    break;
                case BrowserCommand.Reload:
                    ReloadActive();
                    break;
                case BrowserCommand.Back:
                    if (ActiveHost?.View.CoreWebView2?.CanGoBack == true)
                    {
                        ActiveHost.View.CoreWebView2.GoBack();
                    }
                    break;
                case BrowserCommand.Forward:
                    if (ActiveHost?.View.CoreWebView2?.CanGoForward == true)
                    {
                        ActiveHost.View.CoreWebView2.GoForward();
                    }
                    break;
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Shortcut failed: {exception.Message}";
        }
    }

    private async Task RestoreClosedTabAsync()
    {
        var snapshot = _closedTabs.Pop();
        if (snapshot is null)
        {
            return;
        }
        await CreateTabAsync(snapshot.Url, snapshot.Title, snapshot.Pinned, activate: true);
    }

    private void ReloadActive()
    {
        var active = ActiveHost;
        if (active?.View.CoreWebView2 is null)
        {
            return;
        }
        if (active.Model.IsLoading)
        {
            active.View.CoreWebView2.Stop();
        }
        else
        {
            active.View.CoreWebView2.Reload();
        }
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }
        _started = true;
        try
        {
            await InitializeAsync();
        }
        catch (Exception exception)
        {
            AppLog.Error("AMARE startup failed", exception);
            StatusText.Text = "AMARE could not start.";
            await ShowErrorAsync("AMARE could not start", exception.Message);
        }
    }

    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(_appDataRoot);
        _native = AmareNative.Create(Path.Combine(_appDataRoot, "data"));
        _profiles = new ProfileClient(_native);
        _settings = new SettingsClient(_native);
        _search = new SearchClient(_native);
        _sessions = new SessionClient(_native);
        _history = new HistoryClient(_native);

        _context = await _profiles.InitializeAsync();
        _profileId = (await _profiles.GetActiveAsync())?.Id ?? _context.ProfileId;
        _saveTimer = _dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(500);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await SaveSessionAsync();

        await LoadSettingsAsync();
        await CreateEnvironmentAsync();
        await RestoreSessionOrNewTabAsync();
        _ = ResumeProfileCleanupAsync();
        AppLog.Info($"AMARE initialized profile={_profileId} engine={_engineId} restore_session={_restoreSession}");
        StatusText.Text = "Ready";
    }

    private async Task LoadSettingsAsync()
    {
        if (_context is null || _settings is null)
        {
            return;
        }
        _initializingSettings = true;
        try
        {
            var engine = await _settings.GetAsync(_context, "engine");
            _engineId = SearchEngineCatalog.Resolve(engine.Value.AsText()).Id;
            SearchEngineBox.SelectedItem = SearchEngineCatalog.Resolve(_engineId);
            var restore = await _settings.GetAsync(_context, "restore_session");
            _restoreSession = restore.Value.AsBoolean();
            var theme = await _settings.GetAsync(_context, "theme");
            ApplyTheme(theme.Value.AsText());
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Settings unavailable: {exception.Message}";
        }
        finally
        {
            _initializingSettings = false;
        }
    }

    private void ApplyTheme(string theme)
    {
        RootGrid.RequestedTheme = theme.ToLowerInvariant() switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private async Task CreateEnvironmentAsync()
    {
        var folder = ProfilePathPolicy.GetWebViewDataFolder(_appDataRoot, _profileId);
        Directory.CreateDirectory(folder);
        _environment = await CoreWebView2Environment.CreateWithOptionsAsync(
            null, folder, new CoreWebView2EnvironmentOptions());
    }

    private async Task RestoreSessionOrNewTabAsync()
    {
        if (_restoreSession && _context is not null && _sessions is not null)
        {
            try
            {
                var latest = await _sessions.LatestAsync(_context);
                var plan = SessionRestorePlanner.Create(latest);
                if (plan is not null)
                {
                    _sessionId = latest!.Session.Id;
                    _lastSessionFingerprint = null;
                    var models = new List<BrowserTabModel>(plan.Tabs.Count);
                    foreach (var tab in plan.Tabs)
                    {
                        models.Add(await CreateTabAsync(tab.Url, tab.Title, tab.Pinned, activate: false));
                    }
                    var active = models[Math.Clamp(plan.ActiveIndex, 0, models.Count - 1)];
                    SelectTab(active);
                    StatusText.Text = "Session restored";
                    return;
                }
            }
            catch (Exception exception)
            {
                StatusText.Text = $"Session restore unavailable: {exception.Message}";
            }
        }
        await CreateTabAsync("amare://newtab", "New Tab", false, activate: true);
    }

    private async Task ResumeProfileCleanupAsync()
    {
        try
        {
            var results = await _profileCleanup.ResumePendingAsync(3);
            var pending = results.Count(result => result.Pending);
            if (pending > 0)
            {
                StatusText.Text = $"{pending} WebView2 profile folder(s) still need cleanup.";
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"WebView2 cleanup resume failed: {exception.Message}";
        }
    }

    private async Task<BrowserTabModel> CreateTabAsync(string url, string? title, bool pinned, bool activate)
    {
        var model = _tabs.AddTab(url, title, activate);
        model.IsPinned = pinned;
        var webView = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        var host = new BrowserTab
        {
            Model = model,
            View = webView,
            NewTabNonce = Guid.NewGuid().ToString("N")
        };
        _hosts[model.Id] = host;
        WebViewHost.Children.Add(webView);
        await WaitForLoadedAsync(webView);
        if (_environment is null)
        {
            throw new InvalidOperationException("WebView2 environment is not ready.");
        }
        await webView.EnsureCoreWebView2Async(_environment);
        AttachWebViewEvents(host);
        if (url.Equals("amare://newtab", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(url))
        {
            await ShowNewTabPageAsync(host);
        }
        else
        {
            Navigate(host, url);
        }
        if (activate)
        {
            SelectTab(model);
        }
        UpdateWebViewVisibility();
        LogTabDiagnostics("created");
        ScheduleSessionSave();
        return model;
    }

    private static Task WaitForLoadedAsync(FrameworkElement element)
    {
        if (element.IsLoaded)
        {
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource();
        RoutedEventHandler? handler = null;
        handler = (_, _) =>
        {
            element.Loaded -= handler;
            completion.TrySetResult();
        };
        element.Loaded += handler;
        return completion.Task;
    }

    private void AttachWebViewEvents(BrowserTab host)
    {
        var core = host.View.CoreWebView2;
        core.NavigationStarting += (sender, args) =>
        {
            var decision = NavigationPolicy.Decide(args.Uri);
            if (decision == NavigationDecision.External)
            {
                args.Cancel = true;
                _ = ConfirmExternalAsync(args.Uri);
                return;
            }
            if (decision == NavigationDecision.Block)
            {
                args.Cancel = true;
                StatusText.Text = $"Blocked navigation: {args.Uri}";
                return;
            }
            if (args.Uri.Equals("amare://newtab", StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
                _ = ShowNewTabPageAsync(host);
                return;
            }
            if (!host.ShowingNewTab)
            {
                host.Model.IsNewTabPage = false;
                host.Model.Url = args.Uri;
                host.Model.IsLoading = true;
                UpdateAddressIfActive(host);
                UpdateToolbar();
            }
        };
        core.SourceChanged += (_, _) =>
        {
            if (!host.ShowingNewTab && !host.Model.IsNewTabPage && host.View.Source is not null)
            {
                host.Model.Url = host.View.Source.AbsoluteUri;
                UpdateAddressIfActive(host);
            }
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            var title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "New Tab" : core.DocumentTitle;
            host.Model.Title = title;
            if (IsActive(host))
            {
                Title = $"{title} — AMARE";
                UpdateToolbar();
            }
        };
        core.NavigationCompleted += (_, args) =>
        {
            host.ShowingNewTab = false;
            host.Model.IsLoading = false;
            UpdateNavigationState(host);
            if (!args.IsSuccess && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                StatusText.Text = $"Page failed to load: {args.WebErrorStatus}";
            }
            if (IsActive(host))
            {
                UpdateToolbar();
                UpdateAddressIfActive(host);
            }
            ScheduleSessionSave();
        };
        core.NewWindowRequested += (sender, args) =>
        {
            args.Handled = true;
            if (NavigationPolicy.CanOpenInNewTab(args.Uri))
            {
                _ = CreateTabAsync(args.Uri, null, false, activate: true);
            }
            else
            {
                _ = ConfirmExternalAsync(args.Uri);
            }
        };
        core.DownloadStarting += (_, args) =>
        {
            try
            {
                var downloads = Path.Combine(_appDataRoot, "downloads");
                Directory.CreateDirectory(downloads);
                var suggested = Path.GetFileName(args.ResultFilePath);
                if (string.IsNullOrWhiteSpace(suggested))
                {
                    suggested = "download";
                }
                var path = DownloadPolicy.AllocatePath(downloads, suggested);
                args.ResultFilePath = path;
                _downloads.Add(new DownloadRecord(suggested, path));
                StatusText.Text = $"Downloading: {suggested}";
            }
            catch (Exception exception)
            {
                args.Cancel = true;
                StatusText.Text = $"Download blocked: {exception.Message}";
            }
        };
        core.WebMessageReceived += async (_, args) =>
        {
            if (!host.ShowingNewTab)
            {
                return;
            }
            try
            {
                var json = args.TryGetWebMessageAsString();
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.GetProperty("token").GetString() != host.NewTabNonce)
                {
                    return;
                }
                if (root.GetProperty("type").GetString() != "search")
                {
                    return;
                }
                var query = root.GetProperty("query").GetString() ?? "";
                var resolution = AddressBarResolver.Resolve(query, _engineId);
                if (resolution.IsNavigation)
                {
                    Navigate(host, resolution.Target);
                }
            }
            catch (Exception exception)
            {
                StatusText.Text = $"New tab action failed: {exception.Message}";
            }
            await Task.CompletedTask;
        };
    }

    private void Navigate(BrowserTab host, string url)
    {
        host.ShowingNewTab = false;
        host.Model.IsNewTabPage = false;
        host.Model.IsLoading = true;
        host.View.CoreWebView2.Navigate(url);
        if (IsActive(host))
        {
            UpdateToolbar();
        }
    }

    private async Task ShowNewTabPageAsync(BrowserTab host)
    {
        host.ShowingNewTab = true;
        host.Model.IsNewTabPage = true;
        host.Model.IsLoading = true;
        host.Model.Url = "";
        host.Model.Title = "New Tab";
        if (IsActive(host))
        {
            AddressBar.Text = "";
        }
        IReadOnlyList<HistoryEntryDto> topSites = [];
        if (_context is not null && _history is not null)
        {
            try
            {
                topSites = await _history.TopAsync(_context, 8);
            }
            catch
            {
                // New tab must remain usable if history is unavailable.
            }
        }
        var html = NewTabPageBuilder.Build(
            topSites, SearchEngineCatalog.Resolve(_engineId).Name, host.NewTabNonce);
        host.View.NavigateToString(html);
        host.Model.IsLoading = false;
        if (IsActive(host))
        {
            UpdateToolbar();
        }
    }

    private async void NewTab_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await CreateTabAsync("amare://newtab", "New Tab", false, activate: true);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not create tab: {exception.Message}";
        }
    }

    private async void TabClose_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button { DataContext: BrowserTabModel model })
            {
                await CloseTabAsync(model.Id);
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not close tab: {exception.Message}";
        }
    }

    private async Task CloseTabAsync(Guid id)
    {
        if (!_hosts.TryGetValue(id, out var host))
        {
            return;
        }
        var wasLast = _tabs.Tabs.Count == 1;
        if (!host.Model.IsNewTabPage && !string.IsNullOrWhiteSpace(host.Model.Url))
        {
            _closedTabs.Push(new TabSnapshot(host.Model.Url, host.Model.Title, host.Model.IsPinned));
        }
        CloseHost(host);
        _hosts.Remove(id);
        var madeReplacement = _tabs.Close(id);
        if (madeReplacement && _tabs.ActiveTab is { } replacement)
        {
            await CreateTabAsync("amare://newtab", "New Tab", false, activate: true);
            // TabManager created a model as a placeholder; remove it now that a
            // fully hosted replacement exists.
            if (replacement != _tabs.ActiveTab)
            {
                _tabs.Tabs.Remove(replacement);
            }
        }
        else if (_tabs.ActiveTab is { } active)
        {
            SelectTab(active);
        }
        if (wasLast && _tabs.Tabs.Count == 0)
        {
            await CreateTabAsync("amare://newtab", "New Tab", false, activate: true);
        }
        UpdateWebViewVisibility();
        UpdateToolbar();
        LogTabDiagnostics("closed");
        ScheduleSessionSave();
    }

    private void CloseHost(BrowserTab host)
    {
        if (host.Closed)
        {
            return;
        }
        host.Closed = true;
        try
        {
            host.View.CoreWebView2?.Stop();
            host.View.Close();
        }
        catch
        {
            // The process may already be shutting down.
        }
        WebViewHost.Children.Remove(host.View);
    }

    private void CloseAllTabs()
    {
        foreach (var host in _hosts.Values)
        {
            CloseHost(host);
        }
        _hosts.Clear();
        _tabs.Clear();
        _environment = null;
        LogTabDiagnostics("cleared");
    }

    private void LogTabDiagnostics(string operation)
    {
        var diagnostics = _tabs.Diagnostics();
        var message = $"tabs {operation}: created={diagnostics.Created} closed={diagnostics.Closed} " +
                      $"live={diagnostics.Live} webviews={_hosts.Count} active_index={diagnostics.ActiveIndex}";
        System.Diagnostics.Debug.WriteLine(message);
        AppLog.Info(message);
    }

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabList.SelectedItem is BrowserTabModel model && _tabs.Activate(model.Id))
        {
            UpdateWebViewVisibility();
            UpdateToolbar();
            UpdateAddressIfActive(null);
            UpdateWindowTitle();
        }
    }

    private void TabList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var ordered = TabList.Items.OfType<BrowserTabModel>().ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            _tabs.Move(ordered[index].Id, index);
        }
        ScheduleSessionSave();
    }

    private void SelectTab(BrowserTabModel model)
    {
        _tabs.Activate(model.Id);
        TabList.SelectedItem = model;
        UpdateWebViewVisibility();
        UpdateToolbar();
        UpdateAddressIfActive(null);
        UpdateWindowTitle();
    }

    private void UpdateWebViewVisibility()
    {
        foreach (var (id, host) in _hosts)
        {
            host.View.Visibility = _tabs.ActiveTab?.Id == id ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private bool IsActive(BrowserTab host) => _tabs.ActiveTab?.Id == host.Model.Id;

    private BrowserTab? ActiveHost =>
        _tabs.ActiveTab is { } active && _hosts.TryGetValue(active.Id, out var host) ? host : null;

    private void UpdateWindowTitle()
    {
        Title = $"{_tabs.ActiveTab?.Title ?? "AMARE"} — AMARE";
    }

    private void UpdateAddressIfActive(BrowserTab? host)
    {
        if (AddressBar.FocusState != FocusState.Unfocused || _tabs.ActiveTab is null)
        {
            return;
        }
        if (host is not null && !IsActive(host))
        {
            return;
        }
        var active = _tabs.ActiveTab;
        AddressBar.Text = active.IsNewTabPage ? "" : active.Url;
    }

    private void UpdateToolbar()
    {
        var active = ActiveHost;
        var core = active?.View.CoreWebView2;
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
        LoadingRing.IsActive = _tabs.ActiveTab?.IsLoading == true;
        ReloadIcon.Glyph = _tabs.ActiveTab?.IsLoading == true ? "\uE71A" : "\uE72C";
        if (active is not null)
        {
            UpdateNavigationState(active);
        }
    }

    private void UpdateNavigationState(BrowserTab host)
    {
        var core = host.View.CoreWebView2;
        host.Model.CanGoBack = core?.CanGoBack == true;
        host.Model.CanGoForward = core?.CanGoForward == true;
        if (IsActive(host))
        {
            BackButton.IsEnabled = host.Model.CanGoBack;
            ForwardButton.IsEnabled = host.Model.CanGoForward;
            LoadingRing.IsActive = host.Model.IsLoading;
            ReloadIcon.Glyph = host.Model.IsLoading ? "\uE71A" : "\uE72C";
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveHost?.View.CoreWebView2?.CanGoBack == true)
        {
            ActiveHost.View.CoreWebView2.GoBack();
        }
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveHost?.View.CoreWebView2?.CanGoForward == true)
        {
            ActiveHost.View.CoreWebView2.GoForward();
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => ReloadActive();

    private async void AddressBar_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }
        e.Handled = true;
        var active = ActiveHost;
        if (active is null)
        {
            return;
        }
        var resolution = AddressBarResolver.Resolve(AddressBar.Text, _engineId);
        switch (resolution.Kind)
        {
            case AddressInputKind.Url when resolution.Target.Equals("amare://newtab", StringComparison.OrdinalIgnoreCase):
                await ShowNewTabPageAsync(active);
                AddressBar.Text = "";
                break;
            case AddressInputKind.Url:
            case AddressInputKind.Search:
                Navigate(active, resolution.Target);
                break;
        }
    }

    private async void SearchEngineBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingSettings || SearchEngineBox.SelectedItem is not SearchEngineDescriptor engine)
        {
            return;
        }
        _engineId = engine.Id;
        if (_context is null || _settings is null)
        {
            return;
        }
        try
        {
            await _settings.SetAsync(_context, "engine", SettingValueDto.Text(engine.Id));
            StatusText.Text = $"Search engine: {engine.Name}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not save search engine: {exception.Message}";
        }
    }

    private async void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (_profiles is null)
        {
            return;
        }
        try
        {
            var page = await _profiles.ListAsync(100);
            var menu = new MenuFlyout();
            foreach (var profile in page.Profiles)
            {
                var item = new MenuFlyoutItem
                {
                    Text = $"{(profile.IsDefault ? "✓ " : "")}{profile.Name}{(profile.Id == _profileId ? "  (current)" : "")}",
                    Tag = profile.Id
                };
                item.Click += async (_, _) => await SwitchProfileAsync(profile.Id);
                menu.Items.Add(item);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
            var create = new MenuFlyoutItem { Text = "New profile…" };
            create.Click += async (_, _) => await CreateProfileAsync();
            menu.Items.Add(create);
            var rename = new MenuFlyoutItem { Text = "Rename current profile…" };
            rename.Click += async (_, _) => await RenameCurrentProfileAsync();
            menu.Items.Add(rename);
            var delete = new MenuFlyoutItem { Text = "Delete current profile…" };
            delete.Click += async (_, _) => await DeleteCurrentProfileAsync();
            menu.Items.Add(delete);
            menu.ShowAt(ProfileButton);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Profile menu unavailable: {exception.Message}";
        }
    }

    private async Task SwitchProfileAsync(string id)
    {
        if (id == _profileId || _profiles is null || _native is null)
        {
            return;
        }
        try
        {
            await SaveSessionAsync(waitForInFlight: true);
            CloseAllTabs();
            _context = await _profiles.SwitchAsync(id);
            _profileId = id;
            _sessionId = null;
            _lastSessionFingerprint = null;
            await LoadSettingsAsync();
            await CreateEnvironmentAsync();
            await RestoreSessionOrNewTabAsync();
            AppLog.Info($"Profile switched to {id}");
            StatusText.Text = $"Profile: {id}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Profile switch failed: {exception.Message}";
            try
            {
                if (_environment is null)
                {
                    await CreateEnvironmentAsync();
                }
                if (_tabs.Tabs.Count == 0)
                {
                    await CreateTabAsync("amare://newtab", "New Tab", false, activate: true);
                }
            }
            catch (Exception recovery)
            {
                StatusText.Text = $"Profile recovery failed: {recovery.Message}";
            }
        }
    }

    private async Task CreateProfileAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "New profile",
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            Content = new TextBox { PlaceholderText = "Profile name", MaxLength = 256 }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        var name = ((TextBox)dialog.Content).Text.Trim();
        if (name.Length == 0 || _profiles is null)
        {
            return;
        }
        var id = "profile-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await _profiles.CreateAsync(id, name);
            await SwitchProfileAsync(id);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not create profile: {exception.Message}";
        }
    }

    private async Task RenameCurrentProfileAsync()
    {
        if (_profiles is null)
        {
            return;
        }
        var current = await _profiles.GetActiveAsync();
        if (current is null)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "Rename profile",
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            Content = new TextBox { Text = current.Name, MaxLength = 256 }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        var name = ((TextBox)dialog.Content).Text.Trim();
        if (name.Length > 0)
        {
            try
            {
                await _profiles.RenameAsync(current.Id, name);
                StatusText.Text = $"Renamed profile to {name}";
            }
            catch (Exception exception)
            {
                StatusText.Text = $"Could not rename profile: {exception.Message}";
            }
        }
    }

    private async Task DeleteCurrentProfileAsync()
    {
        if (_profiles is null)
        {
            return;
        }
        var current = await _profiles.GetActiveAsync();
        if (current is null)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "Delete profile?",
            Content = $"Delete “{current.Name}” and its local WebView2 data? This cannot be undone.",
            PrimaryButtonText = "Delete",
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
            var page = await _profiles.ListAsync(10);
            if (page.Total <= 1)
            {
                StatusText.Text = "The last profile cannot be deleted.";
                return;
            }
            CloseAllTabs();
            await _profiles.DeleteAsync(current.Id);
            var active = await _profiles.GetActiveAsync();
            _context = active is null ? await _profiles.InitializeAsync() : await _profiles.SwitchAsync(active.Id);
            _profileId = _context.ProfileId;
            _sessionId = null;
            _lastSessionFingerprint = null;
            await LoadSettingsAsync();
            await CreateEnvironmentAsync();
            await RestoreSessionOrNewTabAsync();
            await Task.Delay(200);
            var cleanup = await _profileCleanup.CleanupAsync(current.Id, 3);
            AppLog.Info($"Profile deleted id={current.Id} webview_cleanup_removed={cleanup.Removed} pending={cleanup.Pending}");
            StatusText.Text = cleanup.Removed
                ? "Profile deleted"
                : "Profile deleted; WebView2 data cleanup pending.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not delete profile: {exception.Message}";
            try
            {
                if (_environment is null)
                {
                    await CreateEnvironmentAsync();
                }
                if (_tabs.Tabs.Count == 0)
                {
                    await CreateTabAsync("amare://newtab", "New Tab", false, activate: true);
                }
            }
            catch (Exception recovery)
            {
                StatusText.Text = $"Profile recovery failed: {recovery.Message}";
            }
        }
    }

    private async void Downloads_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        if (_downloads.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "No downloads in this session", IsEnabled = false });
        }
        else
        {
            foreach (var download in _downloads.AsEnumerable().Reverse())
            {
                var item = new MenuFlyoutItem { Text = download.FileName, Tag = download.Path };
                item.Click += async (_, _) =>
                {
                    try
                    {
                        await Launcher.LaunchFileAsync(await global::Windows.Storage.StorageFile.GetFileFromPathAsync(download.Path));
                    }
                    catch (Exception exception)
                    {
                        StatusText.Text = $"Could not open download: {exception.Message}";
                    }
                };
                menu.Items.Add(item);
            }
        }
        menu.ShowAt(DownloadsButton);
        await Task.CompletedTask;
    }

    private async Task ConfirmExternalAsync(string uri)
    {
        var dialog = new ContentDialog
        {
            Title = "Open external application?",
            Content = $"The page wants to open:\n{uri}",
            PrimaryButtonText = "Open",
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
            if (!await Launcher.LaunchUriAsync(new Uri(uri)))
            {
                StatusText.Text = "No application handled that link.";
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not open external link: {exception.Message}";
        }
    }

    private void ScheduleSessionSave()
    {
        if (_saveTimer is null)
        {
            return;
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveSessionAsync(bool waitForInFlight = false)
    {
        if (_context is null || _sessions is null || _tabs.Tabs.Count == 0)
        {
            return;
        }
        var acquired = waitForInFlight
            ? await _sessionSaveGate.WaitAsync(TimeSpan.FromSeconds(8))
            : await _sessionSaveGate.WaitAsync(0);
        if (!acquired)
        {
            if (!waitForInFlight)
            {
                ScheduleSessionSave();
            }
            return;
        }
        try
        {
            var snapshot = _tabs.Snapshot();
            var fingerprint = SessionFingerprint(_context, snapshot);
            if (fingerprint == _lastSessionFingerprint)
            {
                return;
            }
            var tabs = snapshot.Tabs.Select(tab => new SessionTabInput
            {
                Url = tab.Url,
                Title = tab.Title,
                Pinned = tab.Pinned
            }).ToArray();
            Exception? lastError = null;
            for (var attempt = 1; attempt <= SessionSaveRetryPolicy.DefaultMaxAttempts; attempt++)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var state = await _sessions.SaveAsync(
                        _context,
                        _sessionId,
                        tabs,
                        snapshot.ActiveIndex < 0 ? null : snapshot.ActiveIndex,
                        cancellationToken: timeout.Token);
                    _sessionId = state.Session.Id;
                    _lastSessionFingerprint = fingerprint;
                    return;
                }
                catch (Exception exception) when (SessionSaveRetryPolicy.ShouldRetry(attempt, exception))
                {
                    lastError = exception;
                    await Task.Delay(SessionSaveRetryPolicy.DelayFor(attempt));
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    break;
                }
            }
            AppLog.Error("Session save failed", lastError ?? new InvalidOperationException("unknown error"));
            StatusText.Text = $"Session save failed: {lastError?.Message ?? "unknown error"}";
        }
        finally
        {
            _sessionSaveGate.Release();
        }
    }

    private static string SessionFingerprint(ProfileContextDto context, TabSessionSnapshot snapshot) =>
        JsonSerializer.Serialize(new
        {
            profile = context.ProfileId,
            generation = context.Generation,
            active = snapshot.ActiveIndex,
            tabs = snapshot.Tabs
        });

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _saveTimer?.Stop();
        try
        {
            // The process may exit immediately after this event returns. Capture
            // UI state on the UI thread, then block on an off-UI native save.
            SaveSessionForShutdown();
        }
        catch
        {
            // Best-effort close; the debounced autosave normally already ran.
        }
        CloseAllTabs();
        _native?.Dispose();
        _native = null;
    }

    private void SaveSessionForShutdown()
    {
        if (_context is null || _sessions is null || _tabs.Tabs.Count == 0)
        {
            return;
        }
        var context = _context;
        var sessionId = _sessionId;
        var snapshot = _tabs.Snapshot();
        var fingerprint = SessionFingerprint(context, snapshot);
        if (fingerprint == _lastSessionFingerprint)
        {
            return;
        }
        var tabs = snapshot.Tabs.Select(tab => new SessionTabInput
        {
            Url = tab.Url,
            Title = tab.Title,
            Pinned = tab.Pinned
        }).ToArray();
        var active = snapshot.ActiveIndex < 0 ? (int?)null : snapshot.ActiveIndex;
        var client = _sessions;
        // Do not block on the UI-owned save gate here: an in-flight async save
        // may need the UI context to release it. Native calls are serialized by
        // AmareNative, and this snapshot is newer than any queued debounce save.
        var save = Task.Run(async () => await client.SaveAsync(
            context, sessionId, tabs, active, cancellationToken: CancellationToken.None));
        if (!save.Wait(TimeSpan.FromSeconds(8)))
        {
            return;
        }
        _sessionId = save.Result.Session.Id;
        _lastSessionFingerprint = fingerprint;
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "Close",
                XamlRoot = RootGrid.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch
        {
            // The window may be closing.
        }
    }
}
