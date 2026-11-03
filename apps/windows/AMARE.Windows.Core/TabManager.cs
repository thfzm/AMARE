using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Amare.Windows.Core;

public sealed class BrowserTabModel : INotifyPropertyChanged
{
    private string _title = "New Tab";
    private string _url = "";
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _isPinned;
    private bool _isNewTabPage = true;

    public Guid Id { get; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public string Url
    {
        get => _url;
        set => Set(ref _url, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => Set(ref _isLoading, value);
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        set => Set(ref _canGoBack, value);
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        set => Set(ref _canGoForward, value);
    }

    public bool IsPinned
    {
        get => _isPinned;
        set => Set(ref _isPinned, value);
    }

    public bool IsNewTabPage
    {
        get => _isNewTabPage;
        set => Set(ref _isNewTabPage, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}

public sealed record TabSnapshot(string Url, string Title, bool Pinned);

public sealed record TabLifecycleSnapshot(int Created, int Closed, int Live, int ActiveIndex);

public sealed record TabSessionSnapshot(IReadOnlyList<TabSnapshot> Tabs, int ActiveIndex);

public sealed class TabManager
{
    private readonly Func<BrowserTabModel> _factory;
    private int _nextOrdinal;

    public TabManager(Func<BrowserTabModel>? factory = null)
    {
        _factory = factory ?? (() => new BrowserTabModel());
        Tabs.CollectionChanged += (_, _) => { };
    }

    public ObservableCollection<BrowserTabModel> Tabs { get; } = [];
    public BrowserTabModel? ActiveTab { get; private set; }
    public int CreatedCount { get; private set; }
    public int ClosedCount { get; private set; }
    public int LiveCount => Math.Max(0, CreatedCount - ClosedCount);

    public BrowserTabModel AddTab(string? url = null, string? title = null, bool activate = true)
    {
        var tab = _factory();
        tab.Title = string.IsNullOrWhiteSpace(title) ? $"New Tab {++_nextOrdinal}" : title;
        tab.Url = url ?? "";
        tab.IsNewTabPage = string.IsNullOrWhiteSpace(url) || url.Equals("amare://newtab", StringComparison.OrdinalIgnoreCase);
        Tabs.Add(tab);
        CreatedCount++;
        if (activate || ActiveTab is null)
        {
            ActiveTab = tab;
        }
        return tab;
    }

    public bool Activate(Guid id)
    {
        var tab = Tabs.FirstOrDefault(candidate => candidate.Id == id);
        if (tab is null)
        {
            return false;
        }
        ActiveTab = tab;
        return true;
    }

    /// <summary>
    /// Closes a tab. Closing the last tab creates a replacement new tab.
    /// Returns true when a replacement was created.
    /// </summary>
    public bool Close(Guid id)
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            return false;
        }
        var wasActive = ActiveTab?.Id == id;
        Tabs.RemoveAt(index);
        ClosedCount++;
        if (Tabs.Count == 0)
        {
            AddTab();
            return true;
        }
        if (wasActive)
        {
            ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }
        return false;
    }

    public bool Move(Guid id, int newIndex)
    {
        var oldIndex = IndexOf(id);
        if (oldIndex < 0)
        {
            return false;
        }
        newIndex = Math.Clamp(newIndex, 0, Tabs.Count - 1);
        if (oldIndex != newIndex)
        {
            Tabs.Move(oldIndex, newIndex);
        }
        return true;
    }

    public bool SetPinned(Guid id, bool pinned)
    {
        var tab = Tabs.FirstOrDefault(candidate => candidate.Id == id);
        if (tab is null)
        {
            return false;
        }
        tab.IsPinned = pinned;
        return true;
    }

    public int ActiveIndex =>
        ActiveTab is null ? -1 : Math.Max(0, Tabs.IndexOf(ActiveTab));

    public TabSessionSnapshot Snapshot() =>
        new(
            Tabs.Select(tab => new TabSnapshot(
                string.IsNullOrWhiteSpace(tab.Url) ? "amare://newtab" : tab.Url,
                tab.Title,
                tab.IsPinned)).ToArray(),
            ActiveIndex);

    public void Clear()
    {
        ClosedCount += Tabs.Count;
        Tabs.Clear();
        ActiveTab = null;
    }

    public TabLifecycleSnapshot Diagnostics() =>
        new(CreatedCount, ClosedCount, LiveCount, ActiveIndex);

    public int IndexOf(Guid id)
    {
        for (var index = 0; index < Tabs.Count; index++)
        {
            if (Tabs[index].Id == id)
            {
                return index;
            }
        }
        return -1;
    }
}
