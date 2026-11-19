// ＋ Projects found in your emails — the same panel in the Submittal log and the 📁 Projects window. Designed by A.HAMROUNI
(function () {
  var W = window.chrome && window.chrome.webview;
  function post(o) { if (W) W.postMessage(o); }
  function el(tag, cls, text) { var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  var st = document.createElement("style");
  st.textContent = ".pfPop{position:fixed;right:24px;top:96px;width:680px;max-width:calc(100vw - 48px);max-height:calc(100vh - 140px);overflow:auto;background:#151925;border:1px solid #3A3F55;border-radius:14px;padding:14px 16px;box-shadow:0 16px 50px rgba(0,0,0,.55);z-index:50;display:none}" +
    ".pfPop.full{position:static;width:auto;max-width:none;max-height:none;box-shadow:none;display:block;border:none;padding:4px}" +
    ".pfHd{display:flex;align-items:center;gap:8px}.pfHd b{font-size:15px}.pfX{margin-left:auto;cursor:pointer;color:#9AA3B5;font-size:16px}" +
    ".pfRow{display:grid;grid-template-columns:170px 1fr 80px auto;gap:10px;align-items:center;padding:9px 0;border-bottom:1px solid #232836}" +
    ".pfRow .v{font-weight:700;word-break:break-all}.pfRow .s{color:#9AA3B5;font-size:11.5px;word-break:break-all}.pfRow .n{color:#C9CED8;font-size:12px;text-align:right}" +
    ".pfBtns{display:flex;gap:6px;align-items:center;flex-wrap:wrap}.pfBtns button{padding:4px 10px;font-size:12px}.pfBtns select{font-size:12px;padding:3px 4px;max-width:150px}" +
    ".pfAdd{grid-column:1/-1;display:flex;gap:8px;align-items:center;background:#1B2030;border-radius:9px;padding:8px}.pfAdd input{padding:4px 8px}" +
    ".pfSaid{color:#3DDC97;font-size:12.5px;margin:6px 0}.pfFoot{color:#9AA3B5;font-size:11.5px;margin-top:8px}";
  document.head.appendChild(st);
  var KIND = { prefix: "document numbers", folder: "Outlook folder", tag: "subject tag" };
  var box, listBox, said, list = [], projects = [], full = false;

  function ensure() {
    if (box) return;
    box = el("div", "pfPop" + (full ? " full" : ""));
    var hd = el("div", "pfHd"); hd.appendChild(el("b", null, "＋ Projects found in your emails"));
    if (!full) { var x = el("span", "pfX", "✕"); x.onclick = function () { box.style.display = "none"; }; hd.appendChild(x); }
    box.appendChild(hd);
    box.appendChild(el("div", "muted small", "document-number prefixes, Outlook folders and subject tags in emails that no project of yours claims yet"));
    said = el("div", "pfSaid"); box.appendChild(said);
    listBox = el("div"); box.appendChild(listBox);
    box.appendChild(el("div", "pfFoot", "✚ Add or ⇢ merge → email sorting, the logs, the search box and the phone use it at once · ✗ Ignore = never suggested again"));
    (document.getElementById("pfHost") || document.body).appendChild(box);
  }

  function draw() {
    listBox.innerHTML = "";
    if (!list.length) { listBox.appendChild(el("div", "muted", "Nothing new — every project in your emails is already in your list.")); return; }
    list.forEach(function (s) {
      var r = el("div", "pfRow");
      r.appendChild(el("div", "v", s.value + (s.kind === "prefix" ? "-…" : "")));
      r.appendChild(el("div", "s", KIND[s.kind] + " · " + s.sample));
      r.appendChild(el("div", "n", s.emails + " emails"));
      var b = el("div", "pfBtns");
      var add = el("button", s.target ? null : "primary", "✚ Add" + (s.target ? "" : " as " + s.code));
      var sel = el("select");
      sel.appendChild(new Option(s.target ? "⇢ " + s.target : "⇢ Merge into…", s.target || ""));
      projects.forEach(function (p) { if (p.code !== s.target) sel.appendChild(new Option(p.code + (p.name ? " · " + p.name : ""), p.code)); });
      var mg = el("button", s.target ? "primary" : null, s.target ? "⇢ " + s.target : "⇢");
      mg.title = "Merge into the project chosen on the left";
      sel.onchange = function () { mg.textContent = sel.value ? "⇢ " + sel.value : "⇢"; };
      mg.onclick = function () { if (!sel.value) { sel.focus(); return; } busy(); post({ t: "pf.merge", kind: s.kind, value: s.value, target: sel.value }); };
      var ig = el("button", null, "✗"); ig.title = "Ignore";
      ig.onclick = function () { busy(); post({ t: "pf.ignore", value: s.value }); };
      add.onclick = function () {
        if (r.querySelector(".pfAdd")) return;
        var f = el("div", "pfAdd");
        var c = el("input"); c.value = s.code; c.style.width = "90px"; c.placeholder = "Code";
        var n = el("input"); n.value = s.kind === "prefix" ? "" : s.value; n.placeholder = "Full name (e.g. Saadiyat Marina Nouran Living)"; n.style.flex = "1";
        var ok = el("button", "primary", "✓ Add project");
        ok.onclick = function () { busy(); post({ t: "pf.add", kind: s.kind, value: s.value, code: c.value.trim(), name: n.value.trim() }); };
        f.appendChild(el("span", "muted small", "Code")); f.appendChild(c); f.appendChild(n); f.appendChild(ok);
        r.appendChild(f); c.focus();
      };
      b.appendChild(add); b.appendChild(sel); b.appendChild(mg); b.appendChild(ig);
      r.appendChild(b);
      listBox.appendChild(r);
    });
  }

  function busy() { said.style.color = "#B9B0FF"; said.textContent = "⏳ …"; }

  window.ProjFound = {
    full: function () { full = true; },
    open: function () { ensure(); box.style.display = "block"; busy(); post({ t: "pf.load" }); },
    handle: function (m) {
      if (m.t !== "pf.list") return false;
      ensure();
      list = m.items || []; projects = m.projects || [];
      said.style.color = /^⚠/.test(m.said || "") ? "#FFB547" : "#3DDC97";
      said.textContent = m.said || "";
      draw();
      return true;
    },
    items: function () { return list; }
  };
})();
