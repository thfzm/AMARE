namespace Amare.Windows.Core;

public sealed record SearchEngineDescriptor(string Id, string Name, string SearchUrlTemplate);

public static class SearchEngineCatalog
{
    public const string DefaultId = "duckduckgo";

    public static IReadOnlyList<SearchEngineDescriptor> All { get; } =
    [
        new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q={query}"),
        new("google", "Google", "https://www.google.com/search?q={query}"),
        new("bing", "Bing", "https://www.bing.com/search?q={query}"),
        new("naver", "Naver", "https://search.naver.com/search.naver?query={query}")
    ];

    public static SearchEngineDescriptor Resolve(string? id) =>
        All.FirstOrDefault(engine => string.Equals(engine.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All.First(engine => engine.Id == DefaultId);

    public static string BuildSearchUrl(string? id, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Search query is required.", nameof(query));
        }
        var engine = Resolve(id);
        return engine.SearchUrlTemplate.Replace("{query}", Uri.EscapeDataString(query.Trim()), StringComparison.Ordinal);
    }
}
