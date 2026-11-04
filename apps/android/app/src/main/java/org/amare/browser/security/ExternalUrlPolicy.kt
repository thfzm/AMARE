package org.amare.browser.security

import java.net.URI

object ExternalUrlPolicy {
    fun isWebUrl(url: String?): Boolean {
        if (url.isNullOrBlank()) return false
        return runCatching {
            val uri = URI(url)
            (uri.scheme == "http" || uri.scheme == "https") &&
                !uri.host.isNullOrBlank()
        }.getOrDefault(false)
    }

    fun blockedByHttpsOnly(url: String, httpsOnly: Boolean): Boolean {
        if (!httpsOnly) return false
        return runCatching { URI(url).scheme == "http" }.getOrDefault(true)
    }
}
