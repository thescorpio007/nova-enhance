package com.nova.enhance

import android.content.Context
import android.content.Intent
import android.net.VpnService
import android.os.ParcelFileDescriptor
import android.util.Base64
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.OutputStream
import java.net.HttpURLConnection
import java.net.InetSocketAddress
import java.net.Socket
import java.net.URL
import java.net.URLEncoder
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.X509Certificate
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.X509TrustManager

/**
 * 🖧 Nova's own office VPN (Fortinet SSL VPN, the same protocol as the PC's OpenConnect engine) — for Nova ONLY:
 * no other app's traffic goes to the office, and only the office network goes through it.
 *  1 · signs in over HTTPS to the firewall (its certificate must match the fingerprint your PC trusts);
 *  2 · reads the address and the office routes the firewall gives;
 *  3 · opens the tunnel (PPP inside TLS), then Android's VPN carries Nova's server traffic.
 * The password is used for the sign-in only, never written anywhere. One session per account: the PC is told to wait.
 * Designed by A.HAMROUNI
 */
class FortiVpn : VpnService() {
    companion object {
        @Volatile var state = "off"          // off · connecting · up · error
        @Volatile var line = "🔴 Office VPN off"
        @Volatile var ip = ""
        @Volatile var seenPin = ""           // the firewall's fingerprint (when no pin was set yet)
        @Volatile private var pending: Triple<String, String, String>? = null   // host:port, user, password — memory only, once
        @Volatile private var inst: FortiVpn? = null
        var onChange: (() -> Unit)? = null

        fun start(c: Context, host: String, user: String, password: String) {
            pending = Triple(host, user, password)
            c.startService(Intent(c, FortiVpn::class.java).setAction("start"))
        }
        fun stop(c: Context) { inst?.shutdown("🔴 Office VPN off") ?: run { state = "off"; line = "🔴 Office VPN off"; onChange?.invoke() } }
        val up get() = state == "up"
    }

    @Volatile private var running = false
    private var tls: SSLSocket? = null
    private var tun: ParcelFileDescriptor? = null
    private var host = ""; private var port = 443; private var cookie = ""

