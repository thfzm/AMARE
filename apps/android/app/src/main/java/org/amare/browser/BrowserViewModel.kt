package org.amare.browser

import android.app.Application
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Handler
import android.os.Looper
import android.widget.Toast
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import androidx.core.content.edit
import androidx.core.net.toUri
import androidx.lifecycle.AndroidViewModel
import java.util.ArrayDeque
import java.util.concurrent.Executors
import org.amare.browser.data.Bookmark
import org.amare.browser.data.BrowserRepository
import org.amare.browser.data.DownloadRecord
import org.amare.browser.data.DownloadStatus
import org.amare.browser.data.HistoryEntry
import org.amare.browser.data.SitePermissionRecord
import org.json.JSONArray
import org.mozilla.geckoview.GeckoResult
import org.mozilla.geckoview.GeckoRuntime
import org.mozilla.geckoview.GeckoRuntimeSettings
import org.mozilla.geckoview.GeckoSession
import org.mozilla.geckoview.GeckoSessionSettings
import org.mozilla.geckoview.WebRequestError
import org.mozilla.geckoview.WebResponse

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

data class PermissionPrompt(
    val id: Long,
    val origin: String,
    val title: String,
    val detail: String,
    val keys: List<String>,
    val contentResult: GeckoResult<Int>? = null,
    val mediaCallback: GeckoSession.PermissionDelegate.MediaCallback? = null,
    val videoId: String? = null,
    val audioId: String? = null,
)

data class AndroidPermissionRequest(
    val id: Long,
    val origin: String,
    val permissions: List<String>,
    val callback: GeckoSession.PermissionDelegate.Callback,
)

class BrowserViewModel(application: Application) : AndroidViewModel(application) {
    companion object {
        const val HOME_URL = "https://duckduckgo.com/"
        private const val PREFS = "amare_android_browser"
        private const val KEY_URLS = "open_urls"
        private const val KEY_ACTIVE = "active_index"
        private const val MAX_RESTORED_TABS = 8
        private const val TAG = "BrowserViewModel"

        @Volatile
        private var sharedRuntime: GeckoRuntime? = null

        private fun getRuntime(context: Context): GeckoRuntime {
            sharedRuntime?.let { return it }
            synchronized(this) {
                sharedRuntime?.let { return it }
                val settings = GeckoRuntimeSettings.Builder()
                    .globalPrivacyControlEnabled(true)
                    .build()
                return GeckoRuntime.create(context.applicationContext, settings)
                    .also { sharedRuntime = it }
            }
        }
    }

    private val prefs = application.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
    private val runtime = getRuntime(application)
    private val repository = BrowserRepository(application)
    private val tabList = mutableStateListOf<BrowserTab>()
    private val mainHandler = Handler(Looper.getMainLooper())
    private val downloadExecutor = Executors.newFixedThreadPool(2)
    private val permissionQueue = ArrayDeque<PermissionPrompt>()
    private val androidPermissionQueue = ArrayDeque<AndroidPermissionRequest>()
    private var nextPromptId = 1L

    val tabs: List<BrowserTab> get() = tabList
    val bookmarks = mutableStateListOf<Bookmark>()
    val history = mutableStateListOf<HistoryEntry>()
    val downloads = mutableStateListOf<DownloadRecord>()
    val sitePermissions = mutableStateListOf<SitePermissionRecord>()

    var activeTabId by mutableLongStateOf(0L)
        private set

    var pendingPermission by mutableStateOf<PermissionPrompt?>(null)
        private set

    var pendingAndroidPermission by mutableStateOf<AndroidPermissionRequest?>(null)
        private set

    private var nextTabId = 1L

    val activeTab: BrowserTab?
        get() = tabList.firstOrNull { it.id == activeTabId }

