using System.Text;

namespace Amare.Windows.Core;

/// <summary>
/// Small append-only local log used by the Windows RC. It deliberately avoids
/// page URLs, page content, secrets, and search queries.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string? LogPath => _path;

    public static void Initialize(string appDataRoot)
    {
        if (string.IsNullOrWhiteSpace(appDataRoot))
        {
            throw new ArgumentException("App data root is required.", nameof(appDataRoot));
        }
        var directory = Path.Combine(appDataRoot, "logs");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"amare-{DateTime.UtcNow:yyyyMMdd}.log");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception exception) =>
        Write("ERROR", $"{message}: {exception.GetType().Name}: {exception.Message}");

    private static void Write(string level, string message)
    {
        if (_path is null)
        {
            return;
        }
        var line = $"{DateTime.UtcNow:O} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            File.AppendAllText(_path, line, Encoding.UTF8);
        }
    }
}
