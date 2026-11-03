using System.Text.Json;

namespace Amare.Windows.Core;

public sealed record ProfileCleanupResult(string ProfileId, bool Removed, int Attempts, bool Pending);

/// <summary>
/// Deletes per-profile WebView2 data with retries and a persisted pending list.
/// The profile registry/database deletion is separate; this class only tracks
/// filesystem cleanup, so a locked UserDataFolder is never reported as fully
/// cleaned up.
/// </summary>
public sealed class ProfileDataCleanup
{
    private const string StateFileName = "pending-webview2-cleanup.json";
    private readonly string _appDataRoot;
    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProfileDataCleanup(string appDataRoot)
    {
        _appDataRoot = appDataRoot ?? throw new ArgumentNullException(nameof(appDataRoot));
        _statePath = Path.Combine(appDataRoot, "cleanup", StateFileName);
    }

    public string StatePath => _statePath;

    public async Task<IReadOnlyList<string>> ReadPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await LoadPendingAsync(cancellationToken))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProfileCleanupResult> CleanupAsync(
        string profileId,
        int maxAttempts = 5,
        Func<string, Task>? delete = null,
        Func<int, TimeSpan>? retryDelay = null,
        CancellationToken cancellationToken = default)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await CleanupLockedAsync(profileId, maxAttempts, delete, retryDelay, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ProfileCleanupResult>> ResumePendingAsync(
        int maxAttempts = 5,
        Func<string, Task>? delete = null,
        Func<int, TimeSpan>? retryDelay = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pending = await LoadPendingAsync(cancellationToken);
            var results = new List<ProfileCleanupResult>();
            foreach (var profileId in pending)
            {
                results.Add(await CleanupLockedAsync(profileId, maxAttempts, delete, retryDelay, cancellationToken));
            }
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ProfileCleanupResult> CleanupLockedAsync(
        string profileId,
        int maxAttempts,
        Func<string, Task>? delete,
        Func<int, TimeSpan>? retryDelay,
        CancellationToken cancellationToken)
    {
        var folder = ProfilePathPolicy.GetWebViewDataFolder(_appDataRoot, profileId);
        var pending = await LoadPendingAsync(cancellationToken);
        var attempts = 0;
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts = attempt;
            if (!Directory.Exists(folder))
            {
                pending.Remove(profileId);
                await SavePendingAsync(pending, cancellationToken);
                return new ProfileCleanupResult(profileId, true, attempts, false);
            }
            try
            {
                if (delete is not null)
                {
                    await delete(folder);
                }
                else
                {
                    await Task.Run(() => Directory.Delete(folder, recursive: true), cancellationToken);
                }
                if (!Directory.Exists(folder))
                {
                    pending.Remove(profileId);
                    await SavePendingAsync(pending, cancellationToken);
                    return new ProfileCleanupResult(profileId, true, attempts, false);
                }
                lastError = new IOException("directory still exists after deletion");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
            }
            if (attempt < maxAttempts)
            {
                var delay = retryDelay?.Invoke(attempt) ?? TimeSpan.FromMilliseconds(100 * attempt);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        pending.Add(profileId);
        await SavePendingAsync(pending, cancellationToken);
        _ = lastError;
        return new ProfileCleanupResult(profileId, false, attempts, true);
    }

    private async Task<HashSet<string>> LoadPendingAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        try
        {
            var json = await File.ReadAllTextAsync(_statePath, cancellationToken);
            var values = JsonSerializer.Deserialize<List<string>>(json, JsonDefaults.Options) ?? [];
            return values
                .Where(ProfilePathPolicy.IsSafeProfileId)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        catch (IOException)
        {
            throw;
        }
    }

    private async Task SavePendingAsync(HashSet<string> pending, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var sorted = pending.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (sorted.Length == 0)
        {
            if (File.Exists(_statePath))
            {
                File.Delete(_statePath);
            }
            return;
        }
        var json = JsonSerializer.Serialize(sorted, JsonDefaults.Options);
        var temporary = _statePath + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(temporary, json, cancellationToken);
        File.Move(temporary, _statePath, overwrite: true);
    }
}
