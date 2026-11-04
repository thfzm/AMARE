package org.amare.browser

object DownloadSafety {
    private val dangerousExtensions = setOf(
        "apk", "exe", "msi", "dll", "bat", "cmd", "com", "scr", "js", "jse",
        "vbs", "vbe", "ps1", "sh", "jar", "dex",
    )

    fun warning(fileName: String, mimeType: String): String? {
        val extension = fileName.substringAfterLast('.', "").lowercase()
        if (extension in dangerousExtensions) {
            return "Potentially dangerous file type"
        }
        return when {
            mimeType.equals("application/vnd.android.package-archive", true) ->
                "Android app package"
            mimeType.equals("application/x-msdownload", true) ||
                mimeType.equals("application/x-msdos-program", true) ->
                "Windows executable"
            mimeType.startsWith("application/x-sh", true) -> "Shell script"
            else -> null
        }
    }
}
