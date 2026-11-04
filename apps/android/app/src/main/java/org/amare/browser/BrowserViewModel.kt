package org.amare.browser

import android.Manifest
import android.app.Application
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.SparseArray
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
import java.security.MessageDigest
import java.util.ArrayDeque
import java.util.concurrent.Executors
import org.amare.browser.data.Bookmark
import org.amare.browser.data.BrowserRepository
import org.amare.browser.data.DownloadRecord
import org.amare.browser.data.DownloadStatus
import org.amare.browser.data.HistoryEntry
import org.amare.browser.data.PasswordRepository
import org.amare.browser.data.PasswordSummary
import org.amare.browser.data.SessionLoadResult
import org.amare.browser.data.StoredLogin
import org.amare.browser.data.SessionTabDraft
import org.amare.browser.data.SitePermissionRecord
import org.amare.browser.data.StoredSession
import org.amare.browser.security.SessionRecoveryPolicy
import org.amare.browser.security.KeyedRateLimiter
import org.amare.browser.security.NotificationRules
import org.amare.browser.security.PasswordRules
import org.amare.browser.security.PopupRules
import org.json.JSONArray
import org.mozilla.geckoview.Autocomplete
import org.mozilla.geckoview.Autofill
import org.mozilla.geckoview.GeckoResult
import org.mozilla.geckoview.GeckoRuntime
import org.mozilla.geckoview.GeckoRuntimeSettings
import org.mozilla.geckoview.GeckoSession
import org.mozilla.geckoview.GeckoSessionSettings
import org.mozilla.geckoview.GeckoSession.PromptDelegate.PromptResponse
import org.mozilla.geckoview.WebNotification
import org.mozilla.geckoview.WebNotificationDelegate
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

data class LoginFieldIds(
    val usernameId: Int = -1,
    val passwordId: Int = -1,
)

data class LoginSavePrompt(
    val request: GeckoSession.PromptDelegate.AutocompleteRequest<Autocomplete.LoginSaveOption>,
    val option: Autocomplete.LoginSaveOption,
    val origin: String,
    val username: String,
    val password: String,
    val isUpdate: Boolean,
    val result: GeckoResult<PromptResponse>,
)

data class AutofillPrompt(
    val tabId: Long,
    val origin: String,
    val accounts: List<PasswordSummary>,
)

data class NotificationTarget(
    val notification: WebNotification,
    val origin: String,
)

class BrowserViewModel(application: Application) : AndroidViewModel(application) {
    companion object {
        const val HOME_URL = "https://duckduckgo.com/"
        private const val PREFS = "amare_android_browser"
        private const val KEY_URLS = "open_urls"
        private const val KEY_ACTIVE = "active_index"
        private const val MAX_RESTORED_TABS = 8
        private const val MAX_RECOVERY_ATTEMPTS = 3
        private const val TAG = "BrowserViewModel"
        private const val NOTIFICATION_CHANNEL_ID = "amare_web_notifications"
        private const val NOTIFICATION_ID = 1001
        const val ACTION_OPEN_NOTIFICATION = "org.amare.browser.OPEN_NOTIFICATION"
        const val EXTRA_NOTIFICATION_KEY = "notification_key"

        @Volatile
        private var sharedRuntime: GeckoRuntime? = null

        private fun getRuntime(context: Context): GeckoRuntime {
            sharedRuntime?.let { return it }
            synchronized(this) {
                sharedRuntime?.let { return it }
                val settings = GeckoRuntimeSettings.Builder()
                    .globalPrivacyControlEnabled(true)
                    .remoteDebuggingEnabled(false)
                    .consoleOutput(false)
                    .aboutConfigEnabled(false)
                    .build()
                return GeckoRuntime.create(context.applicationContext, settings)
                    .also { sharedRuntime = it }
            }
        }
    }

