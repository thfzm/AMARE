package org.amare.browser.data

data class SessionTabDraft(
    val url: String,
    val title: String,
    val sessionState: String?,
)

data class StoredSessionTab(
    val position: Int,
    val url: String,
    val title: String,
    val sessionState: String?,
)

data class StoredSession(
    val tabs: List<StoredSessionTab>,
    val activeIndex: Int,
    val cleanShutdown: Boolean,
)

data class SessionLoadResult(
    val session: StoredSession?,
    val error: String? = null,
    val keyInvalid: Boolean = false,
)
