package org.amare.browser.data

import androidx.test.platform.app.InstrumentationRegistry
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SessionSnapshotRepositoryTest {
    @Test
    fun encryptedSnapshotRoundTripPreservesTabsAndActiveIndex() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val databaseName = "amare_browser_test_session.db"
        context.deleteDatabase(databaseName)
        val repository = BrowserRepository(context, databaseName)
        try {
            val saveLatch = CountDownLatch(1)
            repository.saveSessionSnapshot(
                tabs = listOf(
                    SessionTabDraft("https://example.com/a", "A", """{"history":["a"]}"""),
                    SessionTabDraft("https://example.com/b", "B", null),
                ),
                activeIndex = 1,
                cleanShutdown = false,
            ) { saveLatch.countDown() }
            assertTrue(saveLatch.await(10, TimeUnit.SECONDS))

            val loadLatch = CountDownLatch(1)
            var result: SessionLoadResult? = null
            repository.loadSessionSnapshot {
                result = it
                loadLatch.countDown()
            }
            assertTrue(loadLatch.await(10, TimeUnit.SECONDS))

            val session = result?.session
            assertNotNull(session)
            assertEquals(1, session!!.activeIndex)
            assertEquals(false, session.cleanShutdown)
            assertEquals(2, session.tabs.size)
            assertEquals("https://example.com/a", session.tabs[0].url)
            assertEquals("A", session.tabs[0].title)
            assertEquals("""{"history":["a"]}""", session.tabs[0].sessionState)
            assertEquals("https://example.com/b", session.tabs[1].url)
        } finally {
            val clearLatch = CountDownLatch(1)
            repository.clearSessionSnapshot { clearLatch.countDown() }
            clearLatch.await(10, TimeUnit.SECONDS)
            repository.close()
            context.deleteDatabase(databaseName)
        }
    }
}
