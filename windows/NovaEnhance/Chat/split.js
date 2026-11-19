// ⇔ Drag the edge of a side panel to make it wider or narrower (remembered). Elements: data-split="left" (handle on its right edge)
// or data-split="right" (handle on its left edge). Designed by A.HAMROUNI
(function () {
  function remember(key, v) { try { localStorage.setItem("split_" + key, String(v)); } catch (e) { } }
  function recall(key) { try { return parseInt(localStorage.getItem("split_" + key), 10) || 0; } catch (e) { return 0; } }
  function init() {
    [].slice.call(document.querySelectorAll("[data-split]")).forEach(function (el) {
      var side = el.getAttribute("data-split"), key = el.id || side;
      var min = parseInt(el.getAttribute("data-min"), 10) || 160, max = parseInt(el.getAttribute("data-max"), 10) || 700;
      var w = recall(key); if (w) setW(w);
      function setW(px) {
        px = Math.max(min, Math.min(max, px));
        el.style.width = px + "px"; el.style.flex = "0 0 " + px + "px";
        var grid = el.parentElement; if (grid && getComputedStyle(grid).display === "grid" && el.getAttribute("data-col") != null) {
          var cols = getComputedStyle(grid).gridTemplateColumns.split(" "); cols[+el.getAttribute("data-col")] = px + "px"; grid.style.gridTemplateColumns = cols.join(" ");
        }
      }
      var h = document.createElement("div");
      h.title = "Drag to resize";
      h.style.cssText = "position:absolute;top:0;bottom:0;width:6px;cursor:col-resize;z-index:50;" + (side === "left" ? "right:-3px" : "left:-3px");
      h.onmouseenter = function () { h.style.background = "rgba(124,108,255,.35)"; };
      h.onmouseleave = function () { if (!drag) h.style.background = ""; };
      if (getComputedStyle(el).position === "static") el.style.position = "relative";
      el.appendChild(h);
      var drag = false, x0 = 0, w0 = 0;
      h.onmousedown = function (e) { drag = true; x0 = e.clientX; w0 = el.getBoundingClientRect().width; document.body.style.userSelect = "none"; e.preventDefault(); };
      window.addEventListener("mousemove", function (e) { if (!drag) return; setW(side === "left" ? w0 + (e.clientX - x0) : w0 - (e.clientX - x0)); });
      window.addEventListener("mouseup", function () { if (!drag) return; drag = false; h.style.background = ""; document.body.style.userSelect = ""; remember(key, Math.round(el.getBoundingClientRect().width)); });
    });
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init); else init();
})();
