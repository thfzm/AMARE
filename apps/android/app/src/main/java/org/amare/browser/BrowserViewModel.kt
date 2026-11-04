package org.amare.browser

import android.app.Application
import android.content.Context
import android.os.Handler
import android.os.Looper
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.setValue
import androidx.core.content.edit
import androidx.lifecycle.AndroidViewModel
import org.json.JSONArray
import org.mozilla.geckoview.GeckoResult
import org.mozilla.geckoview.GeckoRuntime
import org.mozilla.geckoview.GeckoSession
import org.mozilla.geckoview.WebRequestError

data class BrowserTab(
    val id: Long,
    val session: GeckoSession,
    val currentUrl: String,
    val title: String = "New tab",
    val canGoBack: Boolean = false,
    val canGoForward: Boolean = false,
    val isLoading: Boolean = false,
    val progress: Int = 0,
    val error: String? = null,
)

class BrowserViewModel(application: Application) : AndroidViewModel(application) {
    companion object {
        const val HOME_URL = "https://duckduckgo.com/"
        private const val PREFS = "amare_android_browser"
        private const val KEY_URLS = "open_urls"
        private const val KEY_ACTIVE = "active_index"
        private const val MAX_RESTORED_TABS = 8

        @Volatile
        private var sharedRuntime: GeckoRuntime? = null

        private fun getRuntime(context: Context): GeckoRuntime {
            sharedRuntime?.let { return it }
            synchronized(this) {
                sharedRuntime?.let { return it }
                return GeckoRuntime.create(context.applicationContext).also { sharedRuntime = it }
            }
        }
    }

    private val prefs = application.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
    private val runtime = getRuntime(application)
    private val tabList = mutableStateListOf<BrowserTab>()

    val tabs: List<BrowserTab> get() = tabList

    var activeTabId by mutableLongStateOf(0L)
        private set

    private var nextTabId = 1L

    val activeTab: BrowserTab?
        get() = tabList.firstOrNull { it.id == activeTabId }

    init {
        restoreTabs()
    }

    fun newTab() {
        val tab = createTab(HOME_URL)
        activateTab(tab.id, persist = true)
    }

    fun selectTab(id: Long) {
        if (id != activeTabId && tabList.any { it.id == id }) {
            activateTab(id, persist = true)
        }
    }

    fun closeTab(id: Long) {
        val index = tabList.indexOfFirst { it.id == id }
        if (index < 0) return

        val closed = tabList.removeAt(index)
        closed.session.setActive(false)
        Handler(Looper.getMainLooper()).postDelayed({
            runCatching { closed.session.close() }
        }, 300L)

        if (tabList.isEmpty()) {
            newTab()
            return
        }

        if (activeTabId == id) {
            val next = tabList[index.coerceAtMost(tabList.lastIndex)]
            activateTab(next.id, persist = false)
        }
        persistState()
    }

    fun navigate(input: String) {
        val tab = activeTab ?: return
        val url = UrlNormalizer.normalize(input) ?: return
        updateTab(tab.id) { it.copy(currentUrl = url, error = null) }
        tab.session.loadUri(url)
    }

    fun goBack() {
        activeTab?.session?.goBack()
    }

    fun goForward() {
        activeTab?.session?.goForward()
    }

    fun reloadOrStop() {
        val tab = activeTab ?: return
        if (tab.isLoading) {
            tab.session.stop()
            updateTab(tab.id) { it.copy(isLoading = false) }
        } else {
            updateTab(tab.id) { it.copy(error = null) }
            tab.session.reload()
        }
    }

    fun retry() {
        val tab = activeTab ?: return
        updateTab(tab.id) { it.copy(error = null, isLoading = true) }
        tab.session.reload()
    }

    fun onHostStop() {
        activeTab?.session?.flushSessionState()
        persistState()
    }

    override fun onCleared() {
        tabList.forEach { runCatching { it.session.close() } }
        tabList.clear()
        super.onCleared()
    }

    private fun restoreTabs() {
        val savedUrls = readSavedUrls()
        val initialUrls = savedUrls.ifEmpty { listOf(HOME_URL) }.take(MAX_RESTORED_TABS)
        val restored = initialUrls.map { createTab(it) }
        if (restored.isEmpty()) return

        val savedIndex = prefs.getInt(KEY_ACTIVE, 0).coerceIn(0, restored.lastIndex)
        activateTab(restored[savedIndex].id, persist = false)
    }

