// 🧠 Nova Brain page. Designed by A.HAMROUNI
(function () {
  var W = window.chrome && window.chrome.webview;
  function post(o) { if (W) W.postMessage(o); }
  function $(id) { return document.getElementById(id); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  function btn(t, primary, f) { var b = el("button", primary ? "primary" : null, t); b.onclick = function (ev) { ev.stopPropagation(); f(); }; return b; }
  var st = null, cur = "submittal", view = "section", history = [], editing = false;

  function toast(t) { var x = $("toast"); x.textContent = t; x.style.display = "block"; clearTimeout(x._t); x._t = setTimeout(function () { x.style.display = "none"; }, 4500); }
  function when(ms) { if (!ms) return ""; var d = new Date(ms); return d.toLocaleDateString("en-GB", { day: "2-digit", month: "short", year: "numeric" }) + " " + d.toTimeString().slice(0, 5); }

  // ---------------------------------------------------------------- lock / make / pending restore
  function drawLock() {
    var L = $("lock"), b = $("lockBox");
    if (st.unlocked) { L.className = "hide"; return; }
    L.className = ""; b.innerHTML = "";
    b.appendChild(el("b", null, "🧠 Nova Brain")).style.fontSize = "18px";
    var p1 = el("input"); p1.type = "password";
    var p2 = el("input"); p2.type = "password";
    var msg = el("div", "small muted");
    if (!st.exists) {
      b.appendChild(el("div", "muted", "Make your brain: one encrypted file (AES-256) in the Nova folder, with its OWN password. Nova remembers it on this PC; it is never inside the full backup and never sent anywhere. Keep it safe: without it the brain and its backups can't be opened."));
      p1.placeholder = "New brain password (6+ characters)"; p2.placeholder = "Repeat it";
      b.appendChild(p1); b.appendChild(p2); b.appendChild(msg);
      b.appendChild(btn("🧠 Make my brain (starts with the built-in rules)", true, function () {
        if (p1.value.length < 6) { msg.textContent = "Use at least 6 characters"; return; }
        if (p1.value !== p2.value) { msg.textContent = "The two passwords are different"; return; }
        post({ t: "create", pw: p1.value });
      }));
    } else {
      b.appendChild(el("div", "muted", st.pending ? "A brain from your full Nova backup is waiting. Type ITS password to check and restore it (your current brain is kept as “before restore”)." : "The brain is locked. Type its password — Nova remembers it on this PC."));
      p1.placeholder = "Brain password"; b.appendChild(p1); b.appendChild(msg);
      p1.onkeydown = function (e) { if (e.key === "Enter") go(); };
      function go() { post({ t: st.pending ? "pending" : "unlock", pw: p1.value }); }
      b.appendChild(btn(st.pending ? "↩ Check and restore" : "🔓 Open", true, go));
      if (st.pending) b.appendChild(btn("Open my current brain instead", false, function () { post({ t: "unlock", pw: p1.value }); }));
    }
    b.appendChild(el("div", "sign", st.sign || ""));
    setTimeout(function () { p1.focus(); }, 50);
  }

  // ---------------------------------------------------------------- sections
  function sections() { return (st.brain && st.brain.sections) || []; }
  function drawTop() {
    $("pill").className = "pill " + (st.unlocked ? "pg" : "pw"); $("pill").textContent = st.unlocked ? "🔒 unlocked · AES-256" : "locked";
    $("meta").textContent = st.unlocked ? "Nova Brain.nbrain · version " + st.version + " · " + st.rules + " rules · last backup " + (st.lastBackup || "none yet") : "";
  }
  function drawSecs() {
    var box = $("secs"); box.innerHTML = "";
    box.appendChild(el("div", "muted small", "SECTIONS · " + st.rules + " rules")).style.padding = "4px 10px 8px";
    sections().forEach(function (s) {
      var d = el("div", "sec" + (view === "section" && cur === s.id ? " on" : ""));
      d.appendChild(el("span", null, (s.icon ? s.icon + " " : "") + s.name)); d.appendChild(el("span", "muted small", String((s.rules || []).length)));
      d.onclick = function () { view = "section"; cur = s.id; editing = false; drawSecs(); drawMid(); };
      box.appendChild(d);
    });
    var n = el("div", "muted small", "＋ New section"); n.style.cssText = "padding:10px;cursor:pointer";
    n.onclick = function () { var name = prompt("Name of the new section"); if (name) post({ t: "newSection", name: name, icon: "" }); };
    box.appendChild(n);
  }
  function drawMid() {
    if (view === "versions") return drawVersions();
    if (view === "health") return drawHealth();
    var box = $("mid"); box.innerHTML = "";
    var s = sections().filter(function (x) { return x.id === cur; })[0] || sections()[0];
    if (!s) return;
    cur = s.id;
    var h = el("div", "row");
    h.appendChild(el("b", null, (s.icon ? s.icon + " " : "") + s.name)).style.fontSize = "16px";
    h.appendChild(el("span", "muted small", "used for: " + (s.tags || []).join(" · ")));
    h.appendChild(el("span", "grow"));
    if (s.readonly) { editing = false; h.appendChild(el("span", "muted small", "read-only · change it with ✎ Edit in the 📋 Submittal log")); }
    else if (!editing) h.appendChild(btn("✎ Edit section", false, function () { editing = true; drawMid(); }));
    box.appendChild(h);
    var card = el("div", "card2"); card.style.marginTop = "10px";
    var rows = [];
    (s.rules || []).forEach(function (r) {
      var d = el("div", "rule" + (r.off ? " off" : "") + (editing ? " edit" : ""));
      d.appendChild(el("span", "id", r.id));
      if (editing) {
        var c = el("div"); var ti = el("input"); ti.value = r.title || ""; ti.placeholder = "title"; var tx = el("textarea"); tx.value = r.text || "";
        c.appendChild(ti); c.appendChild(tx); d.appendChild(c);
        var off = el("label", "small"); var cb = el("input"); cb.type = "checkbox"; cb.style.width = "auto"; cb.checked = !!r.off; off.appendChild(cb); off.appendChild(document.createTextNode(" off"));
        var rm = btn("🗑", false, function () { d.remove(); rows = rows.filter(function (x) { return x.d !== d; }); });
        var cc = el("div"); cc.appendChild(off); cc.appendChild(rm); d.appendChild(cc);
        rows.push({ d: d, r: r, ti: ti, tx: tx, cb: cb });
      } else {
        var c2 = el("div"); var t = el("div"); if (r.title) t.appendChild(el("b", null, r.title + " · ")); t.appendChild(document.createTextNode(r.text || "")); c2.appendChild(t);
        c2.appendChild(el("div", "by", (r.by || "") + (r.when ? " · " + when(r.when) : "")));
        d.appendChild(c2);
        var tb = el("span", "tbadge" + (r.tested ? " yes" : ""), r.tested ? "🧪 tested ✓" : "○ untested");
        tb.title = r.tested ? "passed " + r.tested.when + " · “" + (r.tested.q || "") + "” · " + r.tested.yes + " ✓ · +" + r.tested.plus + " / −" + r.tested.minus : "not tested yet (rules from before 🧪 TEST, the built-in ones and your own edits)";
        d.appendChild(tb);
      }
      card.appendChild(d);
    });
    box.appendChild(card);
    if (editing) {
      var add = btn("＋ Add a rule", false, function () {
        var r = { id: "", title: "", text: "" }, d = el("div", "rule edit"); d.appendChild(el("span", "id", "new"));
        var c = el("div"); var ti = el("input"); ti.placeholder = "title"; var tx = el("textarea"); tx.placeholder = "the rule, in your words ([name = value] for a setting)"; c.appendChild(ti); c.appendChild(tx); d.appendChild(c); d.appendChild(el("span"));
        card.appendChild(d); rows.push({ d: d, r: r, ti: ti, tx: tx, cb: { checked: false } }); tx.focus();
      });
      var bar = el("div", "row"); bar.appendChild(add); bar.appendChild(el("span", "grow"));
      bar.appendChild(btn("✗ Cancel", false, function () { editing = false; drawMid(); }));
      bar.appendChild(btn("✓ Save (a version is kept)", true, function () {
        var out = rows.filter(function (x) { return x.tx.value.trim(); }).map(function (x) { var o = JSON.parse(JSON.stringify(x.r)); o.title = x.ti.value.trim(); o.text = x.tx.value.trim(); if (x.cb.checked) o.off = true; else delete o.off; return o; });
        editing = false; post({ t: "saveSection", id: s.id, rules: out });
      }));
      box.appendChild(bar);
    } else box.appendChild(el("div", "muted small", "A setting inside a rule is written [name = value] — Nova reads it (e.g. [overdue.WIR = 2])."));
  }

  // ---------------------------------------------------------------- versions & backups
  var vers = [], baks = [], diffOf = null;
  function drawVersions() {
    var box = $("mid"); box.innerHTML = "";
    var h = el("div", "row"); h.appendChild(el("b", null, "🕘 Versions")).style.fontSize = "16px"; h.appendChild(el("span", "muted small", "saved before every change · click one to see what changed"));
    h.appendChild(el("span", "grow")); h.appendChild(btn("← Back to the rules", false, function () { view = "section"; drawSecs(); drawMid(); })); box.appendChild(h);
    var t = el("table"); t.style.marginTop = "8px";
    var hr = el("tr"); ["Ver.", "When", "Change", "By", ""].forEach(function (x) { hr.appendChild(el("th", null, x)); }); t.appendChild(hr);
    vers.slice(0, 60).forEach(function (v, i) {
      var tr = el("tr"); tr.style.cursor = "pointer";
      tr.appendChild(el("td", null, String(v.v))); tr.appendChild(el("td", null, when(v.when))); tr.appendChild(el("td", null, v.summary)); tr.appendChild(el("td", "by", v.by));
      var td = el("td"); if (i === 0) td.appendChild(el("span", "ok", "current")); else if (v.file) td.appendChild(btn("↩ Back to this", false, function () { if (confirm("Go back to version " + v.v + "? (the current one stays in the list)")) post({ t: "backTo", v: v.v }); }));
      tr.appendChild(td);
      tr.onclick = function () { if (v.file) post({ t: "viewVersion", v: v.v }); };
      t.appendChild(tr);
      if (diffOf && diffOf.v === v.v) { var dr = el("tr"); var dc = el("td"); dc.colSpan = 5; dc.appendChild(diffBox(diffOf.diff)); dr.appendChild(dc); t.appendChild(dr); }
    });
    box.appendChild(t); if (window.ColResize) ColResize.attach(t, "brain.versions");
    // Backups
    var h2 = el("div", "row"); h2.style.marginTop = "18px"; h2.appendChild(el("b", null, "💾 Brain backups")).style.fontSize = "16px"; h2.appendChild(el("span", "muted small", "encrypted · each one opened and checked")); box.appendChild(h2);
    var c = el("div", "card2"); c.style.marginTop = "8px";
    var r1 = el("div", "row"); r1.appendChild(el("span", "pill pg", "✓ daily at 03:00")); r1.appendChild(el("span", "small muted", "missed (PC off) → at the next start · keep"));
    var sd = el("select"), sm = el("select");
    [7, 14, 30, 60, 90].forEach(function (n) { var o = el("option", null, n + " daily"); o.value = n; if (n === st.keepDaily) o.selected = true; sd.appendChild(o); });
    [3, 6, 12, 24].forEach(function (n) { var o = el("option", null, n + " monthly"); o.value = n; if (n === st.keepMonthly) o.selected = true; sm.appendChild(o); });
    sd.onchange = sm.onchange = function () { post({ t: "keep", daily: +sd.value, monthly: +sm.value }); };
    r1.appendChild(sd); r1.appendChild(sm); c.appendChild(r1);
    var r2 = el("div", "row"); r2.style.marginTop = "6px";
    var dv = el("label", "small"); var cb = el("input"); cb.type = "checkbox"; cb.style.width = "auto"; cb.checked = !!st.drive; cb.onchange = function () { post({ t: "drive", on: cb.checked }); };
    dv.appendChild(cb); dv.appendChild(document.createTextNode(" also copy to Google Drive")); r2.appendChild(dv);
    r2.appendChild(el("span", "pill pg", "✓ inside the full Nova backup")); r2.appendChild(btn("📂 Open the folder", false, function () { post({ t: "folder" }); }));
    c.appendChild(r2); box.appendChild(c);
    var t2 = el("table"); var hr2 = el("tr"); ["Backup", "Ver.", "Size", "Check", ""].forEach(function (x) { hr2.appendChild(el("th", null, x)); }); t2.appendChild(hr2);
    if (!baks.length) { var e = el("tr"); var ec = el("td", "muted", "⏳ checking…"); ec.colSpan = 5; e.appendChild(ec); t2.appendChild(e); }
    baks.forEach(function (b) {
      var tr = el("tr");
      tr.appendChild(el("td", null, when(b.when) + " · " + (b.kind || "")));
      tr.appendChild(el("td", null, b.v != null ? String(b.v) : "–")); tr.appendChild(el("td", null, Math.round(b.size / 1024) + " KB"));
      tr.appendChild(el("td", b.ok ? "ok" : "bad", b.ok ? "✓ opened & verified · " + b.rules + " rules" : "⚠ " + (b.why || "can't be opened")));
      var td = el("td"); if (b.ok) td.appendChild(btn("↩ Restore", false, function () { if (confirm("Restore this backup? Your current brain is kept first as “before restore”.")) post({ t: "restoreBackup", file: b.file }); })); tr.appendChild(td);
      t2.appendChild(tr);
    });
    box.appendChild(t2); if (window.ColResize) ColResize.attach(t2, "brain.backups");
    var r3 = el("div", "row"); r3.style.marginTop = "10px";
    r3.appendChild(btn("💾 Back up the brain now", true, function () { post({ t: "backupNow" }); }));
    r3.appendChild(btn("📥 Restore from a file…", false, function () { post({ t: "restoreFile" }); }));
    r3.appendChild(btn("🔑 Change the brain password", false, function () {
      var o = prompt("Current brain password"); if (!o) return; var n = prompt("New password (6+ characters)"); if (!n) return; var n2 = prompt("Repeat the new password"); if (n !== n2) { toast("The two new passwords are different"); return; }
      post({ t: "changePw", old: o, "new": n });
    }));
    box.appendChild(r3);
  }

  function diffBox(diff) {
    var d = el("div", "diff");
    if (!diff || !diff.length) { d.textContent = "(no rule changed)"; return d; }
    diff.forEach(function (x) {
      if (x.op === "remove") d.appendChild(el("div", "del", "− " + x.id + " " + (x.old || "")));
      else if (x.op === "change") { d.appendChild(el("div", "del", "  " + x.id + " " + (x.old || ""))); d.appendChild(el("div", "chg", "✎ " + x.id + " " + (x.text || ""))); }
      else d.appendChild(el("div", "add", "+ " + x.id + " " + (x.text || "") + (x.section ? "   (" + x.section + ")" : "")));
    });
    return d;
  }

  // ---------------------------------------------------------------- 🎓 the trainer (talk to the brain)
  function addMsg(cls, text) { var m = el("div", "msg " + cls, text); $("msgs2").appendChild(m); $("msgs2").scrollTop = 1e9; return m; }
  // 📎 example files (✓ like this / ✗ not like this)
  var bfiles = [];
  function drawFiles() {
    var b = $("bfiles"); b.innerHTML = "";
    bfiles.forEach(function (f, i) {
      var c = el("span", "fchip" + (f.private ? " priv" : "")); c.title = f.path;
      c.appendChild(el("span", "n", (f.private ? "🔒 " : "📄 ") + f.name));
      var g = el("button", f.good !== false ? "g" : "b", f.good !== false ? "✓ like this" : "✗ not like this");
      g.title = "click to switch: an example to follow (✓) or to leave out (✗)";
      g.onclick = function () { f.good = f.good === false; drawFiles(); };
      var x = el("button", null, "✕"); x.onclick = function () { bfiles.splice(i, 1); drawFiles(); };
      c.appendChild(g); c.appendChild(x); b.appendChild(c);
    });
  }
  function addFiles(list) { (list || []).forEach(function (f) { if (!bfiles.some(function (x) { return x.path === f.path; })) { f.good = f.good !== false; bfiles.push(f); } }); drawFiles(); $("ask").focus(); }
  function say(t, files) {
    files = files || [];
    if (!t && !files.length) return;
    var me = addMsg("me", t || "Learn from these examples and propose the rule.");
    if (files.length) me.appendChild(el("div", "small", "📎 " + files.map(function (f) { return (f.good === false ? "✗ " : "✓ ") + f.name; }).join(" · ")));
    post({ t: "ask", text: t || "", files: files });
  }
  function send() {
    var t = $("ask").value.trim(); var fs = bfiles.filter(function (f) { return !f.private; });
    if (!t && !fs.length) return;
    $("ask").value = ""; bfiles = []; drawFiles(); say(t, fs);
  }
  $("bAttach").onclick = function () { post({ t: "pickFiles" }); };
  $("bOutlook").onclick = function () { post({ t: "outlookMail" }); };
  function sendFile(f) {
    if (f.size > 40 * 1024 * 1024) { toast("⚠ " + f.name + " is too big"); return; }
    var r = new FileReader();
    r.onload = function () { var s = String(r.result); post({ t: "pasteFile", name: f.name || "", b64: s.substring(s.indexOf(",") + 1) }); };
    r.readAsDataURL(f);
  }
  var ab = $("askBox");
  ab.addEventListener("dragover", function (e) { e.preventDefault(); e.dataTransfer.dropEffect = "copy"; ab.classList.add("drag"); });
  ab.addEventListener("dragleave", function () { ab.classList.remove("drag"); });
  ab.addEventListener("drop", function (e) { e.preventDefault(); ab.classList.remove("drag"); var fs = e.dataTransfer && e.dataTransfer.files; if (fs) for (var i = 0; i < fs.length && i < 8; i++) sendFile(fs[i]); });
  $("ask").addEventListener("paste", function (e) {
    var cd = e.clipboardData; if (!cd) return;
    if (Array.prototype.slice.call(cd.types || []).indexOf("Files") < 0) return;
    e.preventDefault(); post({ t: "pasteClipboard" });
  });
  $("send").onclick = send;
  $("ask").onkeydown = function (e) { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); } };

  // ● ● ● the work card (trainer steps · test · health)
  var wk = null, wkSteps = 0;
  function work(title) {
    wkSteps = 0;
    var holder = el("div"); $("msgs2").appendChild(holder);
    wk = window.Thinking ? Thinking.start(holder, title + "…", { onStop: function () { post({ t: "stop" }); } }) : null;
    $("msgs2").scrollTop = 1e9;
  }

  var cards = {}, nCard = 0;
  function drawAnswer(m) {
    var a = addMsg("ai", m.reply || "");
    if (m.kind === "ask" && m.options && m.options.length) {
      var ob = el("div", "opts");
      m.options.forEach(function (o) { ob.appendChild(btn(String(o), false, function () { ob.querySelectorAll("button").forEach(function (b) { b.disabled = true; }); say(String(o)); })); });
      a.appendChild(ob);
      a.appendChild(el("div", "muted small", "pick one, or type your own answer below"));
    }
    if (m.kind !== "propose" || !m.changes || !m.changes.length) { if (m.by) a.appendChild(el("div", "by", "by " + m.by)); return; }
    if (m.learned && m.learned.length) {
      var lb = el("div", "learned"); lb.appendChild(el("b", null, "🎓 What I learned from your example"));
      var ul = el("ul"); ul.style.margin = "4px 0 0 16px"; ul.style.padding = "0";
      m.learned.forEach(function (x) { ul.appendChild(el("li", null, String(x))); }); lb.appendChild(ul);
      if ((m.mustFind || []).length) lb.appendChild(el("div", "muted small", "🧪 the test checks that it finds: " + m.mustFind.join(", ") + ((m.mustNotFind || []).length ? " · and not: " + m.mustNotFind.join(", ") : "")));
      a.appendChild(lb);
    }
    if (m.why) a.appendChild(el("div", "muted small", "Why: " + m.why));
    if (m.conflict) a.appendChild(el("div", "chg", "⚠ " + m.conflict));
    var id = "c" + (++nCard);
    var c = cards[id] = { m: m, a: a, test: "", edited: false };
    c.diffEl = diffBox(m.diff); a.appendChild(c.diffEl);
    var ck = m.checks || {};
    (ck.duplicates || []).forEach(function (x) { a.appendChild(el("div", "bad small", "⛔ " + x.why)); });
    (ck.vague || []).concat(ck.syntax || []).forEach(function (x) { a.appendChild(el("div", "chg small", "⚠ " + x)); });
    var bar = el("div", "row"); bar.style.marginTop = "6px"; bar.style.flexWrap = "wrap"; c.bar = bar;
    c.testBtn = btn("🧪 TEST", false, function () { runTest(id, "quick"); }); c.testBtn.className = "testb"; c.testBtn.title = "⚡ quick test (about 30 s): your 📎 examples + the 20 most similar emails";
    c.apply = btn("✓ Apply", true, function () { if (c.apply.disabled) return; post({ t: "apply", changes: m.changes, test: c.test, summary: "" }); bar.remove(); c.res.textContent = "✓ applied (a version is saved with its test)"; });
    c.apply.disabled = true; c.apply.title = "🔒 opens after a passed 🧪 TEST";
    bar.appendChild(c.testBtn); bar.appendChild(c.apply);
    bar.appendChild(btn("✎ Edit", false, function () { editCard(id); }));
    bar.appendChild(btn("✗ Cancel", false, function () { bar.remove(); c.res.textContent = "cancelled — nothing changed"; }));
    a.appendChild(bar);
    c.res = el("div", "muted small", "🔒 ✓ Apply opens after a passed test · the test saves nothing"); a.appendChild(c.res);
    c.tq = m.testQuestion || "";
    if (m.by) a.appendChild(el("div", "by", "by " + m.by));
  }
  function lockApply(c, why) { c.apply.disabled = true; c.test = ""; c.res.textContent = why || "🔒 ✓ Apply opens after a passed test"; }
  function editCard(id) {
    var c = cards[id]; if (c.ed) return;
    var ed = c.ed = el("div", "edit");
    c.m.changes.forEach(function (ch) { var tx = el("textarea"); tx.value = ch.text || ""; tx.oninput = function () { ch.text = tx.value; lockApply(c, "✎ edited — 🧪 test again"); }; ed.appendChild(el("div", "id", (ch.op || "") + " " + (ch.id || "") + " · " + (ch.section || ""))); if (ch.op !== "remove") ed.appendChild(tx); });
    c.a.insertBefore(ed, c.bar);
  }
  function runTest(id, mode) {
    var c = cards[id]; mode = mode || "quick"; lockApply(c, "🧪 testing… 0:00");
    // ⏱ live timer on the card while the test runs
    clearInterval(c.iv); c.t0 = Date.now();
    c.iv = setInterval(function () { c.res.textContent = "🧪 testing… " + Thinking.mmss(Math.round((Date.now() - c.t0) / 1000)) + (wkSteps ? " · step " + wkSteps : ""); }, 1000);
    pendingCard = id;
    post({ t: "test", changes: c.m.changes, question: c.tq, kind: c.m.kindOf || "", card: id, mustFind: c.m.mustFind || [], mustNotFind: c.m.mustNotFind || [], mode: mode });
  }
  var pendingCard = null;
  function drawTest(m) {
    var c = cards[m.card]; if (!c) return;
    clearInterval(c.iv); c.secs = c.t0 ? Math.round((Date.now() - c.t0) / 1000) : 0;
    if (c.tbox) c.tbox.remove();
    var b = c.tbox = el("div", "tbox");
    var quick = m.mode === "quick";
    b.appendChild(el("b", null, "🧪 " + (quick ? "⚡ Quick test · your examples + similar emails" : "🔬 Full test · " + (m.kind === "writing" ? "a sample text" : m.kind === "log" ? "the 📋 log + a search" : "a search, now vs with the new rule"))));
    var qr = el("div", "row"); var q = el("textarea", "tq"); q.value = m.question; q.rows = 2; q.oninput = function () { c.tq = q.value; };
    if (!quick) { qr.appendChild(q); b.appendChild(el("div", "muted small", "test question (change it and 🔬 test again):")); b.appendChild(qr); }
    if (quick) {
      // ⚡ one list: each email and what Nova says with the new rule
      var t = el("table"); var hr = el("tr"); ((m.after && m.after.columns) || ["Email", "With the new rule", "Why"]).forEach(function (x) { hr.appendChild(el("th", null, x)); }); t.appendChild(hr);
      ((m.after && m.after.rows) || []).forEach(function (r) { var tr = el("tr"); (r.cells || []).forEach(function (x, i) { var td = el("td", i === 1 ? (/✓ fits/.test(x) ? "ok" : "muted") : "small", x); tr.appendChild(td); }); if (r.mark === "minus") tr.style.background = "#2A1416"; t.appendChild(tr); });
      var tw = el("div"); tw.style.cssText = "max-height:300px;overflow:auto;margin:8px 0"; tw.appendChild(t); b.appendChild(tw);
      b.appendChild(el("div", "muted small", (m.after && m.after.text || "") + " · ⟳ = Nova would treat it differently without the new rule (" + (m.plus || 0) + ")"));
    }
    // before | after
    var ba = el("div", "ba"); if (quick) ba.style.display = "none";
    function side(title, x) {
      var col = el("div", "side"); col.appendChild(el("b", null, title));
      if (m.kind === "writing") col.appendChild(el("div", "txt", (x && x.text) || ""));
      else {
        (x.rows || []).forEach(function (r) { col.appendChild(el("div", "r " + (r.mark || ""), (r.mark === "plus" ? "➕ " : r.mark === "minus" ? "➖ " : "") + (r.cells || []).join(" · "))); });
        if (!(x.rows || []).length) col.appendChild(el("div", "muted small", Agent0(x.text)));
      }
      return col;
    }
    function Agent0(t) { return (t || "(nothing found)").slice(0, 300); }
    ba.appendChild(side("Your brain now" + (m.kind === "writing" ? "" : " · " + ((m.before && m.before.rows) || []).length + " found"), m.before || {}));
    ba.appendChild(side("With the new rule" + (m.kind === "writing" ? "" : " · " + ((m.after && m.after.rows) || []).length + " found"), m.after || {}));
    b.appendChild(ba);
    if (m.log) {
      b.appendChild(el("div", "small", "📋 log, last " + m.log.days + " days: " + m.log.changed + " of " + m.log.total + " items change status"));
      if (m.log.rows && m.log.rows.length) {
        var t = el("table"); var hr = el("tr"); ["Document", "Now", "With the new rule"].forEach(function (x) { hr.appendChild(el("th", null, x)); }); t.appendChild(hr);
        m.log.rows.forEach(function (r) { var tr = el("tr"); tr.title = r.why; tr.appendChild(el("td", "id", r.no)); tr.appendChild(el("td", null, r.now)); tr.appendChild(el("td", null, r.then)); t.appendChild(tr); });
        b.appendChild(t);
      }
    }
    // ✓ / ✗
    b.appendChild(el("b", "small", "✓ / ✗ — are these right?"));
    var verdict = {};
    (m.examples || []).forEach(function (h) {
      var r = el("div", "exr"), y = el("button", null, "✓"), n = el("button", null, "✗");
      y.onclick = function () { verdict[h.id] = true; y.className = "on"; n.className = ""; };
      n.onclick = function () { verdict[h.id] = false; n.className = "on"; y.className = ""; };
      var tx = el("div"); tx.appendChild(el("div", "s", h.subject || "")); tx.appendChild(el("div", "muted small", [h.date, h.from ? "from " + h.from : "", h.to ? "to " + h.to : ""].filter(Boolean).join(" · ")));
      if (h.lines) tx.appendChild(el("div", "muted small", h.lines));
      if (h.entry) { tx.style.cursor = "pointer"; tx.title = "open in Outlook"; tx.onclick = function () { post({ t: "open", entry: h.entry, store: h.store }); }; }
      r.appendChild(y); r.appendChild(n); r.appendChild(tx); b.appendChild(r);
    });
    // checks
    var ck = m.checks || {}, cl = el("div", "checks");
    (m.examplesFound || []).forEach(function (x) { cl.appendChild(el("div", "ok", "✓ Your example " + x + " is found")); });
    if (!(ck.contradictions || []).length) cl.appendChild(el("div", "ok", "✓ No contradiction with your rules"));
    (ck.contradictions || []).forEach(function (x) { cl.appendChild(el("div", "bad", "⛔ Contradiction · " + (x.why || x))); });
    if (!(ck.duplicates || []).length) cl.appendChild(el("div", "ok", "✓ Not a duplicate"));
    (ck.duplicates || []).forEach(function (x) { cl.appendChild(el("div", "bad", "⛔ Duplicate · " + (x.why || x))); });
    (ck.weak || []).forEach(function (x) { cl.appendChild(el("div", "chg", "⚠ " + x)); });
    if (ck.better) cl.appendChild(el("div", "muted small", "💡 Suggested wording: " + ck.better));
    b.appendChild(cl);
    var vb = el("div", "row"); vb.style.flexWrap = "wrap";
    vb.appendChild(btn("✔ Confirm my ✓ / ✗", true, function () {
      var yes = 0, no = 0; Object.keys(verdict).forEach(function (k) { if (verdict[k]) yes++; else no++; });
      post({ t: "verdict", id: m.id, yes: yes, no: no, card: m.card, secs: c.secs || 0 });
    }));
    if (ck.better) vb.appendChild(btn("✎ Use the suggested wording", false, function () {
      var ch = c.m.changes.filter(function (x) { return x.op !== "remove"; })[0]; if (!ch) return;
      ch.text = ck.better; c.a.replaceChild(c.diffEl = diffBox([{ op: ch.op === "change" ? "change" : "add", id: ch.id || "(new)", text: ck.better, section: ch.section }]), c.diffEl);
      if (c.ed) { c.ed.remove(); c.ed = null; } lockApply(c, "✎ new wording — 🧪 test again");
    }));
    (ck.duplicates || []).forEach(function (x) { if (x.with) vb.appendChild(btn("⇢ Merge into " + x.with, false, function () { say("Merge this proposal into " + x.with + " instead of adding a new rule: change " + x.with + " so it covers both, then propose it."); })); });
    vb.appendChild(btn("⚡ Quick test again", false, function () { runTest(m.card, "quick"); }));
    vb.appendChild(btn("🔬 Full test", false, function () { runTest(m.card, "full"); })).title = "the test question run with your brain now and with the new rule (slower)";
    b.appendChild(vb);
    c.vline = el("div", "verdict"); b.appendChild(c.vline);
    c.test = m.id; c.apply.disabled = true;
    c.res.textContent = m.blocking ? "⛔ blocked — fix the problems above first" : "tick ✓ / ✗, then ✔ Confirm — ✓ Apply opens when the test passes";
    c.a.insertBefore(b, c.bar);
    b.scrollIntoView({ block: "nearest" });
  }
  function drawVerdict(m) {
    var c = cards[m.card]; if (!c || !c.vline) return;
    c.vline.className = "verdict " + m.verdict;
    c.vline.textContent = (m.verdict === "passed" ? "✅ Passed · " : m.verdict === "weak" ? "⚠ Weak · " : "⛔ Blocked · ") + m.why + (c.secs ? " · tested in " + Thinking.mmss(c.secs) : "");
    if (m.verdict === "passed") { c.apply.disabled = false; c.apply.title = ""; c.res.textContent = "✅ tested — ✓ Apply saves a new version with this test · ↩ undo anytime in Versions"; c.testBtn.textContent = "🧪 Test again"; }
    else lockApply(c, m.verdict === "weak" ? "⚠ weak — sharpen the rule (✎ Edit / suggested wording) and test again" : "⛔ blocked — fix it, then test again");
    c.test = m.verdict === "passed" ? m.id : "";
  }

  // ---------------------------------------------------------------- 🩺 health check
  var health = null;
  function drawHealth() {
    var box = $("mid"); box.innerHTML = "";
    var h = el("div", "row"); h.appendChild(el("b", null, "🩺 Brain health check")).style.fontSize = "16px";
    h.appendChild(el("span", "grow")); h.appendChild(btn("↻ Check again", false, function () { post({ t: "health" }); })); h.appendChild(btn("← Back to the rules", false, function () { view = "section"; drawSecs(); drawMid(); }));
    box.appendChild(h);
    if (!health) { box.appendChild(el("div", "muted", "Checking every rule… (see the steps on the right)")); return; }
    var sum = el("div", "row"); sum.style.margin = "10px 0";
    [["✅ strong", health.strong, "ok"], ["⚠ weak", health.weak, "chg"], ["⛔ to fix", health.bad, "bad"], ["○ untested", health.untested, "muted"]].forEach(function (x) { var k = el("div", "hk"); k.appendChild(el("b", x[2], String(x[1]))); k.appendChild(el("span", "muted small", x[0])); sum.appendChild(k); });
    box.appendChild(sum);
    (health.lessons || []).forEach(function (l) {
      var d = el("div", "lesson"); d.appendChild(el("span", null, "🎓 You rejected " + l.what + " " + l.count + " times in the agent's examples. "));
      d.appendChild(btn("Teach Nova", true, function () { say(l.teach); })); box.appendChild(d);
    });
    (health.contradictions || []).forEach(function (c) {
      if (!c.ids || !c.ids.length) return;
      var d = el("div", "lesson bad2"); d.appendChild(el("span", null, "⛔ " + c.ids.join(" ↔ ") + ": " + c.why + (c.fix ? " → " + c.fix : "")));
      d.appendChild(btn("🎓 Fix with the trainer", false, function () { say("Fix the contradiction between " + c.ids.join(" and ") + ": " + c.why + (c.fix ? " Suggested: " + c.fix : "")); })); box.appendChild(d);
    });
    var t = el("table"); var hr = el("tr"); ["Rule", "Section", "Strength", "What to fix", ""].forEach(function (x) { hr.appendChild(el("th", null, x)); }); t.appendChild(hr);
    (health.rows || []).forEach(function (r) {
      var tr = el("tr"); var td = el("td"); td.appendChild(el("span", "id", r.id)); td.appendChild(el("div", null, r.title || "")); td.title = r.text; tr.appendChild(td);
      tr.appendChild(el("td", "muted small", r.section)); tr.appendChild(el("td", null, r.badge)); tr.appendChild(el("td", "small", (r.notes || []).join(" · ") || (r.tested ? "passed " + r.tested.when + (r.tested.yes != null ? " · " + r.tested.yes + " ✓" : "") + (r.tested.secs ? " · took " + Thinking.mmss(r.tested.secs) : "") : "")));
      var a = el("td"); if ((r.notes || []).length) a.appendChild(btn("🎓 Improve", false, function () { say("Improve rule " + r.id + " (" + (r.title || "") + "): " + (r.notes || []).join("; ") + ". Propose the better rule."); }));
      else if (!r.tested) a.appendChild(btn("🧪 Test it", false, function () { say("Test rule " + r.id + " as it is: propose it unchanged (op change with the same text) with a good test question."); }));
      tr.appendChild(a); t.appendChild(tr);
    });
    box.appendChild(t); if (window.ColResize) ColResize.attach(t, "brain.health");
    box.appendChild(el("div", "muted small", "checked " + health.at + " · strong = tested and no problem · ⚠ weak = vague, finished project or nothing in 30 days of email · ⛔ = duplicate, contradiction or broken [setting]"));
  }
  $("bClear").onclick = function () { $("msgs2").innerHTML = ""; post({ t: "chatClear" }); addMsg("ai", "✚ New conversation — the rules stay as they are. What should Nova learn?"); };
  $("bHealth").onclick = function () { view = "health"; health = null; drawSecs(); drawMid(); post({ t: "health" }); };
  function lessonsBar(list) {
    var lb = $("lessons"); lb.innerHTML = "";
    (list || []).slice(0, 2).forEach(function (l) { var d = el("div", "lesson"); d.appendChild(el("span", null, "🎓 You rejected " + l.what + " " + l.count + "×. ")); d.appendChild(btn("Teach Nova", true, function () { say(l.teach); d.remove(); })); lb.appendChild(d); });
  }

  $("bBackup").onclick = function () { post({ t: "backupNow" }); };
  $("bVers").onclick = function () { view = "versions"; baks = []; drawSecs(); drawMid(); post({ t: "versions" }); post({ t: "backups" }); };
  $("bLock").onclick = function () { if (confirm("Lock the brain? Nova forgets the password on this PC and uses its built-in rules until you open it again.")) post({ t: "lock" }); };

  if (W) W.addEventListener("message", function (ev) {
    var m = ev.data;
    if (m.t !== "busy" && m.t !== "wstep") $("busy2").textContent = "";
    if (m.t === "state") {
      st = m; drawTop(); drawLock();
      if (st.unlocked) {
        if (!history.length && !$("msgs2").children.length) {
          addMsg("ai", "🎓 Hi — I'm your Brain trainer. I hold " + st.rules + " rules in " + sections().length + " sections. Tell me what Nova should learn: I check your real emails first, ask when something is unclear, and every rule gets a 🧪 TEST before ✓ Apply.\n\nTry: “Why was SLV-ZUC-CW-WIR-15280 marked Closed?” · “For SLV, Omnium reviews before Züblin — teach Nova to track it” · “WIRs are overdue after 3 days”.");
        }
        drawSecs(); drawMid(); if (view === "versions") post({ t: "versions" });
      }
    }
    else if (m.t === "busy") { $("busy2").textContent = m.text; if (window.Thinking) Thinking.badge($("busy2")); }
    else if (m.t === "answer") drawAnswer(m);
    else if (m.t === "work") work(m.title);
    else if (m.t === "wstep") { if (m.step) wkSteps++; if (wk) { if (m.step) wk.step(m.text); else wk.detail(m.text); $("msgs2").scrollTop = 1e9; } }
    else if (m.t === "wdone") { if (wk) wk.done(); wk = null; }
    else if (m.t === "wfail") { if (wk) wk.fail(m.text); else toast("⚠ " + m.text); wk = null; Object.keys(cards).forEach(function (k) { var c = cards[k]; if (c.iv) { clearInterval(c.iv); c.iv = null; if (/testing/.test(c.res.textContent)) c.res.textContent = "⚠ the test stopped after " + Thinking.mmss(Math.round((Date.now() - c.t0) / 1000)) + " — 🧪 test again"; } }); }
    else if (m.t === "test2") drawTest(m);
    else if (m.t === "verdict") drawVerdict(m);
    else if (m.t === "health") { health = m; view = "health"; drawSecs(); drawMid(); }
    else if (m.t === "bfiles") addFiles(m.files);
    else if (m.t === "teach") say(m.text || "", m.files || []);   // 📱 from the phone: the emails as examples
    else if (m.t === "chatLoad") {
      if ((m.chat || []).length) $("msgs2").innerHTML = "";
      (m.chat || []).forEach(function (x) { addMsg(x.role === "user" ? "me" : "ai", x.text); });
      lessonsBar(m.lessons);
    }
    else if (m.t === "versions") { vers = m.list || []; if (view === "versions") drawMid(); }
    else if (m.t === "backups") { baks = m.list || []; if (view === "versions") drawMid(); }
    else if (m.t === "diff") { diffOf = m; drawMid(); }
    else if (m.t === "toast") { toast(m.text); if (m.refresh === "backups") post({ t: "backups" }); if (view === "versions") post({ t: "versions" }); }
    else if (m.t === "error") { toast("⚠ " + m.text); var lb = $("lockBox"); if (!st || !st.unlocked) { var e = lb.querySelector(".small.muted"); if (e) e.textContent = "⚠ " + m.text; } }
  });
  post({ t: "init" });
})();