    private val prefs = application.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
    private val runtime = getRuntime(application)
    private val repository = BrowserRepository(application)
    private val passwordRepository = PasswordRepository(application)
    private val notificationManager = application.getSystemService(NotificationManager::class.java)
    private val tabList = mutableStateListOf<BrowserTab>()
    private val mainHandler = Handler(Looper.getMainLooper())
    private val downloadExecutor = Executors.newFixedThreadPool(2)
    private val permissionQueue = ArrayDeque<PermissionPrompt>()
    private val androidPermissionQueue = ArrayDeque<AndroidPermissionRequest>()
    private val sessionStates = mutableMapOf<Long, String>()
    private val recoveryAttempts = mutableMapOf<Long, Int>()
    private val loginFields = mutableMapOf<Long, LoginFieldIds>()
    private val autofillOfferedFor = mutableMapOf<Long, String>()
    private val popupLimiter = KeyedRateLimiter(5, 10_000)
    private val notificationLimiter = KeyedRateLimiter(5, 60_000)
    private val notificationTargets = mutableMapOf<String, NotificationTarget>()
    private var persistRunnable: Runnable? = null
    private var nextPromptId = 1L

    val tabs: List<BrowserTab> get() = tabList
    val bookmarks = mutableStateListOf<Bookmark>()
    val history = mutableStateListOf<HistoryEntry>()
    val downloads = mutableStateListOf<DownloadRecord>()
    val sitePermissions = mutableStateListOf<SitePermissionRecord>()
    val passwords = mutableStateListOf<PasswordSummary>()

    var activeTabId by mutableLongStateOf(0L)
        private set

    var pendingPermission by mutableStateOf<PermissionPrompt?>(null)
        private set

    var pendingAndroidPermission by mutableStateOf<AndroidPermissionRequest?>(null)
        private set

    var isRestoring by mutableStateOf(true)
        private set

    var sessionNotice by mutableStateOf<String?>(null)
        private set

    var pendingLoginSave by mutableStateOf<LoginSavePrompt?>(null)
        private set

    var autofillPrompt by mutableStateOf<AutofillPrompt?>(null)
        private set

    var pendingNotificationOsPermission by mutableStateOf(false)
        private set

    private var nextTabId = 1L

    val activeTab: BrowserTab?
        get() = tabList.firstOrNull { it.id == activeTabId }

    init {
        ensureNotificationChannel()
        NotificationBridge.dismissHandler = { key -> closeNotificationByKey(key) }
        runtime.webNotificationDelegate = object : WebNotificationDelegate {
            override fun onShowNotification(notification: WebNotification) {
                showWebNotification(notification)
            }

            override fun onCloseNotification(notification: WebNotification) {
                closeWebNotification(notification)
            }
        }
        runtime.autocompleteStorageDelegate = object : Autocomplete.StorageDelegate {
            override fun onLoginFetch(domain: String): GeckoResult<Array<Autocomplete.LoginEntry>> =
                GeckoResult.fromValue(emptyArray())

            override fun onLoginFetch(): GeckoResult<Array<Autocomplete.LoginEntry>> =
                GeckoResult.fromValue(emptyArray())
        }
        refreshBookmarks()
        refreshHistory("")
        refreshDownloads()
        refreshPermissions()
        refreshPasswords()
        repository.loadSessionSnapshot { result ->
            restoreInitialSession(result)
            repository.markSessionRunning()
        }
    }

    fun newTab() {
        val tab = createTab(HOME_URL)
        activateTab(tab.id, persist = true)
        scheduleSessionPersist()
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
        sessionStates.remove(id)
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
        scheduleSessionPersist(delayMs = 0)
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
        tabList.forEach { runCatching { it.session.flushSessionState() } }
        persistSession(cleanShutdown = false)
    }

    fun onHostDestroy(isFinishing: Boolean) {
        if (isFinishing) {
            persistRunnable?.let { mainHandler.removeCallbacks(it) }
            persistRunnable = null
            persistSession(cleanShutdown = true)
        }
    }

