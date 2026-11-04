package org.amare.browser

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class DownloadSafetyTest {
    @Test
    fun flagsExecutableAndScriptTypes() {
        assertEquals(
            "Potentially dangerous file type",
            DownloadSafety.warning("update.apk", "application/octet-stream"),
        )
        assertEquals(
            "Potentially dangerous file type",
            DownloadSafety.warning("installer.EXE", "application/octet-stream"),
        )
        assertEquals(
            "Potentially dangerous file type",
            DownloadSafety.warning("script.sh", "text/plain"),
        )
    }

    @Test
    fun flagsDangerousMimeTypes() {
        assertEquals(
            "Android app package",
            DownloadSafety.warning(
                "download.bin",
                "application/vnd.android.package-archive",
            ),
        )
        assertEquals(
            "Windows executable",
            DownloadSafety.warning("download.bin", "application/x-msdownload"),
        )
    }

    @Test
    fun allowsOrdinaryDocumentsAndImages() {
        assertNull(DownloadSafety.warning("report.pdf", "application/pdf"))
        assertNull(DownloadSafety.warning("photo.jpg", "image/jpeg"))
        assertNull(DownloadSafety.warning("archive.zip", "application/zip"))
    }
}
