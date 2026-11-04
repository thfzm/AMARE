package org.amare.browser

import android.app.NotificationManager
import android.content.Context
import android.content.pm.PackageManager
import android.database.sqlite.SQLiteDatabase
import android.os.Build
import android.os.Handler
import android.os.Looper
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import org.amare.browser.security.AndroidKeystoreProtector

private data class DiagnosticsSnapshot(
    val runtimeInitialized: Boolean,
    val tabCount: Int,
    val sessionCount: Int,
    val browserKeyAvailable: Boolean,
    val passwordKeyAvailable: Boolean,
    val browserKeystoreRoundTrip: Boolean,
    val passwordKeystoreRoundTrip: Boolean,
    val browserDbVersion: Int?,
    val passwordDbVersion: Int?,
    val androidNotificationsEnabled: Boolean,
    val postNotificationsGranted: Boolean?,
    val notificationPermissions: Int,
    val lastNotice: String?,
)

@Composable
fun DiagnosticsPanel(viewModel: BrowserViewModel, onClose: () -> Unit) {
    val context = LocalContext.current
    var snapshot by remember { mutableStateOf<DiagnosticsSnapshot?>(null) }
    val mainHandler = remember { Handler(Looper.getMainLooper()) }

    fun refresh() {
        snapshot = null
        val runtimeInitialized = viewModel.runtimeInitialized()
        val tabCount = viewModel.tabs.size
        val notificationPermissions = viewModel.sitePermissions.count {
            it.permission == PermissionRules.NOTIFICATIONS
        }
        val lastNotice = viewModel.sessionNotice
        Thread {
            val result = collectDiagnostics(
                context = context,
                runtimeInitialized = runtimeInitialized,
                tabCount = tabCount,
                notificationPermissions = notificationPermissions,
                lastNotice = lastNotice,
            )
            mainHandler.post { snapshot = result }
        }.start()
    }

    LaunchedEffect(Unit) { refresh() }

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
    ) {
        Text("Diagnostics", style = MaterialTheme.typography.headlineSmall)
        Text(
            "Debug-only screen. No URLs, passwords, notification text, or payloads are shown.",
            style = MaterialTheme.typography.bodySmall,
            modifier = Modifier.padding(vertical = 8.dp),
        )
        val current = snapshot
        if (current == null) {
            Text("Running checks...")
        } else {
            DiagnosticRow("GeckoRuntime initialized", current.runtimeInitialized.toString())
            DiagnosticRow("Tabs", current.tabCount.toString())
            DiagnosticRow("Sessions", current.sessionCount.toString())
            DiagnosticRow("Browser Keystore key", current.browserKeyAvailable.toString())
            DiagnosticRow("Password Keystore key", current.passwordKeyAvailable.toString())
            DiagnosticRow("Browser AES-GCM round-trip", current.browserKeystoreRoundTrip.toString())
            DiagnosticRow("Password AES-GCM round-trip", current.passwordKeystoreRoundTrip.toString())
            DiagnosticRow("Browser DB version", current.browserDbVersion?.toString() ?: "not created")
            DiagnosticRow("Password DB version", current.passwordDbVersion?.toString() ?: "not created")
            DiagnosticRow("Android notifications enabled", current.androidNotificationsEnabled.toString())
            DiagnosticRow(
                "POST_NOTIFICATIONS",
                current.postNotificationsGranted?.toString() ?: "not applicable",
            )
            DiagnosticRow("Stored site notification permissions", current.notificationPermissions.toString())
            DiagnosticRow("Last notice", current.lastNotice ?: "none")
        }
        Button(onClick = { refresh() }, modifier = Modifier.padding(top = 12.dp)) {
            Text("Run checks")
        }
        Button(onClick = onClose, modifier = Modifier.padding(top = 8.dp)) {
            Text("Close")
        }
    }
}

@Composable
private fun DiagnosticRow(label: String, value: String) {
    Column(modifier = Modifier.fillMaxWidth().padding(vertical = 5.dp)) {
        Text(label, style = MaterialTheme.typography.labelMedium)
        Text(value, style = MaterialTheme.typography.bodyMedium)
    }
}

private fun collectDiagnostics(
    context: Context,
    runtimeInitialized: Boolean,
    tabCount: Int,
    notificationPermissions: Int,
    lastNotice: String?,
): DiagnosticsSnapshot {
    val browserProtector = AndroidKeystoreProtector()
    val passwordProtector = AndroidKeystoreProtector("amare_passwords_v1")
    val marker = "amare-diagnostics-roundtrip".toByteArray()

    fun roundTrip(protector: AndroidKeystoreProtector): Boolean = runCatching {
        val protected = protector.protect("amare.diagnostics.v1", marker)
        protector.unprotect("amare.diagnostics.v1", protected).contentEquals(marker)
    }.getOrDefault(false)

    val browserRoundTrip = roundTrip(browserProtector)
    val passwordRoundTrip = roundTrip(passwordProtector)
    val notificationManager = context.getSystemService(NotificationManager::class.java)
    val postGranted = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
        ContextCompat.checkSelfPermission(
            context,
            "android.permission.POST_NOTIFICATIONS",
        ) == PackageManager.PERMISSION_GRANTED
    } else {
        null
    }

    return DiagnosticsSnapshot(
        runtimeInitialized = runtimeInitialized,
        tabCount = tabCount,
        sessionCount = tabCount,
        browserKeyAvailable = browserProtector.hasKey(),
        passwordKeyAvailable = passwordProtector.hasKey(),
        browserKeystoreRoundTrip = browserRoundTrip,
        passwordKeystoreRoundTrip = passwordRoundTrip,
        browserDbVersion = databaseVersion(context, "amare_browser.db"),
        passwordDbVersion = databaseVersion(context, "amare_passwords.db"),
        androidNotificationsEnabled = notificationManager.areNotificationsEnabled(),
        postNotificationsGranted = postGranted,
        notificationPermissions = notificationPermissions,
        lastNotice = lastNotice,
    )
}

private fun databaseVersion(context: Context, name: String): Int? {
    val path = context.getDatabasePath(name)
    if (!path.exists()) return null
    return runCatching {
        SQLiteDatabase.openDatabase(
            path.path,
            null,
            SQLiteDatabase.OPEN_READONLY,
        ).use { db ->
            db.rawQuery("PRAGMA user_version", null).use { cursor ->
                if (cursor.moveToFirst()) cursor.getInt(0) else null
            }
        }
    }.getOrNull()
}
