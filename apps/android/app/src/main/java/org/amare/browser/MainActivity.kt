package org.amare.browser

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable

class MainActivity : ComponentActivity() {
    private val browserViewModel: BrowserViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            AmareTheme {
                BrowserScreen(browserViewModel)
            }
        }
    }

    override fun onStop() {
        browserViewModel.onHostStop()
        super.onStop()
    }

    override fun onDestroy() {
        browserViewModel.onHostDestroy(isFinishing)
        super.onDestroy()
    }
}

@Composable
private fun AmareTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = if (isSystemInDarkTheme()) darkColorScheme() else lightColorScheme(),
        content = content,
    )
}
