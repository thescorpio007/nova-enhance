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
 * 🖧 The office server on the phone, without the PC: where it is (sent by your PC: S: → \\192.168.1.12\…, the VPN gateway,
 * user and the firewall's fingerprint — never a password), your passwords in the phone's secure hardware store (Android Keystore),
 * read-only file access over Nova's own VPN, and a 7-day cache. Designed by A.HAMROUNI
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

    var smbUser: (Context) -> String = { c -> sp(c).getString("smb_user", null) ?: vpnUser(c) }
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

    fun download(c: Context, path: String, progress: (String) -> Unit): File {
        val (h, share, inside) = unc(c, path) ?: throw Exception("this file is not on the server (a local copy on your PC)")
        if (Regex("wallet|password|passwd|keepass|bitwarden|\\b(bank|banking|crypto|seed|passport|salary|personal)\\b", RegexOption.IGNORE_CASE).containsMatchIn(path))
            throw Exception("🔒 private — not opened")
        val pw = secret(c, "smb").ifEmpty { secret(c, "vpn") }
        val out = File(cacheDir(c), inside.substringAfterLast('\\').replace(Regex("[\\\\/:*?\"<>|]"), "_"))
        client().use { cl ->
            cl.connect(h).use { con ->
                val ses = con.authenticate(AuthenticationContext(smbUser(c), pw.toCharArray(), smbDomain(c)))
                (ses.connectShare(share) as DiskShare).use { ds ->
                    // read-only: only GENERIC_READ is ever asked for — Nova can't change, rename or delete anything on the server
                    ds.openFile(inside, EnumSet.of(AccessMask.GENERIC_READ), null, SMB2ShareAccess.ALL, SMB2CreateDisposition.FILE_OPEN, null).use { f ->
                        val size = f.fileInformation.standardInformation.endOfFile
                        f.inputStream.use { i -> out.outputStream().use { o ->
                            val buf = ByteArray(256 * 1024); var n: Int; var done = 0L; var last = 0L
                            while (i.read(buf).also { n = it } > 0) { o.write(buf, 0, n); done += n; if (done - last > 1_000_000) { last = done; progress("📥 %.1f of %.1f MB".format(done / 1e6, size / 1e6)) } }
                        } }
                    }
                }
            }
        }
        log(c, path)
        return out
    }

    /** A live look at a server folder (what is there right now). */
    fun list(c: Context, folder: String): List<Pair<String, Boolean>> {
        val (h, share, inside) = unc(c, folder.trimEnd('\\') + "\\x")?.let { Triple(it.first, it.second, it.third.removeSuffix("x").trimEnd('\\')) } ?: return emptyList()
        val pw = secret(c, "smb").ifEmpty { secret(c, "vpn") }
        client().use { cl -> cl.connect(h).use { con ->
            val ses = con.authenticate(AuthenticationContext(smbUser(c), pw.toCharArray(), smbDomain(c)))
            (ses.connectShare(share) as DiskShare).use { ds ->
                return ds.list(inside).filter { it.fileName != "." && it.fileName != ".." }
                    .map { it.fileName to ((it.fileAttributes and 0x10L) != 0L) }
                    .filterNot { Regex("wallet|password|passwd|keepass|bitwarden|\\b(bank|banking|crypto|seed|passport|salary|personal)\\b", RegexOption.IGNORE_CASE).containsMatchIn(it.first) }
                    .sortedWith(compareBy({ !it.second }, { it.first.lowercase() }))
            }
        } }
    }

    // every file opened from the server is noted on the phone
    private fun log(c: Context, path: String) {
        val a = try { JSONArray(sp(c).getString("opens", "[]")) } catch (_: Exception) { JSONArray() }
        a.put(JSONObject().put("p", path).put("at", System.currentTimeMillis()))
        while (a.length() > 200) a.remove(0)
        sp(c).edit().putString("opens", a.toString()).apply()
    }
    fun opens(c: Context): List<Pair<String, Long>> = try { val a = JSONArray(sp(c).getString("opens", "[]")); (0 until a.length()).map { a.getJSONObject(it).let { o -> o.optString("p") to o.optLong("at") } }.reversed() } catch (_: Exception) { emptyList() }
}
