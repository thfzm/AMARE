package org.amare.browser

import android.Manifest
import android.annotation.SuppressLint
import android.app.role.RoleManager
import android.graphics.BitmapFactory
import android.os.Build
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
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
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
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
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import org.amare.browser.data.Bookmark
import org.amare.browser.data.DownloadRecord
import org.amare.browser.data.DownloadStatus
import org.amare.browser.data.HistoryEntry
import org.amare.browser.data.PasswordSummary
import org.amare.browser.data.SitePermissionRecord
import org.amare.browser.data.StoredLogin
import org.amare.browser.security.PasswordRules
import java.net.HttpURLConnection
import java.net.URL
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.mozilla.geckoview.GeckoSession
import org.mozilla.geckoview.GeckoView

private enum class BrowserPanel(val title: String) {
    BOOKMARKS("Bookmarks"),
    HISTORY("History"),
    DOWNLOADS("Downloads"),
    PERMISSIONS("Site permissions"),
    PASSWORDS("Passwords"),
    SECURITY("보안"),
    SETTINGS("설정"),
    DIAGNOSTICS("Diagnostics"),
}

@SuppressLint("InlinedApi")
@Composable
fun BrowserScreen(viewModel: BrowserViewModel) {
    val activeTab = viewModel.activeTab
    var panel by remember { mutableStateOf<BrowserPanel?>(null) }

    val hasReader = viewModel.readerContent != null || viewModel.readerLoading
    val findState = viewModel.findState
    BackHandler(
        enabled = panel != null || hasReader || findState != null ||
            viewModel.aiPanelOpen || activeTab?.canGoBack == true,
    ) {
        when {
            viewModel.aiPanelOpen -> viewModel.closeAiPanel()
            hasReader -> viewModel.closeReader()
            findState != null -> viewModel.closeFind()
            panel != null -> panel = null
            else -> viewModel.goBack()
        }
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

    val notificationPermissionLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestPermission(),
    ) { granted ->
        viewModel.completeNotificationOsPermission(granted)
    }

    LaunchedEffect(viewModel.pendingNotificationOsPermission) {
        if (
            viewModel.pendingNotificationOsPermission &&
            Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU
        ) {
            notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
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
            onSuggest = viewModel::addressSuggestions,
        )

        TabStrip(
            tabs = viewModel.tabs,
            activeTabId = viewModel.activeTabId,
            onSelect = viewModel::selectTab,
            onClose = viewModel::closeTab,
            onNew = viewModel::newTab,
            canReopen = viewModel.canReopenClosedTab(),
            onReopen = viewModel::reopenLastClosedTab,
        )

        LibraryBar(
            onBookmarks = { panel = BrowserPanel.BOOKMARKS },
            onHistory = { panel = BrowserPanel.HISTORY },
            onDownloads = { panel = BrowserPanel.DOWNLOADS },
            onSettings = { panel = BrowserPanel.SETTINGS },
            onFind = {
                panel = null
                viewModel.openFind()
            },
            onReader = {
                panel = null
                viewModel.openReader()
            },
            onAi = {
                panel = null
                viewModel.openAiPanel()
            },
        )

        viewModel.findState?.let { state ->
            FindBar(
                state = state,
                onQueryChange = viewModel::updateFindQuery,
                onPrevious = { viewModel.findNext(false) },
                onNext = { viewModel.findNext(true) },
                onClose = viewModel::closeFind,
            )
        }

        viewModel.sessionNotice?.let { notice ->
            SessionNoticeBanner(
                message = notice,
                onDismiss = viewModel::dismissSessionNotice,
            )
        }

        val autofill = viewModel.autofillPrompt
        if (autofill != null && autofill.tabId == activeTab?.id) {
            AutofillBanner(
                prompt = autofill,
                onFill = viewModel::fillAutofill,
                onDismiss = viewModel::dismissAutofillPrompt,
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
                    onOpenPanel = { nextPanel -> panel = nextPanel },
                    onOpenUrl = { url ->
                        viewModel.openUrl(url)
                        panel = null
                    },
                )
            }

            if (viewModel.readerLoading) {
                ReaderLoadingOverlay(onClose = viewModel::closeReader)
            }
            viewModel.readerContent?.let { document ->
                ReaderOverlay(
                    document = document,
                    settings = viewModel.readerSettings,
                    onSettingsChange = viewModel::updateReaderSettings,
                    onClose = viewModel::closeReader,
                )
            }
            if (viewModel.aiPanelOpen) {
                AiPanel(
                    viewModel = viewModel,
                    modifier = Modifier.align(Alignment.CenterEnd),
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

    viewModel.pendingLoginSave?.let { prompt ->
        LoginSaveDialog(
            prompt = prompt,
            onSave = { viewModel.resolveLoginSave(true, false) },
            onNotNow = { viewModel.resolveLoginSave(false, false) },
            onNever = { viewModel.resolveLoginSave(false, true) },
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
    onSuggest: (String) -> List<AddressSuggestion>,
) {
    val focusManager = LocalFocusManager.current
    var address by remember(tab?.id) { mutableStateOf(tab?.currentUrl.orEmpty()) }
    var suggestions by remember(tab?.id) { mutableStateOf(emptyList<AddressSuggestion>()) }
    var showSuggestions by remember(tab?.id) { mutableStateOf(false) }

    LaunchedEffect(tab?.id, tab?.currentUrl) {
        address = tab?.currentUrl.orEmpty()
        showSuggestions = false
    }

    Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp)) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Box(modifier = Modifier.weight(1f)) {
                OutlinedTextField(
                    value = address,
                    onValueChange = {
                        address = it
                        suggestions = onSuggest(it)
                        showSuggestions = true
                    },
                    modifier = Modifier
                        .fillMaxWidth()
                        .onFocusChanged { focus ->
                            if (focus.isFocused) {
                                suggestions = onSuggest(address)
                                showSuggestions = true
                            }
                        },
                    singleLine = true,
                    placeholder = { Text("Search or enter address") },
                    keyboardOptions = KeyboardOptions(
                        keyboardType = KeyboardType.Uri,
                        imeAction = ImeAction.Go,
                    ),
                    keyboardActions = KeyboardActions(
                        onGo = {
                            showSuggestions = false
                            focusManager.clearFocus()
                            onNavigate(address)
                        },
                    ),
                )
                DropdownMenu(
                    expanded = showSuggestions && suggestions.isNotEmpty(),
                    onDismissRequest = { showSuggestions = false },
                ) {
                    suggestions.forEach { suggestion ->
                        DropdownMenuItem(
                            text = {
                                Column {
                                    Text(
                                        suggestion.title,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                    )
                                    Text(
                                        suggestionLabel(suggestion.kind),
                                        style = MaterialTheme.typography.labelSmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    )
                                }
                            },
                            onClick = {
                                address = suggestion.url
                                showSuggestions = false
                                focusManager.clearFocus()
                                onNavigate(suggestion.url)
                            },
                        )
                    }
                }
            }
            IconButton(
                onClick = {
                    showSuggestions = false
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
                Icon(
                    if (tab?.isLoading == true) Icons.Default.Close else Icons.Default.Refresh,
                    contentDescription = if (tab?.isLoading == true) "Stop" else "Reload",
                )
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
    onSettings: () -> Unit,
    onFind: () -> Unit,
    onReader: () -> Unit,
    onAi: () -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState())
            .padding(horizontal = 8.dp, vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        TextButton(onClick = onFind) { Text("Find") }
        TextButton(onClick = onReader) { Text("Reader") }
        TextButton(onClick = onAi) { Text("AI") }
        TextButton(onClick = onBookmarks) { Text("Bookmarks") }
        TextButton(onClick = onHistory) { Text("History") }
        TextButton(onClick = onDownloads) { Text("Downloads") }
        TextButton(onClick = onSettings) { Text("설정") }
    }
}

@Composable
private fun TabStrip(
    tabs: List<BrowserTab>,
    activeTabId: Long,
    onSelect: (Long) -> Unit,
    onClose: (Long) -> Unit,
    onNew: () -> Unit,
    canReopen: Boolean,
    onReopen: () -> Unit,
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
        if (canReopen) {
            TextButton(onClick = onReopen) {
                Text("Reopen")
            }
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
private fun FindBar(
    state: FindState,
    onQueryChange: (String) -> Unit,
    onPrevious: () -> Unit,
    onNext: () -> Unit,
    onClose: () -> Unit,
) {
    val focusRequester = remember { FocusRequester() }
    val focusManager = LocalFocusManager.current
    LaunchedEffect(Unit) { focusRequester.requestFocus() }
    val count = when {
        state.query.isBlank() -> ""
        state.loading -> "…"
        state.total <= 0 -> "No results"
        else -> "${state.current}/${state.total}"
    }
    Surface(tonalElevation = 3.dp) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            OutlinedTextField(
                value = state.query,
                onValueChange = onQueryChange,
                modifier = Modifier.weight(1f).focusRequester(focusRequester),
                singleLine = true,
                placeholder = { Text("Find in page") },
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                keyboardActions = KeyboardActions(onSearch = { focusManager.clearFocus() }),
            )
            Text(
                count,
                modifier = Modifier.padding(horizontal = 8.dp),
                style = MaterialTheme.typography.labelMedium,
            )
            IconButton(onClick = onPrevious, enabled = state.total > 0) {
                Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Previous match")
            }
            IconButton(onClick = onNext, enabled = state.total > 0) {
                Icon(Icons.AutoMirrored.Filled.ArrowForward, contentDescription = "Next match")
            }
            IconButton(onClick = onClose) {
                Icon(Icons.Default.Close, contentDescription = "Close find")
            }
        }
    }
}

@Composable
private fun ReaderLoadingOverlay(onClose: () -> Unit) {
    Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.surface) {
        Column(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.Center,
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            CircularProgressIndicator()
            Text(
                "Reader Mode 준비 중…",
                modifier = Modifier.padding(top = 12.dp),
                style = MaterialTheme.typography.bodyMedium,
            )
            TextButton(onClick = onClose) { Text("취소") }
        }
    }
}

private data class ReaderPalette(
    val background: Color,
    val text: Color,
    val muted: Color,
    val surface: Color,
    val accent: Color,
)

private fun readerPalette(theme: ReaderTheme): ReaderPalette = when (theme) {
    ReaderTheme.LIGHT -> ReaderPalette(
        background = Color(0xFFFCFBF8),
        text = Color(0xFF202124),
        muted = Color(0xFF5F6368),
        surface = Color(0xFFF0EEE8),
        accent = Color(0xFF2457A7),
    )
    ReaderTheme.DARK -> ReaderPalette(
        background = Color(0xFF121212),
        text = Color(0xFFE8E6E3),
        muted = Color(0xFFA8A5A0),
        surface = Color(0xFF232323),
        accent = Color(0xFF9EC1FF),
    )
    ReaderTheme.SEPIA -> ReaderPalette(
        background = Color(0xFFF5ECD8),
        text = Color(0xFF3D3427),
        muted = Color(0xFF776B59),
        surface = Color(0xFFEADFC9),
        accent = Color(0xFF7A4E1D),
    )
}

@Composable
private fun ReaderOverlay(
    document: ReaderDocument,
    settings: ReaderSettings,
    onSettingsChange: (ReaderSettings) -> Unit,
    onClose: () -> Unit,
) {
    val palette = readerPalette(settings.theme)
    val family = readerFontFamily(settings.font)
    val textLineHeight = (settings.textSizeSp * settings.lineHeight).sp
    Surface(modifier = Modifier.fillMaxSize(), color = palette.background) {
        Column(modifier = Modifier.fillMaxSize()) {
            Row(
                modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 4.dp, top = 6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    "Reader Mode",
                    modifier = Modifier.weight(1f),
                    color = palette.text,
                    style = MaterialTheme.typography.titleMedium,
                )
                IconButton(onClick = onClose) {
                    Icon(Icons.Default.Close, contentDescription = "Close reader", tint = palette.text)
                }
            }
            Row(
                modifier = Modifier.fillMaxWidth()
                    .horizontalScroll(rememberScrollState())
                    .padding(horizontal = 12.dp, vertical = 2.dp),
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                TextButton(onClick = {
                    onSettingsChange(settings.copy(textSizeSp = settings.textSizeSp - 1))
                }) { Text("A−", color = palette.text) }
                TextButton(onClick = {
                    onSettingsChange(settings.copy(textSizeSp = settings.textSizeSp + 1))
                }) { Text("A+", color = palette.text) }
                TextButton(onClick = {
                    onSettingsChange(settings.copy(lineHeight = settings.lineHeight - 0.1f))
                }) { Text("줄−", color = palette.text) }
                TextButton(onClick = {
                    onSettingsChange(settings.copy(lineHeight = settings.lineHeight + 0.1f))
                }) { Text("줄+", color = palette.text) }
                ReaderFont.entries.forEach { font ->
                    FilterChip(
                        selected = settings.font == font,
                        onClick = { onSettingsChange(settings.copy(font = font)) },
                        label = { Text(font.label) },
                    )
                }
                ReaderTheme.entries.forEach { theme ->
                    FilterChip(
                        selected = settings.theme == theme,
                        onClick = { onSettingsChange(settings.copy(theme = theme)) },
                        label = { Text(theme.label) },
                    )
                }
                ReaderWidth.entries.forEach { width ->
                    FilterChip(
                        selected = settings.width == width,
                        onClick = { onSettingsChange(settings.copy(width = width)) },
                        label = { Text(width.label) },
                    )
                }
            }
            Box(modifier = Modifier.weight(1f).fillMaxWidth()) {
                LazyColumn(
                    modifier = Modifier
                        .align(Alignment.TopCenter)
                        .widthIn(max = settings.width.maxDp.dp)
                        .fillMaxWidth()
                        .padding(horizontal = 24.dp, vertical = 12.dp),
                ) {
                    item {
                        Text(
                            document.title,
                            color = palette.text,
                            fontFamily = family,
                            fontWeight = FontWeight.Bold,
                            fontSize = (settings.textSizeSp + 10).sp,
                            lineHeight = (settings.textSizeSp * settings.lineHeight + 12).sp,
                        )
                        Text(
                            document.url,
                            modifier = Modifier.padding(top = 6.dp, bottom = 18.dp),
                            color = palette.muted,
                            fontSize = 12.sp,
                            maxLines = 2,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                    items(document.blocks) { block ->
                        ReaderBlockView(block, settings, palette, family, textLineHeight)
                    }
                }
            }
        }
    }
}

@Composable
private fun ReaderBlockView(
    block: ReaderBlock,
    settings: ReaderSettings,
    palette: ReaderPalette,
    family: FontFamily,
    textLineHeight: androidx.compose.ui.unit.TextUnit,
) {
    val bodyStyle = MaterialTheme.typography.bodyLarge.copy(
        fontFamily = family,
        fontSize = settings.textSizeSp.sp,
        lineHeight = textLineHeight,
        color = palette.text,
    )
    when (block) {
        is ReaderBlock.Heading -> Text(
            ReaderMarkdown.plainText(block.text),
            modifier = Modifier.padding(top = 18.dp, bottom = 6.dp),
            color = palette.text,
            fontFamily = family,
            fontWeight = FontWeight.Bold,
            fontSize = (settings.textSizeSp + (7 - block.level) * 2).coerceAtLeast(settings.textSizeSp + 1).sp,
            lineHeight = textLineHeight,
        )
        is ReaderBlock.Paragraph -> Text(
            ReaderMarkdown.plainText(block.text),
            modifier = Modifier.padding(vertical = 6.dp),
            style = bodyStyle,
        )
        is ReaderBlock.ListItem -> Text(
            "${if (block.ordered) "${block.index}." else "•"} ${ReaderMarkdown.plainText(block.text)}",
            modifier = Modifier.padding(start = (block.depth * 16).dp, top = 2.dp, bottom = 2.dp),
            style = bodyStyle,
        )
        is ReaderBlock.Quote -> Text(
            "“${ReaderMarkdown.plainText(block.text)}”",
            modifier = Modifier.padding(start = 16.dp, top = 8.dp, bottom = 8.dp),
            color = palette.muted,
            fontFamily = family,
            fontSize = settings.textSizeSp.sp,
            lineHeight = textLineHeight,
            fontWeight = FontWeight.Medium,
        )
        is ReaderBlock.Code -> Surface(
            modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
            color = palette.surface,
            shape = RoundedCornerShape(6.dp),
        ) {
            Text(
                block.text,
                modifier = Modifier.padding(12.dp).horizontalScroll(rememberScrollState()),
                color = palette.text,
                fontFamily = FontFamily.Monospace,
                fontSize = (settings.textSizeSp - 2).coerceAtLeast(12).sp,
            )
        }
        is ReaderBlock.Image -> Column(modifier = Modifier.padding(vertical = 10.dp)) {
            ReaderImage(block.url, block.alt, palette)
            if (block.alt.isNotBlank()) {
                Text(
                    block.alt,
                    modifier = Modifier.padding(top = 4.dp),
                    color = palette.muted,
                    fontSize = 12.sp,
                )
            }
        }
    }
}

@Composable
private fun ReaderImage(url: String, alt: String, palette: ReaderPalette) {
    var bitmap by remember(url) { mutableStateOf<androidx.compose.ui.graphics.ImageBitmap?>(null) }
    var failed by remember(url) { mutableStateOf(false) }
    LaunchedEffect(url) {
        val loaded = withContext(Dispatchers.IO) {
            runCatching {
                val connection = URL(url).openConnection() as? HttpURLConnection
                connection?.connectTimeout = 8_000
                connection?.readTimeout = 8_000
                connection?.inputStream?.use { BitmapFactory.decodeStream(it) }
            }.getOrNull()
        }
        bitmap = loaded?.asImageBitmap()
        failed = loaded == null
    }
    when {
        bitmap != null -> Image(
            bitmap = bitmap!!,
            contentDescription = alt,
            modifier = Modifier.fillMaxWidth().heightIn(max = 420.dp),
            contentScale = ContentScale.Fit,
        )
        failed -> Text(
            alt.ifBlank { "이미지를 불러오지 못했습니다." },
            color = palette.muted,
            fontSize = 12.sp,
        )
        else -> Text("이미지 불러오는 중…", color = palette.muted, fontSize = 12.sp)
    }
}

private fun readerFontFamily(font: ReaderFont): FontFamily = when (font) {
    ReaderFont.SERIF -> FontFamily.Serif
    ReaderFont.SANS -> FontFamily.SansSerif
    ReaderFont.MONO -> FontFamily.Monospace
}

@Composable
private fun AiPanel(viewModel: BrowserViewModel, modifier: Modifier = Modifier) {
    BoxWithConstraints(modifier = modifier.fillMaxHeight()) {
        val panelWidth = if (maxWidth * 0.92f > 520.dp) 520.dp else maxWidth * 0.92f
        Surface(
            modifier = Modifier.width(panelWidth).fillMaxHeight(),
            tonalElevation = 6.dp,
            color = MaterialTheme.colorScheme.surface,
        ) {
            Column(modifier = Modifier.fillMaxSize()) {
                Row(
                    modifier = Modifier.fillMaxWidth().padding(start = 12.dp, end = 4.dp, top = 4.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        "AI · ${viewModel.aiService.label}",
                        modifier = Modifier.weight(1f),
                        style = MaterialTheme.typography.titleMedium,
                    )
                    IconButton(onClick = viewModel::closeAiPanel) {
                        Icon(Icons.Default.Close, contentDescription = "Close AI panel")
                    }
                }
                Row(
                    modifier = Modifier.fillMaxWidth()
                        .horizontalScroll(rememberScrollState())
                        .padding(horizontal = 12.dp),
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                ) {
                    AiService.entries.forEach { service ->
                        FilterChip(
                            selected = viewModel.aiService == service,
                            onClick = { viewModel.selectAiService(service) },
                            label = { Text(service.label) },
                        )
                    }
                }
                Row(
                    modifier = Modifier.fillMaxWidth()
                        .horizontalScroll(rememberScrollState())
                        .padding(horizontal = 12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    IconButton(onClick = viewModel::aiGoBack, enabled = viewModel.aiCanGoBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "AI back")
                    }
                    IconButton(onClick = viewModel::aiReload) {
                        Icon(Icons.Default.Refresh, contentDescription = "AI reload")
                    }
                    TextButton(onClick = { viewModel.copyCurrentPageContext(false) }) {
                        Text("페이지 정보 복사")
                    }
                    TextButton(onClick = { viewModel.copyCurrentPageContext(true) }) {
                        Text("본문 포함 복사")
                    }
                }
                Text(
                    "문맥은 클립보드에만 복사되며 자동 전송되지 않습니다.",
                    modifier = Modifier.padding(horizontal = 12.dp),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                viewModel.aiNotice?.let { notice ->
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(
                            notice,
                            modifier = Modifier.weight(1f),
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.primary,
                        )
                        TextButton(onClick = viewModel::dismissAiNotice) { Text("확인") }
                    }
                }
                Box(modifier = Modifier.weight(1f).fillMaxWidth()) {
                    val session = viewModel.aiSession
                    if (session == null) {
                        CircularProgressIndicator(modifier = Modifier.align(Alignment.Center))
                    } else {
                        key(session) {
                            AndroidView(
                                factory = { context ->
                                    GeckoView(context).apply { setSession(session) }
                                },
                                update = { view ->
                                    if (view.session !== session) view.setSession(session)
                                },
                                onRelease = { view -> view.releaseSession() },
                                modifier = Modifier.fillMaxSize(),
                            )
                        }
                    }
                }
                Text(
                    "웹 AI 모드 · 로그인/팝업은 서비스 정책에 따라 제한될 수 있습니다.",
                    modifier = Modifier.padding(8.dp),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

@Composable
private fun BrowserPanelOverlay(
    panel: BrowserPanel,
    viewModel: BrowserViewModel,
    onClose: () -> Unit,
    onOpenPanel: (BrowserPanel) -> Unit,
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
                BrowserPanel.PASSWORDS -> PasswordsPanel(viewModel)
                BrowserPanel.SECURITY -> SecurityPanel(
                    httpsOnly = viewModel.httpsOnly,
                    onHttpsOnlyChange = viewModel::updateHttpsOnly,
                )
                BrowserPanel.SETTINGS -> SettingsPanel(
                    viewModel = viewModel,
                    onOpenPanel = onOpenPanel,
                    onDiagnostics = if (BuildConfig.DEBUG) {
                        { onOpenPanel(BrowserPanel.DIAGNOSTICS) }
                    } else {
                        null
                    },
                )
                BrowserPanel.DIAGNOSTICS -> DiagnosticsPanel(
                    viewModel = viewModel,
                    onClose = onClose,
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
                    text = download.mimeType,
                    style = MaterialTheme.typography.labelSmall,
                )
                Text(
                    text = downloadStatusText(download),
                    style = MaterialTheme.typography.labelSmall,
                )
                DownloadSafety.warning(download.fileName, download.mimeType)?.let { warning ->
                    Text(
                        text = warning,
                        color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.labelSmall,
                    )
                }
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
private fun PasswordsPanel(viewModel: BrowserViewModel) {
    var query by remember { mutableStateOf("") }
    var sortByName by remember { mutableStateOf(false) }
    var editorVisible by remember { mutableStateOf(false) }
    var editing by remember { mutableStateOf<StoredLogin?>(null) }
    var defaultOrigin by remember { mutableStateOf("") }

    LaunchedEffect(Unit) { viewModel.refreshPasswords() }

    val filtered = viewModel.passwords
        .filter {
            query.isBlank() ||
                it.origin.contains(query, ignoreCase = true) ||
                it.username.contains(query, ignoreCase = true)
        }
        .let { list ->
            if (sortByName) list.sortedBy { it.username.lowercase() } else list
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
            placeholder = { Text("Search saved logins") },
        )
        TextButton(onClick = { sortByName = !sortByName }) {
            Text(if (sortByName) "Sort: A-Z" else "Sort: Recent")
        }
    }
    Button(
        onClick = {
            editing = null
            defaultOrigin = PasswordRules.originFor(viewModel.activeTab?.currentUrl).orEmpty()
            editorVisible = true
        },
    ) {
        Text("Add login")
    }
    TextButton(onClick = viewModel::allowPasswordSaveForCurrentOrigin) {
        Text("Reset save prompt for current site")
    }

    if (filtered.isEmpty()) {
        EmptyPanelText("No saved logins found.")
    } else {
        LazyColumn(modifier = Modifier.fillMaxSize()) {
            items(filtered, key = { it.id }) { summary ->
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable {
                            viewModel.loadPassword(summary.id) { login ->
                                if (login != null) {
                                    editing = login
                                    defaultOrigin = login.origin
                                    editorVisible = true
                                }
                            }
                        }
                        .padding(vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = summary.username.ifBlank { "(no username)" },
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            style = MaterialTheme.typography.titleSmall,
                        )
                        Text(
                            text = summary.origin,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            style = MaterialTheme.typography.bodySmall,
                        )
                    }
                }
            }
        }
    }

    if (editorVisible) {
        PasswordEditorDialog(
            initial = editing,
            defaultOrigin = defaultOrigin,
            onDismiss = { editorVisible = false },
            onSave = { id, origin, username, password, onResult ->
                viewModel.savePassword(id, origin, username, password) { saved ->
                    onResult(saved)
                    if (saved) editorVisible = false
                }
            },
            onDelete = { id ->
                viewModel.deletePassword(id)
                editorVisible = false
            },
        )
    }
}

@Composable
private fun PasswordEditorDialog(
    initial: StoredLogin?,
    defaultOrigin: String,
    onDismiss: () -> Unit,
    onSave: (Long?, String, String, String, (Boolean) -> Unit) -> Unit,
    onDelete: (Long) -> Unit,
) {
    var origin by remember(initial?.id) { mutableStateOf(initial?.origin ?: defaultOrigin) }
    var username by remember(initial?.id) { mutableStateOf(initial?.username.orEmpty()) }
    var password by remember(initial?.id) { mutableStateOf(initial?.password.orEmpty()) }
    var showPassword by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(if (initial == null) "Add login" else "Edit login") },
        text = {
            Column {
                OutlinedTextField(
                    value = origin,
                    onValueChange = { origin = it },
                    modifier = Modifier.fillMaxWidth(),
                    singleLine = true,
                    label = { Text("HTTPS origin") },
                    placeholder = { Text("https://example.com") },
                )
                OutlinedTextField(
                    value = username,
                    onValueChange = { username = it },
                    modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
                    singleLine = true,
                    label = { Text("Username or email") },
                )
                OutlinedTextField(
                    value = password,
                    onValueChange = { password = it },
                    modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
                    singleLine = true,
                    label = { Text("Password") },
                    visualTransformation = if (showPassword) {
                        VisualTransformation.None
                    } else {
                        PasswordVisualTransformation()
                    },
                )
                TextButton(onClick = { showPassword = !showPassword }) {
                    Text(if (showPassword) "Hide password" else "Show password")
                }
                error?.let {
                    Text(it, color = MaterialTheme.colorScheme.error)
                }
            }
        },
        confirmButton = {
            TextButton(
                onClick = {
                    val normalizedOrigin = PasswordRules.originFor(origin)
                    if (!PasswordRules.isSecureOrigin(normalizedOrigin)) {
                        error = "Enter a valid HTTPS origin."
                        return@TextButton
                    }
                    onSave(initial?.id, normalizedOrigin!!, username, password) { saved ->
                        if (!saved) error = "Could not save. Check the fields and try again."
                    }
                },
            ) {
                Text("Save")
            }
        },
        dismissButton = {
            Row {
                if (initial != null) {
                    TextButton(onClick = { onDelete(initial.id) }) { Text("Delete") }
                }
                TextButton(onClick = onDismiss) { Text("Cancel") }
            }
        },
    )
}

@Composable
private fun LoginSaveDialog(
    prompt: LoginSavePrompt,
    onSave: () -> Unit,
    onNotNow: () -> Unit,
    onNever: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onNotNow,
        title = { Text(if (prompt.isUpdate) "Update saved login?" else "Save login?") },
        text = {
            Column {
                Text(prompt.origin)
                Text(
                    text = prompt.username.ifBlank { "(no username)" },
                    modifier = Modifier.padding(top = 8.dp),
                    style = MaterialTheme.typography.titleSmall,
                )
                Text(
                    "Password is protected by Android Keystore. It is not shown here.",
                    modifier = Modifier.padding(top = 8.dp),
                    style = MaterialTheme.typography.bodySmall,
                )
            }
        },
        confirmButton = {
            TextButton(onClick = onSave) {
                Text(if (prompt.isUpdate) "Update" else "Save")
            }
        },
        dismissButton = {
            Row {
                TextButton(onClick = onNotNow) { Text("Not now") }
                TextButton(onClick = onNever) { Text("Never") }
            }
        },
    )
}

@Composable
private fun AutofillBanner(
    prompt: AutofillPrompt,
    onFill: (Long) -> Unit,
    onDismiss: () -> Unit,
) {
    var choose by remember(prompt.origin, prompt.accounts.size) { mutableStateOf(false) }
    Surface(
        modifier = Modifier.fillMaxWidth(),
        color = MaterialTheme.colorScheme.secondaryContainer,
    ) {
        Row(
            modifier = Modifier.padding(start = 12.dp, end = 4.dp, top = 4.dp, bottom = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text("Saved login for ${prompt.origin}", style = MaterialTheme.typography.labelMedium)
                Text(
                    if (prompt.accounts.size == 1) prompt.accounts[0].username else
                        "${prompt.accounts.size} accounts",
                    style = MaterialTheme.typography.bodySmall,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            if (prompt.accounts.size == 1) {
                TextButton(onClick = { onFill(prompt.accounts[0].id) }) { Text("Fill") }
            } else {
                TextButton(onClick = { choose = true }) { Text("Choose") }
            }
            TextButton(onClick = onDismiss) { Text("Dismiss") }
        }
    }

    if (choose) {
        AlertDialog(
            onDismissRequest = { choose = false },
            title = { Text("Choose saved login") },
            text = {
                Column {
                    prompt.accounts.forEach { account ->
                        TextButton(
                            onClick = {
                                choose = false
                                onFill(account.id)
                            },
                        ) {
                            Text("${account.username} — ${account.origin}")
                        }
                    }
                }
            },
            confirmButton = {
                TextButton(onClick = { choose = false }) { Text("Cancel") }
            },
        )
    }
}

@Composable
private fun SettingsPanel(
    viewModel: BrowserViewModel,
    onOpenPanel: (BrowserPanel) -> Unit,
    onDiagnostics: (() -> Unit)?,
) {
    val context = LocalContext.current
    val roleManager = remember { context.getSystemService(RoleManager::class.java) }
    var isDefaultBrowser by remember {
        mutableStateOf(roleManager?.isRoleHeld(RoleManager.ROLE_BROWSER) == true)
    }
    var pendingAction by remember { mutableStateOf<DataAction?>(null) }
    val roleLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.StartActivityForResult(),
    ) {
        isDefaultBrowser = roleManager?.isRoleHeld(RoleManager.ROLE_BROWSER) == true
    }

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .verticalScroll(rememberScrollState())
            .padding(top = 8.dp),
    ) {
        SettingsSection("보안")
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text("HTTPS만 허용", style = MaterialTheme.typography.bodyLarge)
                Text("HTTP 주소를 차단합니다.", style = MaterialTheme.typography.bodySmall)
            }
            Switch(
                checked = viewModel.httpsOnly,
                onCheckedChange = viewModel::updateHttpsOnly,
            )
        }
        SettingsButton("사이트 권한 관리") { onOpenPanel(BrowserPanel.PERMISSIONS) }
        SettingsButton("비밀번호 관리") { onOpenPanel(BrowserPanel.PASSWORDS) }

        SettingsSection("검색")
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            SearchEngine.entries.forEach { engine ->
                FilterChip(
                    selected = viewModel.searchEngine == engine,
                    onClick = { viewModel.updateSearchEngine(engine) },
                    label = { Text(engine.label) },
                )
            }
        }

        SettingsSection("화면")
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            ThemeMode.entries.forEach { mode ->
                FilterChip(
                    selected = viewModel.themeMode == mode,
                    onClick = { viewModel.updateThemeMode(mode) },
                    label = { Text(mode.label) },
                )
            }
        }

        SettingsSection("데이터 관리")
        SettingsButton("방문 기록 삭제") {
            pendingAction = DataAction(
                title = "방문 기록 삭제",
                message = "AMARE에 저장된 방문 기록을 삭제합니다. 열린 탭의 뒤로/앞으로 기록은 유지됩니다.",
                action = viewModel::deleteHistoryData,
            )
        }
        SettingsButton("쿠키 삭제") {
            pendingAction = DataAction(
                title = "쿠키 삭제",
                message = "모든 사이트의 쿠키를 삭제합니다. 열린 탭은 다시 쿠키를 만들 수 있습니다.",
                action = viewModel::deleteCookies,
            )
        }
        SettingsButton("캐시 삭제") {
            pendingAction = DataAction(
                title = "캐시 삭제",
                message = "네트워크 및 이미지 캐시를 삭제합니다.",
                action = viewModel::deleteCache,
            )
        }
        SettingsButton("사이트 저장 데이터 삭제") {
            pendingAction = DataAction(
                title = "사이트 저장 데이터 삭제",
                message = "LocalStorage, IndexedDB 등 GeckoView DOM 저장 데이터를 삭제합니다.",
                action = viewModel::deleteSiteStorage,
            )
        }
        SettingsButton("사이트 권한 초기화") {
            pendingAction = DataAction(
                title = "사이트 권한 초기화",
                message = "저장된 사이트 권한을 모두 초기화합니다.",
                action = viewModel::deleteAllPermissions,
            )
        }
        SettingsButton("현재 사이트 데이터 삭제") {
            pendingAction = DataAction(
                title = "현재 사이트 데이터 삭제",
                message = "현재 탭 사이트의 쿠키/저장소/권한을 삭제합니다.",
                action = viewModel::deleteCurrentSiteData,
            )
        }
        SettingsButton("저장된 비밀번호 삭제") {
            pendingAction = DataAction(
                title = "저장된 비밀번호 삭제",
                message = "AMARE 비밀번호 저장소의 모든 계정을 삭제합니다.",
                action = viewModel::deletePasswords,
            )
        }
        SettingsButton("모든 브라우저 데이터 삭제") {
            pendingAction = DataAction(
                title = "모든 브라우저 데이터 삭제",
                message = "탭을 닫고 방문 기록, 북마크, 사이트 데이터, 권한, 비밀번호, 다운로드 기록을 삭제합니다. Android Downloads의 파일은 남습니다.",
                action = viewModel::deleteAllBrowserData,
            )
        }

        SettingsSection("일반")
        val roleAvailable = roleManager?.isRoleAvailable(RoleManager.ROLE_BROWSER) == true
        Text(
            "기본 브라우저: ${if (!roleAvailable) "지원 안 함" else if (isDefaultBrowser) "예" else "아니오"}",
            style = MaterialTheme.typography.bodyMedium,
        )
        if (roleAvailable) {
            Button(
                onClick = {
                    runCatching {
                        roleLauncher.launch(
                            roleManager!!.createRequestRoleIntent(RoleManager.ROLE_BROWSER),
                        )
                    }.onFailure {
                        // The role UI may be unavailable in a managed device.
                    }
                },
                modifier = Modifier.padding(top = 8.dp),
            ) {
                Text(if (isDefaultBrowser) "기본 브라우저 설정 확인" else "기본 브라우저로 설정")
            }
        }
        SettingsButton("모든 탭 닫기") { viewModel.closeAllTabs() }
        SettingsButton("정보") {
            pendingAction = DataAction(
                title = "정보",
                message = "AMARE Android ${BuildConfig.VERSION_NAME} (${BuildConfig.APPLICATION_ID})",
                action = null,
            )
        }
        onDiagnostics?.let { open ->
            SettingsButton("Debug Diagnostics") { open() }
        }
    }

    pendingAction?.let { action ->
        AlertDialog(
            onDismissRequest = { pendingAction = null },
            title = { Text(action.title) },
            text = { Text(action.message) },
            confirmButton = {
                TextButton(
                    onClick = {
                        pendingAction = null
                        action.action?.invoke()
                    },
                ) {
                    Text(if (action.action == null) "확인" else "삭제")
                }
            },
            dismissButton = if (action.action != null) {
                { TextButton(onClick = { pendingAction = null }) { Text("취소") } }
            } else {
                null
            },
        )
    }
}

