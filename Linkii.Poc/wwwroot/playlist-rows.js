/* Page d'une liste : lignes verticales et colonne « Ajouter » (maquette maquettes/liste-page-epuree.html).
   Glisser aux événements pointeur (souris, doigt, stylet) :
   - la ligne (ou le contenu de la colonne de droite) saisie est portée par le pointeur (légère inclinaison, ombre) ;
   - les autres lignes glissent d'un cran (transform, 0,25 s) pour lui faire place ; aucun trait ni emplacement dessiné ;
   - au lâcher, la page enregistre : DropAdd(clé, position) pour un contenu de la colonne, MoveItem(de, vers) pour une ligne.
   Le DOM n'est jamais réordonné ici : seuls des transform sont posés, retirés dès que la page a redessiné la liste.
   Échap annule. La page défile toute seule près de ses bords. */
(function (w, d) {
  'use strict';
  var EASE = 'cubic-bezier(.2,.8,.2,1)';
  var drag = null;
  var ROW = '.le-row[data-i]';

  function reduced() { return !!(w.matchMedia && w.matchMedia('(prefers-reduced-motion: reduce)').matches); }
  function ms() { return reduced() ? 0 : 250; }

  function init(root, dotnet) {
    if (!root || root.__lkRows) return;
    root.__lkRows = true;
    root.addEventListener('dragstart', function (e) { if (e.target && e.target.closest && e.target.closest('.le-row, .le-it')) e.preventDefault(); });   // jamais de glisser natif d'image ou de texte
    root.addEventListener('pointerdown', function (e) {
      if (drag || (e.pointerType === 'mouse' && e.button !== 0)) return;
      var t = e.target;
      if (!t || !t.closest) return;
      var row = t.closest('.le-row[data-drag]');
      var tile = !row && t.closest('.le-it[data-add]');
      var inter = t.closest('button, input, select, textarea, a, label');
      // durée, ⋯, crayon, saisie : ils gardent leur geste ; le nom, l'image et le reste de la ligne saisissent la ligne
      if (inter && inter !== tile) return;
      var list = root.querySelector('.le-card');
      var empty = !list ? root.querySelector('.le-empty') : null;   // liste vide : toute la zone de dépôt reçoit le contenu
      if ((!list && !empty) || (!row && !tile)) return;
      var el = row || tile, r = el.getBoundingClientRect();
      drag = { root: root, dotnet: dotnet, list: list || empty, empty: !!empty, started: false, x0: e.clientX, y0: e.clientY, px: e.clientX, py: e.clientY, kind: row ? 'move' : 'add', el: el, ox: e.clientX - r.left, oy: e.clientY - r.top, k: null, placed: undefined, raf: 0 };
      if (row) { drag.from = +row.getAttribute('data-i'); drag.k = drag.from; }
      else { drag.key = tile.getAttribute('data-add'); drag.ox = Math.min(60, drag.ox); drag.oy = Math.min(30, drag.oy); }
      d.addEventListener('pointermove', onMove);
      d.addEventListener('pointerup', onUp);
      d.addEventListener('pointercancel', onCancel);
      d.addEventListener('keydown', onKey);
    });
  }

  function begin() {
    drag.started = true;
    d.body.classList.add('le-dragging');
    var r = drag.el.getBoundingClientRect();
    var g = drag.el.cloneNode(true);
    g.removeAttribute('data-i'); g.removeAttribute('data-id'); g.removeAttribute('data-add'); g.removeAttribute('data-drag'); g.removeAttribute('data-fresh');
    [].forEach.call(g.querySelectorAll('.le-menu, .kebab-backdrop, .le-more'), function (x) { x.remove(); });
    g.classList.remove('new', 'fresh');
    g.classList.add('le-ghost');
    g.style.width = r.width + 'px';
    d.body.appendChild(g);
    drag.ghost = g;
    if (drag.kind === 'move') drag.el.style.visibility = 'hidden';
    else drag.el.classList.add('dragging');
    // positions de mise en page des lignes (les transform en cours ne les changent pas), en coordonnées de la page
    drag.rows = [].slice.call(drag.root.querySelectorAll(ROW));
    drag.base = drag.rows.map(function (x) { var b = x.getBoundingClientRect(); return { top: b.top + w.scrollY, h: b.height }; });
    drag.H = drag.rows.length > 1 ? drag.base[1].top - drag.base[0].top : drag.rows.length ? drag.base[0].h : 92;
    var lr = drag.list.getBoundingClientRect();
    drag.left = lr.left;
    drag.loop = drag.root.querySelector('.le-loop');
    drag.raf = w.requestAnimationFrame(tick);
  }

  function setShift(el, px, animate) {
    if (!el) return;
    el.style.transition = animate && !reduced() ? 'transform ' + ms() + 'ms ' + EASE : 'none';
    el.style.transform = px ? 'translateY(' + px + 'px)' : '';
  }

  // Décale les lignes selon la position du pointeur : les autres lignes glissent pour faire place
  function update() {
    if (drag.empty) {   // liste vide : la zone de dépôt s'allume, le contenu s'y pose en position 1
      var zr = drag.list.getBoundingClientRect();
      var inz = drag.kind === 'add' && drag.px > zr.left && drag.px < zr.right && drag.py > zr.top - 30 && drag.py < zr.bottom + 30;
      drag.k = inz ? 0 : null;
      drag.list.classList.toggle('over', inz);
      drag.target = inz ? { x: zr.left + 20, y: zr.top + 20 } : null;
      return;
    }
    var lr = drag.list.getBoundingClientRect(), px = drag.px, py = drag.py;
    var over = px > lr.left - 30 && px < lr.right + 30 && py > lr.top - 60 && py < lr.bottom + 60;
    var k = drag.k;
    if (over || drag.kind === 'move') {
      k = 0;
      var y = py + w.scrollY;
      drag.rows.forEach(function (r, j) { if (j !== drag.from && drag.base[j].top + drag.base[j].h / 2 < y) k++; });
    } else k = null;   // hors de la liste : le contenu n'ajoute rien, les lignes se referment
    if (k !== drag.placed) {
      drag.placed = k;
      var H = drag.H;
      drag.rows.forEach(function (s, j) {
        var sh = 0;
        if (drag.kind === 'move') {
          if (j !== drag.from) { var m = j < drag.from ? j : j - 1, fin = m < k ? m : m + 1; sh = (fin - j) * H; }
        } else if (k != null && j >= k) sh = H;
        setShift(s, sh, true);
      });
      setShift(drag.loop, drag.kind === 'add' && k != null ? H : 0, true);
    }
    drag.k = k;
    drag.target = k == null || !drag.base.length ? null : { x: drag.left, y: drag.base[0].top - w.scrollY + k * drag.H };
  }

  function place() { drag.ghost.style.transform = 'translate(' + (drag.px - drag.ox) + 'px,' + (drag.py - drag.oy) + 'px) rotate(1.5deg)'; }

  function onMove(e) {
    if (!drag) return;
    if (e.pointerType === 'mouse' && e.buttons === 0) { if (drag.started) onUp(); else { stop(); drag = null; } return; }   // bouton relâché hors de la fenêtre : le glisser s'arrête
    drag.px = e.clientX; drag.py = e.clientY;
    if (!drag.started) {
      if (Math.hypot(drag.px - drag.x0, drag.py - drag.y0) < 5) return;
      begin();
    }
    e.preventDefault();
    place(); update();
  }

  // La page défile quand on s'approche du haut ou du bas de la fenêtre
  function tick() {
    if (!drag || !drag.started) return;
    var v = 0, h = w.innerHeight;
    if (drag.py < 90) v = -(90 - drag.py) / 4;
    else if (drag.py > h - 90) v = (90 - (h - drag.py)) / 4;
    if (v) { w.scrollBy(0, v); update(); }
    drag.raf = w.requestAnimationFrame(tick);
  }

  function stop() {
    d.removeEventListener('pointermove', onMove);
    d.removeEventListener('pointerup', onUp);
    d.removeEventListener('pointercancel', onCancel);
    d.removeEventListener('keydown', onKey);
    if (drag && drag.raf) w.cancelAnimationFrame(drag.raf);
  }

  function shifted(dr) { return [].slice.call(dr.root.querySelectorAll(ROW + ', .le-loop')); }
  function clearShifts(dr) { shifted(dr).forEach(function (el) { el.style.transition = 'none'; el.style.transform = ''; }); }

  function cleanup(dr) {
    clearShifts(dr);
    if (dr.ghost) dr.ghost.remove();
    if (dr.el) { dr.el.style.visibility = ''; dr.el.classList.remove('dragging'); }
    shifted(dr).forEach(function (el) { el.style.transition = ''; });
    dr.list.classList.remove('over');
    d.body.classList.remove('le-dragging');
  }

  // La ligne vient se poser à sa place ; puis, une fois la page redessinée, tout est remis en état
  function land(dr, needPage) {
    var t = dr.target;
    d.body.classList.remove('le-dragging');
    var g = dr.ghost;
    g.classList.add('land');
    if (dr.kind === 'move' && t) g.style.transform = 'translate(' + t.x + 'px,' + t.y + 'px)';
    else g.classList.add('fade');   // contenu de la colonne : il devient une ligne, la nouvelle ligne clignote
    var landed = false, updated = !needPage, done = false;
    function tryDone() { if (!done && landed && updated) { done = true; cleanup(dr); } }
    setTimeout(function () { landed = true; tryDone(); }, ms() + 30);
    if (needPage) {
      var mo = new MutationObserver(function () { mo.disconnect(); clearShifts(dr); updated = true; tryDone(); });   // la page a redessiné : sa mise en page est déjà la bonne
      mo.observe(dr.root, { childList: true, subtree: true });
      setTimeout(function () { mo.disconnect(); clearShifts(dr); updated = true; tryDone(); }, 900);
    }
  }

  // Un clic qui suit un glisser ne doit pas régler ni ajouter quoi que ce soit
  function swallowClick() {
    function h(ev) { ev.stopPropagation(); ev.preventDefault(); w.removeEventListener('click', h, true); }
    w.addEventListener('click', h, true);
    setTimeout(function () { w.removeEventListener('click', h, true); }, 0);
  }

  function onUp() {
    var dr = drag;
    if (!dr) return;
    stop(); drag = null;
    if (!dr.started) return;
    swallowClick();
    if (dr.kind === 'move') {
      var at = dr.k >= dr.from ? dr.k + 1 : dr.k;   // position comptée avant le retrait de la ligne
      var noop = dr.k === dr.from;
      if (!noop) dr.dotnet.invokeMethodAsync('MoveItem', dr.from, at);
      land(dr, !noop);
    } else if (dr.k != null && dr.target) {
      dr.dotnet.invokeMethodAsync('DropAdd', dr.key, dr.k);
      land(dr, true);
    } else {
      // lâché hors de la liste : le contenu disparaît en fondu, rien n'est ajouté
      dr.ghost.classList.add('fade');
      setTimeout(function () { cleanup(dr); }, 220);
    }
  }

  // Annulé (Échap, interruption) : tout revient à sa place sans rien enregistrer
  function onCancel() {
    var dr = drag;
    if (!dr) return;
    stop(); drag = null;
    if (dr.started) { swallowClick(); cleanup(dr); }
  }
  function onKey(e) { if (e.key === 'Escape') onCancel(); }

  // Amène une ligne à l'écran (contenu qui vient d'être ajouté ou déplacé)
  function reveal(id) {
    var el = d.querySelector('.le-row[data-id="' + id + '"]');
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }

  w.linkiiRows = { init: init, reveal: reveal };
})(window, document);
