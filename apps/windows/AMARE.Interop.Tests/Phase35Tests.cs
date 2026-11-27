using Amare.Windows.Core;

namespace Amare.Interop.Tests;

internal static class Phase35Tests
{
    public static void Register()
    {
        Program.Run("backup_helper_create_verify_windows", CreateVerify);
        Program.Run("backup_helper_password_rejection_windows", PasswordRejection);
    }

    private static string CreateVerify()
    {
        var helper = Environment.GetEnvironmentVariable("AMARE_BACKUP_HELPER");
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(helper) || !File.Exists(helper))
        {
            return "UNVERIFIED: Windows backup helper was not available.";
        }
        using var temp = new TempTree();
        var root = Path.Combine(temp.Root, "data");
        using var native = AmareNative.Create(root);
        var profiles = new ProfileClient(native);
        var settings = new SettingsClient(native);
        var context = profiles.InitializeAsync().GetAwaiter().GetResult();
        settings.SetAsync(context, "theme", SettingValueDto.Text("dark"))
            .GetAwaiter().GetResult();

        var client = new BackupClient(root, helper);
        var path = Path.Combine(temp.Root, "manual.amarebak");
        var created = client.CreateAsync(path, context.ProfileId, includeVault: false, password: null, vaultPassword: null)
            .GetAwaiter().GetResult();
        Require(created.Succeeded, created.Error ?? created.Status);
        Require(File.Exists(path), "backup file missing");
        Require(created.Sha256?.Length == 64, "backup SHA-256 missing");

        var verified = client.VerifyAsync(path, null).GetAwaiter().GetResult();
        Require(verified.Valid, verified.Error ?? verified.Status);
        Require(verified.Manifest?.Profiles.Count == 1, "manifest profile count");
        Require(verified.Manifest?.Profiles[0].Id == context.ProfileId, "manifest profile id");
        Require(verified.Manifest?.ContainsVault == false, "unexpected Vault flag");
        return "C# BackupClient invoked the Rust helper, created and verified a backup";
    }

    private static string PasswordRejection()
    {
        var helper = Environment.GetEnvironmentVariable("AMARE_BACKUP_HELPER");
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(helper) || !File.Exists(helper))
        {
            return "UNVERIFIED: Windows backup helper was not available.";
        }
        using var temp = new TempTree();
        var root = Path.Combine(temp.Root, "data");
        using var native = AmareNative.Create(root);
        var profiles = new ProfileClient(native);
        var context = profiles.InitializeAsync().GetAwaiter().GetResult();
        var client = new BackupClient(root, helper);
        var path = Path.Combine(temp.Root, "encrypted.amarebak");
        var created = client.CreateAsync(
            path, context.ProfileId, includeVault: false,
            password: "correct-pass", vaultPassword: null)
            .GetAwaiter().GetResult();
        Require(created.Succeeded, created.Error ?? created.Status);

        var wrong = client.VerifyAsync(path, "wrong-pass").GetAwaiter().GetResult();
        Require(wrong.Status == "InvalidPassword", $"expected InvalidPassword, got {wrong.Status}");
        var valid = client.VerifyAsync(path, "correct-pass").GetAwaiter().GetResult();
        Require(valid.Valid, valid.Error ?? valid.Status);
        return "encrypted backup rejects wrong password and accepts the correct one";
    }

    private static void Require(bool condition, string? message)
    {
        if (!condition) throw new InvalidOperationException(message ?? "assertion failed");
    }

    private sealed class TempTree : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "amare-backup-csharp", Guid.NewGuid().ToString("N"));
        public TempTree() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
