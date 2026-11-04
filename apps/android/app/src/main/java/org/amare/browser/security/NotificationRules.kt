package org.amare.browser.security

import org.amare.browser.PermissionRules

object NotificationRules {
    fun isAllowedOrigin(origin: String?): Boolean =
        origin != null && origin.startsWith("https://", ignoreCase = true)

    fun safeNavigationUrl(origin: String, source: String?): String {
        if (source.isNullOrBlank()) return origin
        val sourceOrigin = PermissionRules.originFor(source) ?: return origin
        return if (sourceOrigin == origin && isAllowedOrigin(sourceOrigin)) source else origin
    }
}
