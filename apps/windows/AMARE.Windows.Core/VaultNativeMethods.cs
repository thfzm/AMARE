using System.Runtime.InteropServices;

namespace Amare.Windows.Core;

/// <summary>
/// ABI 2 entry points. Kept separate from <see cref="NativeMethods"/> (ABI 1)
/// so the two contracts can be loaded independently.
/// </summary>
internal static class VaultNativeMethods
{
    private const string Library = "amare_ffi";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint amare_services_abi2_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int amare_services_abi2_create(IntPtr rootDir, out IntPtr services);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void amare_services_abi2_destroy(IntPtr services);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int amare_services_abi2_call(
        IntPtr services, IntPtr requestJson, out IntPtr outputJson);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int amare_services_abi2_last_error(out IntPtr output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void amare_string_free(IntPtr value);
}

public enum VaultErrorCode
{
    Ok = 0,
    Panic = 2,
    Locked = 20,
    Corrupt = 21,
    Auth = 22,
    Conflict = 23,
    Unsupported = 24,
    KeyUnavailable = 25,
    InvalidJson = 26,
    Internal = 27,
    InvalidArgument = 28,
    Profile = 29,
    NotFound = 30,
    OriginMismatch = 31,
    Unknown = 99
}

public static class VaultErrorMapper
{
    public static VaultErrorCode FromCode(int code) => code switch
    {
        0 => VaultErrorCode.Ok,
        2 => VaultErrorCode.Panic,
        20 => VaultErrorCode.Locked,
        21 => VaultErrorCode.Corrupt,
        22 => VaultErrorCode.Auth,
        23 => VaultErrorCode.Conflict,
        24 => VaultErrorCode.Unsupported,
        25 => VaultErrorCode.KeyUnavailable,
        26 => VaultErrorCode.InvalidJson,
        27 => VaultErrorCode.Internal,
        28 => VaultErrorCode.InvalidArgument,
        29 => VaultErrorCode.Profile,
        30 => VaultErrorCode.NotFound,
        31 => VaultErrorCode.OriginMismatch,
        _ => VaultErrorCode.Unknown
    };
}

public sealed class VaultException : Exception
{
    public VaultException(VaultErrorCode code, string message, string? method = null)
        : base(message)
    {
        Code = code;
        Method = method;
    }

    public VaultErrorCode Code { get; }
    public string? Method { get; }
}
