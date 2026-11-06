/* Nova Enhance Office panel — plain ES5 so it runs in every Office version. Designed by A.HAMROUNI */
(function () {
  var host = "doc", lastOriginal = "", lastWasWhole = false, originals = [], origIdx = 0;

  function $(id) { return document.getElementById(id); }
  function status(t, kind) { var s = $("status"); s.innerHTML = ""; s.appendChild(document.createTextNode(t)); s.className = "status" + (kind ? " " + kind : ""); }
  function show(id, on) { var e = $(id); if (e) e.className = e.className.replace(/\s*hide/g, "") + (on ? "" : " hide"); }

  function api(path, body, cb) {
    var x = new XMLHttpRequest();
    x.open(body ? "POST" : "GET", "/api/" + path, true);
    x.setRequestHeader("X-Nova", "1");
    if (body) x.setRequestHeader("Content-Type", "application/json");
    x.onreadystatechange = function () {
      if (x.readyState !== 4) return;
      var r = null;
      try { r = JSON.parse(x.responseText); } catch (e) { r = { error: "Nova Enhance is not answering — is it running? (" + x.status + ")" }; }
      if (x.status === 0) r = { error: "Nova Enhance is not running on this PC — start it from the Start menu." };
      cb(r || {});
    };
    x.send(body ? JSON.stringify(body) : null);
  }

  function each(sel, fn) { var l = document.querySelectorAll(sel); for (var i = 0; i < l.length; i++) fn(l[i]); }

  // ---------------------------------------------------------------- reading and writing the selection
  function getSelection(cb) {
    if (host === "compose") {
      Office.context.mailbox.item.getSelectedDataAsync(Office.CoercionType.Text, function (r) {
        var t = r.status === Office.AsyncResultStatus.Succeeded && r.value ? (r.value.data || "") : "";
        if (t.replace(/\s/g, "").length > 0) { cb(t, false); return; }
        Office.context.mailbox.item.body.getAsync(Office.CoercionType.Text, function (b) { cb(b.status === Office.AsyncResultStatus.Succeeded ? b.value : "", true); });
      });
      return;
    }
    Office.context.document.getSelectedDataAsync(Office.CoercionType.Text, function (r) {
      cb(r.status === Office.AsyncResultStatus.Succeeded ? (r.value || "") : "", false);
    });
  }

  function putText(text, whole, insert, cb) {
    var done = function (r) { cb(r.status === Office.AsyncResultStatus.Succeeded, r.error ? r.error.message : ""); };
    if (host === "compose") {
      if (whole && !insert) Office.context.mailbox.item.body.setAsync(text, { coercionType: Office.CoercionType.Text }, done);
      else Office.context.mailbox.item.body.setSelectedDataAsync(text, { coercionType: Office.CoercionType.Text }, done);
      return;
    }
    if (insert) text = (lastOriginal || "") + "\n" + text;
    Office.context.document.setSelectedDataAsync(text, { coercionType: Office.CoercionType.Text }, done);
  }

  // ---------------------------------------------------------------- enhance
  function enhance(mode) {
    getSelection(function (text, whole) {
      if (!text || text.replace(/\s/g, "").length === 0) { status("Select some text first", "bad"); return; }
      status("✦ Working…");
      each("button", function (b) { b.disabled = true; });
      api("enhance", { mode: mode, text: text, lang: $("lang").value, host: host }, function (r) {
        each("button", function (b) { b.disabled = false; });
        if (r.error) { status("⚠ " + r.error, "bad"); return; }
        lastOriginal = text; lastWasWhole = whole;
        $("result").value = r.result;
        $("outlabel").innerHTML = ""; $("outlabel").appendChild(document.createTextNode(r.label + " · " + r.ai));
        show("out", true);
        setOrig(text);
        status("✓ Done — check it, then ⇄ Replace. The original is saved.", "good");
      });
    });
  }

  function setOrig(t) { var o = $("orig"); o.innerHTML = ""; o.appendChild(document.createTextNode(t)); }

  function mailInfo(cb) {
    var it = Office.context.mailbox.item;
    it.body.getAsync(Office.CoercionType.Text, function (b) {
      var from = it.from ? (it.from.displayName || it.from.emailAddress) : "";
      cb({ text: b.status === Office.AsyncResultStatus.Succeeded ? b.value : "", subject: it.subject || "", from: from });
    });
  }

  function init() {
    var mb = Office.context && Office.context.mailbox;
    if (mb && mb.item) host = mb.item.itemType === Office.MailboxEnums.ItemType.Message && typeof mb.item.subject === "string" ? "read" : "compose";
    if (mb && mb.item && typeof mb.item.subject !== "string") host = "compose";
    show("doc", host === "doc"); show("mailread", host === "read"); show("mailcompose", host === "compose");
    if (host === "read") { $("insert").style.display = "none"; $("replace").innerHTML = "↩ Reply with this"; }

    api("ping", null, function (r) {
      if (r.error) { status("⚠ " + r.error, "bad"); return; }
      $("ai").appendChild(document.createTextNode(r.ai || "no AI key"));
      $("sign").innerHTML = ""; $("sign").appendChild(document.createTextNode(r.sign + " · build " + r.build));
      status(r.ai ? "Ready — select text and choose a mode." : "Add an AI key in Nova Enhance → Settings.", r.ai ? "" : "bad");
    });
    api("langs", null, function (r) {
      var sel = $("lang");
      if (!r.langs) return;
      for (var i = 0; i < r.langs.length; i++) { var o = document.createElement("option"); o.value = r.langs[i].v; o.text = r.langs[i].l; if (r.langs[i].v === r.current) o.selected = true; sel.appendChild(o); }
      var setTr = function () { $("tr").innerHTML = ""; $("tr").appendChild(document.createTextNode("🌐 → " + sel.options[sel.selectedIndex].text)); };
      sel.onchange = setTr; setTr();
    });
    api("originals", null, function (r) { originals = r.originals || []; if (originals.length) setOrig(originals[0].text); });

    each("button[data-mode]", function (b) { b.onclick = function () { enhance(b.getAttribute("data-mode")); }; });

    $("replace").onclick = function () {
      var t = $("result").value;
      if (host === "read") { Office.context.mailbox.item.displayReplyForm(t.replace(/\n/g, "<br>")); return; }
      putText(t, lastWasWhole, false, function (ok, err) { status(ok ? "✓ Replaced — Ctrl + Z in Office to undo" : "⚠ " + err, ok ? "good" : "bad"); });
    };
    $("insert").onclick = function () { putText($("result").value, false, true, function (ok, err) { status(ok ? "✓ Inserted" : "⚠ " + err, ok ? "good" : "bad"); }); };
    $("copy").onclick = function () { var t = $("result"); t.select(); try { document.execCommand("copy"); status("⧉ Copied", "good"); } catch (e) { status("Press Ctrl + C to copy"); } };
    $("putback").onclick = function () {
      var t = lastOriginal || (originals[origIdx] ? originals[origIdx].text : "");
      if (!t) { status("No original yet"); return; }
      if (host === "read") { $("result").value = t; show("out", true); return; }
      putText(t, lastWasWhole, false, function (ok, err) { status(ok ? "↩ Original put back" : "⚠ " + err, ok ? "good" : "bad"); });
    };
    $("older").onclick = function () {
      api("originals", null, function (r) {
        originals = r.originals || [];
        if (!originals.length) { status("No original saved yet"); return; }
        origIdx = (origIdx + 1) % originals.length; lastOriginal = originals[origIdx].text;
        setOrig(originals[origIdx].when + " · " + originals[origIdx].mode + "\n" + originals[origIdx].text);
      });
    };
    $("settings").onclick = function () { api("open", { what: "settings" }, function () { }); return false; };

    if (host === "read") {
      $("sum").onclick = function () { status("✦ Summarising…"); mailInfo(function (m) { api("summarize", m, function (r) { if (r.error) { status("⚠ " + r.error, "bad"); return; } $("result").value = r.result; $("outlabel").innerHTML = "Summary"; show("out", true); $("replace").style.display = "none"; status("✓ Summary ready", "good"); }); }); };
      $("draft").onclick = function () { status("✦ Writing the reply…"); mailInfo(function (m) { m.instruction = $("instr").value; api("draft", m, function (r) { if (r.error) { status("⚠ " + r.error, "bad"); return; } $("result").value = r.result; $("outlabel").innerHTML = "Draft reply — check it"; show("out", true); $("replace").style.display = ""; status("✓ Draft ready — ↩ Reply with this opens the reply", "good"); }); }); };
      $("trmail").onclick = function () { status("🌐 Translating…"); mailInfo(function (m) { api("enhance", { mode: "translate", text: m.text, lang: $("lang") ? $("lang").value : "", host: "read" }, function (r) { if (r.error) { status("⚠ " + r.error, "bad"); return; } $("result").value = r.result; $("outlabel").innerHTML = ""; $("outlabel").appendChild(document.createTextNode(r.label)); show("out", true); $("replace").style.display = "none"; status("✓ Translated", "good"); }); }); };
      $("asknova").onclick = function () { api("open", { what: "ask" }, function () { status("🔍 Ask Nova opened on your desktop"); }); };
    }
  }

  if (window.Office) {
    if (Office.onReady) Office.onReady(function () { init(); });
    else Office.initialize = function () { init(); };
  }
})();
