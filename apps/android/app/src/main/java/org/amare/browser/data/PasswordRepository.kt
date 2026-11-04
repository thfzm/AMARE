package org.amare.browser.data

import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.database.sqlite.SQLiteDatabase
import android.os.Handler
import android.os.Looper
import android.util.Log
import java.security.MessageDigest
import java.util.concurrent.Executors
import org.amare.browser.security.AndroidKeystoreProtector
import org.amare.browser.security.PasswordRules
import org.json.JSONObject

class PasswordRepository(context: Context) {
    private val database = PasswordDatabase(context.applicationContext)
    private val executor = Executors.newSingleThreadExecutor()
    private val mainHandler = Handler(Looper.getMainLooper())
    private val protector = AndroidKeystoreProtector(PASSWORD_ALIAS)

    @Volatile
    private var closed = false

    private var pendingNotice: String? = null

    fun consumeNotice(): String? = synchronized(this) {
        pendingNotice.also { pendingNotice = null }
    }

    fun listAll(callback: (List<PasswordSummary>) -> Unit) {
        background(
            work = {
                val result = mutableListOf<PasswordSummary>()
                database.readableDatabase.query(
                    "credentials",
                    arrayOf("id", "payload", "updated_at"),
                    null,
                    null,
                    null,
                    null,
                    "updated_at DESC",
                    "1000",
                ).use { cursor ->
                    while (cursor.moveToNext()) {
                        val json = decryptOrNull(cursor.getBlob(1)) ?: continue
                        val origin = json.optString("origin")
                        val username = json.optString("username")
                        if (origin.isBlank()) continue
                        result.add(
                            PasswordSummary(
                                id = cursor.getLong(0),
                                origin = origin,
                                username = username,
                                updatedAt = cursor.getLong(2),
                            ),
                        )
                    }
                }
                result
            },
            callback = callback,
        )
    }

    fun listForOrigin(origin: String, callback: (List<PasswordSummary>) -> Unit) {
        background(
            work = {
                val result = mutableListOf<PasswordSummary>()
                database.readableDatabase.query(
                    "credentials",
                    arrayOf("id", "payload", "updated_at"),
                    "origin_key = ?",
                    arrayOf(originKey(origin)),
                    null,
                    null,
                    "updated_at DESC",
                    "100",
                ).use { cursor ->
                    while (cursor.moveToNext()) {
                        val json = decryptOrNull(cursor.getBlob(1)) ?: continue
                        if (json.optString("origin") != origin) continue
                        result.add(
                            PasswordSummary(
                                id = cursor.getLong(0),
                                origin = origin,
                                username = json.optString("username"),
                                updatedAt = cursor.getLong(2),
                            ),
                        )
                    }
                }
                result
            },
            callback = callback,
        )
    }

    fun load(id: Long, callback: (StoredLogin?) -> Unit) {
        background(
            work = {
                database.readableDatabase.query(
                    "credentials",
                    arrayOf("id", "payload", "updated_at"),
                    "id = ?",
                    arrayOf(id.toString()),
                    null,
                    null,
                    null,
                    "1",
                ).use { cursor ->
                    if (!cursor.moveToFirst()) return@background null
                    val json = decryptOrNull(cursor.getBlob(1)) ?: return@background null
                    StoredLogin(
                        id = cursor.getLong(0),
                        origin = json.optString("origin"),
                        username = json.optString("username"),
                        password = json.optString("password"),
                        updatedAt = cursor.getLong(2),
                    )
                }
            },
            callback = callback,
        )
    }

    fun save(
        origin: String,
        username: String,
        password: String,
        callback: (Boolean) -> Unit = {},
    ) {
        background(
            work = {
                if (!PasswordRules.isSecureOrigin(origin)) return@background false
                val normalizedUsername = PasswordRules.normalizeUsername(username)
                if (normalizedUsername.isBlank() || password.isEmpty()) return@background false
                val db = database.writableDatabase
                val existing = findIdByUsername(db, origin, normalizedUsername)
                val payload = encryptPayload(origin, normalizedUsername, password)
                val now = System.currentTimeMillis()
                if (existing != null) {
                    db.update(
                        "credentials",
                        ContentValues().apply {
                            put("payload", payload)
                            put("updated_at", now)
                        },
                        "id = ?",
                        arrayOf(existing.toString()),
                    )
                } else {
                    db.insertOrThrow(
                        "credentials",
                        null,
                        ContentValues().apply {
                            put("origin_key", originKey(origin))
                            put("payload", payload)
                            put("created_at", now)
                            put("updated_at", now)
                        },
                    )
                }
                true
            },
            callback = callback,
        )
    }

