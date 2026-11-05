package org.amare.browser

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class SearchEngineTest {
    @Test
    fun encodesQueryWithUtf8() {
        assertEquals(
            "https://duckduckgo.com/?q=%ED%95%9C%EA%B8%80%20query",
            SearchEngine.DUCKDUCKGO.searchUrl("한글 query"),
        )
    }

    @Test
    fun supportsMultipleEngines() {
        assertEquals("duckduckgo", SearchEngine.fromId("duckduckgo").id)
        assertEquals("google", SearchEngine.fromId("google").id)
        assertEquals("bing", SearchEngine.fromId("bing").id)
        assertEquals("naver", SearchEngine.fromId("naver").id)
        assertEquals(SearchEngine.DUCKDUCKGO, SearchEngine.fromId("unknown"))
    }

    @Test
    fun normalizerUsesSelectedEngine() {
        val result = UrlNormalizer.normalize("amare browser", SearchEngine.GOOGLE)
        assertTrue(result!!.startsWith("https://www.google.com/search?q="))
        assertTrue(result.contains("amare%20browser"))
    }
}
