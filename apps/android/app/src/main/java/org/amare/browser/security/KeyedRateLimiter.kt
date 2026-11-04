package org.amare.browser.security

import java.util.ArrayDeque

class KeyedRateLimiter(
    private val maxEvents: Int,
    private val windowMillis: Long,
    private val clock: () -> Long = { System.currentTimeMillis() },
) {
    private val events = mutableMapOf<String, ArrayDeque<Long>>()

    @Synchronized
    fun tryAcquire(key: String): Boolean {
        require(maxEvents > 0) { "maxEvents must be positive" }
        require(windowMillis > 0) { "windowMillis must be positive" }
        val now = clock()
        val queue = events.getOrPut(key) { ArrayDeque() }
        while (queue.isNotEmpty() && now - queue.first > windowMillis) {
            queue.removeFirst()
        }
        if (queue.size >= maxEvents) return false
        queue.addLast(now)
        return true
    }

    @Synchronized
    fun clear(key: String) {
        events.remove(key)
    }
}
