using System.Text.Json;

namespace Amare.Windows.Core;

public sealed class HistoryClient
{
    private readonly INativeBridge _bridge;

    public HistoryClient(INativeBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    public async Task<IReadOnlyList<HistoryEntryDto>> TopAsync(ProfileContextDto context, uint limit = 6,
        CancellationToken cancellationToken = default)
    {
        var result = await _bridge.CallAsync("history.top", new { context, limit }, cancellationToken);
        var page = JsonSerializer.Deserialize<HistoryPageDto>(result, JsonDefaults.Options)
            ?? throw new NativeException(NativeError.Json, "Could not deserialize history");
        return page.Items;
    }

    public async Task<HistoryPageDto> ListAsync(
        ProfileContextDto context,
        string? query = null,
        uint limit = 100,
        ulong offset = 0,
        CancellationToken cancellationToken = default)
    {
        var result = await _bridge.CallAsync("history.list", new
        {
            context,
            query,
            limit,
            offset
        }, cancellationToken);
        return JsonSerializer.Deserialize<HistoryPageDto>(result, JsonDefaults.Options)
            ?? throw new NativeException(NativeError.Json, "Could not deserialize history");
    }

    public async Task RecordAsync(
        ProfileContextDto context,
        string url,
        string title,
        CancellationToken cancellationToken = default)
    {
        await _bridge.CallAsync("history.record", new
        {
            context,
            url,
            title,
            visited_at_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }, cancellationToken);
    }

    public async Task<long> ClearAsync(ProfileContextDto context, CancellationToken cancellationToken = default)
    {
        var result = await _bridge.CallAsync("history.clear", new { context }, cancellationToken);
        return result.GetProperty("deleted").GetInt64();
    }
}
