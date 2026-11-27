package com.nova.enhance

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.webkit.MimeTypeMap
import java.io.File

/**
 * 📁 Everything Nova saves goes under Download › Nova Enhance, in a sub-folder per kind
 * (Server by project · Backups · History · Submittal log · Emails · Attachments · Starred · Error log),
 * like Nova IPTV. Designed by A.HAMROUNI
 */
object Downloads {
    fun mimeOf(name: String) = MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase()) ?: "application/octet-stream"

    /** sub = the sub-folder(s) under "Nova Enhance" (e.g. "Server/SLV/10. RFIs" or "Backups"). Returns (where it is, a link apps can open). */
    fun save(c: Context, f: File, sub: String): Pair<String, Uri?> {
        val rel = ("Nova Enhance/" + sub.trim('/')).trimEnd('/')
        if (Build.VERSION.SDK_INT >= 29) {
            val v = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, f.name); put(MediaStore.Downloads.MIME_TYPE, mimeOf(f.name))
                put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/" + rel)
            }
            val u = c.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, v) ?: throw Exception("the phone refused to save the file")
            c.contentResolver.openOutputStream(u)?.use { o -> f.inputStream().use { it.copyTo(o) } } ?: throw Exception("the phone refused to save the file")
            return "Download › ${rel.replace('/', '›')} › ${f.name}" to u
        }
        val dir = File(c.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS), rel).apply { mkdirs() }
        f.copyTo(File(dir, f.name), true)
        return "Android › data › Nova Enhance › Download › $rel › ${f.name}" to null
    }

    /** Save raw bytes under Nova Enhance/<sub>/<name>. */
    fun saveBytes(c: Context, name: String, bytes: ByteArray, sub: String): Pair<String, Uri?> {
        val tmp = File.createTempFile("nd", "." + name.substringAfterLast('.', "bin"), c.cacheDir)
        tmp.writeBytes(bytes)
        val safe = File(tmp.parentFile, name.replace(Regex("[\\\\/:*?\"<>|]"), "_"))
        tmp.copyTo(safe, true); tmp.delete()
        val r = save(c, safe, sub); safe.delete(); return r
    }

    /** For a server path, the sub-folders that mirror the server (project top folder + the folders inside), under "Server". */
    fun serverSub(path: String): String {
        val p = path.trimEnd('\\')
        val inside = if (p.startsWith("\\\\")) p.trimStart('\\').substringAfter('\\').substringAfter('\\')  // \\host\share\A\B\f → A\B\f
            else p.substringAfter(":\\", p)                                                                   // S:\A\B\f → A\B\f
        val dirs = inside.substringBeforeLast('\\', "").replace('\\', '/').trim('/')
        return ("Server/" + dirs).trimEnd('/')
    }
}
