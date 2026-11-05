using System.Diagnostics;
using Amare.Windows.Core;

namespace Amare.Updater;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: AMARE.Updater apply|recover [options]");
            return 2;
        }
        var options = Parse(args.Skip(1).ToArray());
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "apply" => ApplyAsync(options).GetAwaiter().GetResult(),
                "recover" => RecoverAsync(options).GetAwaiter().GetResult(),
                "validate" => Validate(options),
                _ => Usage("Unknown command.")
            };
        }
        catch (Exception exception)
        {
            Log(options, "fatal", exception.ToString());
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> ApplyAsync(Dictionary<string, string?> options)
    {
        var package = Required(options, "package");
        var appDir = Required(options, "app-dir");
        var dataDir = Required(options, "data-dir");
        var version = Required(options, "version");
        var sha256 = Required(options, "sha256");
        var request = new PortableUpdateRequest(
            PackagePath: package,
            AppDir: appDir,
            DataDir: dataDir,
            Version: version,
            ExpectedSha256: sha256,
            Architecture: Value(options, "architecture") ?? PortableUpdatePackage.DefaultArchitecture,
            EntryPoint: Value(options, "entry") ?? "AMARE.Windows.exe",
            ParentProcessId: int.TryParse(Value(options, "parent-pid"), out var parentPid) ? parentPid : 0,
            LaunchApp: !Has(options, "no-launch"),
            HealthMarkerPath: Value(options, "health-marker"));
        var result = await PortableUpdateEngine.ApplyAsync(request);
        Log(options, result.Succeeded ? "success" : "failure", result.Error ?? result.Status ?? "");
        if (!result.Succeeded)
        {
            Console.Error.WriteLine(result.Error);
            return 1;
        }
        return 0;
    }

    private static int Validate(Dictionary<string, string?> options)
    {
        var package = Required(options, "package");
        var version = Required(options, "version");
        var architecture = Value(options, "architecture") ?? PortableUpdatePackage.DefaultArchitecture;
        var result = PortableUpdatePackage.Validate(package, version, architecture);
        if (!result.Valid || result.Manifest == null)
        {
            Console.Error.WriteLine(result.Error ?? "Update package is invalid.");
            return 1;
        }
        Console.WriteLine($"valid {result.Manifest.Product} {result.Manifest.Version} {result.Manifest.Architecture}");
        return 0;
    }

    private static async Task<int> RecoverAsync(Dictionary<string, string?> options)
    {
        var dataDir = Required(options, "data-dir");
        var parentPid = int.TryParse(Value(options, "parent-pid"), out var pid) ? pid : 0;
        if (!await WaitForParentExitAsync(parentPid)) return 1;
        var result = await PortableUpdateEngine.RecoverAsync(dataDir,
            launchApp: !Has(options, "no-launch"));
        Log(options, result.Succeeded ? "recovered" : "recovery_failed", result.Error ?? result.Status ?? "");
        if (!result.Succeeded)
        {
            Console.Error.WriteLine(result.Error);
            return 1;
        }
        return 0;
    }

    private static async Task<bool> WaitForParentExitAsync(int parentPid)
    {
        if (parentPid <= 0) return true;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(parentPid);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (!process.HasExited && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static Dictionary<string, string?> Parse(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;
            var name = arg[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values[name] = args[++i];
            }
            else
            {
                values[name] = null;
            }
        }
        return values;
    }

    private static string Required(Dictionary<string, string?> options, string name) =>
        Value(options, name) ?? throw new ArgumentException($"--{name} is required.");

    private static string? Value(Dictionary<string, string?> options, string name) =>
        options.TryGetValue(name, out var value) ? value : null;

    private static bool Has(Dictionary<string, string?> options, string name) =>
        options.ContainsKey(name);

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine("Usage: AMARE.Updater apply --package <zip> --app-dir <dir> --data-dir <dir> --version <semver> --sha256 <hash> [--parent-pid N] [--no-launch]");
        Console.Error.WriteLine("       AMARE.Updater recover --data-dir <dir> [--parent-pid N] [--no-launch]");
        return 2;
    }

    private static void Log(Dictionary<string, string?> options, string status, string detail)
    {
        try
        {
            var dataDir = Value(options, "data-dir");
            if (string.IsNullOrWhiteSpace(dataDir)) return;
            var logDir = Path.Combine(dataDir, "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "updater.log"),
                $"[{DateTimeOffset.UtcNow:O}] {status} {detail}{Environment.NewLine}");
        }
        catch
        {
            // Logging must not change the update outcome.
        }
    }
}
