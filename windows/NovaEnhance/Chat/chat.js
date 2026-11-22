/* Nova Chat — the page inside Nova's AI window. The work is done by the Nova app (C#); this page only shows it. Designed by A.HAMROUNI */
(function () {
  var W = window.chrome && window.chrome.webview;
  var chats = [], cur = null, ctx = newCtx(), detail = false, recording = false, busy = false;

  function $(id) { return document.getElementById(id); }
  function post(o) { if (W) W.postMessage(o); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  function newCtx() { return { about: true, folder: null, files: [], emails: [] }; }

  // ---------------------------------------------------------------- chats list
  function drawList() {
    var q = $("find").value.toLowerCase(), list = $("list");
    list.innerHTML = "";
    var now = Date.now(), groups = [["Today", 864e5], ["This week", 7 * 864e5], ["Older", Infinity]], g = -1;
    chats.filter(function (c) { return !q || c.title.toLowerCase().indexOf(q) >= 0; }).forEach(function (c) {
      var age = now - c.updated, k = 0;
      while (age > groups[k][1]) k++;
      if (k !== g) { list.appendChild(el("div", "grp", groups[k][0])); g = k; }
      var it = el("div", "it" + (cur && cur.id === c.id ? " on" : ""), c.title);
      var d = el("span", "del", "✕"); d.title = "Delete this chat";
      d.onclick = function (e) { e.stopPropagation(); post({ t: "delete", id: c.id }); };
      it.appendChild(d);
      it.onclick = function () { post({ t: "open", id: c.id }); };
      list.appendChild(it);
    });
  }

  // ---------------------------------------------------------------- context chips
  function drawChips() {
    var c = $("chips"); c.innerHTML = "";
    var a = el("span", "chip" + (ctx.about ? " on" : ""), "👤 About me");
    a.title = "Use your About me and terms (Settings → About me)";
    a.onclick = function () { ctx.about = !ctx.about; drawChips(); };
    c.appendChild(a);
    if (ctx.folder) c.appendChild(removable("📁 " + short(ctx.folder), function () { ctx.folder = null; }));
    ctx.files.forEach(function (f, i) { c.appendChild(removable("📄 " + f.split("\\").pop(), function () { ctx.files.splice(i, 1); })); });
    ctx.emails.forEach(function (m, i) { c.appendChild(removable("✉ " + m.from + " · " + m.subject, function () { ctx.emails.splice(i, 1); })); });
    add("＋ Attach file", function () { post({ t: "pickFiles" }); });
    add("＋ Folder", function () { post({ t: "pickFolder" }); });
    add("＋ Email", function () { $("picker").className = ""; $("pq").focus(); });
    function add(t, f) { var b = el("span", "chip add", t); b.onclick = f; c.appendChild(b); }
  }
  function removable(text, onRemove) {
    var s = el("span", "chip on", text.length > 60 ? text.substring(0, 58) + "…" : text);
    var x = el("span", "x", "✕"); x.onclick = function (e) { e.stopPropagation(); onRemove(); drawChips(); };
    s.appendChild(x); return s;
  }
  function short(p) { var parts = p.split("\\"); return parts.length > 3 ? parts[0] + "\\…\\" + parts.slice(-2).join("\\") : p; }

  // ---------------------------------------------------------------- messages
  function drawChat() {
    var box = $("msgs"); box.innerHTML = "";
    if (!cur || !cur.messages.length) { box.appendChild(emptyView()); return; }
    cur.messages.forEach(function (m, i) { box.appendChild(m.role === "user" ? userMsg(m) : aiMsg(m, i)); });
    box.scrollTop = box.scrollHeight;
  }
  function emptyView() {
    var e = el("div", "empty");
    e.appendChild(el("div", "big", "✦ Nova Chat"));
    var p = el("div"); p.innerHTML = "Ask anything. Add <b>About me</b>, a <b>folder</b>, <b>files</b> or <b>emails</b> below and Nova answers from them, with sources you can open. 📎 Paste (Ctrl+V) a screenshot or copied files, or drop files here — then ask: <b>read it</b> · <b>check it</b> · <b>compare them in a table</b>.";
    e.appendChild(p);
    var ex = el("div", "ex");
    ["Which doors need a 90-minute fire rating in this folder?", "Summarise the attached email and list what I must reply to.", "Rewrite this method statement paragraph in clear professional English:", "What are the usual clear widths for escape corridors in offices?"].forEach(function (t) {
      var d = el("div", null, t); d.onclick = function () { $("input").value = t; $("input").focus(); }; ex.appendChild(d);
    });
    e.appendChild(ex);
    return e;
  }
  function userMsg(m) {
    var f = document.createDocumentFragment();
    f.appendChild(el("div", "mu", m.text));
    if (m.ctxLabel) f.appendChild(el("div", "mctx", m.ctxLabel));
    return f;
  }
  function aiMsg(m, idx) {
    var d = el("div", "ma");
    if (m.label) d.appendChild(el("div", "lab", m.label));
    var t = el("div", "txt");
    render(t, m.text || "", m.sources || []);
    d.appendChild(t);
    if (m.sources && m.sources.length) {
      var s = el("div", "srcs");
      m.sources.forEach(function (src, i) {
        var a = el("span", null, "[" + (i + 1) + "] " + src.label);
        a.title = "Open";
        a.onclick = function () { post({ t: "openSource", src: src }); };
        s.appendChild(a);
      });
      d.appendChild(s);
    }
    var acts = el("div", "acts");
    btn("⧉ Copy", function () { post({ t: "copy", text: m.text }); });
    if (/^\s*\|.*\|/m.test(m.text || "")) {
      btn("📊 Comparison sheet → Excel", function () { post({ t: "exportTable", text: m.text, title: (cur && cur.title) || "Comparison" }); });
      btn("⧉ Copy table", function () { post({ t: "copyTable", text: m.text }); });
    }
    if (cur.ctxUsed && cur.ctxUsed.emails && cur.ctxUsed.emails.length) btn("✉ Draft reply to " + cur.ctxUsed.emails[0].from.split(" ")[0], function () { post({ t: "draftReply", id: cur.id, idx: idx }); });
    btn("⇩ Save as Word", function () { post({ t: "saveWord", id: cur.id, idx: idx }); });
    btn("📌 Save as task", function () { post({ t: "saveTask", id: cur.id, idx: idx }); });
    if (m.agResult) {
      // 🧭 the agent's answer: CSV, the same conversation in the 📋 log
      if ((m.agResult.rows || []).length) {
        btn("⬇ Excel (with email links)", function () { post({ t: "agCsv", id: cur.id, idx: idx }); });
        btn("⇩ Word (with email links)", function () { post({ t: "agWord", id: cur.id, idx: idx }); });
      }
      btn("📋 Show in the log", function () { post({ t: "ag.toLog", id: cur.id }); });
    } else if (!m.detailed) btn("More detail", function () { send(m.question || "", true); });
    d.appendChild(acts);
    function btn(t, f) { var b = el("button", null, t); b.onclick = f; acts.appendChild(b); }
    return d;
  }
  // Light formatting: **bold**, bullets, and [n] citations that open the source.
  function render(box, text, sources) {
    var lines = text.split("\n"), tbl = null;
    // 📊 Markdown tables (comparison sheets) become real tables.
    function flushTable() {
      if (!tbl) return;
      var rows = tbl.filter(function (l) { return !/^\|?\s*:?-{2,}/.test(l.trim()); }).map(function (l) {
        return l.trim().replace(/^\||\|$/g, "").split("|").map(function (c) { return c.trim().replace(/\*\*(.+?)\*\*/g, "$1"); });
      });
      tbl = null;
      if (rows.length < 2) return;
      var wrap = el("div", "tblwrap"), t = el("table", "cmp");
      rows.forEach(function (r, ri) {
        var tr = el("tr");
        var note = r[r.length - 1] || "";
        if (ri > 0 && note.indexOf("⚠") >= 0) tr.className = "diff"; else if (ri > 0 && note.indexOf("✓") >= 0) tr.className = "best";
        for (var i = 0; i < rows[0].length; i++) {
          var cell = el(ri === 0 ? "th" : "td");
          if (ri > 0) inline(cell, r[i] || ""); else cell.textContent = r[i] || "";
          tr.appendChild(cell);
        }
        t.appendChild(tr);
      });
      wrap.appendChild(t); box.appendChild(wrap);
    }
    // **bold** and [n] links: click = the email in Nova's view · Ctrl+click = in Outlook (also inside tables)
    function inline(into, line) {
      var parts = line.split(/(\*\*[^*]+\*\*|\[\d+(?:\s*[,;]\s*\d+)*\])/);
      parts.forEach(function (p) {
        if (/^\*\*[^*]+\*\*$/.test(p)) into.appendChild(el("b", null, p.slice(2, -2)));
        else if (/^\[\d/.test(p)) {
          p.slice(1, -1).split(/[,;]/).forEach(function (n, k) {
            var i = parseInt(n, 10) - 1;
            var c = el("span", "cite", (k ? "" : "[") + (i + 1) + (k < p.slice(1, -1).split(/[,;]/).length - 1 ? "," : "]"));
            if (sources[i]) {
              c.title = sources[i].label + (sources[i].kind === "email" ? "\nclick: open in Nova · Ctrl+click: open in Outlook" : "");
              c.onclick = function (e) { post({ t: "openSource", src: sources[i], ctrl: !!(e && e.ctrlKey) }); };
            }
            into.appendChild(c);
          });
        } else into.appendChild(document.createTextNode(p));
      });
    }
    lines.forEach(function (line, li) {
      if (/^\s*\|.*\|/.test(line)) { (tbl = tbl || []).push(line); return; }
      flushTable();
      var bullet = /^\s*[-*•]\s+/.test(line);
      if (bullet) line = "• " + line.replace(/^\s*[-*•]\s+/, "");
      line = line.replace(/^#{1,4}\s+/, "");
      inline(box, line);
      if (li < lines.length - 1) box.appendChild(document.createTextNode("\n"));
    });
    flushTable();
  }

  function ctxLabel(c) {
    var p = [];
    if (c.folder) p.push("📁 " + short(c.folder));
    if (c.files.length) p.push("📄 " + c.files.length + " file" + (c.files.length > 1 ? "s" : ""));
    if (c.emails.length) p.push("✉ " + c.emails.length + " email" + (c.emails.length > 1 ? "s" : ""));
    if (c.about) p.push("👤 About me");
    return p.join(" · ");
  }

  function setBusy(b) { busy = b; $("send").disabled = b; $("deep").disabled = b; $("stop").style.display = b ? "" : "none"; }

  function send(text, moreDetail, deep, resume) {
    text = (text || "").trim();
    if (!text || busy) return;
    if (!cur) cur = { id: null, title: text.substring(0, 60), messages: [] };
    var d = moreDetail ? true : detail;
    if (!moreDetail) cur.messages.push({ role: "user", text: text, ctxLabel: ctxLabel(ctx) });
    drawChat();
    var th = el("div"); th.id = "thinking";
    $("msgs").appendChild(th);
    var title = deep ? "Deep search — this takes a few minutes…" : ctx.folder ? "Reading the folder and thinking…" : "Thinking…";
    thk = window.Thinking ? Thinking.start(th, title, { onStop: function () { post({ t: "stop" }); } }) : null;
    if (!thk) th.textContent = "✦ " + title, th.className = "thinking";
    $("msgs").scrollTop = 1e9;
    setBusy(true);
    post({ t: "send", id: cur.id, text: text, ctx: ctx, detailed: d, again: !!moreDetail, deep: !!deep, resume: resume || "" });
    $("input").value = "";
  }

  // ● ● ● the thinking card: a step (✓ when the next one starts) or a line inside the step
  var thk = null;
  function thinking(t, isStep) {
    if (thk && thk.active()) { if (isStep) thk.step(t); else thk.detail(t); $("msgs").scrollTop = 1e9; return; }
    var e = $("thinking"); if (e) e.textContent = "✦ " + t;
  }

  // ---------------------------------------------------------------- from Nova
  function onMsg(m) {
    if (m.t === "init") {
      chats = m.chats || []; $("ai").textContent = m.ai ? "AI: " + m.ai : "No AI key — add one in Settings";
      $("sign").textContent = m.sign; drawList(); drawChips(); drawChat();
      if (m.text) { $("input").value = m.text; $("input").focus(); }
    } else if (m.t === "chats") { chats = m.chats || []; drawList(); }
    else if (m.t === "chat") { cur = m.chat; ctx = m.chat.ctx || newCtx(); drawList(); drawChips(); drawChat(); }
    else if (m.t === "progress") thinking(m.text, !!m.step);
    else if (m.t === "answer") {
      setBusy(false); if (thk) thk.done(); thk = null;
      cur = m.chat; drawList(); drawChat();
    } else if (m.t === "error") {
      setBusy(false); if (thk) { thk.fail(m.text); thk = null; return; }
      var e = $("thinking"); if (e) { e.className = "thinking err"; e.textContent = "⚠ " + m.text; }
      else { var x = el("div", "thinking err", "⚠ " + m.text); $("msgs").appendChild(x); }
    } else if (m.t === "folder") { ctx.folder = m.path; drawChips(); }
    else if (m.t === "files") { m.paths.forEach(function (p) { if (ctx.files.indexOf(p) < 0) ctx.files.push(p); }); drawChips(); }
    else if (m.t === "emails") {
      var r = $("presults"); r.innerHTML = "";
      if (!m.list.length) r.appendChild(el("div", "muted small", m.note || "Nothing found."));
      m.list.forEach(function (h) {
        var d = el("div", "pr");
        d.appendChild(el("div", "s", h.subject || "(no subject)"));
        d.appendChild(el("div", "m", h.from + " · " + h.date + (h.sent ? " · sent" : "") + " — " + h.lines));
        d.onclick = function () { ctx.emails.push(h); $("picker").className = "hide"; drawChips(); };
        r.appendChild(d);
      });
    } else if (m.t === "mic") {
      recording = m.on; $("mic").className = m.on ? "rec" : ""; $("mic").textContent = m.on ? "■ Stop" : "🎤";
      if (m.text) { $("input").value = ($("input").value ? $("input").value + " " : "") + m.text; $("input").focus(); }
      if (m.error) { var x2 = el("div", "thinking err", "⚠ " + m.error); $("msgs").appendChild(x2); }
    } else if (m.t === "insert") { $("input").value = ($("input").value ? $("input").value + "\n" : "") + m.text; $("input").focus(); }
    else if (m.t === "askNow") send(m.text);
    else if (m.t === "ag.confirm") agConfirm(m);
    else if (m.t === "continueDeep") { if (busy) return; newChat(); send(m.text, false, true, m.resume); }
    else if (m.t === "new") newChat();
  }
  // 🧭 Nova agent: “are these the kind of emails you mean?” ✓ / ✗, then it continues.
  function agConfirm(m) {
    var card = el("div", "msg nova"); card.style.cssText = "border:1px solid #3A3F55;border-radius:12px;padding:10px 12px;margin:6px 0";
    card.appendChild(el("div", null, "✓ / ✗ " + m.question)).style.fontWeight = "600";
    var verdict = {};
    (m.items || []).forEach(function (h) {
      var r = el("div"); r.style.cssText = "display:flex;gap:8px;padding:6px 0;border-bottom:1px solid #232836";
      var y = el("button", null, "✓"), n = el("button", null, "✗");
      y.onclick = function () { verdict[h.id] = true; y.style.background = "#7C6CFF"; n.style.background = ""; };
      n.onclick = function () { verdict[h.id] = false; n.style.background = "#7C6CFF"; y.style.background = ""; };
      var c = el("div"); var s = el("div", null, h.subject || "(no subject)"); s.style.cssText = "font-weight:600;cursor:pointer"; s.onclick = function () { post({ t: "openSource", src: { kind: "email", entry: h.entry, store: h.store } }); };
      c.appendChild(s); c.appendChild(el("div", "muted small", h.date + " · from " + h.from + " · to " + h.to + (h.cc ? " · cc " + h.cc : "")));
      if (h.lines) c.appendChild(el("div", "muted small", h.lines));
      r.appendChild(y); r.appendChild(n); r.appendChild(c); card.appendChild(r);
    });
    var note = el("input"); note.placeholder = "anything to add? e.g. “only formal approval requests”"; note.style.cssText = "width:100%;margin-top:6px";
    card.appendChild(note);
    var go = el("button", null, "Continue →"); go.style.marginTop = "6px";
    go.onclick = function () {
      var yes = [], no = []; Object.keys(verdict).forEach(function (k) { (verdict[k] ? yes : no).push(+k); });
      card.querySelectorAll("button,input").forEach(function (b) { b.disabled = true; });
      post({ t: "ag.confirmed", yes: yes, no: no, note: note.value });
    };
    card.appendChild(go);
    if (thk) thk.pause("waiting for your ✓ / ✗ above");
    go.addEventListener("click", function () { if (thk) { thk.resume(); thk.detail("continuing with your ✓ / ✗…"); } });
    var th = $("thinking"); $("msgs").insertBefore(card, th || null);
    card.scrollIntoView({ block: "nearest" });
  }
  function newChat() { cur = null; ctx = newCtx(); drawList(); drawChips(); drawChat(); $("input").focus(); }

  // ---------------------------------------------------------------- wiring
  $("newchat").onclick = newChat;
  $("find").oninput = drawList;
  $("send").onclick = function () { send($("input").value); };
  $("input").onkeydown = function (e) { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send($("input").value); } };
  $("deep").onclick = function () { send($("input").value || "Check in my records whether this system, or parts of it, was submitted or approved earlier.", false, true); };
  $("stop").onclick = function () { post({ t: "stop" }); thinking("Stopping…"); };
  // ✓ / ✗ examples: the thinking card waits for you meanwhile
  $("short").onclick = function () { detail = false; $("short").className = "on"; $("detail").className = ""; };
  $("detail").onclick = function () { detail = true; $("detail").className = "on"; $("short").className = ""; };
  $("mic").onclick = function () { post({ t: "mic", on: !recording }); };
  $("pclose").onclick = function () { $("picker").className = "hide"; };
  $("pq").onkeydown = function (e) { if (e.key === "Enter") { $("presults").innerHTML = "<div class='muted small'>Searching…</div>"; post({ t: "findEmail", q: $("pq").value }); } if (e.key === "Escape") $("picker").className = "hide"; };
  // 📎 Paste (Ctrl+V a screenshot or files copied in Explorer) or drop files: attached to the question.
  function sendFile(f) {
    if (f.size > 40 * 1024 * 1024) { thinking("⚠ " + f.name + " is too big to drop here — use 📄 Files"); return; }
    var r = new FileReader();
    r.onload = function () { var s = String(r.result); post({ t: "pasteFile", name: f.name || "", b64: s.substring(s.indexOf(",") + 1) }); };
    r.readAsDataURL(f);
  }
  document.addEventListener("paste", function (e) {
    var cd = e.clipboardData; if (!cd) return;
    var types = Array.prototype.slice.call(cd.types || []);
    if (types.indexOf("Files") < 0) return;
    e.preventDefault();
    post({ t: "pasteClipboard" });
  });
  document.addEventListener("dragover", function (e) { e.preventDefault(); e.dataTransfer.dropEffect = "copy"; });
  document.addEventListener("drop", function (e) {
    e.preventDefault();
    var fs = e.dataTransfer && e.dataTransfer.files;
    if (fs) for (var i = 0; i < fs.length && i < 6; i++) sendFile(fs[i]);
  });
  if (W) W.addEventListener("message", function (e) { onMsg(e.data); });
  drawChips(); drawChat();
  post({ t: "init" });
})();
