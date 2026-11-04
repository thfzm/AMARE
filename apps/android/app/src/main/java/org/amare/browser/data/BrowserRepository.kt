package org.amare.browser.data

import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.database.sqlite.SQLiteDatabase
import android.os.Handler
import android.os.Looper
import android.util.Log
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import org.amare.browser.security.AndroidKeystoreProtector
import org.amare.browser.security.SessionRecoveryPolicy
import org.json.JSONObject

class BrowserRepository(context: Context) {
    private val database = BrowserDatabase(context.applicationContext)
    private val executor = Executors.newSingleThreadExecutor()
    private val mainHandler = Handler(Looper.getMainLooper())
    private val permissionCache = ConcurrentHashMap<String, String>()
    private val sessionProtector = AndroidKeystoreProtector()

    @Volatile
    private var closed = false

    init {
        executor.execute {
            runCatching {
                markInterruptedDownloadsFailed()
                loadPermissionCache()
            }.onFailure { Log.e(TAG, "Repository initialization failed", it) }
        }
    }

    fun saveSessionSnapshot(
        tabs: List<SessionTabDraft>,
        activeIndex: Int,
        cleanShutdown: Boolean,
        callback: () -> Unit = {},
    ) {
        background(
            work = {
                val db = database.writableDatabase
                val encryptedTabs = tabs.mapIndexed { position, tab ->
                    val payload = JSONObject()
                        .put("url", tab.url)
                        .put("title", tab.title)
                        .put("session_state", tab.sessionState ?: JSONObject.NULL)
                        .toString()
                        .toByteArray(Charsets.UTF_8)
                    Triple(
                        position,
                        sessionProtector.protect(SESSION_TAB_AAD, payload),
                        tab,
                    )
                }

                db.beginTransaction()
                try {
                    db.delete("session_tabs", "snapshot_id = ?", arrayOf(SNAPSHOT_ID.toString()))
                    db.delete("session_snapshots", "id = ?", arrayOf(SNAPSHOT_ID.toString()))
                    db.insertOrThrow(
                        "session_snapshots",
                        null,
                        ContentValues().apply {
                            put("id", SNAPSHOT_ID)
                            put("clean_shutdown", if (cleanShutdown) 1 else 0)
                            put("active_index", activeIndex.coerceAtLeast(0))
                            put("updated_at", System.currentTimeMillis())
                        },
                    )
                    encryptedTabs.forEach { (position, payload, _) ->
                        db.insertOrThrow(
                            "session_tabs",
                            null,
                            ContentValues().apply {
                                put("snapshot_id", SNAPSHOT_ID)
                                put("position", position)
                                put("payload", payload)
                            },
                        )
                    }
                    db.setTransactionSuccessful()
                } finally {
                    db.endTransaction()
                }
            },
            callback = { callback() },
        )
    }

    fun loadSessionSnapshot(callback: (SessionLoadResult) -> Unit) {
        background(
            work = {
                val db = database.readableDatabase
                var cleanShutdown = true
                var activeIndex = 0
                var hasSnapshot = false
                db.query(
                    "session_snapshots",
                    arrayOf("clean_shutdown", "active_index"),
                    "id = ?",
                    arrayOf(SNAPSHOT_ID.toString()),
                    null,
                    null,
                    null,
                    "1",
                ).use { cursor ->
                    if (cursor.moveToFirst()) {
                        hasSnapshot = true
                        cleanShutdown = cursor.getInt(0) != 0
                        activeIndex = cursor.getInt(1)
                    }
                }
                if (!hasSnapshot) return@background SessionLoadResult(null)

                val tabs = mutableListOf<StoredSessionTab>()
                var failedTabs = 0
                var keyInvalid = false
                var failure: String? = null
                db.query(
                    "session_tabs",
                    arrayOf("position", "payload"),
                    "snapshot_id = ?",
                    arrayOf(SNAPSHOT_ID.toString()),
                    null,
                    null,
                    "position ASC",
                ).use { cursor ->
                    while (cursor.moveToNext()) {
                        val position = cursor.getInt(0)
                        val payload = cursor.getBlob(1)
                        try {
                            val json = JSONObject(
                                String(
                                    sessionProtector.unprotect(SESSION_TAB_AAD, payload),
                                    Charsets.UTF_8,
                                ),
                            )
                            val url = json.optString("url")
                            if (url.isBlank()) {
                                failedTabs++
                                continue
                            }
                            tabs.add(
                                StoredSessionTab(
                                    position = position,
                                    url = url,
                                    title = json.optString("title", url),
                                    sessionState = json.optString("session_state")
                                        .takeIf { it.isNotBlank() && it != "null" },
                                ),
                            )
                        } catch (error: AndroidKeystoreProtector.KeystoreException) {
                            failedTabs++
                            failure = error.message
                            if (error.kind == AndroidKeystoreProtector.ErrorKind.KEY_INVALIDATED) {
                                keyInvalid = true
                                break
                            }
                        } catch (_: Throwable) {
                            failedTabs++
                            failure = "A saved tab could not be decoded."
                            Log.e(TAG, "Session tab decode failed; details suppressed")
                        }
                    }
                }

                if (keyInvalid) {
                    clearSessionSnapshotLocked(db)
                    return@background SessionLoadResult(
                        session = null,
                        error = "The saved session key is no longer available. Started a new session.",
                        keyInvalid = true,
                    )
                }

                if (tabs.isEmpty()) {
                    clearSessionSnapshotLocked(db)
                    return@background SessionLoadResult(
                        session = null,
                        error = failure ?: "No restorable tabs were found in the saved session.",
                    )
                }

                SessionLoadResult(
                    session = StoredSession(
                        tabs = tabs,
                        activeIndex = SessionRecoveryPolicy.activeIndex(activeIndex, tabs.size),
                        cleanShutdown = cleanShutdown,
                    ),
                    error = if (failedTabs > 0) {
                        "Some saved tabs could not be restored."
                    } else {
                        null
                    },
                )
            },
            callback = callback,
        )
    }

