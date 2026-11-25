using System.Runtime.InteropServices;
using System.Text.Json;

namespace Amare.Windows.Core;

/// <summary>
/// ABI 2 vault client. This type owns one native vault-service handle and
/// serializes calls through a semaphore. It never logs request bodies, keys or
/// password responses.
/// </summary>
public sealed class AmareVaultClient : IDisposable
{
    public const uint ExpectedAbiVersion = 2;

    private readonly IntPtr _handle;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private AmareVaultClient(IntPtr handle) => _handle = handle;

    public static uint GetNativeAbiVersion() => VaultNativeMethods.amare_services_abi2_version();

    public static AmareVaultClient Create(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Root directory is required.", nameof(rootDirectory));
        }
        Directory.CreateDirectory(rootDirectory);
        try
        {
            var nativeVersion = VaultNativeMethods.amare_services_abi2_version();
            if (nativeVersion != ExpectedAbiVersion)
            {
                throw new VaultException(VaultErrorCode.Unsupported,
                    $"Unsupported vault ABI version: {nativeVersion}");
            }
            var rootPtr = Marshal.StringToCoTaskMemUTF8(Path.GetFullPath(rootDirectory));
            try
            {
                var code = VaultNativeMethods.amare_services_abi2_create(rootPtr, out var handle);
                if (code != 0 || handle == IntPtr.Zero)
                {
                    throw Failure(code, null);
                }
                return new AmareVaultClient(handle);
            }
            finally
            {
                Marshal.FreeCoTaskMem(rootPtr);
            }
        }
        catch (Exception exception) when (IsNativeUnavailable(exception))
        {
            throw new VaultException(VaultErrorCode.Unsupported,
                "Vault native library is unavailable");
        }
    }

    public Task<VaultStatusDto> InitializeAsync(
        ProfileContextDto context, CancellationToken cancellationToken = default) =>
        CallAsync<VaultStatusDto>("vault.initialize", new { context }, cancellationToken);

    public Task<VaultStatusDto> UnlockAsync(
        ProfileContextDto context, CancellationToken cancellationToken = default) =>
        CallAsync<VaultStatusDto>("vault.unlock", new { context }, cancellationToken);

    public Task<VaultStatusDto> LockAsync(
        ProfileContextDto context, CancellationToken cancellationToken = default) =>
        CallAsync<VaultStatusDto>("vault.lock", new { context }, cancellationToken);

    public Task<VaultStatusDto> StatusAsync(
        ProfileContextDto context, CancellationToken cancellationToken = default) =>
        CallAsync<VaultStatusDto>("vault.status", new { context }, cancellationToken);

    public async Task<IReadOnlyList<VaultEntryViewDto>> ListAsync(
        ProfileContextDto context, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<VaultListResponseDto>(
            "vault.list", new { context }, cancellationToken).ConfigureAwait(false);
        return response.Entries;
    }

    /// <summary>Explicit id lookup; the response contains a password.</summary>
    public Task<VaultEntryDto> GetAsync(
        ProfileContextDto context, ulong id, CancellationToken cancellationToken = default) =>
        CallAsync<VaultEntryDto>("vault.get", new { context, id }, cancellationToken);

    /// <summary>
    /// Explicit fill lookup. The native layer enforces exact origin matching;
    /// this method does not auto-submit or inject anything into a page.
    /// </summary>
    public Task<VaultEntryDto> GetForFillAsync(
        ProfileContextDto context,
        string origin,
        ulong id,
        CancellationToken cancellationToken = default) =>
        CallAsync<VaultEntryDto>(
            "vault.get_for_fill", new { context, origin, id }, cancellationToken);

    public async Task<VaultEntryViewDto> CreateAsync(
        ProfileContextDto context,
        string origin,
        string username,
        string password,
        string title,
        CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<VaultEntryResponseDto>(
            "vault.create",
            new { context, origin, username, password, title },
            cancellationToken).ConfigureAwait(false);
        return response.Entry;
    }

    public async Task<VaultEntryViewDto> UpdateAsync(
        ProfileContextDto context,
        ulong id,
        ulong expectedRevision,
        string origin,
        string username,
        string password,
        string title,
        CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<VaultEntryResponseDto>(
            "vault.update",
            new { context, id, expected_revision = expectedRevision, origin, username, password, title },
            cancellationToken).ConfigureAwait(false);
        return response.Entry;
    }

    public async Task<ulong> DeleteAsync(
        ProfileContextDto context,
        ulong id,
        ulong expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<VaultDeleteResponseDto>(
            "vault.delete",
            new { context, id, expected_revision = expectedRevision },
            cancellationToken).ConfigureAwait(false);
        return response.Revision;
    }

    public Task<VaultBackupExportDto> ExportBackupAsync(
        ProfileContextDto context,
        string password,
        CancellationToken cancellationToken = default) =>
        CallAsync<VaultBackupExportDto>(
            "vault.export_backup", new { context, password }, cancellationToken);

    public Task<VaultBackupSummaryDto> ValidateBackupAsync(
        ProfileContextDto context,
        string payloadHex,
        string password,
        CancellationToken cancellationToken = default) =>
        CallAsync<VaultBackupSummaryDto>(
            "vault.validate_backup",
            new { context, payload_hex = payloadHex, password },
            cancellationToken);

    public Task<VaultStatusDto> ImportBackupAsync(
        ProfileContextDto context,
        string payloadHex,
        string password,
        CancellationToken cancellationToken = default) =>
        CallAsync<VaultStatusDto>(
            "vault.import_backup",
            new { context, payload_hex = payloadHex, password },
            cancellationToken);

    public async Task<IReadOnlyList<SitePermissionDto>> ListPermissionsAsync(
        ProfileContextDto context,
        CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<SitePermissionListDto>(
            "permissions.list", new { context }, cancellationToken).ConfigureAwait(false);
        return response.Permissions;
    }

    public async Task<SitePermissionDto?> GetPermissionAsync(
        ProfileContextDto context,
        string origin,
        string kind,
        CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<SitePermissionResponseDto>(
            "permissions.get", new { context, origin, kind }, cancellationToken)
            .ConfigureAwait(false);
        return response.Permission;
    }

    public async Task<SitePermissionDto> SetPermissionAsync(
        ProfileContextDto context,
        string origin,
        string kind,
        string decision,
        string scope,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var response = await CallAsync<SitePermissionResponseDto>(
            "permissions.set",
            new
            {
                context,
                origin,
                kind,
                decision,
                scope,
                expected_revision = expectedRevision
            },
            cancellationToken).ConfigureAwait(false);
        return response.Permission
               ?? throw new VaultException(VaultErrorCode.InvalidJson,
                   "permission response was empty", "permissions.set");
    }

    public Task<SitePermissionDeleteDto> DeletePermissionAsync(
        ProfileContextDto context,
        string origin,
        string kind,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        CallAsync<SitePermissionDeleteDto>(
            "permissions.delete",
            new { context, origin, kind, expected_revision = expectedRevision },
            cancellationToken);

    public Task<SitePermissionCountDto> ClearOriginPermissionsAsync(
        ProfileContextDto context,
        string origin,
        CancellationToken cancellationToken = default) =>
        CallAsync<SitePermissionCountDto>(
            "permissions.clear_origin", new { context, origin }, cancellationToken);

    public Task<SitePermissionCountDto> ClearAllPermissionsAsync(
        ProfileContextDto context,
        CancellationToken cancellationToken = default) =>
        CallAsync<SitePermissionCountDto>(
            "permissions.clear_all", new { context }, cancellationToken);

    private async Task<T> CallAsync<T>(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var element = await Task.Run(
                () => CallCore(method, parameters), cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(element, JsonDefaults.Options)
                ?? throw new VaultException(VaultErrorCode.InvalidJson,
                    "Vault response was empty", method);
        }
        finally
        {
            _gate.Release();
        }
    }

    private JsonElement CallCore(string method, object parameters)
    {
        var request = JsonSerializer.Serialize(new
        {
            version = ExpectedAbiVersion,
            method,
            @params = parameters
        }, JsonDefaults.Options);
        var requestPtr = Marshal.StringToCoTaskMemUTF8(request);
        try
        {
            int code;
            IntPtr outputPtr;
            try
            {
                code = VaultNativeMethods.amare_services_abi2_call(
                    _handle, requestPtr, out outputPtr);
            }
            catch (Exception exception) when (IsNativeUnavailable(exception))
            {
                throw new VaultException(VaultErrorCode.Unsupported,
                    "Vault native library is unavailable", method);
            }
            if (code != 0)
            {
                throw Failure(code, method);
            }
            try
            {
                var json = outputPtr == IntPtr.Zero ? "null" : Marshal.PtrToStringUTF8(outputPtr);
                using var document = JsonDocument.Parse(json ?? "null");
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new VaultException(VaultErrorCode.InvalidJson,
                    "Vault response was not valid JSON", method);
            }
            finally
            {
                if (outputPtr != IntPtr.Zero)
                {
                    VaultNativeMethods.amare_string_free(outputPtr);
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(requestPtr);
        }
    }

    private static VaultException Failure(int code, string? method)
    {
        var message = LastError();
        return new VaultException(VaultErrorMapper.FromCode(code), message, method);
    }

    private static bool IsNativeUnavailable(Exception exception) =>
        exception is DllNotFoundException
        or EntryPointNotFoundException
        or BadImageFormatException;

    private static string LastError()
    {
        int code;
        IntPtr output;
        try
        {
            code = VaultNativeMethods.amare_services_abi2_last_error(out output);
        }
        catch (Exception exception) when (IsNativeUnavailable(exception))
        {
            return "vault native error";
        }
        if (code != 0)
        {
            return "vault native error";
        }
        try
        {
            return output == IntPtr.Zero
                ? "vault native error"
                : Marshal.PtrToStringUTF8(output) ?? "vault native error";
        }
        finally
        {
            if (output != IntPtr.Zero)
            {
                VaultNativeMethods.amare_string_free(output);
            }
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
            VaultNativeMethods.amare_services_abi2_destroy(_handle);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
