using System.Text.Json.Serialization;

namespace WindowsVerify;

internal static class ProbeStatus
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string Unverified = "UNVERIFIED";
    public const string Skipped = "SKIPPED";
}

internal sealed record ProbeResult(
    string Name,
    string Status,
    bool Executed,
    string Detail,
    DateTimeOffset Timestamp,
    object? Data = null);

internal sealed record EnvironmentInfo(
    string Os,
    string OsVersion,
    string Architecture,
    string Cpu,
    ulong RamBytes,
    string Gpu,
    string DotNet,
    string WebView2Version,
    string TestRoot,
    DateTimeOffset CapturedAt);

internal sealed record ProcessSample(
    int Pid,
    string Type,
    long WorkingSetBytes,
    string CommandLine);

internal sealed record MemorySnapshot(
    DateTimeOffset At,
    long TotalWorkingSetBytes,
    int ProcessCount,
    long BrowserBytes,
    long RendererBytes,
    long GpuBytes,
    long UtilityBytes,
    long OtherBytes,
    IReadOnlyList<ProcessSample> Processes,
    long TestToolBytes,
    long AmareMainBytes);

internal sealed record MemoryReport(
    int TabCount,
    MemorySnapshot Before,
    MemorySnapshot AfterEmpty,
    MemorySnapshot AfterCreate,
    MemorySnapshot AfterBackground,
    MemorySnapshot AfterSuspend,
    MemorySnapshot AfterClose,
    double AverageRendererBytesPerTab,
    bool Reclaimed,
    string Note);

internal sealed record RunReport(
    string SchemaVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    EnvironmentInfo Environment,
    IReadOnlyList<ProbeResult> Results,
    string Summary);
