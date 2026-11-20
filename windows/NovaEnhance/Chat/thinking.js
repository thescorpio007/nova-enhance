// ● ● ● Nova is thinking — one card for every Nova page: moving dots, a timer that counts every second, the steps (✓ done · ● now),
// ■ Stop, and after a quiet while what Nova is waiting for (“Waiting for Gemini · 25 s — still working, not stuck”).
// Designed by A.HAMROUNI
(function () {
  var W = window.chrome && window.chrome.webview;
  function post(o) { if (W) W.postMessage(o); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  var st = document.createElement("style");
  st.textContent =
    ".thk{background:#1E1B33;border:1px solid #4A42A0;border-radius:11px;padding:10px 14px;margin:6px 0;color:#E6E8EE}" +
    ".thk .top{display:flex;align-items:center;gap:12px}.thk .dts{display:flex;gap:5px;flex:none}" +
    ".thk .dts i{width:8px;height:8px;border-radius:50%;background:#8B7DFF;display:block;animation:thkb 1.1s infinite ease-in-out}" +
    ".thk .dts i:nth-child(2){animation-delay:.18s}.thk .dts i:nth-child(3){animation-delay:.36s}" +
    "@keyframes thkb{0%,80%,100%{opacity:.25;transform:scale(.8)}40%{opacity:1;transform:scale(1.15)}}" +
    ".thk .tt{font-weight:600}.thk .sb{color:#A9A3C8;font-size:12.5px;margin-top:1px}.thk .tm{margin-left:auto;color:#9AA3B5;font-variant-numeric:tabular-nums;flex:none}" +
    ".thk .stp{margin:6px 0 0 32px;font-size:12.5px;line-height:1.7}.thk .stp div{color:#B9B9C9}.thk .stp .ok{color:#4ADE80}.thk .stp .now{color:#C4BDFF}" +
    ".thk .wt{color:#FBBF24;font-size:12.5px;margin:6px 0 0 32px}.thk .bar{height:3px;background:#2C2C36;border-radius:3px;margin-top:9px;overflow:hidden;position:relative}" +
    ".thk .bar b{position:absolute;top:0;bottom:0;width:30%;background:linear-gradient(90deg,transparent,#8B7DFF,transparent);animation:thkm 1.6s infinite linear}" +
    "@keyframes thkm{0%{left:-30%}100%{left:100%}}.thk button{padding:3px 11px;font-size:12px}" +
    ".thk.done{background:transparent;border-color:#2A3040}.thk.done .dts,.thk.done .tm,.thk.done .bar,.thk.done button,.thk.done .wt{display:none}.thk.err{border-color:#7A2E33}";
  document.head.appendChild(st);

  var live = [];
  function mmss(s) { var m = Math.floor(s / 60); s = s % 60; return m + ":" + (s < 10 ? "0" : "") + s; }

  /** Thinking.start(where, title, opt) → handle · where: an element (the card is appended) · opt: {onStop, steps:true, replace:true} */
  function start(where, title, opt) {
    opt = opt || {};
    if (opt.replace !== false) where.querySelectorAll(":scope > .thk:not(.done)").forEach(function (x) { x.remove(); });
    var card = el("div", "thk"), top = el("div", "top"), dts = el("div", "dts");
    dts.appendChild(el("i")); dts.appendChild(el("i")); dts.appendChild(el("i"));
    var txt = el("div"), tt = el("div", "tt", title || "Nova is working…"), sb = el("div", "sb", "");
    txt.appendChild(tt); txt.appendChild(sb);
    var tm = el("div", "tm", "0:00");
    top.appendChild(dts); top.appendChild(txt); top.appendChild(tm);
    if (opt.onStop) { var stop = el("button", null, "■ Stop"); stop.onclick = function () { stop.disabled = true; sb.textContent = "Stopping…"; opt.onStop(); }; top.appendChild(stop); }
    card.appendChild(top);
    var steps = el("div", "stp"), wt = el("div", "wt", ""); wt.style.display = "none";
    card.appendChild(steps); card.appendChild(wt);
    var bar = el("div", "bar"); bar.appendChild(el("b")); card.appendChild(bar);
    where.appendChild(card);
    var t0 = Date.now(), last = Date.now(), asked = 0, n = 0, nowRow = null, finished = false;
    var h = {
      el: card,
      /** a new step: the one before gets ✓ */
      step: function (text) {
        if (finished) return h;
        if (nowRow) { nowRow.className = "ok"; nowRow.textContent = "✓ " + nowRow.textContent.replace(/^● /, ""); }
        n++; nowRow = el("div", "now", "● " + text); steps.appendChild(nowRow);
        tt.textContent = (title || "Nova is working") .replace(/…$/, "") + " · step " + n; sb.textContent = text;
        last = Date.now(); wt.style.display = "none"; return h;
      },
      /** what is happening inside the step (e.g. “312 emails checked”) */
      detail: function (text) { if (!finished) { sb.textContent = text; last = Date.now(); wt.style.display = "none"; } return h; },
      title: function (text) { title = text; tt.textContent = text; last = Date.now(); return h; },
      /** waiting for you (✓ / ✗): the timer and the stall note pause */
      pause: function (text) { sb.textContent = text; last = Date.now() + 36e5; return h; },
      resume: function () { last = Date.now(); return h; },
      done: function (text) {
        if (finished) return h; finished = true;
        if (nowRow) { nowRow.className = "ok"; nowRow.textContent = "✓ " + nowRow.textContent.replace(/^● /, ""); }
        card.className = "thk done"; tt.textContent = text || ("✓ Done in " + mmss(Math.round((Date.now() - t0) / 1000)));
        sb.textContent = ""; if (!steps.children.length) card.remove(); else if (opt.removeOnDone) card.remove();
        live = live.filter(function (x) { return x !== h; }); return h;
      },
      fail: function (text) { finished = true; card.className = "thk done err"; tt.textContent = "⚠ " + text; sb.textContent = ""; live = live.filter(function (x) { return x !== h; }); return h; },
      wait: function (text) { if (!finished && text && Date.now() - last > 12000) { wt.textContent = "⏳ " + text + " — still working, not stuck"; wt.style.display = ""; } },
      tick: function () {
        if (finished || !card.isConnected) { live = live.filter(function (x) { return x !== h; }); return; }
        var s = Math.round((Date.now() - t0) / 1000); tm.textContent = mmss(s);
        var quiet = (Date.now() - last) / 1000;
        if (quiet > 12 && Date.now() - asked > 5000) { asked = Date.now(); post({ t: "think.ask" }); }
        if (quiet > 60) { wt.textContent = "⚠ This is taking longer than usual — Nova is still working" + (h.waitFor ? " (" + h.waitFor + ")" : "") + ". You can wait, or ■ Stop."; wt.style.display = ""; }
      },
      active: function () { return !finished; }
    };
    live.push(h);
    return h;
  }
  setInterval(function () { live.slice().forEach(function (h) { h.tick(); }); }, 1000);
  // What Nova is waiting for (the AI, Outlook, the index…), asked from the PC after 12 quiet seconds.
  if (W) W.addEventListener("message", function (e) {
    var m = e.data; if (!m || m.t !== "think.wait") return;
    live.forEach(function (h) { h.waitFor = m.text; h.wait(m.text); });
  });
  window.Thinking = { start: start, any: function () { return live.length > 0; } };
})();