    private fun readSavedUrls(): List<String> {
        val raw = prefs.getString(KEY_URLS, null) ?: return emptyList()
        return runCatching {
            val array = JSONArray(raw)
            buildList {
                for (index in 0 until array.length()) {
                    val url = array.optString(index)
                    if (UrlNormalizer.normalize(url) != null) add(url)
                }
            }
        }.getOrDefault(emptyList())
    }

    private fun createTab(url: String): BrowserTab {
        val id = nextTabId++
        val session = GeckoSession()
        attachDelegates(session, id)
        session.open(runtime)

        val tab = BrowserTab(id = id, session = session, currentUrl = url)
        tabList.add(tab)
        session.loadUri(url)
        session.setActive(false)
        return tab
    }

    private fun activateTab(id: Long, persist: Boolean) {
        tabList.firstOrNull { it.id == activeTabId }?.session?.setActive(false)
        activeTabId = id
        tabList.firstOrNull { it.id == id }?.session?.setActive(true)
        if (persist) persistState()
    }

    private fun updateTab(id: Long, transform: (BrowserTab) -> BrowserTab) {
        val index = tabList.indexOfFirst { it.id == id }
        if (index >= 0) {
            tabList[index] = transform(tabList[index])
        }
    }

    private fun attachDelegates(session: GeckoSession, id: Long) {
        session.navigationDelegate = object : GeckoSession.NavigationDelegate {
            override fun onLocationChange(
                session: GeckoSession,
                url: String?,
                perms: MutableList<GeckoSession.PermissionDelegate.ContentPermission>,
                hasUserGesture: Boolean,
            ) {
                if (url.isNullOrBlank()) return
                updateTab(id) { it.copy(currentUrl = url) }
                if (id == activeTabId) persistState()
            }

            override fun onCanGoBack(session: GeckoSession, canGoBack: Boolean) {
                updateTab(id) { it.copy(canGoBack = canGoBack) }
            }

            override fun onCanGoForward(session: GeckoSession, canGoForward: Boolean) {
                updateTab(id) { it.copy(canGoForward = canGoForward) }
            }

            override fun onLoadError(
                session: GeckoSession,
                uri: String?,
                error: WebRequestError,
            ): GeckoResult<String>? {
                updateTab(id) {
                    it.copy(isLoading = false, error = describeError(error))
                }
                return null
            }
        }

        session.progressDelegate = object : GeckoSession.ProgressDelegate {
            override fun onPageStart(session: GeckoSession, url: String) {
                updateTab(id) { it.copy(isLoading = true, progress = 0, error = null) }
            }

            override fun onPageStop(session: GeckoSession, success: Boolean) {
                updateTab(id) {
                    it.copy(isLoading = false, progress = if (success) 100 else it.progress)
                }
            }

            override fun onProgressChange(session: GeckoSession, progress: Int) {
                updateTab(id) { it.copy(progress = progress.coerceIn(0, 100)) }
            }
        }

        session.contentDelegate = object : GeckoSession.ContentDelegate {
            override fun onTitleChange(session: GeckoSession, title: String?) {
                if (!title.isNullOrBlank()) {
                    updateTab(id) { it.copy(title = title) }
                }
            }
        }
    }

    private fun describeError(error: WebRequestError): String = when (error.code) {
        WebRequestError.ERROR_OFFLINE -> "No network connection."
        WebRequestError.ERROR_UNKNOWN_HOST -> "Server not found."
        WebRequestError.ERROR_CONNECTION_REFUSED -> "Connection refused."
        WebRequestError.ERROR_NET_TIMEOUT -> "The connection timed out."
        WebRequestError.ERROR_NET_INTERRUPT, WebRequestError.ERROR_NET_RESET ->
            "The network connection was interrupted."
        WebRequestError.ERROR_SECURITY_SSL, WebRequestError.ERROR_SECURITY_BAD_CERT,
        WebRequestError.ERROR_BAD_HSTS_CERT -> "The secure connection could not be verified."
        WebRequestError.ERROR_HTTPS_ONLY -> "This site could not be loaded over HTTPS."
        else -> "Could not load this page (error ${error.code})."
    }

    private fun persistState() {
        val urls = JSONArray()
        tabList.forEach { urls.put(it.currentUrl) }
        val activeIndex = tabList.indexOfFirst { it.id == activeTabId }.coerceAtLeast(0)
        prefs.edit {
            putString(KEY_URLS, urls.toString())
            putInt(KEY_ACTIVE, activeIndex)
        }
    }
}
