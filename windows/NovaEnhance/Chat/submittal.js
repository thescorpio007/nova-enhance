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
    var t = $("cType"); t.innerHTML = "";
    chip(t, "All", !opt.types.length, function () { opt.types = []; run(); });
    allTypes.forEach(function (x) { chip(t, x, opt.types.indexOf(x) >= 0, function () { toggle(opt.types, x); run(); }); });
    var per = $("cPer");
    [].slice.call(per.querySelectorAll(".chip")).forEach(function (c) { c.remove(); });
    var today = new Date(), first = new Date(today.getFullYear(), today.getMonth(), 1);
    [["This week", function () { var d = new Date(); d.setDate(d.getDate() - ((d.getDay() + 6) % 7)); return [d, today]; }],
     ["Last 30 days", function () { return [new Date(Date.now() - 30 * 864e5), today]; }],
     ["This month", function () { return [first, today]; }],
     ["Last month", function () { var a = new Date(today.getFullYear(), today.getMonth() - 1, 1); return [a, new Date(today.getFullYear(), today.getMonth(), 0)]; }],
     ["Last 90 days", function () { return [new Date(Date.now() - 90 * 864e5), today]; }]].forEach(function (x) {
      var r = x[1](), on = opt.from === iso(r[0]) && opt.to === iso(r[1]);
      var c = chip(per, x[0], on, function () { opt.from = iso(r[0]); opt.to = iso(r[1]); run(); });
      per.insertBefore(c, $("from"));
    });
    $("from").value = opt.from; $("to").value = opt.to;
    var w = $("cWhere"); w.innerHTML = "";
    chip(w, "All towers", !opt.tower, function () { opt.tower = ""; run(); });
    Object.keys(towers).sort().forEach(function (x) { chip(w, x, opt.tower === x, function () { opt.tower = opt.tower === x ? "" : x; run(); }); });
    chip(w, "Townhouses", opt.tower === "TH", function () { opt.tower = opt.tower === "TH" ? "" : "TH"; run(); });
    var z = el("input", "n"); z.placeholder = "Zone"; z.value = opt.zone; z.onchange = function () { opt.zone = z.value.trim().toUpperCase().replace(/^(\d)/, "Z$1"); run(); }; w.appendChild(z);
    w.appendChild(document.createTextNode(" Level"));
    var a = el("input", "n"); a.placeholder = "from"; a.value = opt.lfrom; a.onchange = function () { opt.lfrom = a.value.trim(); run(); }; w.appendChild(a);
    var b = el("input", "n"); b.placeholder = "to"; b.value = opt.lto; b.onchange = function () { opt.lto = b.value.trim(); run(); }; w.appendChild(b);
    var d = $("cDisc"); d.innerHTML = "";
    chip(d, "All", !opt.disc, function () { opt.disc = ""; run(); });
    // Each discipline with its count for the projects, types, period and place chosen (a discipline the project does not use shows 0).
    var dk = Object.keys(discs); if (opt.disc && dk.indexOf(opt.disc) < 0) dk.push(opt.disc);
    dk.sort().forEach(function (x) { var n = discs[x] || 0; var c = chip(d, x + " · " + n.toLocaleString(), opt.disc === x, function () { opt.disc = opt.disc === x ? "" : x; run(); }); if (!n) c.style.opacity = ".5"; });
    // Status: its own row, for every search.
    var sr = $("cStat"); sr.innerHTML = "";
    chip(sr, "All", !opt.status, function () { opt.status = ""; run(); });
    ["Open", "Overdue", "Closed", "Code3/4"].forEach(function (x) { var n = stats[x]; chip(sr, (x === "Code3/4" ? "Code 3/4" : x) + (n != null ? " · " + n.toLocaleString() : ""), opt.status === x, function () { opt.status = opt.status === x ? "" : x; run(); }); });
  }

  function run() { drawFilters(); post({ t: "run", opt: opt }); }
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
    COLS.forEach(function (c) { var th = el("th", null, c[1] + (sortKey === c[0] ? (sortDir > 0 ? " ▲" : " ▼") : "")); th.onclick = function () { sortDir = sortKey === c[0] ? -sortDir : 1; sortKey = c[0]; drawTable(); }; hr.appendChild(th); });
    t.appendChild(hr);
    var l = items.filter(function (i) { return !show || (show === "check" ? i.check : show === "Open" ? (i.status === "Open" || i.status === "Reviewing" || i.status === "Replied" || i.status === "Draft") : show === "Closed" ? (i.status === "Closed" || i.status === "Received") : i.status === show); });
    if (sortKey) l = l.slice().sort(function (a, b) { var x = a[sortKey], y = b[sortKey]; if (sortKey === "sub" || sortKey === "reply" || sortKey === "due") { x = Date.parse(x) || 0; y = Date.parse(y) || 0; } return (x > y ? 1 : x < y ? -1 : 0) * sortDir; });
    l.forEach(function (i) {
      var tr = el("tr", "r");
      COLS.forEach(function (c) {
        var v = i[c[0]]; var td = el("td", c[0] === "no" ? "mono" : c[0] === "title" ? "tt" : null);
        if (c[0] === "status") { td.appendChild(el("span", "st " + (v === "?" ? "Superseded" : v), v === "?" ? "check" : v)); if (i.check) td.appendChild(el("span", "chk", " ⚠")); }
        else td.textContent = v == null || v === "" ? "–" : v;
        if (c[0] === "title") td.title = i.title + "\n" + i.why + (i.check ? "\n⚠ " + i.check : "");
        if (c[0] === "type") td.title = i.label || "";
        tr.appendChild(td);
      });
      tr.onclick = function () { if (i.entry) post({ t: "open", entry: i.entry, store: i.store }); };
      t.appendChild(tr);
    });
    box.appendChild(t);
    if (!l.length) box.appendChild(el("div", "muted", "  Nothing found for these choices."));
  }

  $("xl").onclick = function () { post({ t: "excel" }); };
  $("fold").onclick = function () { post({ t: "folder" }); };
  $("upd").onclick = run;
  $("brain").onclick = function () { post({ t: "brain" }); };
  function ask() { var t = $("ask").value.trim(); if (!t) return; post({ t: "ask", text: t }); $("ask").value = ""; }
  $("askBtn").onclick = ask;
  $("ask").onkeydown = function (e) { if (e.key === "Enter") ask(); };

  if (W) W.addEventListener("message", function (ev) {
    var m = ev.data;
    if (ProjFound.handle(m)) {
      fresh = ProjFound.items().filter(function (s) { return s.kind === "prefix"; }).slice(0, 3);
      if (m.projects && m.projects.length) allProjects = m.projects;
      if (m.said && !/^⚠/.test(m.said)) run(); else drawFilters();
      return;
    }
    if (m.t === "setup") {
      allProjects = m.projects || []; allTypes = m.types || []; opt = m.opt; $("sign").textContent = m.sign || ""; $("brain").textContent = m.brain;
      builtin = m.builtin !== false; us = m.us || "MZA";
      $("ttl").textContent = "📋 " + (m.name || "Submittal log");
      if (!builtin) $("sub").textContent = "every item of your field found in your emails (" + (m.field || "") + ") · checks the reply on the same thread";
      else if (us !== "MZA") $("sub").textContent = "every submission found in Outlook · Thinkproject or direct email · checks if " + us + " replied on the same thread";
      setCols(); drawFilters();
    }
    else if (m.t === "busy") { $("busy").textContent = "⏳ " + m.text; }
    else if (m.t === "opt") { opt = m.opt; drawFilters(); if (m.said) $("busy").textContent = "🧭 " + m.said; }
    else if (m.t === "result") {
      $("busy").textContent = ""; items = m.items || [];
      (m.towers || []).forEach(function (x) { towers[x] = 1; }); discs = m.discCounts || {}; stats = m.statCounts || {}; (m.projects || []).forEach(function (x) { if (!allProjects.some(function (p) { return p.code === x; }) && !fresh.some(function (s) { return s.value === x; })) allProjects.push({ code: x, name: "" }); });
      show = ""; drawFilters(); drawKpi(m.kpi); drawTable();
      $("gaps").textContent = (m.kpi.gaps || []).length ? "⚠ Missing in the register: " + m.kpi.gaps.join(" · ") : "";
    }
    else if (m.t === "saved") $("busy").textContent = "✓ Saved and opened: " + m.path;
    else if (m.t === "error") $("busy").textContent = "⚠ " + m.text;
  });
  post({ t: "init" });
})();
