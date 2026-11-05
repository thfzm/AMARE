package org.amare.browser

import java.net.URLEncoder

enum class SearchEngine(
    val id: String,
    val label: String,
    private val template: String,
) {
    DUCKDUCKGO("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q=%s"),
    GOOGLE("google", "Google", "https://www.google.com/search?q=%s"),
    BING("bing", "Bing", "https://www.bing.com/search?q=%s"),
    NAVER("naver", "Naver", "https://search.naver.com/search.naver?query=%s"),
    ;

    fun searchUrl(query: String): String =
        template.format(URLEncoder.encode(query, Charsets.UTF_8.name()).replace("+", "%20"))

    companion object {
        fun fromId(id: String?): SearchEngine =
            entries.firstOrNull { it.id == id } ?: DUCKDUCKGO
    }
}

enum class ThemeMode(val id: String, val label: String) {
    SYSTEM("system", "시스템"),
    LIGHT("light", "라이트"),
    DARK("dark", "다크"),
    ;

    companion object {
        fun fromId(id: String?): ThemeMode =
            entries.firstOrNull { it.id == id } ?: SYSTEM
    }
}
