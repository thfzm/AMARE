package org.amare.browser

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

class NotificationActionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != ACTION_DISMISS_NOTIFICATION) return
        val key = intent.getStringExtra(BrowserViewModel.EXTRA_NOTIFICATION_KEY) ?: return
        NotificationBridge.dismiss(key)
    }

    companion object {
        const val ACTION_DISMISS_NOTIFICATION = "org.amare.browser.DISMISS_NOTIFICATION"
    }
}

object NotificationBridge {
    @Volatile
    var dismissHandler: ((String) -> Unit)? = null

    fun dismiss(key: String) {
        dismissHandler?.invoke(key)
    }
}
