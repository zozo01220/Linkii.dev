/* Back-office : réorganiser les tuiles de l'aperçu de la borne par glisser-déposer (souris ou stylet).
   La tuile saisie suit le pointeur, les autres glissent vers leur nouvel emplacement en direct (transform + transition) ;
   au lâcher, la tuile se pose, puis Blazor enregistre l'ordre (méthode .NET « ReorderTile(id, position) ») et les transformations sont retirées.
   Le DOM n'est jamais réordonné ici. Au doigt : pas de glisser (il garde le défilement de l'aperçu).
   Robustesse : le pointeur est capturé (le lâcher hors de la fenêtre est bien reçu), Échap ou la perte de focus annulent,
   l'aperçu défile tout seul quand on approche de son bord, et un second glisser est refusé tant que l'ordre n'est pas enregistré. */
(function (w) {
  'use strict';
  var IGNORE = 'button, a, input, select, textarea';
  var EDGE = 56, SPEED = 14, SETTLE_MS = 220;

  function init(grid, dotnet) {
    if (!grid) return;
    grid.__lkDn = dotnet;
    if (grid.__lkTs) return;
    grid.__lkTs = true;
    grid.addEventListener('pointerdown', function (e) { start(grid, e); });
    grid.addEventListener('dragstart', function (e) { e.preventDefault(); });
  }

  /* Ancêtre qui défile (l'aperçu de l'accueil), ou null. */
  function scroller(el) {
    for (var p = el.parentElement; p && p !== document.body; p = p.parentElement) {
      var oy = getComputedStyle(p).overflowY;
      if ((oy === 'auto' || oy === 'scroll') && p.scrollHeight > p.clientHeight + 1) return p;
    }
    return null;
  }

  function start(grid, e) {
    if (e.button !== 0 || e.pointerType === 'touch' || grid.__lkBusy) return;
    if (e.target.closest(IGNORE)) return;
    var tile = e.target.closest('[data-tile]');
    if (!tile || !grid.contains(tile)) return;
    var tiles = [].slice.call(grid.querySelectorAll('[data-tile]'));
    var from = tiles.indexOf(tile);
    if (tiles.length < 2 || from < 0) return;

    var box = scroller(grid), top0 = box ? box.scrollTop : 0;
    var rects = tiles.map(function (t) { return t.getBoundingClientRect(); });   // emplacements, dans le repère de la page au moment de la saisie
    var sx = e.clientX, sy = e.clientY, px = sx, py = sy, to = from, active = false, raf = 0, ended = false;

    function sc() { return box ? box.scrollTop - top0 : 0; }   // défilement depuis la saisie

    function nearest(dx, dy) {   // emplacement dont le centre est le plus proche du centre de la tuile déplacée
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

    function place() {   // la tuile saisie suit le pointeur (défilement compris) ; l'emplacement visé est recalculé
      var dx = px - sx, dy = py - sy + sc();
      tile.style.transform = 'translate(' + dx + 'px,' + dy + 'px) scale(1.03)';
      var n = nearest(dx, dy);
      if (n !== to) { to = n; layout(); }
    }

    function clear() {
      tiles.forEach(function (t) { t.style.transform = ''; t.classList.remove('lifted'); });
      grid.classList.remove('k-sorting', 'k-settle');
      document.body.classList.remove('k-dragging');
      grid.__lkBusy = false;
    }

    function listen(on) {
      var f = on ? 'addEventListener' : 'removeEventListener';
      document[f]('pointermove', move, true); document[f]('pointerup', end, true); document[f]('pointercancel', end, true);
      document[f]('keydown', key, true); w[f]('blur', cancel);
    }

    function loop() {   // défilement automatique près du bord haut ou bas de l'aperçu
      raf = 0;
      if (!active || ended || !box) return;
      var r = box.getBoundingClientRect(), v = 0;
      if (py < r.top + EDGE) v = -Math.ceil(SPEED * Math.min(1, (r.top + EDGE - py) / EDGE));
      else if (py > r.bottom - EDGE) v = Math.ceil(SPEED * Math.min(1, (py - (r.bottom - EDGE)) / EDGE));
      if (v) {
        var before = box.scrollTop;
        box.scrollTop += v;
        if (box.scrollTop !== before) place();
        raf = requestAnimationFrame(loop);
      }
    }

    function move(ev) {
      if (ev.pointerId !== e.pointerId || ended) return;
      if (ev.buttons === 0) { end(ev); return; }   // le bouton a été relâché hors de la page sans que l'on le sache
      px = ev.clientX; py = ev.clientY;
      if (!active) {
        if (Math.hypot(px - sx, py - sy) < 5) return;
        active = true; grid.__lkBusy = true;
        grid.classList.add('k-sorting'); tile.classList.add('lifted'); document.body.classList.add('k-dragging');
        try { tile.setPointerCapture(e.pointerId); } catch (x) {}
      }
      place();
      if (!raf && box) raf = requestAnimationFrame(loop);
    }

    function key(ev) { if (ev.key === 'Escape' && active) { ev.preventDefault(); ev.stopPropagation(); cancel(); } }
    function cancel() { if (active && !ended) { to = from; finish(true); } else if (!active) { ended = true; listen(false); } }
    function end(ev) { if (ev.pointerId !== e.pointerId) return; finish(ev.type === 'pointercancel'); }

    function finish(aborted) {
      if (ended) return;
      ended = true; listen(false);
      if (raf) { cancelAnimationFrame(raf); raf = 0; }
      try { tile.releasePointerCapture(e.pointerId); } catch (x) {}
      if (!active) return;
      if (aborted) { to = from; layout(); }
      // le clic qui suit le lâcher ne doit ni sélectionner ni ouvrir la tuile
      var stop = function (c) { c.stopPropagation(); c.preventDefault(); };
      tile.addEventListener('click', stop, { capture: true, once: true });
      setTimeout(function () { tile.removeEventListener('click', stop, true); }, 120);
      // pose : la tuile glisse jusqu'à son emplacement (en tenant compte du défilement)
      grid.classList.add('k-settle');
      tile.style.transform = 'translate(' + (rects[to].left - rects[from].left) + 'px,' + (rects[to].top - rects[from].top + 0) + 'px)';
      setTimeout(function () {
        if (to === from) { clear(); return; }
        var mo = new MutationObserver(function () { mo.disconnect(); clear(); });   // Blazor a réordonné le DOM : on retire les transformations
        mo.observe(grid, { childList: true });
        setTimeout(function () { mo.disconnect(); clear(); }, 1500);
        grid.__lkDn.invokeMethodAsync('ReorderTile', tile.getAttribute('data-tile'), to).catch(function () { mo.disconnect(); clear(); });
      }, SETTLE_MS + 20);
    }

    listen(true);
  }

  w.linkiiTileSort = { init: init };
})(window);
