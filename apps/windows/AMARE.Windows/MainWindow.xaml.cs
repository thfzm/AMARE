using System.Text;
using System.Text.Json;
using Amare.Windows.Core;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Dispatching;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace Amare.Windows;

public sealed partial class MainWindow : Window
{
    private readonly TabManager _tabs = new();
    private readonly Dictionary<Guid, BrowserTab> _hosts = [];
    private readonly List<DownloadRecord> _downloads = [];
    private readonly List<BookmarkDto> _bookmarkCache = [];
    private readonly List<HistoryEntryDto> _historyCache = [];
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
    private BookmarkClient? _bookmarks;
    private ProfileContextDto? _context;
    private CoreWebView2Environment? _environment;
    private CoreWebView2Environment? _aiEnvironment;
    private ReaderExtractedPage? _readerPage;
    private BrowserTab? _findHost;
    private ReaderDisplaySettings _readerSettings = new("serif", "system", 18, 1.6, 720);
    private bool _settingReader;
    private bool _aiOpen;
    private bool _fullScreen;
    private double _zoomFactor = 1.0;
    private string _readerNonce = Guid.NewGuid().ToString("N");
    private DispatcherQueueTimer? _saveTimer;
    private string _profileId = "";
    private string _engineId = SearchEngineCatalog.DefaultId;
    private string _themeId = "system";
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

    private sealed class DownloadRecord
    {
        public required string FileName { get; init; }
        public required string Path { get; init; }
        public string State { get; set; } = "In progress";
        public long ReceivedBytes { get; set; }
        public long TotalBytes { get; set; } = -1;
        public string? Error { get; set; }
    }

    private sealed record ReaderOption(string Id, string Name);

    private sealed record AiServiceOption(string Id, string Name, string Url);

    public MainWindow()
    {
        InitializeComponent();
        ApplyWindowIcon();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AMARE");
        _profileCleanup = new ProfileDataCleanup(_appDataRoot);
        AppLog.Initialize(_appDataRoot);
        AppLog.Info("MainWindow created");
        TabList.ItemsSource = _tabs.Tabs;
        SearchEngineBox.ItemsSource = SearchEngineCatalog.All;
        AiServiceBox.ItemsSource = new[]
        {
            new AiServiceOption("chatgpt", "ChatGPT", "https://chatgpt.com/"),
            new AiServiceOption("gemini", "Gemini", "https://gemini.google.com/app"),
            new AiServiceOption("claude", "Claude", "https://claude.ai/new")
        };
        AiServiceBox.DisplayMemberPath = "Name";
        ReaderFontBox.ItemsSource = new[]
        {
            new ReaderOption("serif", "Serif"),
            new ReaderOption("sans", "Sans"),
            new ReaderOption("mono", "Mono")
        };
        ReaderThemeBox.ItemsSource = new[]
        {
            new ReaderOption("system", "System"),
            new ReaderOption("light", "Light"),
            new ReaderOption("dark", "Dark"),
            new ReaderOption("sepia", "Sepia")
        };
        ReaderFontBox.DisplayMemberPath = "Name";
        ReaderThemeBox.DisplayMemberPath = "Name";
        ReaderFontBox.SelectedIndex = 0;
        ReaderThemeBox.SelectedIndex = 0;
        AiServiceBox.SelectedIndex = 0;
        LoadDownloads();
        RegisterKeyboardAccelerators();
        Closed += MainWindow_Closed;
    }

