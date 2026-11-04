package org.amare.browser.security

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyPermanentlyInvalidatedException
import android.security.keystore.KeyProperties
import android.security.keystore.StrongBoxUnavailableException
import java.security.KeyStore
import java.security.UnrecoverableKeyException
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class AndroidKeystoreProtector(
    private val alias: String = DEFAULT_ALIAS,
) {
    enum class ErrorKind {
        KEY_INVALIDATED,
        INVALID_MATERIAL,
        UNAVAILABLE,
    }

    class KeystoreException(
        val kind: ErrorKind,
        message: String,
        cause: Throwable? = null,
    ) : Exception(message, cause)

    fun protect(context: String, plaintext: ByteArray): ByteArray {
        try {
            val key = getOrCreateKey()
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.ENCRYPT_MODE, key)
            cipher.updateAAD(aad(context))
            val ciphertext = cipher.doFinal(plaintext)
            return KeystoreBlob.encode(cipher.iv, ciphertext)
        } catch (error: KeystoreException) {
            throw error
        } catch (error: Throwable) {
            throw KeystoreException(
                ErrorKind.UNAVAILABLE,
                "Android Keystore encryption failed.",
                error,
            )
        }
    }

    fun unprotect(context: String, protected: ByteArray): ByteArray {
        val parsed = KeystoreBlob.parse(protected)
            ?: throw KeystoreException(ErrorKind.INVALID_MATERIAL, "Invalid keystore blob.")
        val key = loadKey()
            ?: throw KeystoreException(
                ErrorKind.KEY_INVALIDATED,
                "The Android Keystore key is missing.",
            )
        return try {
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(
                Cipher.DECRYPT_MODE,
                key,
                GCMParameterSpec(GCM_TAG_BITS, parsed.iv),
            )
            cipher.updateAAD(aad(context))
            cipher.doFinal(parsed.ciphertext)
        } catch (error: KeyPermanentlyInvalidatedException) {
            throw KeystoreException(
                ErrorKind.KEY_INVALIDATED,
                "The Android Keystore key was invalidated.",
                error,
            )
        } catch (error: AEADBadTagException) {
            throw KeystoreException(
                ErrorKind.INVALID_MATERIAL,
                "Protected data authentication failed.",
                error,
            )
        } catch (error: Throwable) {
            throw KeystoreException(
                ErrorKind.INVALID_MATERIAL,
                "Android Keystore decryption failed.",
                error,
            )
        }
    }

    fun hasKey(): Boolean = runCatching { loadKey() != null }.getOrDefault(false)

    fun deleteKey() {
        runCatching {
            keyStore().deleteEntry(alias)
        }
    }

    private fun loadKey(): SecretKey? {
        return try {
            keyStore().getKey(alias, null) as? SecretKey
        } catch (error: UnrecoverableKeyException) {
            throw KeystoreException(
                ErrorKind.KEY_INVALIDATED,
                "The Android Keystore key is unrecoverable.",
                error,
            )
        } catch (error: KeyPermanentlyInvalidatedException) {
            throw KeystoreException(
                ErrorKind.KEY_INVALIDATED,
                "The Android Keystore key was invalidated.",
                error,
            )
        }
    }

    private fun getOrCreateKey(): SecretKey {
        val existing = try {
            loadKey()
        } catch (error: KeystoreException) {
            if (error.kind == ErrorKind.KEY_INVALIDATED) {
                deleteKey()
                null
            } else {
                throw error
            }
        }
        if (existing != null) return existing

        try {
            return generateKey(strongBox = true)
        } catch (_: StrongBoxUnavailableException) {
            // Fall back to the device's TEE-backed key.
        }
        return generateKey(strongBox = false)
    }

    private fun generateKey(strongBox: Boolean): SecretKey {
        val builder = KeyGenParameterSpec.Builder(
            alias,
            KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
        )
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(KEY_SIZE_BITS)
            .setRandomizedEncryptionRequired(true)
        if (strongBox) {
            builder.setIsStrongBoxBacked(true)
        }
        val generator = KeyGenerator.getInstance(
            KeyProperties.KEY_ALGORITHM_AES,
            ANDROID_KEYSTORE,
        )
        generator.init(builder.build())
        return generator.generateKey()
    }

    private fun keyStore(): KeyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply {
        load(null)
    }

    private fun aad(context: String): ByteArray = "$AAD_PREFIX$context".toByteArray(Charsets.UTF_8)

    companion object {
        private const val ANDROID_KEYSTORE = "AndroidKeyStore"
        private const val TRANSFORMATION = "AES/GCM/NoPadding"
        private const val KEY_SIZE_BITS = 256
        private const val GCM_TAG_BITS = 128
        private const val AAD_PREFIX = "amare.keystore.v1|"
        const val DEFAULT_ALIAS = "amare_browser_data_v1"
    }
}
