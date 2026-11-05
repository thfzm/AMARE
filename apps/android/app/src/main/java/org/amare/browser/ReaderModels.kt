package org.amare.browser

import java.net.URI

enum class ReaderFont(val id: String, val label: String) {
    SERIF("serif", "Serif"),
    SANS("sans", "Sans"),
    MONO("mono", "Mono"),
    ;

    companion object {
        fun fromId(id: String?): ReaderFont = entries.firstOrNull { it.id == id } ?: SERIF
    }
}

enum class ReaderTheme(val id: String, val label: String) {
    LIGHT("light", "라이트"),
    DARK("dark", "다크"),
    SEPIA("sepia", "세피아"),
    ;

    companion object {
        fun fromId(id: String?): ReaderTheme = entries.firstOrNull { it.id == id } ?: LIGHT
    }
}

enum class ReaderWidth(val id: String, val label: String, val maxDp: Int) {
    NARROW("narrow", "좁게", 560),
    WIDE("wide", "넓게", 760),
    ;

    companion object {
        fun fromId(id: String?): ReaderWidth = entries.firstOrNull { it.id == id } ?: WIDE
    }
}

data class ReaderSettings(
    val textSizeSp: Int = 18,
    val font: ReaderFont = ReaderFont.SERIF,
    val lineHeight: Float = 1.65f,
    val width: ReaderWidth = ReaderWidth.WIDE,
    val theme: ReaderTheme = ReaderTheme.LIGHT,
)

data class ReaderDocument(
    val title: String,
    val url: String,
    val blocks: List<ReaderBlock>,
)

sealed interface ReaderBlock {
    data class Heading(val level: Int, val text: String) : ReaderBlock
    data class Paragraph(val text: String) : ReaderBlock
    data class ListItem(
        val text: String,
        val ordered: Boolean,
        val index: Int,
        val depth: Int,
    ) : ReaderBlock
    data class Quote(val text: String) : ReaderBlock
    data class Code(val text: String) : ReaderBlock
    data class Image(val url: String, val alt: String) : ReaderBlock
}

object ReaderMarkdown {
    private val heading = Regex("^(#{1,6})\\s+(.+)$")
    private val image = Regex("^!\\[([^]]*)]\\(([^)\\s]+)(?:\\s+\"[^\"]*\")?\\)\\s*$")
    private val list = Regex("^(\\s*)([-*+]|\\d+[.)])\\s+(.+)$")
    private val quote = Regex("^>\\s?(.*)$")
    private val rule = Regex("^[-*_]{3,}$")

    fun parse(markdown: String, title: String, url: String): ReaderDocument {
        val blocks = mutableListOf<ReaderBlock>()
        val paragraph = StringBuilder()
        val code = StringBuilder()
        var inCode = false

        fun flushParagraph() {
            if (paragraph.isNotBlank()) {
                blocks.add(ReaderBlock.Paragraph(paragraph.toString().trim()))
                paragraph.clear()
            }
        }

        fun flushCode() {
            if (code.isNotBlank()) {
                blocks.add(ReaderBlock.Code(code.toString().trimEnd()))
                code.clear()
            }
        }

        markdown.replace("\r\n", "\n").split('\n').forEach { raw ->
            val trimmed = raw.trim()
            if (trimmed.startsWith("```")) {
                if (inCode) {
                    flushCode()
                } else {
                    flushParagraph()
                }
                inCode = !inCode
                return@forEach
            }
            if (inCode) {
                code.appendLine(raw)
                return@forEach
            }
            if (trimmed.isEmpty()) {
                flushParagraph()
                return@forEach
            }
            if (rule.matches(trimmed)) {
                flushParagraph()
                return@forEach
            }

            heading.matchEntire(trimmed)?.let { match ->
                flushParagraph()
                blocks.add(ReaderBlock.Heading(match.groupValues[1].length, match.groupValues[2].trim()))
                return@forEach
            }
            image.matchEntire(trimmed)?.let { match ->
                flushParagraph()
                val src = safeUrl(url, match.groupValues[2])
                if (src != null) {
                    blocks.add(ReaderBlock.Image(src, match.groupValues[1].trim()))
                } else {
                    blocks.add(ReaderBlock.Paragraph(match.groupValues[1].trim()))
                }
                return@forEach
            }
            list.matchEntire(raw)?.let { match ->
                flushParagraph()
                val marker = match.groupValues[2]
                blocks.add(
                    ReaderBlock.ListItem(
                        text = match.groupValues[3].trim(),
                        ordered = marker.first().isDigit(),
                        index = marker.takeWhile(Char::isDigit).toIntOrNull() ?: 1,
                        depth = match.groupValues[1].length / 2,
                    ),
                )
                return@forEach
            }
            quote.matchEntire(trimmed)?.let { match ->
                flushParagraph()
                blocks.add(ReaderBlock.Quote(match.groupValues[1].trim()))
                return@forEach
            }

            if (paragraph.isNotEmpty()) paragraph.append(' ')
            paragraph.append(trimmed)
        }
        flushParagraph()
        flushCode()
        return ReaderDocument(title = title, url = url, blocks = blocks)
    }

    private fun safeUrl(base: String, value: String): String? = runCatching {
        val raw = value.trim().removeSurrounding("<", ">")
        val candidate = URI(raw)
        val resolved = if (candidate.isAbsolute) candidate else URI(base).resolve(candidate)
        if (resolved.scheme !in listOf("http", "https") || resolved.host.isNullOrBlank()) {
            null
        } else {
            resolved.toString()
        }
    }.getOrNull()

    fun plainText(value: String): String = value
        .replace(Regex("!\\[([^]]*)]\\([^)]+\\)"), "$1")
        .replace(Regex("\\[([^]]+)]\\([^)]+\\)"), "$1")
        .replace(Regex("[*_`]"), "")
        .replace(Regex("\\s+"), " ")
        .trim()
}
