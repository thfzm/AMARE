using System.IO;
using Microsoft.UI.Xaml;

namespace Amare.Windows;

public partial class App : Application
{
    private Window? _window;
    private static readonly string StartupLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AMARE", "logs", "startup-error.log");

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            WriteStartupError("Application.UnhandledException", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteStartupError("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            WriteStartupError("TaskScheduler.UnobservedTaskException", args.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            WriteStartupError("OnLaunched", null, "starting MainWindow");
            _window = new MainWindow();
            _window.Activate();
            WriteStartupError("OnLaunched", null, "MainWindow activated");
        }
        catch (Exception exception)
        {
            WriteStartupError("OnLaunched", exception);
            throw;
        }
    }

    private static void WriteStartupError(string source, Exception? exception, string? detail = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupLogPath)!);
            File.AppendAllText(StartupLogPath,
                $"[{DateTimeOffset.UtcNow:O}] {source} {detail}{Environment.NewLine}" +
                (exception is null ? "" : exception + Environment.NewLine));
        }
        catch
        {
            // Startup diagnostics must never mask the original failure.
        }
    }
}
