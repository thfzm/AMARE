package org.amare.browser.data

import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper

class BrowserDatabase(context: Context) : SQLiteOpenHelper(
    context.applicationContext,
    DATABASE_NAME,
    null,
    DATABASE_VERSION,
) {
    init {
        setWriteAheadLoggingEnabled(true)
    }

    override fun onCreate(db: SQLiteDatabase) {
        createEncryptedTables(db)
        db.execSQL(
            """
            CREATE TABLE site_permissions (
                origin TEXT NOT NULL,
                permission TEXT NOT NULL,
                decision TEXT NOT NULL,
                updated_at INTEGER NOT NULL,
                PRIMARY KEY (origin, permission)
            )
            """.trimIndent(),
        )
        createSessionTables(db)
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        if (oldVersion < 2) {
            createSessionTables(db)
        }
        if (oldVersion < 3) {
            createEncryptedTables(db)
        }
    }

    private fun createEncryptedTables(db: SQLiteDatabase) {
        db.execSQL(
            """
            CREATE TABLE IF NOT EXISTS bookmarks_enc (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                payload BLOB NOT NULL,
                created_at INTEGER NOT NULL
            )
            """.trimIndent(),
        )
        db.execSQL(
            "CREATE INDEX IF NOT EXISTS idx_bookmarks_enc_created " +
                "ON bookmarks_enc(created_at DESC)",
        )
        db.execSQL(
            """
            CREATE TABLE IF NOT EXISTS history_enc (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                payload BLOB NOT NULL,
                visited_at INTEGER NOT NULL
            )
            """.trimIndent(),
        )
        db.execSQL(
            "CREATE INDEX IF NOT EXISTS idx_history_enc_visited " +
                "ON history_enc(visited_at DESC)",
        )
        db.execSQL(
            """
            CREATE TABLE IF NOT EXISTS downloads_enc (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                payload BLOB NOT NULL,
                received_bytes INTEGER NOT NULL DEFAULT 0,
                total_bytes INTEGER NOT NULL DEFAULT -1,
                status TEXT NOT NULL,
                created_at INTEGER NOT NULL
            )
            """.trimIndent(),
        )
        db.execSQL(
            """
            CREATE TABLE IF NOT EXISTS site_permissions_enc (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                origin_key TEXT NOT NULL,
                permission TEXT NOT NULL,
                payload BLOB NOT NULL,
                updated_at INTEGER NOT NULL,
                UNIQUE (origin_key, permission)
            )
            """.trimIndent(),
        )
    }

    private fun createSessionTables(db: SQLiteDatabase) {
        db.execSQL(
            """
            CREATE TABLE IF NOT EXISTS session_snapshots (
                id INTEGER PRIMARY KEY,
                clean_shutdown INTEGER NOT NULL,
                active_index INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            )
            """.trimIndent(),
        )
        db.execSQL(
            """
            CREATE TABLE IF NOT EXISTS session_tabs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                snapshot_id INTEGER NOT NULL,
                position INTEGER NOT NULL,
                payload BLOB NOT NULL
            )
            """.trimIndent(),
        )
        db.execSQL(
            "CREATE INDEX IF NOT EXISTS idx_session_tabs_position " +
                "ON session_tabs(snapshot_id, position)",
        )
    }

    companion object {
        private const val DATABASE_NAME = "amare_browser.db"
        private const val DATABASE_VERSION = 3
    }
}
