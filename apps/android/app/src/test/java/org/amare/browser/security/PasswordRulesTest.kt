package org.amare.browser.security

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PasswordRulesTest {
    @Test
    fun requiresExactHttpsOriginForAutofill() {
        assertTrue(
            PasswordRules.sameOrigin(
                "https://example.com",
                "https://example.com/login",
            ),
        )
        assertFalse(
            PasswordRules.sameOrigin(
                "https://example.com",
                "https://evil.example/login",
            ),
        )
        assertFalse(
            PasswordRules.sameOrigin(
                "http://example.com",
                "http://example.com/login",
            ),
        )
    }

    @Test
    fun rejectsCrossOriginFormActions() {
        assertTrue(PasswordRules.formOriginAllowed("https://example.com", null))
        assertTrue(
            PasswordRules.formOriginAllowed(
                "https://example.com",
                "https://example.com/login",
            ),
        )
        assertFalse(
            PasswordRules.formOriginAllowed(
                "https://example.com",
                "https://other.example/login",
            ),
        )
    }

    @Test
    fun nodeDomainMustBelongToTopLevelHost() {
        assertTrue(PasswordRules.nodeDomainMatches("example.com", "https://example.com"))
        assertTrue(PasswordRules.nodeDomainMatches("example.com", "https://login.example.com"))
        assertFalse(PasswordRules.nodeDomainMatches("evil.example", "https://example.com"))
        assertFalse(PasswordRules.nodeDomainMatches("", "https://example.com"))
    }

    @Test
    fun normalizesUsernameWhitespaceOnly() {
        org.junit.Assert.assertEquals("alice", PasswordRules.normalizeUsername("  alice  "))
    }
}
