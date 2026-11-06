/* Nova Enhance browser add-on — in each page: reads the selection, puts the result back, shows a small note. Designed by A.HAMROUNI */
(function () {
  if (window.__novaEnhance) return;
  window.__novaEnhance = true;
  // The chrome.* namespace with callbacks works in Chrome, Edge and Firefox alike.
var B = chrome;
  var saved = null;      // where the selection was: { el, start, end, range }
  var last = null;       // what Nova changed last: { el, start, text, original, kind }

  function editable(el) {
    if (!el) return null;
    if (el.tagName === "TEXTAREA" || (el.tagName === "INPUT" && /^(text|search|email|url|)$/i.test(el.type || ""))) return "input";
    if (el.isContentEditable) return "rich";
    return null;
  }

  function remember() {
    var el = document.activeElement;
    var kind = editable(el);
    if (kind === "input") saved = { el: el, kind: kind, start: el.selectionStart, end: el.selectionEnd, text: el.value.substring(el.selectionStart, el.selectionEnd) };
    else {
      var s = window.getSelection();
      if (!s || s.rangeCount === 0) { saved = null; return; }
      saved = { el: kind ? el : null, kind: kind, range: s.getRangeAt(0).cloneRange(), text: s.toString() };
    }
  }
  document.addEventListener("contextmenu", remember, true);
  document.addEventListener("keydown", function (e) { if (e.altKey && (e.key === "g" || e.key === "G" || e.key === "t" || e.key === "T")) remember(); }, true);

  // ---------------------------------------------------------------- small note at the bottom right
  var host = null, box = null, timer = null;
  function toast(kind, text, withPutBack) {
    if (!host) {
      host = document.createElement("div");
      host.style.cssText = "position:fixed;right:18px;bottom:18px;z-index:2147483647";
      var root = host.attachShadow ? host.attachShadow({ mode: "closed" }) : host;
      box = document.createElement("div");
      box.style.cssText = "font:13px 'Segoe UI',Arial,sans-serif;background:#12151C;color:#ECEEF3;border-radius:10px;padding:10px 14px;box-shadow:0 6px 20px rgba(0,0,0,.35);max-width:440px;line-height:1.45";
      root.appendChild(box);
      document.documentElement.appendChild(host);
    }
    box.innerHTML = "";
    var t = document.createElement("span");
    t.style.color = kind === "bad" ? "#FF6B6B" : kind === "good" ? "#5ED68A" : "#B9B0FF";
    t.textContent = text;
    box.appendChild(t);
    if (withPutBack) {
      var a = document.createElement("a");
      a.textContent = "  ↩ Put back original";
      a.href = "#";
      a.style.cssText = "color:#FFD27A;margin-left:8px;text-decoration:none;font-weight:bold";
      a.onclick = function (e) { e.preventDefault(); putBack(); };
      box.appendChild(a);
    }
    host.style.display = "block";
    clearTimeout(timer);
    if (kind !== "busy") timer = setTimeout(function () { host.style.display = "none"; }, kind === "bad" ? 7000 : 9000);
  }

  function showResult(text, label) {
    // The page can't be edited here: show the result with a Copy button.
    toast("good", "✓ " + label + " — this text can't be changed on the page:", false);
    var pre = document.createElement("div");
    pre.style.cssText = "white-space:pre-wrap;background:#181C24;border:1px solid #2E3546;border-radius:6px;padding:8px;margin-top:6px;max-height:240px;overflow:auto;color:#ECEEF3";
    pre.textContent = text;
    var c = document.createElement("button");
    c.textContent = "⧉ Copy";
    c.style.cssText = "margin-top:6px;background:#7C6CFF;color:#fff;border:0;border-radius:6px;padding:6px 10px;font-weight:bold;cursor:pointer";
    c.onclick = function () { navigator.clipboard.writeText(text).then(function () { c.textContent = "✓ Copied"; }); };
    box.appendChild(pre); box.appendChild(c);
    clearTimeout(timer); timer = setTimeout(function () { host.style.display = "none"; }, 30000);
  }

  function replace(text, original, label, ai) {
    var s = saved;
    if (s && s.kind === "input" && s.el && document.contains(s.el)) {
      s.el.focus();
      s.el.setSelectionRange(s.start, s.end);
      if (!document.execCommand("insertText", false, text)) s.el.setRangeText(text, s.start, s.end, "end");
      s.el.dispatchEvent(new Event("input", { bubbles: true }));
      last = { kind: "input", el: s.el, start: s.start, text: text, original: s.text || original };
    } else if (s && s.kind === "rich" && s.el && document.contains(s.el)) {
      s.el.focus();
      var sel = window.getSelection();
      sel.removeAllRanges(); sel.addRange(s.range);
      document.execCommand("insertText", false, text);
      last = { kind: "rich", el: s.el, text: text, original: s.text || original };
    } else { showResult(text, label); return; }
    saved = null;
    toast("good", "✓ " + label + " · original kept · " + ai, true);
  }

  function putBack() {
    if (!last) { toast("bad", "Nothing to put back on this page"); return; }
    if (last.kind === "input" && document.contains(last.el)) {
      last.el.focus();
      last.el.setSelectionRange(last.start, last.start + last.text.length);
      if (!document.execCommand("insertText", false, last.original)) last.el.setRangeText(last.original, last.start, last.start + last.text.length, "end");
      last.el.dispatchEvent(new Event("input", { bubbles: true }));
    } else if (last.kind === "rich" && document.contains(last.el)) {
      last.el.focus();
      document.execCommand("undo");
    }
    last = null;
    toast("good", "↩ Original put back", false);
  }

  function pageText() {
    var main = document.querySelector("main, article, [role=main]") || document.body;
    var t = (main.innerText || "").replace(/\n{3,}/g, "\n\n");
    if (t.length < 400 && main !== document.body) t = document.body.innerText || t;
    return t.substring(0, 60000);
  }

  B.runtime.onMessage.addListener(function (msg, sender, reply) {
    if (!msg) return;
    if (msg.t === "grab") {
      if (!saved) remember();
      reply({ text: saved ? saved.text : String(window.getSelection() || "") });
    } else if (msg.t === "toast") { toast(msg.kind, msg.text, false); reply(true); }
    else if (msg.t === "replace") { replace(msg.text, msg.original, msg.label || "Done", msg.ai || ""); reply(true); }
    else if (msg.t === "putback") { putBack(); reply(true); }
    else if (msg.t === "page") { reply({ text: pageText(), selection: String(window.getSelection() || "") }); }
    else if (msg.t === "selection") { remember(); reply({ text: saved ? saved.text : "" }); }
  });
})();
