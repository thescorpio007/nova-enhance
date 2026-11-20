// 🧭 Nova agent in a page (the Submittal log): a conversation panel with its own scroll bar — your questions, Nova's steps
// (● ● ● thinking card), ✓ / ✗ examples, the answer and its table, ⬇ CSV, 💬 continue in Nova Chat (the same conversation),
// ⭐ save as prompt, and a reply box at the bottom for follow-ups. ▾ folds it to see the log again.
// Designed by A.HAMROUNI
(function () {
  var W = window.chrome && window.chrome.webview;
  function post(o) { if (W) W.postMessage(o); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  var st = document.createElement("style");
  st.textContent =
    "#agentBox{display:none;flex:1;min-height:0;flex-direction:column;background:#131722;border:1px solid #3A3F55;border-radius:12px;overflow:hidden}" +
    "#agentBox.fold{flex:none}#agentBox.fold #agMsgs,#agentBox.fold #agReply{display:none}" +
    "#agHead{display:flex;align-items:center;gap:8px;padding:7px 12px;background:#1A1F2E;border-bottom:1px solid #2A3040}#agHead b{color:#CFC8FF}#agHead button{padding:3px 10px;font-size:12px}" +
    "#agMsgs{flex:1;min-height:0;overflow-y:scroll;padding:10px 16px 16px;scrollbar-width:auto;scrollbar-color:#5A5390 #151925}" +
    "#agMsgs::-webkit-scrollbar{width:12px}#agMsgs::-webkit-scrollbar-track{background:#151925}#agMsgs::-webkit-scrollbar-thumb{background:#4A4480;border-radius:6px;border:2px solid #151925}#agMsgs::-webkit-scrollbar-thumb:hover{background:#6A60C0}" +
    "#agMsgs .you{margin:10px 0 4px auto;max-width:78%;width:fit-content;background:#3B3570;color:#fff;border-radius:12px 12px 3px 12px;padding:8px 13px;white-space:pre-wrap}" +
    "#agMsgs .nova{margin:4px 0 10px;max-width:100%}" +
    "#agMsgs .ex{display:grid;grid-template-columns:auto 1fr;gap:8px;align-items:start;padding:7px 0;border-bottom:1px solid #232836}" +
    "#agMsgs .ex .s{font-weight:600;color:#fff;cursor:pointer}#agMsgs .ex .m{color:#9AA3B5;font-size:12px}" +
    "#agMsgs .yn button{padding:3px 10px;margin-right:4px}#agMsgs .yn button.on{background:#7C6CFF;color:#fff}" +
    "#agMsgs .card{background:#1A1E2B;border:1px solid #2E3448;border-radius:11px;padding:10px 13px;margin:6px 0}#agMsgs .card .q{font-weight:600;color:#fff;margin-bottom:4px}" +
    "#agMsgs .tw{max-height:55vh;overflow:auto;border:1px solid #232836;border-radius:9px;margin-top:8px}" +
    "#agMsgs table{border-collapse:collapse;width:100%;font-size:12.5px}#agMsgs th{text-align:left;color:#C9CED8;padding:6px 8px;border-bottom:1px solid #2A3040;position:sticky;top:0;background:#232A3A;z-index:2}" +
    "#agMsgs td{padding:5px 8px;border-bottom:1px solid #1F2433;vertical-align:top;white-space:normal}#agMsgs tr.r{cursor:pointer}#agMsgs tr.r:hover td{background:#1E2230}" +
    "#agMsgs td.no{color:#FFB547}#agMsgs td.yes{color:#3DDC97}" +
    "#agMsgs .acts{display:flex;gap:8px;flex-wrap:wrap;margin-top:8px}#agMsgs .ans{white-space:pre-wrap;color:#E6E8EE;margin:6px 0;line-height:1.5}#agMsgs .note{color:#FFB547;font-size:12.5px}" +
    "#agReply{display:flex;gap:8px;padding:8px 12px;border-top:1px solid #2A3040;background:#161A26}#agReply textarea{flex:1;resize:none;height:38px;padding:8px 10px;background:#1A1E28;color:#E6E8EE;border:1px solid #2A3040;border-radius:8px;font:inherit}";
  document.head.appendChild(st);
  var box, msgs, think = null, question = "";
  function scroll() { if (msgs) msgs.scrollTop = msgs.scrollHeight; }
  function ensure() {
    if (box) return;
    box = el("div"); box.id = "agentBox";
    var head = el("div"); head.id = "agHead";
    head.appendChild(el("b", null, "🧭 Conversation with Nova"));
    head.appendChild(el("span", "muted small", "scroll to see everything · type a follow-up at the bottom"));
    head.appendChild(el("span", "grow"));
    var fold = el("button", null, "▾ Fold"); fold.title = "fold the conversation to see the log, unfold to come back";
    fold.onclick = function () { var f = box.classList.toggle("fold"); fold.textContent = f ? "▸ Show the conversation" : "▾ Fold"; var t = document.getElementById("tbl"); if (t) t.style.display = f ? "" : "none"; if (!f) scroll(); };
    head.appendChild(fold);
    var chat = el("button", null, "💬 Continue in Nova Chat"); chat.onclick = function () { post({ t: "ag.chat" }); }; head.appendChild(chat);
    var back = el("button", null, "↩ Back to the log"); back.onclick = function () { show(false); msgs.innerHTML = ""; think = null; post({ t: "ag.reset" }); }; head.appendChild(back);
    box.appendChild(head);
    msgs = el("div"); msgs.id = "agMsgs"; box.appendChild(msgs);
    var rep = el("div"); rep.id = "agReply";
    var ta = el("textarea"); ta.placeholder = "Reply to Nova — “only the ones older than 7 days” · “add the MARs” · “draft a reminder to Charbel” (Enter = send · Shift+Enter = new line)";
    var go = el("button", "primary", "Send");
    function send() { var t = ta.value.trim(); if (!t) return; ta.value = ""; window.AgentUi.follow(t); }
    ta.onkeydown = function (e) { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); } };
    go.onclick = send;
    rep.appendChild(ta); rep.appendChild(go); box.appendChild(rep);
    var t = document.getElementById("tbl"); t.parentNode.insertBefore(box, t);
  }
  function show(on) {
    ensure(); box.style.display = on ? "flex" : "none"; box.classList.remove("fold");
    var f = box.querySelector("#agHead button"); if (f) f.textContent = "▾ Fold";
    var t = document.getElementById("tbl"); if (t) t.style.display = on ? "none" : "";
  }
  function you(text) { msgs.appendChild(el("div", "you", text)); scroll(); }
  function nova() { var n = el("div", "nova"); msgs.appendChild(n); return n; }
  function startThinking() {
    var n = nova();
    think = window.Thinking ? Thinking.start(n, "Nova is working…", { onStop: function () { post({ t: "ag.stop" }); }, replace: false }) : null;
    scroll();
  }
  function cellClass(c) { return /⏳|no (formal )?(reply|response)|not (yet )?answered|NO REPLY/i.test(c) ? "no" : /✅|replied|answered|approved/i.test(c) ? "yes" : null; }
  function result(r, where) {
    var n = where || nova();
    n.appendChild(el("div", "ans", r.text || ""));
    if (r.note) n.appendChild(el("div", "note", r.note));
    if ((r.rows || []).length) {
      var tw = el("div", "tw"), t = el("table"), hr = el("tr");
      (r.columns || []).forEach(function (c) { hr.appendChild(el("th", null, c)); }); t.appendChild(hr);
      r.rows.forEach(function (row) {
        var tr = el("tr", row.entry ? "r" : null);
        (row.cells || []).forEach(function (c) { tr.appendChild(el("td", cellClass(c), c)); });
        if (row.entry) { tr.title = "open in Outlook"; tr.onclick = function () { post({ t: "open", entry: row.entry, store: row.store }); }; }
        t.appendChild(tr);
      });
      tw.appendChild(t); n.appendChild(tw);
      if (window.ColResize) ColResize.attach(t, "agent");
      n.appendChild(el("div", "muted small", r.rows.length + " row(s) · click a row to open its email · drag a column edge to widen it"));
    }
    if (r.by) n.appendChild(el("div", "muted small", "by " + r.by));
    var acts = el("div", "acts");
    function b(t, f, p) { var x = el("button", p ? "primary" : null, t); x.onclick = f; acts.appendChild(x); }
    if ((r.rows || []).length) b("⬇ Excel (CSV)", function () { post({ t: "ag.csv" }); }, true);
    b("💬 Continue in Nova Chat", function () { post({ t: "ag.chat" }); });
    b("⭐ Save as prompt", function () { post({ t: "ag.prompt" }); });
    n.appendChild(acts);
    scroll();
  }
  window.AgentUi = {
    start: function (text) { question = text; ensure(); show(true); msgs.innerHTML = ""; you(text); startThinking(); post({ t: "ag.ask", text: text }); },
    handle: function (m) {
      if (!/^ag\./.test(m.t || "")) return false;
      if (m.t === "ag.start") { window.AgentUi.start(m.text); return true; }
      ensure();
      if (m.t === "ag.step") { if (think) { think.step(m.text); think.resume(); } scroll(); }
      else if (m.t === "ag.detail") { if (think) think.detail(m.text); }
      else if (m.t === "ag.confirm") {
        if (think) think.pause("waiting for your ✓ / ✗ below");
        var card = el("div", "card");
        card.appendChild(el("div", "q", "✓ / ✗ " + m.question));
        var verdict = {};
        (m.items || []).forEach(function (h) {
          var r = el("div", "ex"), yn = el("div", "yn");
          var y = el("button", null, "✓"), no = el("button", null, "✗");
          y.onclick = function () { verdict[h.id] = true; y.className = "on"; no.className = ""; };
          no.onclick = function () { verdict[h.id] = false; no.className = "on"; y.className = ""; };
          yn.appendChild(y); yn.appendChild(no); r.appendChild(yn);
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
          var yes = [], no2 = []; Object.keys(verdict).forEach(function (k) { (verdict[k] ? yes : no2).push(+k); });
          card.querySelectorAll("button").forEach(function (b) { b.disabled = true; }); note.disabled = true;
          if (think) { think.resume(); think.detail("continuing with your ✓ / ✗…"); }
          post({ t: "ag.confirmed", yes: yes, no: no2, note: note.value });
        };
        var stop = el("button", null, "■ Stop"); stop.onclick = function () { post({ t: "ag.stop" }); };
        acts.appendChild(go); acts.appendChild(stop); card.appendChild(acts);
        msgs.appendChild(card); scroll();
      }
      else if (m.t === "ag.result") { if (think) think.done(); think = null; result(m.result || {}); }
      else if (m.t === "ag.error") { if (think) think.fail(m.text); else msgs.appendChild(el("div", "note", "⚠ " + m.text)); think = null; scroll(); }
      else if (m.t === "ag.load") {
        // 📋 opened from Nova Chat: the whole conversation
        show(true); msgs.innerHTML = "";
        (m.turns || []).forEach(function (t) { if (t.q) you(t.q); result(t.result || {}); });
      }
      return true;
    },
    follow: function (text) { question = text; ensure(); show(true); you(text); startThinking(); post({ t: "ag.ask", text: text }); },
    active: function () { return box && box.style.display !== "none"; }
  };
})();
