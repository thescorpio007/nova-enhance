// 📋 Submittal log page. Designed by A.HAMROUNI
(function () {
  var W = window.chrome && window.chrome.webview;
  function post(o) { if (W) W.postMessage(o); }
  function $(id) { return document.getElementById(id); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  var opt = { projects: [], types: [], from: "", to: "", tower: "", zone: "", disc: "", status: "", lfrom: "", lto: "" };
  var stats = {};
  var allProjects = [], allTypes = [], towers = {}, discs = {}, items = [], show = "", sortKey = "", sortDir = 1;

  function chip(box, label, on, f) { var s = el("span", "chip" + (on ? " on" : ""), label); s.onclick = f; box.appendChild(s); return s; }
  function toggle(list, v) { var i = list.indexOf(v); if (i >= 0) list.splice(i, 1); else list.push(v); }
  function iso(d) { return d.toISOString().slice(0, 10); }

  var prefs = {}, allProjectsFull = [], allTypesFull = [], allStatuses = [];
  function L(k) { return (prefs && prefs[k]) || []; }
  function editChip(box, what) { var e = chip(box, "✎ Edit", false, function () { openEdit(what); }); e.className += " edit"; e.title = "add, hide or order what this row shows"; return e; }
  function selProjects() { return opt.projects.length ? opt.projects : allProjects.map(function (p) { return p.code; }); }
  function placeList(k) {
    var out = [], pl = prefs.places || {};
    selProjects().concat(["*"]).forEach(function (p) { var v = pl[p] && pl[p][k]; if (v) String(v).split(",").forEach(function (x) { x = x.trim(); if (x && out.indexOf(x) < 0) out.push(x); }); });
    return out;
  }

  function drawFilters() {
    var p = $("cProj"); p.innerHTML = "";
    chip(p, "All", !opt.projects.length, function () { opt.projects = []; run(); });
    allProjects.forEach(function (x) {
      var c = chip(p, x.code, opt.projects.indexOf(x.code) >= 0, function () { toggle(opt.projects, x.code); run(); });
      if (x.name) c.appendChild(el("span", "nm", x.name));
    });
    // ✚ New: document prefixes found in your emails that are not a project of yours yet (click = see their items).
    fresh.forEach(function (s) {
      var c = chip(p, "✚ " + s.value + " · new · " + s.emails, opt.projects.indexOf(s.value) >= 0, function () { toggle(opt.projects, s.value); run(); });
      c.className += " new"; c.title = "Found in " + s.emails + " emails, not in your projects yet — ＋ to add or merge it";
    });
    var plus = chip(p, "＋", false, function () { ProjFound.open(); }); plus.className += " plus"; plus.title = "Projects found in your emails";
    editChip(p, "project");
    var t = $("cType"); t.innerHTML = "";
    chip(t, "All", !opt.types.length, function () { opt.types = []; run(); });
    allTypes.forEach(function (x) { var c = chip(t, x, opt.types.indexOf(x) >= 0, function () { toggle(opt.types, x); run(); }); var d = allTypesFull.filter(function (y) { return y.code === x; })[0]; if (d) c.title = d.label; });
    editChip(t, "type");
    var per = $("cPer");
    [].slice.call(per.querySelectorAll(".chip")).forEach(function (c) { c.remove(); });
    var today = new Date(), first = new Date(today.getFullYear(), today.getMonth(), 1);
    var ranges = [["This week", function () { var d = new Date(); d.setDate(d.getDate() - ((d.getDay() + 6) % 7)); return [d, today]; }],
     ["Last 30 days", function () { return [new Date(Date.now() - 30 * 864e5), today]; }],
     ["This month", function () { return [first, today]; }],
     ["Last month", function () { var a = new Date(today.getFullYear(), today.getMonth() - 1, 1); return [a, new Date(today.getFullYear(), today.getMonth(), 0)]; }],
     ["Last 90 days", function () { return [new Date(Date.now() - 90 * 864e5), today]; }]];
    // ✎ Your own periods (“Since project start”, “Q4 2026”…)
    L("periods").forEach(function (x) { ranges.push([x.name, function () { return [new Date(x.from), x.to ? new Date(x.to) : today]; }]); });
    ranges.forEach(function (x) {
      var r = x[1](), on = opt.from === iso(r[0]) && opt.to === iso(r[1]);
      var c = chip(per, x[0], on, function () { opt.from = iso(r[0]); opt.to = iso(r[1]); run(); });
      per.insertBefore(c, $("from"));
    });
    var pe = $("perEdit"); if (!pe) { pe = el("span"); pe.id = "perEdit"; per.appendChild(pe); } pe.innerHTML = ""; editChip(pe, "period");
    $("from").value = opt.from; $("to").value = opt.to;
    var w = $("cWhere"); w.innerHTML = "";
    chip(w, "All towers", !opt.tower, function () { opt.tower = ""; run(); });
    var tw = Object.keys(towers); placeList("towers").forEach(function (x) { if (tw.indexOf(x) < 0) tw.push(x); });
    tw = tw.filter(function (x) { return placeList("hidden").indexOf(x) < 0; });
    tw.sort().forEach(function (x) { chip(w, x, opt.tower === x, function () { opt.tower = opt.tower === x ? "" : x; run(); }); });
    chip(w, "Townhouses", opt.tower === "TH", function () { opt.tower = opt.tower === "TH" ? "" : "TH"; run(); });
    placeList("zones").forEach(function (x) { var zz = x.toUpperCase().replace(/^(\d)/, "Z$1"); chip(w, zz, opt.zone === zz, function () { opt.zone = opt.zone === zz ? "" : zz; run(); }); });
    var z = el("input", "n"); z.placeholder = "Zone"; z.value = opt.zone; z.onchange = function () { opt.zone = z.value.trim().toUpperCase().replace(/^(\d)/, "Z$1"); run(); }; w.appendChild(z);
    w.appendChild(document.createTextNode(" Level"));
    placeList("levels").forEach(function (x) { chip(w, x, opt.lfrom === x && !opt.lto, function () { if (opt.lfrom === x && !opt.lto) { opt.lfrom = ""; } else { opt.lfrom = x; opt.lto = ""; } run(); }); });
    var a = el("input", "n"); a.placeholder = "from"; a.value = opt.lfrom; a.onchange = function () { opt.lfrom = a.value.trim(); run(); }; w.appendChild(a);
    var b = el("input", "n"); b.placeholder = "to"; b.value = opt.lto; b.onchange = function () { opt.lto = b.value.trim(); run(); }; w.appendChild(b);
    editChip(w, "where");
    var d = $("cDisc"); d.innerHTML = "";
    chip(d, "All", !opt.disc, function () { opt.disc = ""; run(); });
    // Groups first (Architecture = AR + ARC + CW + ID + LS …), then each code with its count for the projects, types, period and place chosen.
    L("discGroups").forEach(function (g) {
      var n = 0; (g.codes || []).forEach(function (c) { n += discs[c] || 0; });
      var c = chip(d, "▣ " + g.name + " · " + n.toLocaleString(), opt.disc === g.name, function () { opt.disc = opt.disc === g.name ? "" : g.name; run(); });
      c.title = (g.codes || []).join(" + "); if (!n) c.style.opacity = ".5";
    });
    var hd = L("hiddenDiscs");
    var dk = Object.keys(discs).filter(function (x) { return hd.indexOf(x) < 0; }); if (opt.disc && dk.indexOf(opt.disc) < 0 && !L("discGroups").some(function (g) { return g.name === opt.disc; })) dk.push(opt.disc);
    dk.sort().forEach(function (x) { var n = discs[x] || 0; var c = chip(d, x + " · " + n.toLocaleString(), opt.disc === x, function () { opt.disc = opt.disc === x ? "" : x; run(); }); if (!n) c.style.opacity = ".5"; });
    editChip(d, "disc");
    // Status: its own row, for every search · ✎ choose which chips show and their order.
    var sr = $("cStat"); sr.innerHTML = "";
    chip(sr, "All", !opt.status, function () { opt.status = ""; run(); });
    (L("statuses").length ? L("statuses") : ["Open", "Overdue", "Closed", "Code3/4"]).forEach(function (x) { var n = stats[x]; chip(sr, (x === "Code3/4" ? "Code 3/4" : x) + (n != null ? " · " + n.toLocaleString() : ""), opt.status === x, function () { opt.status = opt.status === x ? "" : x; run(); }); });
    editChip(sr, "status");
    markFrom();
  }

  // ---------------------------------------------------------------- ✎ Edit panels (saved in My log settings)
  var ed = null, pp = null;
  function inp(v, ph, wd) { var i = el("input"); i.value = v || ""; i.placeholder = ph || ""; i.style.width = (wd || 120) + "px"; i.style.padding = "4px 8px"; return i; }
  function btn(t, prim, f) { var b = el("button", prim ? "primary" : null, t); b.onclick = f; return b; }
  function line() { var r = el("div"); r.style.cssText = "display:flex;gap:8px;align-items:center;flex-wrap:wrap;padding:5px 0;border-bottom:1px solid #232836"; return r; }
  function cb(on, f) { var c = el("input"); c.type = "checkbox"; c.checked = !!on; c.style.width = "auto"; c.onchange = function () { f(c.checked); }; return c; }
  function hideToggle(list, v, show) { var i = list.indexOf(v); if (show && i >= 0) list.splice(i, 1); if (!show && i < 0) list.push(v); }
  function openEdit(what) {
    pp = JSON.parse(JSON.stringify(prefs || {}));
    ["hiddenProjects", "projectOrder", "hiddenTypes", "customTypes", "periods", "hiddenDiscs", "discGroups", "statuses"].forEach(function (k) { if (!pp[k]) pp[k] = []; });
    if (!pp.places) pp.places = {};
    if (!ed) { ed = el("div", "edPop"); document.body.appendChild(ed); }
    ed.style.display = "block"; drawEdit(what);
  }
  function drawEdit(what) {
    ed.innerHTML = "";
    var titles = { project: "✎ Projects in the log", type: "✎ Types", period: "✎ Your periods", where: "✎ Towers · zones · levels", disc: "✎ Disciplines and groups", status: "✎ Status chips" };
    var hd = el("div", "row"); hd.appendChild(el("b", null, titles[what])).style.fontSize = "15px"; hd.appendChild(el("span", "grow"));
    var x = el("span", null, "✕"); x.style.cursor = "pointer"; x.onclick = function () { ed.style.display = "none"; }; hd.appendChild(x); ed.appendChild(hd);
    ed.appendChild(el("div", "muted small", "Saved in My log settings · used by the log, the 🧭 Ask box, ⬇ Excel and the phone · shown read-only in 🧠 Nova Brain"));
    var box = el("div"); box.style.cssText = "margin-top:8px;max-height:56vh;overflow:auto"; ed.appendChild(box);
    if (what === "project") {
      var order = allProjectsFull.map(function (p) { return p.code; });
      order.sort(function (a, b) { var i = pp.projectOrder.indexOf(a), j = pp.projectOrder.indexOf(b); return (i < 0 ? 999 : i) - (j < 0 ? 999 : j); });
      order.forEach(function (code, k) {
        var p = allProjectsFull.filter(function (x) { return x.code === code; })[0] || { code: code, name: "" };
        var r = line();
        r.appendChild(cb(pp.hiddenProjects.indexOf(code) < 0, function (on) { hideToggle(pp.hiddenProjects, code, on); }));
        r.appendChild(el("b", null, code)); r.appendChild(el("span", "muted small", p.name || ""));
        r.appendChild(el("span", "grow"));
        r.appendChild(btn("↑", false, function () { if (k > 0) { order.splice(k, 1); order.splice(k - 1, 0, code); pp.projectOrder = order.slice(); drawEdit("project"); } }));
        r.appendChild(btn("↓", false, function () { if (k < order.length - 1) { order.splice(k, 1); order.splice(k + 1, 0, code); pp.projectOrder = order.slice(); drawEdit("project"); } }));
        box.appendChild(r);
      });
      var r2 = line(); r2.appendChild(btn("＋ Projects found in your emails", false, function () { ed.style.display = "none"; ProjFound.open(); }));
      r2.appendChild(btn("✎ Rename · codes · names (📁 Projects)", false, function () { post({ t: "projectsWin" }); })); box.appendChild(r2);
      box.appendChild(el("div", "muted small", "Unticked = hidden: left out of “All”, the log and ⬇ Excel (its emails stay sorted)."));
    }
    if (what === "type") {
      allTypesFull.forEach(function (t) {
        var r = line(); var mine = pp.customTypes.some(function (c) { return (c.code || "").toUpperCase() === t.code; });
        r.appendChild(cb(pp.hiddenTypes.indexOf(t.code) < 0, function (on) { hideToggle(pp.hiddenTypes, t.code, on); }));
        r.appendChild(el("b", null, t.code)); r.appendChild(el("span", "muted small", t.label + (mine ? " · yours" : "")));
        if (mine) { r.appendChild(el("span", "grow")); r.appendChild(btn("🗑", false, function () { pp.customTypes = pp.customTypes.filter(function (c) { return (c.code || "").toUpperCase() !== t.code; }); allTypesFull = allTypesFull.filter(function (y) { return y.code !== t.code; }); drawEdit("type"); })); }
        box.appendChild(r);
      });
      pp.customTypes.filter(function (c) { return !allTypesFull.some(function (t) { return t.code === (c.code || "").toUpperCase(); }); }).forEach(function (c) {
        var r = line(); r.appendChild(el("b", null, (c.code || "").toUpperCase())); r.appendChild(el("span", "muted small", (c.label || "") + " · new, saved with ✓"));
        r.appendChild(el("span", "grow")); r.appendChild(btn("🗑", false, function () { pp.customTypes.splice(pp.customTypes.indexOf(c), 1); drawEdit("type"); })); box.appendChild(r);
      });
      var f = el("div", "card2"); f.style.cssText = "margin-top:10px;padding:10px;background:#1B2030;border-radius:9px";
      f.appendChild(el("b", null, "＋ Your own type"));
      var r1 = line(); var code = inp("", "Code e.g. SNG", 90), label = inp("", "Name e.g. Snag list", 200);
      var find = el("select"); [["code", "the code in the document number (…-SNG-0012)"], ["words", "words in the subject"]].forEach(function (o) { find.appendChild(new Option(o[1], o[0])); });
      r1.appendChild(code); r1.appendChild(label); r1.appendChild(el("span", "muted small", "found by")); r1.appendChild(find); f.appendChild(r1);
      var r3 = line(); var words = inp("", "subject words, comma-separated (e.g. snag, snagging list)", 360); r3.appendChild(words); f.appendChild(r3);
      var r4 = line(); var close = el("select");
      [["code", "closed by " + us + "'s code reply"], ["reply", "closed when the other side replies"], ["anyreply", "closed by any reply"], ["done", "closed by a reply with these words"], ["received", "just received (no reply needed)"]].forEach(function (o) { close.appendChild(new Option(o[1], o[0])); });
      var done = inp("", "closing words e.g. closed, rectified", 220), od = inp("7", "days", 50);
      r4.appendChild(close); r4.appendChild(done); r4.appendChild(el("span", "muted small", "overdue after")); r4.appendChild(od); r4.appendChild(el("span", "muted small", "days")); f.appendChild(r4);
      f.appendChild(btn("＋ Add this type", true, function () {
        var c = code.value.trim().toUpperCase(); if (c.length < 2) { code.focus(); return; }
        pp.customTypes = pp.customTypes.filter(function (x) { return (x.code || "").toUpperCase() !== c; });
        pp.customTypes.push({ code: c, label: label.value.trim() || c, find: find.value, words: words.value.trim(), close: close.value, done: done.value.trim(), overdue: parseInt(od.value, 10) || 0 });
        drawEdit("type");
      }));
      box.appendChild(f);
      box.appendChild(el("div", "muted small", "Unticked = hidden: left out of “All”, the log and ⬇ Excel. Built-in types can be hidden, not deleted."));
    }
    if (what === "period") {
      pp.periods.forEach(function (x, k) {
        var r = line(); r.appendChild(el("b", null, x.name)); r.appendChild(el("span", "muted small", x.from + " → " + (x.to || "today")));
        r.appendChild(el("span", "grow")); r.appendChild(btn("🗑", false, function () { pp.periods.splice(k, 1); drawEdit("period"); })); box.appendChild(r);
      });
      var r5 = line(); var nm = inp("", "Name e.g. Since project start", 200), fr = inp("", "", 140), to = inp("", "", 140); fr.type = "date"; to.type = "date";
      r5.appendChild(nm); r5.appendChild(fr); r5.appendChild(el("span", null, "→")); r5.appendChild(to); r5.appendChild(el("span", "muted small", "(empty = today)"));
      r5.appendChild(btn("＋ Add", true, function () { if (!nm.value.trim() || !fr.value) return; pp.periods.push({ name: nm.value.trim(), from: fr.value, to: to.value }); drawEdit("period"); }));
      box.appendChild(r5);
    }
    if (what === "where") {
      box.appendChild(el("div", "muted small", "Per project (or * for all): comma-separated. Towers and zones become chips; levels become quick chips (GF, L1, Podium, Roof…); hidden towers are not shown as chips."));
      allProjectsFull.map(function (p) { return p.code; }).concat(["*"]).forEach(function (code) {
        var v = pp.places[code] || {};
        var r = line(); r.appendChild(el("b", null, code)).style.minWidth = "70px";
        [["towers", "Towers e.g. T0, T1, T2, T3, T4", 200], ["zones", "Zones e.g. Z1, Z2", 130], ["levels", "Levels e.g. GF, L1, Podium, Roof", 200], ["hidden", "Hide e.g. T0", 110]].forEach(function (f) {
          var i = inp(v[f[0]] || "", f[1], f[2]); i.onchange = function () { pp.places[code] = pp.places[code] || {}; pp.places[code][f[0]] = i.value.trim(); }; r.appendChild(i);
        });
        box.appendChild(r);
      });
    }
    if (what === "disc") {
      box.appendChild(el("b", null, "Groups (a group chip shows the total of its codes)"));
      pp.discGroups.forEach(function (g, k) {
        var r = line(); var n = inp(g.name, "Group name", 160), c = inp((g.codes || []).join(", "), "codes, comma-separated", 320);
        n.onchange = function () { g.name = n.value.trim(); }; c.onchange = function () { g.codes = c.value.split(",").map(function (x) { return x.trim().toUpperCase(); }).filter(Boolean); };
        r.appendChild(n); r.appendChild(c); r.appendChild(btn("🗑", false, function () { pp.discGroups.splice(k, 1); drawEdit("disc"); })); box.appendChild(r);
      });
      box.appendChild(btn("＋ Add a group", false, function () { pp.discGroups.push({ name: "New group", codes: [] }); drawEdit("disc"); }));
      var codes = Object.keys(discs); pp.hiddenDiscs.forEach(function (x) { if (codes.indexOf(x) < 0) codes.push(x); });
      if (codes.length) {
        box.appendChild(el("div", null, " ")); box.appendChild(el("b", null, "Codes (unticked = hidden: left out of the log)"));
        var r6 = line(); codes.sort().forEach(function (x) { var l = el("label", "small"); l.appendChild(cb(pp.hiddenDiscs.indexOf(x) < 0, function (on) { hideToggle(pp.hiddenDiscs, x, on); })); l.appendChild(document.createTextNode(" " + x + "  ")); r6.appendChild(l); });
        box.appendChild(r6);
      }
    }
    if (what === "status") {
      var cur = pp.statuses.length ? pp.statuses.slice() : ["Open", "Overdue", "Closed", "Code3/4"];
      var all = cur.concat(allStatuses.filter(function (s) { return cur.indexOf(s) < 0; }));
      all.forEach(function (s, k) {
        var r = line();
        r.appendChild(cb(cur.indexOf(s) >= 0, function (on) { if (on && cur.indexOf(s) < 0) cur.push(s); if (!on) cur.splice(cur.indexOf(s), 1); pp.statuses = all.filter(function (x) { return cur.indexOf(x) >= 0; }); }));
        r.appendChild(el("b", null, s === "Code3/4" ? "Code 3/4" : s)); r.appendChild(el("span", "grow"));
        r.appendChild(btn("↑", false, function () { if (k > 0) { all.splice(k, 1); all.splice(k - 1, 0, s); pp.statuses = all.filter(function (x) { return cur.indexOf(x) >= 0; }); drawEdit("status"); } }));
        box.appendChild(r);
      });
      pp.statuses = all.filter(function (x) { return cur.indexOf(x) >= 0; });
    }
    var bar = el("div", "row"); bar.style.marginTop = "10px"; bar.appendChild(el("span", "grow"));
    bar.appendChild(btn("✗ Cancel", false, function () { ed.style.display = "none"; }));
    bar.appendChild(btn("✓ Save", true, function () { ed.style.display = "none"; prefs = pp; post({ t: "prefs.save", prefs: pp }); }));
    ed.appendChild(bar);
  }

  // 🔎 Your words → the filter buttons (yellow ring = set from your text) · ✦ Ask Nova · ✕ Clear · “Did you mean” quick options
  var fromText = [], picksOff = [], picksAdd = [], parseTimer = null, lastPicks = [];
  function foldConversation() { if (window.AgentUi && AgentUi.active && AgentUi.active() && AgentUi.fold) AgentUi.fold(); }
  function run() { fromText = []; opt.words = opt.words || []; foldConversation(); $("aiBox").style.display = "none"; drawFilters(); post({ t: "run", opt: opt }); }
  function markFrom() {
    var rows = { proj: "cProj", type: "cType", disc: "cDisc", status: "cStat", period: "cPer", where: "cWhere" };
    Object.keys(rows).forEach(function (k) {
      if (fromText.indexOf(k) < 0) return;
      [].slice.call($(rows[k]).querySelectorAll(".chip.on")).forEach(function (c) { if (!/^All/.test(c.textContent)) c.className += " from"; });
    });
  }
  function drawSugg(picks) {
    var s = $("sugg"); s.innerHTML = ""; lastPicks = picks || [];
    if (!lastPicks.length) return;
    s.appendChild(el("span", "muted small", "Did you mean:"));
    lastPicks.forEach(function (p) {
      var on = p.add ? picksAdd.indexOf(p.key) >= 0 : picksOff.indexOf(p.key) < 0;
      var c = el("span", "sg" + (p.add ? " add" : "") + (on || p.add ? "" : " off"), (p.add ? "" : (on ? "✓ " : "✗ ")) + p.label);
      if (p.why) c.appendChild(el("small", null, "  (" + p.why + ")"));
      c.title = p.add ? "click to add it" : "click to switch it " + (on ? "off" : "on");
      c.onclick = function () {
        var l = p.add ? picksAdd : picksOff, i = l.indexOf(p.key);
        if (i >= 0) l.splice(i, 1); else l.push(p.key);
        textGo("filter");
      };
      s.appendChild(c);
    });
  }
  function textGo(kind) {
    var t = $("ask").value.trim(); if (!t) return;
    foldConversation();
    if (kind === "askai") { var b = $("aiBox"); b.style.display = "block"; b.innerHTML = ""; b.appendChild(el("div", "muted", "✦ Nova is reading the rows…")); }
    else $("aiBox").style.display = "none";
    post({ t: kind, text: t, off: picksOff, add: picksAdd });
  }
  $("ask").addEventListener("input", function () {
    picksOff = []; picksAdd = [];
    clearTimeout(parseTimer);
    var t = $("ask").value.trim();
    if (!t) { drawSugg([]); return; }
    parseTimer = setTimeout(function () { post({ t: "parse", text: t, off: [], add: [] }); }, 350);
  });
  $("aiBtn").onclick = function () { textGo("askai"); };
  $("clrBtn").onclick = function () { $("ask").value = ""; picksOff = []; picksAdd = []; drawSugg([]); opt.words = []; run(); };
  function aiAnswer(m) {
    var b = $("aiBox"); b.style.display = "block"; b.innerHTML = "";
    var h = el("div"); h.appendChild(el("b", null, "✦ Nova" + (m.by ? " (" + m.by + (m.brain ? " + 🧠 Brain v" + m.brain : "") + ")" : "")));
    if (m.rows != null) h.appendChild(el("span", "muted small", "  answered from the " + m.rows + " row(s) of the table"));
    var x = el("span", "muted small", "   ✕"); x.style.cursor = "pointer"; x.onclick = function () { b.style.display = "none"; }; h.appendChild(x);
    b.appendChild(h);
    b.appendChild(el("div", "ans", m.answer || ""));
    if ((m.used || []).length) b.appendChild(el("div", "muted small", "🧠 used: " + m.used.join(" · ")));
    var acts = el("div", "chips"); acts.style.marginTop = "6px";
    (m.next || []).forEach(function (n) { var c = el("span", "sg", n); c.onclick = function () { $("ask").value = n; picksOff = []; picksAdd = []; textGo("askai"); }; acts.appendChild(c); });
    var ag = el("span", "sg add", "🧭 Search the emails (agent)"); ag.title = "for who / why / drafts: the Nova agent reads the emails and threads";
    ag.onclick = function () { var q = $("ask").value.trim() || ""; if (q) AgentUi.start(q); }; acts.appendChild(ag);
    b.appendChild(acts);
  }
  $("from").onchange = function () { opt.from = $("from").value; run(); };
  $("to").onchange = function () { opt.to = $("to").value; run(); };

  function drawKpi(k) {
    var box = $("kpi"); box.innerHTML = "";
    function one(n, label, color, f) { var d = el("div", "k"); var b = el("b", null, n == null ? "–" : String(n)); if (color) b.style.color = color; d.appendChild(b); d.appendChild(el("span", null, label)); d.onclick = function () { show = f; drawTable(); }; box.appendChild(d); }
    one(k.found, "found", null, "");
    one(k.closed, "closed" + (builtin ? " by " + us : ""), "#3DDC97", "Closed");
    one(k.open, "open", "#FFB547", "Open");
    one(k.overdue, "overdue", "#FF6B6B", "Overdue");
    one(k.avg == null ? null : k.avg + " d", "average " + (builtin ? us + " " : "") + "reply", null, "");
    one(k.check, "to check", "#9AA3B5", "check");
    var s = el("div", "muted small", k.mails + " emails read · " + k.secs + " s"); s.style.alignSelf = "center"; box.appendChild(s);
  }

  var ALL_COLS = [["proj", "Proj"], ["type", "Type"], ["no", "Document no."], ["rev", "Rev"], ["disc", "Disc."], ["title", "Title"], ["tower", "Tower"], ["zone", "Zone"], ["level", "Level"], ["apt", "Apt / TH"], ["sub", "Submitted"], ["due", "Deadline"], ["reply", "MZA reply"], ["code", "Code"], ["days", "Days"], ["status", "Status"]];
  var COLS = ALL_COLS, builtin = true, us = "MZA", fresh = [];
  function setCols() {
    COLS = ALL_COLS.filter(function (c) { return builtin || ["rev", "disc", "tower", "zone", "level", "apt", "code"].indexOf(c[0]) < 0; })
      .map(function (c) { return c[0] === "reply" ? ["reply", builtin ? us + " reply" : "Reply"] : c[0] === "no" && !builtin ? ["no", "Reference"] : c; });
    ["lWhere", "cWhere"].forEach(function (id) { $(id).style.display = builtin ? "" : "none"; });
  }
  function drawTable() {
    var box = $("tbl"); box.innerHTML = "";
    var t = el("table"), hr = el("tr");
    var hid = ColResize.hidden("submittal");
    var cols = COLS.filter(function (c) { return hid.indexOf(c[0]) < 0; });
    cols.forEach(function (c) { var th = el("th", null, c[1] + (sortKey === c[0] ? (sortDir > 0 ? " ▲" : " ▼") : "")); th.onclick = function () { sortDir = sortKey === c[0] ? -sortDir : 1; sortKey = c[0]; drawTable(); }; hr.appendChild(th); });
    t.appendChild(hr);
    var l = items.filter(function (i) { return !show || (show === "check" ? i.check : show === "Open" ? (i.status === "Open" || i.status === "Reviewing" || i.status === "Replied" || i.status === "Draft") : show === "Closed" ? (i.status === "Closed" || i.status === "Received") : i.status === show); });
    if (sortKey) l = l.slice().sort(function (a, b) { var x = a[sortKey], y = b[sortKey]; if (sortKey === "sub" || sortKey === "reply" || sortKey === "due") { x = Date.parse(x) || 0; y = Date.parse(y) || 0; } return (x > y ? 1 : x < y ? -1 : 0) * sortDir; });
    l.forEach(function (i) {
      var tr = el("tr", "r");
      cols.forEach(function (c) {
        var v = i[c[0]]; var td = el("td", c[0] === "no" ? "mono" : c[0] === "title" ? "tt wrap" : null);
        if (c[0] === "status") { td.appendChild(el("span", "st " + (v === "?" ? "Superseded" : v), v === "?" ? "check" : v)); if (i.check) td.appendChild(el("span", "chk", " ⚠")); }
        else td.textContent = v == null || v === "" ? "–" : v;
        if (c[0] === "title") td.title = i.title + "\n" + i.why + (i.check ? "\n⚠ " + i.check : "");
        if (c[0] === "type") td.title = i.label || "";
        tr.appendChild(td);
      });
      tr.onclick = function (ev) { if (i.entry) post({ t: "open", entry: i.entry, store: i.store, ctrl: !!(ev && ev.ctrlKey) }); };
      if (i.subEntry || i.entry) { tr.setAttribute("data-entry", i.subEntry || i.entry); tr.setAttribute("data-store", i.subEntry ? i.subStore : i.store); }
      t.appendChild(tr);
    });
    box.appendChild(t);
    // ↔ drag a column edge to widen it, double-click it to fit the text (remembered)
    ColResize.attach(t, "submittal", cols.map(function (c) { return c[0]; }));
    if (!l.length) box.appendChild(el("div", "muted", "  Nothing found for these choices."));
  }

  $("xl").onclick = function () { post({ t: "excel" }); };
  // ▦ Columns: show or hide columns, ↺ reset the widths.
  $("colsBtn").onclick = function () {
    var pop = $("colsPop"); if (pop.style.display === "block") { pop.style.display = "none"; return; }
    pop.innerHTML = ""; var hid = ColResize.hidden("submittal");
    COLS.forEach(function (c) {
      var l = el("label", "small"); var x = el("input"); x.type = "checkbox"; x.style.width = "auto"; x.checked = hid.indexOf(c[0]) < 0;
      x.onchange = function () { var h = ColResize.hidden("submittal").filter(function (y) { return y !== c[0]; }); if (!x.checked) h.push(c[0]); ColResize.setHidden("submittal", h); drawTable(); };
      l.appendChild(x); l.appendChild(document.createTextNode(" " + c[1])); pop.appendChild(l);
    });
    var r = el("button", null, "↺ Reset the widths"); r.onclick = function () { ColResize.reset("submittal"); drawTable(); }; pop.appendChild(r);
    pop.style.display = "block";
  };
  $("fold").onclick = function () { post({ t: "folder" }); };
  $("upd").onclick = run;
  $("brain").onclick = function () { post({ t: "brain" }); };
  // Enter = 🔎 filter (no AI) · Ctrl+Enter = ✦ Ask Nova · “ask AI: …” still opens the 🧭 agent (emails and threads)
  function ask(ai) {
    var t = $("ask").value.trim(); if (!t) return;
    if (/^\s*(✦|ask ai\s*:?)/i.test(t)) { $("ask").value = ""; post({ t: "ask", text: t }); return; }
    textGo(ai ? "askai" : "filter");
  }
  $("askBtn").onclick = function () { ask(false); };
  $("ask").onkeydown = function (e) { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); ask(e.ctrlKey); } };

  // ● ● ● “Nova is thinking” while the log is read (dots, timer, ■ Stop, what Nova waits for)
  var bth = null;
  function busy(t, working) {
    var b = $("busy");
    if (working && window.Thinking) {
      if (!bth || !bth.active()) { b.innerHTML = ""; bth = Thinking.start(b, t, { onStop: function () { post({ t: "stop" }); } }); }
      else bth.title(t);
      return;
    }
    if (bth && bth.active()) bth.done(); bth = null;
    b.innerHTML = ""; b.textContent = t || "";
  }

  if (W) W.addEventListener("message", function (ev) {
    var m = ev.data;
    if (AgentUi.handle(m)) return;
    if (m.t === "ag.start") { AgentUi.start(m.text); return; }
    if (ProjFound.handle(m)) {
      fresh = ProjFound.items().filter(function (s) { return s.kind === "prefix"; }).slice(0, 3);
      if (m.said && !/^⚠/.test(m.said)) { post({ t: "setup" }); run(); } else drawFilters();
      return;
    }
    if (m.t === "setup") {
      allProjects = m.projects || []; allTypes = m.types || []; opt = m.opt;
      prefs = m.prefs || {}; allProjectsFull = m.allProjects || allProjects; allTypesFull = m.allTypes || []; allStatuses = m.allStatuses || []; $("sign").textContent = m.sign || ""; $("brain").textContent = m.brain;
      builtin = m.builtin !== false; us = m.us || "MZA";
      $("ttl").textContent = "📋 " + (m.name || "Submittal log");
      if (!builtin) $("sub").textContent = "every item of your field found in your emails (" + (m.field || "") + ") · checks the reply on the same thread";
      else if (us !== "MZA") $("sub").textContent = "every submission found in Outlook · Thinkproject or direct email · checks if " + us + " replied on the same thread";
      setCols(); drawFilters();
    }
    else if (m.t === "busy") { if (/^[✓⚠]|^⚡ \d/.test(m.text)) busy(m.text); else busy(m.text, true); }
    else if (m.t === "opt") { opt = m.opt; fromText = m.opt.fromText || []; drawFilters(); if (m.said) busy("🧭 " + m.said + " …", true); }
    else if (m.t === "parsed") { if (($("ask").value.trim() || "") === (m.text || "")) drawSugg(m.picks); }
    else if (m.t === "aithinking") { var ab = $("aiBox"); ab.style.display = "block"; ab.innerHTML = ""; ab.appendChild(el("div", "muted", "✦ Nova is reading the " + (items.length || 0) + " rows with your 🧠 Brain…")); }
    else if (m.t === "aians") aiAnswer(m);
    else if (m.t === "result") {
      var sh = $("showing");
      if (sh) {
        sh.innerHTML = "";
        if (m.showing) {
          sh.style.display = "block"; sh.appendChild(document.createTextNode(m.showing + "   "));
          var x = document.createElement("a"); x.textContent = "✕ show everything"; x.style.cssText = "cursor:pointer;color:#b9adff;margin-left:8px";
          x.title = "every type, discipline, status and place again (your projects and period stay)";
          x.onclick = function () { opt.words = []; opt.types = []; opt.disc = ""; opt.status = ""; opt.tower = ""; opt.zone = ""; opt.lfrom = ""; opt.lto = ""; $("ask").value = ""; drawSugg([]); run(); }; sh.appendChild(x);
        } else sh.style.display = "none";
      }
      busy(""); items = m.items || [];
      (m.towers || []).forEach(function (x) { towers[x] = 1; }); discs = m.discCounts || {}; stats = m.statCounts || {}; (m.projects || []).forEach(function (x) { if (!allProjects.some(function (p) { return p.code === x; }) && !fresh.some(function (s) { return s.value === x; })) allProjects.push({ code: x, name: "" }); });
      show = ""; drawFilters(); drawKpi(m.kpi); drawTable();
      $("gaps").textContent = (m.kpi.gaps || []).length ? "⚠ Missing in the register: " + m.kpi.gaps.join(" · ") : "";
      if (m.said) busy(m.said);   // ⚡ found on this PC (no AI)
    }
    else if (m.t === "saved") busy("✓ Saved and opened: " + m.path);
    else if (m.t === "error") busy("⚠ " + m.text);
  });
  post({ t: "init" });
})();
