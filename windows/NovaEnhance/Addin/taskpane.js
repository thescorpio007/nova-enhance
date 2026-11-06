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
        Office.context.mailbox.item.body.getAsync(Office.CoercionType.Text, function (b) { cb(b.status === Office.AsyncResultStatus.Succeeded ? b.value : "", true); });
      });
      return;
    }
    Office.context.document.getSelectedDataAsync(Office.CoercionType.Text, function (r) {
      cb(r.status === Office.AsyncResultStatus.Succeeded ? (r.value || "") : "", false);
    });
  }

  function putText(text, whole, insert, cb) {
    var done = function (r) { cb(r.status === Office.AsyncResultStatus.Succeeded, r.error ? r.error.message : ""); };
    if (host === "compose") {
      if (whole && !insert) Office.context.mailbox.item.body.setAsync(text, { coercionType: Office.CoercionType.Text }, done);
      else Office.context.mailbox.item.body.setSelectedDataAsync(text, { coercionType: Office.CoercionType.Text }, done);
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
        lastOriginal = text; lastWasWhole = whole;
        $("result").value = r.result;
        $("outlabel").innerHTML = ""; $("outlabel").appendChild(document.createTextNode(r.label + " · " + r.ai));
        show("out", true);
        setOrig(text);
        status("✓ Done — check it, then ⇄ Replace. The original is saved.", "good");
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
    api("originals", null, function (r) { originals = r.originals || []; if (originals.length) setOrig(originals[0].text); });

    each("button[data-mode]", function (b) { b.onclick = function () { enhance(b.getAttribute("data-mode")); }; });
    if (isExcel) {
      show("xlnote", true);
      $("xlyes").onclick = xlWholeSheet;
      $("xlno").onclick = function () { xlPending = null; show("xlask", false); status("Cancelled"); };
      $("xlstop").onclick = function () { xlStop = true; $("xlcount").appendChild(document.createTextNode(" stopping…")); };
    }

    $("replace").onclick = function () {
      var t = $("result").value;
      if (host === "read") { Office.context.mailbox.item.displayReplyForm(t.replace(/\n/g, "<br>")); return; }
      putText(t, lastWasWhole, false, function (ok, err) { status(ok ? "✓ Replaced — Ctrl + Z in Office to undo" : "⚠ " + err, ok ? "good" : "bad"); });
    };
    $("insert").onclick = function () { putText($("result").value, false, true, function (ok, err) { status(ok ? "✓ Inserted" : "⚠ " + err, ok ? "good" : "bad"); }); };
    $("copy").onclick = function () { var t = $("result"); t.select(); try { document.execCommand("copy"); status("⧉ Copied", "good"); } catch (e) { status("Press Ctrl + C to copy"); } };
    $("putback").onclick = function () {
      if (isExcel && xlPutBack()) return;
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

    if (host === "read") {
      $("sum").onclick = function () { status("✦ Summarising…"); mailInfo(function (m) { api("summarize", m, function (r) { if (r.error) { status("⚠ " + r.error, "bad"); return; } $("result").value = r.result; $("outlabel").innerHTML = "Summary"; show("out", true); $("replace").style.display = "none"; status("✓ Summary ready", "good"); }); }); };
      $("draft").onclick = function () { status("✦ Writing the reply…"); mailInfo(function (m) { m.instruction = $("instr").value; api("draft", m, function (r) { if (r.error) { status("⚠ " + r.error, "bad"); return; } $("result").value = r.result; $("outlabel").innerHTML = "Draft reply — check it"; show("out", true); $("replace").style.display = ""; status("✓ Draft ready — ↩ Reply with this opens the reply", "good"); }); }); };
      $("trmail").onclick = function () { status("🌐 Translating…"); mailInfo(function (m) { api("enhance", { mode: "translate", text: m.text, lang: $("lang") ? $("lang").value : "", host: "read" }, function (r) { if (r.error) { status("⚠ " + r.error, "bad"); return; } $("result").value = r.result; $("outlabel").innerHTML = ""; $("outlabel").appendChild(document.createTextNode(r.label)); show("out", true); $("replace").style.display = "none"; status("✓ Translated", "good"); }); }); };
      $("asknova").onclick = function () { api("open", { what: "ask" }, function () { status("🔍 Ask Nova opened on your desktop"); }); };
    }
  }

  if (window.Office) {
    if (Office.onReady) Office.onReady(function (info) { init(info); });
    else Office.initialize = function () { init(); };
  }
})();
