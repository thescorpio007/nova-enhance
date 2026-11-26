// 🖱 Right-click on any email row (tables, timelines, [n] links): Nova's full email menu — open in Nova / Outlook, 🔗 Find related,
// thread, reply / forward / reminder drafts, copy, save .msg / attachments / PDF, talk about it, project. Rows carry data-mid
// (the email's index id) and optionally data-row (the row text to copy). Designed by A.HAMROUNI
(function () {
  var W = window.chrome && window.chrome.webview;
  document.addEventListener("contextmenu", function (e) {
    // 📄 a file row (data-path): Nova's file menu — open / location / copy the file, path, name, number / email it / phone / other features
    var f = e.target && e.target.closest && e.target.closest("[data-path]");
    if (f && W) {
      var sel0 = window.getSelection && window.getSelection();
      if (sel0 && !sel0.isCollapsed && f.contains(sel0.anchorNode)) return;
      e.preventDefault();
      W.postMessage({ t: "filemenu", path: f.getAttribute("data-path"), loc: f.getAttribute("data-loc") || "", go: f.getAttribute("data-go") || "" });
      return;
    }
    var r = e.target && e.target.closest && e.target.closest("[data-mid],[data-entry]");
    if (!r || !W) return;               // anything else keeps the normal menu (Copy when text is selected)
    var sel = window.getSelection && window.getSelection();
    if (sel && !sel.isCollapsed && r.contains(sel.anchorNode)) return;   // selected text: the normal Copy menu
    e.preventDefault();
    W.postMessage({ t: "rowmenu", id: r.getAttribute("data-mid") || "", entry: r.getAttribute("data-entry") || "", store: r.getAttribute("data-store") || "", row: r.getAttribute("data-row") || (r.innerText || "").replace(/\s*\n\s*/g, "\t").trim() });
  });
})();

// ⋯ on every file and email row: the same menu without right-click (touch, remote desktop, a laptop pad)
(function () {
  var W = window.chrome && window.chrome.webview;
  if (!W) return;
  var st = document.createElement("style");
  st.textContent = ".nv-dots{display:inline-block;margin-left:8px;padding:0 7px;border-radius:4px;color:#b9adff;font-weight:700;cursor:pointer;user-select:none}.nv-dots:hover{background:#2e2a55}";
  document.head.appendChild(st);
  function add(root) {
    (root.querySelectorAll ? root.querySelectorAll("[data-path],[data-mid],[data-entry]") : []).forEach(function (r) {
      var host = r.tagName === "TR" ? r.lastElementChild : r;
      if (!host || host.querySelector(":scope > .nv-dots") || r.closest(".nv-nodots") || r.classList.contains("cite")) return;
      var d = document.createElement("span"); d.className = "nv-dots"; d.textContent = "⋯"; d.title = "every option (open location, copy, email, other Nova features…)";
      d.onclick = function (e) {
        e.stopPropagation(); e.preventDefault();
        if (r.hasAttribute("data-path")) W.postMessage({ t: "filemenu", path: r.getAttribute("data-path"), loc: r.getAttribute("data-loc") || "", go: r.getAttribute("data-go") || "" });
        else W.postMessage({ t: "rowmenu", id: r.getAttribute("data-mid") || "", entry: r.getAttribute("data-entry") || "", store: r.getAttribute("data-store") || "", row: r.getAttribute("data-row") || (r.innerText || "").replace(/\s*\n\s*/g, "\t").trim() });
      };
      host.appendChild(d);
    });
  }
  new MutationObserver(function (ms) { ms.forEach(function (m) { m.addedNodes.forEach(function (n) { if (n.nodeType === 1) add(n.parentNode || n); }); }); })
    .observe(document.documentElement, { childList: true, subtree: true });
  document.addEventListener("DOMContentLoaded", function () { add(document); });
})();
