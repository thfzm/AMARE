package org.amare.browser.data

import android.database.sqlite.SQLiteDatabase
import androidx.test.platform.app.InstrumentationRegistry
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import org.amare.browser.security.AndroidKeystoreProtector
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class PasswordRepositoryTest {
    private val context = InstrumentationRegistry.getInstrumentation().targetContext
    private val TEST_DATABASE = "amare_passwords_test.db"
    private val TEST_ALIAS = "amare_passwords_test_v1"
    private var repository: PasswordRepository? = null

    @Before
    fun reset() {
        context.deleteDatabase(TEST_DATABASE)
        AndroidKeystoreProtector(TEST_ALIAS).deleteKey()
    }

    @After
    fun cleanup() {
        repository?.close()
        context.deleteDatabase(TEST_DATABASE)
        AndroidKeystoreProtector(TEST_ALIAS).deleteKey()
    }

    @Test
    fun saveListUpdateDeleteAndOriginIsolation() {
        val repo = PasswordRepository(context, TEST_DATABASE, TEST_ALIAS).also { repository = it }
        assertTrue(save(repo, "https://example.com", "alice", "first-secret"))
        assertTrue(save(repo, "https://example.com", "bob", "second-secret"))
        assertTrue(save(repo, "https://other.example", "alice", "third-secret"))

        val all = list(repo)
        assertEquals(3, all.size)
        val example = listForOrigin(repo, "https://example.com")
        assertEquals(2, example.size)
        val other = listForOrigin(repo, "https://other.example")
        assertEquals(1, other.size)

        val alice = example.first { it.username == "alice" }
        val loaded = load(repo, alice.id)
        assertNotNull(loaded)
        assertEquals("first-secret", loaded!!.password)

        assertTrue(save(repo, "https://example.com", "alice", "updated-secret"))
        val updated = load(repo, alice.id)
        assertEquals("updated-secret", updated!!.password)
        assertEquals(2, listForOrigin(repo, "https://example.com").size)

        repo.delete(alice.id)
        val afterDelete = listForOrigin(repo, "https://example.com")
        assertEquals(1, afterDelete.size)
        assertEquals("bob", afterDelete[0].username)
    }

    @Test
    fun encryptedRowsDoNotContainPlaintext() {
        val repo = PasswordRepository(context, TEST_DATABASE, TEST_ALIAS).also { repository = it }
        assertTrue(save(repo, "https://example.com", "alice", "super-secret"))
        val db = SQLiteDatabase.openDatabase(
            context.getDatabasePath(TEST_DATABASE).path,
            null,
            SQLiteDatabase.OPEN_READONLY,
        )
        try {
            db.rawQuery("SELECT payload FROM credentials", null).use { cursor ->
                assertTrue(cursor.moveToFirst())
                val payload = String(cursor.getBlob(0), Charsets.UTF_8)
                assertFalse(payload.contains("super-secret"))
                assertFalse(payload.contains("alice"))
                assertFalse(payload.contains("example.com"))
            }
        } finally {
            db.close()
        }
    }

    @Test
    fun corruptedPayloadIsSkippedAndReported() {
        val repo = PasswordRepository(context, TEST_DATABASE, TEST_ALIAS).also { repository = it }
        PasswordDatabase(context, TEST_DATABASE).writableDatabase.close()
        val db = SQLiteDatabase.openDatabase(
            context.getDatabasePath(TEST_DATABASE).path,
            null,
            SQLiteDatabase.OPEN_READWRITE,
        )
        try {
            db.execSQL(
                "INSERT INTO credentials(origin_key, payload, created_at, updated_at) " +
                    "VALUES(?, ?, ?, ?)",
                arrayOf<Any>(
                    "bad-origin-key",
                    byteArrayOf(1, 2, 3, 4),
                    System.currentTimeMillis(),
                    System.currentTimeMillis(),
                ),
            )
        } finally {
            db.close()
        }
        val rows = list(repo)
        assertTrue(rows.isEmpty())
        assertNotNull(repo.consumeNotice())
    }

    @Test
    fun missingKeyReportsNoticeWithoutPlaintextFallback() {
        val repo = PasswordRepository(context, TEST_DATABASE, TEST_ALIAS).also { repository = it }
        assertTrue(save(repo, "https://example.com", "alice", "secret"))
        AndroidKeystoreProtector(TEST_ALIAS).deleteKey()
        val rows = list(repo)
        assertTrue(rows.isEmpty())
        assertNotNull(repo.consumeNotice())
        assertNull(load(repo, 1))
    }

    private fun save(
        repo: PasswordRepository,
        origin: String,
        username: String,
        password: String,
    ): Boolean {
        val latch = CountDownLatch(1)
        var result = false
        repo.save(origin, username, password) {
            result = it
            latch.countDown()
        }
        assertTrue(latch.await(15, TimeUnit.SECONDS))
        return result
    }

    private fun list(repo: PasswordRepository): List<PasswordSummary> {
        val latch = CountDownLatch(1)
        var result: List<PasswordSummary> = emptyList()
        repo.listAll {
            result = it
            latch.countDown()
        }
        assertTrue(latch.await(15, TimeUnit.SECONDS))
        return result
    }

    private fun listForOrigin(repo: PasswordRepository, origin: String): List<PasswordSummary> {
        val latch = CountDownLatch(1)
        var result: List<PasswordSummary> = emptyList()
        repo.listForOrigin(origin) {
            result = it
            latch.countDown()
        }
        assertTrue(latch.await(15, TimeUnit.SECONDS))
        return result
    }

    private fun load(repo: PasswordRepository, id: Long): StoredLogin? {
        val latch = CountDownLatch(1)
        var result: StoredLogin? = null
        repo.load(id) {
            result = it
            latch.countDown()
        }
        assertTrue(latch.await(15, TimeUnit.SECONDS))
        return result
    }
}
