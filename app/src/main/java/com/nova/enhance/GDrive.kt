package com.nova.enhance

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.ServerSocket
import java.net.URL
import java.net.URLEncoder
import java.security.MessageDigest
import java.security.SecureRandom

/**
 * ☁ The phone reads Google Drive by itself — the PC can be off.
 * The PC tells the phone (QR code or the encrypted manifest) which Google sign-in app to use (the one set up on the PC),
 * where "Nova Enhance Sync" is, and where the work folder is copied in My Drive (FreeFileSync…).
 * Sign-in: Google's page in the browser, answer on this phone only (127.0.0.1), PKCE; read-only access.
 * The sync files stay encrypted with the pairing key: Google only stores locked files. Designed by A.HAMROUNI
 */
object GDrive {
    private const val API = "https://www.googleapis.com/drive/v3"
    private const val FOLDER = "application/vnd.google-apps.folder"
    private fun sp(c: Context) = c.applicationContext.getSharedPreferences("nova_pc", Context.MODE_PRIVATE)

    fun clientId(c: Context) = sp(c).getString("gd_id", "").orEmpty()
    private fun secret(c: Context) = sp(c).getString("gd_secret", "").orEmpty()
    private fun refresh(c: Context) = sp(c).getString("gd_refresh", "").orEmpty()
    fun account(c: Context) = sp(c).getString("gd_email", "").orEmpty()
    fun configured(c: Context) = clientId(c).length > 10 && secret(c).isNotEmpty()
    fun signedIn(c: Context) = configured(c) && refresh(c).isNotEmpty()
    private fun folderPath(c: Context) = sp(c).getString("gd_folder", "Nova Enhance Sync").orEmpty().ifBlank { "Nova Enhance Sync" }
    private fun work(c: Context) = sp(c).getString("gd_work", "").orEmpty()
    /** null = the work folder is not in Drive; "" = My Drive itself; "_MZA" = that folder. */
    private fun mirror(c: Context): String? = if (sp(c).getBoolean("gd_mirror_known", false)) sp(c).getString("gd_mirror", "") else null

    /** From the QR code or the manifest ("gd": {id, secret, folder, work, mirror}). */
    fun learn(c: Context, o: JSONObject?) {
        if (o == null) return
        val e = sp(c).edit()
        val id = o.optString("id")
        if (id.isNotBlank()) {
            if (id != clientId(c)) e.remove("gd_refresh").remove("gd_email")      // another Google app: sign in again
            e.putString("gd_id", id).putString("gd_secret", o.optString("secret"))
        }
        e.putString("gd_folder", o.optString("folder", "Nova Enhance Sync")).putString("gd_work", o.optString("work"))
        if (o.isNull("mirror")) e.putBoolean("gd_mirror_known", false) else e.putBoolean("gd_mirror_known", true).putString("gd_mirror", o.optString("mirror"))
        e.apply()
    }

    fun signOut(c: Context) { sp(c).edit().remove("gd_refresh").remove("gd_email").apply(); access = null }

    // ------------------------------------------------------------------ sign-in (browser → 127.0.0.1 on this phone)

    private fun b64(b: ByteArray) = Base64.encodeToString(b, Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING)
    private fun enc(s: String) = URLEncoder.encode(s, "UTF-8")

