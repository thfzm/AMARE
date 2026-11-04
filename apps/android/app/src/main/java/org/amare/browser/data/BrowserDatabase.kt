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
        db.execSQL(
            """
            CREATE TABLE bookmarks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                url TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                created_at INTEGER NOT NULL
            )
            """.trimIndent(),
        )
        db.execSQL("CREATE INDEX idx_bookmarks_created ON bookmarks(created_at DESC)")
        db.execSQL(
            """
            CREATE TABLE history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                url TEXT NOT NULL,
                title TEXT NOT NULL,
                visited_at INTEGER NOT NULL
            )
            """.trimIndent(),
        )
        db.execSQL("CREATE INDEX idx_history_visited ON history(visited_at DESC)")
        db.execSQL(
            """
            CREATE TABLE downloads (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_name TEXT NOT NULL,
                source_url TEXT NOT NULL,
                mime_type TEXT NOT NULL,
                received_bytes INTEGER NOT NULL DEFAULT 0,
                total_bytes INTEGER NOT NULL DEFAULT -1,
                status TEXT NOT NULL,
                local_uri TEXT,
                error TEXT,
                created_at INTEGER NOT NULL
            )
            """.trimIndent(),
        )
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
        private const val DATABASE_VERSION = 2
    }
}
