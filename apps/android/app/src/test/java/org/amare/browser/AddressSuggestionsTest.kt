package org.amare.browser

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class AddressSuggestionsTest {
    private val tabs = listOf(AddressEntry("https://open.example/page", "Open page"))
    private val bookmarks = listOf(AddressEntry("https://book.example/", "Bookmark"))
    private val history = listOf(
        AddressEntry("https://example.com/", "Example"),
        AddressEntry("https://other.test/", "Other"),
    )

    @Test
    fun emptyQueryShowsLocalEntriesAndNoSearch() {
        val result = AddressSuggestions.suggest(
            query = "",
            engine = SearchEngine.DUCKDUCKGO,
            openTabs = tabs,
            bookmarks = bookmarks,
            history = history,
        )
        assertEquals(4, result.size)
        assertTrue(result.none { it.kind == AddressSuggestionKind.SEARCH })
    }

    @Test
    fun matchesOpenTabsBookmarksAndHistoryWithoutDuplicates() {
        val result = AddressSuggestions.suggest(
            query = "example",
            engine = SearchEngine.DUCKDUCKGO,
            openTabs = tabs,
            bookmarks = bookmarks,
            history = history + AddressEntry("https://example.com/", "Example duplicate"),
        )
        assertEquals(AddressSuggestionKind.SEARCH, result.last().kind)
        assertEquals(1, result.count { it.url == "https://example.com/" })
        assertTrue(result.any { it.kind == AddressSuggestionKind.OPEN_TAB })
        assertTrue(result.any { it.kind == AddressSuggestionKind.BOOKMARK })
    }

    @Test
    fun searchSuggestionUsesSelectedEngine() {
        val result = AddressSuggestions.suggest(
            query = "amare",
            engine = SearchEngine.NAVER,
            openTabs = emptyList(),
            bookmarks = emptyList(),
            history = emptyList(),
        )
        assertEquals(1, result.size)
        assertEquals(AddressSuggestionKind.SEARCH, result[0].kind)
        assertTrue(result[0].url.startsWith("https://search.naver.com/search.naver?query="))
    }

    @Test
    fun limitIsRespected() {
        val many = (1..20).map { AddressEntry("https://example.com/$it", "Example $it") }
        val result = AddressSuggestions.suggest(
            query = "example",
            engine = SearchEngine.DUCKDUCKGO,
            openTabs = emptyList(),
            bookmarks = many,
            history = emptyList(),
            limit = 3,
        )
        assertEquals(3, result.size)
    }
}
