using System.Text.Json;

namespace Amare.Windows.Core;

public sealed class SessionTabInput
{
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public bool Pinned { get; init; }
    public string? GroupId { get; init; }
}

public sealed class SessionClient
{
    private readonly INativeBridge _bridge;

    public SessionClient(INativeBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    public async Task<SessionStateDto> SaveAsync(
        ProfileContextDto context,
        long? sessionId,
        IReadOnlyList<SessionTabInput> tabs,
        int? activePosition,
        string name = "AMARE session",
        CancellationToken cancellationToken = default)
    {
        var result = await _bridge.CallAsync("session.save", new
        {
            context,
            session_id = sessionId,
            name,
            tabs,
            active_position = activePosition
        }, cancellationToken);
        return Deserialize<SessionStateDto>(result);
    }

    public async Task<SessionStateDto?> LatestAsync(ProfileContextDto context, CancellationToken cancellationToken = default)
    {
        var result = await _bridge.CallAsync("session.latest", new { context }, cancellationToken);
        return result.ValueKind == JsonValueKind.Null ? null : Deserialize<SessionStateDto>(result);
    }

    public async Task<bool> DeleteAsync(ProfileContextDto context, long sessionId, CancellationToken cancellationToken = default)
    {
        var result = await _bridge.CallAsync("session.delete", new { context, session_id = sessionId }, cancellationToken);
        return result.GetProperty("deleted").GetBoolean();
    }

    private static T Deserialize<T>(JsonElement element) =>
        JsonSerializer.Deserialize<T>(element, JsonDefaults.Options)
        ?? throw new NativeException(NativeError.Json, $"Could not deserialize {typeof(T).Name}");
}
