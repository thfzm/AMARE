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
}
