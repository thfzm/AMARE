using Amare.Windows.Core;

namespace Amare.Interop.Tests;

internal static class Phase22Tests
{
    public static void Register()
    {
        Program.Run("windows_0_15_address_suggestions", () =>
        {
            var suggestions = AddressSuggestionProvider.Suggest(
                "ama",
                "google",
                [new AddressEntry("https://open.example/page", "Open AMARE page")],
                [new AddressEntry("https://book.example/", "AMARE bookmark")],
                [new AddressEntry("https://history.example/", "AMARE history")]);
            if (suggestions.Count != 4) throw new InvalidOperationException($"unexpected count {suggestions.Count}");
            if (suggestions[0].Kind != "Open tab") throw new InvalidOperationException("open tab rank");
            if (suggestions[^1].Kind != "Search") throw new InvalidOperationException("search fallback");
            if (!suggestions[^1].Url.StartsWith("https://www.google.com/search?q=", StringComparison.Ordinal))
                throw new InvalidOperationException("search engine");
            return string.Join(",", suggestions.Select(item => item.Kind));
        });

        Program.Run("windows_0_15_search_engines", () =>
        {
            var url = SearchEngineCatalog.BuildSearchUrl("naver", "한글 & test");
            if (!url.StartsWith("https://search.naver.com/search.naver?query=", StringComparison.Ordinal))
                throw new InvalidOperationException(url);
            return url;
        });

        Program.Run("windows_0_15_reader_page_builder", () =>
        {
            var html = ReaderPageBuilder.Build(
                new ReaderExtractedPage(
                    "A <title>",
                    "https://example.com/article",
                    [new ReaderExtractedBlock("h1", "Heading"), new ReaderExtractedBlock("p", "Body & text")],
                    12),
                new ReaderDisplaySettings("serif", "dark", 19, 1.7, 720),
                "nonce");
            if (!html.Contains("&lt;title&gt;", StringComparison.Ordinal)) throw new InvalidOperationException("escape");
            if (!html.Contains("nonce", StringComparison.Ordinal)) throw new InvalidOperationException("nonce");
            if (!html.Contains("prefers-color-scheme", StringComparison.Ordinal)) throw new InvalidOperationException("theme");
            return $"html={html.Length}";
        });

        Program.Run("windows_0_15_reader_safe_image_urls", () =>
        {
            var html = ReaderPageBuilder.Build(
                new ReaderExtractedPage(
                    "Images",
                    "https://example.com",
                    [new ReaderExtractedBlock("img", "", "javascript:alert(1)"), new ReaderExtractedBlock("img", "hero", "https://cdn.example/hero.jpg")],
                    4),
                new ReaderDisplaySettings("sans", "light", 18, 1.6, 700),
                "n");
            if (html.Contains("javascript:", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("unsafe image");
            if (!html.Contains("https://cdn.example/hero.jpg", StringComparison.Ordinal)) throw new InvalidOperationException("safe image");
            return "safe image filtering";
        });
    }
}
