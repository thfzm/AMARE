package org.amare.browser

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class UrlNormalizerTest {
    @Test
    fun addsHttpsToBareHost() {
        assertEquals("https://example.com", UrlNormalizer.normalize("example.com"))
    }

    @Test
    fun keepsExplicitHttpAndPath() {
        assertEquals(
            "https://example.com/path?q=1",
            UrlNormalizer.normalize("https://example.com/path?q=1"),
        )
        assertEquals("http://example.com/", UrlNormalizer.normalize("http://example.com/"))
    }

    @Test
    fun turnsPlainTextIntoHttpsSearch() {
        assertEquals(
            "https://duckduckgo.com/?q=hello%20world",
            UrlNormalizer.normalize("hello world"),
        )
    }

    @Test
    fun rejectsUnknownSchemeAsNavigationAndUsesSearch() {
        assertEquals(
            "https://duckduckgo.com/?q=javascript%3Aalert%281%29",
            UrlNormalizer.normalize("javascript:alert(1)"),
        )
    }

    @Test
    fun rejectsBlankAndMalformedUrls() {
        assertNull(UrlNormalizer.normalize("   "))
        assertNull(UrlNormalizer.normalize("https://"))
    }
}