    fun update(
        id: Long,
        origin: String,
        username: String,
        password: String,
        callback: (Boolean) -> Unit = {},
    ) {
        background(
            work = {
                if (!PasswordRules.isSecureOrigin(origin)) return@background false
                val normalizedUsername = PasswordRules.normalizeUsername(username)
                if (normalizedUsername.isBlank() || password.isEmpty()) return@background false
                database.writableDatabase.update(
                    "credentials",
                    ContentValues().apply {
                        put("origin_key", originKey(origin))
                        put("payload", encryptPayload(origin, normalizedUsername, password))
                        put("updated_at", System.currentTimeMillis())
                    },
                    "id = ?",
                    arrayOf(id.toString()),
                ) > 0
            },
            callback = callback,
        )
    }

    fun delete(id: Long, callback: () -> Unit = {}) {
        background(
            work = {
                database.writableDatabase.delete(
                    "credentials",
                    "id = ?",
                    arrayOf(id.toString()),
                )
            },
            callback = { callback() },
        )
    }

    fun isSaveDisabled(origin: String, callback: (Boolean) -> Unit) {
        background(
            work = {
                database.readableDatabase.query(
                    "site_settings",
                    arrayOf("save_disabled"),
                    "origin_key = ?",
                    arrayOf(originKey(origin)),
                    null,
                    null,
                    null,
                    "1",
                ).use { cursor ->
                    cursor.moveToFirst() && cursor.getInt(0) != 0
                }
            },
            callback = callback,
        )
    }

    fun setSaveDisabled(origin: String, disabled: Boolean, callback: () -> Unit = {}) {
        background(
            work = {
                database.writableDatabase.insertWithOnConflict(
                    "site_settings",
                    null,
                    ContentValues().apply {
                        put("origin_key", originKey(origin))
                        put("save_disabled", if (disabled) 1 else 0)
                        put("updated_at", System.currentTimeMillis())
                    },
                    SQLiteDatabase.CONFLICT_REPLACE,
                )
            },
            callback = { callback() },
        )
    }

    fun close() {
        closed = true
        executor.execute { runCatching { database.close() } }
        executor.shutdown()
    }

    private fun findIdByUsername(
        db: SQLiteDatabase,
        origin: String,
        username: String,
    ): Long? {
        db.query(
            "credentials",
            arrayOf("id", "payload"),
            "origin_key = ?",
            arrayOf(originKey(origin)),
            null,
            null,
            "updated_at DESC",
        ).use { cursor ->
            while (cursor.moveToNext()) {
                val json = decryptOrNull(cursor.getBlob(1)) ?: continue
                if (
                    json.optString("origin") == origin &&
                    json.optString("username") == username
                ) {
                    return cursor.getLong(0)
                }
            }
        }
        return null
    }

    private fun encryptPayload(origin: String, username: String, password: String): ByteArray =
        protector.protect(
            PASSWORD_AAD,
            JSONObject()
                .put("origin", origin)
                .put("username", username)
                .put("password", password)
                .toString()
                .toByteArray(Charsets.UTF_8),
        )

    private fun decryptOrNull(payload: ByteArray): JSONObject? = try {
        JSONObject(
            String(
                protector.unprotect(PASSWORD_AAD, payload),
                Charsets.UTF_8,
            ),
        )
    } catch (error: AndroidKeystoreProtector.KeystoreException) {
        if (error.kind == AndroidKeystoreProtector.ErrorKind.KEY_INVALIDATED) {
            setNotice("Saved passwords could not be decrypted because the password-store key is unavailable.")
        } else {
            setNotice("Some saved passwords could not be authenticated.")
        }
        null
    } catch (_: Throwable) {
        setNotice("Some saved passwords could not be decoded.")
        null
    }

    private fun setNotice(message: String) {
        synchronized(this) {
            if (pendingNotice == null) pendingNotice = message
        }
    }

    private fun originKey(origin: String): String {
        val digest = MessageDigest.getInstance("SHA-256")
            .digest(origin.lowercase().toByteArray(Charsets.UTF_8))
        return digest.joinToString("") { "%02x".format(it.toInt() and 0xFF) }
    }

    private fun <T> background(work: () -> T, callback: ((T) -> Unit)? = null) {
        if (closed) return
        executor.execute {
            val result = runCatching(work)
            result.onFailure { Log.e(TAG, "Password store operation failed") }
            if (callback != null && result.isSuccess) {
                @Suppress("UNCHECKED_CAST")
                val value = result.getOrNull() as T
                mainHandler.post {
                    if (!closed) callback(value)
                }
            }
        }
    }

    companion object {
        private const val TAG = "PasswordRepository"
        private const val PASSWORD_ALIAS = "amare_passwords_v1"
        private const val PASSWORD_AAD = "amare.password.v1"
    }
}