private data class DataAction(
    val title: String,
    val message: String,
    val action: (() -> Unit)?,
)

@Composable
private fun SettingsSection(title: String) {
    Text(
        title,
        modifier = Modifier.padding(top = 16.dp, bottom = 4.dp),
        style = MaterialTheme.typography.titleSmall,
    )
}

@Composable
private fun SettingsButton(label: String, onClick: () -> Unit) {
    TextButton(onClick = onClick, modifier = Modifier.fillMaxWidth()) {
        Text(label, modifier = Modifier.fillMaxWidth())
    }
}

@Composable
private fun SecurityPanel(
    httpsOnly: Boolean,
    onHttpsOnlyChange: (Boolean) -> Unit,
) {
    Column(modifier = Modifier.fillMaxWidth().padding(top = 8.dp)) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text("HTTPS만 허용", style = MaterialTheme.typography.titleMedium)
                Text(
                    "HTTP 주소를 차단하고 HTTPS 연결만 허용합니다.",
                    style = MaterialTheme.typography.bodySmall,
                )
            }
            Switch(checked = httpsOnly, onCheckedChange = onHttpsOnlyChange)
        }
        Text(
            "인증서 오류는 자동으로 무시하지 않습니다. " +
                "Mixed Content는 GeckoView의 보안 정책을 따릅니다. " +
                "HTTPS-only를 끄면 HTTP 사이트도 열 수 있지만 보안 수준이 낮아집니다.",
            modifier = Modifier.padding(top = 12.dp),
            style = MaterialTheme.typography.bodySmall,
        )
    }
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

private fun suggestionLabel(kind: AddressSuggestionKind): String = when (kind) {
    AddressSuggestionKind.OPEN_TAB -> "열린 탭"
    AddressSuggestionKind.BOOKMARK -> "북마크"
    AddressSuggestionKind.HISTORY -> "방문 기록"
    AddressSuggestionKind.SEARCH -> "검색"
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
