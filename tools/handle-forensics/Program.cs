using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

if (args.Length == 1 && args[0] is "--help" or "-h")
{
    Console.WriteLine("HandleForensics --pid <pid> [--duration <seconds>] [--interval <seconds>] [--out <path>] [--self]");
    return 0;
}

var options = Parse(args);
var pid = options.TryGetValue("--pid", out var pidText) && int.TryParse(pidText, out var parsed)
    ? parsed
    : Environment.ProcessId;
var duration = options.TryGetValue("--duration", out var durationText) && double.TryParse(durationText, out var parsedDuration)
    ? Math.Max(0, parsedDuration)
    : 0;
var interval = options.TryGetValue("--interval", out var intervalText) && double.TryParse(intervalText, out var parsedInterval)
    ? Math.Max(1, parsedInterval)
    : 5;
var output = options.TryGetValue("--out", out var outputPath) ? outputPath : "";
var samples = new List<Snapshot>();
var started = Stopwatch.StartNew();
do
{
    var snapshot = Snapshot.Capture(pid);
    samples.Add(snapshot);
    Console.WriteLine($"{snapshot.Timestamp:O} pid={pid} handles={snapshot.Handles} types=[{snapshot.Types}] procs=[{snapshot.ProcessTargets}]");
    if (duration <= 0 || started.Elapsed.TotalSeconds >= duration) break;
    Thread.Sleep(TimeSpan.FromSeconds(interval));
} while (started.Elapsed.TotalSeconds < duration);

if (!string.IsNullOrWhiteSpace(output))
{
    var directory = Path.GetDirectoryName(output);
    if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    File.WriteAllText(output, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
    var csv = Path.ChangeExtension(output, ".csv");
    var lines = new List<string> { "timestamp,pid,handles,types,process_targets" };
    lines.AddRange(samples.Select(s => $"{s.Timestamp:O},{s.Pid},{s.Handles},\"{s.Types.Replace("\"", "\"\"")}\",\"{s.ProcessTargets.Replace("\"", "\"\"")}\""));
    File.WriteAllLines(csv, lines, new UTF8Encoding(false));
    Console.WriteLine($"Wrote {output} and {csv}");
}
return 0;

static Dictionary<string, string> Parse(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < values.Length; i++)
    {
        if (!values[i].StartsWith("--", StringComparison.Ordinal)) continue;
        var key = values[i];
        var value = i + 1 < values.Length && !values[i + 1].StartsWith("--", StringComparison.Ordinal) ? values[++i] : "true";
        result[key] = value;
    }
    return result;
}

internal sealed record Snapshot(
    DateTimeOffset Timestamp,
    int Pid,
    int Handles,
    string Types,
    string ProcessTargets)
{
    public static Snapshot Capture(int pid)
    {
        var names = ObjectTypes();
        var handles = new List<HandleEntry>();
        var status = ReadHandles(pid, handles);
        var typeCounts = handles.GroupBy(h => h.TypeIndex).ToDictionary(g => g.Key, g => g.Count());
        var typeText = string.Join(",", typeCounts.OrderByDescending(p => p.Value)
            .Take(10)
            .Select(p => $"{names.GetValueOrDefault(p.Key, $"type{p.Key}")}={p.Value}"));
        var processTargets = new Dictionary<string, int>();
        var processType = names.FirstOrDefault(p => string.Equals(p.Value, "Process", StringComparison.OrdinalIgnoreCase)).Key;
        var targetProcess = OpenProcess(ProcessAccess.DupHandle, false, pid);
        try
        {
            if (processType != 0 && targetProcess != IntPtr.Zero)
            {
                foreach (var handle in handles.Where(h => h.TypeIndex == processType))
                {
                    if (!DuplicateHandle(targetProcess, handle.Handle, GetCurrentProcess(), out var duplicate, 0, false, DuplicateSameAccess))
                        continue;
                    try
                    {
                        var targetPid = (int)GetProcessId(duplicate);
                        var key = targetPid == 0 ? "unknown" : ProcessName(targetPid);
                        processTargets[key] = processTargets.GetValueOrDefault(key) + 1;
                    }
                    finally { CloseHandle(duplicate); }
                }
            }
        }
        finally
        {
            if (targetProcess != IntPtr.Zero) CloseHandle(targetProcess);
        }
        var processText = string.Join(",", processTargets.OrderByDescending(p => p.Value).Take(10)
            .Select(p => $"{p.Key}={p.Value}"));
        return new Snapshot(DateTimeOffset.UtcNow, pid, handles.Count, typeText, processText);
    }

    private const uint DuplicateSameAccess = 0x00000002;
    private const int ObjectTypesInformation = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemHandleEntry
    {
        public IntPtr Object;
        public IntPtr ProcessId;
        public IntPtr Handle;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    private sealed record HandleEntry(IntPtr Handle, ushort TypeIndex);

    [StructLayout(LayoutKind.Explicit, Size = 0x68)]
    private struct ObjectTypeInformation
    {
        [FieldOffset(0x00)] public ushort NameLength;
        [FieldOffset(0x02)] public ushort NameMaximumLength;
        [FieldOffset(0x08)] public IntPtr NameBuffer;
        [FieldOffset(0x5A)] public byte TypeIndex;
    }

    private static Dictionary<ushort, string> ObjectTypes()
    {
        var result = new Dictionary<ushort, string>();
        var length = 1 << 16;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                var status = NtQueryObject(IntPtr.Zero, ObjectTypesInformation, buffer, length, out var returned);
                if (status == unchecked((int)0xC0000004))
                {
                    length = Math.Max(length * 2, returned);
                    continue;
                }
                if (status != 0) return result;
                var count = (uint)Marshal.ReadInt32(buffer);
                var entry = IntPtr.Add(buffer, 8);
                for (uint i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<ObjectTypeInformation>(entry);
                    if (info.NameLength > 0 && info.NameBuffer != IntPtr.Zero)
                    {
                        var name = Marshal.PtrToStringUni(info.NameBuffer, info.NameLength / 2) ?? "";
                        if (!string.IsNullOrWhiteSpace(name)) result[info.TypeIndex] = name;
                    }
                    var stride = (0x68 + info.NameMaximumLength + 7) & ~7;
                    entry = IntPtr.Add(entry, stride);
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return result;
    }

    private static int ReadHandles(int pid, List<HandleEntry> output)
    {
        var length = 1 << 20;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                var status = NtQuerySystemInformation(64, buffer, length, out var returned);
                if (status == unchecked((int)0xC0000004))
                {
                    length = Math.Max(length * 2, returned);
                    continue;
                }
                if (status != 0) return status;
                var count = Marshal.ReadIntPtr(buffer).ToInt64();
                var entrySize = Marshal.SizeOf<SystemHandleEntry>();
                var start = IntPtr.Add(buffer, 16);
                for (long i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<SystemHandleEntry>(IntPtr.Add(start, (int)(i * entrySize)));
                    if (entry.ProcessId.ToInt64() == pid) output.Add(new HandleEntry(entry.Handle, entry.ObjectTypeIndex));
                }
                return 0;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return unchecked((int)0xC0000004);
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return $"{process.ProcessName}({pid},start={process.StartTime:O})";
        }
        catch { return $"exited({pid})"; }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returned);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr info, int length, out int returned);
    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess, out IntPtr targetHandle, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern uint GetProcessId(IntPtr process);

    private static class ProcessAccess
    {
        public const uint DupHandle = 0x0040;
    }
}