    /** Opens Google's page; waits up to 5 minutes for the answer. Run on a background thread. Returns the account. */
    fun signIn(act: Activity, progress: (String) -> Unit): String {
        val c = act.applicationContext
        if (!configured(c)) throw Exception("Your PC has not shared its Google sign-in yet — on the PC: Settings → Google Drive (Client ID and secret), then ⟳ update the phone over Wi-Fi or scan the QR code again")
        val rnd = SecureRandom()
        val verifier = b64(ByteArray(48).also { rnd.nextBytes(it) })
        val challenge = b64(MessageDigest.getInstance("SHA-256").digest(verifier.toByteArray(Charsets.US_ASCII)))
        val state = b64(ByteArray(16).also { rnd.nextBytes(it) })
        val server = ServerSocket(0, 1, InetAddress.getByName("127.0.0.1"))
        server.soTimeout = 300_000
        val redirect = "http://127.0.0.1:${server.localPort}"
        val url = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&access_type=offline&prompt=consent" +
            "&client_id=${enc(clientId(c))}&redirect_uri=${enc(redirect)}&scope=${enc("https://www.googleapis.com/auth/drive.readonly email")}" +
            "&code_challenge=$challenge&code_challenge_method=S256&state=$state"
        act.runOnUiThread { try { act.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url))) } catch (_: Exception) { } }
        progress("⏳ Sign in with Google in the browser, then come back to Nova…")
        var code: String? = null; var error: String? = null
        try {
            while (code == null && error == null) {
                val s = server.accept()
                s.use { sock ->
                    val line = sock.getInputStream().bufferedReader().readLine() ?: ""
                    val path = Regex("GET (\\S+) HTTP").find(line)?.groupValues?.get(1) ?: ""
                    val q = Uri.parse("http://x$path")
                    if (q.getQueryParameter("state") == state) { code = q.getQueryParameter("code"); if (code == null) error = q.getQueryParameter("error") ?: "cancelled" }
                    val ok = code != null
                    val html = "<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width'><body style='font-family:sans-serif;background:#12151C;color:#ECEEF3;text-align:center;padding-top:60px'>" +
                        "<h2>${if (ok) "✓ Nova Enhance can read your Google Drive" else "Google sign-in was not completed"}</h2><p>${if (ok) "Go back to the Nova Enhance app." else (error ?: "")}</p><p style='color:#6B7385'>${Ui.SIGNATURE}</p>"
                    val body = html.toByteArray()
                    sock.getOutputStream().apply {
                        write("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: ${body.size}\r\nConnection: close\r\n\r\n".toByteArray()); write(body); flush()
                    }
                }
            }
        } catch (_: java.net.SocketTimeoutException) { throw Exception("Google sign-in took more than 5 minutes — try again") }
        finally { try { server.close() } catch (_: Exception) { } }
        if (code == null) throw Exception("Google sign-in was not completed: $error")
        progress("⏳ Connecting to Google Drive…")
        val tok = form("https://oauth2.googleapis.com/token", mapOf("code" to code!!, "client_id" to clientId(c), "client_secret" to secret(c),
            "redirect_uri" to redirect, "grant_type" to "authorization_code", "code_verifier" to verifier))
        val r = tok.optString("refresh_token")
        if (r.isBlank()) throw Exception("Google did not give a lasting sign-in — remove Nova Enhance in your Google account (Security → Third-party access) and sign in again")
        sp(c).edit().putString("gd_refresh", r).apply()
        access = tok.optString("access_token"); accessUntil = System.currentTimeMillis() + (tok.optLong("expires_in", 3600) - 120) * 1000
        val email = try { get(c, "$API/about?fields=user(emailAddress)").optJSONObject("user")?.optString("emailAddress").orEmpty() } catch (_: Exception) { "" }
        sp(c).edit().putString("gd_email", email).apply()
        return email
    }

    // ------------------------------------------------------------------ HTTP

    @Volatile private var access: String? = null
    @Volatile private var accessUntil = 0L

    private fun form(url: String, f: Map<String, String>): JSONObject {
        val h = URL(url).openConnection() as HttpURLConnection
        h.requestMethod = "POST"; h.doOutput = true; h.connectTimeout = 20000; h.readTimeout = 60000
        h.setRequestProperty("Content-Type", "application/x-www-form-urlencoded")
        h.outputStream.use { it.write(f.entries.joinToString("&") { enc(it.key) + "=" + enc(it.value) }.toByteArray()) }
        val code = h.responseCode
        val txt = (if (code in 200..299) h.inputStream else h.errorStream)?.use { it.readBytes().toString(Charsets.UTF_8) } ?: ""
        h.disconnect()
        val o = try { JSONObject(txt) } catch (_: Exception) { JSONObject() }
        if (code !in 200..299) throw Exception("Google: " + (o.optString("error_description").ifBlank { o.optString("error").ifBlank { "HTTP $code" } }))
        return o
    }

    @Synchronized private fun token(c: Context): String {
        access?.let { if (System.currentTimeMillis() < accessUntil) return it }
        if (!signedIn(c)) throw Exception("Not signed in to Google Drive on this phone")
        val o = try {
            form("https://oauth2.googleapis.com/token", mapOf("client_id" to clientId(c), "client_secret" to secret(c), "refresh_token" to refresh(c), "grant_type" to "refresh_token"))
        } catch (e: Exception) {
            if ((e.message ?: "").contains("invalid_grant", true) || (e.message ?: "").contains("expired", true) || (e.message ?: "").contains("revoked", true)) signOut(c)
            throw Exception("Google Drive sign-in expired — sign in again (🖥 My PC → ⚙)")
        }
        access = o.optString("access_token"); accessUntil = System.currentTimeMillis() + (o.optLong("expires_in", 3600) - 120) * 1000
        return access!!
    }

    private fun raw(c: Context, url: String): Pair<Int, ByteArray> {
        val h = URL(url).openConnection() as HttpURLConnection
        h.connectTimeout = 20000; h.readTimeout = 300000
        h.setRequestProperty("Authorization", "Bearer " + token(c))
        val code = h.responseCode
        val b = (if (code in 200..299) h.inputStream else h.errorStream)?.use { it.readBytes() } ?: ByteArray(0)
        h.disconnect()
        return code to b
    }

    private fun get(c: Context, url: String): JSONObject {
        val (code, b) = raw(c, url)
        if (code !in 200..299) throw Exception("Google Drive HTTP $code")
        return JSONObject(String(b, Charsets.UTF_8))
    }

    fun download(c: Context, id: String): ByteArray? {
        val (code, b) = raw(c, "$API/files/$id?alt=media&supportsAllDrives=true")
        return if (code == 200) b else null
    }

    private fun q(s: String) = s.replace("\\", "\\\\").replace("'", "\\'")

    private fun children(c: Context, parent: String, name: String? = null): List<JSONObject> {
        val out = ArrayList<JSONObject>()
        var page = ""
        do {
            val query = "'${q(parent)}' in parents and trashed=false" + (if (name != null) " and name='${q(name)}'" else "")
            val o = get(c, "$API/files?q=${enc(query)}&pageSize=1000&fields=${enc("nextPageToken,files(id,name,mimeType,size,modifiedTime)")}" +
                (if (page.isNotEmpty()) "&pageToken=${enc(page)}" else ""))
            val a = o.optJSONArray("files") ?: JSONArray()
            for (i in 0 until a.length()) out.add(a.getJSONObject(i))
            page = o.optString("nextPageToken")
        } while (page.isNotEmpty())
        return out
    }

    /** Walks My Drive by names ("a/b/c"); null when a part is missing. */
    private fun walk(c: Context, path: String, lastIsFile: Boolean = false): String? {
        var id = "root"
        val parts = path.split('/', '\\').filter { it.isNotBlank() }
        for ((i, p) in parts.withIndex()) {
            val kids = children(c, id, p)
            val pick = if (lastIsFile && i == parts.size - 1) kids.firstOrNull { it.optString("mimeType") != FOLDER } else kids.firstOrNull { it.optString("mimeType") == FOLDER }
            id = pick?.optString("id") ?: return null
        }
        return id
    }

    // ------------------------------------------------------------------ 📥 the sync folder

    /** Every file of "Nova Enhance Sync" (with its sub-folders): "email/full-0001.nes" → Drive id. */
    fun syncFiles(c: Context): Map<String, String> {
        val top = walk(c, folderPath(c))
            ?: get(c, "$API/files?q=${enc("name='${q(folderPath(c).substringAfterLast('/'))}' and mimeType='$FOLDER' and trashed=false")}&fields=files(id)")
                .optJSONArray("files")?.optJSONObject(0)?.optString("id")
            ?: throw Exception("“${folderPath(c)}” was not found in your Google Drive (${account(c)}) — is Google Drive for desktop running on the PC?")
        val map = HashMap<String, String>()
        fun scan(id: String, prefix: String, depth: Int) {
            for (f in children(c, id)) {
                val n = f.optString("name")
                if (f.optString("mimeType") == FOLDER) { if (depth < 3) scan(f.optString("id"), "$prefix$n/", depth + 1) }
                else map.putIfAbsent(prefix + n, f.optString("id"))
            }
        }
        scan(top, "", 0)
        return map
    }

    // ------------------------------------------------------------------ 📂 a project file, straight from Drive

    /** True when this PC path is inside the work folder that is copied to Google Drive. */
    fun canOpen(c: Context, pcPath: String): Boolean {
        val w = work(c).trimEnd('\\', '/')
        return signedIn(c) && mirror(c) != null && w.isNotEmpty() && pcPath.startsWith("$w\\", true)
    }

    /** Downloads the file from My Drive (same place as on the PC) into the phone's My PC folder. */
    fun fetchWork(c: Context, pcPath: String, progress: (String) -> Unit): File {
        val w = work(c).trimEnd('\\', '/')
        val rel = pcPath.substring(w.length).trim('\\', '/').replace('\\', '/')
        val m = mirror(c) ?: throw Exception("The work folder is not in Google Drive")
        progress("⏳ Finding it in Google Drive…")
        val id = walk(c, (if (m.isNotEmpty()) "$m/" else "") + rel, lastIsFile = true)
            ?: throw Exception("Not in Google Drive yet (FreeFileSync not run since it changed?)")
        progress("📥 Downloading from Google Drive…")
        val data = download(c, id) ?: throw Exception("Google Drive refused the download")
        val f = File(File(PcLink.dir(c), "fetched").apply { mkdirs() }, rel.substringAfterLast('/').replace(Regex("[\\\\/:*?\"<>|]"), "_"))
        f.writeBytes(data)
        return f
    }
}
