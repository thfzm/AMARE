using System.Runtime.InteropServices;

namespace WindowsVerify;

internal static class NativeInput
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    public static uint LastInputTick()
    {
        var info = new LastInputInfo
        {
            cbSize = (uint)Marshal.SizeOf<LastInputInfo>(),
            dwTime = 0,
        };
        return GetLastInputInfo(ref info) ? info.dwTime : 0;
    }
}
