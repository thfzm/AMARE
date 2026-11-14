namespace Amare.Windows.Core;

public enum BrowserCommand
{
    FocusAddressBar,
    NewTab,
    NewWindow,
    CloseTab,
    RestoreClosedTab,
    Reload,
    Back,
    Forward,
    Find,
    NextTab,
    PreviousTab
}

public static class BrowserShortcutPolicy
{
    public static BrowserCommand? Resolve(bool control, bool shift, bool alt, string key)
    {
        var normalized = key.Trim().ToUpperInvariant();
        if (control && !alt)
        {
            return (shift, normalized) switch
            {
                (false, "L") => BrowserCommand.FocusAddressBar,
                (false, "T") => BrowserCommand.NewTab,
                (false, "N") => BrowserCommand.NewWindow,
                (true, "N") => BrowserCommand.NewWindow,
                (false, "W") => BrowserCommand.CloseTab,
                (true, "T") => BrowserCommand.RestoreClosedTab,
                (false, "R") => BrowserCommand.Reload,
                (false, "F") => BrowserCommand.Find,
                (false, "TAB") => BrowserCommand.NextTab,
                (true, "TAB") => BrowserCommand.PreviousTab,
                _ => null
            };
        }
        if (alt && !control && !shift)
        {
            return normalized switch
            {
                "LEFT" => BrowserCommand.Back,
                "RIGHT" => BrowserCommand.Forward,
                _ => null
            };
        }
        return null;
    }
}
