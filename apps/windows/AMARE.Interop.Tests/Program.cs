using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Amare.Interop.Tests;

internal static class NativeMethods
{
    private const string Library = "amare_ffi";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr amare_core_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int amare_core_create(IntPtr configJson, out IntPtr core);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void amare_core_destroy(IntPtr core);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int amare_core_ping(IntPtr core, IntPtr input, out IntPtr output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int amare_core_last_error(out IntPtr output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void amare_string_free(IntPtr value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern long amare_core_add(long a, long b);
}

internal sealed record TestResult(string Name, bool Passed, string? Detail);

internal static class Program
{
    internal static readonly List<TestResult> Results = [];

    private static int Main()
    {
        Phase2Tests.Register();
        Phase21Tests.Register();
        Phase22Tests.Register();
        Phase23Tests.Register();

        Run("version", () =>
        {
            var ptr = NativeMethods.amare_core_version();
            Assert(ptr != IntPtr.Zero, "version pointer is null");
            var version = Marshal.PtrToStringUTF8(ptr);
            Assert(!string.IsNullOrWhiteSpace(version), "version is empty");
            return $"version={version}";
        });

        Run("create_ping_add_destroy", () =>
        {
            var code = NativeMethods.amare_core_create(IntPtr.Zero, out var core);
            Assert(code == 0, $"create failed: {code}");
            Assert(core != IntPtr.Zero, "core handle is null");
            try
            {
                var reply = Ping(core, "hello");
                Assert(reply is not null && reply.EndsWith("pong:hello", StringComparison.Ordinal),
                    $"unexpected ping reply: {reply}");

                var korean = Ping(core, "한글");
                Assert(korean is not null && korean.EndsWith("pong:한글", StringComparison.Ordinal),
                    $"unexpected UTF-8 reply: {korean}");

                Assert(NativeMethods.amare_core_add(20, 22) == 42, "add returned wrong value");
                return $"reply={reply}";
            }
            finally
            {
                NativeMethods.amare_core_destroy(core);
            }
        });

        Run("ffi_ping_latency_10000", () =>
        {
            var code = NativeMethods.amare_core_create(IntPtr.Zero, out var core);
            Assert(code == 0, $"create failed: {code}");
            try
            {
                for (var i = 0; i < 100; i++) Ping(core, "warmup");
                const int iterations = 10_000;
                var samples = new double[iterations];
                var total = Stopwatch.StartNew();
                for (var i = 0; i < iterations; i++)
                {
                    var start = Stopwatch.GetTimestamp();
                    _ = Ping(core, "latency");
                    samples[i] = (Stopwatch.GetTimestamp() - start) * 1_000_000.0 / Stopwatch.Frequency;
                }
                total.Stop();
                Array.Sort(samples);
                var avg = samples.Average();
                var p99 = samples[(int)(iterations * 0.99) - 1];
                var max = samples[^1];
                Assert(p99 < 5_000, $"p99 too high: {p99:F2} us");
                return $"iterations={iterations} avg_us={avg:F2} p99_us={p99:F2} max_us={max:F2} wall_ms={total.Elapsed.TotalMilliseconds:F1} (Linux host, not target hardware)";
            }
            finally
            {
                NativeMethods.amare_core_destroy(core);
            }
        });

        Run("null_input_reports_error", () =>
        {
            var code = NativeMethods.amare_core_create(IntPtr.Zero, out var core);
            Assert(code == 0, $"create failed: {code}");
            try
            {
                var pingCode = NativeMethods.amare_core_ping(core, IntPtr.Zero, out var output);
                Assert(pingCode == 1, $"expected invalid-argument code, got {pingCode}");
                Assert(output == IntPtr.Zero, "output should be null on error");
                var error = LastError();
                Assert(error is not null && error.Contains("null", StringComparison.OrdinalIgnoreCase),
                    $"unexpected error text: {error}");
                return $"error={error}";
            }
            finally
            {
                NativeMethods.amare_core_destroy(core);
            }
        });

        var failed = Results.Count(r => !r.Passed);
        foreach (var result in Results)
        {
            Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {result.Name}: {result.Detail}");
        }
        Console.WriteLine($"SUMMARY passed={Results.Count - failed} failed={failed}");

        var outputDir = Environment.GetEnvironmentVariable("AMARE_VERIFY_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            outputDir = Environment.CurrentDirectory;
        }
        Directory.CreateDirectory(outputDir);
        var jsonPath = Path.Combine(outputDir, "interop-results.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(Results, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        Console.WriteLine($"RESULTS {jsonPath}");
        return failed == 0 ? 0 : 1;
    }

    private static string? Ping(IntPtr core, string input)
    {
        var inputPtr = Marshal.StringToCoTaskMemUTF8(input);
        try
        {
            var code = NativeMethods.amare_core_ping(core, inputPtr, out var output);
            if (code != 0)
            {
                throw new InvalidOperationException($"ping failed: {code}; {LastError()}");
            }
            try
            {
                return Marshal.PtrToStringUTF8(output);
            }
            finally
            {
                NativeMethods.amare_string_free(output);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(inputPtr);
        }
    }

    private static string? LastError()
    {
        var code = NativeMethods.amare_core_last_error(out var output);
        if (code != 0)
        {
            return $"last_error failed: {code}";
        }
        try
        {
            return output == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(output);
        }
        finally
        {
            if (output != IntPtr.Zero)
            {
                NativeMethods.amare_string_free(output);
            }
        }
    }

    internal static void Run(string name, Func<string?> test)
    {
        try
        {
            var detail = test();
            Results.Add(new TestResult(name, true, detail));
        }
        catch (Exception exception)
        {
            Results.Add(new TestResult(name, false, exception.ToString()));
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
