package org.amare.browser

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ReaderMarkdownTest {
    @Test
    fun parsesHeadingsParagraphsListsQuotesAndCode() {
        val doc = ReaderMarkdown.parse(
            """
            # Title

            A paragraph with **bold** and a [link](https://example.com/page).

            - first
            1. second

            > quoted

            ```kotlin
            val answer = 42
            ```
            """.trimIndent(),
            title = "Page title",
            url = "https://example.com/article",
        )

        assertEquals("Page title", doc.title)
        assertTrue(doc.blocks.any { it is ReaderBlock.Heading && it.level == 1 })
        assertTrue(doc.blocks.any { it is ReaderBlock.Paragraph })
        assertEquals(2, doc.blocks.count { it is ReaderBlock.ListItem })
        assertTrue(doc.blocks.any { it is ReaderBlock.Quote })
        assertTrue(doc.blocks.any { it is ReaderBlock.Code && it.text.contains("42") })
    }

    @Test
    fun keepsOnlySafeAbsoluteOrResolvedImageUrls() {
        val doc = ReaderMarkdown.parse(
            """
            ![hero](https://cdn.example.com/hero.jpg)
            ![bad](javascript:alert(1))
            ![relative](/images/photo.png)
            """.trimIndent(),
            title = "Images",
            url = "https://example.com/post",
        )

        val images = doc.blocks.filterIsInstance<ReaderBlock.Image>()
        assertEquals(2, images.size)
        assertEquals("https://cdn.example.com/hero.jpg", images[0].url)
        assertEquals("https://example.com/images/photo.png", images[1].url)
        assertFalse(images.any { it.url.startsWith("javascript:") })
    }

    @Test
    fun plainTextRemovesMarkdownMarkers() {
        assertEquals(
            "A bold link and code",
            ReaderMarkdown.plainText("A **bold** [link](https://example.com) and `code`"),
        )
    }

    @Test
    fun emptyContentProducesNoBlocks() {
        val doc = ReaderMarkdown.parse("", "Empty", "https://example.com")
        assertTrue(doc.blocks.isEmpty())
    }
}
