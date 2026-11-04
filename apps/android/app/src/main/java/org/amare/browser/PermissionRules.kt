package org.amare.browser

import java.net.URI
import org.mozilla.geckoview.GeckoSession

object PermissionRules {
    const val ALLOW = "allow"
    const val DENY = "deny"

    const val GEOLOCATION = "geolocation"
    const val NOTIFICATIONS = "notifications"
    const val PERSISTENT_STORAGE = "persistent-storage"
    const val STORAGE_ACCESS = "storage-access"
    const val CAMERA = "camera"
    const val MICROPHONE = "microphone"

    fun originFor(url: String?): String? {
        if (url.isNullOrBlank()) return null
        return runCatching {
            val uri = URI(url)
            val scheme = uri.scheme?.lowercase()
            if (scheme != "http" && scheme != "https") return@runCatching null
            val host = uri.host?.lowercase() ?: return@runCatching null
            val authority = if (host.contains(':')) "[$host]" else host
            if (uri.port == -1) "$scheme://$authority" else "$scheme://$authority:${uri.port}"
        }.getOrNull()
    }

    fun contentPermissionKey(permission: Int): String? = when (permission) {
        GeckoSession.PermissionDelegate.PERMISSION_GEOLOCATION -> GEOLOCATION
        GeckoSession.PermissionDelegate.PERMISSION_DESKTOP_NOTIFICATION -> NOTIFICATIONS
        GeckoSession.PermissionDelegate.PERMISSION_PERSISTENT_STORAGE -> PERSISTENT_STORAGE
        GeckoSession.PermissionDelegate.PERMISSION_STORAGE_ACCESS -> STORAGE_ACCESS
        else -> null
    }

    fun mediaKeys(hasVideo: Boolean, hasAudio: Boolean): List<String> = buildList {
        if (hasVideo) add(CAMERA)
        if (hasAudio) add(MICROPHONE)
    }

    fun label(permission: String): String = when (permission) {
        GEOLOCATION -> "Location"
        NOTIFICATIONS -> "Notifications"
        PERSISTENT_STORAGE -> "Persistent storage"
        STORAGE_ACCESS -> "Third-party storage access"
        CAMERA -> "Camera"
        MICROPHONE -> "Microphone"
        else -> permission
    }

    fun decisionValue(decision: String): Int = when (decision) {
        ALLOW -> GeckoSession.PermissionDelegate.ContentPermission.VALUE_ALLOW
        DENY -> GeckoSession.PermissionDelegate.ContentPermission.VALUE_DENY
        else -> GeckoSession.PermissionDelegate.ContentPermission.VALUE_PROMPT
    }

    fun toggleDecision(current: String?): String = if (current == ALLOW) DENY else ALLOW
}