    fun markSessionRunning() {
        background(
            work = {
                database.writableDatabase.update(
                    "session_snapshots",
                    ContentValues().apply { put("clean_shutdown", 0) },
                    "id = ?",
                    arrayOf(SNAPSHOT_ID.toString()),
                )
            },
        )
    }

    fun clearSessionSnapshot(callback: () -> Unit = {}) {
        background(
            work = {
                val db = database.writableDatabase
                db.beginTransaction()
                try {
                    clearSessionSnapshotLocked(db)
                    db.setTransactionSuccessful()
                } finally {
                    db.endTransaction()
                }
            },
            callback = { callback() },
        )
    }

    private fun clearSessionSnapshotLocked(db: SQLiteDatabase) {
        db.delete("session_tabs", "snapshot_id = ?", arrayOf(SNAPSHOT_ID.toString()))
        db.delete("session_snapshots", "id = ?", arrayOf(SNAPSHOT_ID.toString()))
    }

    fun loadAllBookmarks(callback: (List<Bookmark>) -> Unit) {
        background(
            work = {
                val result = mutableListOf<Bookmark>()
                database.readableDatabase.query(
                    "bookmarks",
                    arrayOf("id", "url", "title", "created_at"),
                    null,
                    null,
                    null,
                    null,
                    "created_at DESC",
                    "1000",
                ).use { cursor ->
                    while (cursor.moveToNext()) result.add(cursor.toBookmark())
                }
                result
            },
            callback = callback,
        )
    }

    fun addBookmark(url: String, title: String, callback: () -> Unit = {}) {
        background(
            work = {
                val values = ContentValues().apply {
                    put("url", url)
                    put("title", title.ifBlank { url })
                    put("created_at", System.currentTimeMillis())
                }
                database.writableDatabase.insertWithOnConflict(
                    "bookmarks",
                    null,
                    values,
                    android.database.sqlite.SQLiteDatabase.CONFLICT_REPLACE,
                )
            },
            callback = { callback() },
        )
    }

    fun removeBookmarkByUrl(url: String, callback: () -> Unit = {}) {
        background(
            work = {
                database.writableDatabase.delete("bookmarks", "url = ?", arrayOf(url))
            },
            callback = { callback() },
        )
    }

    fun removeBookmark(id: Long, callback: () -> Unit = {}) {
        background(
            work = {
                database.writableDatabase.delete("bookmarks", "id = ?", arrayOf(id.toString()))
            },
            callback = { callback() },
        )
    }

    fun recordHistory(url: String, title: String) {
        background(
            work = {
                val values = ContentValues().apply {
                    put("url", url)
                    put("title", title.ifBlank { url })
                    put("visited_at", System.currentTimeMillis())
                }
                database.writableDatabase.insert("history", null, values)
                database.writableDatabase.execSQL(
                    "DELETE FROM history WHERE id NOT IN " +
                        "(SELECT id FROM history ORDER BY visited_at DESC LIMIT 5000)",
                )
            },
        )
    }

