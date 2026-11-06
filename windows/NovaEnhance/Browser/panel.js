/* Nova Enhance browser add-on — the ✦ panel (side panel in Chrome/Edge, sidebar in Firefox). Designed by A.HAMROUNI */
(function () {
  var B = chrome;
  var lastOriginal = "", lastQ = "", lastMode = "";

  function $(id) { return document.getElementById(id); }
  function status(t, kind) { $("status").textContent = t; $("status").className = "status" + (kind ? " " + kind : ""); }
  function show(id, on) { $(id).className = $(id).className.replace(/\s*hide/g, "") + (on ? "" : " hide"); }
  function busy(on) { var b = document.querySelectorAll("button"); for (var i = 0; i < b.length; i++) b[i].disabled = on; }
  function bg(msg) { return new Promise(function (res) { B.runtime.sendMessage(msg, function (r) { void (B.runtime.lastError); res(r); }); }); }
  function api(path, body) { return bg({ t: "api", path: path, body: body }).then(function (r) { return r || { error: "Nova Enhance is not answering" }; }); }
  function tab(msg) { return bg({ t: "fromTab", msg: msg }); }

  function answer(text, label) {
    var box = $("answer"); box.innerHTML = "";
    var parts = String(text).split(/(\[[^\]\n]{1,40}\])/);
    for (var i = 0; i < parts.length; i++) {
      if (/^\[[^\]]+\]$/.test(parts[i])) { var c = document.createElement("span"); c.className = "cite"; c.textContent = parts[i]; box.appendChild(c); }
      else box.appendChild(document.createTextNode(parts[i]));
    }
    $("anslabel").textContent = label;
    show("ansbox", true);
  }

  function page(kind, detailed) {
    var q = $("q").value.trim();
    if (kind === "ask" && !q) { status("Type your question about this page", "bad"); $("q").focus(); return; }
    busy(true); status(kind === "ask" ? "🔍 Reading the page…" : "✦ Summarising the page…");
    tab({ t: "page" }).then(function (p) {
      if (!p || !p.text) { busy(false); status("⚠ This page can't be read (browser pages and some sites block add-ons)", "bad"); return; }
      lastQ = q; lastMode = kind;
      api("page", { mode: kind, question: q, url: p.url, title: p.title, text: p.text, detailed: !!detailed }).then(function (r) {
        busy(false);
        if (r.error) { status("⚠ " + r.error, "bad"); return; }
        answer(r.result, (kind === "ask" ? "Answer — from this page" : "Summary of this page") + " · " + r.ai);
        status("✓ Ready", "good");
      });
    });
  }

  function enhance(mode) {
    tab({ t: "selection" }).then(function (s) {
      var text = s && s.text ? s.text : "";
      if (!text.replace(/\s/g, "").length) { status("Select some text in the page first", "bad"); return; }
      busy(true); status(mode === "translate" ? "🌐 Translating…" : "✦ Working…");
      api("enhance", { mode: mode, text: text, lang: $("lang").value, host: "browser" }).then(function (r) {
        busy(false);
        if (r.error) { status("⚠ " + r.error, "bad"); return; }
        lastOriginal = text;
        $("result").value = r.result;
        $("outlabel").textContent = r.label + " · " + r.ai;
        $("orig").textContent = text;
        show("out", true);
        status("✓ Done — check it, then ⇄ Replace in page. The original is saved.", "good");
      });
    });
  }

  api("ping").then(function (r) {
    if (r.error) { status("⚠ " + r.error, "bad"); return; }
    $("ai").textContent = r.ai || "no AI key";
    $("sign").textContent = r.sign + " · build " + r.build;
    status(r.ai ? "Ready — select text, or ask about this page." : "Add an AI key in Nova Enhance → Settings.", r.ai ? "" : "bad");
  });
  api("langs").then(function (r) {
    var sel = $("lang");
    if (!r.langs) return;
    for (var i = 0; i < r.langs.length; i++) { var o = document.createElement("option"); o.value = r.langs[i].v; o.text = r.langs[i].l; if (r.langs[i].v === r.current) o.selected = true; sel.appendChild(o); }
    var setTr = function () { $("tr").textContent = "🌐 → " + sel.options[sel.selectedIndex].text; };
    sel.onchange = setTr; setTr();
  });
  api("originals").then(function (r) { if (r.originals && r.originals.length) $("orig").textContent = r.originals[0].text; });

  var mb = document.querySelectorAll("button[data-mode]");
  for (var i = 0; i < mb.length; i++) (function (b) { b.onclick = function () { enhance(b.getAttribute("data-mode")); }; })(mb[i]);
  $("sum").onclick = function () { page("summary"); };
  $("ask").onclick = function () { page("ask"); };
  $("q").onkeydown = function (e) { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); page("ask"); } };
  $("more").onclick = function () { $("q").value = lastQ; page(lastMode || "summary", true); };
  $("copyans").onclick = function () { navigator.clipboard.writeText($("answer").textContent).then(function () { status("⧉ Copied", "good"); }); };
  $("copy").onclick = function () { navigator.clipboard.writeText($("result").value).then(function () { status("⧉ Copied", "good"); }); };
  $("replace").onclick = function () {
    bg({ t: "replaceInTab", text: $("result").value, original: lastOriginal, label: $("outlabel").textContent, ai: "" }).then(function () { status("✓ Replaced in the page", "good"); });
  };
  $("putback").onclick = function () { tab({ t: "putback" }).then(function () { status("↩ Put back (if the page allows it)", "good"); }); };
})();
