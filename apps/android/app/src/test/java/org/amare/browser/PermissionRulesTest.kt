package org.amare.browser

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.mozilla.geckoview.GeckoSession

class PermissionRulesTest {
    @Test
    fun extractsOriginWithoutPathOrQuery() {
        assertEquals("https://example.com", PermissionRules.originFor("https://example.com/a?b=1"))
        assertEquals("http://example.com:8080", PermissionRules.originFor("http://example.com:8080/x"))
    }

    @Test
    fun rejectsNonWebOrigins() {
        assertNull(PermissionRules.originFor("about:blank"))
        assertNull(PermissionRules.originFor(""))
        assertNull(PermissionRules.originFor(null))
    }

    @Test
    fun mapsPromptedContentPermissionsOnly() {
        assertEquals(
            PermissionRules.GEOLOCATION,
            PermissionRules.contentPermissionKey(
                GeckoSession.PermissionDelegate.PERMISSION_GEOLOCATION,
            ),
        )
        assertEquals(
            PermissionRules.NOTIFICATIONS,
            PermissionRules.contentPermissionKey(
                GeckoSession.PermissionDelegate.PERMISSION_DESKTOP_NOTIFICATION,
            ),
        )
        assertNull(
            PermissionRules.contentPermissionKey(
                GeckoSession.PermissionDelegate.PERMISSION_AUTOPLAY_AUDIBLE,
            ),
        )
    }

    @Test
    fun buildsCameraAndMicrophoneKeys() {
        assertEquals(listOf(PermissionRules.CAMERA), PermissionRules.mediaKeys(true, false))
        assertEquals(
            listOf(PermissionRules.CAMERA, PermissionRules.MICROPHONE),
            PermissionRules.mediaKeys(true, true),
        )
        assertTrue(PermissionRules.mediaKeys(false, false).isEmpty())
    }

    @Test
    fun togglesDecisionsAndMapsValues() {
        assertEquals(PermissionRules.ALLOW, PermissionRules.toggleDecision(null))
        assertEquals(PermissionRules.DENY, PermissionRules.toggleDecision(PermissionRules.ALLOW))
        assertEquals(
            GeckoSession.PermissionDelegate.ContentPermission.VALUE_ALLOW,
            PermissionRules.decisionValue(PermissionRules.ALLOW),
        )
        assertEquals(
            GeckoSession.PermissionDelegate.ContentPermission.VALUE_DENY,
            PermissionRules.decisionValue(PermissionRules.DENY),
        )
    }
}
