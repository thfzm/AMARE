namespace Amare.Windows.Core;

public sealed record AddressSuggestion(string Label, string Url, string Kind)
{
    public override string ToString() => Label;
}

public static class AddressSuggestionProvider
{
    public static IReadOnlyList<AddressSuggestion> Suggest(
        string? query,
        string? searchEngineId,
        IReadOnlyList<AddressEntry> openTabs,
        IReadOnlyList<AddressEntry> bookmarks,
        IReadOnlyList<AddressEntry> history,
        int limit = 8)
    {
        var value = query?.Trim() ?? "";
        var normalized = value.ToLowerInvariant();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(AddressEntry Entry, string Kind, int Rank)>();

        void Add(IEnumerable<AddressEntry> entries, string kind)
        {
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Url)) continue;
                var url = entry.Url.ToLowerInvariant();
                var title = entry.Title.ToLowerInvariant();
                var rank = normalized.Length == 0
                    ? 3
                    : url.StartsWith(normalized, StringComparison.Ordinal)
                      || title.StartsWith(normalized, StringComparison.Ordinal) ? 0
                    : url.Contains(normalized, StringComparison.Ordinal)
                      || title.Contains(normalized, StringComparison.Ordinal) ? 1
                    : 2;
                if (normalized.Length > 0 && rank == 2) continue;
                candidates.Add((entry, kind, rank));
            }
        }

        Add(openTabs, "Open tab");
        Add(bookmarks, "Bookmark");
        Add(history, "History");
        candidates.Sort((left, right) =>
        {
            var rank = left.Rank.CompareTo(right.Rank);
            return rank != 0 ? rank : StringComparer.OrdinalIgnoreCase.Compare(left.Entry.Title, right.Entry.Title);
        });

        var results = new List<AddressSuggestion>();
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate.Entry.Url)) continue;
            results.Add(new AddressSuggestion(
                string.IsNullOrWhiteSpace(candidate.Entry.Title) ? candidate.Entry.Url : candidate.Entry.Title,
                candidate.Entry.Url,
                candidate.Kind));
            if (results.Count >= limit) break;
        }

        if (value.Length > 0 && results.Count < limit)
        {
            var searchUrl = SearchEngineCatalog.BuildSearchUrl(searchEngineId, value);
            if (seen.Add(searchUrl))
            {
                var engine = SearchEngineCatalog.Resolve(searchEngineId);
                results.Add(new AddressSuggestion($"Search {engine.Name} for “{value}”", searchUrl, "Search"));
            }
        }
        return results.Take(limit).ToArray();
    }
}

public sealed record AddressEntry(string Url, string Title);
