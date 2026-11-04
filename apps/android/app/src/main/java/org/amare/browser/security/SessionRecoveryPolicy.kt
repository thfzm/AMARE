package org.amare.browser.security

object SessionRecoveryPolicy {
    fun activeIndex(savedIndex: Int, tabCount: Int): Int {
        if (tabCount <= 0) return 0
        return savedIndex.coerceIn(0, tabCount - 1)
    }

    fun isUnexpectedExit(cleanShutdown: Boolean): Boolean = !cleanShutdown
}
