package org.amare.browser

import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.ArrowForward
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import org.amare.browser.data.Bookmark
import org.amare.browser.data.DownloadRecord
import org.amare.browser.data.DownloadStatus
import org.amare.browser.data.HistoryEntry
import org.amare.browser.data.SitePermissionRecord
import org.mozilla.geckoview.GeckoView

private enum class BrowserPanel(val title: String) {
    BOOKMARKS("Bookmarks"),
    HISTORY("History"),
    DOWNLOADS("Downloads"),
    PERMISSIONS("Site permissions"),
}

@Composable
fun BrowserScreen(viewModel: BrowserViewModel) {
    val activeTab = viewModel.activeTab
    var panel by remember { mutableStateOf<BrowserPanel?>(null) }

    BackHandler(enabled = panel != null || activeTab?.canGoBack == true) {
        if (panel != null) panel = null else viewModel.goBack()
    }

    val permissionLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestMultiplePermissions(),
    ) { result ->
        viewModel.completeAndroidPermission(result)
    }

    LaunchedEffect(viewModel.pendingAndroidPermission?.id) {
        viewModel.pendingAndroidPermission?.let { request ->
            permissionLauncher.launch(request.permissions.toTypedArray())
        }
    }

    Column(modifier = Modifier.fillMaxSize().safeDrawingPadding()) {
        BrowserToolbar(
            tab = activeTab,
            isBookmarked = viewModel.isCurrentPageBookmarked(),
            onBack = viewModel::goBack,
            onForward = viewModel::goForward,
            onReload = viewModel::reloadOrStop,
            onNavigate = viewModel::navigate,
            onToggleBookmark = viewModel::toggleCurrentBookmark,
        )

        TabStrip(
            tabs = viewModel.tabs,
            activeTabId = viewModel.activeTabId,
            onSelect = viewModel::selectTab,
            onClose = viewModel::closeTab,
            onNew = viewModel::newTab,
        )

        LibraryBar(
            onBookmarks = { panel = BrowserPanel.BOOKMARKS },
            onHistory = { panel = BrowserPanel.HISTORY },
            onDownloads = { panel = BrowserPanel.DOWNLOADS },
            onPermissions = { panel = BrowserPanel.PERMISSIONS },
        )

        viewModel.sessionNotice?.let { notice ->
            SessionNoticeBanner(
                message = notice,
                onDismiss = viewModel::dismissSessionNotice,
            )
        }

        if (activeTab?.isLoading == true) {
            LinearProgressIndicator(
                progress = { activeTab.progress / 100f },
                modifier = Modifier.fillMaxWidth(),
            )
        }

        Box(modifier = Modifier.weight(1f).fillMaxWidth()) {
            if (activeTab != null) {
                key(activeTab.id) {
                    BrowserContent(activeTab)
                }
                activeTab.error?.let { message ->
                    ErrorOverlay(message = message, onRetry = viewModel::retry)
                }
            }

            if (viewModel.isRestoring) {
                RestoreOverlay()
            }

            panel?.let { selectedPanel ->
                BrowserPanelOverlay(
                    panel = selectedPanel,
                    viewModel = viewModel,
                    onClose = { panel = null },
                    onOpenUrl = { url ->
                        viewModel.openUrl(url)
                        panel = null
                    },
                )
            }
        }
    }

    viewModel.pendingPermission?.let { prompt ->
        PermissionDialog(
            prompt = prompt,
            onAllow = { viewModel.resolvePermission(true) },
            onDeny = { viewModel.resolvePermission(false) },
        )
    }
}

@Composable
private fun BrowserToolbar(
    tab: BrowserTab?,
    isBookmarked: Boolean,
    onBack: () -> Unit,
    onForward: () -> Unit,
    onReload: () -> Unit,
    onNavigate: (String) -> Unit,
    onToggleBookmark: () -> Unit,
) {
    val focusManager = LocalFocusManager.current
    var address by remember(tab?.id) { mutableStateOf(tab?.currentUrl.orEmpty()) }

    LaunchedEffect(tab?.id, tab?.currentUrl) {
        address = tab?.currentUrl.orEmpty()
    }

    Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp)) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            OutlinedTextField(
                value = address,
                onValueChange = { address = it },
                modifier = Modifier.weight(1f),
                singleLine = true,
                placeholder = { Text("Search or enter address") },
                keyboardOptions = KeyboardOptions(
                    keyboardType = KeyboardType.Uri,
                    imeAction = ImeAction.Go,
                ),
                keyboardActions = KeyboardActions(
                    onGo = {
                        focusManager.clearFocus()
                        onNavigate(address)
                    },
                ),
            )
            IconButton(
                onClick = {
                    focusManager.clearFocus()
                    onNavigate(address)
                },
            ) {
                Icon(Icons.Default.Search, contentDescription = "Go")
            }
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(2.dp),
        ) {
            IconButton(onClick = onBack, enabled = tab?.canGoBack == true) {
                Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
            }
            IconButton(onClick = onForward, enabled = tab?.canGoForward == true) {
                Icon(Icons.AutoMirrored.Filled.ArrowForward, contentDescription = "Forward")
            }
            IconButton(onClick = onReload) {
                Icon(Icons.Default.Refresh, contentDescription = "Reload or stop")
            }
            TextButton(onClick = onToggleBookmark, enabled = tab != null) {
                Text(if (isBookmarked) "★ Saved" else "☆ Save")
            }
            Text(
                text = tab?.title.orEmpty(),
                modifier = Modifier.weight(1f).padding(horizontal = 8.dp),
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.labelMedium,
            )
        }
    }
}

