using Amare.Windows.Core;

namespace Amare.Interop.Tests;

internal static class Phase26Tests
{
    public static void Register(bool coreOnly)
    {
        if (coreOnly)
        {
            return;
        }
        Program.Run("vault_abi2_version_is_2", () =>
        {
            var version = AmareVaultClient.GetNativeAbiVersion();
            Assert(version == 2, $"ABI 2 version={version}");
            return $"abi={version}";
        });
        Program.Run("vault_abi2_error_mapping", () =>
        {
            Assert(VaultErrorMapper.FromCode(20) == VaultErrorCode.Locked, "locked");
            Assert(VaultErrorMapper.FromCode(21) == VaultErrorCode.Corrupt, "corrupt");
            Assert(VaultErrorMapper.FromCode(22) == VaultErrorCode.Auth, "auth");
            Assert(VaultErrorMapper.FromCode(23) == VaultErrorCode.Conflict, "conflict");
            Assert(VaultErrorMapper.FromCode(24) == VaultErrorCode.Unsupported, "unsupported");
            Assert(VaultErrorMapper.FromCode(25) == VaultErrorCode.KeyUnavailable, "key");
            Assert(VaultErrorMapper.FromCode(26) == VaultErrorCode.InvalidJson, "json");
            Assert(VaultErrorMapper.FromCode(28) == VaultErrorCode.InvalidArgument, "argument");
            return "ABI 2 codes mapped";
        });
        Program.Run("vault_abi2_platform_contract", PlatformContract);
    }

    private static string PlatformContract()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"amare-vault-abi2-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var abi1 = AmareNative.Create(root);
            var context = new ProfileClient(abi1).InitializeAsync().GetAwaiter().GetResult();
            try
            {
                using var vault = AmareVaultClient.Create(root);
                if (!OperatingSystem.IsWindows())
                {
                    throw new InvalidOperationException("non-Windows ABI 2 create unexpectedly succeeded");
                }
                var status = vault.InitializeAsync(context).GetAwaiter().GetResult();
                Assert(status.Unlocked && status.EntryCount == 0, "initialize");
                using var second = AmareVaultClient.Create(root);
                second.InitializeAsync(context).GetAwaiter().GetResult();
                var created = vault.CreateAsync(
                    context,
                    "https://bücher.example/login",
                    "한글",
                    "vault-secret",
                    "Example").GetAwaiter().GetResult();
                Assert(created.Origin == "https://xn--bcher-kva.example", created.Origin);
                var list = vault.ListAsync(context).GetAwaiter().GetResult();
                Assert(list.Count == 1, "list");
                Assert(list[0].Username == "한글", "utf8 username");
                var secret = vault.GetAsync(context, created.Id).GetAwaiter().GetResult();
                Assert(secret.Password == "vault-secret", "get password");
                secret.ClearPassword();
                var fill = vault.GetForFillAsync(
                    context, "https://xn--bcher-kva.example", created.Id).GetAwaiter().GetResult();
                Assert(fill.Password == "vault-secret", "fill password");
                fill.ClearPassword();
                var mismatch = AssertThrows<VaultException>(() =>
                    vault.GetForFillAsync(context, "https://evil.example", created.Id)
                        .GetAwaiter().GetResult());
                Assert(mismatch.Code == VaultErrorCode.OriginMismatch, mismatch.Code.ToString());
                Assert(!mismatch.Message.Contains("vault-secret", StringComparison.Ordinal),
                    "password leaked in error");

                vault.LockAsync(context).GetAwaiter().GetResult();
                var locked = AssertThrows<VaultException>(() =>
                    vault.ListAsync(context).GetAwaiter().GetResult());
                Assert(locked.Code == VaultErrorCode.Locked, locked.Code.ToString());
                vault.UnlockAsync(context).GetAwaiter().GetResult();

                var backup = vault.ExportBackupAsync(context, "backup-password")
                    .GetAwaiter().GetResult();
                var summary = vault.ValidateBackupAsync(
                    context, backup.PayloadHex, "backup-password").GetAwaiter().GetResult();
                Assert(summary.EntryCount == 1, "backup summary");
                vault.DeleteAsync(context, created.Id, 1).GetAwaiter().GetResult();
                var imported = vault.ImportBackupAsync(
                    context, backup.PayloadHex, "backup-password").GetAwaiter().GetResult();
                Assert(imported.EntryCount == 1, "import");

                var conflict = AssertThrows<VaultException>(() =>
                    second.CreateAsync(
                        context,
                        "https://second.example",
                        "bob",
                        "second-secret",
                        "Second").GetAwaiter().GetResult());
                Assert(conflict.Code == VaultErrorCode.Conflict, conflict.Code.ToString());
                return "ABI 2 Rust DLL + C# UTF-8/CRUD/backup/conflict PASS";
            }
            catch (VaultException exception) when (!OperatingSystem.IsWindows()
                && exception.Code == VaultErrorCode.Unsupported)
            {
                return "ABI 2 create returned Unsupported on non-Windows (Windows CI is authoritative)";
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static T AssertThrows<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
