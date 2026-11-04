package org.amare.browser.data

data class PasswordSummary(
    val id: Long,
    val origin: String,
    val username: String,
    val updatedAt: Long,
)

data class StoredLogin(
    val id: Long,
    val origin: String,
    val username: String,
    val password: String,
    val updatedAt: Long,
)
