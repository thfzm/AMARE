using System.Text.Json;

namespace Amare.Windows.Core;

public sealed class ProfileClient
{
    private readonly INativeBridge _bridge;

    public ProfileClient(INativeBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    public async Task<ProfilePageDto> ListAsync(uint limit = 50, ulong offset = 0, CancellationToken cancellationToken = default)
    {
        var result = await _bridge.CallAsync("profile.list", new { limit, offset }, cancellationToken);
        return Deserialize<ProfilePageDto>(result);
    }

    public Task<ProfileContextDto> InitializeAsync(CancellationToken cancellationToken = default) =>
        Call<ProfileContextDto>("profile.initialize", new { }, cancellationToken);

    public Task<ProfileDto?> GetActiveAsync(CancellationToken cancellationToken = default) =>
        CallNullable<ProfileDto>("profile.active", new { }, cancellationToken);

    public Task<ProfileContextDto?> GetCurrentContextAsync(CancellationToken cancellationToken = default) =>
        CallNullable<ProfileContextDto>("profile.current_context", new { }, cancellationToken);

    public Task<ProfileDto> CreateAsync(string id, string name, string? icon = null, bool isDefault = false,
        CancellationToken cancellationToken = default) =>
        Call<ProfileDto>("profile.create", new { id, name, icon, is_default = isDefault }, cancellationToken);

    public Task<ProfileDto> RenameAsync(string id, string name, CancellationToken cancellationToken = default) =>
        Call<ProfileDto>("profile.rename", new { id, name }, cancellationToken);

    public Task<ProfileDto> SetIconAsync(string id, string? icon, CancellationToken cancellationToken = default) =>
        Call<ProfileDto>("profile.set_icon", new { id, icon }, cancellationToken);

    public Task<ProfileDto> SetDefaultAsync(string id, CancellationToken cancellationToken = default) =>
        Call<ProfileDto>("profile.set_default", new { id }, cancellationToken);

    public Task<ProfileContextDto> SwitchAsync(string id, CancellationToken cancellationToken = default) =>
        Call<ProfileContextDto>("profile.switch", new { id }, cancellationToken);

    public async Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default)
    {
        var result = await _bridge.CallAsync("profile.exists", new { id }, cancellationToken);
        return result.GetProperty("exists").GetBoolean();
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        _ = await _bridge.CallAsync("profile.delete", new { id }, cancellationToken);
    }

    public async Task ResetAsync(ProfileContextDto context, CancellationToken cancellationToken = default)
    {
        _ = await _bridge.CallAsync("profile.reset", new { context }, cancellationToken);
    }

    private async Task<T> Call<T>(string method, object parameters, CancellationToken cancellationToken)
    {
        var result = await _bridge.CallAsync(method, parameters, cancellationToken);
        return Deserialize<T>(result);
    }

    private async Task<T?> CallNullable<T>(string method, object parameters, CancellationToken cancellationToken)
        where T : class
    {
        var result = await _bridge.CallAsync(method, parameters, cancellationToken);
        return result.ValueKind == JsonValueKind.Null ? null : Deserialize<T>(result);
    }

    private static T Deserialize<T>(JsonElement element) =>
        JsonSerializer.Deserialize<T>(element, JsonDefaults.Options)
        ?? throw new NativeException(NativeError.Json, $"Could not deserialize {typeof(T).Name}");
}
