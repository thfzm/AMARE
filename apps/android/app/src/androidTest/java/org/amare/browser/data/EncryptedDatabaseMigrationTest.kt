package org.amare.browser.data

import android.database.sqlite.SQLiteDatabase
import androidx.test.platform.app.InstrumentationRegistry
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class EncryptedDatabaseMigrationTest {
    @Test
    fun legacyPlaintextTablesAreMigratedAndDropped() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        context.deleteDatabase("amare_browser.db")

        val bootstrap = BrowserDatabase(context).writableDatabase
        createLegacyTables(bootstrap)
        bootstrap.close()

        val repository = BrowserRepository(context)
        try {
            val bookmarksLatch = CountDownLatch(1)
            var bookmarks: List<Bookmark> = emptyList()
            repository.loadAllBookmarks {
                bookmarks = it
                bookmarksLatch.countDown()
            }
            assertTrue(bookmarksLatch.await(15, TimeUnit.SECONDS))
            assertEquals(1, bookmarks.size)
            assertEquals("https://legacy.example/bookmark", bookmarks[0].url)
            assertEquals("Legacy bookmark", bookmarks[0].title)

            val historyLatch = CountDownLatch(1)
            var history: List<HistoryEntry> = emptyList()
            repository.loadHistory("") {
                history = it
                historyLatch.countDown()
            }
            assertTrue(historyLatch.await(15, TimeUnit.SECONDS))
            assertEquals(1, history.size)
            assertEquals("https://legacy.example/history", history[0].url)

            val downloadsLatch = CountDownLatch(1)
            var downloads: List<DownloadRecord> = emptyList()
            repository.loadDownloads {
                downloads = it
                downloadsLatch.countDown()
            }
            assertTrue(downloadsLatch.await(15, TimeUnit.SECONDS))
            assertEquals(1, downloads.size)
            assertEquals("legacy.bin", downloads[0].fileName)

            val permissionsLatch = CountDownLatch(1)
            var permissions: List<SitePermissionRecord> = emptyList()
            repository.loadPermissions {
                permissions = it
                permissionsLatch.countDown()
            }
            assertTrue(permissionsLatch.await(15, TimeUnit.SECONDS))
            assertEquals(1, permissions.size)
            assertEquals("https://legacy.example", permissions[0].origin)

            assertFalse(tableExists("bookmarks"))
            assertFalse(tableExists("history"))
            assertFalse(tableExists("downloads"))
            assertFalse(tableExists("site_permissions"))

            val db = SQLiteDatabase.openDatabase(
                context.getDatabasePath("amare_browser.db").path,
                null,
                SQLiteDatabase.OPEN_READONLY,
            )
            try {
                db.rawQuery("SELECT payload FROM bookmarks_enc", null).use { cursor ->
                    assertTrue(cursor.moveToFirst())
                    val plaintext = String(cursor.getBlob(0), Charsets.UTF_8)
                    assertFalse(plaintext.contains("legacy.example"))
                }
            } finally {
                db.close()
            }
        } finally {
            repository.close()
            context.deleteDatabase("amare_browser.db")
        }
    }

    private fun createLegacyTables(db: SQLiteDatabase) {
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
        db.execSQL(
            "INSERT INTO bookmarks(url, title, created_at) VALUES(?, ?, ?)",
            arrayOf<Any>("https://legacy.example/bookmark", "Legacy bookmark", 1L),
        )
        db.execSQL(
            "INSERT INTO history(url, title, visited_at) VALUES(?, ?, ?)",
            arrayOf<Any>("https://legacy.example/history", "Legacy history", 2L),
        )
        db.execSQL(
            "INSERT INTO downloads(file_name, source_url, mime_type, status, created_at) " +
                "VALUES(?, ?, ?, ?, ?)",
            arrayOf<Any>(
                "legacy.bin",
                "https://legacy.example/file",
                "application/octet-stream",
                "COMPLETE",
                3L,
            ),
        )
        db.execSQL(
            "INSERT INTO site_permissions(origin, permission, decision, updated_at) " +
                "VALUES(?, ?, ?, ?)",
            arrayOf<Any>("https://legacy.example", "camera", "allow", 4L),
        )
    }

    private fun tableExists(table: String): Boolean {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val db = SQLiteDatabase.openDatabase(
            context.getDatabasePath("amare_browser.db").path,
            null,
            SQLiteDatabase.OPEN_READONLY,
        )
        return try {
            db.rawQuery(
                "SELECT name FROM sqlite_master WHERE type='table' AND name=?",
                arrayOf(table),
            ).use { it.moveToFirst() }
        } finally {
            db.close()
        }
    }
}
