package com.nova.enhance

import android.content.ContentValues
import android.content.Context
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper

data class Conv(
    val id: Long, val project: String, val provider: String, val mode: String,
    val title: String, val route: String, val created: Long, val updated: Long
)

/**
 * Every conversation is saved here, inside the project "Enhancement", one thread per AI.
 * route = "api" (answered through the API key) or "account" (sent into the signed-in account).
 */
class Db private constructor(c: Context) : SQLiteOpenHelper(c, "nova_enhance.db", null, 1) {

    companion object {
        const val PROJECT = "Enhancement"
        @Volatile private var inst: Db? = null
        fun get(c: Context): Db = inst ?: synchronized(this) {
            inst ?: Db(c.applicationContext).also { inst = it }
        }
    }

    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL("CREATE TABLE projects(name TEXT PRIMARY KEY, created INTEGER)")
        db.execSQL("""CREATE TABLE conversations(
            id INTEGER PRIMARY KEY AUTOINCREMENT, project TEXT, provider TEXT, mode TEXT,
            title TEXT, route TEXT, created INTEGER, updated INTEGER)""")
        db.execSQL("""CREATE TABLE messages(
            id INTEGER PRIMARY KEY AUTOINCREMENT, conv INTEGER, role TEXT, content TEXT, ts INTEGER)""")
        db.execSQL("CREATE INDEX msg_conv ON messages(conv)")
        db.execSQL("INSERT INTO projects VALUES('$PROJECT', ${System.currentTimeMillis()})")
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {}

    fun newConv(provider: String, mode: String, title: String, route: String): Long {
        val now = System.currentTimeMillis()
        return writableDatabase.insert("conversations", null, ContentValues().apply {
            put("project", PROJECT); put("provider", provider); put("mode", mode)
            put("title", title.replace('\n', ' ').take(80)); put("route", route)
            put("created", now); put("updated", now)
        })
    }

    fun addMsg(conv: Long, role: String, content: String) {
        val now = System.currentTimeMillis()
        writableDatabase.insert("messages", null, ContentValues().apply {
            put("conv", conv); put("role", role); put("content", content); put("ts", now)
        })
        writableDatabase.execSQL("UPDATE conversations SET updated=? WHERE id=?", arrayOf<Any>(now, conv))
    }

    fun messages(conv: Long): List<Msg> {
        val out = mutableListOf<Msg>()
        readableDatabase.rawQuery("SELECT role, content FROM messages WHERE conv=? ORDER BY id", arrayOf(conv.toString()))
            .use { c -> while (c.moveToNext()) out += Msg(c.getString(0), c.getString(1)) }
        return out
    }

    fun conv(id: Long): Conv? = convs("WHERE id=?", arrayOf(id.toString())).firstOrNull()

    fun list(provider: String?, search: String): List<Conv> {
        val where = mutableListOf("project=?"); val args = mutableListOf(PROJECT)
        if (provider != null) { where += "provider=?"; args += provider }
        if (search.isNotBlank()) {
            where += "(title LIKE ? OR id IN (SELECT conv FROM messages WHERE content LIKE ?))"
            args += "%$search%"; args += "%$search%"
        }
        return convs("WHERE " + where.joinToString(" AND ") + " ORDER BY updated DESC", args.toTypedArray())
    }

    private fun convs(tail: String, args: Array<String>): List<Conv> {
        val out = mutableListOf<Conv>()
        readableDatabase.rawQuery(
            "SELECT id, project, provider, mode, title, route, created, updated FROM conversations $tail", args
        ).use { c ->
            while (c.moveToNext()) out += Conv(c.getLong(0), c.getString(1), c.getString(2), c.getString(3),
                c.getString(4), c.getString(5), c.getLong(6), c.getLong(7))
        }
        return out
    }

    fun delete(id: Long) {
        writableDatabase.delete("messages", "conv=?", arrayOf(id.toString()))
        writableDatabase.delete("conversations", "id=?", arrayOf(id.toString()))
    }

    /** Every conversation with its messages, for the backup file. */
    fun exportJson(): org.json.JSONArray {
        val arr = org.json.JSONArray()
        for (c in list(null, "").reversed()) {
            val msgs = org.json.JSONArray()
            readableDatabase.rawQuery("SELECT role, content, ts FROM messages WHERE conv=? ORDER BY id", arrayOf(c.id.toString()))
                .use { q -> while (q.moveToNext()) msgs.put(org.json.JSONObject()
                    .put("role", q.getString(0)).put("content", q.getString(1)).put("ts", q.getLong(2))) }
            arr.put(org.json.JSONObject().put("provider", c.provider).put("mode", c.mode).put("title", c.title)
                .put("route", c.route).put("created", c.created).put("updated", c.updated).put("messages", msgs))
        }
        return arr
    }

    /** Adds conversations from a backup; ones already on the phone are skipped. Returns how many were added. */
    fun importJson(arr: org.json.JSONArray): Int {
        var added = 0
        val db = writableDatabase
        db.beginTransaction()
        try {
            for (i in 0 until arr.length()) {
                val o = arr.getJSONObject(i)
                val exists = db.rawQuery("SELECT 1 FROM conversations WHERE provider=? AND created=? AND title=?",
                    arrayOf(o.optString("provider"), o.optLong("created").toString(), o.optString("title"))).use { it.moveToFirst() }
                if (exists) continue
                val id = db.insert("conversations", null, ContentValues().apply {
                    put("project", PROJECT); put("provider", o.optString("provider")); put("mode", o.optString("mode"))
                    put("title", o.optString("title")); put("route", o.optString("route", "api"))
                    put("created", o.optLong("created")); put("updated", o.optLong("updated"))
                })
                val msgs = o.optJSONArray("messages") ?: org.json.JSONArray()
                for (j in 0 until msgs.length()) {
                    val m = msgs.getJSONObject(j)
                    db.insert("messages", null, ContentValues().apply {
                        put("conv", id); put("role", m.optString("role")); put("content", m.optString("content"))
                        put("ts", m.optLong("ts"))
                    })
                }
                added++
            }
            db.setTransactionSuccessful()
        } finally { db.endTransaction() }
        return added
    }

    /** Whole project as Markdown, for export/backup. */
    fun exportMarkdown(): String {
        val fmt = java.text.SimpleDateFormat("yyyy-MM-dd HH:mm", java.util.Locale.US)
        val sb = StringBuilder("# Nova Enhance — project \"$PROJECT\"\n\nExported ${fmt.format(java.util.Date())}\n")
        for (p in Providers.all) {
            val cs = list(p.id, "")
            if (cs.isEmpty()) continue
            sb.append("\n\n## ${p.label} / $PROJECT\n")
            for (c in cs.reversed()) {
                sb.append("\n### ${c.title}\n_${fmt.format(java.util.Date(c.created))} · ${Modes.byId(c.mode).label}" +
                    (if (c.route == "account") " · sent to my ${p.label} account" else "") + "_\n\n")
                for (m in messages(c.id)) {
                    sb.append(if (m.role == "user") "**Me:**\n\n" else "**${p.label}:**\n\n")
                    sb.append(m.content).append("\n\n")
                }
                sb.append("---\n")
            }
        }
        return sb.toString()
    }
}
