package org.amare.browser

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.os.SystemClock
import android.provider.MediaStore
import android.webkit.URLUtil
import java.io.IOException
import org.mozilla.geckoview.WebResponse

object DownloadWriter {
    data class WriteResult(
        val localUri: Uri?,
        val receivedBytes: Long,
        val totalBytes: Long,
        val error: String?,
    )

    fun resolveFileName(response: WebResponse): String {
        val disposition = header(response, "content-disposition")
        val mimeType = header(response, "content-type")?.substringBefore(';')
            ?: "application/octet-stream"
        val guessed = runCatching {
            URLUtil.guessFileName(response.uri, disposition, mimeType)
        }.getOrDefault("download.bin")
        return DownloadNaming.sanitize(guessed)
    }

    fun resolveMimeType(response: WebResponse): String =
        header(response, "content-type")?.substringBefore(';')?.trim()
            ?.takeIf { it.isNotEmpty() } ?: "application/octet-stream"

    fun resolveTotalBytes(response: WebResponse): Long =
        header(response, "content-length")?.toLongOrNull()?.takeIf { it >= 0 } ?: -1L

    fun write(
        context: Context,
        fileName: String,
        mimeType: String,
        response: WebResponse,
        onProgress: (received: Long, total: Long) -> Unit,
    ): WriteResult {
        val total = resolveTotalBytes(response)
        val body = response.body
            ?: return WriteResult(null, 0, total, "The server returned no download body.")

        val resolver = context.contentResolver
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, fileName)
            put(MediaStore.MediaColumns.MIME_TYPE, mimeType)
            put(
                MediaStore.MediaColumns.RELATIVE_PATH,
                "${Environment.DIRECTORY_DOWNLOADS}/AMARE",
            )
            put(MediaStore.MediaColumns.IS_PENDING, 1)
        }

        val localUri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
            ?: return WriteResult(null, 0, total, "Could not create the Downloads entry.")

        var received = 0L
        var lastProgressAt = 0L
        return try {
            body.use { input ->
                val output = resolver.openOutputStream(localUri)
                    ?: throw IOException("Could not open the download output stream.")
                output.use { destination ->
                    val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        destination.write(buffer, 0, read)
                        received += read
                        val now = SystemClock.elapsedRealtime()
                        if (now - lastProgressAt >= PROGRESS_INTERVAL_MS) {
                            lastProgressAt = now
                            onProgress(received, total)
                        }
                    }
                }
            }
            onProgress(received, total)
            resolver.update(
                localUri,
                ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) },
                null,
                null,
            )
            WriteResult(localUri, received, total, null)
        } catch (error: Throwable) {
            runCatching { resolver.delete(localUri, null, null) }
            WriteResult(null, received, total, error.message ?: error.javaClass.simpleName)
        }
    }

    private fun header(response: WebResponse, name: String): String? =
        response.headers.entries
            .firstOrNull { it.key.equals(name, ignoreCase = true) }
            ?.value

    private const val PROGRESS_INTERVAL_MS = 250L
}