    private fun say(st: String, l: String) { state = st; line = l; onChange?.invoke() }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == "start") {
            val p = pending ?: return START_NOT_STICKY
            pending = null
            inst = this
            Thread { connect(p.first, p.second, p.third) }.start()
        }
        return START_NOT_STICKY
    }

    override fun onRevoke() { shutdown("🔴 Office VPN off (switched off in Android)") }
    override fun onDestroy() { shutdown("🔴 Office VPN off"); super.onDestroy() }

    fun shutdown(why: String) {
        val was = running
        running = false
        try { if (cookie.isNotEmpty()) Thread { try { https("/remote/logout", null) } catch (_: Exception) { } }.start() } catch (_: Exception) { }
        try { tls?.close() } catch (_: Exception) { }
        try { tun?.close() } catch (_: Exception) { }
        tls = null; tun = null; ip = ""
        if (was) ServerAccess.hold(applicationContext, 0)   // 📱 → 💻 the PC may reconnect now
        say("off", why)
        inst = null
        stopSelf()
    }

    // ------------------------------------------------------------------ 🔐 the firewall's certificate: only the one your PC trusts

    private fun pinOf(cert: X509Certificate) = "pin-sha256:" + Base64.encodeToString(MessageDigest.getInstance("SHA-256").digest(cert.publicKey.encoded), Base64.NO_WRAP)

    private val trust = object : X509TrustManager {
        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {}
        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            val leaf = chain?.firstOrNull() ?: throw java.security.cert.CertificateException("no certificate")
            val got = pinOf(leaf)
            val want = ServerAccess.pin(applicationContext)
            if (want.isEmpty()) { seenPin = got; throw java.security.cert.CertificateException("PIN_UNKNOWN") }
            if (!got.equals(want, true)) { seenPin = got; throw java.security.cert.CertificateException("PIN_MISMATCH") }
        }
        override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
    }
    private val ssl: SSLSocketFactory by lazy { SSLContext.getInstance("TLS").apply { init(null, arrayOf(trust), SecureRandom()) }.socketFactory }

    private fun https(path: String, body: String?): Pair<String, Map<String, List<String>>> {
        val u = URL("https://$host:$port$path")
        val h = (u.openConnection() as HttpsURLConnection).apply {
            sslSocketFactory = ssl; hostnameVerifier = javax.net.ssl.HostnameVerifier { _, _ -> true }   // the certificate is checked by its fingerprint
            connectTimeout = 20000; readTimeout = 30000; instanceFollowRedirects = false
            setRequestProperty("User-Agent", "Mozilla/5.0 SV1")
            if (cookie.isNotEmpty()) setRequestProperty("Cookie", "SVPNCOOKIE=$cookie")
            if (body != null) { requestMethod = "POST"; doOutput = true; setRequestProperty("Content-Type", "application/x-www-form-urlencoded"); outputStream.use { it.write(body.toByteArray()) } }
        }
        val code = h.responseCode
        val text = try { (if (code < 400) h.inputStream else h.errorStream)?.bufferedReader()?.readText() ?: "" } catch (_: Exception) { "" }
        val headers = h.headerFields.filterKeys { it != null }
        h.disconnect()
        return text to headers
    }

    // ------------------------------------------------------------------ the connection

    private fun connect(hostPort: String, user: String, password: String) {
        running = true
        try {
            host = hostPort.substringBefore(':'); port = hostPort.substringAfter(':', "443").toIntOrNull() ?: 443
            say("connecting", "⏳ Signing in to the office firewall…")
            try { https("/remote/login?lang=en", null) } catch (e: Exception) { if (e.toString().contains("PIN_")) throw e }
            val (ret, hdr) = https("/remote/logincheck", "ajax=1&username=" + URLEncoder.encode(user, "UTF-8") + "&realm=&credential=" + URLEncoder.encode(password, "UTF-8"))
            cookie = hdr.entries.filter { it.key.equals("Set-Cookie", true) }.flatMap { it.value }
                .firstOrNull { it.startsWith("SVPNCOOKIE=") && it.length > 12 && !it.startsWith("SVPNCOOKIE=;") }?.substringAfter("SVPNCOOKIE=")?.substringBefore(';') ?: ""
            if (cookie.isEmpty()) throw Exception(if (ret.contains("ret=0")) "the firewall refused the username or password" else "the firewall did not open a session (${ret.take(80)})")
            say("connecting", "⏳ Reading the office network settings…")
            val (xml, _) = https("/remote/fortisslvpn_xml", null)
            val assigned = Regex("assigned-addr[^>]*ipv4='([0-9.]+)'").find(xml)?.groupValues?.get(1) ?: ""
            val routes = Regex("<addr ip='([0-9.]+)' mask='([0-9.]+)'").findAll(xml).map { it.groupValues[1] to it.groupValues[2] }.toList()

            say("connecting", "⏳ Opening the tunnel…")
            val raw = Socket(); protect(raw)                       // the tunnel itself never goes into the tunnel
            raw.connect(InetSocketAddress(host, port), 20000)
            val s = ssl.createSocket(raw, host, port, true) as SSLSocket
            s.startHandshake()
            tls = s
            val out = s.outputStream
            out.write("GET /remote/sslvpn-tunnel HTTP/1.1\r\nHost: $host:$port\r\nUser-Agent: Mozilla/5.0 SV1\r\nCookie: SVPNCOOKIE=$cookie\r\n\r\n".toByteArray()); out.flush()
            val inp = DataInputStream(s.inputStream.buffered())

            val ppp = Ppp(inp, out)
            val myIp = ppp.negotiate(assigned) ?: throw Exception("the firewall did not give an address")
            ip = myIp

            val b = Builder().setSession("Nova · MZA office").addAddress(myIp, 32).setMtu(1354).setBlocking(true)
            try { b.addAllowedApplication(packageName) } catch (_: Exception) { }   // 🔒 only Nova uses this VPN
            val probe = ServerAccess.serverHost(applicationContext)
            var any = false
            for ((r, m) in routes) {
                val bits = m.split('.').sumOf { Integer.bitCount(it.toIntOrNull() ?: 0) }
                if (bits in 8..32 && !r.startsWith("0.")) try { b.addRoute(r, bits); any = true } catch (_: Exception) { }
            }
            if (!any && probe.isNotEmpty()) b.addRoute(probe.substringBeforeLast('.') + ".0", 24)
            tun = b.establish() ?: throw Exception("Android did not allow the VPN (allow Nova in the VPN request)")
            say("up", "🟢 Connected to the office · $myIp · Nova only")
            ServerAccess.hold(applicationContext, 20)       // 📱 → 💻 the PC waits 20 min (renewed while connected)
            pump(ppp)
        } catch (e: Exception) {
            val m = e.toString()
            val why = when {
                m.contains("PIN_UNKNOWN") -> "the PC has not sent the firewall's fingerprint yet — update the phone copy, or confirm it below"
                m.contains("PIN_MISMATCH") -> "⚠ the firewall's certificate is NOT the one your PC trusts — not connected"
                else -> e.message ?: m
            }
            if (running) { shutdown("⚠ $why"); state = "error"; onChange?.invoke() }
        }
    }

    /** The tunnel: IP packets from Nova → PPP frames to the firewall, and back. LCP keep-alives are answered. */
    private fun pump(ppp: Ppp) {
        val fd = tun ?: return
        val tin = FileInputStream(fd.fileDescriptor); val tout = FileOutputStream(fd.fileDescriptor)
        Thread {
            val buf = ByteArray(32767)
            try {
                while (running) {
                    val n = tin.read(buf); if (n <= 0) continue
                    if ((buf[0].toInt() shr 4) == 4) ppp.send(0x0021, buf.copyOf(n))
                }
            } catch (_: Exception) { }
        }.start()
        Thread {
            var last = System.currentTimeMillis()
            while (running) { Thread.sleep(60_000); if (running && System.currentTimeMillis() - last > 9 * 60_000) { ServerAccess.hold(applicationContext, 20); last = System.currentTimeMillis() } }
        }.start()
        try {
            while (running) {
                val (proto, data) = ppp.read()
                when (proto) {
                    0x0021 -> tout.write(data)
                    0xC021 -> if (!ppp.lcp(data)) { shutdown("🔴 The office closed the VPN session (signed in elsewhere?)"); return }
                    0x8021 -> ppp.ipcp(data)
                    else -> ppp.reject(proto, data)
                }
            }
        } catch (e: Exception) { if (running) shutdown("🔴 The VPN dropped (${e.message ?: "connection lost"}) — tap Connect") }
    }

    /** 🔗 Minimal PPP over Fortinet's TLS tunnel: each frame = 6-byte header (total, 0x5050, length) + protocol + data. */
    private inner class Ppp(private val inp: DataInputStream, private val out: OutputStream) {
        private val magic = SecureRandom().nextInt()
        private var id = 1

        fun send(proto: Int, data: ByteArray) {
            val len = data.size + 2
            val b = ByteArray(6 + len)
            val total = len + 6
            b[0] = (total shr 8).toByte(); b[1] = total.toByte(); b[2] = 0x50; b[3] = 0x50; b[4] = (len shr 8).toByte(); b[5] = len.toByte()
            b[6] = (proto shr 8).toByte(); b[7] = proto.toByte()
            System.arraycopy(data, 0, b, 8, data.size)
            synchronized(out) { out.write(b); out.flush() }
        }

        fun read(): Pair<Int, ByteArray> {
            val h = ByteArray(6); inp.readFully(h)
            if (h[2] != 0x50.toByte() || h[3] != 0x50.toByte()) throw Exception("unexpected data from the firewall")
            val len = ((h[4].toInt() and 0xFF) shl 8) or (h[5].toInt() and 0xFF)
            val d = ByteArray(len); inp.readFully(d)
            var o = 0
            if (len >= 2 && d[0] == 0xFF.toByte() && d[1] == 0x03.toByte()) o = 2
            val proto = ((d[o].toInt() and 0xFF) shl 8) or (d[o + 1].toInt() and 0xFF)
            return proto to d.copyOfRange(o + 2, len)
        }

        private fun pkt(code: Int, ident: Int, body: ByteArray): ByteArray {
            val l = body.size + 4
            return byteArrayOf(code.toByte(), ident.toByte(), (l shr 8).toByte(), l.toByte()) + body
        }
        private fun opt(type: Int, v: ByteArray) = byteArrayOf(type.toByte(), (v.size + 2).toByte()) + v
        private fun int4(v: Int) = byteArrayOf((v shr 24).toByte(), (v shr 16).toByte(), (v shr 8).toByte(), v.toByte())

        var lcpMine = false; var lcpTheirs = false; var ipMine = false; var ipTheirs = false
        var myIp = byteArrayOf(0, 0, 0, 0)
        private var lcpOpts = opt(1, byteArrayOf((1354 shr 8).toByte(), 1354.toByte())) + opt(5, int4(magic))

        /** LCP: their request → Ack; our request acked; echo → reply; terminate → false. */
        fun lcp(d: ByteArray): Boolean {
            if (d.size < 4) return true
            val code = d[0].toInt() and 0xFF; val ident = d[1].toInt() and 0xFF
            val len = (((d[2].toInt() and 0xFF) shl 8) or (d[3].toInt() and 0xFF)).coerceAtMost(d.size)
            val body = d.copyOfRange(4, len)
            when (code) {
                1 -> { send(0xC021, pkt(2, ident, body)); lcpTheirs = true }                      // Configure-Request → Ack
                2 -> lcpMine = true                                                                 // our request acked
                3 -> { lcpOpts = body; send(0xC021, pkt(1, ++id, lcpOpts)) }                       // Nak → their values
                4 -> { lcpOpts = opt(5, int4(magic)); send(0xC021, pkt(1, ++id, lcpOpts)) }        // Reject → magic only
                5 -> { send(0xC021, pkt(6, ident, ByteArray(0))); return false }                   // Terminate-Request
                9 -> send(0xC021, pkt(10, ident, int4(magic) + (if (body.size > 4) body.copyOfRange(4, body.size) else ByteArray(0))))   // Echo
            }
            return true
        }

        fun ipcp(d: ByteArray) {
            if (d.size < 4) return
            val code = d[0].toInt() and 0xFF; val ident = d[1].toInt() and 0xFF
            val len = (((d[2].toInt() and 0xFF) shl 8) or (d[3].toInt() and 0xFF)).coerceAtMost(d.size)
            val body = d.copyOfRange(4, len)
            when (code) {
                1 -> { send(0x8021, pkt(2, ident, body)); ipTheirs = true }
                2 -> ipMine = true
                3 -> {   // Nak: the firewall tells our address
                    var i = 0
                    while (i + 2 <= body.size) { val t = body[i].toInt() and 0xFF; val l = body[i + 1].toInt() and 0xFF; if (l < 2) break; if (t == 3 && l == 6) myIp = body.copyOfRange(i + 2, i + 6); i += l }
                    send(0x8021, pkt(1, ++id, opt(3, myIp)))
                }
                4 -> send(0x8021, pkt(1, ++id, opt(3, myIp)))
            }
        }

        fun reject(proto: Int, d: ByteArray) {
            val b = byteArrayOf((proto shr 8).toByte(), proto.toByte()) + d.copyOf(minOf(d.size, 1300))
            send(0xC021, pkt(8, ++id, b))
        }

        /** Until both sides agreed on LCP and IPCP (address); returns our address. */
        fun negotiate(assigned: String): String? {
            if (assigned.isNotEmpty()) myIp = assigned.split('.').map { it.toInt().toByte() }.toByteArray()
            val end = System.currentTimeMillis() + 25_000
            var lastSend = 0L
            raw().soTimeout = 3000
            while (System.currentTimeMillis() < end) {
                if (System.currentTimeMillis() - lastSend > 3000) {
                    if (!lcpMine) send(0xC021, pkt(1, ++id, lcpOpts))
                    else if (!ipMine) send(0x8021, pkt(1, ++id, opt(3, myIp)))
                    lastSend = System.currentTimeMillis()
                }
                val f = try { read() } catch (_: java.net.SocketTimeoutException) { continue }
                when (f.first) {
                    0xC021 -> if (!lcp(f.second)) return null
                    0x8021 -> ipcp(f.second)
                    0x0021 -> { }
                    else -> reject(f.first, f.second)
                }
                if (lcpMine && lcpTheirs && !ipMine && lastSend != 0L && f.first == 0xC021) { send(0x8021, pkt(1, ++id, opt(3, myIp))); lastSend = System.currentTimeMillis() }
                if (lcpMine && ipMine && ipTheirs && myIp.any { it != 0.toByte() }) { raw().soTimeout = 0; return myIp.joinToString(".") { (it.toInt() and 0xFF).toString() } }
            }
            return null
        }
        private fun raw() = tls!!
    }
}
