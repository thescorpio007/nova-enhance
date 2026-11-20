// ⏱ Nova is working — one card for every Nova page: a timer ring (fills once a minute, the time inside, amber when one step
// takes more than 30 s), the steps with their own time (✓ 22 s · ⏱ 0:18 live), ■ Stop, what Nova is waiting for after a quiet
// while (“Waiting for Gemini · 25 s — still working, not stuck”), and the total kept at the end (“✓ Done in 0:47”).
// Thinking.badge(el) gives the small ⏱ 0:06 badge for status lines. Designed by A.HAMROUNI
(function () {
  var W = window.chrome && window.chrome.webview;
  function post(o) { if (W) W.postMessage(o); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  var NS = "http://www.w3.org/2000/svg", C = 119.38;   // 2πr, r = 19
  var st = document.createElement("style");
  st.textContent =
    ".thk{background:#1E1B33;border:1px solid #4A42A0;border-radius:11px;padding:10px 14px;margin:6px 0;color:#E6E8EE}" +
    ".thk .top{display:flex;align-items:center;gap:12px}" +
    ".thk .ring{position:relative;width:46px;height:46px;flex:none}.thk .ring svg{position:absolute;inset:0}" +
    ".thk .ring span{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;font-weight:700;font-size:12.5px;font-variant-numeric:tabular-nums;color:#E6E8EE}" +
    ".thk .ring .arc{transition:stroke-dashoffset .9s linear,stroke .3s}" +
    ".thk .tt{font-weight:600}.thk .sb{color:#A9A3C8;font-size:12.5px;margin-top:1px}.thk .sb.slow{color:#FFD27A}" +
    ".thk .stp{margin:6px 0 0 58px;font-size:12.5px;line-height:1.8}.thk .stp>div{display:flex;gap:10px;color:#B9B9C9}.thk .stp .d{margin-left:auto;color:#9AA3B5;font-variant-numeric:tabular-nums;white-space:nowrap}" +
    ".thk .stp .ok{color:#4ADE80}.thk .stp .now{color:#C4BDFF}.thk .stp .now .d{color:#C4BDFF;font-weight:700}.thk .stp .slow .d{color:#FFD27A;font-weight:700}.thk .stp .cut{color:#9AA3B5}" +
    ".thk .wt{color:#FBBF24;font-size:12.5px;margin:6px 0 0 58px}.thk button{padding:3px 11px;font-size:12px;margin-left:auto}" +
    ".thk.done{background:transparent;border-color:#2A3040}.thk.done .ring,.thk.done button,.thk.done .wt{display:none}.thk.done .stp{margin-left:22px}.thk.err{border-color:#7A2E33}.thk.err .tt{color:#FF8A8A}" +
    ".tbadge2{display:inline-flex;align-items:center;gap:4px;background:#2A2550;border:1px solid #4A42A0;border-radius:20px;padding:1px 9px;font-weight:700;font-variant-numeric:tabular-nums;color:#E6E8EE;font-size:12px;margin-right:6px;vertical-align:1px}" +
    ".tbadge2.am{background:#3A3016;border-color:#6A5520;color:#FFD27A}.tbadge2.ok{background:#12301F;border-color:#1F5C38;color:#4ADE80}";
  document.head.appendChild(st);

  var live = [];
  function mmss(s) { var m = Math.floor(s / 60); s = s % 60; return m + ":" + (s < 10 ? "0" : "") + s; }
  function secs(ms) { var s = Math.max(0, Math.round(ms / 1000)); return s < 60 ? s + " s" : mmss(s); }

  function ring() {
    var r = el("div", "ring"), svg = document.createElementNS(NS, "svg");
    svg.setAttribute("viewBox", "0 0 46 46");
    function c(cls, color) { var x = document.createElementNS(NS, "circle"); x.setAttribute("cx", "23"); x.setAttribute("cy", "23"); x.setAttribute("r", "19"); x.setAttribute("fill", "none"); x.setAttribute("stroke-width", "4"); x.setAttribute("stroke", color); if (cls) x.setAttribute("class", cls); return x; }
    svg.appendChild(c(null, "#2C2C40"));
    var arc = c("arc", "#8B7DFF"); arc.setAttribute("stroke-dasharray", String(C)); arc.setAttribute("stroke-dashoffset", String(C)); arc.setAttribute("stroke-linecap", "round"); arc.setAttribute("transform", "rotate(-90 23 23)");
    svg.appendChild(arc); r.appendChild(svg);
    var t = el("span", null, "0:00"); r.appendChild(t);
    return { el: r, set: function (s, slow) { t.textContent = mmss(s); arc.setAttribute("stroke-dashoffset", String(C - C * ((s % 60) || (s ? 60 : 0)) / 60)); arc.setAttribute("stroke", slow ? "#F5B83D" : "#8B7DFF"); } };
  }

  /** Thinking.start(where, title, opt) → handle · where: an element (the card is appended) · opt: {onStop, replace:true} */
  function start(where, title, opt) {
    opt = opt || {};
    if (opt.replace !== false) where.querySelectorAll(":scope > .thk:not(.done)").forEach(function (x) { x.remove(); });
    var card = el("div", "thk"), top = el("div", "top"), rg = ring();
    var txt = el("div"), tt = el("div", "tt", title || "Nova is working…"), sb = el("div", "sb", "");
    txt.appendChild(tt); txt.appendChild(sb);
    top.appendChild(rg.el); top.appendChild(txt);
    if (opt.onStop) { var stop = el("button", null, "■ Stop"); stop.onclick = function () { stop.disabled = true; sb.textContent = "Stopping…"; opt.onStop(); }; top.appendChild(stop); }
    card.appendChild(top);
    var steps = el("div", "stp"), wt = el("div", "wt", ""); wt.style.display = "none";
    card.appendChild(steps); card.appendChild(wt);
    where.appendChild(card);
    var t0 = Date.now(), last = Date.now(), asked = 0, n = 0, now = null, stepAt = Date.now(), finished = false, paused = 0, pauseAt = 0;
    function closeStep(cls, mark) {
      if (!now) return;
      var took = Date.now() - stepAt - (pauseAt ? Date.now() - pauseAt : 0);
      now.row.className = cls + (took > 30000 ? " slow" : "");
      now.label.textContent = mark + " " + now.text; now.d.textContent = secs(took) + (took > 30000 ? " ⏳" : "");
      now = null;
    }
    function elapsed() { return Math.round((Date.now() - t0 - paused - (pauseAt ? Date.now() - pauseAt : 0)) / 1000); }
    var h = {
      el: card,
      /** a new step: the one before gets ✓ and its time */
      step: function (text) {
        if (finished) return h;
        closeStep("ok", "✓");
        n++; var row = el("div", "now"), label = el("span", null, "⏱ " + text), d = el("span", "d", "0:00");
        row.appendChild(label); row.appendChild(d); steps.appendChild(row);
        now = { row: row, label: label, d: d, text: text }; stepAt = Date.now();
        tt.textContent = (title || "Nova is working").replace(/…$/, "") + " · step " + n; sb.textContent = text; sb.className = "sb";
        last = Date.now(); wt.style.display = "none"; return h;
      },
      /** what is happening inside the step (e.g. “312 emails checked”) */
      detail: function (text) { if (!finished) { sb.textContent = text; last = Date.now(); wt.style.display = "none"; } return h; },
      title: function (text) { title = text; tt.textContent = text; last = Date.now(); return h; },
      /** waiting for you (✓ / ✗): the timers stop meanwhile */
      pause: function (text) { sb.textContent = text; if (!pauseAt) pauseAt = Date.now(); last = Date.now() + 36e5; return h; },
      resume: function () { if (pauseAt) { var p = Date.now() - pauseAt; paused += p; stepAt += p; pauseAt = 0; } last = Date.now(); return h; },
      seconds: function () { return elapsed(); },
      done: function (text) {
        if (finished) return h; h.resume(); finished = true;
        closeStep("ok", "✓");
        card.className = "thk done"; tt.textContent = text || ("✓ Done in " + mmss(elapsed()));
        sb.textContent = ""; if (!steps.children.length) card.remove(); else if (opt.removeOnDone) card.remove();
        live = live.filter(function (x) { return x !== h; }); return h;
      },
      fail: function (text) {
        if (finished) return h; h.resume(); finished = true;
        closeStep("cut", "■");
        card.className = "thk done err"; tt.textContent = "⚠ " + (/^stopped\.?$/i.test(text || "") ? "Stopped" : text) + " · after " + mmss(elapsed()); sb.textContent = "";
        live = live.filter(function (x) { return x !== h; }); return h;
      },
      wait: function (text) { if (!finished && text && Date.now() - last > 12000) { wt.textContent = "⏳ " + text + " — still working, not stuck"; wt.style.display = ""; } },
      tick: function () {
        if (finished || !card.isConnected) { live = live.filter(function (x) { return x !== h; }); return; }
        if (pauseAt) return;
        var stepS = (Date.now() - stepAt) / 1000, slow = !!now && stepS > 30;
        rg.set(elapsed(), slow);
        if (now) { now.d.textContent = mmss(Math.round(stepS)); now.row.className = "now" + (slow ? " slow" : ""); }
        var quiet = (Date.now() - last) / 1000;
        if (quiet > 12 && Date.now() - asked > 5000) { asked = Date.now(); post({ t: "think.ask" }); }
        if (quiet > 60) { wt.textContent = "⚠ This is taking longer than usual — Nova is still working" + (h.waitFor ? " (" + h.waitFor + ")" : "") + ". You can wait, or ■ Stop."; wt.style.display = ""; }
      },
      active: function () { return !finished; }
    };
    live.push(h);
    return h;
  }

  /** ⏱ the small badge for a status line: Thinking.badge(span) → {stop(ok)} — counts up, amber after 30 s, green ✓ at the end */
  function badge(where) {
    var b = el("span", "tbadge2", "⏱ 0:00"), t0 = Date.now(), on = true;
    where.insertBefore(b, where.firstChild);
    var iv = setInterval(function () { if (!on || !b.isConnected) { clearInterval(iv); return; } var s = Math.round((Date.now() - t0) / 1000); b.textContent = "⏱ " + mmss(s); b.className = "tbadge2" + (s > 30 ? " am" : ""); }, 1000);
    return { el: b, stop: function (ok) { on = false; clearInterval(iv); var s = Math.round((Date.now() - t0) / 1000); b.textContent = (ok === false ? "■ " : "✓ ") + mmss(s); b.className = "tbadge2" + (ok === false ? " am" : " ok"); } };
  }

  setInterval(function () { live.slice().forEach(function (h) { h.tick(); }); }, 1000);
  // What Nova is waiting for (the AI, Outlook, the index…), asked from the PC after 12 quiet seconds.
  if (W) W.addEventListener("message", function (e) {
    var m = e.data; if (!m || m.t !== "think.wait") return;
    live.forEach(function (h) { h.waitFor = m.text; h.wait(m.text); });
  });
  window.Thinking = { start: start, badge: badge, mmss: mmss, any: function () { return live.length > 0; } };
})();
