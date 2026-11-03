using System.Text.Json;

namespace Amare.Windows.Core;

public sealed class SearchClient
{
    private readonly INativeBridge _bridge;

    public SearchClient(INativeBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    public async Task<SearchPageDto> QueryAsync(
        ProfileContextDto context,
        string query,
        IEnumerable<SearchKind>? kinds = null,
        uint limit = 20,
        ulong offset = 0,
        string sort = "relevance",
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["context"] = context,
            ["query"] = query,
            ["limit"] = limit,
            ["offset"] = offset,
            ["sort"] = sort
        };
        if (kinds is not null)
        {
            parameters["kinds"] = kinds.Select(kind => kind.ToWire()).ToArray();
        }
        var result = await _bridge.CallAsync("search.query", parameters, cancellationToken);
        return Deserialize<SearchPageDto>(result);
    }

    private static T Deserialize<T>(JsonElement element) =>
        JsonSerializer.Deserialize<T>(element, JsonDefaults.Options)
        ?? throw new NativeException(NativeError.Json, $"Could not deserialize {typeof(T).Name}");
}
