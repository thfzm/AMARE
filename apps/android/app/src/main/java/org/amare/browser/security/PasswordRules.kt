package org.amare.browser.security

import java.net.URI
import org.amare.browser.PermissionRules

object PasswordRules {
    fun originFor(url: String?): String? = PermissionRules.originFor(url)

    fun isSecureOrigin(origin: String?): Boolean =
        origin != null && origin.startsWith("https://", ignoreCase = true)

    fun normalizeUsername(username: String): String = username.trim()

    fun sameOrigin(storedOrigin: String?, currentUrl: String?): Boolean {
        val current = originFor(currentUrl) ?: return false
        return storedOrigin != null && storedOrigin == current && isSecureOrigin(current)
    }

    fun formOriginAllowed(origin: String, formActionOrigin: String?): Boolean {
        if (formActionOrigin.isNullOrBlank()) return true
        val actionOrigin = originFor(formActionOrigin) ?: return false
        return actionOrigin == origin && isSecureOrigin(origin)
    }

    fun nodeDomainMatches(nodeDomain: String?, origin: String): Boolean {
        if (nodeDomain.isNullOrBlank()) return false
        val host = runCatching { URI(origin).host?.lowercase() }.getOrNull() ?: return false
        val domain = nodeDomain.lowercase()
            .removePrefix("https://")
            .removePrefix("http://")
            .substringBefore('/')
            .substringBefore(':')
        if (domain.isBlank()) return false
        return host == domain || host.endsWith(".$domain")
    }
}
