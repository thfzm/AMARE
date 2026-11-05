using System.Text.Json;

namespace Amare.Windows.Core;

public sealed class BookmarkDto
{
    public long Id { get; init; }
    public string ProfileId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public long? FolderId { get; init; }
    public long Position { get; init; }
    public long CreatedAtMs { get; init; }
    public long UpdatedAtMs { get; init; }
}

public sealed class BookmarkPageDto
{
    public List<BookmarkDto> Items { get; init; } = [];
    public ulong Total { get; init; }
    public uint Limit { get; init; }
    public ulong Offset { get; init; }
}

public sealed class BookmarkClient
{
    private readonly INativeBridge _bridge;

    public BookmarkClient(INativeBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    public async Task<BookmarkPageDto> ListAsync(
        ProfileContextDto context,
        string? query = null,
        uint limit = 100,
        ulong offset = 0,
        CancellationToken cancellationToken = default)
    {
        var result = await _bridge.CallAsync("bookmark.list", new
        {
            context,
            query,
            limit,
            offset
        }, cancellationToken);
        return Deserialize<BookmarkPageDto>(result);
    }

    public async Task<BookmarkDto> CreateAsync(
        ProfileContextDto context,
        string title,
        string url,
        CancellationToken cancellationToken = default)
    {
        var result = await _bridge.CallAsync("bookmark.create", new
        {
            context,
            title,
            url
        }, cancellationToken);
        return Deserialize<BookmarkDto>(result);
    }

    public async Task<bool> DeleteAsync(
        ProfileContextDto context,
        long id,
        CancellationToken cancellationToken = default)
    {
        var result = await _bridge.CallAsync("bookmark.delete", new { context, id }, cancellationToken);
        return result.GetProperty("deleted").GetBoolean();
    }

    private static T Deserialize<T>(JsonElement element) =>
        JsonSerializer.Deserialize<T>(element, JsonDefaults.Options)
        ?? throw new NativeException(NativeError.Json, $"Could not deserialize {typeof(T).Name}");
}
