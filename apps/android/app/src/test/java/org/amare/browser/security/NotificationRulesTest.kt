package org.amare.browser.security

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class NotificationRulesTest {
    @Test
    fun requiresHttpsOrigin() {
        assertTrue(NotificationRules.isAllowedOrigin("https://example.com"))
        assertFalse(NotificationRules.isAllowedOrigin("http://example.com"))
        assertFalse(NotificationRules.isAllowedOrigin(null))
    }

    @Test
    fun navigationOnlyUsesSameOriginSource() {
        assertEquals(
            "https://example.com/messages/1",
            NotificationRules.safeNavigationUrl(
                "https://example.com",
                "https://example.com/messages/1",
            ),
        )
        assertEquals(
            "https://example.com",
            NotificationRules.safeNavigationUrl(
                "https://example.com",
                "https://evil.example/phish",
            ),
        )
        assertEquals(
            "https://example.com",
            NotificationRules.safeNavigationUrl(
                "https://example.com",
                "javascript:alert(1)",
            ),
        )
    }
}
