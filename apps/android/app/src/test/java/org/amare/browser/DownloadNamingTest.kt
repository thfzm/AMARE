package org.amare.browser

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test

class DownloadNamingTest {
    @Test
    fun keepsNormalFileName() {
        assertEquals("report.pdf", DownloadNaming.sanitize("report.pdf"))
    }

    @Test
    fun stripsDirectoriesAndForbiddenCharacters() {
        assertEquals("photo_1.jpg", DownloadNaming.sanitize("/tmp/photo:1.jpg"))
        assertEquals("a_b_c.txt", DownloadNaming.sanitize("a<b>c.txt"))
    }

    @Test
    fun usesFallbackForEmptyOrTraversalNames() {
        assertEquals("download.bin", DownloadNaming.sanitize("   "))
        assertEquals("download.bin", DownloadNaming.sanitize(".."))
        assertEquals("fallback.bin", DownloadNaming.sanitize("", "fallback.bin"))
    }

    @Test
    fun truncatesLongNames() {
        val name = DownloadNaming.sanitize("a".repeat(200) + ".txt")
        assertFalse(name.length > 120)
    }
}