@Composable
private fun LibraryBar(
    onBookmarks: () -> Unit,
    onHistory: () -> Unit,
    onDownloads: () -> Unit,
    onPermissions: () -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState())
            .padding(horizontal = 8.dp, vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        TextButton(onClick = onBookmarks) { Text("Bookmarks") }
        TextButton(onClick = onHistory) { Text("History") }
        TextButton(onClick = onDownloads) { Text("Downloads") }
        TextButton(onClick = onPermissions) { Text("Permissions") }
    }
}

@Composable
private fun TabStrip(
    tabs: List<BrowserTab>,
    activeTabId: Long,
    onSelect: (Long) -> Unit,
    onClose: (Long) -> Unit,
    onNew: () -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState())
            .padding(horizontal = 8.dp, vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        tabs.forEach { tab ->
            Surface(
                color = if (tab.id == activeTabId) {
                    MaterialTheme.colorScheme.secondaryContainer
                } else {
                    MaterialTheme.colorScheme.surfaceVariant
                },
                shape = RoundedCornerShape(8.dp),
                modifier = Modifier
                    .heightIn(min = 36.dp)
                    .widthIn(max = 190.dp)
                    .clickable { onSelect(tab.id) },
            ) {
                Row(
                    modifier = Modifier.padding(start = 10.dp, end = 2.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = tab.title.ifBlank { "New tab" },
                        modifier = Modifier.weight(1f, fill = false),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        style = MaterialTheme.typography.labelMedium,
                    )
                    IconButton(
                        onClick = { onClose(tab.id) },
                        modifier = Modifier.heightIn(min = 32.dp, max = 32.dp),
                    ) {
                        Icon(
                            Icons.Default.Close,
                            contentDescription = "Close tab",
                            modifier = Modifier.heightIn(max = 18.dp),
                        )
                    }
                }
            }
        }

        TextButton(onClick = onNew) {
            Text("+ Tab")
        }
    }
}

@Composable
private fun BrowserContent(tab: BrowserTab) {
    AndroidView(
        factory = { context ->
            GeckoView(context).apply {
                setSession(tab.session)
            }
        },
        update = { view ->
            if (view.session !== tab.session) {
                view.setSession(tab.session)
            }
        },
        onRelease = { view -> view.releaseSession() },
        modifier = Modifier.fillMaxSize(),
    )
}

@Composable
private fun BrowserPanelOverlay(
    panel: BrowserPanel,
    viewModel: BrowserViewModel,
    onClose: () -> Unit,
    onOpenUrl: (String) -> Unit,
) {
    Surface(
        modifier = Modifier.fillMaxSize(),
        color = MaterialTheme.colorScheme.surface,
    ) {
        Column(modifier = Modifier.fillMaxSize().padding(12.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = panel.title,
                    modifier = Modifier.weight(1f),
                    style = MaterialTheme.typography.headlineSmall,
                )
                IconButton(onClick = onClose) {
                    Icon(Icons.Default.Close, contentDescription = "Close panel")
                }
            }

            when (panel) {
                BrowserPanel.BOOKMARKS -> BookmarksPanel(
                    bookmarks = viewModel.bookmarks,
                    onOpen = onOpenUrl,
                    onDelete = viewModel::removeBookmark,
                )
                BrowserPanel.HISTORY -> HistoryPanel(
                    history = viewModel.history,
                    onSearch = viewModel::refreshHistory,
                    onOpen = onOpenUrl,
                    onDelete = viewModel::removeHistory,
                    onClear = viewModel::clearHistory,
                )
                BrowserPanel.DOWNLOADS -> DownloadsPanel(
                    downloads = viewModel.downloads,
                    onRefresh = viewModel::refreshDownloads,
                    onOpen = viewModel::openDownload,
                    onRemove = viewModel::removeDownload,
                )
                BrowserPanel.PERMISSIONS -> PermissionsPanel(
                    permissions = viewModel.sitePermissions,
                    onRefresh = viewModel::refreshPermissions,
                    onSet = viewModel::setSitePermission,
                    onRemove = viewModel::removeSitePermission,
                )
            }
        }
    }
}

