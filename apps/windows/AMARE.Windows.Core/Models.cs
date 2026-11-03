using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amare.Windows.Core;

public sealed class ProfileDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Icon { get; init; }
    public bool IsDefault { get; init; }
    public long CreatedAtMs { get; init; }
    public long UpdatedAtMs { get; init; }
}

public sealed class ProfilePageDto
{
    public List<ProfileDto> Profiles { get; init; } = [];
    public ulong Total { get; init; }
    public uint Limit { get; init; }
    public ulong Offset { get; init; }
}

public sealed class ProfileContextDto
{
    public string ProfileId { get; init; } = "";
    public ulong Generation { get; init; }
}

public sealed class SettingValueDto
{
    public string Type { get; init; } = "text";
    public JsonElement Value { get; init; }

    public static SettingValueDto Text(string value) => new() { Type = "text", Value = JsonSerializer.SerializeToElement(value) };
    public static SettingValueDto Boolean(bool value) => new() { Type = "boolean", Value = JsonSerializer.SerializeToElement(value) };
    public static SettingValueDto Integer(long value) => new() { Type = "integer", Value = JsonSerializer.SerializeToElement(value) };
    public static SettingValueDto Json(string value) => new() { Type = "json", Value = JsonSerializer.SerializeToElement(value) };

    public string AsText() => Value.ValueKind == JsonValueKind.String ? Value.GetString() ?? "" : Value.ToString();
    public bool AsBoolean() => Value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidOperationException("Setting value is not boolean.")
    };
}

public sealed class ResolvedSettingDto
{
    public string Key { get; init; } = "";
    public string Group { get; init; } = "";
    public SettingValueDto Value { get; init; } = new();
    public string Source { get; init; } = "default";
    public long? UpdatedAtMs { get; init; }
}

public sealed class SettingsListDto
{
    public List<ResolvedSettingDto> Settings { get; init; } = [];
}

public sealed class SettingChangeDto
{
    public string Key { get; init; } = "";
    public string Group { get; init; } = "";
    public SettingValueDto? Old { get; init; }
    public SettingValueDto New { get; init; } = new();
    public string Source { get; init; } = "user";
}

public sealed class SettingsChangeSetDto
{
    public List<SettingChangeDto> Changes { get; init; } = [];
    public int ChangedCount { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SearchKind
{
    LibraryItem,
    Tag,
    Collection,
    Conversation,
    Message,
    Workspace,
    Task,
    TaskMemory,
    Bookmark,
    History
}

public static class SearchKindNames
{
    public static string ToWire(this SearchKind kind) => kind switch
    {
        SearchKind.LibraryItem => "library_item",
        SearchKind.Tag => "tag",
        SearchKind.Collection => "collection",
        SearchKind.Conversation => "conversation",
        SearchKind.Message => "message",
        SearchKind.Workspace => "workspace",
        SearchKind.Task => "task",
        SearchKind.TaskMemory => "task_memory",
        SearchKind.Bookmark => "bookmark",
        SearchKind.History => "history",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static SearchKind Parse(string value) => value switch
    {
        "library_item" => SearchKind.LibraryItem,
        "tag" => SearchKind.Tag,
        "collection" => SearchKind.Collection,
        "conversation" => SearchKind.Conversation,
        "message" => SearchKind.Message,
        "workspace" => SearchKind.Workspace,
        "task" => SearchKind.Task,
        "task_memory" => SearchKind.TaskMemory,
        "bookmark" => SearchKind.Bookmark,
        "history" => SearchKind.History,
        _ => throw new ArgumentException($"Unknown search kind: {value}", nameof(value))
    };
}

public sealed class SearchHitDto
{
    public string Kind { get; init; } = "";
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string? Snippet { get; init; }
    public long? UpdatedAtMs { get; init; }

    public SearchKind SearchKind => SearchKindNames.Parse(Kind);
}

public sealed class SearchPageDto
{
    public string ProfileId { get; init; } = "";
    public string Query { get; init; } = "";
    public List<SearchHitDto> Items { get; init; } = [];
    public ulong Total { get; init; }
    public uint Limit { get; init; }
    public ulong Offset { get; init; }
    public string Sort { get; init; } = "relevance";
}

public sealed class SessionTabDto
{
    public long Id { get; init; }
    public long SessionId { get; init; }
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public long Position { get; init; }
    public bool Pinned { get; init; }
    public string? GroupId { get; init; }
    public long LastAccessedAtMs { get; init; }
}

public sealed class SessionDto
{
    public long Id { get; init; }
    public string ProfileId { get; init; } = "";
    public string Name { get; init; } = "";
    public long? ActiveTabId { get; init; }
    public long CreatedAtMs { get; init; }
    public long UpdatedAtMs { get; init; }
}

public sealed class SessionStateDto
{
    public SessionDto Session { get; init; } = new();
    public List<SessionTabDto> Tabs { get; init; } = [];
}

public sealed class HistoryEntryDto
{
    public long Id { get; init; }
    public string ProfileId { get; init; } = "";
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public long FirstVisitedAtMs { get; init; }
    public long LastVisitedAtMs { get; init; }
    public ulong VisitCount { get; init; }
}

public sealed class HistoryPageDto
{
    public List<HistoryEntryDto> Items { get; init; } = [];
    public ulong Total { get; init; }
}
