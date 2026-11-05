package org.amare.browser

enum class AiService(
    val id: String,
    val label: String,
    val url: String,
) {
    CHATGPT("chatgpt", "ChatGPT", "https://chatgpt.com/"),
    GEMINI("gemini", "Gemini", "https://gemini.google.com/app"),
    CLAUDE("claude", "Claude", "https://claude.ai/new"),
    ;

    companion object {
        fun fromId(id: String?): AiService = entries.firstOrNull { it.id == id } ?: CHATGPT
    }
}
