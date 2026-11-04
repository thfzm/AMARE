package org.amare.browser.security

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PopupRulesTest {
    @Test
    fun allowsOnlyHttpHttpsAndBlank() {
        assertTrue(PopupRules.isAllowedPopupUri("https://example.com"))
        assertTrue(PopupRules.isAllowedPopupUri("http://example.com"))
        assertTrue(PopupRules.isAllowedPopupUri("about:blank"))
        assertFalse(PopupRules.isAllowedPopupUri("javascript:alert(1)"))
        assertFalse(PopupRules.isAllowedPopupUri("file:///etc/passwd"))
        assertFalse(PopupRules.isAllowedPopupUri("https://"))
        assertFalse(PopupRules.isAllowedPopupUri(null))
    }
}
