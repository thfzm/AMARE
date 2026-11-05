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
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            IsPackaged ? "AMARE-MSIX" : "AMARE");
    }
}
