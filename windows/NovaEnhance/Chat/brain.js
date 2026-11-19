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
    var box = $("mid"); box.innerHTML = "";
    var s = sections().filter(function (x) { return x.id === cur; })[0] || sections()[0];
    if (!s) return;
    cur = s.id;
    var h = el("div", "row");
    h.appendChild(el("b", null, (s.icon ? s.icon + " " : "") + s.name)).style.fontSize = "16px";
    h.appendChild(el("span", "muted small", "used for: " + (s.tags || []).join(" · ")));
    h.appendChild(el("span", "grow"));
    if (!editing) h.appendChild(btn("✎ Edit section", false, function () { editing = true; drawMid(); }));
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
        d.appendChild(c2); d.appendChild(el("span"));
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
    box.appendChild(t);
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
    box.appendChild(t2);
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

  // ---------------------------------------------------------------- 💬 chat
  function addMsg(cls, text) { var m = el("div", "msg " + cls, text); $("msgs2").appendChild(m); $("msgs2").scrollTop = 1e9; return m; }
  function send() {
    var t = $("ask").value.trim(); if (!t) return;
    addMsg("me", t); history.push({ role: "user", text: t }); $("ask").value = "";
    post({ t: "ask", text: t, history: history.slice(0, -1) });
  }
  $("send").onclick = send;
  $("ask").onkeydown = function (e) { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); } };

  function drawAnswer(m) {
    var a = addMsg("ai", m.reply);
    history.push({ role: "brain", text: m.reply });
    if (m.conflict) a.appendChild(el("div", "chg", "⚠ " + m.conflict));
    if (!m.changes || !m.changes.length) return;
    a.appendChild(diffBox(m.diff));
    var res = el("div", "small"); a.appendChild(res);
    var bar = el("div", "row"); bar.style.marginTop = "6px";
    bar.appendChild(btn("✓ Apply", true, function () { post({ t: "apply", changes: m.changes, summary: "" }); bar.remove(); res.textContent = "✓ applied"; }));
    bar.appendChild(btn("✎ Edit", false, function () {
      bar.remove();
      var ed = el("div", "edit");
      m.changes.forEach(function (c) { var tx = el("textarea"); tx.value = c.text || ""; tx.oninput = function () { c.text = tx.value; }; ed.appendChild(el("div", "id", (c.op || "") + " " + (c.id || "") + " · " + (c.section || ""))); if (c.op !== "remove") ed.appendChild(tx); });
      ed.appendChild(btn("✓ Apply as edited", true, function () { post({ t: "apply", changes: m.changes }); ed.remove(); res.textContent = "✓ applied"; }));
      a.appendChild(ed);
    }));
    if (m.testable) {
      bar.appendChild(btn("🧪 Test (30 days)", false, function () { pendingTest = res; post({ t: "test", changes: m.changes, days: 30 }); }));
      bar.appendChild(btn("90 days", false, function () { pendingTest = res; post({ t: "test", changes: m.changes, days: 90 }); }));
    }
    bar.appendChild(btn("✗ Cancel", false, function () { bar.remove(); res.textContent = "cancelled — nothing changed"; }));
    a.appendChild(bar);
    a.appendChild(el("div", "muted small", "Applying saves a new version first — ↩ undo anytime in Versions."));
  }
  var pendingTest = null;
  function drawTest(m) {
    var box = pendingTest || addMsg("ai", ""); box.innerHTML = "";
    box.appendChild(el("div", null, "🧪 Last " + m.days + " days (" + m.total + " items): " + m.changed + " change · " + m.toClosed + " become Closed · " + m.fromClosed + " stop being Closed."));
    if (m.rows.length) {
      var t = el("table"); var hr = el("tr"); ["Document", "Now", "With the new rule"].forEach(function (x) { hr.appendChild(el("th", null, x)); }); t.appendChild(hr);
      m.rows.forEach(function (r) { var tr = el("tr"); tr.title = r.why; tr.appendChild(el("td", "id", r.no)); tr.appendChild(el("td", null, r.now)); tr.appendChild(el("td", null, r.then)); t.appendChild(tr); });
      box.appendChild(t);
    }
  }

  $("bBackup").onclick = function () { post({ t: "backupNow" }); };
  $("bVers").onclick = function () { view = "versions"; baks = []; drawSecs(); drawMid(); post({ t: "versions" }); post({ t: "backups" }); };
  $("bLock").onclick = function () { if (confirm("Lock the brain? Nova forgets the password on this PC and uses its built-in rules until you open it again.")) post({ t: "lock" }); };

  if (W) W.addEventListener("message", function (ev) {
    var m = ev.data;
    if (m.t !== "busy") $("busy2").textContent = "";
    if (m.t === "state") {
      st = m; drawTop(); drawLock();
      if (st.unlocked) {
        if (!history.length && !$("msgs2").children.length) {
          addMsg("ai", "Hi — I hold " + st.rules + " rules in " + sections().length + " sections. Ask me why Nova did something, or give me a new instruction and I'll show you exactly what changes.\n\nTry: “Why was SLV-ZUC-CW-WIR-15280 marked Closed?” · “For RS5, a reply with -signed.pdf means closed” · “WIRs are overdue after 3 days”.");
        }
        drawSecs(); drawMid(); if (view === "versions") post({ t: "versions" });
      }
    }
    else if (m.t === "busy") $("busy2").textContent = "⏳ " + m.text;
    else if (m.t === "answer") drawAnswer(m);
    else if (m.t === "test") drawTest(m);
    else if (m.t === "versions") { vers = m.list || []; if (view === "versions") drawMid(); }
    else if (m.t === "backups") { baks = m.list || []; if (view === "versions") drawMid(); }
    else if (m.t === "diff") { diffOf = m; drawMid(); }
    else if (m.t === "toast") { toast(m.text); if (m.refresh === "backups") post({ t: "backups" }); if (view === "versions") post({ t: "versions" }); }
    else if (m.t === "error") { toast("⚠ " + m.text); var lb = $("lockBox"); if (!st || !st.unlocked) { var e = lb.querySelector(".small.muted"); if (e) e.textContent = "⚠ " + m.text; } }
  });
  post({ t: "init" });
})();
