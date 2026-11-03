namespace Amare.Windows.Core;

public enum BrowserCommand
{
    FocusAddressBar,
    NewTab,
    CloseTab,
    RestoreClosedTab,
    Reload,
    Back,
    Forward
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
                (false, "W") => BrowserCommand.CloseTab,
                (true, "T") => BrowserCommand.RestoreClosedTab,
                (false, "R") => BrowserCommand.Reload,
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
