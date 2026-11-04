package org.amare.browser.data

import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper

class PasswordDatabase(context: Context) : SQLiteOpenHelper(
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
            CREATE TABLE credentials (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                origin_key TEXT NOT NULL,
                payload BLOB NOT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            )
            """.trimIndent(),
        )
        db.execSQL(
            "CREATE INDEX idx_credentials_origin ON credentials(origin_key, updated_at DESC)",
        )
        db.execSQL(
            """
            CREATE TABLE site_settings (
                origin_key TEXT PRIMARY KEY,
                save_disabled INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            )
            """.trimIndent(),
        )
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        // Version 1 is the first password-store schema.
    }

    companion object {
        private const val DATABASE_NAME = "amare_passwords.db"
        private const val DATABASE_VERSION = 1
    }
}
