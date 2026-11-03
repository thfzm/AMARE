using System.Text.Json;

namespace Amare.Windows.Core;

public sealed class SettingsClient
{
    private readonly INativeBridge _bridge;

    public SettingsClient(INativeBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    public Task<ResolvedSettingDto> GetAsync(ProfileContextDto context, string key, CancellationToken cancellationToken = default) =>
        Call<ResolvedSettingDto>("settings.get", new { context, key }, cancellationToken);

    public async Task<IReadOnlyList<ResolvedSettingDto>> ListGroupAsync(ProfileContextDto context, string group,
        CancellationToken cancellationToken = default)
    {
        var result = await _bridge.CallAsync("settings.list_group", new { context, group }, cancellationToken);
        return Deserialize<SettingsListDto>(result).Settings;
    }

    public async Task<IReadOnlyList<ResolvedSettingDto>> ListAllAsync(ProfileContextDto context,
        CancellationToken cancellationToken = default)
    {
        var result = await _bridge.CallAsync("settings.list_all", new { context }, cancellationToken);
        return Deserialize<SettingsListDto>(result).Settings;
    }

    public Task<SettingsChangeSetDto> SetAsync(ProfileContextDto context, string key, SettingValueDto value,
        CancellationToken cancellationToken = default) =>
        Call<SettingsChangeSetDto>("settings.set", new { context, key, value }, cancellationToken);

    public async Task<SettingsChangeSetDto> SetManyAsync(ProfileContextDto context,
        IEnumerable<(string Key, SettingValueDto Value)> entries, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            context,
            entries = entries.Select(entry => new { key = entry.Key, value = entry.Value }).ToArray()
        };
        var result = await _bridge.CallAsync("settings.set_many", payload, cancellationToken);
        return Deserialize<SettingsChangeSetDto>(result);
    }

    public Task<SettingsChangeSetDto> ResetKeyAsync(ProfileContextDto context, string key,
        CancellationToken cancellationToken = default) =>
        Call<SettingsChangeSetDto>("settings.reset_key", new { context, key }, cancellationToken);

    public Task<SettingsChangeSetDto> ResetGroupAsync(ProfileContextDto context, string group,
        CancellationToken cancellationToken = default) =>
        Call<SettingsChangeSetDto>("settings.reset_group", new { context, group }, cancellationToken);

    public Task<SettingsChangeSetDto> ResetAllAsync(ProfileContextDto context, CancellationToken cancellationToken = default) =>
        Call<SettingsChangeSetDto>("settings.reset_all", new { context }, cancellationToken);

    private async Task<T> Call<T>(string method, object parameters, CancellationToken cancellationToken)
    {
        var result = await _bridge.CallAsync(method, parameters, cancellationToken);
        return Deserialize<T>(result);
    }

    private static T Deserialize<T>(JsonElement element) =>
        JsonSerializer.Deserialize<T>(element, JsonDefaults.Options)
        ?? throw new NativeException(NativeError.Json, $"Could not deserialize {typeof(T).Name}");
}
