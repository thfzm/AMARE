package org.amare.browser

object DownloadNaming {
    private const val MAX_NAME_LENGTH = 120
    private const val FORBIDDEN = "<>:\"/\\|?*"

    fun sanitize(rawName: String, fallback: String = "download.bin"): String {
        val leaf = rawName.substringAfterLast('/').substringAfterLast('\\')
        val cleaned = buildString {
            for (character in leaf) {
                append(
                    when {
                        character.isISOControl() -> '_'
                        character in FORBIDDEN -> '_'
                        else -> character
                    },
                )
            }
        }.trim().trim('.', ' ')

        if (cleaned.isEmpty()) return fallback
        return cleaned.take(MAX_NAME_LENGTH)
    }
}