@Composable
private fun BookmarksPanel(
    bookmarks: List<Bookmark>,
    onOpen: (String) -> Unit,
    onDelete: (Long) -> Unit,
) {
    var query by remember { mutableStateOf("") }
    val filtered = bookmarks.filter {
        query.isBlank() ||
            it.title.contains(query, ignoreCase = true) ||
            it.url.contains(query, ignoreCase = true)
    }

    OutlinedTextField(
        value = query,
        onValueChange = { query = it },
        modifier = Modifier.fillMaxWidth().padding(vertical = 6.dp),
        singleLine = true,
        placeholder = { Text("Search bookmarks") },
    )

    if (filtered.isEmpty()) {
        EmptyPanelText("No bookmarks found.")
        return
    }

    LazyColumn(modifier = Modifier.fillMaxSize()) {
        items(filtered, key = { it.id }) { bookmark ->
            LibraryRow(
                title = bookmark.title,
                subtitle = bookmark.url,
                onClick = { onOpen(bookmark.url) },
                actionLabel = "Delete",
                onAction = { onDelete(bookmark.id) },
            )
        }
    }
}

@Composable
private fun HistoryPanel(
    history: List<HistoryEntry>,
    onSearch: (String) -> Unit,
    onOpen: (String) -> Unit,
    onDelete: (Long) -> Unit,
    onClear: () -> Unit,
) {
    var query by remember { mutableStateOf("") }

    LaunchedEffect(query) {
        onSearch(query)
    }

    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        OutlinedTextField(
            value = query,
            onValueChange = { query = it },
            modifier = Modifier.weight(1f).padding(vertical = 6.dp),
            singleLine = true,
            placeholder = { Text("Search history") },
        )
        TextButton(onClick = onClear) { Text("Clear") }
    }

    if (history.isEmpty()) {
        EmptyPanelText("No history entries found.")
        return
    }

    LazyColumn(modifier = Modifier.fillMaxSize()) {
        items(history, key = { it.id }) { entry ->
            LibraryRow(
                title = entry.title,
                subtitle = entry.url,
                onClick = { onOpen(entry.url) },
                actionLabel = "Delete",
                onAction = { onDelete(entry.id) },
            )
        }
    }
}

@Composable
private fun DownloadsPanel(
    downloads: List<DownloadRecord>,
    onRefresh: () -> Unit,
    onOpen: (DownloadRecord) -> Unit,
    onRemove: (DownloadRecord) -> Unit,
) {
    LaunchedEffect(Unit) { onRefresh() }

    if (downloads.isEmpty()) {
        EmptyPanelText("No downloads yet.")
        return
    }

    LazyColumn(modifier = Modifier.fillMaxSize()) {
        items(downloads, key = { it.id }) { download ->
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(vertical = 8.dp),
            ) {
                Text(download.fileName, style = MaterialTheme.typography.titleSmall)
                Text(
                    text = download.sourceUrl,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    style = MaterialTheme.typography.bodySmall,
                )
                Text(
                    text = downloadStatusText(download),
                    style = MaterialTheme.typography.labelSmall,
                )
                if (download.status == DownloadStatus.RUNNING) {
                    if (download.totalBytes > 0) {
                        LinearProgressIndicator(
                            progress = {
                                (download.receivedBytes.toFloat() / download.totalBytes)
                                    .coerceIn(0f, 1f)
                            },
                            modifier = Modifier.fillMaxWidth(),
                        )
                    } else {
                        LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                    }
                }
                Row {
                    if (download.status == DownloadStatus.COMPLETE) {
                        TextButton(onClick = { onOpen(download) }) { Text("Open") }
                    }
                    TextButton(onClick = { onRemove(download) }) { Text("Remove") }
                }
            }
        }
    }
}

