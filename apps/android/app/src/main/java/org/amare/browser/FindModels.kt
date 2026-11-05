package org.amare.browser

data class FindState(
    val query: String = "",
    val current: Int = 0,
    val total: Int = 0,
    val found: Boolean = false,
    val wrapped: Boolean = false,
    val loading: Boolean = false,
)
