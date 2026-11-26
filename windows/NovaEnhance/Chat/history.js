// 🕘 Search history page. Designed by A.HAMROUNI
(function () {
  var W = window.chrome && window.chrome.webview;
  function post(o) { if (W) W.postMessage(o); }
  function $(id) { return document.getElementById(id); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  function btn(t, primary, f) { var b = el("button", primary ? "primary" : null, t); b.onclick = function (ev) { ev.stopPropagation(); f(); }; return b; }

  var items = [], kind = "", days = 60;
  var KINDS = [["", "All"], ["deep", "🔎 Deep search"], ["chat", "✦ Nova Chat"], ["folder", "📂 Folder search"], ["email", "✉ Email search"], ["interrupted", "⚠ Interrupted"]];
  var ICON = { deep: "🔎", chat: "✦", folder: "📂", email: "✉" };
  function match(x, k) { return !k || (k === "interrupted" ? (x.status === "interrupted" || x.status === "stopped") && x.kind === "deep" : x.kind === k); }
  function pad(n) { return (n < 10 ? "0" : "") + n; }
  var DAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"], MON = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  function dayOf(ts) {
    var d = new Date(ts), t = new Date(), y = new Date(Date.now() - 864e5);
    var s = DAYS[d.getDay()] + " " + d.getDate() + " " + MON[d.getMonth()] + (d.getFullYear() !== t.getFullYear() ? " " + d.getFullYear() : "");
    if (d.toDateString() === t.toDateString()) return "Today · " + s;
    if (d.toDateString() === y.toDateString()) return "Yesterday · " + s;
    return s;
  }
  function status(x) {
    if (x.status === "done") return ["ok", "✓ finished"];
    if (x.status === "running") return ["warn", "⏳ running now"];
    if (x.status === "stopped") return ["stop", "■ stopped — what was found is kept"];
    if (x.status === "interrupted") return ["warn", "⚠ interrupted (Nova closed) — what was found is kept"];
    return ["", x.status];
  }

  function drawChips() {
    var c = $("chips"); c.innerHTML = "";
    KINDS.forEach(function (k) {
      var n = items.filter(function (x) { return match(x, k[0]); }).length;
      if (k[0] && !n) return;
      var s = el("span", "chip" + (kind === k[0] ? " on" : ""), k[1] + " " + n);
      s.onclick = function () { kind = kind === k[0] ? "" : k[0]; drawChips(); drawList(); };
      c.appendChild(s);
    });
  }

  function drawList() {
    $("detail").className = "hide"; $("list").className = ""; $("top").className = "";
    var box = $("list"); box.innerHTML = "";
    var words = $("find").value.toLowerCase().split(/\s+/).filter(Boolean);
    var l = items.filter(function (x) { return match(x, kind) && words.every(function (w) { return (x.find || "").indexOf(w) >= 0; }); });
    if (!l.length) box.appendChild(el("div", "muted", items.length ? "Nothing matches." : "Nothing yet — every search you make is saved here by itself, with its date, subject and answer."));
    var last = "";
    l.slice(0, 500).forEach(function (x) {
      var d = dayOf(x.ts);
      if (d !== last) { box.appendChild(el("div", "grp", d)); last = d; }
      var it = el("div", "it"), t = new Date(x.ts);
      it.appendChild(el("span", "t", pad(t.getHours()) + ":" + pad(t.getMinutes())));
      it.appendChild(el("span", "s", (ICON[x.kind] || "•") + " " + x.subject));
      var st = status(x), m = el("span", "m");
      m.appendChild(el("span", st[0], st[1]));
      m.appendChild(document.createTextNode(" · " + x.n + " source(s)" + (x.note ? " · " + x.note : "")));
      it.appendChild(m);
      var r = el("span", "r");
      if (x.kind === "deep" && x.status !== "done" && x.status !== "running") r.appendChild(btn("▶ Continue", true, function () { post({ t: "cont", id: x.id }); }));
      r.appendChild(btn("Open", !(x.kind === "deep" && x.status !== "done"), function () { post({ t: "open", id: x.id }); }));
      r.appendChild(btn("🗑", false, function () { post({ t: "del", id: x.id }); }));
      it.appendChild(r);
      it.onclick = function () { post({ t: "open", id: x.id }); };
      box.appendChild(it);
    });
    drawFoot();
  }

  function drawFoot() {
    var f = $("foot"); f.innerHTML = "";
    f.appendChild(document.createTextNode("Keep history for"));
    [[60, "60 days"], [90, "90 days"], [180, "180 days"], [0, "Always"]].forEach(function (d) {
      var s = el("span", "chip" + (days === d[0] ? " on" : ""), d[1]);
      s.onclick = function () { post({ t: "days", d: d[0] }); };
      f.appendChild(s);
    });
    f.appendChild(document.createTextNode("· older ones are removed by themselves · 🔒 stays on this PC"));
    f.appendChild(el("span", "grow"));
    if (items.length) f.appendChild(btn("🗑 Clear…", false, function () { post({ t: "clear" }); }));
    f.appendChild(el("span", "sign", sign));
  }

  // Light formatting: **bold**, tables, [n] citations that open the source.
  function render(box, text, sources) {
    var lines = (text || "").split("\n"), tbl = null;
    function flush() {
      if (!tbl) return;
      var rows = tbl.filter(function (l) { return !/^\|?\s*:?-{2,}/.test(l.trim()); }).map(function (l) {
        return l.trim().replace(/^\||\|$/g, "").split("|").map(function (c) { return c.trim().replace(/\*\*(.+?)\*\*/g, "$1"); });
      });
      tbl = null;
      if (rows.length < 2) return;
      var wrap = el("div", "tblwrap"), t = el("table", "cmp");
      rows.forEach(function (r, ri) {
        var tr = el("tr");
        for (var i = 0; i < rows[0].length; i++) tr.appendChild(el(ri === 0 ? "th" : "td", null, r[i] || ""));
        t.appendChild(tr);
      });
      wrap.appendChild(t); box.appendChild(wrap);
    }
    lines.forEach(function (line, li) {
      if (/^\s*\|.*\|/.test(line)) { (tbl = tbl || []).push(line); return; }
      flush();
      line = line.replace(/^#{1,4}\s+/, "").replace(/^\s*[-*]\s+/, "• ");
      line.split(/(\*\*[^*]+\*\*|\[\d+(?:\s*[,;]\s*\d+)*\])/).forEach(function (p) {
        if (/^\*\*[^*]+\*\*$/.test(p)) box.appendChild(el("b", null, p.slice(2, -2)));
        else if (/^\[\d/.test(p)) {
          var ns = p.slice(1, -1).split(/[,;]/);
          ns.forEach(function (n, k) {
            var i = parseInt(n, 10) - 1, c = el("span", "cite", (k ? "" : "[") + (i + 1) + (k < ns.length - 1 ? "," : "]"));
            if (sources[i]) { c.title = sources[i].label; c.onclick = function () { post({ t: "src", src: sources[i] }); }; }
            box.appendChild(c);
          });
        } else box.appendChild(document.createTextNode(p));
      });
      if (li < lines.length - 1) box.appendChild(document.createTextNode("\n"));
    });
    flush();
  }

  function drawEntry(e) {
    $("list").className = "hide"; $("top").className = "hide";
    var d = $("detail"); d.className = ""; d.innerHTML = "";
    var bar = el("div", "row");
    bar.appendChild(btn("← All searches", false, drawList));
    bar.appendChild(el("span", "grow"));
    if (e.kind === "deep" && e.status !== "done" && e.status !== "running") bar.appendChild(btn("▶ Continue the search", true, function () { post({ t: "cont", id: e.id }); }));
    if (e.chatId && (e.kind === "chat" || e.status === "done")) bar.appendChild(btn("✦ Open in Nova Chat", false, function () { post({ t: "chat", chat: e.chatId }); }));
    if (e.folder) bar.appendChild(btn("📂 Search this folder again", false, function () { post({ t: "folder", path: e.folder }); }));
    bar.appendChild(btn("⧉ Copy", false, function () {
      var s = e.subject + "\n\n" + (e.answer || "") + "\n\nSources:\n" + (e.sources || []).map(function (x, i) { return "[" + (i + 1) + "] " + x.label; }).join("\n");
      post({ t: "copy", text: s });
    }));
    d.appendChild(bar);
    var t = new Date(e.ts), st = status(e);
    d.appendChild(el("div", "q", (ICON[e.kind] || "") + " " + e.subject));
    var m = el("div", "small"); m.appendChild(el("span", st[0], st[1]));
    m.appendChild(document.createTextNode(" · " + dayOf(e.ts) + " " + pad(t.getHours()) + ":" + pad(t.getMinutes()) + (e.note ? " · " + e.note : "")));
    d.appendChild(m);
    var a = el("div", "ans"); render(a, e.answer || "", e.sources || []); d.appendChild(a);
    var src = e.sources || [];
    if (src.length) {
      d.appendChild(el("div", "grp", "Sources · " + src.length + " (click to open)"));
      src.forEach(function (x, i) {
        var s = el("div", "src", "[" + (i + 1) + "] " + (x.kind === "email" ? "✉ " : "📄 ") + (x.label || x.path || ""));
        if (x.path && x.kind !== "email") { s.setAttribute("data-path", x.path); s.setAttribute("data-loc", x.loc || ""); s.setAttribute("data-go", x.goto || ""); }
        s.onclick = function () { post({ t: "src", src: x }); };
        d.appendChild(s);
      });
    }
    d.scrollTop = 0;
  }

  var sign = "Designed by A.HAMROUNI";
  if (W) W.addEventListener("message", function (ev) {
    var m = ev.data;
    if (m.t === "list") { items = m.items || []; days = m.days; sign = m.sign || sign; if ($("detail").className === "hide") { drawChips(); drawList(); } else { drawChips(); drawFoot(); } }
    else if (m.t === "entry") drawEntry(m.e);
  });
  $("find").oninput = drawList;
  post({ t: "init" });
})();
