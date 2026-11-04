package org.amare.browser.data

data class Bookmark(
    val id: Long,
    val url: String,
    val title: String,
    val createdAt: Long,
)

data class HistoryEntry(
    val id: Long,
    val url: String,
    val title: String,
    val visitedAt: Long,
)

enum class DownloadStatus {
    RUNNING,
    COMPLETE,
    FAILED,
}

data class DownloadRecord(
    val id: Long,
    val fileName: String,
    val sourceUrl: String,
    val mimeType: String,
    val receivedBytes: Long,
    val totalBytes: Long,
    val status: DownloadStatus,
    val localUri: String?,
    val error: String?,
    val createdAt: Long,
)

data class SitePermissionRecord(
    val origin: String,
    val permission: String,
    val decision: String,
    val updatedAt: Long,
)