    fun dismissSessionNotice() {
        sessionNotice = null
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
            showDataNotice()
        }
    }

    fun refreshHistory(query: String) {
        repository.loadHistory(query) { values ->
            history.clear()
            history.addAll(values)
            showDataNotice()
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
            showDataNotice()
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
            showDataNotice()
        }
    }

    fun refreshPasswords() {
        passwordRepository.listAll { values ->
            passwords.clear()
            passwords.addAll(values)
            passwordRepository.consumeNotice()?.let { sessionNotice = it }
        }
    }

    fun loadPassword(id: Long, callback: (StoredLogin?) -> Unit) {
        passwordRepository.load(id, callback)
    }

    fun savePassword(
        id: Long?,
        origin: String,
        username: String,
        password: String,
        callback: (Boolean) -> Unit = {},
    ) {
        if (id == null) {
            passwordRepository.save(origin, username, password) { saved ->
                refreshPasswords()
                callback(saved)
            }
        } else {
            passwordRepository.update(id, origin, username, password) { saved ->
                refreshPasswords()
                callback(saved)
            }
        }
    }

    fun deletePassword(id: Long) {
        passwordRepository.delete(id) { refreshPasswords() }
    }

    fun allowPasswordSaveForCurrentOrigin() {
        val origin = PasswordRules.originFor(activeTab?.currentUrl) ?: return
        passwordRepository.setSaveDisabled(origin, false)
    }

    fun resolveLoginSave(save: Boolean, never: Boolean) {
        val prompt = pendingLoginSave ?: return
        pendingLoginSave = null
        if (never) {
            passwordRepository.setSaveDisabled(prompt.origin, true)
        }
        if (save) {
            passwordRepository.save(prompt.origin, prompt.username, prompt.password) {
                refreshPasswords()
            }
        }
        prompt.result.complete(
            if (save) prompt.request.confirm(prompt.option) else prompt.request.dismiss(),
        )
    }

    fun dismissAutofillPrompt() {
        autofillPrompt = null
    }

    fun fillAutofill(accountId: Long) {
        val prompt = autofillPrompt ?: return
        val tab = tabList.firstOrNull { it.id == prompt.tabId } ?: return
        if (!PasswordRules.sameOrigin(prompt.origin, tab.currentUrl)) {
            autofillPrompt = null
            return
        }
        val fields = loginFields[prompt.tabId] ?: return
        passwordRepository.load(accountId) { login ->
            if (login == null || login.origin != prompt.origin) return@load
            if (!PasswordRules.sameOrigin(login.origin, tab.currentUrl)) return@load
            val values = SparseArray<CharSequence>()
            if (fields.usernameId >= 0) values.put(fields.usernameId, login.username)
            if (fields.passwordId >= 0) values.put(fields.passwordId, login.password)
            tab.session.autofillSession.autofill(values)
            values.clear()
            autofillPrompt = null
        }
    }

    fun setSitePermission(origin: String, permission: String, decision: String) {
        if (permission == PermissionRules.NOTIFICATIONS) {
            if (decision == PermissionRules.ALLOW) requestNotificationOsPermissionIfNeeded()
            else cancelNotificationsForOrigin(origin)
        }
        repository.setPermission(origin, permission, decision) { refreshPermissions() }
    }

    fun removeSitePermission(origin: String, permission: String) {
        if (permission == PermissionRules.NOTIFICATIONS) {
            cancelNotificationsForOrigin(origin)
        }
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

    fun completeNotificationOsPermission(granted: Boolean) {
        pendingNotificationOsPermission = false
        if (!granted) {
            sessionNotice = "Android notifications are blocked. Enable them in system settings."
        }
    }

    fun requestNotificationOsPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            val granted = ContextCompat.checkSelfPermission(
                getApplication<Application>(),
                Manifest.permission.POST_NOTIFICATIONS,
            ) == PackageManager.PERMISSION_GRANTED
            if (!granted) pendingNotificationOsPermission = true
        }
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
        persistRunnable?.let { mainHandler.removeCallbacks(it) }
        persistRunnable = null
        tabList.forEach { runCatching { it.session.close() } }
        tabList.clear()
        repository.close()
        NotificationBridge.dismissHandler = null
        passwordRepository.close()
        downloadExecutor.shutdownNow()
        super.onCleared()
    }

    private fun restoreInitialSession(result: SessionLoadResult) {
        val notices = mutableListOf<String>()

        val restored = when {
            result.session != null -> restoreSession(result.session)
            result.keyInvalid -> false
            else -> restoreLegacyUrls()
        }

        if (!restored) {
            if (result.keyInvalid) clearLegacyPrefs()
            val tab = createTab(HOME_URL)
            activateTab(tab.id, persist = false)
        }

        if (result.session != null && !result.session.cleanShutdown) {
            notices.add("Previous session was restored after an unexpected exit.")
        }
        result.error?.let { notices.add(it) }
        if (notices.isNotEmpty()) {
            sessionNotice = notices.joinToString(" ")
        }
        isRestoring = false
        scheduleSessionPersist(delayMs = 0)
    }

    private fun restoreSession(session: StoredSession): Boolean {
        if (session.tabs.isEmpty()) return false
        val restored = session.tabs.map { stored ->
            createTab(
                url = stored.url,
                sessionState = stored.sessionState,
                title = stored.title,
            )
        }
        val active = restored[SessionRecoveryPolicy.activeIndex(session.activeIndex, restored.size)]
        activateTab(active.id, persist = false)
        return true
    }

    private fun restoreLegacyUrls(): Boolean {
        val savedUrls = readSavedUrls()
        val savedIndex = prefs.getInt(KEY_ACTIVE, 0)
        clearLegacyPrefs()
        if (savedUrls.isEmpty()) return false
        val restored = savedUrls.take(MAX_RESTORED_TABS).map { createTab(it) }
        val activeIndex = savedIndex.coerceIn(0, restored.lastIndex)
        activateTab(restored[activeIndex].id, persist = false)
        return true
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

    private fun newSession(): GeckoSession {
        val settings = GeckoSessionSettings.Builder()
            .useTrackingProtection(true)
            .build()
        return GeckoSession(settings)
    }

    private fun createTab(
        url: String,
        sessionState: String? = null,
        title: String = "New tab",
    ): BrowserTab {
        val id = nextTabId++
        val session = newSession()
        attachDelegates(session, id)
        session.open(runtime)

        val tab = BrowserTab(id = id, session = session, currentUrl = url, title = title)
        tabList.add(tab)
        val parsedState = sessionState?.let { GeckoSession.SessionState.fromString(it) }
        if (parsedState != null) {
            sessionStates[id] = sessionState
            session.restoreState(parsedState)
        } else {
            session.loadUri(url)
        }
        session.setActive(false)
        return tab
    }

    private fun activateTab(id: Long, persist: Boolean) {
        tabList.firstOrNull { it.id == activeTabId }?.session?.setActive(false)
        activeTabId = id
        tabList.firstOrNull { it.id == id }?.session?.setActive(true)
        if (persist) scheduleSessionPersist()
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
                loginFields.remove(id)
                autofillOfferedFor.remove(id)
                if (id == activeTabId) autofillPrompt = null
                scheduleSessionPersist()
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

            override fun onNewSession(
                session: GeckoSession,
                uri: String,
            ): GeckoResult<GeckoSession>? {
                val popup = createPopupTab(uri, openerTabId = id) ?: return null
                return GeckoResult.fromValue(popup)
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
                    recoveryAttempts.remove(id)
                    val tab = tabList.firstOrNull { it.id == id } ?: return
                    val url = tab.currentUrl
                    if (url.startsWith("http://") || url.startsWith("https://")) {
                        repository.recordHistory(url, tab.title)
                    }
                }
                session.flushSessionState()
                scheduleSessionPersist()
            }

            override fun onSessionStateChange(
                session: GeckoSession,
                sessionState: GeckoSession.SessionState,
            ) {
                val serialized = sessionState.toString() ?: return
                sessionStates[id] = serialized
                scheduleSessionPersist()
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

            override fun onCrash(session: GeckoSession) {
                recoverCrashedSession(id, "The page process crashed.")
            }

            override fun onKill(session: GeckoSession) {
                recoverCrashedSession(id, "The page process was killed.")
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

        session.promptDelegate = object : GeckoSession.PromptDelegate {
            override fun onPopupPrompt(
                session: GeckoSession,
                prompt: GeckoSession.PromptDelegate.PopupPrompt,
            ): GeckoResult<PromptResponse>? {
                return GeckoResult.fromValue(prompt.dismiss())
            }

            override fun onLoginSave(
                session: GeckoSession,
                request: GeckoSession.PromptDelegate.AutocompleteRequest<Autocomplete.LoginSaveOption>,
            ): GeckoResult<PromptResponse>? {
                val option = request.options.firstOrNull()
                    ?: return GeckoResult.fromValue(request.dismiss())
                val entry = option.value
                val origin = PasswordRules.originFor(entry.origin)
                if (
                    origin == null ||
                    !PasswordRules.isSecureOrigin(origin) ||
                    !PasswordRules.formOriginAllowed(origin, entry.formActionOrigin)
                ) {
                    return GeckoResult.fromValue(request.dismiss())
                }

                val result = GeckoResult<PromptResponse>()
                passwordRepository.isSaveDisabled(origin) { disabled ->
                    if (disabled) {
                        result.complete(request.dismiss())
                        return@isSaveDisabled
                    }
                    passwordRepository.listForOrigin(origin) { accounts ->
                        val update = accounts.any {
                            it.username == PasswordRules.normalizeUsername(entry.username)
                        }
                        if (pendingLoginSave != null) {
                            result.complete(request.dismiss())
                        } else {
                            pendingLoginSave = LoginSavePrompt(
                                request = request,
                                option = option,
                                origin = origin,
                                username = PasswordRules.normalizeUsername(entry.username),
                                password = entry.password,
                                isUpdate = update,
                                result = result,
                            )
                        }
                    }
                }
                return result
            }

            override fun onLoginSelect(
                session: GeckoSession,
                request: GeckoSession.PromptDelegate.AutocompleteRequest<Autocomplete.LoginSelectOption>,
            ): GeckoResult<PromptResponse>? {
                return GeckoResult.fromValue(request.dismiss())
            }
        }

        session.autofillDelegate = object : Autofill.Delegate {
            override fun onSessionStart(session: GeckoSession) {
                loginFields.remove(id)
                autofillOfferedFor.remove(id)
                if (id == activeTabId) autofillPrompt = null
            }

            override fun onSessionCancel(session: GeckoSession) {
                loginFields.remove(id)
                autofillOfferedFor.remove(id)
                if (id == activeTabId) autofillPrompt = null
            }

            override fun onNodeAdd(
                session: GeckoSession,
                node: Autofill.Node,
                data: Autofill.NodeData,
            ) {
                updateLoginField(id, node, data)
            }

            override fun onNodeUpdate(
                session: GeckoSession,
                node: Autofill.Node,
                data: Autofill.NodeData,
            ) {
                updateLoginField(id, node, data)
            }

            override fun onNodeFocus(
                session: GeckoSession,
                focused: Autofill.Node,
                data: Autofill.NodeData,
            ) {
                updateLoginField(id, focused, data)
            }
        }
    }

    private fun updateLoginField(
        id: Long,
        node: Autofill.Node,
        data: Autofill.NodeData,
    ) {
        val tab = tabList.firstOrNull { it.id == id } ?: return
        val origin = PasswordRules.originFor(tab.currentUrl) ?: return
        if (!PasswordRules.isSecureOrigin(origin)) return
        if (!PasswordRules.nodeDomainMatches(node.domain, origin)) return

        val current = loginFields[id] ?: LoginFieldIds()
        loginFields[id] = when (node.hint) {
            Autofill.Hint.USERNAME, Autofill.Hint.EMAIL_ADDRESS ->
                current.copy(usernameId = data.id)
            Autofill.Hint.PASSWORD ->
                current.copy(passwordId = data.id)
            else -> return
        }
        maybeOfferAutofill(id, origin)
    }

    private fun maybeOfferAutofill(tabId: Long, origin: String) {
        val fields = loginFields[tabId] ?: return
        if (fields.usernameId < 0 || fields.passwordId < 0) return
        if (tabId != activeTabId) return
        if (autofillOfferedFor[tabId] == origin) return
        autofillOfferedFor[tabId] = origin
        passwordRepository.listForOrigin(origin) { accounts ->
            val tab = tabList.firstOrNull { it.id == tabId } ?: return@listForOrigin
            if (accounts.isEmpty()) return@listForOrigin
            if (!PasswordRules.sameOrigin(origin, tab.currentUrl)) return@listForOrigin
            autofillPrompt = AutofillPrompt(tabId, origin, accounts)
        }
    }

    private fun createPopupTab(uri: String, openerTabId: Long): GeckoSession? {
        if (!PopupRules.isAllowedPopupUri(uri)) {
            sessionNotice = "A popup was blocked because its URL is not allowed."
            return null
        }
        if (!popupLimiter.tryAcquire(PopupRules.popupMarker(openerTabId))) {
            sessionNotice = "Popup blocked: too many popup requests."
            return null
        }
        val id = nextTabId++
        return try {
            val session = newSession()
            attachDelegates(session, id)
            session.open(runtime)
            val tab = BrowserTab(
                id = id,
                session = session,
                currentUrl = uri,
                title = "New tab",
            )
            tabList.add(tab)
            activateTab(tab.id, persist = true)
            scheduleSessionPersist()
            session
        } catch (_: Throwable) {
            sessionNotice = "The requested popup could not be opened."
            null
        }
    }

    fun handleNotificationIntent(intent: Intent?) {
        if (intent?.action != ACTION_OPEN_NOTIFICATION) return
        val key = intent.getStringExtra(EXTRA_NOTIFICATION_KEY) ?: return
        val target = notificationTargets.remove(key) ?: return
        notificationManager.cancel(key, NOTIFICATION_ID)
        target.notification.click()
        target.notification.dismiss()
        val url = NotificationRules.safeNavigationUrl(
            target.origin,
            target.notification.source,
        )
        val existing = tabList.firstOrNull {
            PermissionRules.originFor(it.currentUrl) == target.origin
        }
        if (existing != null) {
            activateTab(existing.id, persist = true)
            existing.session.loadUri(url)
        } else {
            val tab = createTab(url)
            activateTab(tab.id, persist = true)
        }
    }

    private fun showWebNotification(notification: WebNotification) {
        val origin = PermissionRules.originFor(notification.origin)
        if (
            origin == null ||
            !NotificationRules.isAllowedOrigin(origin) ||
            notification.privateBrowsing ||
            repository.getPermission(origin, PermissionRules.NOTIFICATIONS) !=
            PermissionRules.ALLOW ||
            !notificationLimiter.tryAcquire(origin)
        ) {
            notification.dismiss()
            return
        }
        if (!notificationManager.areNotificationsEnabled()) {
            sessionNotice = "Android notifications are disabled for AMARE."
            notification.dismiss()
            return
        }
        ensureNotificationChannel()

        val key = notificationKey(origin, notification.tag)
        notificationTargets.remove(key)?.notification?.dismiss()
        notificationTargets[key] = NotificationTarget(notification, origin)
        val intent = Intent(getApplication(), MainActivity::class.java).apply {
            action = ACTION_OPEN_NOTIFICATION
            putExtra(EXTRA_NOTIFICATION_KEY, key)
            addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        }
        val pendingIntent = PendingIntent.getActivity(
            getApplication(),
            key.hashCode(),
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val dismissIntent = Intent(getApplication(), NotificationActionReceiver::class.java).apply {
            action = NotificationActionReceiver.ACTION_DISMISS_NOTIFICATION
            putExtra(EXTRA_NOTIFICATION_KEY, key)
        }
        val dismissPendingIntent = PendingIntent.getBroadcast(
            getApplication(),
            key.hashCode() + 1,
            dismissIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val title = notification.title?.takeIf { it.isNotBlank() } ?: origin
        val text = notification.text?.takeIf { it.isNotBlank() }
            ?: "New notification from $origin"
        val builder = Notification.Builder(getApplication(), NOTIFICATION_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle(title)
            .setContentText(text)
            .setSubText(origin)
            .setContentIntent(pendingIntent)
            .setAutoCancel(true)
            .setVisibility(Notification.VISIBILITY_PRIVATE)
            .setCategory(Notification.CATEGORY_MESSAGE)
            .setDeleteIntent(dismissPendingIntent)
            .setWhen(System.currentTimeMillis())
        if (notification.silent) {
            builder.setSound(null)
            builder.setVibrate(null)
        }
        if (notification.requireInteraction) builder.setOngoing(true)
        try {
            notificationManager.notify(key, NOTIFICATION_ID, builder.build())
            notification.show()
        } catch (_: SecurityException) {
            notificationTargets.remove(key)
            notification.dismiss()
            sessionNotice = "Notifications are blocked by Android."
        } catch (_: Throwable) {
            notificationTargets.remove(key)
            notification.dismiss()
            sessionNotice = "The web notification could not be displayed."
        }
    }

    private fun closeWebNotification(notification: WebNotification) {
        val key = notificationKey(
            PermissionRules.originFor(notification.origin) ?: notification.origin,
            notification.tag,
        )
        closeNotificationByKey(key)
    }

    private fun closeNotificationByKey(key: String) {
        notificationTargets.remove(key)?.notification?.dismiss()
        notificationManager.cancel(key, NOTIFICATION_ID)
    }

    private fun cancelNotificationsForOrigin(origin: String) {
        val keys = notificationTargets.filterValues { it.origin == origin }.keys.toList()
        keys.forEach { key ->
            notificationTargets.remove(key)?.notification?.dismiss()
            notificationManager.cancel(key, NOTIFICATION_ID)
        }
    }

    private fun ensureNotificationChannel() {
        val channel = NotificationChannel(
            NOTIFICATION_CHANNEL_ID,
            "Web notifications",
            NotificationManager.IMPORTANCE_DEFAULT,
        ).apply {
            description = "Notifications from websites allowed in AMARE"
            lockscreenVisibility = Notification.VISIBILITY_PRIVATE
        }
        notificationManager.createNotificationChannel(channel)
    }

    private fun notificationKey(origin: String, tag: String): String {
        val digest = MessageDigest.getInstance("SHA-256")
            .digest("$origin\u0000$tag".toByteArray(Charsets.UTF_8))
        return digest.joinToString("") { "%02x".format(it.toInt() and 0xFF) }
    }

    private fun recoverCrashedSession(id: Long, reason: String) {
        val tab = tabList.firstOrNull { it.id == id } ?: return
        val attempts = recoveryAttempts[id] ?: 0
        val exhausted = attempts >= MAX_RECOVERY_ATTEMPTS
        recoveryAttempts[id] = attempts + 1

        val replacement = newSession()
        attachDelegates(replacement, id)
        replacement.open(runtime)
        val parsedState = if (exhausted) {
            null
        } else {
            sessionStates[id]?.let { GeckoSession.SessionState.fromString(it) }
        }
        if (parsedState != null) {
            replacement.restoreState(parsedState)
        } else {
            replacement.loadUri(tab.currentUrl)
        }

        val wasActive = id == activeTabId
        updateTab(id) {
            it.copy(
                session = replacement,
                isLoading = false,
                error = if (exhausted) {
                    "This tab crashed repeatedly. Press Retry to load the last URL."
                } else {
                    null
                },
                progress = 0,
            )
        }
        if (wasActive) replacement.setActive(true)
        sessionNotice = if (exhausted) {
            "$reason Automatic state recovery limit reached; reloaded the last URL."
        } else {
            reason
        }
        scheduleSessionPersist()
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

    private fun scheduleSessionPersist(delayMs: Long = 800L) {
        if (isRestoring) return
        persistRunnable?.let { mainHandler.removeCallbacks(it) }
        val runnable = Runnable {
            persistRunnable = null
            persistSession(cleanShutdown = false)
        }
        persistRunnable = runnable
        mainHandler.postDelayed(runnable, delayMs)
    }

    private fun persistSession(cleanShutdown: Boolean) {
        val drafts = tabList.map { tab ->
            SessionTabDraft(
                url = tab.currentUrl,
                title = tab.title,
                sessionState = sessionStates[tab.id],
            )
        }
        if (drafts.isEmpty()) return
        val activeIndex = tabList.indexOfFirst { it.id == activeTabId }.coerceAtLeast(0)
        repository.saveSessionSnapshot(drafts, activeIndex, cleanShutdown)
    }

    private fun showDataNotice() {
        repository.consumeDataNotice()?.let { sessionNotice = it }
    }

    private fun clearLegacyPrefs() {
        prefs.edit {
            remove(KEY_URLS)
            remove(KEY_ACTIVE)
        }
    }
}
