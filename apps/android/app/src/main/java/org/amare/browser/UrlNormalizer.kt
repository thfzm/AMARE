package org.amare.browser

import java.net.URI

object UrlNormalizer {
    private val schemePattern = Regex("^[a-zA-Z][a-zA-Z0-9+.-]*:")

    fun normalize(input: String, searchEngine: SearchEngine = SearchEngine.DUCKDUCKGO): String? {
        val value = input.trim()
        if (value.isEmpty()) return null

        if (schemePattern.containsMatchIn(value)) {
            return when {
                value.startsWith("https://", ignoreCase = true) ||
                    value.startsWith("http://", ignoreCase = true) -> validate(value)
                else -> search(value, searchEngine)
            }
        }

        return if (looksLikeHost(value)) validate("https://$value") else search(value, searchEngine)
    }

    private fun search(query: String, engine: SearchEngine): String = engine.searchUrl(query)

    private fun looksLikeHost(value: String): Boolean {
        if (value.any(Char::isWhitespace)) return false
        val authority = value.substringBefore('/').substringBefore('?').substringBefore('#')
        if (authority.isEmpty()) return false
        if (authority.startsWith("localhost", ignoreCase = true)) return true
        return authority.substringBefore(':').contains('.')
    }

    private fun validate(value: String): String? = runCatching {
        val uri = URI(value)
        if (uri.scheme !in listOf("http", "https") || uri.host.isNullOrBlank()) {
            null
        } else {
            uri.toASCIIString()
        }
    }.getOrNull()
}