    fun loadHistory(query: String, callback: (List<HistoryEntry>) -> Unit) {
        background(
            work = {
                val result = mutableListOf<HistoryEntry>()
                val trimmed = query.trim()
                val selection = if (trimmed.isEmpty()) null else "url LIKE ? OR title LIKE ?"
                val args = if (trimmed.isEmpty()) {
                    null
                } else {
                    val pattern = "%$trimmed%"
                    arrayOf(pattern, pattern)
                }
                database.readableDatabase.query(
                    "history",
                    arrayOf("id", "url", "title", "visited_at"),
                    selection,
                    args,
                    null,
                    null,
                    "visited_at DESC",
                    "500",
                ).use { cursor ->
                    while (cursor.moveToNext()) result.add(cursor.toHistoryEntry())
                }
                result
            },
            callback = callback,
        )
    }

    fun removeHistory(id: Long, callback: () -> Unit = {}) {
        background(
            work = {
                database.writableDatabase.delete("history", "id = ?", arrayOf(id.toString()))
            },
            callback = { callback() },
        )
    }

    fun clearHistory(callback: () -> Unit = {}) {
        background(
            work = { database.writableDatabase.delete("history", null, null) },
            callback = { callback() },
        )
    }

    fun createDownload(
        fileName: String,
        sourceUrl: String,
        mimeType: String,
        totalBytes: Long,
        callback: (DownloadRecord) -> Unit,
    ) {
        background(
            work = {
                val createdAt = System.currentTimeMillis()
                val values = ContentValues().apply {
                    put("file_name", fileName)
                    put("source_url", sourceUrl)
                    put("mime_type", mimeType)
                    put("received_bytes", 0L)
                    put("total_bytes", totalBytes)
                    put("status", DownloadStatus.RUNNING.name)
                    put("created_at", createdAt)
                }
                val id = database.writableDatabase.insert("downloads", null, values)
                DownloadRecord(
                    id = id,
                    fileName = fileName,
                    sourceUrl = sourceUrl,
                    mimeType = mimeType,
                    receivedBytes = 0,
                    totalBytes = totalBytes,
                    status = DownloadStatus.RUNNING,
                    localUri = null,
                    error = null,
                    createdAt = createdAt,
                )
            },
            callback = callback,
        )
    }

    fun updateDownloadProgress(id: Long, receivedBytes: Long, totalBytes: Long) {
        background(
            work = {
                val values = ContentValues().apply {
                    put("received_bytes", receivedBytes)
                    put("total_bytes", totalBytes)
                }
                database.writableDatabase.update(
                    "downloads",
                    values,
                    "id = ?",
                    arrayOf(id.toString()),
                )
            },
        )
    }

    fun completeDownload(id: Long, localUri: String, receivedBytes: Long, callback: () -> Unit = {}) {
        background(
            work = {
                val values = ContentValues().apply {
                    put("status", DownloadStatus.COMPLETE.name)
                    put("local_uri", localUri)
                    put("received_bytes", receivedBytes)
                    put("error", null as String?)
                }
                database.writableDatabase.update(
                    "downloads",
                    values,
                    "id = ?",
                    arrayOf(id.toString()),
                )
            },
            callback = { callback() },
        )
    }

    fun failDownload(id: Long, error: String, callback: () -> Unit = {}) {
        background(
            work = {
                val values = ContentValues().apply {
                    put("status", DownloadStatus.FAILED.name)
                    put("error", error)
                }
                database.writableDatabase.update(
                    "downloads",
                    values,
                    "id = ?",
                    arrayOf(id.toString()),
                )
            },
            callback = { callback() },
        )
    }

    fun loadDownloads(callback: (List<DownloadRecord>) -> Unit) {
        background(
            work = {
                val result = mutableListOf<DownloadRecord>()
                database.readableDatabase.query(
                    "downloads",
                    arrayOf(
                        "id",
                        "file_name",
                        "source_url",
                        "mime_type",
                        "received_bytes",
                        "total_bytes",
                        "status",
                        "local_uri",
                        "error",
                        "created_at",
                    ),
                    null,
                    null,
                    null,
                    null,
                    "created_at DESC",
                    "200",
                ).use { cursor ->
                    while (cursor.moveToNext()) result.add(cursor.toDownloadRecord())
                }
                result
            },
            callback = callback,
        )
    }

    fun removeDownload(id: Long, callback: () -> Unit = {}) {
        background(
            work = {
                database.writableDatabase.delete("downloads", "id = ?", arrayOf(id.toString()))
            },
            callback = { callback() },
        )
    }

