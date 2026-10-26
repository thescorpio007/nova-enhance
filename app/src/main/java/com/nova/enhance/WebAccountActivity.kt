package com.nova.enhance

import android.annotation.SuppressLint
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.os.Bundle
import android.view.View
import android.webkit.CookieManager
import android.webkit.JavascriptInterface
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.LinearLayout
import android.widget.TextView
import org.json.JSONObject

/**
 * Your real AI account, signed in inside the app.
 *  setup mode: sign in, create/open the "Enhancement" project, tap "Use this page".
 *  send mode:  opens the Enhancement page and types + sends the text for you.
 */
class WebAccountActivity : BaseActivity() {
    companion object {
        fun setup(c: Context, p: Provider) =
            c.startActivity(Intent(c, WebAccountActivity::class.java).putExtra("p", p.id).putExtra("setup", true))

        fun send(c: Context, p: Provider, text: String) {
            val i = Intent(c, WebAccountActivity::class.java).putExtra("p", p.id).putExtra("text", text)
            if (c !is Activity) i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            c.startActivity(i)
        }
    }

    private lateinit var web: WebView
    private lateinit var p: Provider
    private lateinit var store: Store
    private lateinit var note: TextView
    private var pending: String? = null
    private var sent = false

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val c = this
        store = Store(c)
        p = Providers.byId(intent.getStringExtra("p")) ?: run { finish(); return }
        pending = intent.getStringExtra("text")?.takeIf { it.isNotBlank() }
        val setupMode = intent.getBooleanExtra("setup", false)

