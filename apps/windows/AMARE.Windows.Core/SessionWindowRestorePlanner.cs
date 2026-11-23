namespace Amare.Windows.Core;

/// <summary>
/// Helpers for the window-scoped session model. Restoration itself reuses
/// <see cref="SessionRestorePlanner"/> so single-window behavior does not drift.
/// </summary>
public static class SessionWindowRestorePlanner
{
    public static IReadOnlyList<SessionWindowDto> Order(IEnumerable<SessionWindowDto> windows) =>
        windows
            .OrderBy(window => window.Ordinal)
            .ThenBy(window => window.WindowId, StringComparer.Ordinal)
            .ToArray();

    public static long NextOrdinal(IEnumerable<SessionWindowDto> windows)
    {
        var max = windows.Select(window => window.Ordinal).DefaultIfEmpty(-1).Max();
        return max + 1;
    }

    public static SessionRestorePlan? Create(SessionWindowDto? window, int maxTabs = SessionRestorePlanner.DefaultMaxTabs)
    {
        if (window is null)
        {
            return null;
        }
        var state = new SessionStateDto
        {
            Session = new SessionDto
            {
                Id = 0,
                ActiveTabId = window.ActiveTabId
            },
            Tabs = window.Tabs
        };
        return SessionRestorePlanner.Create(state, maxTabs);
    }

    public static bool IsValidWindowId(string? windowId) =>
        !string.IsNullOrWhiteSpace(windowId)
        && windowId.Length <= 128
        && windowId.All(character => !char.IsControl(character));
}
