using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace WindowsVerify;

internal static class EnvironmentProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    public static EnvironmentInfo Capture(string testRoot)
    {
        _ = GetPhysicallyInstalledSystemMemory(out var memoryKb);
        return new EnvironmentInfo(
            Os: RuntimeInformation.OSDescription,
            OsVersion: Environment.OSVersion.VersionString,
            Architecture: RuntimeInformation.OSArchitecture.ToString(),
            Cpu: $"{Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")} / {Environment.ProcessorCount} logical processors",
            RamBytes: memoryKb * 1024UL,
            Gpu: GetGpuName(),
            DotNet: RuntimeInformation.FrameworkDescription,
            WebView2Version: GetWebView2Version(),
            TestRoot: testRoot,
            CapturedAt: DateTimeOffset.UtcNow);
    }

    private static string GetGpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            var names = new List<string>();
            foreach (ManagementObject item in searcher.Get())
            {
                var name = item["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            return names.Count > 0 ? string.Join("; ", names) : "UNKNOWN";
        }
        catch
        {
            return "UNKNOWN";
        }
    }

    private static string GetWebView2Version()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "NOT_FOUND";
        }
        catch
        {
            return "NOT_FOUND";
        }
    }
}
