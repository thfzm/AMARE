package org.amare.browser.security

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SessionRecoveryPolicyTest {
    @Test
    fun clampsSavedActiveIndex() {
        assertEquals(0, SessionRecoveryPolicy.activeIndex(-4, 3))
        assertEquals(1, SessionRecoveryPolicy.activeIndex(1, 3))
        assertEquals(2, SessionRecoveryPolicy.activeIndex(9, 3))
        assertEquals(0, SessionRecoveryPolicy.activeIndex(2, 0))
    }

    @Test
    fun detectsUnexpectedExit() {
        assertTrue(SessionRecoveryPolicy.isUnexpectedExit(false))
        assertFalse(SessionRecoveryPolicy.isUnexpectedExit(true))
    }
}
