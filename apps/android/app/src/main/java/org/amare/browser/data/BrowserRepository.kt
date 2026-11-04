package org.amare.browser.data

import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.database.sqlite.SQLiteDatabase
import android.os.Handler
import android.os.Looper
import android.util.Log
import java.security.MessageDigest
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
    private var bookmarksCache: List<Bookmark>? = null

    @Volatile
    private var historyCache: List<HistoryEntry>? = null

    @Volatile
    private var downloadsCache: List<DownloadRecord>? = null

    @Volatile
    private var closed = false

    private var pendingDataNotice: String? = null

    init {
        executor.execute {
            runCatching {
                migrateLegacyPlaintextTables()
                markInterruptedDownloadsFailed()
                loadPermissionCache()
            }.onFailure {
                setDataNotice(
                    "Encrypted browser data could not be initialized. Existing data was left in place.",
                )
                Log.e(TAG, "Encrypted repository initialization failed")
            }
        }
    }

    fun consumeDataNotice(): String? = synchronized(this) {
        pendingDataNotice.also { pendingDataNotice = null }
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
                    Pair(position, sessionProtector.protect(SESSION_TAB_AAD, payload))
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
                    encryptedTabs.forEach { (position, payload) ->
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

    fun loadAllBookmarks(callback: (List<Bookmark>) -> Unit) {
        background(
            work = {
                bookmarksCache ?: readAllBookmarks(database.readableDatabase).also {
                    bookmarksCache = it
                }
            },
            callback = callback,
        )
    }

    fun addBookmark(url: String, title: String, callback: () -> Unit = {}) {
        background(
            work = {
                val db = database.writableDatabase
                val cached = bookmarksCache ?: readAllBookmarks(db).also { bookmarksCache = it }
                if (cached.any { it.url == url }) {
                    return@background
                }
                val payload = encryptJson(
                    BOOKMARK_AAD,
                    JSONObject()
                        .put("url", url)
                        .put("title", title.ifBlank { url }),
                )
                db.insertOrThrow(
                    "bookmarks_enc",
                    null,
                    ContentValues().apply {
                        put("payload", payload)
                        put("created_at", System.currentTimeMillis())
                    },
                )
                bookmarksCache = null
            },
            callback = { callback() },
        )
    }

    fun removeBookmarkByUrl(url: String, callback: () -> Unit = {}) {
        background(
            work = {
                val db = database.writableDatabase
                val cached = bookmarksCache ?: readAllBookmarks(db).also { bookmarksCache = it }
                val ids = cached.filter { it.url == url }.map { it.id }
                ids.forEach { id ->
                    db.delete("bookmarks_enc", "id = ?", arrayOf(id.toString()))
                }
                bookmarksCache = cached.filterNot { it.url == url }
            },
            callback = { callback() },
        )
    }

    fun removeBookmark(id: Long, callback: () -> Unit = {}) {
        background(
            work = {
                database.writableDatabase.delete(
                    "bookmarks_enc",
                    "id = ?",
                    arrayOf(id.toString()),
                )
                bookmarksCache = bookmarksCache?.filterNot { it.id == id }
            },
            callback = { callback() },
        )
    }

    fun recordHistory(url: String, title: String) {
        background(
            work = {
                val payload = encryptJson(
                    HISTORY_AAD,
                    JSONObject()
                        .put("url", url)
                        .put("title", title.ifBlank { url }),
                )
                val db = database.writableDatabase
                db.insertOrThrow(
                    "history_enc",
                    null,
                    ContentValues().apply {
                        put("payload", payload)
                        put("visited_at", System.currentTimeMillis())
                    },
                )
                db.execSQL(
                    "DELETE FROM history_enc WHERE id NOT IN " +
                        "(SELECT id FROM history_enc ORDER BY visited_at DESC LIMIT 5000)",
                )
                historyCache = null
            },
        )
    }

    fun loadHistory(query: String, callback: (List<HistoryEntry>) -> Unit) {
        background(
            work = {
                val all = historyCache ?: readAllHistory(database.readableDatabase)
                    .also { historyCache = it }
                val trimmed = query.trim()
                if (trimmed.isEmpty()) {
                    all.take(HISTORY_RESULT_LIMIT)
                } else {
                    all.asSequence()
                        .filter {
                            it.url.contains(trimmed, ignoreCase = true) ||
                                it.title.contains(trimmed, ignoreCase = true)
                        }
                        .take(HISTORY_RESULT_LIMIT)
                        .toList()
                }
            },
            callback = callback,
        )
    }

    fun removeHistory(id: Long, callback: () -> Unit = {}) {
        background(
            work = {
                database.writableDatabase.delete(
                    "history_enc",
                    "id = ?",
                    arrayOf(id.toString()),
                )
                historyCache = historyCache?.filterNot { it.id == id }
            },
            callback = { callback() },
        )
    }

    fun clearHistoryData(callback: (Boolean) -> Unit) {
        backgroundResult(
            work = {
                database.writableDatabase.delete("history_enc", null, null)
                historyCache = emptyList()
            },
            callback = callback,
        )
    }

    fun clearAllBookmarks(callback: (Boolean) -> Unit) {
        backgroundResult(
            work = {
                database.writableDatabase.delete("bookmarks_enc", null, null)
                bookmarksCache = emptyList()
            },
            callback = callback,
        )
    }

    fun clearAllPermissions(callback: (Boolean) -> Unit) {
        backgroundResult(
            work = {
                database.writableDatabase.delete("site_permissions_enc", null, null)
                permissionCache.clear()
            },
            callback = callback,
        )
    }

    fun removePermissionsForOrigin(origin: String, callback: (Boolean) -> Unit) {
        backgroundResult(
            work = {
                database.writableDatabase.delete(
                    "site_permissions_enc",
                    "origin_key = ?",
                    arrayOf(originKey(origin)),
                )
                permissionCache.keys
                    .filter { it.startsWith("$origin|") }
                    .forEach { permissionCache.remove(it) }
            },
            callback = callback,
        )
    }

    fun clearAllUserData(callback: (Boolean) -> Unit) {
        backgroundResult(
            work = {
                val db = database.writableDatabase
                db.beginTransaction()
                try {
                    db.delete("bookmarks_enc", null, null)
                    db.delete("history_enc", null, null)
                    db.delete("downloads_enc", null, null)
                    db.delete("site_permissions_enc", null, null)
                    clearSessionSnapshotLocked(db)
                    db.setTransactionSuccessful()
                } finally {
                    db.endTransaction()
                }
                bookmarksCache = emptyList()
                historyCache = emptyList()
                downloadsCache = emptyList()
                permissionCache.clear()
            },
            callback = callback,
        )
    }

    fun clearHistory(callback: () -> Unit = {}) {
        background(
            work = {
                database.writableDatabase.delete("history_enc", null, null)
                historyCache = emptyList()
            },
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
                val payload = encryptJson(
                    DOWNLOAD_AAD,
                    JSONObject()
                        .put("file_name", fileName)
                        .put("source_url", sourceUrl)
                        .put("mime_type", mimeType)
                        .put("local_uri", JSONObject.NULL)
                        .put("error", JSONObject.NULL),
                )
                val id = database.writableDatabase.insertOrThrow(
                    "downloads_enc",
                    null,
                    ContentValues().apply {
                        put("payload", payload)
                        put("received_bytes", 0L)
                        put("total_bytes", totalBytes)
                        put("status", DownloadStatus.RUNNING.name)
                        put("created_at", createdAt)
                    },
                )
                downloadsCache = null
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
                database.writableDatabase.update(
                    "downloads_enc",
                    ContentValues().apply {
                        put("received_bytes", receivedBytes)
                        put("total_bytes", totalBytes)
                    },
                    "id = ?",
                    arrayOf(id.toString()),
                )
                downloadsCache = null
            },
        )
    }

    fun completeDownload(id: Long, localUri: String, receivedBytes: Long, callback: () -> Unit = {}) {
        background(
            work = {
                val db = database.writableDatabase
                val payload = readDownloadPayload(db, id)
                    ?: throw IllegalStateException("download payload missing")
                payload.put("local_uri", localUri)
                payload.put("error", JSONObject.NULL)
                db.update(
                    "downloads_enc",
                    ContentValues().apply {
                        put("payload", encryptJson(DOWNLOAD_AAD, payload))
                        put("status", DownloadStatus.COMPLETE.name)
                        put("received_bytes", receivedBytes)
                    },
                    "id = ?",
                    arrayOf(id.toString()),
                )
                downloadsCache = null
            },
            callback = { callback() },
        )
    }

    fun failDownload(id: Long, error: String, callback: () -> Unit = {}) {
        background(
            work = {
                val db = database.writableDatabase
                val payload = readDownloadPayload(db, id)
                    ?: throw IllegalStateException("download payload missing")
                payload.put("error", error)
                db.update(
                    "downloads_enc",
                    ContentValues().apply {
                        put("payload", encryptJson(DOWNLOAD_AAD, payload))
                        put("status", DownloadStatus.FAILED.name)
                    },
                    "id = ?",
                    arrayOf(id.toString()),
                )
                downloadsCache = null
            },
            callback = { callback() },
        )
    }

    fun loadDownloads(callback: (List<DownloadRecord>) -> Unit) {
        background(
            work = {
                downloadsCache ?: readAllDownloads(database.readableDatabase).also {
                    downloadsCache = it
                }
            },
            callback = callback,
        )
    }

    fun removeDownload(id: Long, callback: () -> Unit = {}) {
        background(
            work = {
                database.writableDatabase.delete(
                    "downloads_enc",
                    "id = ?",
                    arrayOf(id.toString()),
                )
                downloadsCache = downloadsCache?.filterNot { it.id == id }
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
                val payload = encryptJson(
                    PERMISSION_AAD,
                    JSONObject()
                        .put("origin", origin)
                        .put("decision", decision),
                )
                database.writableDatabase.insertWithOnConflict(
                    "site_permissions_enc",
                    null,
                    ContentValues().apply {
                        put("origin_key", originKey(origin))
                        put("permission", permission)
                        put("payload", payload)
                        put("updated_at", System.currentTimeMillis())
                    },
                    SQLiteDatabase.CONFLICT_REPLACE,
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
                    "site_permissions_enc",
                    "origin_key = ? AND permission = ?",
                    arrayOf(originKey(origin), permission),
                )
            },
            callback = { callback() },
        )
    }

    fun loadPermissions(callback: (List<SitePermissionRecord>) -> Unit) {
        background(
            work = { readAllPermissions(database.readableDatabase) },
            callback = callback,
        )
    }

    fun close() {
        closed = true
        executor.execute { runCatching { database.close() } }
        executor.shutdown()
    }

    private fun migrateLegacyPlaintextTables() {
        val db = database.writableDatabase
        val hasBookmarks = tableExists(db, LEGACY_BOOKMARKS)
        val hasHistory = tableExists(db, LEGACY_HISTORY)
        val hasDownloads = tableExists(db, LEGACY_DOWNLOADS)
        val hasPermissions = tableExists(db, LEGACY_PERMISSIONS)
        if (!hasBookmarks && !hasHistory && !hasDownloads && !hasPermissions) return

        val bookmarks = mutableListOf<Triple<ByteArray, Long, Long>>()
        if (hasBookmarks) {
            db.query(
                LEGACY_BOOKMARKS,
                arrayOf("id", "url", "title", "created_at"),
                null,
                null,
                null,
                null,
                "created_at ASC",
            ).use { cursor ->
                while (cursor.moveToNext()) {
                    bookmarks.add(
                        Triple(
                            encryptJson(
                                BOOKMARK_AAD,
                                JSONObject()
                                    .put("url", cursor.getString(1))
                                    .put("title", cursor.getString(2)),
                            ),
                            cursor.getLong(0),
                            cursor.getLong(3),
                        ),
                    )
                }
            }
        }

        val history = mutableListOf<Triple<ByteArray, Long, Long>>()
        if (hasHistory) {
            db.query(
                LEGACY_HISTORY,
                arrayOf("id", "url", "title", "visited_at"),
                null,
                null,
                null,
                null,
                "visited_at ASC",
            ).use { cursor ->
                while (cursor.moveToNext()) {
                    history.add(
                        Triple(
                            encryptJson(
                                HISTORY_AAD,
                                JSONObject()
                                    .put("url", cursor.getString(1))
                                    .put("title", cursor.getString(2)),
                            ),
                            cursor.getLong(0),
                            cursor.getLong(3),
                        ),
                    )
                }
            }
        }

        val downloads = mutableListOf<LegacyDownload>()
        if (hasDownloads) {
            db.query(
                LEGACY_DOWNLOADS,
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
                "created_at ASC",
            ).use { cursor ->
                while (cursor.moveToNext()) {
                    val payload = JSONObject()
                        .put("file_name", cursor.getString(1))
                        .put("source_url", cursor.getString(2))
                        .put("mime_type", cursor.getString(3))
                        .put("local_uri", cursor.getString(7) ?: JSONObject.NULL)
                        .put("error", cursor.getString(8) ?: JSONObject.NULL)
                    downloads.add(
                        LegacyDownload(
                            id = cursor.getLong(0),
                            payload = encryptJson(DOWNLOAD_AAD, payload),
                            receivedBytes = cursor.getLong(4),
                            totalBytes = cursor.getLong(5),
                            status = cursor.getString(6),
                            createdAt = cursor.getLong(9),
                        ),
                    )
                }
            }
        }

        val permissions = mutableListOf<LegacyPermission>()
        if (hasPermissions) {
            db.query(
                LEGACY_PERMISSIONS,
                arrayOf("origin", "permission", "decision", "updated_at"),
                null,
                null,
                null,
                null,
                "updated_at ASC",
            ).use { cursor ->
                while (cursor.moveToNext()) {
                    val origin = cursor.getString(0)
                    permissions.add(
                        LegacyPermission(
                            originKey = originKey(origin),
                            permission = cursor.getString(1),
                            payload = encryptJson(
                                PERMISSION_AAD,
                                JSONObject()
                                    .put("origin", origin)
                                    .put("decision", cursor.getString(2)),
                            ),
                            updatedAt = cursor.getLong(3),
                        ),
                    )
                }
            }
        }

        db.beginTransaction()
        try {
            if (hasBookmarks) {
                db.delete("bookmarks_enc", null, null)
                bookmarks.forEach { (payload, _, createdAt) ->
                    db.insertOrThrow(
                        "bookmarks_enc",
                        null,
                        ContentValues().apply {
                            put("payload", payload)
                            put("created_at", createdAt)
                        },
                    )
                }
                db.execSQL("DROP TABLE IF EXISTS $LEGACY_BOOKMARKS")
            }
            if (hasHistory) {
                db.delete("history_enc", null, null)
                history.forEach { (payload, _, visitedAt) ->
                    db.insertOrThrow(
                        "history_enc",
                        null,
                        ContentValues().apply {
                            put("payload", payload)
                            put("visited_at", visitedAt)
                        },
                    )
                }
                db.execSQL("DROP TABLE IF EXISTS $LEGACY_HISTORY")
            }
            if (hasDownloads) {
                db.delete("downloads_enc", null, null)
                downloads.forEach { download ->
                    db.insertOrThrow(
                        "downloads_enc",
                        null,
                        ContentValues().apply {
                            put("payload", download.payload)
                            put("received_bytes", download.receivedBytes)
                            put("total_bytes", download.totalBytes)
                            put("status", download.status)
                            put("created_at", download.createdAt)
                        },
                    )
                }
                db.execSQL("DROP TABLE IF EXISTS $LEGACY_DOWNLOADS")
            }
            if (hasPermissions) {
                db.delete("site_permissions_enc", null, null)
                permissions.forEach { permission ->
                    db.insertWithOnConflict(
                        "site_permissions_enc",
                        null,
                        ContentValues().apply {
                            put("origin_key", permission.originKey)
                            put("permission", permission.permission)
                            put("payload", permission.payload)
                            put("updated_at", permission.updatedAt)
                        },
                        SQLiteDatabase.CONFLICT_REPLACE,
                    )
                }
                db.execSQL("DROP TABLE IF EXISTS $LEGACY_PERMISSIONS")
            }
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }

        bookmarksCache = null
        historyCache = null
        downloadsCache = null
    }

    private fun markInterruptedDownloadsFailed() {
        val db = database.writableDatabase
        val rows = mutableListOf<Pair<Long, JSONObject>>()
        db.query(
            "downloads_enc",
            arrayOf("id", "payload"),
            "status = ?",
            arrayOf(DownloadStatus.RUNNING.name),
            null,
            null,
            null,
        ).use { cursor ->
            while (cursor.moveToNext()) {
                val payload = runCatching {
                    decryptJson(DOWNLOAD_AAD, cursor.getBlob(1))
                }.getOrNull() ?: continue
                payload.put("error", "Interrupted by app restart.")
                rows.add(cursor.getLong(0) to payload)
            }
        }
        rows.forEach { (id, payload) ->
            db.update(
                "downloads_enc",
                ContentValues().apply {
                    put("payload", encryptJson(DOWNLOAD_AAD, payload))
                    put("status", DownloadStatus.FAILED.name)
                },
                "id = ?",
                arrayOf(id.toString()),
            )
        }
        downloadsCache = null
    }

    private fun loadPermissionCache() {
        runCatching {
            readAllPermissions(database.readableDatabase).forEach { record ->
                permissionCache[permissionKey(record.origin, record.permission)] =
                    record.decision
            }
        }.onFailure {
            setDataNotice("Saved site permissions could not be decrypted.")
            Log.e(TAG, "Permission cache load failed")
        }
    }

    private fun readAllBookmarks(db: SQLiteDatabase): List<Bookmark> {
        val result = mutableListOf<Bookmark>()
        db.query(
            "bookmarks_enc",
            arrayOf("id", "payload", "created_at"),
            null,
            null,
            null,
            null,
            "created_at DESC",
            READ_LIMIT.toString(),
        ).use { cursor ->
            while (cursor.moveToNext()) {
                val json = decryptJsonOrNull(BOOKMARK_AAD, cursor.getBlob(1), "bookmarks")
                    ?: continue
                val url = json.optString("url")
                if (url.isBlank()) continue
                result.add(
                    Bookmark(
                        id = cursor.getLong(0),
                        url = url,
                        title = json.optString("title", url),
                        createdAt = cursor.getLong(2),
                    ),
                )
            }
        }
        return result
    }

    private fun readAllHistory(db: SQLiteDatabase): List<HistoryEntry> {
        val result = mutableListOf<HistoryEntry>()
        db.query(
            "history_enc",
            arrayOf("id", "payload", "visited_at"),
            null,
            null,
            null,
            null,
            "visited_at DESC",
            HISTORY_CACHE_LIMIT.toString(),
        ).use { cursor ->
            while (cursor.moveToNext()) {
                val json = decryptJsonOrNull(HISTORY_AAD, cursor.getBlob(1), "history")
                    ?: continue
                val url = json.optString("url")
                if (url.isBlank()) continue
                result.add(
                    HistoryEntry(
                        id = cursor.getLong(0),
                        url = url,
                        title = json.optString("title", url),
                        visitedAt = cursor.getLong(2),
                    ),
                )
            }
        }
        return result
    }

    private fun readAllDownloads(db: SQLiteDatabase): List<DownloadRecord> {
        val result = mutableListOf<DownloadRecord>()
        db.query(
            "downloads_enc",
            arrayOf(
                "id",
                "payload",
                "received_bytes",
                "total_bytes",
                "status",
                "created_at",
            ),
            null,
            null,
            null,
            null,
            "created_at DESC",
            DOWNLOAD_CACHE_LIMIT.toString(),
        ).use { cursor ->
            while (cursor.moveToNext()) {
                val json = decryptJsonOrNull(DOWNLOAD_AAD, cursor.getBlob(1), "downloads")
                    ?: continue
                result.add(
                    DownloadRecord(
                        id = cursor.getLong(0),
                        fileName = json.optString("file_name", "download"),
                        sourceUrl = json.optString("source_url"),
                        mimeType = json.optString("mime_type", "application/octet-stream"),
                        receivedBytes = cursor.getLong(2),
                        totalBytes = cursor.getLong(3),
                        status = runCatching { DownloadStatus.valueOf(cursor.getString(4)) }
                            .getOrDefault(DownloadStatus.FAILED),
                        localUri = json.optString("local_uri").takeIf {
                            it.isNotBlank() && it != "null"
                        },
                        error = json.optString("error").takeIf {
                            it.isNotBlank() && it != "null"
                        },
                        createdAt = cursor.getLong(5),
                    ),
                )
            }
        }
        return result
    }

    private fun readDownloadPayload(db: SQLiteDatabase, id: Long): JSONObject? {
        db.query(
            "downloads_enc",
            arrayOf("payload"),
            "id = ?",
            arrayOf(id.toString()),
            null,
            null,
            null,
            "1",
        ).use { cursor ->
            if (!cursor.moveToFirst()) return null
            return runCatching {
                decryptJson(DOWNLOAD_AAD, cursor.getBlob(0))
            }.getOrNull()
        }
    }

    private fun readAllPermissions(db: SQLiteDatabase): List<SitePermissionRecord> {
        val result = mutableListOf<SitePermissionRecord>()
        db.query(
            "site_permissions_enc",
            arrayOf("permission", "payload", "updated_at"),
            null,
            null,
            null,
            null,
            "updated_at DESC",
            PERMISSION_CACHE_LIMIT.toString(),
        ).use { cursor ->
            while (cursor.moveToNext()) {
                val json = decryptJsonOrNull(
                    PERMISSION_AAD,
                    cursor.getBlob(1),
                    "site permissions",
                ) ?: continue
                val origin = json.optString("origin")
                if (origin.isBlank()) continue
                val record = SitePermissionRecord(
                    origin = origin,
                    permission = cursor.getString(0),
                    decision = json.optString("decision"),
                    updatedAt = cursor.getLong(2),
                )
                result.add(record)
                permissionCache[permissionKey(record.origin, record.permission)] =
                    record.decision
            }
        }
        return result
    }

    private fun decryptJsonOrNull(
        context: String,
        payload: ByteArray,
        dataLabel: String,
    ): JSONObject? = try {
        decryptJson(context, payload)
    } catch (error: AndroidKeystoreProtector.KeystoreException) {
        if (error.kind == AndroidKeystoreProtector.ErrorKind.KEY_INVALIDATED) {
            setDataNotice(
                "Saved $dataLabel could not be decrypted because the Android Keystore key is unavailable.",
            )
        } else {
            setDataNotice("Some saved $dataLabel could not be authenticated.")
        }
        null
    } catch (_: Throwable) {
        setDataNotice("Some saved $dataLabel could not be decoded.")
        null
    }

    private fun setDataNotice(message: String) {
        synchronized(this) {
            if (pendingDataNotice == null) pendingDataNotice = message
        }
    }

    private fun encryptJson(context: String, json: JSONObject): ByteArray =
        sessionProtector.protect(
            context,
            json.toString().toByteArray(Charsets.UTF_8),
        )

    private fun decryptJson(context: String, payload: ByteArray): JSONObject =
        JSONObject(
            String(
                sessionProtector.unprotect(context, payload),
                Charsets.UTF_8,
            ),
        )

    private fun tableExists(db: SQLiteDatabase, table: String): Boolean {
        db.query(
            "sqlite_master",
            arrayOf("name"),
            "type = 'table' AND name = ?",
            arrayOf(table),
            null,
            null,
            null,
            "1",
        ).use { cursor ->
            return cursor.moveToFirst()
        }
    }

    private fun permissionKey(origin: String, permission: String): String = "$origin|$permission"

    private fun originKey(origin: String): String {
        val digest = MessageDigest.getInstance("SHA-256")
            .digest(origin.lowercase().toByteArray(Charsets.UTF_8))
        return digest.joinToString("") { "%02x".format(it.toInt() and 0xFF) }
    }

    private fun clearSessionSnapshotLocked(db: SQLiteDatabase) {
        db.delete("session_tabs", "snapshot_id = ?", arrayOf(SNAPSHOT_ID.toString()))
        db.delete("session_snapshots", "id = ?", arrayOf(SNAPSHOT_ID.toString()))
    }

    private fun backgroundResult(work: () -> Unit, callback: (Boolean) -> Unit) {
        if (closed) {
            callback(false)
            return
        }
        executor.execute {
            val ok = runCatching(work).onFailure {
                Log.e(TAG, "Encrypted browser database operation failed")
            }.isSuccess
            mainHandler.post {
                if (!closed) callback(ok)
            }
        }
    }

    private fun <T> background(work: () -> T, callback: ((T) -> Unit)? = null) {
        if (closed) return
        executor.execute {
            val result = runCatching(work)
            result.onFailure {
                Log.e(TAG, "Encrypted browser database operation failed")
            }
            val value = result.getOrNull()
            if (callback != null && value != null) {
                mainHandler.post {
                    if (!closed) callback(value)
                }
            }
        }
    }

    private data class LegacyDownload(
        val id: Long,
        val payload: ByteArray,
        val receivedBytes: Long,
        val totalBytes: Long,
        val status: String,
        val createdAt: Long,
    )

    private data class LegacyPermission(
        val originKey: String,
        val permission: String,
        val payload: ByteArray,
        val updatedAt: Long,
    )

    companion object {
        private const val TAG = "BrowserRepository"
        private const val SNAPSHOT_ID = 1L
        private const val SESSION_TAB_AAD = "amare.session.tab.v1"
        private const val BOOKMARK_AAD = "amare.bookmark.v1"
        private const val HISTORY_AAD = "amare.history.v1"
        private const val DOWNLOAD_AAD = "amare.download.v1"
        private const val PERMISSION_AAD = "amare.site-permission.v1"
        private const val LEGACY_BOOKMARKS = "bookmarks"
        private const val LEGACY_HISTORY = "history"
        private const val LEGACY_DOWNLOADS = "downloads"
        private const val LEGACY_PERMISSIONS = "site_permissions"
        private const val READ_LIMIT = 1000
        private const val HISTORY_CACHE_LIMIT = 5000
        private const val HISTORY_RESULT_LIMIT = 500
        private const val DOWNLOAD_CACHE_LIMIT = 200
        private const val PERMISSION_CACHE_LIMIT = 500
    }
}
