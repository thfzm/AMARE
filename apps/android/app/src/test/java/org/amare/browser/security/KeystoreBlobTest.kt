package org.amare.browser.security

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class KeystoreBlobTest {
    @Test
    fun roundTripPreservesIvAndCiphertext() {
        val iv = ByteArray(KeystoreBlob.IV_LENGTH) { it.toByte() }
        val ciphertext = ByteArray(32) { (it + 10).toByte() }
        val parsed = KeystoreBlob.parse(KeystoreBlob.encode(iv, ciphertext))!!
        assertArrayEquals(iv, parsed.iv)
        assertArrayEquals(ciphertext, parsed.ciphertext)
    }

    @Test
    fun rejectsWrongVersionIvLengthAndTruncation() {
        val iv = ByteArray(KeystoreBlob.IV_LENGTH)
        val ciphertext = ByteArray(16)
        val valid = KeystoreBlob.encode(iv, ciphertext)

        val wrongVersion = valid.copyOf().also { it[0] = 9 }
        assertNull(KeystoreBlob.parse(wrongVersion))

        val wrongIvLength = valid.copyOf().also { it[1] = 8 }
        assertNull(KeystoreBlob.parse(wrongIvLength))

        assertNull(KeystoreBlob.parse(valid.copyOf(valid.size - 1)))
        assertNull(KeystoreBlob.parse(byteArrayOf(1, 12)))
    }

    @Test
    fun versionAndIvLengthAreExplicit() {
        val blob = KeystoreBlob.encode(ByteArray(KeystoreBlob.IV_LENGTH), ByteArray(16))
        assertEquals(KeystoreBlob.VERSION, blob[0])
        assertEquals(KeystoreBlob.IV_LENGTH.toByte(), blob[1])
    }
}
