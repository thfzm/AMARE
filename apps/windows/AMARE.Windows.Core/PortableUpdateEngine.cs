using System.Diagnostics;
using System.Security.Cryptography;

namespace Amare.Windows.Core;

public sealed record PortableUpdateRequest(
    string PackagePath,
    string AppDir,
    string DataDir,
    string Version,
    string ExpectedSha256,
    string Architecture = PortableUpdatePackage.DefaultArchitecture,
    string EntryPoint = "AMARE.Windows.exe",
    int ParentProcessId = 0,
    bool LaunchApp = true,
    string? HealthMarkerPath = null,
    bool FailBeforeApply = false,
    bool FailAfterApply = false,
    bool CrashAfterBackup = false,
    bool CrashAfterApply = false,
    bool RelaunchAfterRollback = true,
    int HealthTimeoutSeconds = 30);

public sealed record PortableUpdateResult(
    bool Succeeded,
    bool RolledBack,
    string? Error,
    string? StatePath,
    string? BackupDir,
    string? Status);

public static class PortableUpdateEngine
{
    public const string HealthMarkerEnvironmentVariable = "AMARE_UPDATE_HEALTH";
    public const int MaxRecoveryAttempts = 3;
    private const int ParentExitTimeoutSeconds = 60;

    public static async Task<PortableUpdateResult> ApplyAsync(
        PortableUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.PackagePath))
        {
            return Failure("Update package does not exist.");
        }
        if (string.IsNullOrWhiteSpace(request.EntryPoint)
            || request.EntryPoint.Contains('/')
            || request.EntryPoint.Contains('\\')
            || request.EntryPoint.Contains(':'))
        {
            return Failure("Update entry point is unsafe.");
        }
        string actualHash;
        try
        {
            actualHash = await Sha256FileAsync(request.PackagePath, cancellationToken);
        }
        catch (Exception exception)
        {
            return Failure($"Could not hash update package: {exception.Message}");
        }
        if (!actualHash.Equals(request.ExpectedSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            return Failure("Update package SHA-256 does not match the verified release checksum.");
        }
        var validation = PortableUpdatePackage.Validate(
            request.PackagePath, request.Version, request.Architecture, request.EntryPoint);
        if (!validation.Valid || validation.Manifest == null)
        {
            return Failure(validation.Error ?? "Update package manifest is invalid.");
        }
        var manifest = validation.Manifest;
        if (!manifest.EntryPoint.Equals(request.EntryPoint, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("Package entry point does not match the requested AMARE executable.");
        }
        if (!Directory.Exists(request.AppDir))
        {
            return Failure("AMARE application directory does not exist.");
        }
        if (!File.Exists(Path.Combine(request.AppDir, request.EntryPoint)))
        {
            return Failure("AMARE executable is missing from the application directory.");
        }
        if (!UpdateDownloadPolicy.HasSufficientFreeSpace(
                request.DataDir,
                UpdateDownloadPolicy.RequiredFreeBytes(new FileInfo(request.PackagePath).Length * 3)))
        {
            return Failure("Not enough disk space to stage and back up the update.");
        }
        using var updateLock = TryAcquireUpdateLock(request.DataDir);
        if (updateLock == null)
        {
            return Failure("Another AMARE update process is already running.");
        }
        var currentVersion = ReadVersion(request.AppDir);
        if (currentVersion != null
            && SemanticVersion.Compare(currentVersion, request.Version) >= 0)
        {
            return Failure(
                $"Update {request.Version} is not newer than installed version {currentVersion}.");
        }
        var existing = PortableUpdateStateStore.Load(request.DataDir);
        if (existing != null
            && existing.RecoveryAttempts < MaxRecoveryAttempts
            && (existing.IsInProgress
                || existing.Status is PortableUpdateStatus.RollingBack or PortableUpdateStatus.Failed)
            && Directory.Exists(existing.BackupDir))
        {
            return Failure("An interrupted update exists. Recover it before installing another update.");
        }

        CleanupStaleWork(request.DataDir, existing);
        var workRoot = Path.Combine(request.DataDir, "updates", "work");
        var staging = Path.Combine(workRoot, "staging-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(workRoot, "backup-" + Guid.NewGuid().ToString("N"));
        var marker = string.IsNullOrWhiteSpace(request.HealthMarkerPath)
            ? Path.Combine(request.DataDir, "update-health.json")
            : request.HealthMarkerPath;
        var previousVersion = currentVersion ?? "unknown";
        var state = new PortableUpdateState(
            SchemaVersion: 1,
            Status: PortableUpdateStatus.Preparing,
            Version: request.Version,
            PreviousVersion: previousVersion,
            PackagePath: request.PackagePath,
            PackageSha256: actualHash,
            AppDir: request.AppDir,
            DataDir: request.DataDir,
            BackupDir: backup,
            StagingDir: staging,
            EntryPoint: request.EntryPoint,
            HealthMarkerPath: marker,
            StartedUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UpdatedUnixMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Error: null,
            RecoveryAttempts: 0);
        PortableUpdateStateStore.Save(request.DataDir, state);

        try
        {
            if (!await WaitForParentExitAsync(request.ParentProcessId, cancellationToken))
            {
                return FailBeforeMutation(state,
                    "Timed out waiting for the running AMARE process to exit.");
            }
            Directory.CreateDirectory(workRoot);
            PortableUpdatePackage.ExtractValidated(request.PackagePath, staging, manifest);
            EnsureReplaceable(staging, request.AppDir, request.EntryPoint);
            CopyDirectory(request.AppDir, backup, overwrite: true);
            if (!File.Exists(Path.Combine(backup, request.EntryPoint)))
            {
                throw new IOException("The update backup is incomplete; the current executable was not copied.");
            }
            state = state with { Status = PortableUpdateStatus.Applying };
            PortableUpdateStateStore.Save(request.DataDir, state);

            if (request.CrashAfterBackup)
            {
                return new PortableUpdateResult(false, false,
                    "Simulated crash after backup; recovery is required.",
                    PortableUpdateStateStore.PathFor(request.DataDir), backup, state.Status);
            }
            if (request.FailBeforeApply)
            {
                throw new IOException("Simulated failure before file replacement.");
            }

            ApplyStaging(staging, request.AppDir, request.EntryPoint);
            File.WriteAllText(Path.Combine(request.AppDir, "VERSION"), manifest.Version + Environment.NewLine);
            state = state with { Status = PortableUpdateStatus.Applied };
            PortableUpdateStateStore.Save(request.DataDir, state);

            if (request.CrashAfterApply)
            {
                return new PortableUpdateResult(false, false,
                    "Simulated crash after file replacement; recovery is required.",
                    PortableUpdateStateStore.PathFor(request.DataDir), backup, state.Status);
            }
            if (request.FailAfterApply)
            {
                throw new IOException("Simulated failure after file replacement.");
            }

            if (request.LaunchApp)
            {
                state = state with { Status = PortableUpdateStatus.Verifying };
                PortableUpdateStateStore.Save(request.DataDir, state);
                if (!await VerifyNewVersionAsync(request, marker, cancellationToken))
                {
                    throw new InvalidOperationException(
                        "The new AMARE version did not report a successful startup.");
                }
            }

            state = state with
            {
                Status = PortableUpdateStatus.Succeeded,
                UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Error = null
            };
            PortableUpdateStateStore.Save(request.DataDir, state);
            TryCleanup(state);
            return new PortableUpdateResult(true, false, null,
                PortableUpdateStateStore.PathFor(request.DataDir), backup, state.Status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (state.Status == PortableUpdateStatus.Preparing)
            {
                return FailBeforeMutation(state, exception.Message);
            }
            var rollback = await RollbackAsync(
                state, request.LaunchApp && request.RelaunchAfterRollback, cancellationToken);
            return new PortableUpdateResult(false, rollback.RolledBack,
                $"{exception.Message} {(rollback.RolledBack ? "Previous version restored." : "Rollback failed: " + rollback.Error)}",
                PortableUpdateStateStore.PathFor(request.DataDir), backup,
                rollback.Status);
        }
        catch (OperationCanceledException)
        {
            if (state.Status == PortableUpdateStatus.Preparing)
            {
                return FailBeforeMutation(state, "Update cancelled before any application files were changed.");
            }
            var rollback = await RollbackAsync(
                state, request.LaunchApp && request.RelaunchAfterRollback, CancellationToken.None);
            return new PortableUpdateResult(false, rollback.RolledBack,
                "Update cancelled. " + (rollback.RolledBack ? "Previous version restored." : "Rollback failed."),
                PortableUpdateStateStore.PathFor(request.DataDir), backup, rollback.Status);
        }
    }

    private static PortableUpdateResult FailBeforeMutation(PortableUpdateState state, string message)
    {
        TryDeleteDirectory(state.StagingDir);
        TryDeleteDirectory(state.BackupDir);
        TryDeleteEmptyDirectory(Path.Combine(state.DataDir, "updates", "work"));
        var failed = state with
        {
            Status = PortableUpdateStatus.Failed,
            UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Error = message
        };
        PortableUpdateStateStore.Save(state.DataDir, failed);
        return new PortableUpdateResult(false, false,
            $"{message} No application files were changed.",
            PortableUpdateStateStore.PathFor(state.DataDir), state.BackupDir, failed.Status);
    }

    public static async Task<PortableUpdateResult> RecoverAsync(
        string dataDirectory,
        bool launchApp = true,
        CancellationToken cancellationToken = default)
    {
        using var updateLock = TryAcquireUpdateLock(dataDirectory);
        if (updateLock == null)
        {
            return Failure("Another AMARE update process is already running.");
        }
        var state = PortableUpdateStateStore.Load(dataDirectory);
        if (state == null)
        {
            return Failure("No update state was found.");
        }
        if (state.Status == PortableUpdateStatus.Succeeded)
        {
            TryCleanup(state);
            return new PortableUpdateResult(true, false, null,
                PortableUpdateStateStore.PathFor(dataDirectory), state.BackupDir, state.Status);
        }
        if (!Directory.Exists(state.BackupDir))
        {
            var current = ReadVersion(state.AppDir);
            if (current != null && SemanticVersion.Compare(current, state.Version) == 0)
            {
                var completed = state with
                {
                    Status = PortableUpdateStatus.Succeeded,
                    UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Error = null
                };
                PortableUpdateStateStore.Save(dataDirectory, completed);
                TryCleanup(completed);
                return new PortableUpdateResult(true, false, null,
                    PortableUpdateStateStore.PathFor(dataDirectory), state.BackupDir, completed.Status);
            }
            return new PortableUpdateResult(false, false,
                "Update backup is missing; automatic recovery is not possible.",
                PortableUpdateStateStore.PathFor(dataDirectory), state.BackupDir, state.Status);
        }
        if (state.RecoveryAttempts >= MaxRecoveryAttempts)
        {
            var blocked = state with
            {
                Status = PortableUpdateStatus.Failed,
                UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Error = $"Recovery attempt limit ({MaxRecoveryAttempts}) reached. " +
                        "The previous version backup was kept."
            };
            PortableUpdateStateStore.Save(dataDirectory, blocked);
            return new PortableUpdateResult(false, false, blocked.Error,
                PortableUpdateStateStore.PathFor(dataDirectory), state.BackupDir, blocked.Status);
        }
        var rollback = await RollbackAsync(state, launchApp, cancellationToken);
        return new PortableUpdateResult(rollback.RolledBack, rollback.RolledBack,
            rollback.RolledBack ? null : rollback.Error,
            PortableUpdateStateStore.PathFor(dataDirectory), state.BackupDir, rollback.Status);
    }

    public static void TryCleanup(PortableUpdateState state, bool deleteHealthMarker = true)
    {
        TryDeleteDirectory(state.StagingDir);
        TryDeleteDirectory(state.BackupDir);
        TryDeleteEmptyDirectory(Path.Combine(state.DataDir, "updates", "work"));
        if (deleteHealthMarker) TryDeleteMarker(state.HealthMarkerPath);
        try
        {
            var updatesRoot = Path.Combine(state.DataDir, "updates");
            var package = Path.GetFullPath(state.PackagePath);
            if (package.StartsWith(Path.GetFullPath(updatesRoot) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                && File.Exists(package))
            {
                File.Delete(package);
            }
        }
        catch
        {
            // A stale downloaded package is safe and can be cleaned next time.
        }
    }

    private static async Task<(bool RolledBack, string? Error, string? Status)> RollbackAsync(
        PortableUpdateState state,
        bool launchApp,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var attempts = state.RecoveryAttempts + 1;
        var rolling = state with
        {
            Status = PortableUpdateStatus.RollingBack,
            UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Error = null,
            RecoveryAttempts = attempts
        };
        PortableUpdateStateStore.Save(state.DataDir, rolling);
        try
        {
            if (!Directory.Exists(state.BackupDir))
            {
                throw new DirectoryNotFoundException("Backup directory is missing.");
            }
            var backupEntry = Path.Combine(state.BackupDir, state.EntryPoint);
            if (!File.Exists(backupEntry))
            {
                throw new FileNotFoundException(
                    "Rollback backup is incomplete; the previous executable is missing.");
            }
            RemoveStagedNewFiles(state);
            CopyDirectoryReplacing(state.BackupDir, state.AppDir, state.EntryPoint);
            if (!File.Exists(Path.Combine(state.AppDir, state.EntryPoint)))
            {
                throw new IOException("Rollback did not restore the previous executable.");
            }
            var rolledBack = rolling with
            {
                Status = PortableUpdateStatus.RolledBack,
                UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            PortableUpdateStateStore.Save(state.DataDir, rolledBack);
            TryDeleteDirectory(state.StagingDir);
            TryDeleteDirectory(state.BackupDir);
            TryDeleteEmptyDirectory(Path.Combine(state.DataDir, "updates", "work"));
            TryDeleteMarker(state.HealthMarkerPath);
            if (launchApp) TryStartProcess(Path.Combine(state.AppDir, state.EntryPoint), state.AppDir);
            return (true, null, rolledBack.Status);
        }
        catch (Exception exception)
        {
            var failed = rolling with
            {
                Status = PortableUpdateStatus.Failed,
                UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Error = exception.Message
            };
            PortableUpdateStateStore.Save(state.DataDir, failed);
            return (false, exception.Message, failed.Status);
        }
    }

    private static void EnsureReplaceable(string staging, string appDirectory, string entryPoint)
    {
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, file);
            var target = Path.GetFullPath(Path.Combine(appDirectory, relative));
            var appRoot = Path.GetFullPath(appDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!target.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Update file would escape the application directory: {relative}");
            }
            if (!File.Exists(target)) continue;
            try
            {
                using var stream = new FileStream(
                    target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"AMARE file is in use and cannot be replaced: {relative}", exception);
            }
        }
    }

    private static void ApplyStaging(string staging, string appDirectory, string entryPoint)
    {
        var entry = Path.GetFullPath(Path.Combine(staging, entryPoint));
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFullPath(file).Equals(entry, StringComparison.OrdinalIgnoreCase)) continue;
            CopyIntoPlace(file, Path.GetRelativePath(staging, file), appDirectory);
        }
        if (File.Exists(entry))
        {
            CopyIntoPlace(entry, entryPoint, appDirectory);
        }
    }

    private static void CopyIntoPlace(string source, string relative, string appDirectory)
    {
        var target = Path.GetFullPath(Path.Combine(appDirectory, relative));
        var appRoot = Path.GetFullPath(appDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!target.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Update file would escape the application directory: {relative}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".amare-new-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary, overwrite: false);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static void CopyDirectoryReplacing(string source, string destination, string lastFile)
    {
        var last = Path.GetFullPath(Path.Combine(source, lastFile));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFullPath(file).Equals(last, StringComparison.OrdinalIgnoreCase)) continue;
            CopyIntoPlace(file, Path.GetRelativePath(source, file), destination);
        }
        if (File.Exists(last))
        {
            CopyIntoPlace(last, lastFile, destination);
        }
    }

    private static void RemoveStagedNewFiles(PortableUpdateState state)
    {
        if (!Directory.Exists(state.StagingDir) || !Directory.Exists(state.AppDir)) return;
        var appRoot = Path.GetFullPath(state.AppDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(state.StagingDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(state.StagingDir, file);
            if (File.Exists(Path.Combine(state.BackupDir, relative))) continue;
            var target = Path.GetFullPath(Path.Combine(state.AppDir, relative));
            if (!target.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase)) continue;
            TryDeleteFile(target);
        }
    }

    private static async Task<bool> VerifyNewVersionAsync(
        PortableUpdateRequest request,
        string marker,
        CancellationToken cancellationToken)
    {
        TryDeleteMarker(marker);
        var exe = Path.Combine(request.AppDir, request.EntryPoint);
        Process? process;
        try
        {
            process = StartUpdateProcess(exe, request.AppDir, marker);
        }
        catch
        {
            return false;
        }
        if (process == null) return false;
        var healthy = false;
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, request.HealthTimeoutSeconds));
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (File.Exists(marker))
                {
                    healthy = true;
                    return true;
                }
                if (process.HasExited)
                {
                    return false;
                }
                await Task.Delay(250, cancellationToken);
            }
            return false;
        }
        finally
        {
            try
            {
                if (!healthy && !process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The child may have already exited.
            }
        }
    }

    private static Process? StartUpdateProcess(string exe, string workingDirectory, string marker)
    {
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        info.Environment[HealthMarkerEnvironmentVariable] = marker;
        return Process.Start(info);
    }

    private static void TryStartProcess(string exe, string workingDirectory)
    {
        try
        {
            if (!File.Exists(exe)) return;
            Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            });
        }
        catch
        {
            // The caller records rollback success even if relaunch is blocked.
        }
    }

    private static async Task<bool> WaitForParentExitAsync(int parentPid, CancellationToken cancellationToken)
    {
        if (parentPid <= 0) return true;
        Process parent;
        try
        {
            parent = Process.GetProcessById(parentPid);
        }
        catch (ArgumentException)
        {
            return true;
        }
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(ParentExitTimeoutSeconds);
            while (!parent.HasExited && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(200, cancellationToken);
            }
            return parent.HasExited;
        }
        finally
        {
            parent.Dispose();
        }
    }

    private static string? ReadVersion(string appDirectory)
    {
        try
        {
            var versionFile = Path.Combine(appDirectory, "VERSION");
            if (File.Exists(versionFile))
            {
                var text = File.ReadAllText(versionFile).Trim();
                if (SemanticVersion.TryParse(text, out _)) return text.TrimStart('v', 'V');
            }
            var exe = Path.Combine(appDirectory, "AMARE.Windows.exe");
            if (File.Exists(exe))
            {
                var value = FileVersionInfo.GetVersionInfo(exe).FileVersion;
                if (SemanticVersion.TryParse(value, out _)) return value!.TrimStart('v', 'V');
            }
        }
        catch
        {
            // Unknown previous version is recoverable from the backup directory.
        }
        return null;
    }

    private static void CopyDirectory(string source, string destination, bool overwrite)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static PortableUpdateResult Failure(string error, PortableUpdateState? state = null) =>
        new(false, false, error,
            state == null ? null : PortableUpdateStateStore.PathFor(state.DataDir),
            state?.BackupDir, state?.Status);

    private static FileStream? TryAcquireUpdateLock(string dataDirectory)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var path = Path.Combine(dataDirectory, "update.lock");
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void CleanupStaleWork(string dataDirectory, PortableUpdateState? state)
    {
        var work = Path.Combine(dataDirectory, "updates", "work");
        if (!Directory.Exists(work)) return;
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state != null)
        {
            protectedPaths.Add(Path.GetFullPath(state.BackupDir));
            protectedPaths.Add(Path.GetFullPath(state.StagingDir));
        }
        foreach (var directory in Directory.EnumerateDirectories(work))
        {
            if (protectedPaths.Contains(Path.GetFullPath(directory))) continue;
            TryDeleteDirectory(directory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort; recovery can remove stale work directories later.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A temporary replace file is safe to clean next time.
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // Best effort; an empty work directory is harmless.
        }
    }

    private static void TryDeleteMarker(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort only.
        }
    }
}
