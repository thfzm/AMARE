package org.amare.browser.security

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class KeyedRateLimiterTest {
    @Test
    fun limitsEventsWithinWindowAndRecoversAfterWindow() {
        var now = 1_000L
        val limiter = KeyedRateLimiter(maxEvents = 3, windowMillis = 10_000) { now }
        assertTrue(limiter.tryAcquire("a"))
        assertTrue(limiter.tryAcquire("a"))
        assertTrue(limiter.tryAcquire("a"))
        assertFalse(limiter.tryAcquire("a"))
        assertTrue(limiter.tryAcquire("b"))

        now += 10_001
        assertTrue(limiter.tryAcquire("a"))
    }

    @Test
    fun separatesKeys() {
        val limiter = KeyedRateLimiter(maxEvents = 1, windowMillis = 10_000) { 5_000L }
        assertTrue(limiter.tryAcquire("a"))
        assertFalse(limiter.tryAcquire("a"))
        assertTrue(limiter.tryAcquire("b"))
    }
}