@Composable
private fun PermissionsPanel(
    permissions: List<SitePermissionRecord>,
    onRefresh: () -> Unit,
    onSet: (String, String, String) -> Unit,
    onRemove: (String, String) -> Unit,
) {
    LaunchedEffect(Unit) { onRefresh() }

    if (permissions.isEmpty()) {
        EmptyPanelText("No site permissions stored.")
        return
    }

    LazyColumn(modifier = Modifier.fillMaxSize()) {
        items(permissions, key = { "${it.origin}|${it.permission}" }) { permission ->
            Column(
                modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
            ) {
                Text(PermissionRules.label(permission.permission))
                Text(
                    text = permission.origin,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    style = MaterialTheme.typography.bodySmall,
                )
                Text(
                    text = permission.decision.uppercase(),
                    style = MaterialTheme.typography.labelSmall,
                )
                Row {
                    TextButton(
                        onClick = {
                            onSet(permission.origin, permission.permission, PermissionRules.ALLOW)
                        },
                    ) { Text("Allow") }
                    TextButton(
                        onClick = {
                            onSet(permission.origin, permission.permission, PermissionRules.DENY)
                        },
                    ) { Text("Deny") }
                    TextButton(
                        onClick = { onRemove(permission.origin, permission.permission) },
                    ) { Text("Reset") }
                }
            }
        }
    }
}

@Composable
private fun LibraryRow(
    title: String,
    subtitle: String,
    onClick: () -> Unit,
    actionLabel: String,
    onAction: () -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = title.ifBlank { subtitle },
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.titleSmall,
            )
            Text(
                text = subtitle,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                style = MaterialTheme.typography.bodySmall,
            )
        }
        TextButton(onClick = onAction) { Text(actionLabel) }
    }
}

@Composable
private fun EmptyPanelText(message: String) {
    Text(
        text = message,
        modifier = Modifier.fillMaxWidth().padding(vertical = 24.dp),
        style = MaterialTheme.typography.bodyMedium,
    )
}

@Composable
private fun PermissionDialog(
    prompt: PermissionPrompt,
    onAllow: () -> Unit,
    onDeny: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDeny,
        title = { Text(prompt.title) },
        text = {
            Column {
                Text(prompt.detail)
                Text(
                    text = prompt.origin,
                    modifier = Modifier.padding(top = 8.dp),
                    style = MaterialTheme.typography.bodySmall,
                )
            }
        },
        confirmButton = {
            TextButton(onClick = onAllow) { Text("Allow") }
        },
        dismissButton = {
            TextButton(onClick = onDeny) { Text("Deny") }
        },
    )
}

@Composable
private fun SessionNoticeBanner(message: String, onDismiss: () -> Unit) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        color = MaterialTheme.colorScheme.secondaryContainer,
    ) {
        Row(
            modifier = Modifier.padding(start = 12.dp, end = 4.dp, top = 4.dp, bottom = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                text = message,
                modifier = Modifier.weight(1f),
                style = MaterialTheme.typography.bodySmall,
            )
            TextButton(onClick = onDismiss) { Text("Dismiss") }
        }
    }
}

@Composable
private fun RestoreOverlay() {
    Surface(
        modifier = Modifier.fillMaxSize(),
        color = MaterialTheme.colorScheme.surface,
    ) {
        Column(
            modifier = Modifier.fillMaxSize().padding(32.dp),
            verticalArrangement = Arrangement.Center,
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            CircularProgressIndicator()
            Text(
                text = "Restoring tabs...",
                modifier = Modifier.padding(top = 16.dp),
                style = MaterialTheme.typography.bodyMedium,
            )
        }
    }
}

@Composable
private fun ErrorOverlay(message: String, onRetry: () -> Unit) {
    Surface(
        modifier = Modifier.fillMaxSize(),
        color = MaterialTheme.colorScheme.surface,
    ) {
        Column(
            modifier = Modifier.fillMaxSize().padding(32.dp),
            verticalArrangement = Arrangement.Center,
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text("Page failed to load", style = MaterialTheme.typography.headlineSmall)
            Text(
                text = message,
                modifier = Modifier.padding(vertical = 12.dp),
                style = MaterialTheme.typography.bodyMedium,
            )
            Button(onClick = onRetry) {
                Text("Retry")
            }
        }
    }
}

private fun downloadStatusText(download: DownloadRecord): String {
    val size = if (download.totalBytes > 0) {
        "${formatBytes(download.receivedBytes)} / ${formatBytes(download.totalBytes)}"
    } else {
        formatBytes(download.receivedBytes)
    }
    return when (download.status) {
        DownloadStatus.RUNNING -> "Downloading • $size"
        DownloadStatus.COMPLETE -> "Complete • $size"
        DownloadStatus.FAILED -> "Failed • ${download.error ?: "unknown error"}"
    }
}

private fun formatBytes(bytes: Long): String {
    if (bytes < 0) return "unknown size"
    val units = arrayOf("B", "KiB", "MiB", "GiB")
    var value = bytes.toDouble()
    var unit = 0
    while (value >= 1024 && unit < units.lastIndex) {
        value /= 1024
        unit++
    }
    return if (unit == 0) "${value.toLong()} ${units[unit]}" else
        String.format(java.util.Locale.US, "%.1f %s", value, units[unit])
}
