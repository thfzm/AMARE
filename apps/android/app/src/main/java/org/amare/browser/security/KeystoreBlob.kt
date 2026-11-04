package org.amare.browser.security

object KeystoreBlob {
    const val VERSION: Byte = 1
    const val IV_LENGTH: Int = 12

    data class Parsed(
        val iv: ByteArray,
        val ciphertext: ByteArray,
    )

    fun encode(iv: ByteArray, ciphertext: ByteArray): ByteArray {
        require(iv.size == IV_LENGTH) { "AES-GCM IV must be $IV_LENGTH bytes" }
        return ByteArray(2 + iv.size + ciphertext.size).also { out ->
            out[0] = VERSION
            out[1] = IV_LENGTH.toByte()
            iv.copyInto(out, destinationOffset = 2)
            ciphertext.copyInto(out, destinationOffset = 2 + iv.size)
        }
    }

    fun parse(blob: ByteArray): Parsed? {
        if (blob.size < 2 + IV_LENGTH + 16) return null
        if (blob[0] != VERSION) return null
        val ivLength = blob[1].toInt() and 0xFF
        if (ivLength != IV_LENGTH) return null
        if (blob.size < 2 + ivLength + 16) return null
        val iv = blob.copyOfRange(2, 2 + ivLength)
        val ciphertext = blob.copyOfRange(2 + ivLength, blob.size)
        return Parsed(iv = iv, ciphertext = ciphertext)
    }
}
