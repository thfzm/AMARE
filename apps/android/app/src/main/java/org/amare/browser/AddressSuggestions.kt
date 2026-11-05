package org.amare.browser

data class AddressEntry(
    val url: String,
    val title: String,
)

enum class AddressSuggestionKind {
    OPEN_TAB,
    BOOKMARK,
    HISTORY,
    SEARCH,
}

data class AddressSuggestion(
    val url: String,
    val title: String,
    val kind: AddressSuggestionKind,
)

object AddressSuggestions {
    fun suggest(
        query: String,
        engine: SearchEngine,
        openTabs: List<AddressEntry>,
        bookmarks: List<AddressEntry>,
        history: List<AddressEntry>,
        limit: Int = 6,
    ): List<AddressSuggestion> {
        val value = query.trim()
        val normalized = value.lowercase()
        val results = mutableListOf<AddressSuggestion>()
        val seen = mutableSetOf<String>()

        fun add(entry: AddressEntry, kind: AddressSuggestionKind) {
            if (entry.url.isBlank() || !seen.add(entry.url)) return
            results.add(AddressSuggestion(entry.url, entry.title.ifBlank { entry.url }, kind))
        }

        val candidates = buildList {
            openTabs.forEach { add(it to AddressSuggestionKind.OPEN_TAB) }
            bookmarks.forEach { add(it to AddressSuggestionKind.BOOKMARK) }
            history.forEach { add(it to AddressSuggestionKind.HISTORY) }
        }.mapNotNull { (entry, kind) ->
            val url = entry.url.lowercase()
            val title = entry.title.lowercase()
            val rank = when {
                normalized.isEmpty() -> 3
                url.startsWith(normalized) || title.startsWith(normalized) -> 0
                url.contains(normalized) || title.contains(normalized) -> 1
                else -> 2
            }
            if (normalized.isNotEmpty() && rank == 2) null else Triple(entry, kind, rank)
        }.sortedWith(
            compareBy<Triple<AddressEntry, AddressSuggestionKind, Int>> { it.third }
                .thenBy { it.first.title.lowercase() },
        )

        candidates.forEach { (entry, kind, _) -> add(entry, kind) }
        if (results.size < limit && value.isNotEmpty()) {
            val searchUrl = engine.searchUrl(value)
            if (seen.add(searchUrl)) {
                results.add(
                    AddressSuggestion(
                        url = searchUrl,
                        title = "${engine.label}에서 \"$value\" 검색",
                        kind = AddressSuggestionKind.SEARCH,
                    ),
                )
            }
        }
        return results.take(limit.coerceAtLeast(0))
    }
}
