using Amare.Windows.Core;

namespace Amare.Interop.Tests;

internal static class Phase30Tests
{
    public static void Register(bool coreOnly)
    {
        if (coreOnly)
        {
            return;
        }
        Program.Run("permissions_client_crud_conflict_and_origin", ClientCrud);
    }

    private static string ClientCrud()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"amare-permissions-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var abi1 = AmareNative.Create(root);
            var context = new ProfileClient(abi1).InitializeAsync().GetAwaiter().GetResult();
            using var client = AmareVaultClient.Create(root);

            var empty = client.ListPermissionsAsync(context).GetAwaiter().GetResult();
            Assert(empty.Count == 0, "initial permission list");

            var created = client.SetPermissionAsync(
                context,
                "https://EXAMPLE.com:443/path",
                "camera",
                "allow",
                "persistent",
                0).GetAwaiter().GetResult();
            Assert(created.Origin == "https://example.com", created.Origin);
            Assert(created.Revision == 1 && created.Decision == "allow", "created permission");

            var found = client.GetPermissionAsync(context, "https://example.com", "camera")
                .GetAwaiter().GetResult();
            Assert(found is not null && found.Kind == "camera", "get permission");

            var updated = client.SetPermissionAsync(
                context,
                "https://example.com",
                "camera",
                "deny",
                "persistent",
                1).GetAwaiter().GetResult();
            Assert(updated.Revision == 2 && updated.Decision == "deny", "updated permission");

            var stale = AssertThrows<VaultException>(() => client.SetPermissionAsync(
                context,
                "https://example.com",
                "camera",
                "allow",
                "persistent",
                1).GetAwaiter().GetResult());
            Assert(stale.Code == VaultErrorCode.PermissionConflict, stale.Code.ToString());

            var invalid = AssertThrows<VaultException>(() => client.SetPermissionAsync(
                context,
                "http://example.com",
                "camera",
                "allow",
                "persistent",
                0).GetAwaiter().GetResult());
            Assert(invalid.Code == VaultErrorCode.PermissionInvalid, invalid.Code.ToString());

            var session = AssertThrows<VaultException>(() => client.SetPermissionAsync(
                context,
                "https://example.com",
                "microphone",
                "allow",
                "session",
                0).GetAwaiter().GetResult());
            Assert(session.Code == VaultErrorCode.PermissionInvalid, session.Code.ToString());

            var wrongDelete = AssertThrows<VaultException>(() =>
                client.DeletePermissionAsync(context, "https://example.com", "camera", 1)
                    .GetAwaiter().GetResult());
            Assert(wrongDelete.Code == VaultErrorCode.PermissionConflict, wrongDelete.Code.ToString());

            var deleted = client.DeletePermissionAsync(
                context, "https://example.com", "camera", 2).GetAwaiter().GetResult();
            Assert(deleted.Deleted, "delete permission");
            Assert(client.ListPermissionsAsync(context).GetAwaiter().GetResult().Count == 0,
                "permission list after delete");

            return "ABI 2 permissions client CRUD/conflict/origin PASS";
        }
        catch (VaultException exception) when (!OperatingSystem.IsWindows()
            && exception.Code == VaultErrorCode.Unsupported)
        {
            return "ABI 2 permissions create returned Unsupported on non-Windows";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
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
        throw new InvalidOperationException($"expected {typeof(T).Name}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
