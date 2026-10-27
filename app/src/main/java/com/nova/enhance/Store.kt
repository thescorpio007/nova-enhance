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


    fun hasKey(p: Provider) = key(p).isNotBlank()
    fun keyed(): List<Provider> = Providers.all.filter { hasKey(it) }

    /** Selected AI on the enhance screen: a provider id, or "all". */
    var target: String
        get() = sp.getString("target", "gemini")!!
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

    /** "About me" profile: fields (joined by |), own description, terms to keep as written. */
    var profileFields: String
        get() = sp.getString("p_fields", "")!!
        set(v) = sp.edit().putString("p_fields", v).apply()
    var profileAbout: String
        get() = sp.getString("p_about", "")!!
        set(v) = sp.edit().putString("p_about", v).apply()
    var profileTerms: String
        get() = sp.getString("p_terms", "")!!
        set(v) = sp.edit().putString("p_terms", v).apply()
    var profileAsked: Boolean
        get() = sp.getBoolean("p_asked", false)
        set(v) = sp.edit().putBoolean("p_asked", v).apply()

    /** Answer style in Ask AI mode. */
    var askStyle: String
        get() = sp.getString("ask_style", "advise")!!
        set(v) = sp.edit().putString("ask_style", v).apply()

    /** Stronger model for "Think deeper" ("" = not found yet, "-" = none available). */
    fun strongModel(p: Provider): String = sp.getString("strong_${p.id}", "")!!
    fun setStrongModel(p: Provider, v: String) = sp.edit().putString("strong_${p.id}", v).apply()

    var bubbleOn: Boolean
        get() = sp.getBoolean("bubble", false)
        set(v) = sp.edit().putBoolean("bubble", v).apply()

    var firstRunDone: Boolean
        get() = sp.getBoolean("first_run", false)
        set(v) = sp.edit().putBoolean("first_run", v).apply()
}