    private void ApplyWindowIcon()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AMARE.ico");
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
            }
        }
        catch
        {
            // The default executable icon remains if the AppWindow icon cannot be set.
        }
    }

    private void RegisterKeyboardAccelerators()
    {
        AddAccelerator(VirtualKey.L, VirtualKeyModifiers.Control, BrowserCommand.FocusAddressBar);
        AddAccelerator(VirtualKey.T, VirtualKeyModifiers.Control, BrowserCommand.NewTab);
        AddAccelerator(VirtualKey.W, VirtualKeyModifiers.Control, BrowserCommand.CloseTab);
        AddAccelerator(VirtualKey.T, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, BrowserCommand.RestoreClosedTab);
        AddAccelerator(VirtualKey.R, VirtualKeyModifiers.Control, BrowserCommand.Reload);
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control, BrowserCommand.Find);
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
                case BrowserCommand.Find:
                    OpenFind();
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
        _bookmarks = new BookmarkClient(_native);

        _context = await _profiles.InitializeAsync();
        _profileId = (await _profiles.GetActiveAsync())?.Id ?? _context.ProfileId;
        _saveTimer = _dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(500);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += async (_, _) => await SaveSessionAsync();

        await LoadSettingsAsync();
        await RefreshBookmarkCacheAsync();
        await RefreshHistoryCacheAsync();
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
            _themeId = theme.Value.AsText().ToLowerInvariant();
            ApplyTheme(_themeId);
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
        _themeId = theme.ToLowerInvariant();
        RootGrid.RequestedTheme = theme.ToLowerInvariant() switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private async Task RefreshBookmarkCacheAsync()
    {
        if (_context is null || _bookmarks is null) return;
        try
        {
            var page = await _bookmarks.ListAsync(_context, null, 500);
            _bookmarkCache.Clear();
            _bookmarkCache.AddRange(page.Items);
            UpdateBookmarkButton();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Bookmarks unavailable: {exception.Message}";
        }
    }

    private async Task RefreshHistoryCacheAsync()
    {
        if (_context is null || _history is null) return;
        try
        {
            var page = await _history.ListAsync(_context, null, 100);
            _historyCache.Clear();
            _historyCache.AddRange(page.Items);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"History unavailable: {exception.Message}";
        }
    }

    private bool IsCurrentBookmarked() =>
        ActiveHost is { } host
        && !host.Model.IsNewTabPage
        && _bookmarkCache.Any(bookmark => string.Equals(bookmark.Url, host.Model.Url, StringComparison.OrdinalIgnoreCase));

    private void UpdateBookmarkButton()
    {
        BookmarkIcon.Glyph = IsCurrentBookmarked() ? "\uE735" : "\uE734";
        ToolTipService.SetToolTip(BookmarksButton, IsCurrentBookmarked() ? "Remove bookmark" : "Bookmark this page");
    }

    private async Task ToggleBookmarkAsync()
    {
        var active = ActiveHost;
        if (active is null || active.Model.IsNewTabPage || string.IsNullOrWhiteSpace(active.Model.Url))
        {
            return;
        }
        if (_context is null || _bookmarks is null)
        {
            return;
        }
        try
        {
            var existing = _bookmarkCache.FirstOrDefault(bookmark =>
                string.Equals(bookmark.Url, active.Model.Url, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                await _bookmarks.CreateAsync(_context, active.Model.Title, active.Model.Url);
                StatusText.Text = "Bookmark added";
            }
            else
            {
                await _bookmarks.DeleteAsync(_context, existing.Id);
                StatusText.Text = "Bookmark removed";
            }
            await RefreshBookmarkCacheAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Bookmark update failed: {exception.Message}";
        }
    }

    private async void Bookmarks_Click(object sender, RoutedEventArgs e)
    {
        await RefreshBookmarkCacheAsync();
        var menu = new MenuFlyout();
        if (_bookmarkCache.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "No bookmarks", IsEnabled = false });
        }
        else
        {
            foreach (var bookmark in _bookmarkCache)
            {
                var item = new MenuFlyoutItem
                {
                    Text = $"{bookmark.Title} — {bookmark.Url}",
                    Tag = bookmark.Url
                };
                item.Click += (_, _) =>
                {
                    if (ActiveHost is { } active) Navigate(active, bookmark.Url);
                };
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var toggle = new MenuFlyoutItem
        {
            Text = IsCurrentBookmarked() ? "Remove current page bookmark" : "Bookmark current page"
        };
        toggle.Click += async (_, _) => await ToggleBookmarkAsync();
        menu.Items.Add(toggle);
        menu.ShowAt(BookmarksButton);
    }

    private async void ShowHistoryAsync()
    {
        if (_context is null || _history is null)
        {
            return;
        }
        try
        {
            var page = await _history.ListAsync(_context, null, 200);
            var list = new ListView
            {
                ItemsSource = page.Items,
                DisplayMemberPath = "Title",
                IsItemClickEnabled = true,
                SelectionMode = ListViewSelectionMode.None,
                MaxHeight = 440,
                MinWidth = 520
            };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = $"{page.Total} visited pages" });
            panel.Children.Add(list);
            var dialog = new ContentDialog
            {
                Title = "History",
                Content = panel,
                PrimaryButtonText = "Clear history",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            };
            list.ItemClick += (_, args) =>
            {
                if (args.ClickedItem is HistoryEntryDto entry && ActiveHost is { } active)
                {
                    dialog.Hide();
                    Navigate(active, entry.Url);
                }
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                await _history.ClearAsync(_context);
                _historyCache.Clear();
                StatusText.Text = "History cleared";
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"History unavailable: {exception.Message}";
        }
    }

    private async Task RecordHistoryAsync(BrowserTab host)
    {
        if (_context is null || _history is null || host.Model.IsNewTabPage)
        {
            return;
        }
        var url = host.Model.Url;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            await _history.RecordAsync(_context, url, host.Model.Title);
            _historyCache.RemoveAll(entry => string.Equals(entry.Url, url, StringComparison.OrdinalIgnoreCase));
            _historyCache.Insert(0, new HistoryEntryDto
            {
                Url = url,
                Title = host.Model.Title,
                LastVisitedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            if (_historyCache.Count > 100) _historyCache.RemoveRange(100, _historyCache.Count - 100);
        }
        catch (Exception exception)
        {
            AppLog.Error("History record failed", exception);
        }
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
        core.Find.MatchCountChanged += (_, _) => UpdateFindCount(host);
        core.Find.ActiveMatchIndexChanged += (_, _) => UpdateFindCount(host);
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
            if (IsActive(host))
            {
                CloseFind();
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
        core.NavigationCompleted += (webview, args) =>
        {
            host.ShowingNewTab = false;
            host.Model.IsLoading = false;
            UpdateNavigationState(host);
            if (!args.IsSuccess && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                StatusText.Text = $"Page failed to load: {args.WebErrorStatus}";
            }
            if (args.IsSuccess)
            {
                _ = RecordHistoryAsync(host);
            }
            if (IsActive(host))
            {
                UpdateToolbar();
                UpdateAddressIfActive(host);
                UpdateBookmarkButton();
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
                var record = new DownloadRecord
                {
                    FileName = suggested,
                    Path = path,
                    TotalBytes = args.DownloadOperation.TotalBytesToReceive
                };
                _downloads.Add(record);
                SaveDownloads();
                var operation = args.DownloadOperation;
                operation.StateChanged += (_, _) =>
                {
                    record.State = operation.State.ToString();
                    record.ReceivedBytes = operation.BytesReceived;
                    record.TotalBytes = operation.TotalBytesToReceive;
                    if (operation.State == CoreWebView2DownloadState.Completed)
                    {
                        StatusText.Text = $"Download complete: {record.FileName}";
                    }
                    else if (operation.State == CoreWebView2DownloadState.Interrupted)
                    {
                        record.Error = operation.InterruptReason.ToString();
                        StatusText.Text = $"Download failed: {record.FileName}";
                    }
                    SaveDownloads();
                };
                operation.BytesReceivedChanged += (_, _) =>
                {
                    record.ReceivedBytes = operation.BytesReceived;
                    record.TotalBytes = operation.TotalBytesToReceive;
                };
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
            CloseFind();
            CloseReader();
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

    private void AddressBar_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }
        var openTabs = _tabs.Tabs
            .Select(tab => new AddressEntry(tab.Url, tab.Title))
            .ToArray();
        var bookmarks = _bookmarkCache
            .Select(bookmark => new AddressEntry(bookmark.Url, bookmark.Title))
            .ToArray();
        var history = _historyCache
            .Select(entry => new AddressEntry(entry.Url, entry.Title))
            .ToArray();
        var suggestions = AddressSuggestionProvider.Suggest(
            sender.Text, _engineId, openTabs, bookmarks, history);
        sender.ItemsSource = suggestions;
        sender.IsSuggestionListOpen = suggestions.Count > 0;
    }

    private void AddressBar_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is AddressSuggestion suggestion)
        {
            sender.Text = suggestion.Url;
        }
    }

    private async void AddressBar_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var active = ActiveHost;
        if (active is null)
        {
            return;
        }
        var query = args.ChosenSuggestion is AddressSuggestion suggestion
            ? suggestion.Url
            : args.QueryText;
        var resolution = AddressBarResolver.Resolve(query, _engineId);
        switch (resolution.Kind)
        {
            case AddressInputKind.Url when resolution.Target.Equals("amare://newtab", StringComparison.OrdinalIgnoreCase):
                await ShowNewTabPageAsync(active);
                AddressBar.Text = "";
                break;
            case AddressInputKind.Url:
            case AddressInputKind.Search:
                sender.IsSuggestionListOpen = false;
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

    private void OpenFind()
    {
        var active = ActiveHost;
        if (active is null)
        {
            return;
        }
        _findHost = active;
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
        UpdateFindCount(active);
    }

    private async void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseFind();
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await StartFindAsync();
        }
    }

    private async Task StartFindAsync()
    {
        var host = _findHost;
        if (host?.View.CoreWebView2 is null || string.IsNullOrWhiteSpace(FindBox.Text))
        {
            return;
        }
        try
        {
            var options = _environment?.CreateFindOptions();
            if (options is null)
            {
                return;
            }
            options.FindTerm = FindBox.Text;
            options.ShouldHighlightAllMatches = true;
            options.SuppressDefaultFindDialog = true;
            await host.View.CoreWebView2.Find.StartAsync(options);
            UpdateFindCount(host);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Find failed: {exception.Message}";
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        if (_findHost?.View.CoreWebView2 is { } core)
        {
            core.Find.FindNext();
            UpdateFindCount(_findHost);
        }
    }

    private void FindPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (_findHost?.View.CoreWebView2 is { } core)
        {
            core.Find.FindPrevious();
            UpdateFindCount(_findHost);
        }
    }

    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();

    private void CloseFind()
    {
        try
        {
            _findHost?.View.CoreWebView2?.Find.Stop();
        }
        catch
        {
            // The tab may already be closed.
        }
        _findHost = null;
        FindBar.Visibility = Visibility.Collapsed;
        FindCount.Text = "0/0";
    }

    private void UpdateFindCount(BrowserTab host)
    {
        if (host != ActiveHost || FindBar.Visibility != Visibility.Visible)
        {
            return;
        }
        try
        {
            var find = host.View.CoreWebView2?.Find;
            if (find is null)
            {
                return;
            }
            var count = find.MatchCount;
            var active = find.ActiveMatchIndex;
            FindCount.Text = count <= 0 ? "No results" : $"{Math.Max(1, active)}/{count}";
        }
        catch
        {
            FindCount.Text = "No results";
        }
    }

    private async Task OpenReaderAsync()
    {
        var host = ActiveHost;
        if (host?.View.CoreWebView2 is null || host.Model.IsNewTabPage)
        {
            StatusText.Text = "Reader Mode is not available on this page.";
            return;
        }
        CloseFind();
        CloseAiPanel();
        try
        {
            var raw = await host.View.CoreWebView2.ExecuteScriptAsync(ReaderExtractionScript.Script);
            var json = JsonSerializer.Deserialize<string>(raw);
            var page = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<ReaderExtractedPage>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (page is null || page.Blocks.Count == 0)
            {
                StatusText.Text = "Reader Mode could not extract readable content.";
                return;
            }
            _readerPage = page;
            _readerNonce = Guid.NewGuid().ToString("N");
            ReaderTitleText.Text = string.IsNullOrWhiteSpace(page.Title) ? "Reader" : page.Title;
            await EnsureReaderAsync();
            RenderReader();
            ReaderHost.Visibility = Visibility.Visible;
            StatusText.Text = $"Reader Mode · {page.WordCount} words";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Reader Mode failed: {exception.Message}";
        }
    }

    private async Task EnsureReaderAsync()
    {
        if (ReaderWebView.CoreWebView2 is not null || _environment is null)
        {
            return;
        }
        await ReaderWebView.EnsureCoreWebView2Async(_environment);
        if (ReaderWebView.CoreWebView2 is { } readerCore)
        {
            readerCore.NavigationStarting += (_, args) =>
            {
                if (args.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    args.Cancel = true;
                }
            };
        }
    }

    private void RenderReader()
    {
        if (_readerPage is null)
        {
            return;
        }
        _settingReader = true;
        try
        {
            var font = ReaderFontBox.SelectedItem is ReaderOption fontOption ? fontOption.Id : "serif";
            var theme = ReaderThemeBox.SelectedItem is ReaderOption themeOption ? themeOption.Id : "system";
            _readerSettings = _readerSettings with { Font = font, Theme = theme };
            var html = ReaderPageBuilder.Build(_readerPage, _readerSettings, _readerNonce);
            ReaderWebView.NavigateToString(html);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Reader render failed: {exception.Message}";
        }
        finally
        {
            _settingReader = false;
        }
    }

    private void CloseReader()
    {
        if (ReaderHost.Visibility != Visibility.Visible)
        {
            return;
        }
        ReaderHost.Visibility = Visibility.Collapsed;
        try { ReaderWebView.CoreWebView2?.Stop(); } catch { }
        _readerPage = null;
    }

    private void ReaderClose_Click(object sender, RoutedEventArgs e) => CloseReader();

    private void ReaderSmaller_Click(object sender, RoutedEventArgs e)
    {
        _readerSettings = _readerSettings with { FontSize = Math.Max(12, _readerSettings.FontSize - 1) };
        RenderReader();
    }

    private void ReaderLarger_Click(object sender, RoutedEventArgs e)
    {
        _readerSettings = _readerSettings with { FontSize = Math.Min(32, _readerSettings.FontSize + 1) };
        RenderReader();
    }

    private void ReaderFontBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingReader && _readerPage is not null) RenderReader();
    }

    private void ReaderThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingReader && _readerPage is not null) RenderReader();
    }

    private async void Ai_Click(object sender, RoutedEventArgs e)
    {
        if (_aiOpen)
        {
            CloseAiPanel();
            return;
        }
        CloseFind();
        CloseReader();
        try
        {
            AiColumn.Width = new GridLength(380);
            AiPanelHost.Visibility = Visibility.Visible;
            _aiOpen = true;
            await EnsureAiAsync();
            if (AiServiceBox.SelectedItem is AiServiceOption service && AiWebView.CoreWebView2 is { } core)
            {
                core.Navigate(service.Url);
            }
            StatusText.Text = "AI web panel opened";
        }
        catch (Exception exception)
        {
            CloseAiPanel();
            StatusText.Text = $"AI panel failed: {exception.Message}";
        }
    }

    private async Task EnsureAiAsync()
    {
        if (_aiEnvironment is null)
        {
            var folder = Path.Combine(_appDataRoot, "ai-webview2");
            Directory.CreateDirectory(folder);
            _aiEnvironment = await CoreWebView2Environment.CreateWithOptionsAsync(
                null, folder, new CoreWebView2EnvironmentOptions());
        }
        if (AiWebView.CoreWebView2 is null)
        {
            await AiWebView.EnsureCoreWebView2Async(_aiEnvironment);
            if (AiWebView.CoreWebView2 is { } core)
            {
                core.NewWindowRequested += (_, args) =>
                {
                    args.Handled = true;
                    if (NavigationPolicy.CanOpenInNewTab(args.Uri))
                    {
                        core.Navigate(args.Uri);
                    }
                };
                core.NavigationCompleted += (_, args) =>
                {
                    if (!args.IsSuccess)
                    {
                        AiStatusText.Text = $"AI page failed: {args.WebErrorStatus}";
                    }
                };
            }
        }
    }

    private void AiClose_Click(object sender, RoutedEventArgs e) => CloseAiPanel();

    private void CloseAiPanel()
    {
        if (!_aiOpen && AiPanelHost.Visibility == Visibility.Collapsed)
        {
            return;
        }
        _aiOpen = false;
        AiPanelHost.Visibility = Visibility.Collapsed;
        AiColumn.Width = new GridLength(0);
        try { AiWebView.CoreWebView2?.Stop(); } catch { }
    }

    private void AiServiceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_aiOpen && AiServiceBox.SelectedItem is AiServiceOption service && AiWebView.CoreWebView2 is { } core)
        {
            core.Navigate(service.Url);
        }
    }

    private async void AiCopyInfo_Click(object sender, RoutedEventArgs e)
    {
        await CopyPageContextAsync(includeArticle: false);
    }

    private async void AiCopyArticle_Click(object sender, RoutedEventArgs e)
    {
        await CopyPageContextAsync(includeArticle: true);
    }

    private async Task CopyPageContextAsync(bool includeArticle)
    {
        var host = ActiveHost;
        if (host?.View.CoreWebView2 is null)
        {
            AiStatusText.Text = "No active page to copy.";
            return;
        }
        try
        {
            var core = host.View.CoreWebView2;
            var title = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.title")) ?? host.Model.Title;
            var selection = JsonSerializer.Deserialize<string>(
                await core.ExecuteScriptAsync("window.getSelection ? window.getSelection().toString() : ''")) ?? "";
            var builder = new StringBuilder();
            builder.AppendLine("AMARE page context");
            builder.AppendLine($"Title: {title}");
            builder.AppendLine($"URL: {host.Model.Url}");
            if (!string.IsNullOrWhiteSpace(selection))
            {
                builder.AppendLine();
                builder.AppendLine("Selected text:");
                builder.AppendLine(selection.Trim());
            }
            if (includeArticle)
            {
                var raw = await core.ExecuteScriptAsync(ReaderExtractionScript.Script);
                var json = JsonSerializer.Deserialize<string>(raw);
                var page = string.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonSerializer.Deserialize<ReaderExtractedPage>(
                        json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (page is not null)
                {
                    var text = string.Join("\n\n", page.Blocks
                        .Where(block => block.Tag != "img")
                        .Select(block => block.Text)
                        .Where(text => !string.IsNullOrWhiteSpace(text)));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        builder.AppendLine();
                        builder.AppendLine("Article text (user-approved):");
                        builder.AppendLine(text[..Math.Min(20000, text.Length)]);
                    }
                }
            }
            var data = new DataPackage();
            data.SetText(builder.ToString());
            Clipboard.SetContent(data);
            AiStatusText.Text = includeArticle
                ? "Page information and extracted article copied. Paste into the AI panel when ready."
                : "Page information copied. Paste into the AI panel when ready.";
        }
        catch (Exception exception)
        {
            AiStatusText.Text = $"Context copy failed: {exception.Message}";
        }
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        AddMenu(menu, "New tab", "Ctrl+T", () => _ = CreateTabAsync("amare://newtab", "New Tab", false, true));
        AddMenu(menu, "New window", "", () => new MainWindow().Activate());
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenu(menu, "History", "", () => ShowHistoryAsync());
        AddMenu(menu, "Bookmarks", "", () => Bookmarks_Click(BookmarksButton, new RoutedEventArgs()));
        AddMenu(menu, "Downloads", "", () => Downloads_Click(DownloadsButton, new RoutedEventArgs()));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenu(menu, "Reader Mode", "", () => _ = OpenReaderAsync());
        AddMenu(menu, "Find in page", "Ctrl+F", OpenFind);
        AddMenu(menu, _aiOpen ? "Close AI panel" : "AI web panel", "", () => Ai_Click(AiButton, new RoutedEventArgs()));
        AddMenu(menu, "Zoom in", "Ctrl++", () => ChangeZoom(0.1));
        AddMenu(menu, "Zoom out", "Ctrl+-", () => ChangeZoom(-0.1));
        AddMenu(menu, "Reset zoom", "Ctrl+0", () => SetZoom(1.0));
        AddMenu(menu, "Print", "Ctrl+P", () => _ = PrintAsync());
        AddMenu(menu, _fullScreen ? "Exit full screen" : "Full screen", "F11", ToggleFullScreen);
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenu(menu, "Settings", "", () => _ = ShowSettingsAsync());
        menu.ShowAt(MenuButton);
    }

    private static void AddMenu(MenuFlyout menu, string text, string shortcut, Action action)
    {
        var item = new MenuFlyoutItem
        {
            Text = shortcut.Length == 0 ? text : $"{text}\t{shortcut}"
        };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void ChangeZoom(double delta) => _ = ChangeZoomAsync(delta);

    private void SetZoom(double value) => _ = SetZoomAsync(value);

    private async Task ChangeZoomAsync(double delta) =>
        await SetZoomAsync(Math.Clamp(_zoomFactor + delta, 0.25, 3.0));

    private async Task SetZoomAsync(double value)
    {
        _zoomFactor = Math.Clamp(value, 0.25, 3.0);
        if (ActiveHost?.View.CoreWebView2 is { } core)
        {
            var script = $"document.documentElement.style.zoom = '{_zoomFactor.ToString(System.Globalization.CultureInfo.InvariantCulture)}';";
            await core.ExecuteScriptAsync(script);
            StatusText.Text = $"Zoom: {_zoomFactor:P0}";
        }
    }

    private async Task PrintAsync()
    {
        if (ActiveHost?.View.CoreWebView2 is { } core)
        {
            await core.ExecuteScriptAsync("window.print()");
        }
    }

    private void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        AppWindow.SetPresenter(_fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
    }

    private async Task ShowSettingsAsync()
    {
        if (_context is null || _settings is null)
        {
            return;
        }
        var themeBox = new ComboBox { ItemsSource = new[] { "system", "light", "dark" }, SelectedItem = _themeId, MinWidth = 220 };
        var engineBox = new ComboBox
        {
            ItemsSource = SearchEngineCatalog.All,
            DisplayMemberPath = "Name",
            SelectedItem = SearchEngineCatalog.Resolve(_engineId),
            MinWidth = 220
        };
        var restoreBox = new CheckBox { Content = "Restore previous tabs on startup", IsChecked = _restoreSession };
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "Appearance theme" });
        stack.Children.Add(themeBox);
        stack.Children.Add(new TextBlock { Text = "Search engine" });
        stack.Children.Add(engineBox);
        stack.Children.Add(restoreBox);
        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = stack,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        var theme = themeBox.SelectedItem as string ?? "system";
        var engine = engineBox.SelectedItem as SearchEngineDescriptor ?? SearchEngineCatalog.Resolve(_engineId);
        var restore = restoreBox.IsChecked == true;
        try
        {
            await _settings.SetManyAsync(_context, new[]
            {
                ("theme", SettingValueDto.Text(theme)),
                ("engine", SettingValueDto.Text(engine.Id)),
                ("restore_session", SettingValueDto.Boolean(restore))
            });
            _engineId = engine.Id;
            _restoreSession = restore;
            _themeId = theme;
            ApplyTheme(theme);
            _initializingSettings = true;
            SearchEngineBox.SelectedItem = engine;
            _initializingSettings = false;
            StatusText.Text = "Settings saved";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Settings save failed: {exception.Message}";
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

    private string DownloadsStatePath => Path.Combine(_appDataRoot, "downloads.json");

    private void LoadDownloads()
    {
        try
        {
            if (!File.Exists(DownloadsStatePath)) return;
            var items = JsonSerializer.Deserialize<List<DownloadRecord>>(File.ReadAllText(DownloadsStatePath));
            if (items is not null) _downloads.AddRange(items);
        }
        catch (Exception exception)
        {
            AppLog.Error("Download history load failed", exception);
        }
    }

    private void SaveDownloads()
    {
        try
        {
            Directory.CreateDirectory(_appDataRoot);
            File.WriteAllText(DownloadsStatePath, JsonSerializer.Serialize(_downloads));
        }
        catch (Exception exception)
        {
            AppLog.Error("Download history save failed", exception);
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
                var size = download.TotalBytes > 0
                    ? $"{download.ReceivedBytes}/{download.TotalBytes} bytes"
                    : $"{download.ReceivedBytes} bytes";
                var item = new MenuFlyoutItem
                {
                    Text = $"{download.FileName} — {download.State} ({size})",
                    Tag = download.Path,
                    IsEnabled = download.State == "Completed" && File.Exists(download.Path)
                };
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
