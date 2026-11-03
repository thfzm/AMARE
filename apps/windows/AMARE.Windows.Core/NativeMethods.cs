using System.Runtime.InteropServices;

namespace Amare.Windows.Core;

public static class NativeMethods
{
    private const string Library = "amare_ffi";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint amare_services_abi_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int amare_services_create(IntPtr rootDir, out IntPtr services);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void amare_services_destroy(IntPtr services);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int amare_services_call(IntPtr services, IntPtr requestJson, out IntPtr outputJson);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int amare_core_last_error(out IntPtr output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void amare_string_free(IntPtr value);
}
