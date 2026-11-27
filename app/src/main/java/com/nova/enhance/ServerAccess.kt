package com.nova.enhance

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import com.hierynomus.msdtyp.AccessMask
import com.hierynomus.mssmb2.SMB2CreateDisposition
import com.hierynomus.mssmb2.SMB2ShareAccess
import com.hierynomus.smbj.SMBClient
import com.hierynomus.smbj.SmbConfig
import com.hierynomus.smbj.auth.AuthenticationContext
import com.hierynomus.smbj.share.DiskShare
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.security.KeyStore
import java.util.EnumSet
import java.util.concurrent.TimeUnit
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * 🏢 The office server on the phone, without the PC: where it is (sent by your PC: S: → \\192.168.1.12\…, the VPN gateway,
 * user and the firewall's fingerprint — never a password), your passwords in the phone's secure hardware store (Android Keystore),
 * read-only file access over Nova's own VPN (open · download · zip a folder · share — never write, copy to, rename or delete), and a 7-day cache. Designed by A.HAMROUNI
 */
object ServerAccess {
    private fun sp(c: Context) = c.applicationContext.getSharedPreferences("nova_server", Context.MODE_PRIVATE)
    private fun server(c: Context): JSONObject = PcLink.manifest(c)?.optJSONObject("server") ?: JSONObject()

    fun vpnHost(c: Context): String { val v = server(c).optJSONObject("vpn"); return if (v == null) sp(c).getString("host", "")!! else "${v.optString("host")}:${v.optInt("port", 443)}" }
    fun vpnUser(c: Context): String = sp(c).getString("vpn_user", null) ?: server(c).optJSONObject("vpn")?.optString("user").orEmpty()
    fun pin(c: Context): String = server(c).optJSONObject("vpn")?.optString("pin").orEmpty().ifEmpty { sp(c).getString("pin", "")!! }
    fun confirmPin(c: Context, p: String) = sp(c).edit().putString("pin", p).apply()
    fun serverHost(c: Context): String = server(c).optJSONObject("vpn")?.optString("probe").orEmpty().ifEmpty { drives(c).values.firstOrNull()?.trimStart('\\')?.substringBefore('\\') ?: "" }

    /** S: → \\192.168.1.12\Projects (from your PC). */
    fun drives(c: Context): Map<String, String> {
        val d = server(c).optJSONObject("drives") ?: return emptyMap()
        return d.keys().asSequence().associateWith { d.optString(it) }.filterValues { it.startsWith("\\\\") }
    }
    fun isServer(c: Context, path: String): Boolean = path.startsWith("\\\\") || drives(c).keys.any { path.startsWith(it, true) }

    /** 📂 A project your PC shared, with its folder addresses (server + local). */
    data class ProjFolder(val path: String, val server: Boolean, val role: String)
    data class ProjShare(val code: String, val name: String, val folders: List<ProjFolder>)
    fun projects(c: Context): List<ProjShare> {
        val arr = server(c).optJSONArray("projects") ?: return emptyList()
        return (0 until arr.length()).mapNotNull { i ->
            val o = arr.optJSONObject(i) ?: return@mapNotNull null
            val fa = o.optJSONArray("folders") ?: return@mapNotNull null
            val folders = (0 until fa.length()).mapNotNull { j -> fa.optJSONObject(j)?.let { ProjFolder(it.optString("path"), it.optBoolean("server"), it.optString("role", "")) } }
                .filterNot { isPrivate(it.path) }
            if (folders.isEmpty()) null else ProjShare(o.optString("code"), o.optString("name"), folders)
        }
    }

    /** The server login: the one typed in Setup, else the VPN username (an empty box never sends an empty name). */
    var smbUser: (Context) -> String = { c -> sp(c).getString("smb_user", null)?.trim()?.ifEmpty { null } ?: vpnUser(c) }
    fun hasSecret(c: Context, name: String) = sp(c).contains("s_$name")
    fun clearSecret(c: Context, name: String) = sp(c).edit().remove("s_$name").apply()

    /** ✓ The phone signed in to the server itself at least once (else server files come through your PC). */
    fun smbOk(c: Context) = sp(c).getBoolean("smb_ok", false)
    fun setSmbOk(c: Context, v: Boolean) = sp(c).edit().putBoolean("smb_ok", v).apply()
    /** The phone reads the server itself only when its VPN is up AND its own login worked; otherwise your PC fetches. */
    /** How server files are opened: "phone" = the phone only (default, never the PC), "phonefirst" = phone, PC as backup, "pc" = always the PC. */
    fun serverMode(c: Context) = sp(c).getString("server_mode", "phone") ?: "phone"
    fun setServerMode(c: Context, v: String) = sp(c).edit().putString("server_mode", v).apply()
    /** The phone can try its own server login now: VPN up and a password is saved. */
    fun canDirect(c: Context) = FortiVpn.up && (hasSecret(c, "smb") || hasSecret(c, "vpn"))
    /** True = use the PC for this action (live list / folder zip): always in "pc" mode, or in "phonefirst" when the phone can't go direct. */
    fun viaPc(c: Context) = serverMode(c) == "pc" || (serverMode(c) == "phonefirst" && !canDirect(c))

    fun isLoginError(e: Throwable) = (e.message ?: "").contains("LOGON_FAILURE") || (e.message ?: "").contains("0xc000006d", true)
    fun friendly(e: Throwable): String {
        val m = e.message ?: "failed"
        return when {
            isLoginError(e) -> "🔐 The server refused the phone's login — Nova fetches server files through your PC instead"
            m.contains("ACCESS_DENIED") -> "🔒 Your account may not open this on the server"
            m.contains("OBJECT_NAME_NOT_FOUND") || m.contains("PATH_NOT_FOUND") -> "Not found on the server (moved or renamed?)"
            m.contains("timed out", true) || m.contains("Timeout", true) -> "The server did not answer (VPN still connected?)"
            else -> "⚠ $m"
        }
    }
    fun smbDomain(c: Context): String = sp(c).getString("smb_domain", "")!!
    fun saveLogin(c: Context, vpnUser: String, smbUser: String, domain: String) = sp(c).edit().putString("vpn_user", vpnUser).putString("smb_user", smbUser).putString("smb_domain", domain).apply()

    // ------------------------------------------------------------------ 🔐 passwords: Android Keystore (AES-256-GCM, never synced or backed up)

    private const val ALIAS = "nova_server_key"
    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val g = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        g.init(KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build())
        return g.generateKey()
    }
    fun setSecret(c: Context, name: String, value: String) {
        if (value.isEmpty()) { sp(c).edit().remove("s_$name").apply(); return }
        val ci = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key()) }
        val box = ci.iv + ci.doFinal(value.toByteArray())
        sp(c).edit().putString("s_$name", Base64.encodeToString(box, Base64.NO_WRAP)).apply()
    }
    fun secret(c: Context, name: String): String {
        val b = sp(c).getString("s_$name", null)?.let { Base64.decode(it, Base64.NO_WRAP) } ?: return ""
        return try {
            val ci = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, b, 0, 12)) }
            String(ci.doFinal(b, 12, b.size - 12))
        } catch (_: Exception) { "" }
    }
    fun ready(c: Context) = vpnHost(c).contains(':') && vpnUser(c).isNotEmpty() && secret(c, "vpn").isNotEmpty()

    // ------------------------------------------------------------------ 📱 → 💻 one VPN session per account: tell the PC to wait

    fun hold(c: Context, minutes: Int) {
        val until = java.text.SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss", java.util.Locale.US).format(java.util.Date(System.currentTimeMillis() + minutes * 60_000L))
        Thread { try { Outbox.send(c.applicationContext, "vpnhold", JSONObject().put("until", until), if (minutes > 0) "📱 phone uses the office VPN" else "📱 phone left the office VPN") } catch (_: Exception) { } }.start()
    }

    // ------------------------------------------------------------------ 📥 a server file, read-only (SMB over Nova's VPN)

    fun cacheDir(c: Context) = File(PcLink.dir(c), "server").apply { mkdirs() }
    private fun kept(c: Context): MutableSet<String> = sp(c).getStringSet("kept", emptySet())!!.toMutableSet()
    fun keep(c: Context, f: File, on: Boolean) { val k = kept(c); if (on) k.add(f.name) else k.remove(f.name); sp(c).edit().putStringSet("kept", k).apply() }
    fun isKept(c: Context, f: File) = kept(c).contains(f.name)

    /** 🧹 opened files leave the phone after 7 days unless ⭐ kept. */
    fun expire(c: Context) {
        val k = kept(c); val old = System.currentTimeMillis() - 7L * 86_400_000
        cacheDir(c).listFiles()?.forEach { if (it.isFile && it.name !in k && it.lastModified() < old) it.delete() }
    }

    /** \\192.168.1.12\Projects\a\b.pdf (or S:\a\b.pdf through your PC's drive map) → (host, share, path inside). */
    fun unc(c: Context, path: String): Triple<String, String, String>? {
        var p = path
        drives(c).entries.firstOrNull { path.startsWith(it.key, true) }?.let { p = it.value.trimEnd('\\') + path.substring(it.key.length) }
        if (!p.startsWith("\\\\")) return null
        val parts = p.trimStart('\\').split('\\')
        if (parts.size < 3) return null
        return Triple(parts[0], parts[1], parts.drop(2).joinToString("\\"))
    }

    private fun client() = SMBClient(SmbConfig.builder().withTimeout(25, TimeUnit.SECONDS).withSoTimeout(60, TimeUnit.SECONDS)
        .withSecurityProvider(com.hierynomus.security.bc.BCSecurityProvider()).build())

    private val PRIVATE = Regex("wallet|password|passwd|keepass|bitwarden|\\b(bank|banking|crypto|seed|passport|salary|personal)\\b", RegexOption.IGNORE_CASE)
    fun isPrivate(path: String) = PRIVATE.containsMatchIn(path)

    // ------------------------------------------------------------------ 🔒 THE ONE DOOR TO THE SERVER — READ-ONLY
    // Every server action of Nova goes through these three functions, and they only ever ask the server to READ:
    // open a file with GENERIC_READ + FILE_OPEN (never create / overwrite), and list a folder. Nova has no code that writes, copies to,
    // renames, moves or deletes anything on the server — and a check before every build stops the build if such code ever appears.

    /** Signs in to the share of this path (read-only use) and runs [job] with it. */
    private fun <T> share(c: Context, host: String, shareName: String, job: (DiskShare) -> T): T {
        val pw = secret(c, "smb").ifEmpty { secret(c, "vpn") }
        client().use { cl -> cl.connect(host).use { con ->
            val ses = con.authenticate(AuthenticationContext(smbUser(c), pw.toCharArray(), smbDomain(c)))
            (ses.connectShare(shareName) as DiskShare).use { ds -> return job(ds) }
        } }
    }

    /** Reads one server file into [out] (GENERIC_READ only). */
    private fun readInto(ds: DiskShare, inside: String, out: java.io.OutputStream, onBytes: (Long, Long) -> Unit, stop: () -> Boolean = { false }) {
        ds.openFile(inside, EnumSet.of(AccessMask.GENERIC_READ), null, SMB2ShareAccess.ALL, SMB2CreateDisposition.FILE_OPEN, null).use { f ->
            val size = f.fileInformation.standardInformation.endOfFile
            f.inputStream.use { i ->
                val buf = ByteArray(256 * 1024); var n: Int; var done = 0L
                while (i.read(buf).also { n = it } > 0) { if (stop()) throw InterruptedException("stopped"); out.write(buf, 0, n); done += n; onBytes(done, size) }
            }
        }
    }

    /** What a server folder holds (names and sizes, read-only). */
    private fun listIn(ds: DiskShare, inside: String) = ds.list(inside).filter { it.fileName != "." && it.fileName != ".." && !isPrivate(it.fileName) }

    // ------------------------------------------------------------------ 🔐 test the phone's own server login (at most 3 tries)

    /** Tries the login forms once each (as typed · short domain · name@domain) and keeps the first that works. */
    fun testLogin(c: Context, say: (String) -> Unit): String {
        val d = drives(c)
        val target = d["S:"] ?: d.values.firstOrNull() ?: throw Exception("No server drive received from your PC yet")
        val (h, sh, _) = unc(c, target.trimEnd('\\') + "\\x") ?: throw Exception("No server drive")
        val u = smbUser(c).substringBefore('@'); val dom = smbDomain(c).ifEmpty { smbUser(c).substringAfter('@', "") }
        val tries = linkedSetOf(u to dom)
        if (dom.contains('.')) tries.add(u to dom.substringBefore('.').uppercase())
        if (dom.isNotEmpty()) tries.add("$u@${if (dom.contains('.')) dom else dom.lowercase() + ".local"}" to "")
        val pw = secret(c, "smb").ifEmpty { secret(c, "vpn") }
        for ((user, domain) in tries.take(3)) {
            say("🔐 Trying ${if (domain.isNotEmpty()) "$domain\\$user" else user}…")
            try {
                client().use { cl -> cl.connect(h).use { con ->
                    val ses = con.authenticate(AuthenticationContext(user, pw.toCharArray(), domain))
                    (ses.connectShare(sh) as DiskShare).use { ds -> listIn(ds, "") }
                } }
                sp(c).edit().putString("smb_user", user).putString("smb_domain", domain).apply()
                setSmbOk(c, true)
                return "✓ The phone can open the server itself (${if (domain.isNotEmpty()) "$domain\\$user" else user})"
            } catch (e: Exception) { if (!isLoginError(e)) throw Exception(friendly(e)) }
        }
        setSmbOk(c, false)
        return "The server refuses logins from the phone (office security rule). Nothing is lost: Nova opens server files through your PC."
    }

    // ------------------------------------------------------------------ 💻 → 📱 through your PC (it is already signed in to S:, O:, N:)

    /** Your PC reads the server file (or zips the folder), read-only, and sends it here encrypted (Google Drive, or in the answer when small). */
    fun viaPcFetch(c: Context, path: String, zip: Boolean, progress: (String) -> Unit): File {
        if (isPrivate(path)) throw Exception("🔒 private — not opened")
        if (FortiVpn.up && serverMode(c) != "phone") { progress("📱 → 💻 giving the office VPN back to your PC…"); FortiVpn.stop(c); Thread.sleep(3000) }
        progress("⏳ Asking your PC to read it from the server…")
        val o = PcLink.ask(c, "serverfile", JSONObject().put("path", path).put("zip", zip)) { progress(it) }
        val name = o.optString("name").ifEmpty { path.trimEnd('\\').substringAfterLast('\\') }.replace(Regex("[\\\\/:*?\"<>|]"), "_")
        val out = File(cacheDir(c), name)
        if (o.has("data")) { out.writeBytes(PcLink.gunzip(Base64.decode(o.getString("data"), Base64.DEFAULT))); log(c, path, if (zip) "zipped via PC" else "via PC"); return out }
        val rel = o.optString("drive"); val err = o.optString("err")
        if (rel.isEmpty()) throw Exception("your PC did not send the file")
        if (!GDrive.usable(c)) throw Exception("Sign in to Google Drive (My PC → ⚙) — big files come that way")
        val t0 = System.currentTimeMillis()
        while (System.currentTimeMillis() - t0 < 15 * 60_000L) {
            Thread.sleep(5000)
            val s = (System.currentTimeMillis() - t0) / 1000
            progress("⏳ Your PC reads it and Google Drive uploads it… ${s / 60}:${"%02d".format(s % 60)}")
            GDrive.fetchSync(c, err)?.let { throw Exception("Your PC: " + String(PcLink.openSealed(c, it))) }
            val box = GDrive.fetchSync(c, rel) ?: continue
            progress("📥 Downloading %.1f MB…".format(box.size / 1e6))
            out.writeBytes(PcLink.openSealed(c, box))
            log(c, path, if (zip) "zipped via PC" else "via PC")
            return out
        }
        throw Exception("Not arrived after 15 min — is Google Drive for desktop running on the PC?")
    }

    /** What a server folder holds, counted by your PC. */
    fun viaPcInfo(c: Context, path: String): Measure {
        if (FortiVpn.up && serverMode(c) != "phone") { FortiVpn.stop(c); Thread.sleep(3000) }
        val o = PcLink.ask(c, "serverfile", JSONObject().put("path", path).put("info", true))
        return Measure(o.optInt("files"), o.optLong("bytes"))
    }

    /** A live look at a server folder through your PC. */
    fun viaPcList(c: Context, path: String): List<Pair<String, Boolean>> {
        if (FortiVpn.up && serverMode(c) != "phone") { FortiVpn.stop(c); Thread.sleep(3000) }
        val a = PcLink.ask(c, "serverfile", JSONObject().put("path", path).put("list", true)).optJSONArray("items") ?: JSONArray()
        return (0 until a.length()).map { a.getJSONObject(it).let { o -> o.optString("n") to o.optBoolean("d") } }.filterNot { isPrivate(it.first) }
    }

    // ------------------------------------------------------------------ 📥 a server file onto the phone

    fun download(c: Context, path: String, progress: (String) -> Unit): File {
        val (h, sh, inside) = unc(c, path) ?: throw Exception("this file is not on the server (a local copy on your PC)")
        if (isPrivate(path)) throw Exception("🔒 private — not opened")
        val out = File(cacheDir(c), inside.substringAfterLast('\\').replace(Regex("[\\\\/:*?\"<>|]"), "_"))
        var last = 0L
        share(c, h, sh) { ds -> out.outputStream().use { o -> readInto(ds, inside, o, { d, t -> if (d - last > 1_000_000) { last = d; progress("📥 %.1f of %.1f MB".format(d / 1e6, t / 1e6)) } }) } }
        setSmbOk(c, true)
        log(c, path, "opened")
        return out
    }

    /** A live look at a server folder (what is there right now). */
    fun list(c: Context, folder: String): List<Pair<String, Boolean>> {
        val (h, sh, inside) = unc(c, folder.trimEnd('\\') + "\\x")?.let { Triple(it.first, it.second, it.third.removeSuffix("x").trimEnd('\\')) } ?: return emptyList()
        return share(c, h, sh) { ds -> listIn(ds, inside).map { it.fileName to ((it.fileAttributes and 0x10L) != 0L) }.sortedWith(compareBy({ !it.second }, { it.first.lowercase() })) }
    }

    // ------------------------------------------------------------------ 🗜 a whole server folder as one zip (built on the phone)

    class Measure(val files: Int, val bytes: Long)

    private fun walk(ds: DiskShare, inside: String, rel: String, add: (String, String, Long) -> Unit, stop: () -> Boolean) {
        for (e in listIn(ds, inside)) {
            if (stop()) return
            val child = if (inside.isEmpty()) e.fileName else "$inside\\${e.fileName}"
            val r = if (rel.isEmpty()) e.fileName else "$rel/${e.fileName}"
            if ((e.fileAttributes and 0x10L) != 0L) walk(ds, child, r, add, stop) else add(child, r, e.endOfFile)
        }
    }

    /** How many files and MB a server folder holds (asked before a big zip). */
    fun measure(c: Context, folder: String, stop: () -> Boolean = { false }): Measure {
        val (h, sh, inside) = unc(c, folder.trimEnd('\\') + "\\x")?.let { Triple(it.first, it.second, it.third.removeSuffix("x").trimEnd('\\')) } ?: throw Exception("this folder is not on the server")
        if (isPrivate(folder)) throw Exception("🔒 private — not opened")
        var n = 0; var b = 0L
        share(c, h, sh) { ds -> walk(ds, inside, "", { _, _, size -> n++; b += size }, stop) }
        return Measure(n, b)
    }

    /** The folder's files packed into one zip on the phone (🔒 private files left out). Nothing is written on the server. */
    fun zipFolder(c: Context, folder: String, progress: (String) -> Unit, stop: () -> Boolean): File {
        val (h, sh, inside) = unc(c, folder.trimEnd('\\') + "\\x")?.let { Triple(it.first, it.second, it.third.removeSuffix("x").trimEnd('\\')) } ?: throw Exception("this folder is not on the server")
        if (isPrivate(folder)) throw Exception("🔒 private — not opened")
        val name = folder.trimEnd('\\').substringAfterLast('\\').replace(Regex("[\\\\/:*?\"<>|]"), "_").ifEmpty { "server" }
        val out = File(File(cacheDir(c), "zip").apply { mkdirs() }, "$name.zip")
        val part = File(out.path + ".part")
        try {
            share(c, h, sh) { ds ->
                val items = mutableListOf<Triple<String, String, Long>>()
                walk(ds, inside, "", { a, r, size -> items.add(Triple(a, r, size)) }, stop)
                val total = items.sumOf { it.third }.coerceAtLeast(1)
                var before = 0L; var shown = 0L
                java.util.zip.ZipOutputStream(part.outputStream().buffered()).use { z ->
                    items.forEachIndexed { i, (a, r, size) ->
                        if (stop()) throw InterruptedException("stopped")
                        z.putNextEntry(java.util.zip.ZipEntry("$name/$r"))
                        readInto(ds, a, z, { d, _ -> val all = before + d; if (all - shown > 1_000_000) { shown = all; progress("🗜 ${i + 1} of ${items.size} file(s) · %.1f of %.1f MB".format(all / 1e6, total / 1e6)) } }, stop)
                        z.closeEntry(); before += size
                    }
                }
            }
            out.delete(); part.renameTo(out)
        } catch (e: Exception) { part.delete(); throw e }
        log(c, folder, "zipped")
        return out
    }

    // ------------------------------------------------------------------ ⬇ into the phone's Download folder

    /** Saved into Download › Nova Enhance › Server (the phone's own Download folder). Returns (where, a link the phone's apps can open). */
    fun toDownloads(c: Context, f: File): Pair<String, android.net.Uri?> {
        val mime = android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(f.extension.lowercase()) ?: "application/octet-stream"
        if (android.os.Build.VERSION.SDK_INT >= 29) {
            val v = android.content.ContentValues().apply {
                put(android.provider.MediaStore.Downloads.DISPLAY_NAME, f.name); put(android.provider.MediaStore.Downloads.MIME_TYPE, mime)
                put(android.provider.MediaStore.Downloads.RELATIVE_PATH, android.os.Environment.DIRECTORY_DOWNLOADS + "/Nova Enhance/Server")
            }
            val u = c.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI, v) ?: throw Exception("the phone refused to save the file")
            c.contentResolver.openOutputStream(u)?.use { o -> f.inputStream().use { it.copyTo(o) } } ?: throw Exception("the phone refused to save the file")
            return "Download › Nova Enhance › Server › ${f.name}" to u
        }
        val dir = File(c.getExternalFilesDir(android.os.Environment.DIRECTORY_DOWNLOADS), "Nova Enhance/Server").apply { mkdirs() }
        f.copyTo(File(dir, f.name), true)
        return "Android › data › Nova Enhance › Download › Server › ${f.name}" to null
    }

    /** The copy already in Download (same server date and size) — opened again without downloading. */
    fun downloaded(c: Context, path: String, m: Long, b: Long): android.net.Uri? {
        val o = try { JSONObject(sp(c).getString("dl:$path", "") ?: "") } catch (_: Exception) { return null }
        if (m > 0 && o.optLong("m") != m || b > 0 && o.optLong("b") != b) return null
        val u = android.net.Uri.parse(o.optString("u").ifEmpty { return null })
        return try { c.contentResolver.openFileDescriptor(u, "r")?.use { }; u } catch (_: Exception) { null }
    }
    fun rememberDownload(c: Context, path: String, m: Long, b: Long, u: android.net.Uri?) {
        if (u != null) sp(c).edit().putString("dl:$path", JSONObject().put("u", u.toString()).put("m", m).put("b", b).toString()).apply()
    }

    // every file opened, downloaded, zipped or shared from the server is noted on the phone
    fun log(c: Context, path: String, what: String = "opened") {
        val a = try { JSONArray(sp(c).getString("opens", "[]")) } catch (_: Exception) { JSONArray() }
        a.put(JSONObject().put("p", path).put("at", System.currentTimeMillis()).put("w", what))
        while (a.length() > 200) a.remove(0)
        sp(c).edit().putString("opens", a.toString()).apply()
    }
    fun opens(c: Context): List<Triple<String, Long, String>> = try { val a = JSONArray(sp(c).getString("opens", "[]")); (0 until a.length()).map { a.getJSONObject(it).let { o -> Triple(o.optString("p"), o.optLong("at"), o.optString("w", "opened")) } }.reversed() } catch (_: Exception) { emptyList() }
}
