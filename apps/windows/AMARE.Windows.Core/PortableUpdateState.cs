using System.Text.Json;

namespace Amare.Windows.Core;

public static class PortableUpdateStatus
{
    public const string Preparing = "preparing";
    public const string Applying = "applying";
    public const string Applied = "applied";
    public const string Verifying = "verifying";
    public const string Succeeded = "succeeded";
    public const string RollingBack = "rolling_back";
    public const string RolledBack = "rolled_back";
    public const string Failed = "failed";
}

public sealed record PortableUpdateState(
    int SchemaVersion,
    string Status,
    string Version,
    string PreviousVersion,
    string PackagePath,
    string PackageSha256,
    string AppDir,
    string DataDir,
    string BackupDir,
    string StagingDir,
    string EntryPoint,
    string HealthMarkerPath,
    long StartedUnixMs,
    long UpdatedUnixMs,
    string? Error)
{
    public bool IsInProgress => Status is PortableUpdateStatus.Preparing
        or PortableUpdateStatus.Applying
        or PortableUpdateStatus.Applied
        or PortableUpdateStatus.Verifying;
}

public static class PortableUpdateStateStore
{
    public const string FileName = "update-state.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string PathFor(string dataDirectory) =>
        Path.Combine(dataDirectory, FileName);

    public static PortableUpdateState? Load(string dataDirectory)
    {
        try
        {
            var path = PathFor(dataDirectory);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<PortableUpdateState>(
                File.ReadAllText(path), JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Save(string dataDirectory, PortableUpdateState state)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = PathFor(dataDirectory);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(string dataDirectory)
    {
        try
        {
            var path = PathFor(dataDirectory);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // State cleanup is best effort; a stale file is recoverable.
        }
    }
}

public static class PortableUpdateRecovery
{
    public static PortableUpdateState? Load(string dataDirectory) =>
        PortableUpdateStateStore.Load(dataDirectory);

    public static bool NeedsRecovery(string dataDirectory)
    {
        var state = Load(dataDirectory);
        return state != null
            && (state.IsInProgress || state.Status is PortableUpdateStatus.RollingBack
                or PortableUpdateStatus.Failed)
            && Directory.Exists(state.BackupDir);
    }

    public static string? RecoveryMessage(string dataDirectory)
    {
        var state = Load(dataDirectory);
        if (state == null) return null;
        return state.Status switch
        {
            PortableUpdateStatus.Applying or PortableUpdateStatus.Applied
                or PortableUpdateStatus.Verifying => $"An update to {state.Version} was interrupted.",
            PortableUpdateStatus.RollingBack => $"Recovery of the previous version was interrupted.",
            PortableUpdateStatus.Failed => $"Update {state.Version} failed: {state.Error}",
            _ => null
        };
    }

    public static void MarkHealthyIfRequested(string dataDirectory, string currentVersion, string appDirectory)
    {
        try
        {
            var marker = Environment.GetEnvironmentVariable(PortableUpdateEngine.HealthMarkerEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(marker))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, $"healthy {currentVersion} {DateTimeOffset.UtcNow:O}");
            }
            var state = Load(dataDirectory);
            if (state == null || !state.IsInProgress) return;
            if (SemanticVersion.Compare(currentVersion, state.Version) != 0) return;
            if (!File.Exists(Path.Combine(appDirectory, state.EntryPoint))) return;
            var completed = state with
            {
                Status = PortableUpdateStatus.Succeeded,
                UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Error = null
            };
            PortableUpdateStateStore.Save(dataDirectory, completed);
            PortableUpdateEngine.TryCleanup(completed, deleteHealthMarker: false);
        }
        catch
        {
            // A recovery marker must never prevent normal browser startup.
        }
    }
}