    init {
        restoreTabs()
        refreshBookmarks()
        refreshHistory("")
        refreshDownloads()
        refreshPermissions()
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

    fun openUrl(url: String) {
        val tab = activeTab ?: return
        val normalized = UrlNormalizer.normalize(url) ?: return
        updateTab(tab.id) { it.copy(currentUrl = normalized, error = null) }
        tab.session.loadUri(normalized)
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

    fun isCurrentPageBookmarked(): Boolean {
        val url = activeTab?.currentUrl ?: return false
        return bookmarks.any { it.url == url }
    }

    fun toggleCurrentBookmark() {
        val tab = activeTab ?: return
        if (bookmarks.any { it.url == tab.currentUrl }) {
            repository.removeBookmarkByUrl(tab.currentUrl) { refreshBookmarks() }
        } else {
            repository.addBookmark(tab.currentUrl, tab.title) { refreshBookmarks() }
        }
    }

    fun removeBookmark(id: Long) {
        repository.removeBookmark(id) { refreshBookmarks() }
    }

    fun refreshBookmarks() {
        repository.loadAllBookmarks { values ->
            bookmarks.clear()
            bookmarks.addAll(values)
        }
    }

    fun refreshHistory(query: String) {
        repository.loadHistory(query) { values ->
            history.clear()
            history.addAll(values)
        }
    }

    fun removeHistory(id: Long) {
        repository.removeHistory(id) { refreshHistory("") }
    }

    fun clearHistory() {
        repository.clearHistory { refreshHistory("") }
    }

    fun refreshDownloads() {
        repository.loadDownloads { values ->
            downloads.clear()
            downloads.addAll(values)
        }
    }

    fun removeDownload(record: DownloadRecord) {
        repository.removeDownload(record.id) { refreshDownloads() }
    }

    fun openDownload(record: DownloadRecord) {
        val localUri = record.localUri ?: return
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(localUri.toUri(), record.mimeType)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        runCatching { getApplication<Application>().startActivity(intent) }
            .onFailure {
                Toast.makeText(
                    getApplication(),
                    "No app can open this file.",
                    Toast.LENGTH_SHORT,
                ).show()
            }
    }

    fun refreshPermissions() {
        repository.loadPermissions { values ->
            sitePermissions.clear()
            sitePermissions.addAll(values)
        }
    }

    fun setSitePermission(origin: String, permission: String, decision: String) {
        repository.setPermission(origin, permission, decision) { refreshPermissions() }
    }

    fun removeSitePermission(origin: String, permission: String) {
        repository.removePermission(origin, permission) { refreshPermissions() }
    }

    fun resolvePermission(allow: Boolean) {
        val prompt = pendingPermission ?: return
        val decision = if (allow) PermissionRules.ALLOW else PermissionRules.DENY
        prompt.keys.forEach { key ->
            repository.setPermission(prompt.origin, key, decision)
        }
        prompt.contentResult?.complete(PermissionRules.decisionValue(decision))
        prompt.mediaCallback?.let { callback ->
            if (allow) callback.grant(prompt.videoId, prompt.audioId) else callback.reject()
        }
        pendingPermission = permissionQueue.pollFirst()
        refreshPermissions()
    }

    fun completeAndroidPermission(result: Map<String, Boolean>) {
        val request = pendingAndroidPermission ?: return
        val granted = request.permissions.all { permission ->
            result[permission] == true ||
                ContextCompat.checkSelfPermission(getApplication<Application>(), permission) ==
                PackageManager.PERMISSION_GRANTED
        }
        if (granted) request.callback.grant() else request.callback.reject()
        pendingAndroidPermission = androidPermissionQueue.pollFirst()
    }

    fun cancelAndroidPermissionPrompt() {
        val request = pendingAndroidPermission ?: return
        request.callback.reject()
        pendingAndroidPermission = androidPermissionQueue.pollFirst()
    }

    override fun onCleared() {
        tabList.forEach { runCatching { it.session.close() } }
        tabList.clear()
        repository.close()
        downloadExecutor.shutdownNow()
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
        val settings = GeckoSessionSettings.Builder()
            .useTrackingProtection(true)
            .build()
        val session = GeckoSession(settings)
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
                if (success) {
                    val tab = tabList.firstOrNull { it.id == id } ?: return
                    val url = tab.currentUrl
                    if (url.startsWith("http://") || url.startsWith("https://")) {
                        repository.recordHistory(url, tab.title)
                    }
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

            override fun onExternalResponse(session: GeckoSession, response: WebResponse) {
                startDownload(response)
            }
        }

        session.permissionDelegate = object : GeckoSession.PermissionDelegate {
            override fun onContentPermissionRequest(
                session: GeckoSession,
                perm: GeckoSession.PermissionDelegate.ContentPermission,
            ): GeckoResult<Int>? {
                val origin = PermissionRules.originFor(perm.uri)
                    ?: return GeckoResult.fromValue(
                        GeckoSession.PermissionDelegate.ContentPermission.VALUE_DENY,
                    )
                val key = PermissionRules.contentPermissionKey(perm.permission)
                    ?: return GeckoResult.fromValue(
                        GeckoSession.PermissionDelegate.ContentPermission.VALUE_DENY,
                    )
                val stored = repository.getPermission(origin, key)
                if (stored != null) {
                    return GeckoResult.fromValue(PermissionRules.decisionValue(stored))
                }

                val result = GeckoResult<Int>()
                enqueuePermission(
                    PermissionPrompt(
                        id = nextPromptId++,
                        origin = origin,
                        title = "${PermissionRules.label(key)} request",
                        detail = "$origin wants to use ${PermissionRules.label(key).lowercase()}.",
                        keys = listOf(key),
                        contentResult = result,
                    ),
                )
                return result
            }

            override fun onMediaPermissionRequest(
                session: GeckoSession,
                uri: String,
                video: Array<GeckoSession.PermissionDelegate.MediaSource>?,
                audio: Array<GeckoSession.PermissionDelegate.MediaSource>?,
                callback: GeckoSession.PermissionDelegate.MediaCallback,
            ) {
                val origin = PermissionRules.originFor(uri)
                val cameraSources = video.orEmpty().filter {
                    it.source == GeckoSession.PermissionDelegate.MediaSource.SOURCE_CAMERA
                }
                val microphoneSources = audio.orEmpty().filter {
                    it.source == GeckoSession.PermissionDelegate.MediaSource.SOURCE_MICROPHONE
                }
                val unsupportedSources = video.orEmpty().any {
                    it.source != GeckoSession.PermissionDelegate.MediaSource.SOURCE_CAMERA
                } || audio.orEmpty().any {
                    it.source != GeckoSession.PermissionDelegate.MediaSource.SOURCE_MICROPHONE
                }
                val keys = PermissionRules.mediaKeys(
                    hasVideo = cameraSources.isNotEmpty(),
                    hasAudio = microphoneSources.isNotEmpty(),
                )
                if (origin == null || keys.isEmpty() || unsupportedSources) {
                    callback.reject()
                    return
                }

                val decisions = keys.map { repository.getPermission(origin, it) }
                if (decisions.any { it == PermissionRules.DENY }) {
                    callback.reject()
                    return
                }
                if (decisions.all { it == PermissionRules.ALLOW }) {
                    callback.grant(cameraSources.firstOrNull(), microphoneSources.firstOrNull())
                    return
                }

                val labels = keys.joinToString(" and ") { PermissionRules.label(it).lowercase() }
                enqueuePermission(
                    PermissionPrompt(
                        id = nextPromptId++,
                        origin = origin,
                        title = "Media permission request",
                        detail = "$origin wants to use $labels.",
                        keys = keys,
                        mediaCallback = callback,
                        videoId = cameraSources.firstOrNull()?.id,
                        audioId = microphoneSources.firstOrNull()?.id,
                    ),
                )
            }

            override fun onAndroidPermissionsRequest(
                session: GeckoSession,
                permissions: Array<String>?,
                callback: GeckoSession.PermissionDelegate.Callback,
            ) {
                val requested = permissions?.toList().orEmpty()
                if (requested.isEmpty()) {
                    callback.grant()
                    return
                }
                val missing = requested.filter {
                    ContextCompat.checkSelfPermission(getApplication<Application>(), it) !=
                        PackageManager.PERMISSION_GRANTED
                }
                if (missing.isEmpty()) {
                    callback.grant()
                    return
                }
                val origin = PermissionRules.originFor(
                    tabList.firstOrNull { it.id == id }?.currentUrl,
                ) ?: "site"
                enqueueAndroidPermission(
                    AndroidPermissionRequest(
                        id = nextPromptId++,
                        origin = origin,
                        permissions = missing,
                        callback = callback,
                    ),
                )
            }
        }
    }

    private fun startDownload(response: WebResponse) {
        val fileName = DownloadWriter.resolveFileName(response)
        val mimeType = DownloadWriter.resolveMimeType(response)
        val totalBytes = DownloadWriter.resolveTotalBytes(response)
        repository.createDownload(fileName, response.uri, mimeType, totalBytes) { record ->
            downloads.add(0, record)
            downloadExecutor.execute {
                val result = DownloadWriter.write(
                    context = getApplication<Application>(),
                    fileName = record.fileName,
                    mimeType = record.mimeType,
                    response = response,
                ) { received, total ->
                    mainHandler.post {
                        updateDownloadProgressInMemory(record.id, received, total)
                        repository.updateDownloadProgress(record.id, received, total)
                    }
                }
                mainHandler.post {
                    if (result.error == null && result.localUri != null) {
                        repository.completeDownload(
                            record.id,
                            result.localUri.toString(),
                            result.receivedBytes,
                        ) { refreshDownloads() }
                    } else {
                        repository.failDownload(
                            record.id,
                            result.error ?: "Unknown download error.",
                        ) { refreshDownloads() }
                    }
                }
            }
        }
    }

    private fun updateDownloadProgressInMemory(id: Long, received: Long, total: Long) {
        val index = downloads.indexOfFirst { it.id == id }
        if (index < 0) return
        val current = downloads[index]
        downloads[index] = current.copy(
            receivedBytes = received,
            totalBytes = if (total >= 0) total else current.totalBytes,
        )
    }

    private fun enqueuePermission(prompt: PermissionPrompt) {
        if (pendingPermission == null) {
            pendingPermission = prompt
        } else {
            permissionQueue.addLast(prompt)
        }
    }

    private fun enqueueAndroidPermission(request: AndroidPermissionRequest) {
        if (pendingAndroidPermission == null) {
            pendingAndroidPermission = request
        } else {
            androidPermissionQueue.addLast(request)
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
