// 🧭 Nova agent in a page (the Submittal log): steps, ✓ / ✗ examples, the result table, ⬇ CSV, 💬 continue in Nova Chat, ⭐ save as prompt.
// Designed by A.HAMROUNI
(function () {
  var W = window.chrome && window.chrome.webview;
  function post(o) { if (W) W.postMessage(o); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  var st = document.createElement("style");
  st.textContent = "#agentBox{display:none;background:#151925;border:1px solid #3A3F55;border-radius:12px;padding:10px 14px;margin:6px 0;max-height:62vh;overflow:auto}" +
    "#agentBox .q{color:#fff;font-weight:600;margin-bottom:4px}#agentBox .stp{color:#9AA3B5;font-size:12.5px;padding:1px 0}" +
    "#agentBox .ex{display:grid;grid-template-columns:auto 1fr;gap:8px;align-items:start;padding:7px 0;border-bottom:1px solid #232836}" +
    "#agentBox .ex .s{font-weight:600;color:#fff;cursor:pointer}#agentBox .ex .m{color:#9AA3B5;font-size:12px}" +
    "#agentBox .yn button{padding:3px 10px;margin-right:4px}#agentBox .yn button.on{background:#7C6CFF;color:#fff}" +
    "#agentBox table{border-collapse:collapse;width:100%;margin-top:8px;font-size:12.5px}#agentBox th{text-align:left;color:#9AA3B5;padding:5px 8px;border-bottom:1px solid #2A3040;position:sticky;top:0;background:#151925}" +
    "#agentBox td{padding:5px 8px;border-bottom:1px solid #1F2433;vertical-align:top}#agentBox tr.r{cursor:pointer}#agentBox tr.r:hover td{background:#1E2230}" +
    "#agentBox .acts{display:flex;gap:8px;flex-wrap:wrap;margin-top:8px}#agentBox .ans{white-space:pre-wrap;color:#E6E8EE;margin:6px 0}#agentBox .note{color:#FFB547;font-size:12.5px}";
  document.head.appendChild(st);
  var box, question = "";
  function ensure() {
    if (box) return;
    box = el("div"); box.id = "agentBox";
    var t = document.getElementById("tbl"); t.parentNode.insertBefore(box, t);
  }
  function show(on) { ensure(); box.style.display = on ? "block" : "none"; var t = document.getElementById("tbl"); if (t) t.style.display = on ? "none" : ""; }
  function head() { box.innerHTML = ""; var q = el("div", "q", "🧭 " + question); box.appendChild(q); var s = el("div"); s.id = "agSteps"; box.appendChild(s); }
  window.AgentUi = {
    start: function (text) { question = text; ensure(); show(true); head(); post({ t: "ag.ask", text: text }); },
    handle: function (m) {
      if (!/^ag\./.test(m.t || "")) return false;
      ensure();
      if (m.t === "ag.step") { var s = document.getElementById("agSteps"); if (s) s.appendChild(el("div", "stp", m.text)); }
      else if (m.t === "ag.confirm") {
        var card = el("div"); card.style.marginTop = "6px";
        card.appendChild(el("div", "q", "✓ / ✗ " + m.question));
        var verdict = {};
        (m.items || []).forEach(function (h) {
          var r = el("div", "ex"), yn = el("div", "yn");
          var y = el("button", null, "✓"), n = el("button", null, "✗");
          y.onclick = function () { verdict[h.id] = true; y.className = "on"; n.className = ""; };
          n.onclick = function () { verdict[h.id] = false; n.className = "on"; y.className = ""; };
          yn.appendChild(y); yn.appendChild(n); r.appendChild(yn);
          var c = el("div"); var s2 = el("div", "s", h.subject || "(no subject)"); s2.title = "open in Outlook"; s2.onclick = function () { post({ t: "open", entry: h.entry, store: h.store }); };
          c.appendChild(s2); c.appendChild(el("div", "m", h.date + " · from " + h.from + " · to " + h.to + (h.cc ? " · cc " + h.cc : "")));
          if (h.lines) c.appendChild(el("div", "m", h.lines));
          r.appendChild(c); card.appendChild(r);
        });
        var note = el("input"); note.placeholder = "anything to add? e.g. “only formal approval requests, not comments”"; note.style.cssText = "width:100%;margin-top:6px;padding:5px 8px";
        card.appendChild(note);
        var acts = el("div", "acts");
        var go = el("button", "primary", "Continue →");
        go.onclick = function () {
          var yes = [], no = []; Object.keys(verdict).forEach(function (k) { (verdict[k] ? yes : no).push(+k); });
          card.querySelectorAll("button").forEach(function (b) { b.disabled = true; }); note.disabled = true;
          post({ t: "ag.confirmed", yes: yes, no: no, note: note.value });
        };
        var stop = el("button", null, "■ Stop"); stop.onclick = function () { post({ t: "ag.stop" }); };
        acts.appendChild(go); acts.appendChild(stop); card.appendChild(acts);
        box.appendChild(card); card.scrollIntoView({ block: "nearest" });
      }
      else if (m.t === "ag.result") {
        var r = m.result || {};
        box.appendChild(el("div", "ans", r.text || ""));
        if (r.note) box.appendChild(el("div", "note", r.note));
        if ((r.rows || []).length) {
          var t = el("table"), hr = el("tr");
          (r.columns || []).forEach(function (c) { hr.appendChild(el("th", null, c)); }); t.appendChild(hr);
          r.rows.forEach(function (row) {
            var tr = el("tr", row.entry ? "r" : null);
            (row.cells || []).forEach(function (c) { tr.appendChild(el("td", null, c)); });
            if (row.entry) { tr.title = "open in Outlook"; tr.onclick = function () { post({ t: "open", entry: row.entry, store: row.store }); }; }
            t.appendChild(tr);
          });
          box.appendChild(t);
          if (window.ColResize) ColResize.attach(t, "agent");
        }
        box.appendChild(el("div", "muted small", (r.by ? "by " + r.by + " · " : "") + "type a follow-up in the box above (“only the older ones”, “add the MARs”, “draft a reminder”)"));
        var acts2 = el("div", "acts");
        function b(t, f, p) { var x = el("button", p ? "primary" : null, t); x.onclick = f; acts2.appendChild(x); }
        if ((r.rows || []).length) b("⬇ Excel (CSV)", function () { post({ t: "ag.csv" }); }, true);
        b("💬 Continue in Nova Chat", function () { post({ t: "ag.chat" }); });
        b("⭐ Save as prompt", function () { post({ t: "ag.prompt" }); });
        b("↩ Back to the log", function () { show(false); post({ t: "ag.reset" }); });
        box.appendChild(acts2);
      }
      else if (m.t === "ag.error") { box.appendChild(el("div", "note", "⚠ " + m.text)); }
      return true;
    },
    follow: function (text) { question = text; ensure(); show(true); box.appendChild(el("div", "q", "🧭 " + text)); post({ t: "ag.ask", text: text }); },
    active: function () { return box && box.style.display === "block"; }
  };
})();
