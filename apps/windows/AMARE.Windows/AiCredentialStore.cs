using System.Runtime.InteropServices;
using System.Text;
using Amare.Windows.Core;

namespace Amare.Windows;

/// <summary>
/// Stores the OpenAI API key in Windows Credential Manager as a generic
/// credential. The key never enters a settings file, SQLite, a log, or the
/// AI WebView. The credential is scoped to the current Windows user.
/// </summary>
internal static class AiCredentialStore
{
    private const string Target = "AMARE.AI.OpenAI";
    private const string UserName = "openai";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public static string? GetKey()
    {
        if (!CredRead(Target, CredTypeGeneric, 0, out var pointer))
        {
            return null;
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                var value = Encoding.Unicode.GetString(bytes);
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static bool Save(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        var bytes = Encoding.Unicode.GetBytes(key);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = Target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = UserName
            };
            var written = CredWrite(ref credential, 0);
            if (!written)
            {
                AppLog.Warn($"AI credential save failed code={Marshal.GetLastWin32Error()}");
            }
            return written;
        }
        finally
        {
            Array.Clear(bytes);
            if (blob != IntPtr.Zero)
            {
                for (var index = 0; index < bytes.Length; index++)
                {
                    Marshal.WriteByte(blob, index, 0);
                }
                Marshal.FreeHGlobal(blob);
            }
        }
    }

    public static bool Delete()
    {
        if (CredDelete(Target, CredTypeGeneric, 0))
        {
            return true;
        }
        var code = Marshal.GetLastWin32Error();
        if (code == ErrorNotFound)
        {
            return true;
        }
        AppLog.Warn($"AI credential delete failed code={code}");
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
