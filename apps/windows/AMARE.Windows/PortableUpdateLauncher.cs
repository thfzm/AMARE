using System.Diagnostics;

namespace Amare.Windows;

internal static class PortableUpdateLauncher
{
    private const string HelperName = "AMARE.Updater.exe";

    public static bool TryStartApply(
        string packagePath,
        string dataRoot,
        string appDirectory,
        string version,
        string sha256,
        out string? error)
    {
        error = null;
        try
        {
            var helper = CopyHelperToStableLocation(dataRoot);
            var healthMarker = Path.Combine(dataRoot, "update-health.json");
            TryDelete(healthMarker);
            var info = new ProcessStartInfo(helper)
            {
                WorkingDirectory = Path.GetDirectoryName(helper)!,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("apply");
            info.ArgumentList.Add("--package");
            info.ArgumentList.Add(Path.GetFullPath(packagePath));
            info.ArgumentList.Add("--app-dir");
            info.ArgumentList.Add(Path.GetFullPath(appDirectory));
            info.ArgumentList.Add("--data-dir");
            info.ArgumentList.Add(Path.GetFullPath(dataRoot));
            info.ArgumentList.Add("--version");
            info.ArgumentList.Add(version);
            info.ArgumentList.Add("--sha256");
            info.ArgumentList.Add(sha256);
            info.ArgumentList.Add("--architecture");
            info.ArgumentList.Add("win-x64");
            info.ArgumentList.Add("--entry");
            info.ArgumentList.Add("AMARE.Windows.exe");
            info.ArgumentList.Add("--parent-pid");
            info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--health-marker");
            info.ArgumentList.Add(healthMarker);
            return Process.Start(info) != null;
        }
        catch (Exception exception)
        {
            error = $"Could not start the update helper: {exception.Message}";
            return false;
        }
    }

    public static bool TryStartRecovery(string dataRoot, out string? error)
    {
        error = null;
        try
        {
            var helper = CopyHelperToStableLocation(dataRoot);
            var info = new ProcessStartInfo(helper)
            {
                WorkingDirectory = Path.GetDirectoryName(helper)!,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("recover");
            info.ArgumentList.Add("--data-dir");
            info.ArgumentList.Add(Path.GetFullPath(dataRoot));
            info.ArgumentList.Add("--parent-pid");
            info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Process.Start(info) != null;
        }
        catch (Exception exception)
        {
            error = $"Could not start update recovery: {exception.Message}";
            return false;
        }
    }

    private static string CopyHelperToStableLocation(string dataRoot)
    {
        var source = Path.Combine(AppContext.BaseDirectory, HelperName);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException(
                "The update helper is missing from this package. Download a complete AMARE portable package.");
        }
        var directory = Path.Combine(dataRoot, "updater");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, HelperName);
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, destination, overwrite: true);
        }
        return destination;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // The helper also removes stale markers.
        }
    }
}
