// ↔ Stretchable columns for every Nova table: drag a column edge, double-click it to fit the text, widths remembered per table.
// Designed by A.HAMROUNI
(function () {
  function load(k, d) { try { var v = JSON.parse(localStorage.getItem(k) || "null"); return v == null ? d : v; } catch (e) { return d; } }
  function save(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch (e) { } }
  var st = document.createElement("style");
  st.textContent = ".rzh{position:absolute;top:0;right:-4px;width:9px;height:100%;cursor:col-resize;z-index:3}.rzh:hover,.rzh.on{background:rgba(124,108,255,.45)}" +
    "table.rzt{table-layout:fixed}table.rzt td{overflow:hidden;text-overflow:ellipsis}table.rzt td.wrap{white-space:normal;word-break:break-word}";
  document.head.appendChild(st);
  var cv = document.createElement("canvas").getContext("2d");

  function fit(table, idx) {
    var max = 40, rows = table.rows;
    for (var i = 0; i < rows.length && i < 500; i++) {
      var c = rows[i].cells[idx]; if (!c) continue;
      var cs = getComputedStyle(c);
      cv.font = cs.fontWeight + " " + cs.fontSize + " " + cs.fontFamily;
      var w = cv.measureText((c.textContent || "").trim()).width + (parseFloat(cs.paddingLeft) || 0) + (parseFloat(cs.paddingRight) || 0) + 16;
      if (w > max) max = w;
    }
    return Math.min(1000, Math.ceil(max));
  }

  function apply(table, ids, w) {
    var head = table.rows[0]; if (!head) return;
    var sum = 0, any = false;
    for (var i = 0; i < head.cells.length; i++) { var v = w[ids[i]]; if (v) { head.cells[i].style.width = v + "px"; sum += v; any = true; } else sum += head.cells[i].offsetWidth || 110; }
    if (any) { table.classList.add("rzt"); table.style.width = Math.max(sum, table.parentNode ? table.parentNode.clientWidth - 2 : sum) + "px"; }
  }

  window.ColResize = {
    hidden: function (key) { return load("ch." + key, []); },
    setHidden: function (key, list) { save("ch." + key, list); },
    reset: function (key) { save("cw." + key, {}); },
    /** table: its first row is the header · key: the table's name · ids: one id per header cell (default: the header text) */
    attach: function (table, key, ids) {
      var head = table.rows[0]; if (!head) return;
      ids = ids || [].map.call(head.cells, function (c) { return (c.textContent || "").replace(/[▲▼]/g, "").trim(); });
      var w = load("cw." + key, {});
      apply(table, ids, w);
      [].forEach.call(head.cells, function (th, i) {
        if (getComputedStyle(th).position === "static") th.style.position = "relative";
        var h = document.createElement("span"); h.className = "rzh"; h.title = "drag to widen · double-click to fit the text";
        h.onclick = function (e) { e.stopPropagation(); };
        h.ondblclick = function (e) {
          e.stopPropagation();
          [].forEach.call(head.cells, function (c, k) { if (!w[ids[k]]) w[ids[k]] = c.offsetWidth; });
          w[ids[i]] = fit(table, i); apply(table, ids, w); save("cw." + key, w);
        };
        h.onmousedown = function (e) {
          e.preventDefault(); e.stopPropagation();
          [].forEach.call(head.cells, function (c, k) { if (!w[ids[k]]) w[ids[k]] = c.offsetWidth; });
          var x0 = e.clientX, w0 = w[ids[i]]; h.classList.add("on");
          function mv(ev) { w[ids[i]] = Math.max(32, w0 + ev.clientX - x0); apply(table, ids, w); }
          function up() { document.removeEventListener("mousemove", mv); document.removeEventListener("mouseup", up); h.classList.remove("on"); save("cw." + key, w); }
          document.addEventListener("mousemove", mv); document.addEventListener("mouseup", up);
        };
        th.appendChild(h);
      });
    }
  };
})();