    fun getPermission(origin: String, permission: String): String? =
        permissionCache[permissionKey(origin, permission)]

    fun setPermission(
        origin: String,
        permission: String,
        decision: String,
        callback: () -> Unit = {},
    ) {
        permissionCache[permissionKey(origin, permission)] = decision
        background(
            work = {
                val values = ContentValues().apply {
                    put("origin", origin)
                    put("permission", permission)
                    put("decision", decision)
                    put("updated_at", System.currentTimeMillis())
                }
                database.writableDatabase.insertWithOnConflict(
                    "site_permissions",
                    null,
                    values,
                    android.database.sqlite.SQLiteDatabase.CONFLICT_REPLACE,
                )
            },
            callback = { callback() },
        )
    }

    fun removePermission(origin: String, permission: String, callback: () -> Unit = {}) {
        permissionCache.remove(permissionKey(origin, permission))
        background(
            work = {
                database.writableDatabase.delete(
                    "site_permissions",
                    "origin = ? AND permission = ?",
                    arrayOf(origin, permission),
                )
            },
            callback = { callback() },
        )
    }

    fun loadPermissions(callback: (List<SitePermissionRecord>) -> Unit) {
        background(
            work = {
                val result = mutableListOf<SitePermissionRecord>()
                database.readableDatabase.query(
                    "site_permissions",
                    arrayOf("origin", "permission", "decision", "updated_at"),
                    null,
                    null,
                    null,
                    null,
                    "updated_at DESC",
                    "500",
                ).use { cursor ->
                    while (cursor.moveToNext()) {
                        val record = SitePermissionRecord(
                            origin = cursor.getString(0),
                            permission = cursor.getString(1),
                            decision = cursor.getString(2),
                            updatedAt = cursor.getLong(3),
                        )
                        result.add(record)
                        permissionCache[permissionKey(record.origin, record.permission)] =
                            record.decision
                    }
                }
                result
            },
            callback = callback,
        )
    }

    fun close() {
        closed = true
        executor.execute { runCatching { database.close() } }
        executor.shutdown()
    }

    private fun markInterruptedDownloadsFailed() {
        val values = ContentValues().apply {
            put("status", DownloadStatus.FAILED.name)
            put("error", "Interrupted by app restart.")
        }
        database.writableDatabase.update(
            "downloads",
            values,
            "status = ?",
            arrayOf(DownloadStatus.RUNNING.name),
        )
    }

    private fun loadPermissionCache() {
        database.readableDatabase.query(
            "site_permissions",
            arrayOf("origin", "permission", "decision"),
            null,
            null,
            null,
            null,
            null,
        ).use { cursor ->
            while (cursor.moveToNext()) {
                permissionCache[permissionKey(cursor.getString(0), cursor.getString(1))] =
                    cursor.getString(2)
            }
        }
    }

    private fun permissionKey(origin: String, permission: String): String = "$origin|$permission"

    private fun <T> background(work: () -> T, callback: ((T) -> Unit)? = null) {
        if (closed) return
        executor.execute {
            val result = runCatching(work)
            result.onFailure { Log.e(TAG, "Browser database operation failed", it) }
            val value = result.getOrNull()
            if (callback != null && value != null) {
                mainHandler.post {
                    if (!closed) callback(value)
                }
            }
        }
    }

    private fun Cursor.toBookmark(): Bookmark = Bookmark(
        id = getLong(0),
        url = getString(1),
        title = getString(2),
        createdAt = getLong(3),
    )

    private fun Cursor.toHistoryEntry(): HistoryEntry = HistoryEntry(
        id = getLong(0),
        url = getString(1),
        title = getString(2),
        visitedAt = getLong(3),
    )

    private fun Cursor.toDownloadRecord(): DownloadRecord = DownloadRecord(
        id = getLong(0),
        fileName = getString(1),
        sourceUrl = getString(2),
        mimeType = getString(3),
        receivedBytes = getLong(4),
        totalBytes = getLong(5),
        status = runCatching { DownloadStatus.valueOf(getString(6)) }
            .getOrDefault(DownloadStatus.FAILED),
        localUri = getString(7),
        error = getString(8),
        createdAt = getLong(9),
    )

    companion object {
        private const val TAG = "BrowserRepository"
        private const val SNAPSHOT_ID = 1L
        private const val SESSION_TAB_AAD = "amare.session.tab.v1"
    }
}
