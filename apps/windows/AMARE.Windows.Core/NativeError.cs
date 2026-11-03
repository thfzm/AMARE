namespace Amare.Windows.Core;

public enum NativeError
{
    InvalidArgument = 1,
    Panic = 2,
    Internal = 3,
    NotFound = 4,
    ProfileSwitched = 5,
    NoActiveProfile = 6,
    LastProfile = 7,
    Settings = 8,
    Search = 9,
    Store = 10,
    Json = 11,
    Unsupported = 12,
    Unknown = 99
}

public static class NativeErrorMapper
{
    public static NativeError FromCode(int code) => code switch
    {
        1 => NativeError.InvalidArgument,
        2 => NativeError.Panic,
        3 => NativeError.Internal,
        4 => NativeError.NotFound,
        5 => NativeError.ProfileSwitched,
        6 => NativeError.NoActiveProfile,
        7 => NativeError.LastProfile,
        8 => NativeError.Settings,
        9 => NativeError.Search,
        10 => NativeError.Store,
        11 => NativeError.Json,
        12 => NativeError.Unsupported,
        _ => NativeError.Unknown
    };
}

public sealed class NativeException : Exception
{
    public NativeException(NativeError error, string message, string? method = null)
        : base(message)
    {
        Error = error;
        Method = method;
    }

    public NativeError Error { get; }
    public string? Method { get; }
}
