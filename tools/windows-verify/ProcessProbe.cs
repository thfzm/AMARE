using System.Diagnostics;
using System.Management;

namespace WindowsVerify;

internal static class ProcessProbe
{
    private const string ProcessName = "msedgewebview2.exe";

    public static MemorySnapshot Capture()
    {
        var samples = new List<ProcessSample>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, CommandLine, WorkingSetSize FROM Win32_Process WHERE Name='{ProcessName}'");
            foreach (ManagementObject item in searcher.Get())
            {
                var pid = Convert.ToInt32(item["ProcessId"] ?? 0);
                var commandLine = item["CommandLine"]?.ToString() ?? "";
                var workingSet = Convert.ToInt64(item["WorkingSetSize"] ?? 0UL);
                samples.Add(new ProcessSample(pid, Classify(commandLine), workingSet, commandLine));
            }
        }
        catch
        {
            // Fallback: no command-line classification, but still record totals.
            foreach (var process in Process.GetProcessesByName("msedgewebview2"))
            {
                try
                {
                    samples.Add(new ProcessSample(process.Id, "unknown", process.WorkingSet64, ""));
                }
                catch
                {
                    // Access denied or process exited; keep the remaining samples.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        long Sum(string type) => samples.Where(s => s.Type == type).Sum(s => s.WorkingSetBytes);
        long testToolBytes = 0;
        long amareMainBytes = 0;
        try
        {
            using var current = Process.GetCurrentProcess();
            testToolBytes = current.WorkingSet64;
            foreach (var app in Process.GetProcessesByName("AMARE.Windows"))
            {
                try { amareMainBytes += app.WorkingSet64; }
                catch { }
                finally { app.Dispose(); }
            }
        }
        catch
        {
            // Process accounting is best effort.
        }
        return new MemorySnapshot(
            At: DateTimeOffset.UtcNow,
            TotalWorkingSetBytes: samples.Sum(s => s.WorkingSetBytes),
            ProcessCount: samples.Count,
            BrowserBytes: Sum("browser"),
            RendererBytes: Sum("renderer"),
            GpuBytes: Sum("gpu"),
            UtilityBytes: Sum("utility"),
            OtherBytes: Sum("other") + Sum("unknown"),
            Processes: samples,
            TestToolBytes: testToolBytes,
            AmareMainBytes: amareMainBytes);
    }

    private static string Classify(string commandLine)
    {
        if (commandLine.Contains("--type=gpu-process", StringComparison.OrdinalIgnoreCase)) return "gpu";
        if (commandLine.Contains("--type=renderer", StringComparison.OrdinalIgnoreCase)) return "renderer";
        if (commandLine.Contains("--type=utility", StringComparison.OrdinalIgnoreCase)) return "utility";
        if (string.IsNullOrWhiteSpace(commandLine)) return "unknown";
        return "browser";
    }
}
