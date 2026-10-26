package com.nova.enhance

import android.content.Context
import android.content.SharedPreferences

/** Settings kept privately on the phone (API keys never leave the device except to their own AI). */
class Store(c: Context) {
    private val sp: SharedPreferences = c.getSharedPreferences("nova_enhance", Context.MODE_PRIVATE)

    fun key(p: Provider): String = sp.getString("key_${p.id}", "")!!.trim()
    fun setKey(p: Provider, v: String) = sp.edit().putString("key_${p.id}", v.trim()).apply()

    fun model(p: Provider): String = sp.getString("model_${p.id}", "")!!.ifBlank { p.defaultModel }
    fun setModel(p: Provider, v: String) = sp.edit().putString("model_${p.id}", v.trim()).apply()

    /** Address of the "Enhancement" project page inside the user's signed-in AI account. */
    fun webProject(p: Provider): String = sp.getString("web_${p.id}", "")!!
    fun setWebProject(p: Provider, v: String) = sp.edit().putString("web_${p.id}", v).apply()

    fun hasKey(p: Provider) = key(p).isNotBlank()
    fun keyed(): List<Provider> = Providers.all.filter { hasKey(it) }

    /** Selected AI on the enhance screen: a provider id, or "all". */
    var target: String
        get() = sp.getString("target", "chatgpt")!!
        set(v) = sp.edit().putString("target", v).apply()

    var mode: String
        get() = sp.getString("mode", "grammar")!!
        set(v) = sp.edit().putString("mode", v).apply()

    var custom: String
        get() = sp.getString("custom", "")!!
        set(v) = sp.edit().putString("custom", v).apply()

    /** "api" = through API keys, "account" = into the signed-in account, "both". */
    var route: String
        get() = sp.getString("route2", "both")!!
        set(v) = sp.edit().putString("route2", v).apply()

    /** Speaking language for the microphone: "phone" (phone default), "en", "ar", "fr" or "de". */
    var voiceLang: String
        get() = sp.getString("voice2", "phone")!!
        set(v) = sp.edit().putString("voice2", v).apply()

    var bubbleOn: Boolean
        get() = sp.getBoolean("bubble", false)
        set(v) = sp.edit().putBoolean("bubble", v).apply()

    var firstRunDone: Boolean
        get() = sp.getBoolean("first_run", false)
        set(v) = sp.edit().putBoolean("first_run", v).apply()
}
