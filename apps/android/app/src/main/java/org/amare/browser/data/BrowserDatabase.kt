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
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        // Version 1 is the first Android-only schema.
    }

    companion object {
        private const val DATABASE_NAME = "amare_browser.db"
        private const val DATABASE_VERSION = 1
    }
}
