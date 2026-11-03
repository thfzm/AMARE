namespace Amare.Windows.Core;

public sealed record RestoredTab(string Url, string Title, bool Pinned);

public sealed record SessionRestorePlan(IReadOnlyList<RestoredTab> Tabs, int ActiveIndex);

public static class SessionRestorePlanner
{
    public const int DefaultMaxTabs = 100;

    public static SessionRestorePlan? Create(SessionStateDto? state, int maxTabs = DefaultMaxTabs)
    {
        if (state is null || state.Tabs.Count == 0)
        {
            return null;
        }
        if (maxTabs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTabs));
        }
        var ordered = state.Tabs
            .OrderBy(tab => tab.Position)
            .ThenBy(tab => tab.Id)
            .Take(maxTabs)
            .ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }
        var tabs = ordered.Select(tab => new RestoredTab(
            string.IsNullOrWhiteSpace(tab.Url) ? "amare://newtab" : tab.Url,
            string.IsNullOrWhiteSpace(tab.Title) ? "New Tab" : tab.Title,
            tab.Pinned)).ToArray();
        var active = 0;
        if (state.Session.ActiveTabId is { } activeId)
        {
            var index = Array.FindIndex(ordered, tab => tab.Id == activeId);
            if (index >= 0)
            {
                active = index;
            }
        }
        return new SessionRestorePlan(tabs, active);
    }
}

public static class SessionSaveRetryPolicy
{
    public const int DefaultMaxAttempts = 3;

    public static bool ShouldRetry(int failedAttempt, Exception error, int maxAttempts = DefaultMaxAttempts) =>
        failedAttempt < maxAttempts && error is not OperationCanceledException and not ObjectDisposedException;

    public static TimeSpan DelayFor(int failedAttempt)
    {
        var exponent = Math.Clamp(failedAttempt - 1, 0, 4);
        return TimeSpan.FromMilliseconds(Math.Min(1_000, 100 * (1 << exponent)));
    }
}
