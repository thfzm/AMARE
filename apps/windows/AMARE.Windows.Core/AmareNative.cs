using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amare.Windows.Core;

/// <summary>
/// Owns one native AmareServices handle. Calls are serialized and run off the
/// UI thread; the Rust service itself is also thread-safe, but a single
/// semaphore keeps shutdown and call lifetimes unambiguous.
/// </summary>
public sealed class AmareNative : INativeBridge
{
    public const uint ExpectedAbiVersion = 1;

    private readonly IntPtr _handle;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private AmareNative(IntPtr handle)
    {
        _handle = handle;
    }

    public static AmareNative Create(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Root directory is required.", nameof(rootDirectory));
        }

        Directory.CreateDirectory(rootDirectory);
        if (NativeMethods.amare_services_abi_version() != ExpectedAbiVersion)
        {
            throw new NativeException(NativeError.Unsupported,
                $"Unsupported service ABI version: {NativeMethods.amare_services_abi_version()}");
        }

        var rootPtr = MarshalStringUtf8(Path.GetFullPath(rootDirectory));
        try
        {
            var code = NativeMethods.amare_services_create(rootPtr, out var handle);
            if (code != 0 || handle == IntPtr.Zero)
            {
                throw new NativeException(NativeErrorMapper.FromCode(code), LastError());
            }
            return new AmareNative(handle);
        }
        finally
        {
            Marshal.FreeCoTaskMem(rootPtr);
        }
    }

    public async Task<JsonElement> CallAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => CallCore(method, parameters), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private JsonElement CallCore(string method, object? parameters)
    {
        var request = JsonSerializer.Serialize(new
        {
            version = ExpectedAbiVersion,
            method,
            @params = parameters ?? new { }
        }, JsonDefaults.Options);

        var requestPtr = MarshalStringUtf8(request);
        try
        {
            var code = NativeMethods.amare_services_call(_handle, requestPtr, out var outputPtr);
            if (code != 0)
            {
                throw new NativeException(NativeErrorMapper.FromCode(code), LastError(), method);
            }

            try
            {
                var json = outputPtr == IntPtr.Zero ? "null" : Marshal.PtrToStringUTF8(outputPtr);
                using var document = JsonDocument.Parse(json ?? "null");
                return document.RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new NativeException(NativeError.Json,
                    $"Native result was not valid JSON: {exception.Message}", method);
            }
            finally
            {
                if (outputPtr != IntPtr.Zero)
                {
                    NativeMethods.amare_string_free(outputPtr);
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(requestPtr);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _gate.Wait();
        try
        {
            NativeMethods.amare_services_destroy(_handle);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static IntPtr MarshalStringUtf8(string value)
    {
        return Marshal.StringToCoTaskMemUTF8(value);
    }

    private static string LastError()
    {
        var code = NativeMethods.amare_core_last_error(out var output);
        if (code != 0)
        {
            return $"last_error failed: {code}";
        }
        try
        {
            return output == IntPtr.Zero ? "unknown native error" : Marshal.PtrToStringUTF8(output) ?? "unknown native error";
        }
        finally
        {
            if (output != IntPtr.Zero)
            {
                NativeMethods.amare_string_free(output);
            }
        }
    }
}
