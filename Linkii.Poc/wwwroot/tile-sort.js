/* Back-office : réorganiser les tuiles de l'aperçu de la borne par glisser-déposer (souris ou stylet).
   La tuile saisie suit le pointeur, les autres glissent vers leur nouvel emplacement en direct (transform + transition) ;
   au lâcher, la tuile se pose, puis Blazor enregistre l'ordre (méthode .NET « ReorderTile(id, position) ») et les transformations sont retirées.
   Le DOM n'est jamais réordonné ici. Au doigt : pas de glisser (il garde le défilement de l'aperçu). */
(function (w) {
  'use strict';
  var IGNORE = 'button, a, input, select, textarea';

  function init(grid, dotnet) {
    if (!grid) return;
    grid.__lkDn = dotnet;
    if (grid.__lkTs) return;
    grid.__lkTs = true;
    grid.addEventListener('pointerdown', function (e) { start(grid, e); });
    grid.addEventListener('dragstart', function (e) { e.preventDefault(); });
  }

  function start(grid, e) {
    if (e.button !== 0 || e.pointerType === 'touch' || grid.__lkBusy) return;
    if (e.target.closest(IGNORE)) return;
    var tile = e.target.closest('[data-tile]');
    if (!tile || !grid.contains(tile)) return;
    var tiles = [].slice.call(grid.querySelectorAll('[data-tile]'));
    var from = tiles.indexOf(tile);
    if (tiles.length < 2 || from < 0) return;
    var rects = tiles.map(function (t) { return t.getBoundingClientRect(); });
    var sx = e.clientX, sy = e.clientY, dx = 0, dy = 0, to = from, active = false;

    function nearest() {   // emplacement dont le centre est le plus proche du centre de la tuile déplacée
      var cx = rects[from].left + rects[from].width / 2 + dx, cy = rects[from].top + rects[from].height / 2 + dy, best = from, bd = Infinity;
      rects.forEach(function (r, i) {
        var d = Math.pow(cx - (r.left + r.width / 2), 2) + Math.pow(cy - (r.top + r.height / 2), 2);
        if (d < bd) { bd = d; best = i; }
      });
      return best;
    }

    function layout() {   // les autres tuiles prennent la place laissée vide
      tiles.forEach(function (t, i) {
        if (i === from) return;
        var slot = i;
        if (to > from && i > from && i <= to) slot = i - 1;
        else if (to < from && i >= to && i < from) slot = i + 1;
        var tx = rects[slot].left - rects[i].left, ty = rects[slot].top - rects[i].top;
        t.style.transform = (tx || ty) ? 'translate(' + tx + 'px,' + ty + 'px)' : '';
      });
    }

    function clear() {
      tiles.forEach(function (t) { t.style.transform = ''; t.classList.remove('lifted'); });
      grid.classList.remove('k-sorting', 'k-settle');
      grid.__lkBusy = false;
    }

    function move(ev) {
      dx = ev.clientX - sx; dy = ev.clientY - sy;
      if (!active) {
        if (Math.hypot(dx, dy) < 5) return;
        active = true; grid.__lkBusy = true;
        grid.classList.add('k-sorting'); tile.classList.add('lifted');
      }
      tile.style.transform = 'translate(' + dx + 'px,' + dy + 'px) scale(1.03)';
      var n = nearest();
      if (n !== to) { to = n; layout(); }
    }

    function end(ev) {
      document.removeEventListener('pointermove', move); document.removeEventListener('pointerup', end); document.removeEventListener('pointercancel', end);
      if (!active) return;
      if (ev.type === 'pointercancel') { to = from; layout(); }
      // le clic qui suit le lâcher ne doit ni sélectionner ni ouvrir la tuile
      var stop = function (c) { c.stopPropagation(); c.preventDefault(); };
      tile.addEventListener('click', stop, { capture: true, once: true });
      setTimeout(function () { tile.removeEventListener('click', stop, true); }, 80);
      // pose : la tuile glisse jusqu'à son emplacement
      grid.classList.add('k-settle');
      tile.style.transform = 'translate(' + (rects[to].left - rects[from].left) + 'px,' + (rects[to].top - rects[from].top) + 'px)';
      setTimeout(function () {
        if (to === from) { clear(); return; }
        var mo = new MutationObserver(function () { mo.disconnect(); clear(); });   // Blazor a réordonné le DOM : on retire les transformations
        mo.observe(grid, { childList: true });
        setTimeout(function () { mo.disconnect(); clear(); }, 1500);
        grid.__lkDn.invokeMethodAsync('ReorderTile', tile.getAttribute('data-tile'), to).catch(function () { clear(); });
      }, 230);
    }

    document.addEventListener('pointermove', move);   // sur le document : le pointeur peut quitter la tuile en glissant
    document.addEventListener('pointerup', end);
    document.addEventListener('pointercancel', end);
  }

  w.linkiiTileSort = { init: init };
})(window);
