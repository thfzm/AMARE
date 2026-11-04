package org.amare.browser.security

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ExternalUrlPolicyTest {
    @Test
    fun acceptsOnlyHttpAndHttpsWithHosts() {
        assertTrue(ExternalUrlPolicy.isWebUrl("https://example.com/path"))
        assertTrue(ExternalUrlPolicy.isWebUrl("http://example.com"))
        assertFalse(ExternalUrlPolicy.isWebUrl("javascript:alert(1)"))
        assertFalse(ExternalUrlPolicy.isWebUrl("file:///etc/passwd"))
        assertFalse(ExternalUrlPolicy.isWebUrl("https://"))
        assertFalse(ExternalUrlPolicy.isWebUrl(null))
    }

    @Test
    fun appliesHttpsOnlyToExternalHttpLinks() {
        assertTrue(ExternalUrlPolicy.blockedByHttpsOnly("http://example.com", true))
        assertFalse(ExternalUrlPolicy.blockedByHttpsOnly("https://example.com", true))
        assertFalse(ExternalUrlPolicy.blockedByHttpsOnly("http://example.com", false))
    }
}
