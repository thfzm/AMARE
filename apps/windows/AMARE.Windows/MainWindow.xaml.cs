using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
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
using Microsoft.UI.Xaml.Automation;
using Microsoft.Web.WebView2.Core;
using Windows.System;
using WinRT.Interop;

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
    private readonly GitHubReleaseClient _releaseClient = new();
    private readonly UpdateDownloader _updateDownloader = new();
    private static readonly object WindowRegistryGate = new();
    private static readonly List<WeakReference<MainWindow>> WindowRegistry = [];
    private readonly Guid _windowId = Guid.NewGuid();

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
    private bool _updateCheckRunning;
    private UpdatePreferences _updatePreferences = new(true, UpdateChannel.Stable, 0);
    private AiApiSettings _aiApiSettings = new();
    private string _readerNonce = Guid.NewGuid().ToString("N");
    private DispatcherQueueTimer? _saveTimer;
    private string _profileId = "";
    private string _engineId = SearchEngineCatalog.DefaultId;
    private string _themeId = "system";
    private bool _restoreSession = true;
    private bool _initializingSettings;
    private bool _started;
    private TabTransferSnapshot? _initialTab;
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
    private sealed record UpdatePreferences(bool CheckOnStartup, UpdateChannel Channel, long LastCheckUnixMs);

    public MainWindow()
    {
        InitializeComponent();
        ApplyWindowIcon();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _appDataRoot = AppPaths.LocalRoot();
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
        RegisterWindow(this);
        Closed += MainWindow_Closed;
    }

    private static void RegisterWindow(MainWindow window)
    {
        lock (WindowRegistryGate)
        {
            WindowRegistry.RemoveAll(reference => !reference.TryGetTarget(out _));
            WindowRegistry.Add(new WeakReference<MainWindow>(window));
        }
    }

    private static MainWindow? FindWindow(Guid windowId)
    {
        lock (WindowRegistryGate)
        {
            foreach (var reference in WindowRegistry)
            {
                if (reference.TryGetTarget(out var window) && window._windowId == windowId)
                {
                    return window;
                }
            }
        }
        return null;
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
        AddAccelerator(VirtualKey.Tab, VirtualKeyModifiers.Control, BrowserCommand.NextTab);
        AddAccelerator(VirtualKey.Tab, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, BrowserCommand.PreviousTab);
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
                case BrowserCommand.NextTab:
                    ActivateRelativeTab(1);
                    break;
                case BrowserCommand.PreviousTab:
                    ActivateRelativeTab(-1);
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

    private void ActivateRelativeTab(int delta)
    {
        if (_tabs.Tabs.Count < 2 || _tabs.ActiveTab is null) return;
        var index = _tabs.Tabs.IndexOf(_tabs.ActiveTab);
        if (index < 0) return;
        var next = (index + delta + _tabs.Tabs.Count) % _tabs.Tabs.Count;
        SelectTab(_tabs.Tabs[next]);
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

    public void StartWithTab(string url, string title, bool pinned, double zoomFactor)
    {
        _initialTab = new TabTransferSnapshot(url, title, pinned, zoomFactor);
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
        LoadUpdatePreferences();
        LoadAiApiSettings();
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
        if (_initialTab is { } initialTab)
        {
            _initialTab = null;
            await AddTransferredTabAsync(initialTab);
        }
        else
        {
            await RestoreSessionOrNewTabAsync();
        }
        _ = ResumeProfileCleanupAsync();
        AppLog.Info($"AMARE initialized profile={_profileId} engine={_engineId} restore_session={_restoreSession}");
        PortableUpdateRecovery.MarkHealthyIfRequested(
            _appDataRoot,
            CurrentAppVersion,
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var recoveryMessage = PortableUpdateRecovery.RecoveryMessage(_appDataRoot);
        StatusText.Text = recoveryMessage == null
            ? "Ready"
            : recoveryMessage + " Use the browser menu → Recover interrupted update.";
        if (_updatePreferences.CheckOnStartup)
        {
            _ = CheckForUpdatesAsync(false);
        }
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
            var entries = page.Items.ToArray();
            var list = new ListView
            {
                ItemsSource = entries,
                DisplayMemberPath = "Title",
                IsItemClickEnabled = true,
                SelectionMode = ListViewSelectionMode.None,
                MaxHeight = 440,
                MinWidth = 520
            };
            var historySearch = new TextBox
            {
                PlaceholderText = "Search history"
            };
            AutomationProperties.SetAutomationId(historySearch, "HistorySearchBox");
            historySearch.TextChanged += (_, _) =>
            {
                var query = historySearch.Text.Trim();
                list.ItemsSource = string.IsNullOrWhiteSpace(query)
                    ? entries
                    : entries.Where(entry =>
                        entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || entry.Url.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = $"{page.Total} visited pages" });
            panel.Children.Add(historySearch);
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
        core.ServerCertificateErrorDetected += (sender, args) =>
        {
            // Never allow a certificate error automatically. Cancel the navigation
            // and show the user an explicit, non-bypass error notice.
            args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            var message = DescribeCertificateError(args.RequestUri, args.ErrorStatus, args.ServerCertificate);
            StatusText.Text = message;
            _ = ShowCertificateErrorAsync(args.RequestUri, message);
        };
        core.NavigationCompleted += (webview, args) =>
        {
            host.ShowingNewTab = false;
            host.Model.IsLoading = false;
            UpdateNavigationState(host);
            if (!args.IsSuccess && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                StatusText.Text = DescribeWebError(args.WebErrorStatus);
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

    private void TabList_DragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        if (args.Items.FirstOrDefault() is not BrowserTabModel tab) return;
        args.Data.SetText($"amare-tab|{_windowId:N}|{tab.Id:N}");
        args.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void TabList_DragOver(object sender, DragEventArgs args)
    {
        if (args.DataView.Contains(StandardDataFormats.Text))
        {
            args.AcceptedOperation = DataPackageOperation.Move;
        }
    }

    private async void TabList_Drop(object sender, DragEventArgs args)
    {
        if (!args.DataView.Contains(StandardDataFormats.Text)) return;
        try
        {
            var text = await args.DataView.GetTextAsync();
            var parts = text.Split('|');
            if (parts.Length != 3
                || parts[0] != "amare-tab"
                || !Guid.TryParse(parts[1], out var sourceId)
                || !Guid.TryParse(parts[2], out var tabId)
                || sourceId == _windowId)
            {
                return;
            }
            var source = FindWindow(sourceId);
            if (source is null) return;
            var snapshot = source.GetTabSnapshot(tabId);
            if (snapshot is null) return;
            try
            {
                await AddTransferredTabAsync(snapshot);
                await source.CompleteTransferAsync(tabId);
                args.AcceptedOperation = DataPackageOperation.Move;
                args.Handled = true;
            }
            catch (Exception exception)
            {
                // Source remains intact until the target has accepted the tab.
                StatusText.Text = $"Tab move failed; source tab preserved: {exception.Message}";
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Tab move failed: {exception.Message}";
        }
    }

    private sealed record TabTransferSnapshot(string Url, string Title, bool Pinned, double ZoomFactor);

    private TabTransferSnapshot? GetTabSnapshot(Guid tabId)
    {
        if (!_hosts.TryGetValue(tabId, out var host)) return null;
        return new TabTransferSnapshot(
            host.Model.Url,
            host.Model.Title,
            host.Model.IsPinned,
            _zoomFactor);
    }

    private async Task CompleteTransferAsync(Guid tabId)
    {
        await CloseTabAsync(tabId);
    }

    private async Task AddTransferredTabAsync(TabTransferSnapshot snapshot)
    {
        await CreateTabAsync(snapshot.Url, snapshot.Title, snapshot.Pinned, activate: true);
        await SetZoomAsync(snapshot.ZoomFactor);
    }

    private async void TabList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult == DataPackageOperation.None
            && args.Items.FirstOrDefault() is BrowserTabModel dragged
            && IsPointerOutsideWindow()
            && GetTabSnapshot(dragged.Id) is { } snapshot)
        {
            try
            {
                var window = new MainWindow();
                window.StartWithTab(snapshot.Url, snapshot.Title, snapshot.Pinned, snapshot.ZoomFactor);
                window.Activate();
                await CompleteTransferAsync(dragged.Id);
                ScheduleSessionSave();
                return;
            }
            catch (Exception exception)
            {
                StatusText.Text = $"Tab tear-off failed; source tab preserved: {exception.Message}";
            }
        }
        var ordered = TabList.Items.OfType<BrowserTabModel>().ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            _tabs.Move(ordered[index].Id, index);
        }
        ScheduleSessionSave();
    }

    private bool IsPointerOutsideWindow()
    {
        if (!GetCursorPos(out var point)) return false;
        var handle = WindowNative.GetWindowHandle(this);
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return false;
        return point.X < rect.Left || point.X >= rect.Right || point.Y < rect.Top || point.Y >= rect.Bottom;
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

    private async Task ShowCertificateErrorAsync(string uri, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "보안 인증서 오류",
                Content = new TextBlock
                {
                    Text = $"{message}\n\n요청 주소: {uri}\n\n" +
                           "이 페이지로 자동 진행하지 않았습니다. 사이트 관리자에게 문의하거나, " +
                           "인증서가 올바른지 확인한 뒤 다시 시도하세요.",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "다시 시도",
                SecondaryButtonText = "뒤로",
                CloseButtonText = "닫기",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            };
            var result = await dialog.ShowAsync();
            var active = ActiveHost?.View.CoreWebView2;
            if (active is null) return;
            if (result == ContentDialogResult.Primary)
            {
                active.Navigate(uri);
            }
            else if (result == ContentDialogResult.Secondary && active.CanGoBack)
            {
                active.GoBack();
            }
        }
        catch
        {
            // The window may be closing; the navigation remains cancelled.
        }
    }

    private static string DescribeCertificateError(
        string uri, CoreWebView2WebErrorStatus status, CoreWebView2Certificate? serverCertificate)
    {
        try
        {
            var pem = serverCertificate?.PemEncodedIssuerCertificateChain?.FirstOrDefault();
            var certificate = string.IsNullOrWhiteSpace(pem) ? null : X509Certificate2.CreateFromPem(pem);
            if (certificate is not null)
            {
                if (certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow)
                {
                    return "인증서 유효기간이 만료되었습니다. 이 사이트의 인증서를 신뢰하지 마세요.";
                }
                var host = new Uri(uri).Host;
                var san = certificate.Extensions
                    .OfType<X509SubjectAlternativeNameExtension>()
                    .FirstOrDefault();
                var names = san is null
                    ? new List<string>()
                    : san.EnumerateDnsNames().Concat(san.EnumerateIPAddresses().Select(address => address.ToString())).ToList();
                if (names.Count == 0)
                {
                    var commonName = certificate.GetNameInfo(X509NameType.SimpleName, false);
                    if (!string.IsNullOrWhiteSpace(commonName)) names.Add(commonName);
                }
                if (names.Count > 0 && !names.Any(name => string.Equals(name, host, StringComparison.OrdinalIgnoreCase)))
                {
                    return "인증서의 호스트 이름이 요청 주소와 일치하지 않습니다. 이 사이트의 인증서를 신뢰하지 마세요.";
                }
            }
        }
        catch
        {
        }
        return DescribeWebError(status);
    }

    private static string DescribeWebError(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect
            or CoreWebView2WebErrorStatus.CertificateExpired
            or CoreWebView2WebErrorStatus.CertificateIsInvalid
            or CoreWebView2WebErrorStatus.CertificateRevoked
            or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors
            => "보안 인증서 오류로 페이지를 열 수 없습니다. 사이트 관리자에게 문의하고, 인증서 경고를 우회하지 마세요.",
        CoreWebView2WebErrorStatus.HostNameNotResolved
            => "호스트 이름을 찾을 수 없습니다. 주소와 네트워크 연결을 확인하세요.",
        CoreWebView2WebErrorStatus.Disconnected
            => "네트워크 연결이 끊어졌습니다. 연결을 확인한 뒤 다시 시도하세요.",
        CoreWebView2WebErrorStatus.Timeout
            => "페이지 응답 시간이 초과되었습니다. 잠시 후 다시 시도하세요.",
        CoreWebView2WebErrorStatus.CannotConnect
            => "서버에 연결할 수 없습니다. 주소와 방화벽을 확인하세요.",
        CoreWebView2WebErrorStatus.OperationCanceled
            => "페이지 로드가 취소되었습니다.",
        _ => $"페이지를 불러오지 못했습니다: {status}"
    };

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
        var query = args.ChosenSuggestion is AddressSuggestion suggestion
            ? suggestion.Url
            : args.QueryText;
        sender.IsSuggestionListOpen = false;
        await NavigateAddressAsync(query);
    }

    private async void GoButton_Click(object sender, RoutedEventArgs e)
    {
        await NavigateAddressAsync(AddressBar.Text);
    }

    private async Task NavigateAddressAsync(string input)
    {
        var active = ActiveHost;
        if (active is null)
        {
            return;
        }
        var resolution = AddressBarResolver.Resolve(input, _engineId);
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

    private async void FindGo_Click(object sender, RoutedEventArgs e) => await StartFindAsync();

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
        AppLog.Info("Reader Mode requested");
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
            AppLog.Info("Reader extraction starting");
            var raw = await host.View.CoreWebView2.ExecuteScriptAsync(ReaderExtractionScript.Script);
            AppLog.Info($"Reader extraction returned chars={raw?.Length ?? 0}");
            var json = JsonSerializer.Deserialize<string>(raw);
            var page = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<ReaderExtractedPage>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (page is null || page.Blocks.Count == 0)
            {
                AppLog.Info($"Reader Mode no content raw_chars={raw?.Length ?? 0}");
                StatusText.Text = "Reader Mode could not extract readable content.";
                return;
            }
            _readerPage = page;
            _readerNonce = Guid.NewGuid().ToString("N");
            AppLog.Info($"Reader content extracted words={page.WordCount} blocks={page.Blocks.Count}");
            ReaderTitleText.Text = string.IsNullOrWhiteSpace(page.Title) ? "Reader" : page.Title;
            await EnsureReaderAsync();
            WebViewHost.Visibility = Visibility.Collapsed;
            RenderReader();
            ReaderHost.Visibility = Visibility.Visible;
            StatusText.Text = $"Reader Mode · {page.WordCount} words";
        }
        catch (Exception exception)
        {
            AppLog.Error("Reader Mode failed", exception);
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
        if (!_aiOpen) WebViewHost.Visibility = Visibility.Visible;
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

    private void Ai_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Info($"AI panel click open={_aiOpen}");
        if (_aiOpen)
        {
            CloseAiPanel();
            return;
        }
        CloseFind();
        CloseReader();
        WebViewHost.Visibility = Visibility.Collapsed;
        AiColumn.Width = new GridLength(1, GridUnitType.Star);
        AiPanelHost.Visibility = Visibility.Visible;
        _aiOpen = true;
        StatusText.Text = "AI web panel opened";
        AppLog.Info("AI panel opened");
        _ = InitializeAiPanelAsync();
    }

    private async Task InitializeAiPanelAsync()
    {
        try
        {
            AppLog.Info("AI panel ensure starting");
            await EnsureAiAsync();
            AppLog.Info("AI panel ensure complete");
            if (AiServiceBox.SelectedItem is AiServiceOption service && AiWebView.CoreWebView2 is { } core)
            {
                AppLog.Info($"AI navigating={service.Url}");
                core.Navigate(service.Url);
            }
            AppLog.Info("AI panel navigation requested");
        }
        catch (Exception exception)
        {
            AppLog.Error("AI panel failed", exception);
            StatusText.Text = $"AI panel failed: {exception.Message}";
            CloseAiPanel();
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
        WebViewHost.Visibility = Visibility.Visible;
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
        AddMenu(menu, "API AI settings…", "", () => _ = ShowAiApiSettingsAsync());
        AddMenu(menu, "Ask API AI…", "", () => _ = AskApiAiAsync());
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenu(menu, "Check for updates…", "", () => _ = CheckForUpdatesAsync(true));
        if (PortableUpdateRecovery.NeedsRecovery(_appDataRoot))
        {
            AddMenu(menu, "Recover interrupted update…", "", () => _ = RecoverInterruptedUpdateAsync());
        }
        AddMenu(menu, "Diagnostics…", "", () => _ = ShowDiagnosticsAsync());
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
        AutomationProperties.SetName(item, text);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private string CurrentAppVersion =>
        typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.58.0";

    private string UpdatePreferencesPath => Path.Combine(_appDataRoot, "update-preferences.json");
    private string AiApiSettingsPath => Path.Combine(_appDataRoot, "ai-api-settings.json");

    private void LoadUpdatePreferences()
    {
        try
        {
            if (File.Exists(UpdatePreferencesPath))
            {
                _updatePreferences = JsonSerializer.Deserialize<UpdatePreferences>(
                    File.ReadAllText(UpdatePreferencesPath)) ?? _updatePreferences;
            }
        }
        catch (Exception exception)
        {
            AppLog.Warn($"Update preferences could not be loaded: {exception.GetType().Name}");
        }
    }

    private void SaveUpdatePreferences()
    {
        try
        {
            File.WriteAllText(UpdatePreferencesPath, JsonSerializer.Serialize(_updatePreferences));
        }
        catch (Exception exception)
        {
            AppLog.Error("Update preferences could not be saved", exception);
        }
    }

    private void LoadAiApiSettings()
    {
        try
        {
            if (File.Exists(AiApiSettingsPath))
            {
                _aiApiSettings = JsonSerializer.Deserialize<AiApiSettings>(
                    File.ReadAllText(AiApiSettingsPath)) ?? _aiApiSettings;
            }
        }
        catch (Exception exception)
        {
            AppLog.Warn($"AI API settings could not be loaded: {exception.GetType().Name}");
        }
    }

    private void SaveAiApiSettings()
    {
        try
        {
            File.WriteAllText(AiApiSettingsPath, JsonSerializer.Serialize(_aiApiSettings));
        }
        catch (Exception exception)
        {
            AppLog.Error("AI API settings could not be saved", exception);
        }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateCheckRunning) return;
        if (!manual)
        {
            var last = DateTimeOffset.FromUnixTimeMilliseconds(_updatePreferences.LastCheckUnixMs);
            if (DateTimeOffset.UtcNow - last < TimeSpan.FromHours(24)) return;
        }
        _updateCheckRunning = true;
        if (manual) StatusText.Text = "Checking GitHub Releases…";
        try
        {
            var result = await _releaseClient.CheckAsync(CurrentAppVersion, _updatePreferences.Channel);
            _updatePreferences = _updatePreferences with
            {
                LastCheckUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            SaveUpdatePreferences();
            if (!result.Succeeded)
            {
                StatusText.Text = result.Error ?? "Update check failed.";
                if (manual) await ShowMessageAsync("Update check", result.Error ?? "Update check failed.");
                return;
            }
            var update = result.Update;
            if (!result.UpdateAvailable || update == null)
            {
                StatusText.Text = "AMARE is up to date.";
                if (manual) await ShowMessageAsync("Update check", "AMARE is up to date.");
                return;
            }
            await ShowUpdateDialogAsync(update);
        }
        catch (Exception exception)
        {
            AppLog.Error("Update check failed", exception);
            StatusText.Text = $"Update check failed: {exception.Message}";
            if (manual) await ShowMessageAsync("Update check", "Update check failed. Please try again later.");
        }
        finally
        {
            _updateCheckRunning = false;
        }
    }

    private async Task ShowUpdateDialogAsync(AvailableUpdate update)
    {
        var notes = string.IsNullOrWhiteSpace(update.Notes) ? "No release notes provided." : update.Notes;
        if (notes.Length > 4000) notes = notes[..4000] + "…";
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = $"Current version: {CurrentAppVersion}" });
        panel.Children.Add(new TextBlock { Text = $"New version: {update.Version}" });
        panel.Children.Add(new TextBlock
        {
            Text = update.Checksum == null
                ? "This release has no SHA-256 checksum. Automatic download is blocked."
                : $"Download: {update.Package.Name} ({update.Package.Size:N0} bytes)",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 260,
            Content = new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap }
        });
        var releaseLink = new HyperlinkButton { Content = "Open release page" };
        releaseLink.Click += async (_, _) => await OpenUrlAsync(update.PageUrl);
        panel.Children.Add(releaseLink);
        var dialog = new ContentDialog
        {
            Title = update.Prerelease ? "Pre-release update available" : "Update available",
            Content = panel,
            PrimaryButtonText = update.Checksum == null ? "Open release page" : "Download & verify",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (update.Checksum == null)
        {
            await OpenUrlAsync(update.PageUrl);
            return;
        }
        await DownloadUpdateAsync(update);
    }

    private async Task DownloadUpdateAsync(AvailableUpdate update)
    {
        var destination = Path.Combine(_appDataRoot, "updates", update.Version);
        StatusText.Text = $"Downloading AMARE {update.Version}…";
        var result = await _updateDownloader.DownloadAsync(
            update, destination,
            (received, total) => StatusText.Text = total > 0
                ? $"Downloading AMARE {update.Version}: {received * 100 / total}%"
                : $"Downloading AMARE {update.Version}: {received:N0} bytes");
        if (!result.Succeeded || result.FilePath == null)
        {
            StatusText.Text = result.Error ?? "Update download failed.";
            await ShowMessageAsync("Update download", result.Error ?? "Update download failed.");
            return;
        }
        var validation = PortableUpdatePackage.Validate(result.FilePath, update.Version, "win-x64");
        if (!validation.Valid)
        {
            try { File.Delete(result.FilePath); } catch { }
            StatusText.Text = validation.Error ?? "Update package is invalid.";
            await ShowMessageAsync("Update package", validation.Error ?? "Update package is invalid.");
            return;
        }
        StatusText.Text = "Update downloaded, checksum and package verified.";
        if (AppPaths.IsPackaged)
        {
            await ShowMessageAsync("MSIX update",
                "This AMARE build is installed as an MSIX package. Portable file replacement is disabled. " +
                "Install a newer signed MSIX package through Windows; no automatic MSIX updater is available yet.");
            await Launcher.LaunchFolderAsync(
                await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(result.FilePath)!));
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "Update verified",
            Content = $"SHA-256 and package manifest verified.\n{update.Package.Name}\n" +
                      $"Staged at:\n{result.FilePath}\n\n" +
                      "AMARE will close, back up the current app files, apply the update, " +
                      "and restore the previous version if the new version fails to start. " +
                      "User data in %LOCALAPPDATA%\\AMARE is preserved.",
            PrimaryButtonText = "Install and restart",
            SecondaryButtonText = "Open folder",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Secondary)
        {
            await Launcher.LaunchFolderAsync(
                await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(result.FilePath)!));
            return;
        }
        if (choice != ContentDialogResult.Primary) return;
        var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!PortableUpdateLauncher.TryStartApply(
            result.FilePath, _appDataRoot, appDirectory, update.Version, result.Sha256!, out var helperError))
        {
            await ShowMessageAsync("Update helper", helperError ?? "The update helper could not be started.");
            return;
        }
        StatusText.Text = "Applying update. AMARE will close and restart automatically.";
        Close();
        Application.Current.Exit();
    }

    private async Task ShowDiagnosticsAsync()
    {
        var updateState = PortableUpdateStateStore.Load(_appDataRoot);
        var webView2 = "not initialized";
        try
        {
            webView2 = CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "not found";
        }
        catch
        {
            webView2 = "unavailable";
        }
        var handle = WindowNative.GetWindowHandle(this);
        var dpi = handle == IntPtr.Zero ? 0 : GetDpiForWindow(handle);
        var size = AppWindow.Size;
        var lines = new[]
        {
            $"AMARE: {CurrentAppVersion}",
            $"Windows: {Environment.OSVersion.VersionString}",
            $"Process architecture: {RuntimeInformation.ProcessArchitecture}",
            $"WebView2 Runtime: {webView2}",
            $"Window: {size.Width} x {size.Height}",
            $"DPI: {dpi} ({(dpi == 0 ? "unknown" : $"{dpi * 100 / 96}%")})",
            $"Displays: {GetSystemMetrics(80)}",
            $"Log: {AppLog.LogPath ?? "-"}",
            $"Browser engine initialized: {(_environment != null ? "yes" : "no")}",
            "Certificate handling: Cancel, no automatic bypass",
            $"Update state: {updateState?.Status ?? "none"}",
            $"Update version: {updateState?.Version ?? "-"}",
            $"Update error: {updateState?.Error ?? "-"}",
            "GPU: not collected (UNVERIFIED)",
            $"Generated: {DateTimeOffset.UtcNow:O}"
        };
        var text = string.Join(Environment.NewLine, lines);
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 260
        };
        var dialog = new ContentDialog
        {
            Title = "Diagnostics",
            Content = new ScrollViewer { MaxHeight = 560, Content = box },
            PrimaryButtonText = "Copy",
            SecondaryButtonText = "Open logs",
            CloseButtonText = "Close",
            XamlRoot = RootGrid.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var data = new DataPackage();
            data.SetText(text);
            Clipboard.SetContent(data);
        }
        else if (result == ContentDialogResult.Secondary && AppLog.LogPath is { } logPath)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{logPath}\"",
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }
    }

    private async Task RecoverInterruptedUpdateAsync()
    {
        if (!PortableUpdateRecovery.NeedsRecovery(_appDataRoot))
        {
            await ShowMessageAsync("Update recovery", "No recoverable interrupted update was found.");
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "Recover interrupted update",
            Content = "AMARE will close and restore the previous application files from the local update backup. User data is not changed.",
            PrimaryButtonText = "Recover",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!PortableUpdateLauncher.TryStartRecovery(_appDataRoot, out var error))
        {
            await ShowMessageAsync("Update recovery", error ?? "The recovery helper could not be started.");
            return;
        }
        Close();
        Application.Current.Exit();
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        await new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "Close",
            XamlRoot = RootGrid.XamlRoot
        }.ShowAsync();
    }

    private async Task OpenUrlAsync(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            await Launcher.LaunchUriAsync(uri);
        }
    }

    private async Task ShowAiApiSettingsAsync()
    {
        var existingKey = AiCredentialStore.GetKey();
        var keyBox = new PasswordBox { Password = existingKey ?? "", PlaceholderText = "OpenAI API key" };
        var providerCombo = new ComboBox
        {
            ItemsSource = AiProviderCatalog.All,
            DisplayMemberPath = nameof(AiProviderDescriptor.Name),
            SelectedItem = AiProviderCatalog.Resolve(_aiApiSettings.Provider)
        };
        var modelBox = new TextBox { Text = _aiApiSettings.Model, PlaceholderText = AiApiClient.DefaultModel };
        var baseUrlBox = new TextBox { Text = _aiApiSettings.BaseUrl, PlaceholderText = AiApiClient.DefaultBaseUrl };
        var testResult = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var testButton = new Button { Content = "Test connection" };
        testButton.Click += async (_, _) =>
        {
            try
            {
                testResult.Text = "Testing…";
                var provider = providerCombo.SelectedItem as AiProviderDescriptor ?? AiProviderCatalog.All[0];
                var settings = new AiApiSettings(
                    modelBox.Text.Trim(), baseUrlBox.Text.Trim(), provider.Id);
                var answer = await AiApiClient.CompleteAsync(
                    keyBox.Password, settings, "Reply with OK only.", null);
                testResult.Text = $"Connection OK: {answer[..Math.Min(answer.Length, 80)]}";
            }
            catch (Exception exception)
            {
                testResult.Text = $"Connection failed: {exception.Message}";
            }
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "Optional API mode. The API key is stored only in Windows Credential Manager and is never written to logs or settings files.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock { Text = "Provider" });
        panel.Children.Add(providerCombo);
        panel.Children.Add(new TextBlock { Text = "API key" });
        panel.Children.Add(keyBox);
        panel.Children.Add(new TextBlock { Text = "Model" });
        panel.Children.Add(modelBox);
        panel.Children.Add(new TextBlock { Text = "Base URL" });
        panel.Children.Add(baseUrlBox);
        panel.Children.Add(testButton);
        panel.Children.Add(testResult);
        var dialog = new ContentDialog
        {
            Title = "API AI settings",
            Content = new ScrollViewer { MaxHeight = 520, Content = panel },
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Delete key",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            AiCredentialStore.Delete();
            StatusText.Text = "API key removed.";
            return;
        }
        if (result != ContentDialogResult.Primary) return;
        if (!string.IsNullOrWhiteSpace(keyBox.Password) && !AiCredentialStore.Save(keyBox.Password))
        {
            await ShowMessageAsync("API key", "Windows Credential Manager could not store the key.");
            return;
        }
        var selectedProvider = providerCombo.SelectedItem as AiProviderDescriptor ?? AiProviderCatalog.All[0];
        _aiApiSettings = new AiApiSettings(
            modelBox.Text.Trim().Length == 0 ? selectedProvider.DefaultModel : modelBox.Text.Trim(),
            baseUrlBox.Text.Trim().Length == 0 ? selectedProvider.BaseUrl : baseUrlBox.Text.Trim(),
            selectedProvider.Id);
        SaveAiApiSettings();
        StatusText.Text = "API AI settings saved. Web AI remains the default.";
    }

    private async Task AskApiAiAsync()
    {
        var key = AiCredentialStore.GetKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            await ShowMessageAsync("API AI", "Configure an API key first from the browser menu → API AI settings.");
            return;
        }
        var host = ActiveHost;
        if (host?.View.CoreWebView2 is null)
        {
            await ShowMessageAsync("API AI", "Open a page before asking API AI.");
            return;
        }
        var promptBox = new TextBox
        {
            Text = "Summarize this page.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90
        };
        var includeArticle = new CheckBox { Content = "Include extracted article text (user-approved)", IsChecked = true };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Current page: {host.Model.Title}\n{host.Model.Url}",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Page content is untrusted data. It is sent only after you press Send.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(promptBox);
        panel.Children.Add(includeArticle);
        var dialog = new ContentDialog
        {
            Title = "Ask API AI",
            Content = new ScrollViewer { MaxHeight = 520, Content = panel },
            PrimaryButtonText = "Send",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var prompt = promptBox.Text.Trim();
        if (prompt.Length == 0) return;
        try
        {
            StatusText.Text = "API AI request in progress…";
            var context = $"Title: {host.Model.Title}\nURL: {host.Model.Url}";
            var selection = JsonSerializer.Deserialize<string>(
                await host.View.CoreWebView2.ExecuteScriptAsync(
                    "window.getSelection ? window.getSelection().toString() : ''"));
            if (!string.IsNullOrWhiteSpace(selection))
            {
                context += "\nSelected text:\n" + selection.Trim();
            }
            if (includeArticle.IsChecked == true)
            {
                var raw = await host.View.CoreWebView2.ExecuteScriptAsync(ReaderExtractionScript.Script);
                var pageJson = JsonSerializer.Deserialize<string>(raw);
                var page = string.IsNullOrWhiteSpace(pageJson)
                    ? null
                    : JsonSerializer.Deserialize<ReaderExtractedPage>(
                        pageJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (page is not null)
                {
                    var text = string.Join("\n\n", page.Blocks
                        .Where(block => block.Tag != "img")
                        .Select(block => block.Text)
                        .Where(text => !string.IsNullOrWhiteSpace(text)));
                    if (text.Length > 20000) text = text[..20000];
                    context += "\nArticle (untrusted data):\n" + text;
                }
            }
            var responseBuilder = new StringBuilder();
            var responseBox = new TextBox
            {
                Text = "Waiting for the response…",
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 220
            };
            var streamStatus = new TextBlock { Text = "Streaming…", TextWrapping = TextWrapping.Wrap };
            var copyButton = new Button { Content = "Copy" };
            var responsePanel = new StackPanel { Spacing = 8 };
            responsePanel.Children.Add(responseBox);
            responsePanel.Children.Add(streamStatus);
            copyButton.Click += (_, _) =>
            {
                if (responseBuilder.Length == 0) return;
                var data = new DataPackage();
                data.SetText(responseBuilder.ToString());
                Clipboard.SetContent(data);
                streamStatus.Text = "Response copied.";
            };
            responsePanel.Children.Add(copyButton);
            using var streamCancellation = new CancellationTokenSource();
            var responseDialog = new ContentDialog
            {
                Title = "API AI response",
                Content = new ScrollViewer { MaxHeight = 560, Content = responsePanel },
                CloseButtonText = "Cancel / Close",
                XamlRoot = RootGrid.XamlRoot
            };
            responseDialog.Opened += async (_, _) =>
            {
                try
                {
                    await foreach (var chunk in AiApiClient.StreamAsync(
                        key, _aiApiSettings, prompt, context, streamCancellation.Token))
                    {
                        responseBuilder.Append(chunk);
                        responseBox.Text = responseBuilder.ToString();
                        responseBox.SelectionStart = responseBox.Text.Length;
                    }
                    streamStatus.Text = responseBuilder.Length == 0
                        ? "The API returned an empty response."
                        : "Response complete.";
                    StatusText.Text = "API AI response received.";
                }
                catch (OperationCanceledException)
                {
                    streamStatus.Text = "Request cancelled.";
                    StatusText.Text = "API AI request cancelled.";
                }
                catch (Exception exception)
                {
                    streamStatus.Text = $"API AI failed: {exception.Message}";
                    StatusText.Text = "API AI failed.";
                }
            };
            responseDialog.Closed += (_, _) => streamCancellation.Cancel();
            await responseDialog.ShowAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"API AI failed: {exception.Message}";
            await ShowMessageAsync("API AI", $"API AI failed: {exception.Message}");
        }
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
        var updateBox = new CheckBox { Content = "Check GitHub Releases for updates on startup", IsChecked = _updatePreferences.CheckOnStartup };
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "Appearance theme" });
        stack.Children.Add(themeBox);
        stack.Children.Add(new TextBlock { Text = "Search engine" });
        stack.Children.Add(engineBox);
        stack.Children.Add(restoreBox);
        stack.Children.Add(updateBox);
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
            _updatePreferences = _updatePreferences with { CheckOnStartup = updateBox.IsChecked == true };
            SaveUpdatePreferences();
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
        lock (WindowRegistryGate)
        {
            WindowRegistry.RemoveAll(reference =>
                !reference.TryGetTarget(out var window) || window == this);
        }
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
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

}
