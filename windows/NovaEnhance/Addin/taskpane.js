/* Nova Enhance Office panel — plain ES5 so it runs in every Office version. Designed by A.HAMROUNI */
(function () {
  var host = "doc", lastOriginal = "", lastWasWhole = false, originals = [], origIdx = 0;
  var isExcel = false, cellReview = true, xlStop = false, xlPending = null, lastCells = null;

  function $(id) { return document.getElementById(id); }
  function status(t, kind) { var s = $("status"); s.innerHTML = ""; s.appendChild(document.createTextNode(t)); s.className = "status" + (kind ? " " + kind : ""); }
  function show(id, on) { var e = $(id); if (e) e.className = e.className.replace(/\s*hide/g, "") + (on ? "" : " hide"); }

  function api(path, body, cb) {
    var x = new XMLHttpRequest();
    x.open(body ? "POST" : "GET", "/api/" + path, true);
    x.setRequestHeader("X-Nova", "1");
    if (body) x.setRequestHeader("Content-Type", "application/json");
    x.onreadystatechange = function () {
      if (x.readyState !== 4) return;
      var r = null;
      try { r = JSON.parse(x.responseText); } catch (e) { r = { error: "Nova Enhance is not answering — is it running? (" + x.status + ")" }; }
      if (x.status === 0) r = { error: "Nova Enhance is not running on this PC — start it from the Start menu." };
      cb(r || {});
    };
    x.send(body ? JSON.stringify(body) : null);
  }

  function each(sel, fn) { var l = document.querySelectorAll(sel); for (var i = 0; i < l.length; i++) fn(l[i]); }

  // ---------------------------------------------------------------- reading and writing the selection
  function getSelection(cb) {
    if (host === "compose") {
      Office.context.mailbox.item.getSelectedDataAsync(Office.CoercionType.Text, function (r) {
        var t = r.status === Office.AsyncResultStatus.Succeeded && r.value ? (r.value.data || "") : "";
        if (t.replace(/\s/g, "").length > 0) { cb(t, false); return; }
        // Nothing selected: only YOUR text — not the signature, not the earlier emails below.
        Office.context.mailbox.item.body.getAsync(Office.CoercionType.Html, function (b) {
          if (b.status !== Office.AsyncResultStatus.Succeeded) { cb("", true); return; }
          var parts = splitHtml(b.value || "");
          cb(htmlText(parts.own), true);
        });
      });
      return;
    }
    Office.context.document.getSelectedDataAsync(Office.CoercionType.Text, function (r) {
      cb(r.status === Office.AsyncResultStatus.Succeeded ? (r.value || "") : "", false);
    });
  }

  // ---------------------------------------------------------------- your text / your signature / the earlier emails
  var SIG_MARKS = ['name="_mailautosig"', 'name=_mailautosig', 'id="signature"', 'id=signature', 'id="divrplyfwdmsg"', 'id=divrplyfwdmsg',
                   'id="appendonsend"', 'id=appendonsend', 'border-top:solid #e1e1e1', 'border-top:solid #b5c4df', '<hr'];
  var CLOSING = /^\s*((best|kind|warm|warmest|with best|many)\s+regards|regards|thanks?( you)?( and regards| & regards)?|thank you( very much)?|sincerely|yours (sincerely|faithfully)|cordialement|bien à vous)\b[\s,.!]*$/i;

  function splitHtml(html) {
    var low = html.toLowerCase();
    var bm = /<body[^>]*>/i.exec(html);
    var start = bm ? bm.index + bm[0].length : 0;
    var endBody = low.lastIndexOf("</body>"); if (endBody < start) endBody = html.length;
    var cut = -1, i, k;
    for (k = 0; k < SIG_MARKS.length; k++) { i = low.indexOf(SIG_MARKS[k], start); if (i >= 0 && (cut < 0 || i < cut)) cut = i; }
    var rm = /(best|kind|warm|warmest|with best|many)\s+regards|>\s*regards\s*,/i.exec(html.slice(start));
    if (rm && (cut < 0 || start + rm.index < cut)) cut = start + rm.index;
    if (cut >= 0) {
      var b = Math.max(low.lastIndexOf("<p", cut), low.lastIndexOf("<div", cut), low.lastIndexOf("<table", cut));
      if (b >= start) cut = b;
    }
    var found = cut >= 0 && cut < endBody;
    return { pre: html.slice(0, start), own: html.slice(start, found ? cut : endBody), rest: html.slice(found ? cut : endBody), found: found };
  }

  function htmlText(h) { var d = document.createElement("div"); d.innerHTML = h; return (d.innerText || d.textContent || "").replace(/\u00a0/g, " ").replace(/\n{3,}/g, "\n\n").replace(/^\s+|\s+$/g, ""); }

  function stripClosing(t) {
    var lines = String(t || "").replace(/\r\n/g, "\n").replace(/\s+$/, "").split("\n"), seen = 0;
    for (var i = lines.length - 1; i >= 0 && seen < 6; i--) {
      if (!lines[i].replace(/\s/g, "")) continue;
      seen++;
      if (CLOSING.test(lines[i])) return lines.slice(0, i).join("\n").replace(/\s+$/, "");
    }
    return lines.join("\n");
  }

  function esc(t) { return String(t).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;"); }

  /// Replaces only your text: the signature and the earlier emails below stay exactly as they are.
  function replaceOwn(text, done) {
    var item = Office.context.mailbox.item;
    item.body.getAsync(Office.CoercionType.Html, function (b) {
      if (b.status !== Office.AsyncResultStatus.Succeeded) { done(b); return; }
      var parts = splitHtml(b.value || "");
      if (parts.found) text = stripClosing(text);
      var fm = /font-family:\s*([^;"]+)/i.exec(parts.own), sm = /font-size:\s*([^;"]+)/i.exec(parts.own);
      var style = (fm ? "font-family:" + fm[1] + ";" : "") + (sm ? "font-size:" + sm[1] + ";" : "");
      var lines = String(text).replace(/\r\n/g, "\n").split("\n"), out = "";
      for (var i = 0; i < lines.length; i++)
        out += '<p class="MsoNormal" style="margin:0">' + (style ? '<span style="' + style + '">' : "") + (lines[i].length ? esc(lines[i]) : "&nbsp;") + (style ? "</span>" : "") + "</p>";
      out += '<p class="MsoNormal" style="margin:0">&nbsp;</p>';
      item.body.setAsync(parts.pre + out + parts.rest, { coercionType: Office.CoercionType.Html }, done);
    });
  }

  // ---------------------------------------------------------------- 🕘 versions of the draft (Outlook can't Ctrl+Z add-in changes)
  var lastLabel = "Nova", viewKey = null;

  function draftKey(cb) {
    var it = Office.context.mailbox && Office.context.mailbox.item;
    if (!it) { cb("", ""); return; }
    var conv = it.conversationId || "";
    if (typeof it.subject === "string") { cb("mail|" + conv + "|" + it.subject, it.subject); return; }
    it.subject.getAsync(function (r) { var s = r.status === Office.AsyncResultStatus.Succeeded ? (r.value || "") : ""; cb("mail|" + conv + "|" + s, s); });
  }

  // 💬 The key of the chat about this email: a brand-new email (no conversation yet) gets its own,
  // so it never continues another email's chat.
  function askKey(cb) {
    draftKey(function (key, subj) {
      var it = Office.context.mailbox && Office.context.mailbox.item;
      if (it && !it.conversationId) {
        if (!window.__novaNewKey) window.__novaNewKey = "new-" + Date.now().toString(36) + Math.random().toString(36).slice(2, 8);
        key = "mail|" + window.__novaNewKey + "|";
      }
      cb(key, subj);
    });
  }

  function snapshot(label, cb) {
    if (host !== "compose") { if (cb) cb(); return; }
    draftKey(function (key, subj) {
      Office.context.mailbox.item.body.getAsync(Office.CoercionType.Html, function (b) {
        if (b.status !== Office.AsyncResultStatus.Succeeded) { if (cb) cb(); return; }
        api("versions/save", { key: key, subject: subj, label: label, html: b.value || "", text: htmlText(splitHtml(b.value || "").own) }, function () { if (cb) cb(); });
      });
    });
  }

  function showVersions() {
    draftKey(function (key) {
      var k = viewKey || key;
      api("versions/list", { key: k }, function (r) {
        var box = $("verlist"); box.innerHTML = "";
        var vs = r.versions || [];
        if (!vs.length) box.appendChild(document.createTextNode("No versions of this draft yet — they are kept as soon as Nova changes it."));
        for (var i = 0; i < vs.length; i++) (function (v) {
          var d = document.createElement("div"); d.className = "ver";
          var b = document.createElement("b"); b.appendChild(document.createTextNode(v.when + " · " + v.label)); d.appendChild(b);
          var p = document.createElement("div"); p.className = "p"; p.appendChild(document.createTextNode(v.preview || "")); d.appendChild(p);
          d.onclick = function () {
            if (d.getAttribute("data-ask")) return;
            d.setAttribute("data-ask", "1");
            var yes = document.createElement("button"); yes.className = "primary"; yes.appendChild(document.createTextNode("↩ Put this version back"));
            var no = document.createElement("button"); no.appendChild(document.createTextNode("Cancel"));
            var row = document.createElement("div"); row.className = "row"; row.appendChild(yes); row.appendChild(no); d.appendChild(row);
            no.onclick = function (e) { e.stopPropagation(); d.removeChild(row); d.removeAttribute("data-ask"); };
            yes.onclick = function (e) {
              e.stopPropagation();
              api("versions/get", { key: k, i: v.i }, function (g) {
                if (!g.html) { status("⚠ That version could not be read", "bad"); return; }
                snapshot("Before putting back " + v.when, function () {
                  Office.context.mailbox.item.body.setAsync(g.html, { coercionType: Office.CoercionType.Html }, function (res) {
                    status(res.status === Office.AsyncResultStatus.Succeeded ? "↩ The " + v.when + " version is back (formatting and signature included)" : "⚠ " + (res.error ? res.error.message : ""), res.status === Office.AsyncResultStatus.Succeeded ? "good" : "bad");
                    showVersions();
                  });
                });
              });
            };
          };
          box.appendChild(d);
        })(vs[i]);
        var rec = r.recent || [];
        if (rec.length) {
          var h = document.createElement("div"); h.className = "small muted"; h.style.marginTop = "8px"; h.appendChild(document.createTextNode("Other drafts (e.g. if you changed the subject):")); box.appendChild(h);
          for (var j = 0; j < rec.length; j++) (function (o) {
            var a = document.createElement("div"); a.className = "ver";
            a.appendChild(document.createTextNode(o.when + " · " + (o.subject || "(no subject)") + " · " + o.count + " versions"));
            a.onclick = function () { viewKey = o.key; showVersions(); };
            box.appendChild(a);
          })(rec[j]);
        }
        if (viewKey) { var back = document.createElement("a"); back.href = "#"; back.appendChild(document.createTextNode("← this draft")); back.onclick = function () { viewKey = null; showVersions(); return false; }; box.insertBefore(back, box.firstChild); }
      });
    });
  }

  // ---------------------------------------------------------------- before → after (word by word)
  function toks(s) { return String(s || "").match(/\s+|[^\s]+/g) || []; }

  function diffHtml(a, b) {
    var x = toks(a), y = toks(b), n = x.length, m = y.length;
    if (n * m > 2500000) return null;
    var w = m + 1, dp = new Int32Array((n + 1) * (m + 1)), i, j;
    for (i = n - 1; i >= 0; i--) for (j = m - 1; j >= 0; j--)
      dp[i * w + j] = x[i] === y[j] ? dp[(i + 1) * w + j + 1] + 1 : Math.max(dp[(i + 1) * w + j], dp[i * w + j + 1]);
    var out = "", del = "", ins = "";
    function flush() { if (del) out += "<del>" + esc(del) + "</del>"; if (ins) out += "<ins>" + esc(ins) + "</ins>"; del = ins = ""; }
    i = 0; j = 0;
    while (i < n && j < m) {
      if (x[i] === y[j]) { flush(); out += esc(x[i]); i++; j++; }
      else if (dp[(i + 1) * w + j] >= dp[i * w + j + 1]) del += x[i++];
      else ins += y[j++];
    }
    while (i < n) del += x[i++];
    while (j < m) ins += y[j++];
    flush();
    return out;
  }

  function showDiff(before, after) {
    var h = host === "compose" || host === "doc" ? diffHtml(before, after) : null;
    var d = $("diff");
    if (h === null) { show("diff", false); show("diffnote", false); show("keep", false); return; }
    d.innerHTML = h; show("diff", true); show("diffnote", true); show("keep", true);
    if (host === "compose") $("replace").innerHTML = "✓ Use Nova's version";
  }

  function putText(text, whole, insert, cb) {
    var done = function (r) { cb(r.status === Office.AsyncResultStatus.Succeeded, r.error ? r.error.message : ""); };
    if (host === "compose") {
      var label = lastLabel;
      var after = function (r) { if (r.status === Office.AsyncResultStatus.Succeeded) snapshot("After: " + label, function () { done(r); }); else done(r); };
      snapshot(lastOriginal ? "Your text before " + label : "Before " + label, function () {
        if (whole && !insert) replaceOwn(text, after);
        else Office.context.mailbox.item.body.setSelectedDataAsync(text, { coercionType: Office.CoercionType.Text }, after);
      });
      return;
    }
    if (insert) text = (lastOriginal || "") + "\n" + text;
    Office.context.document.setSelectedDataAsync(text, { coercionType: Office.CoercionType.Text }, done);
  }

  // ---------------------------------------------------------------- enhance
  function enhance(mode) {
    if (isExcel) { xlStart(mode); return; }
    enhanceText(mode);
  }

  function enhanceText(mode) {
    getSelection(function (text, whole) {
      if (!text || text.replace(/\s/g, "").length === 0) { status("Select some text first", "bad"); return; }
      status("✦ Working…");
      each("button", function (b) { b.disabled = true; });
      api("enhance", { mode: mode, text: text, lang: $("lang").value, host: host }, function (r) {
        each("button", function (b) { b.disabled = false; });
        if (r.error) { status("⚠ " + r.error, "bad"); return; }
        lastOriginal = text; lastWasWhole = whole; lastLabel = r.label || mode;
        $("result").value = r.result;
        showDiff(text, r.result);
        $("outlabel").innerHTML = ""; $("outlabel").appendChild(document.createTextNode(r.label + " · " + r.ai));
        show("out", true);
        setOrig(text);
        status(host === "compose" ? "✓ Done — check the changes, then ✓ Use Nova's version (or ✗ Keep mine). Every version is kept in 🕘 Versions." : "✓ Done — check it, then ⇄ Replace. The original is saved.", "good");
      });
    });
  }


  // ================================================================ Excel: many cells, each on its own
  function trim(t) { return String(t == null ? "" : t).replace(/^\s+|\s+$/g, ""); }

  // Cells that are not sentences stay as they are: codes, references, units, e-mails, links.
  function skipText(t) {
    var s = trim(t);
    if (s.length < 2) return true;
    if (!/[A-Za-zÀ-ɏ؀-ۿ]/.test(s)) return true;
    if (/^\S+$/.test(s) && /\d/.test(s)) return true;
    if (/^[A-Z0-9]{1,6}[-_\/.]?[A-Z0-9\-_\/.]*\d[A-Z0-9\-_\/.]*(\s+(rev|REV|Rev)\.?\s*[A-Z0-9]{1,3})?$/.test(s)) return true;
    if (/^\S+@\S+\.\S+$/.test(s) || /^(https?:\/\/|www\.)/i.test(s)) return true;
    if (/^[A-Za-z]{1,4}\.?$/.test(s)) return true;
    return false;
  }

  function colName(n) { var s = ""; n++; while (n > 0) { var m = (n - 1) % 26; s = String.fromCharCode(65 + m) + s; n = Math.floor((n - 1) / 26); } return s; }
  function colNum(letters) { var n = 0; for (var i = 0; i < letters.length; i++) n = n * 26 + (letters.charCodeAt(i) - 64); return n - 1; }
  function startOf(address) {
    var a = address.substring(address.lastIndexOf("!") + 1).split(":")[0].replace(/\$/g, "");
    var m = /^([A-Z]+)(\d+)$/.exec(a);
    return m ? { col: colNum(m[1]), row: parseInt(m[2], 10) - 1 } : { col: 0, row: 0 };
  }

  function busy(on) { each("button", function (b) { if (b.id !== "xlstop") b.disabled = on; }); }
  function xlErr(e) { busy(false); show("xlprog", false); status("⚠ " + (e && e.message ? e.message : e), "bad"); }

  function xlStart(mode) {
    show("xlask", false); show("xlreview", false);
    Excel.run(function (ctx) {
      var sel = ctx.workbook.getSelectedRange();
      sel.load("address,rowCount,columnCount,values,formulas,valueTypes");
      return ctx.sync().then(function () {
        if (sel.rowCount * sel.columnCount === 1) {
          var v = sel.values[0][0], f = sel.formulas[0][0];
          if (sel.valueTypes[0][0] === "String" && trim(v).length > 0 && !(typeof f === "string" && f.charAt(0) === "=")) { enhanceText(mode); return; }
          xlPending = mode; show("xlask", true); status("No text selected — whole sheet?"); return;
        }
        return xlCollect(ctx, sel, mode);
      });
    }).catch(xlErr);
  }

  function xlWholeSheet() {
    var mode = xlPending; xlPending = null; show("xlask", false);
    Excel.run(function (ctx) {
      var used = ctx.workbook.worksheets.getActiveWorksheet().getUsedRange(true);
      used.load("address,rowCount,columnCount,values,formulas,valueTypes");
      return ctx.sync().then(function () { return xlCollect(ctx, used, mode, true); });
    }).catch(function (e) { xlErr(e && e.code === "ItemNotFound" ? "This sheet is empty" : e); });
  }

  // Multi-cell selection: only the part that has content (a whole column would be a million cells).
  function xlCollect(ctx, range, mode, already) {
    var target = range, ws = ctx.workbook.worksheets.getActiveWorksheet();
    ws.load("name");
    if (!already) {
      target = range.getIntersection(ws.getUsedRange(true));
      target.load("address,rowCount,columnCount,values,formulas,valueTypes");
    }
    return ctx.sync().then(function () {
      if (target.rowCount * target.columnCount > 300000) { status("⚠ Too many cells (" + target.rowCount * target.columnCount + ") — select a smaller area", "bad"); return; }
      var st = startOf(target.address), items = [], skipped = 0, n = 0;
      for (var r = 0; r < target.rowCount; r++)
        for (var c = 0; c < target.columnCount; c++) {
          var vt = target.valueTypes[r][c], v = target.values[r][c], f = target.formulas[r][c];
          if (vt === "Empty") continue;
          if (vt !== "String" || (typeof f === "string" && f.charAt(0) === "=") || skipText(v)) { skipped++; continue; }
          items.push({ id: "c" + (n++), addr: colName(st.col + c) + (st.row + r + 1), text: String(v) });
        }
      if (!items.length) { status("Nothing to change — no text cells here (" + skipped + " skipped: numbers, dates, formulas, codes)"); return; }
      if (items.length > 3000) { status("⚠ " + items.length + " text cells — select up to 3000 at a time", "bad"); return; }
      xlRun(ws.name, items, skipped, mode);
    });
  }

  function xlRun(sheet, items, skipped, mode) {
    var batches = [], cur = [], size = 0;
    for (var i = 0; i < items.length; i++) {
      if (cur.length && (cur.length >= 25 || size + items[i].text.length > 4000)) { batches.push(cur); cur = []; size = 0; }
      cur.push(items[i]); size += items[i].text.length;
    }
    if (cur.length) batches.push(cur);
    var done = 0, b = 0, failed = 0, label = "", byId = {};
    xlStop = false; busy(true); show("xlprog", true); show("out", false);
    var verb = mode === "translate" ? "Translating" : mode === "grammar" ? "Fixing" : "Working on";
    function prog() {
      $("xlbar").style.width = Math.round(100 * done / items.length) + "%";
      $("xlcount").innerHTML = ""; $("xlcount").appendChild(document.createTextNode(verb + " " + done + " of " + items.length + " cells…"));
    }
    function next() {
      if (xlStop || b >= batches.length) { finish(); return; }
      var batch = batches[b++], send = [];
      for (var k = 0; k < batch.length; k++) send.push({ id: batch[k].id, text: batch[k].text });
      api("cells", { mode: mode, lang: $("lang").value, items: send }, function (r) {
        if (r.error) { if (done === 0) { xlErr(r.error); return; } failed += batch.length; }
        else { label = r.label + " · " + r.ai; for (var j = 0; j < r.items.length; j++) byId[r.items[j].id] = r.items[j].result; }
        done += batch.length; prog(); next();
      });
    }
    function finish() {
      busy(false); show("xlprog", false);
      var changes = [];
      for (var i = 0; i < items.length; i++) {
        var res = byId[items[i].id];
        if (res != null && trim(res) !== trim(items[i].text)) changes.push({ addr: items[i].addr, before: items[i].text, after: res });
      }
      var note = (xlStop ? "Stopped · " : "") + items.length + " text cells checked · " + skipped + " skipped" + (failed ? " · ⚠ " + failed + " not answered" : "");
      if (!changes.length) { status("✓ Nothing to change · " + note, "good"); return; }
      if (cellReview) xlReview(sheet, changes, mode, label, note);
      else xlWrite(sheet, changes, mode, true, note);
    }
    prog(); next();
  }

  function xlReview(sheet, changes, mode, label, note) {
    var list = $("xllist"); list.innerHTML = "";
    for (var i = 0; i < changes.length; i++) {
      var d = document.createElement("div"); d.className = "xlit";
      var l = document.createElement("label");
      var cb = document.createElement("input"); cb.type = "checkbox"; cb.checked = true; cb.style.width = "auto"; cb.setAttribute("data-i", i);
      var ad = document.createElement("span"); ad.className = "ad"; ad.appendChild(document.createTextNode(changes[i].addr));
      var bf = document.createElement("div"); bf.className = "b"; bf.appendChild(document.createTextNode(changes[i].before));
      var af = document.createElement("div"); af.className = "a"; af.appendChild(document.createTextNode("→ " + changes[i].after));
      l.appendChild(cb); l.appendChild(ad); l.appendChild(bf); l.appendChild(af); d.appendChild(l); list.appendChild(d);
    }
    $("xltitle").innerHTML = ""; $("xltitle").appendChild(document.createTextNode(changes.length + " changes · " + label));
    show("xlreview", true);
    status(note + " — untick what you don't want, then ✓ Apply");
    $("xlall").onclick = function () { each("#xllist input", function (c) { c.checked = true; }); return false; };
    $("xlnone").onclick = function () { each("#xllist input", function (c) { c.checked = false; }); return false; };
    $("xlcancel").onclick = function () { show("xlreview", false); status("Cancelled — nothing changed"); };
    $("xlapply").onclick = function () {
      var pick = [];
      each("#xllist input", function (c) { if (c.checked) pick.push(changes[parseInt(c.getAttribute("data-i"), 10)]); });
      show("xlreview", false);
      if (!pick.length) { status("Nothing ticked — nothing changed"); return; }
      xlWrite(sheet, pick, mode, false, note);
    };
  }

  function xlWrite(sheet, changes, mode, mark, note) {
    busy(true);
    Excel.run(function (ctx) {
      var ws = ctx.workbook.worksheets.getItem(sheet), ranges = [];
      for (var i = 0; i < changes.length; i++) {
        var rg = ws.getRange(changes[i].addr);
        if (mark) rg.format.fill.load("color");
        ranges.push(rg);
      }
      return ctx.sync().then(function () {
        for (var i = 0; i < changes.length; i++) {
          if (mark) { changes[i].fill = ranges[i].format.fill.color; ranges[i].format.fill.color = "#FFF59D"; }
          ranges[i].values = [[changes[i].after]];
        }
        return ctx.sync();
      });
    }).then(function () {
      busy(false);
      lastCells = { sheet: sheet, mark: mark, items: changes };
      try { window.localStorage.setItem("nova_cells", JSON.stringify(lastCells)); } catch (e) { }
      var send = [];
      for (var i = 0; i < changes.length; i++) send.push({ addr: sheet + "!" + changes[i].addr, before: changes[i].before, after: changes[i].after });
      api("cellsoriginal", { mode: mode, items: send }, function () { });
      setOrig(changes.length + " cells on " + sheet + " — ↩ Put back original restores them all\n" + changes[0].addr + ": " + changes[0].before + (changes.length > 1 ? "\n…" : ""));
      status("✓ " + changes.length + " cells changed" + (mark ? " (light yellow)" : "") + " · " + note, "good");
    }).catch(xlErr);
  }

  function xlPutBack() {
    var lc = lastCells;
    if (!lc) { try { lc = JSON.parse(window.localStorage.getItem("nova_cells") || "null"); } catch (e) { lc = null; } }
    if (!lc || !lc.items || !lc.items.length) return false;
    busy(true);
    Excel.run(function (ctx) {
      var ws = ctx.workbook.worksheets.getItem(lc.sheet), ranges = [];
      for (var i = 0; i < lc.items.length; i++) { var rg = ws.getRange(lc.items[i].addr); rg.load("values"); ranges.push(rg); }
      return ctx.sync().then(function () {
        var n = 0, kept = 0;
        for (var i = 0; i < lc.items.length; i++) {
          var it = lc.items[i];
          // A cell edited by hand since then is left alone.
          if (trim(ranges[i].values[0][0]) !== trim(it.after)) { kept++; continue; }
          ranges[i].values = [[it.before]]; n++;
          if (lc.mark) { if (!it.fill || it.fill === "#FFFFFF") ranges[i].format.fill.clear(); else ranges[i].format.fill.color = it.fill; }
        }
        return ctx.sync().then(function () { return { n: n, kept: kept }; });
      });
    }).then(function (r) {
      busy(false); lastCells = null;
      try { window.localStorage.removeItem("nova_cells"); } catch (e) { }
      status("↩ " + r.n + " cells put back" + (r.kept ? " · " + r.kept + " edited since then were left as they are" : ""), "good");
    }).catch(xlErr);
    return true;
  }

  function resetPane() {
    show("diff", false); show("diffnote", false); show("keep", false); if ($("verlist")) $("verlist").innerHTML = ""; viewKey = null;
    lastOriginal = ""; lastWasWhole = false; origIdx = 0;
    $("result").value = ""; show("out", false);
    if ($("instr")) $("instr").value = "";
    var o = $("orig"); o.innerHTML = ""; o.appendChild(document.createTextNode("The text before each enhancement is kept here and in Documents\\Nova Enhance\\Originals."));
    if ($("replace")) $("replace").style.display = "";
  }

  function showSubject() {
    var it = Office.context.mailbox && Office.context.mailbox.item, e = $("subj");
    if (!e) return;
    e.innerHTML = "";
    if (it && typeof it.subject === "string") e.appendChild(document.createTextNode("✉ " + it.subject));
  }

  function setOrig(t) { var o = $("orig"); o.innerHTML = ""; o.appendChild(document.createTextNode(t)); }

  function mailInfo(cb) {
    var it = Office.context.mailbox.item;
    it.body.getAsync(Office.CoercionType.Text, function (b) {
      var from = it.from ? (it.from.displayName || it.from.emailAddress) : "";
      cb({ text: b.status === Office.AsyncResultStatus.Succeeded ? b.value : "", subject: it.subject || "", from: from });
    });
  }

  function init(info) {
    isExcel = !!(window.Excel && ((info && info.host === Office.HostType.Excel) || (Office.context && Office.context.host === "Excel")) &&
      Office.context.requirements && Office.context.requirements.isSetSupported("ExcelApi", "1.2"));
    var mb = Office.context && Office.context.mailbox;
    if (mb && mb.item) host = mb.item.itemType === Office.MailboxEnums.ItemType.Message && typeof mb.item.subject === "string" ? "read" : "compose";
    if (mb && mb.item && typeof mb.item.subject !== "string") host = "compose";
    show("doc", host === "doc"); show("mailread", host === "read"); show("mailcompose", host === "compose");
    if (host === "read") { $("insert").style.display = "none"; $("replace").innerHTML = "↩ Reply with this"; }

    api("ping", null, function (r) {
      if (r.error) { status("⚠ " + r.error, "bad"); return; }
      $("ai").appendChild(document.createTextNode(r.ai || "no AI key"));
      $("sign").innerHTML = ""; $("sign").appendChild(document.createTextNode(r.sign + " · build " + r.build));
      if (r.cellReview === false) cellReview = false;
      status(r.ai ? (isExcel ? "Ready — select one or many cells and choose a mode." : "Ready — select text and choose a mode.") : "Add an AI key in Nova Enhance → Settings.", r.ai ? "" : "bad");
    });
    api("langs", null, function (r) {
      var sel = $("lang");
      if (!r.langs) return;
      for (var i = 0; i < r.langs.length; i++) { var o = document.createElement("option"); o.value = r.langs[i].v; o.text = r.langs[i].l; if (r.langs[i].v === r.current) o.selected = true; sel.appendChild(o); }
      var setTr = function () { $("tr").innerHTML = ""; $("tr").appendChild(document.createTextNode("🌐 → " + sel.options[sel.selectedIndex].text)); };
      sel.onchange = setTr; setTr();
    });
    // The Original box starts empty for every email; 🕘 Older still shows earlier ones on request.

    each("button[data-mode]", function (b) { b.onclick = function () { enhance(b.getAttribute("data-mode")); }; });
    if (isExcel) {
      show("xlnote", true);
      $("xlyes").onclick = xlWholeSheet;
      $("xlno").onclick = function () { xlPending = null; show("xlask", false); status("Cancelled"); };
      $("xlstop").onclick = function () { xlStop = true; $("xlcount").appendChild(document.createTextNode(" stopping…")); };
    }

    $("replace").onclick = function () {
      var t = $("result").value;
      if (host === "read") { Office.context.mailbox.item.displayReplyForm(esc(stripClosing(t)).replace(/\n/g, "<br>")); return; }
      putText(t, lastWasWhole, false, function (ok, err) { status(ok ? (host === "compose" ? "✓ Done — to go back: ↩ Put back original or 🕘 Versions (Ctrl + Z doesn't undo it in Outlook)" : "✓ Replaced — Ctrl + Z to undo") : "⚠ " + err, ok ? "good" : "bad"); show("diff", false); show("diffnote", false); show("keep", false); });
    };
    $("keep").onclick = function () { show("out", false); show("diff", false); show("diffnote", false); show("keep", false); status("✗ Kept yours — nothing changed in the email", "good"); };
    $("versions").onclick = function () { viewKey = null; showVersions(); };

    // ---------------------------------------------------------------- 💬 Ask Nova about the email you are writing
    var dLast = "", dLastDraft = "";
    function dAdd(cls, text) { var d = document.createElement("div"); d.className = cls; d.appendChild(document.createTextNode(text)); $("dlog").appendChild(d); $("dlog").scrollTop = 1e6; show("dlog", true); return d; }
    function readDraft(cb) {
      var it = Office.context.mailbox.item, out = { conv: it.conversationId || "" };
      askKey(function (key, subj) {
        out.key = key; out.subject = subj;
        var names = function (r) { var v = r && r.value || []; var a = []; for (var i = 0; i < v.length; i++) a.push(v[i].displayName || v[i].emailAddress); return a.join("; "); };
        var getTo = function (next) { if (it.to && it.to.getAsync) it.to.getAsync(function (r) { out.to = names(r); next(); }); else next(); };
        var getCc = function (next) { if (it.cc && it.cc.getAsync) it.cc.getAsync(function (r) { out.cc = names(r); next(); }); else next(); };
        getTo(function () { getCc(function () {
          it.body.getAsync(Office.CoercionType.Html, function (b) {
            var parts = splitHtml(b.value || "");
            out.own = htmlText(parts.own);
            out.quoted = htmlText(parts.rest);
            cb(out);
          });
        }); });
      });
    }
    function dAsk() {
      var q = trim($("dq").value);
      if (!q) { $("dq").focus(); return; }
      dAdd("q", "You: " + q);
      var w = dAdd("a wait", "Nova is reading your draft, the thread, your emails and project files…");
      $("dask").disabled = true; $("dq").value = "";
      readDraft(function (d) {
        d.q = q;
        api("askdraft", d, function (r) {
          $("dask").disabled = false;
          w.parentNode.removeChild(w);
          if (!r || r.error) { dAdd("a", (r && r.error) || "⚠ Nova Enhance is not answering on this PC."); return; }
          dLast = r.answer || ""; dLastDraft = r.draft || "";
          dAdd("a", dLast);
          var s = r.sources || [], t = [];
          for (var i = 0; i < s.length && i < 8; i++) t.push("[" + s[i].id + "] " + s[i].title);
          if (t.length) dAdd("src", "Sources: " + t.join(" · ") + (r.project ? "  ·  📁 " + r.project : "") + (r.ai ? "  ·  " + r.ai : ""));
          show("dacts", true); show("dnew", true);
          $("dput").innerHTML = dLastDraft ? "✍ Put Nova's text into my draft" : "✍ Put the answer into my draft";
        });
      });
    }
    $("dask").onclick = dAsk;
    $("dq").onkeydown = function (e) { if ((e.keyCode === 13 || e.key === "Enter") && !e.shiftKey) { e.preventDefault(); dAsk(); } };
    $("dput").onclick = function () {
      var text = dLastDraft || dLast.replace(/\s*\[(?:[EADFMH]\d{1,2}(?:\s*,\s*)?)+\]/g, "");
      if (!text) return;
      lastLabel = "💬 Nova's text";
      putText(text, false, true, function (ok, err) { status(ok ? "✍ Put into your draft at the cursor — 🕘 Versions keeps the text before." : "⚠ " + err, ok ? "good" : "bad"); });
    };
    $("dcopy").onclick = function () {
      var ta = document.createElement("textarea"); ta.value = dLastDraft || dLast; document.body.appendChild(ta); ta.select();
      try { document.execCommand("copy"); status("⧉ Copied", "good"); } catch (e) { }
      document.body.removeChild(ta);
    };
    $("dfull").onclick = function () {
      askKey(function (key, subj) {
        api("open", { what: "askdraft", key: key, subject: subj }, function () { status("↗ The conversation continues in Nova's window on your desktop", "good"); });
      });
    };
    $("dnew").onclick = function () { $("dlog").innerHTML = ""; show("dlog", false); show("dacts", false); dLast = dLastDraft = ""; status("🧹 Cleared — a new conversation about this email", "good");
      askKey(function (key) { api("askdraft/new", { key: key }, function () { }); }); };
    $("insert").onclick = function () { putText($("result").value, false, true, function (ok, err) { status(ok ? "✓ Inserted" : "⚠ " + err, ok ? "good" : "bad"); }); };
    $("copy").onclick = function () { var t = $("result"); t.select(); try { document.execCommand("copy"); status("⧉ Copied", "good"); } catch (e) { status("Press Ctrl + C to copy"); } };
    $("putback").onclick = function () {
      if (isExcel && xlPutBack()) return;
      if (host === "compose") {
        // The draft exactly as it was before Nova's first change (formatting and signature included).
        draftKey(function (key) {
          api("versions/list", { key: key }, function (r) {
            var vs = r.versions || [];
            if (!vs.length) { status("No version of this draft yet"); return; }
            var first = vs[vs.length - 1];
            api("versions/get", { key: key, i: first.i }, function (g) {
              if (!g.html) { status("⚠ Could not read it", "bad"); return; }
              snapshot("Before putting back your original", function () {
                Office.context.mailbox.item.body.setAsync(g.html, { coercionType: Office.CoercionType.Html }, function (res) {
                  status(res.status === Office.AsyncResultStatus.Succeeded ? "↩ Your original draft is back (" + first.when + ")" : "⚠ " + (res.error ? res.error.message : ""), res.status === Office.AsyncResultStatus.Succeeded ? "good" : "bad");
                });
              });
            });
          });
        });
        return;
      }
      var t = lastOriginal || (originals[origIdx] ? originals[origIdx].text : "");
      if (!t) { status("No original yet"); return; }
      if (host === "read") { $("result").value = t; show("out", true); return; }
      putText(t, lastWasWhole, false, function (ok, err) { status(ok ? "↩ Original put back" : "⚠ " + err, ok ? "good" : "bad"); });
    };
    $("older").onclick = function () {
      api("originals", null, function (r) {
        originals = r.originals || [];
        if (!originals.length) { status("No original saved yet"); return; }
        origIdx = (origIdx + 1) % originals.length; lastOriginal = originals[origIdx].text;
        setOrig(originals[origIdx].when + " · " + originals[origIdx].mode + "\n" + originals[origIdx].text);
      });
    };
    $("settings").onclick = function () { api("open", { what: "settings" }, function () { }); return false; };
    $("clear").onclick = $("clear2").onclick = function () { resetPane(); status("🧹 Cleared", "good"); };
    // Pinned pane: another email selected → everything from the previous one is cleared.
    try {
      if (Office.context.mailbox && Office.context.mailbox.addHandlerAsync && Office.EventType.ItemChanged)
        Office.context.mailbox.addHandlerAsync(Office.EventType.ItemChanged, function () {
          resetPane();
          var mb = Office.context.mailbox;
          if (mb.item) { host = typeof mb.item.subject === "string" ? "read" : "compose"; show("mailread", host === "read"); show("mailcompose", host === "compose"); }
          showSubject();
    if (host === "compose") show("verbox", true); else show("verbox", false);
          status(mb.item ? "Ready — this email." : "Select an email.");
        });
    } catch (e) { }
    showSubject();
    show("verbox", host === "compose");

    if (host === "read") {
      $("sum").onclick = function () { status("✦ Summarising…"); mailInfo(function (m) { api("summarize", m, function (r) { if (r.error) { status("⚠ " + r.error, "bad"); return; } $("result").value = r.result; $("outlabel").innerHTML = "Summary"; show("out", true); $("replace").style.display = "none"; status("✓ Summary ready", "good"); }); }); };
      $("draft").onclick = function () { status("✦ Writing the reply…"); mailInfo(function (m) { m.instruction = $("instr").value; api("draft", m, function (r) { if (r.error) { status("⚠ " + r.error, "bad"); return; } $("result").value = r.result; $("outlabel").innerHTML = "Draft reply — check it"; show("out", true); $("replace").style.display = ""; status("✓ Draft ready — ↩ Reply with this opens the reply", "good"); }); }); };
      $("trmail").onclick = function () { status("🌐 Translating…"); mailInfo(function (m) { api("enhance", { mode: "translate", text: m.text, lang: $("lang") ? $("lang").value : "", host: "read" }, function (r) { if (r.error) { status("⚠ " + r.error, "bad"); return; } $("result").value = r.result; $("outlabel").innerHTML = ""; $("outlabel").appendChild(document.createTextNode(r.label)); show("out", true); $("replace").style.display = "none"; status("✓ Translated", "good"); }); }); };
      $("asknova").onclick = function () { api("open", { what: "askmail", subject: Office.context.mailbox.item.subject || "" }, function () { status("💬 Talk about this email opened on your desktop"); }); };
      // Opened from the ✦ Nova tab: ✦ Summary / ✦ Draft reply start by themselves.
      var want = /[?&]do=(summary|draft)/.exec(location.search || "");
      if (want) setTimeout(function () { $(want[1] === "summary" ? "sum" : "draft").click(); }, 300);
    }
  }

  if (window.Office) {
    if (Office.onReady) Office.onReady(function (info) { init(info); });
    else Office.initialize = function () { init(); };
  }
})();