        val root = Ui.col(c)
        val bar = Ui.row(c).apply { setPadding(Ui.dp(c, 12), Ui.dp(c, 8), Ui.dp(c, 12), Ui.dp(c, 8)) }
        bar.addView(Ui.text(c, "My ${p.label}", 16f, Ui.TEXT, true), Ui.lp(0, weight = 1f))
        bar.addView(Ui.button(c, "Use this page") {
            val u = web.url ?: return@button
            store.setWebProject(p, u)
            Ui.toast(c, "Saved as your ${Db.PROJECT} page")
            note.text = "✓ Saved. Texts sent to \"My account\" will open here."
            inject()
        }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT, end = Ui.dp(c, 8)))
        bar.addView(Ui.button(c, "Done", true) { finish() }, Ui.lp(LinearLayout.LayoutParams.WRAP_CONTENT))
        root.addView(bar)

        note = Ui.text(c, "", 13f, Ui.MUTED).apply { setPadding(Ui.dp(c, 14), 0, Ui.dp(c, 14), Ui.dp(c, 8)) }
        root.addView(note)

        web = WebView(c)
        root.addView(web, Ui.lp(h = 0, weight = 1f))
        setContentView(root)

        CookieManager.getInstance().setAcceptCookie(true)
        CookieManager.getInstance().setAcceptThirdPartyCookies(web, true)
        web.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            databaseEnabled = true
            loadWithOverviewMode = true
            useWideViewPort = true
            javaScriptCanOpenWindowsAutomatically = true
            setSupportMultipleWindows(false)
            mixedContentMode = WebSettings.MIXED_CONTENT_COMPATIBILITY_MODE
            // Look like normal Chrome so sign-in pages accept it.
            userAgentString = userAgentString.replace("; wv", "").replace(Regex("Version/\\d+\\.\\d+ "), "")
        }
        web.addJavascriptInterface(Bridge(), "NovaBridge")
        web.webChromeClient = WebChromeClient()
        web.webViewClient = object : WebViewClient() {
            override fun shouldOverrideUrlLoading(view: WebView, req: WebResourceRequest): Boolean {
                val s = req.url.scheme ?: return false
                if (s == "http" || s == "https") return false
                return try { startActivity(Intent(Intent.ACTION_VIEW, req.url)); true } catch (_: Exception) { true }
            }
            override fun onPageStarted(view: WebView, url: String?, favicon: Bitmap?) {}
            override fun onPageFinished(view: WebView, url: String?) {
                CookieManager.getInstance().flush()
                inject()
            }
        }

        val project = store.webProject(p)
        if (setupMode || project.isBlank()) {
            Ui.copy(c, Db.PROJECT)
            note.text = when (p.id) {
                "deepseek" -> "1. Sign in.  2. Tap \"Use this page\" (DeepSeek has no projects)."
                "gemini" -> "1. Sign in.  2. Optional: make a Gem named \"${Db.PROJECT}\" (name copied) and open it.  3. Tap \"Use this page\"."
                else -> "1. Sign in.  2. Create a project named \"${Db.PROJECT}\" (name copied — just paste) and open it.  3. Tap \"Use this page\"."
            } + if (pending != null) "\nYour text is ready and will be sent once the page is set." else ""
        } else {
            note.visibility = View.GONE
        }
        if (pending != null) Ui.copy(c, pending!!)
        web.loadUrl(project.ifBlank { p.webUrl })
    }

    /** Types the text into the AI's message box and presses Send. */
    private fun inject() {
        val t = pending ?: return
        if (sent) return
        if (store.webProject(p).isBlank()) return
        val js = """
(function(t){
  if (window.__novaSent) return;
  var tries = 0;
  function visible(e){ return e && (e.offsetParent !== null || e.getClientRects().length); }
  function box(){
    var sels = ['#prompt-textarea','div.ProseMirror[contenteditable="true"]','rich-textarea div[contenteditable="true"]',
      'div.ql-editor[contenteditable="true"]','textarea#chat-input','textarea','div[contenteditable="true"]'];
    for (var i=0;i<sels.length;i++){ var l=document.querySelectorAll(sels[i]);
      for (var j=0;j<l.length;j++){ if (visible(l[j])) return l[j]; } }
    return null;
  }
  function sendBtn(){
    var sels = ['button[data-testid="send-button"]','#composer-submit-button','button[aria-label="Send message"]',
      'button[aria-label*="Send" i]','button[aria-label*="Submit" i]','button[type="submit"]'];
    for (var i=0;i<sels.length;i++){ var b=document.querySelector(sels[i]);
      if (b && !b.disabled && b.getAttribute('aria-disabled')!=='true' && visible(b)) return b; }
    return null;
  }
  var iv = setInterval(function(){
    tries++;
    var e = box();
    if (e){
      clearInterval(iv); window.__novaSent = true;
      e.focus();
      if (e.tagName === 'TEXTAREA'){
        var set = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set;
        set.call(e, t); e.dispatchEvent(new Event('input',{bubbles:true}));
      } else {
        document.execCommand('selectAll', false, null);
        document.execCommand('insertText', false, t);
      }
      var n = 0;
      var iv2 = setInterval(function(){
        n++; var b = sendBtn();
        if (b){ clearInterval(iv2); b.click(); NovaBridge.done('sent'); }
        else if (n > 25){ clearInterval(iv2);
          e.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true}));
          NovaBridge.done('enter'); }
      }, 400);
    } else if (tries > 60){ clearInterval(iv); NovaBridge.done('nobox'); }
  }, 500);
})(${JSONObject.quote(t)});
"""
        web.evaluateJavascript(js, null)
    }

    inner class Bridge {
        @JavascriptInterface
        fun done(status: String) {
            runOnUiThread {
                sent = true
                when (status) {
                    "sent" -> Ui.toast(this@WebAccountActivity, "Sent to your ${p.label} ${Db.PROJECT}")
                    "enter" -> Ui.toast(this@WebAccountActivity, "Text typed — tap Send if it hasn't gone")
                    else -> {
                        note.visibility = View.VISIBLE
                        note.text = "Couldn't find the message box. Your text is copied: tap the box, paste and send."
                    }
                }
            }
        }
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        if (web.canGoBack()) web.goBack() else super.onBackPressed()
    }

    override fun onPause() { super.onPause(); CookieManager.getInstance().flush() }

    override fun onDestroy() {
        if (::web.isInitialized) web.destroy()
        super.onDestroy()
    }
}
