/* Back-office : tri des lignes d'une liste à la souris (ou au doigt, par la poignée).
   La ligne saisie suit le pointeur, les autres s'écartent en direct ; au lâcher, Blazor enregistre le nouvel ordre
   (méthode .NET « Reorder(from, to) »). Le DOM n'est jamais réordonné ici : seulement des transform. */
(function (w) {
  'use strict';
  var IGNORE = 'button, a, input, select, textarea, label, .kebab';

  function init(list, dotnet) {
    if (!list || list.__lkSort) return;
    list.__lkSort = true;
    list.addEventListener('pointerdown', function (e) { start(list, dotnet, e); });
    list.addEventListener('dragstart', function (e) { e.preventDefault(); });   // pas de glisser natif des vignettes
  }

  function start(list, dotnet, e) {
    if (e.button !== 0 || list.__lkBusy) return;
    if (e.target.closest(IGNORE)) return;
    if (list.querySelector('.kebab-menu')) return;                  // un menu est ouvert
    var row = e.target.closest('[data-sort-row]');
    if (!row || !list.contains(row)) return;
    if (e.pointerType !== 'mouse' && !e.target.closest('.grip')) return;  // au doigt : par la poignée, pour garder le défilement

    var rows = Array.prototype.slice.call(list.querySelectorAll('[data-sort-row]'));
    var from = rows.indexOf(row);
    if (from < 0 || rows.length < 2) return;
    var rects = rows.map(function (r) { return r.getBoundingClientRect(); });
    var gap = rects.length > 1 ? Math.max(0, rects[1].top - rects[0].bottom) : 6;
    var size = rects[from].height + gap;
    var startY = e.clientY, dy = 0, to = from, active = false;

    function target() {
      var center = rects[from].top + rects[from].height / 2 + dy, t = from, i;
      for (i = from + 1; i < rows.length; i++) if (center > rects[i].top + rects[i].height / 2) t = i;
      for (i = from - 1; i >= 0; i--) if (center < rects[i].top + rects[i].height / 2) t = i;
      return t;
    }

    function layout() {
      rows.forEach(function (r, i) {
        if (i === from) return;
        var shift = (to > from && i > from && i <= to) ? -size : (to < from && i >= to && i < from) ? size : 0;
        r.style.transform = shift ? 'translateY(' + shift + 'px)' : '';
      });
    }

    function finalOffset() {
      if (to > from) return rects[to].bottom - rects[from].bottom;
      if (to < from) return rects[to].top - rects[from].top;
      return 0;
    }

    function move(ev) {
      dy = ev.clientY - startY;
      if (!active) {
        if (Math.abs(dy) < 4) return;
        active = true;
        list.classList.add('sorting');
        row.classList.add('lifted');
        try { row.setPointerCapture(e.pointerId); } catch (x) {}
      }
      ev.preventDefault();
      var minY = rects[0].top - rects[from].top, maxY = rects[rows.length - 1].bottom - rects[from].bottom;
      dy = Math.max(minY - 20, Math.min(maxY + 20, dy));
      row.style.transform = 'translateY(' + dy + 'px)';
      var t = target();
      if (t !== to) { to = t; layout(); }
    }

    function clear() {
      rows.forEach(function (r) { r.style.transition = 'none'; r.style.transform = ''; });
      list.classList.remove('sorting');
      row.classList.remove('lifted');
      requestAnimationFrame(function () { rows.forEach(function (r) { r.style.transition = ''; }); });
      list.__lkBusy = false;
    }

    function up() {
      w.removeEventListener('pointermove', move);
      w.removeEventListener('pointerup', up);
      w.removeEventListener('pointercancel', up);
      if (!active) return;
      list.__lkBusy = true;
      row.classList.add('settling');
      row.style.transform = 'translateY(' + finalOffset() + 'px)';
      setTimeout(function () {
        row.classList.remove('settling');
        if (to === from) { clear(); return; }
        // Les décalages ne sont retirés qu'au moment où Blazor applique le nouvel ordre dans le DOM
        // (l'observateur s'exécute avant l'affichage) : sinon les lignes reviennent un instant à l'ancien ordre.
        var done = false;
        var obs = new MutationObserver(function () { finish(); });
        function finish() { if (done) return; done = true; obs.disconnect(); clear(); }
        obs.observe(list, { childList: true, characterData: true, subtree: true });
        dotnet.invokeMethodAsync('Reorder', from, to).then(function () { setTimeout(finish, 400); }, finish);
      }, 160);
    }

    w.addEventListener('pointermove', move, { passive: false });
    w.addEventListener('pointerup', up);
    w.addEventListener('pointercancel', up);
  }

  w.linkiiSort = { init: init };
})(window);
