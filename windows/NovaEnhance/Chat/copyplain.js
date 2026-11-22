// ⧉ Copy from any Nova page pastes as normal text: Calibri 11, black, no background or dark-theme colours (Word, Outlook,
// Excel). Tables stay tables (plain borders). Designed by A.HAMROUNI
(function () {
  function esc(t) { return t.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;"); }
  var BASE = "font-family:Calibri,'Calibri (Body)',Arial,sans-serif;font-size:11pt;color:#000;background:transparent";
  function clean(node) {
    // keep only the structure: paragraphs, line breaks, bold, lists, tables, links — every style and class is dropped
    var keep = { B: 1, STRONG: 1, I: 1, EM: 1, U: 1, BR: 1, P: 1, DIV: 1, UL: 1, OL: 1, LI: 1, TABLE: 1, THEAD: 1, TBODY: 1, TR: 1, TD: 1, TH: 1, A: 1 };
    var out = "";
    node.childNodes.forEach(function (n) {
      if (n.nodeType === 3) { out += esc(n.nodeValue).replace(/\n/g, "<br>"); return; }
      if (n.nodeType !== 1) return;
      var tag = n.tagName, inner = clean(n);
      if (tag === "BUTTON" || tag === "SCRIPT" || tag === "STYLE" || tag === "svg" || tag === "SVG") return;
      if (!keep[tag]) { out += inner; return; }
      if (tag === "TABLE") { out += "<table style=\"border-collapse:collapse;" + BASE + "\">" + inner + "</table>"; return; }
      if (tag === "TD" || tag === "TH") { out += "<" + tag.toLowerCase() + " style=\"border:1px solid #999;padding:2px 6px;" + BASE + (tag === "TH" ? ";font-weight:bold" : "") + "\">" + inner + "</" + tag.toLowerCase() + ">"; return; }
      if (tag === "A") { out += n.href ? "<a href=\"" + esc(n.href) + "\">" + inner + "</a>" : inner; return; }
      if (tag === "BR") { out += "<br>"; return; }
      out += "<" + tag.toLowerCase() + ">" + inner + "</" + tag.toLowerCase() + ">";
    });
    return out;
  }
  document.addEventListener("copy", function (e) {
    var t = e.target;
    if (t && (t.tagName === "TEXTAREA" || t.tagName === "INPUT")) return;   // typing boxes copy as usual
    var sel = window.getSelection();
    if (!sel || sel.isCollapsed || !e.clipboardData) return;
    var box = document.createElement("div");
    for (var i = 0; i < sel.rangeCount; i++) box.appendChild(sel.getRangeAt(i).cloneContents());
    var html = "<div style=\"" + BASE + "\">" + clean(box) + "</div>";
    e.clipboardData.setData("text/plain", sel.toString());
    e.clipboardData.setData("text/html", html);
    e.preventDefault();
  });
})();
