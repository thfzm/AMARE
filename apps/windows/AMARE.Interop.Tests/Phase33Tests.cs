using System.Text.Json;
using System.Text.RegularExpressions;
using Amare.Windows.Core;

namespace Amare.Interop.Tests;

internal static class Phase33Tests
{
    public static void Register()
    {
        Program.Run("localization_resource_completeness", ResourceCompleteness);
        Program.Run("localization_fallback_and_language_switch", LanguageSwitch);
        Program.Run("app_preferences_roundtrip", PreferencesRoundtrip);
    }

    private static string ResourceCompleteness()
    {
        var placeholders = new Regex(@"\{\d+\}", RegexOptions.Compiled);
        Require(L10n.Keys.Count > 100, "too few localization keys");
        foreach (var key in L10n.Keys)
        {
            var english = L10n.EnglishText(key);
            var korean = L10n.KoreanText(key);
            Require(!string.IsNullOrWhiteSpace(english), $"empty English for {key}");
            Require(!string.IsNullOrWhiteSpace(korean), $"empty Korean for {key}");
            Require(placeholders.Matches(english).Count == placeholders.Matches(korean).Count,
                $"placeholder mismatch for {key}");
        }
        Require(L10n.StaticResources.Count > 50, "too few static resources");
        foreach (var pair in L10n.StaticResources)
        {
            Require(!string.IsNullOrWhiteSpace(pair.Key), "empty static key");
            Require(!string.IsNullOrWhiteSpace(pair.Value), $"empty static value for {pair.Key}");
        }
        return $"{L10n.Keys.Count} keyed + {L10n.StaticResources.Count} static strings complete";
    }

    private static string LanguageSwitch()
    {
        L10n.Initialize(L10n.Korean);
        Require(L10n.T("settings.title") == "설정", L10n.T("settings.title"));
        Require(L10n.T("menu.newTab") == "새 탭", L10n.T("menu.newTab"));
        Require(L10n.TranslateStatic("Search engine") == "검색 엔진",
            L10n.TranslateStatic("Search engine"));
        Require(L10n.T("missing.key") == "missing.key", "missing key fallback");
        L10n.Initialize(L10n.English);
        Require(L10n.T("settings.title") == "Settings", L10n.T("settings.title"));
        Require(L10n.T("menu.newTab") == "New tab", L10n.T("menu.newTab"));
        Require(L10n.TranslateStatic("Search engine") == "Search engine",
            "English static fallback");
        L10n.Initialize(L10n.SystemLanguage);
        Require(L10n.CurrentLanguage is L10n.English or L10n.Korean,
            L10n.CurrentLanguage);
        L10n.Initialize(L10n.English);
        return "Korean/English switch, static translation and key fallback";
    }

    private static string PreferencesRoundtrip()
    {
        var preferences = new AppPreferences(L10n.Korean, @"C:\Users\example\Downloads");
        var loaded = JsonSerializer.Deserialize<AppPreferences>(
            JsonSerializer.Serialize(preferences));
        Require(loaded?.Language == L10n.Korean, "language roundtrip");
        Require(loaded?.DownloadDirectory == @"C:\Users\example\Downloads", "download path roundtrip");
        var defaults = new AppPreferences();
        Require(defaults.Language == L10n.SystemLanguage && defaults.DownloadDirectory == "",
            "app preference defaults");
        Require(L10n.Languages.Any(option => option.Id == L10n.Korean)
                && L10n.Languages.Any(option => option.Id == L10n.English),
            "language options");
        return "app preferences roundtrip and defaults";
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
