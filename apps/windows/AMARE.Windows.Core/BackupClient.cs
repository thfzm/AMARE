using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Amare.Windows.Core;

/// <summary>
/// Invokes the standalone Rust backup helper. Passwords are written to the
/// helper's stdin only; they never appear in process arguments, logs or files.
/// </summary>
public sealed class BackupClient
{
    public const string HelperName = "AMARE.Backup.exe";

    private readonly string _root;
    private readonly string _helperPath;
    private readonly string? _appRoot;

    public BackupClient(
        string rootDirectory,
        string? helperPath = null,
        string? appRoot = null)
    {
        _root = Path.GetFullPath(rootDirectory);
        _helperPath = helperPath
            ?? Path.Combine(AppContext.BaseDirectory, HelperName);
        _appRoot = appRoot is null ? null : Path.GetFullPath(appRoot);
    }

    public string HelperPath => _helperPath;

    public static bool IsHelperAvailable(string? helperPath = null) =>
        File.Exists(helperPath ?? Path.Combine(AppContext.BaseDirectory, HelperName));

    public async Task<BackupOperationResult> CreateAsync(
        string destinationPath,
        string profileId,
        bool includeVault,
        string? password,
        bool includeRoot = false,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "create", "--root", _root, "--out", Path.GetFullPath(destinationPath)
        };
        if (_appRoot != null)
        {
            arguments.Add("--app-root");
            arguments.Add(_appRoot);
        }
        if (includeRoot)
        {
            arguments.Add("--include-root");
        }
        else
        {
            arguments.Add("--profiles");
            arguments.Add(profileId);
        }
        if (includeVault)
        {
            arguments.Add("--include-vault");
        }
        if (!string.IsNullOrEmpty(password))
        {
            arguments.Add("--password-stdin");
        }
        return await RunAsync(arguments, password, cancellationToken).ConfigureAwait(false);
    }

    public Task<BackupOperationResult> RecoverAsync(CancellationToken cancellationToken = default) =>
        RunAsync(new List<string> { "recover", "--root", _root }, null, cancellationToken);

    public Task<BackupOperationResult> VerifyAsync(
        string backupPath,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "verify", "--root", _root, "--backup", Path.GetFullPath(backupPath)
        };
        if (!string.IsNullOrEmpty(password))
        {
            arguments.Add("--password-stdin");
        }
        return RunAsync(arguments, password, cancellationToken);
    }

    /// <summary>
    /// Start a restore helper. The helper waits for this process to exit, so the
    /// caller must close the application immediately after this returns.
    /// </summary>
    public Process StartRestore(
        string backupPath,
        string sourceProfileId,
        string targetProfileId,
        string appExecutable,
        string? password,
        bool fullRestore = false,
        string? openProfileId = null)
    {
        var arguments = new List<string>
        {
            "restore", "--root", _root, "--backup", Path.GetFullPath(backupPath)
        };
        if (fullRestore)
        {
            arguments.Add("--mode");
            arguments.Add("full");
        }
        else
        {
            arguments.Add("--mode");
            arguments.Add("profile");
            arguments.Add("--map");
            arguments.Add($"{sourceProfileId}:{targetProfileId}");
        }
        arguments.Add("--parent-pid");
        arguments.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("--app-exe");
        arguments.Add(Path.GetFullPath(appExecutable));
        if (!string.IsNullOrWhiteSpace(openProfileId))
        {
            arguments.Add("--open-profile");
            arguments.Add(openProfileId);
        }
        if (_appRoot != null)
        {
            arguments.Add("--app-root");
            arguments.Add(_appRoot);
        }
        arguments.Add("--result");
        arguments.Add(RestoreResultPath);
        if (!string.IsNullOrEmpty(password))
        {
            arguments.Add("--password-stdin");
        }
        var process = Start(arguments);
        WritePassword(process, password);
        return process;
    }

    private async Task<BackupOperationResult> RunAsync(
        List<string> arguments,
        string? password,
        CancellationToken cancellationToken)
    {
        var resultPath = ResultPath(arguments[0]);
        arguments.Add("--result");
        arguments.Add(resultPath);
        using var process = Start(arguments);
        WritePassword(process, password);
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The helper may have exited between the check and the kill.
            }
        });
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(resultPath);
            throw;
        }
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        var result = ReadResult(resultPath);
        TryDelete(resultPath);
        if (result != null)
        {
            return result;
        }
        return new BackupOperationResult
        {
            Status = process.ExitCode == 0 ? "ok" : "error",
            Error = string.IsNullOrWhiteSpace(error)
                ? (string.IsNullOrWhiteSpace(output) ? $"Backup helper exited {process.ExitCode}." : output.Trim())
                : error.Trim()
        };
    }

    private Process Start(List<string> arguments)
    {
        if (!File.Exists(_helperPath))
        {
            throw new FileNotFoundException(
                "AMARE.Backup.exe is missing from this package. Reinstall a complete portable package.",
                _helperPath);
        }
        var info = new ProcessStartInfo(_helperPath)
        {
            WorkingDirectory = Path.GetDirectoryName(_helperPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        return Process.Start(info)
               ?? throw new InvalidOperationException("The backup helper could not be started.");
    }

    private static void WritePassword(Process process, string? password)
    {
        try
        {
            if (!string.IsNullOrEmpty(password))
            {
                process.StandardInput.WriteLine(password);
            }
        }
        finally
        {
            process.StandardInput.Close();
        }
    }

    private BackupOperationResult? ReadResult(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<BackupOperationResult>(
                File.ReadAllText(path), JsonDefaults.Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public string RestoreResultPath =>
        Path.Combine(_root, "backups", "restore-result.json");

    public string RestoreProgressPath =>
        Path.Combine(_root, "backups", "restore-progress.json");

    public BackupOperationResult? ReadRestoreResult()
    {
        try
        {
            if (!File.Exists(RestoreResultPath)) return null;
            return JsonSerializer.Deserialize<BackupOperationResult>(
                File.ReadAllText(RestoreResultPath), JsonDefaults.Options);
        }
        catch
        {
            return null;
        }
    }

    public BackupProgressDto? ReadRestoreProgress()
    {
        try
        {
            if (!File.Exists(RestoreProgressPath)) return null;
            return JsonSerializer.Deserialize<BackupProgressDto>(
                File.ReadAllText(RestoreProgressPath), JsonDefaults.Options);
        }
        catch
        {
            return null;
        }
    }

    private string ResultPath(string prefix)
    {
        var directory = Path.Combine(_root, "backups", ".ops");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{prefix}-{Guid.NewGuid():N}.json");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A stale result file is harmless and can be cleaned later.
        }
    }
}
