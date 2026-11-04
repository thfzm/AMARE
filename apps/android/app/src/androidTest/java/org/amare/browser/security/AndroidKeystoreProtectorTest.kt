package org.amare.browser.security

import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.After
import org.junit.Assert.fail
import org.junit.Test

class AndroidKeystoreProtectorTest {
    private val alias = "amare_test_${System.nanoTime()}"
    private val protector = AndroidKeystoreProtector(alias)

    @After
    fun cleanup() {
        protector.deleteKey()
    }

    @Test
    fun roundTripAndTamperRejection() {
        val plaintext = "sensitive-session-state".toByteArray()
        val blob = protector.protect("session-tab-v1", plaintext)
        assertArrayEquals(plaintext, protector.unprotect("session-tab-v1", blob))

        val tampered = blob.copyOf().also { it[it.lastIndex] = (it[it.lastIndex].toInt() xor 1).toByte() }
        try {
            protector.unprotect("session-tab-v1", tampered)
            fail("tampered blob was accepted")
        } catch (error: AndroidKeystoreProtector.KeystoreException) {
            assertEquals(
                AndroidKeystoreProtector.ErrorKind.INVALID_MATERIAL,
                error.kind,
            )
        }
    }

    @Test
    fun wrongKeyCannotDecrypt() {
        val other = AndroidKeystoreProtector("${alias}_other")
        try {
            val blob = protector.protect("context", "data".toByteArray())
            try {
                other.unprotect("context", blob)
                fail("blob was decrypted with a different key")
            } catch (error: AndroidKeystoreProtector.KeystoreException) {
                assertEquals(
                    AndroidKeystoreProtector.ErrorKind.KEY_INVALIDATED,
                    error.kind,
                )
            }
        } finally {
            other.deleteKey()
        }
    }

    @Test
    fun ivTamperIsRejected() {
        val blob = protector.protect("context", "data".toByteArray())
        val tampered = blob.copyOf().also {
            it[2] = (it[2].toInt() xor 1).toByte()
        }
        try {
            protector.unprotect("context", tampered)
            fail("tampered IV was accepted")
        } catch (error: AndroidKeystoreProtector.KeystoreException) {
            assertEquals(
                AndroidKeystoreProtector.ErrorKind.INVALID_MATERIAL,
                error.kind,
            )
        }
    }

    @Test
    fun wrongAadIsRejected() {
        val blob = protector.protect("context-a", "data".toByteArray())
        try {
            protector.unprotect("context-b", blob)
            fail("wrong AAD was accepted")
        } catch (error: AndroidKeystoreProtector.KeystoreException) {
            assertEquals(
                AndroidKeystoreProtector.ErrorKind.INVALID_MATERIAL,
                error.kind,
            )
        }
    }

    @Test
    fun keyDeletionIsReportedAsInvalidated() {
        val blob = protector.protect("context", "data".toByteArray())
        protector.deleteKey()
        try {
            protector.unprotect("context", blob)
            fail("missing key was accepted")
        } catch (error: AndroidKeystoreProtector.KeystoreException) {
            assertEquals(
                AndroidKeystoreProtector.ErrorKind.KEY_INVALIDATED,
                error.kind,
            )
        }
    }
}
