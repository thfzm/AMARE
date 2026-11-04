package org.amare.browser.security

import java.net.URI

object PopupRules {
    fun isAllowedPopupUri(uri: String?): Boolean {
        if (uri.isNullOrBlank()) return false
        if (uri == "about:blank") return true
        return runCatching {
            val parsed = URI(uri)
            (parsed.scheme == "http" || parsed.scheme == "https") &&
                !parsed.host.isNullOrBlank()
        }.getOrDefault(false)
    }

    fun popupMarker(openerTabId: Long): String = "tab:$openerTabId"
}
