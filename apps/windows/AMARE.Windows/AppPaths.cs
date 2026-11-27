using System.IO;
using Windows.ApplicationModel;

namespace Amare.Windows;

internal static class AppPaths
{
    public static bool IsPackaged
    {
        get
        {
            try
            {
                return !string.IsNullOrWhiteSpace(Package.Current.Id.Name);
            }
            catch
            {
                return false;
            }
        }
    }

    public static string LocalRoot()
    {
        // Validation and isolated tests may redirect all app data to a temp
        // root. This is intentionally env-only: it is never exposed in Settings
        // and does not affect normal users unless they set the variable.
        var overrideRoot = Environment.GetEnvironmentVariable("AMARE_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Path.GetFullPath(overrideRoot);
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            IsPackaged ? "AMARE-MSIX" : "AMARE");
    }
}
