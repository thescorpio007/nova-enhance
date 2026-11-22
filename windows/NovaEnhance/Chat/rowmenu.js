// 🖱 Right-click on any email row (tables, timelines, [n] links): Nova's full email menu — open in Nova / Outlook, 🔗 Find related,
// thread, reply / forward / reminder drafts, copy, save .msg / attachments / PDF, talk about it, project. Rows carry data-mid
// (the email's index id) and optionally data-row (the row text to copy). Designed by A.HAMROUNI
(function () {
  var W = window.chrome && window.chrome.webview;
  document.addEventListener("contextmenu", function (e) {
    var r = e.target && e.target.closest && e.target.closest("[data-mid],[data-entry]");
    if (!r || !W) return;               // anything else keeps the normal menu (Copy when text is selected)
    var sel = window.getSelection && window.getSelection();
    if (sel && !sel.isCollapsed && r.contains(sel.anchorNode)) return;   // selected text: the normal Copy menu
    e.preventDefault();
    W.postMessage({ t: "rowmenu", id: r.getAttribute("data-mid") || "", entry: r.getAttribute("data-entry") || "", store: r.getAttribute("data-store") || "", row: r.getAttribute("data-row") || (r.innerText || "").replace(/\s*\n\s*/g, "\t").trim() });
  });
})();
