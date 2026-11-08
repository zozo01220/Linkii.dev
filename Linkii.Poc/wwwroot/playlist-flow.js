/* Fil d'une liste de lecture (§73, fil horizontal §186, glisser fluide §187) : glisser une vignette de la bibliothèque dans le fil, ou déplacer une carte du fil.
   Glisser aux événements pointeur (souris, doigt, stylet), d'après maquettes/liste-diffusion-glisser-deplacer.html :
   - la carte (ou la vignette) saisie est portée par le pointeur (légère inclinaison, ombre) ; un vide en pointillés bleus marque l'endroit d'arrivée ;
   - dès qu'elle dépasse le milieu d'une autre carte, celle-ci glisse d'un cran (transform, 0,25 s) ; le vide suit ;
   - au lâcher, la carte se pose dans le vide en glissant ; la page enregistre : DropAdd(clé, position) pour une vignette (« med:… », « cmp:… », « inst:… », « app:… »),
     MoveItem(de, vers) pour une carte. Le DOM n'est jamais réordonné ici : seuls des transform sont posés, retirés dès que la page a redessiné le fil.
   Échap annule. Le fil défile tout seul près des bords. */
(function (w, d) {
  'use strict';
  var EASE = 'cubic-bezier(.2,.8,.2,1)';
  var drag = null;
  // Deux balisages : l'ancien fil (.pfx-*, encore utilisé par l'assistant Premiers pas) et la page d'une liste (.nx-*, maquette liste-diffusion-allegee.html)
  var OLD = { nx: false, fil: '.pfx-fil', slot: '.pfx-u', card: '.pfx-card', tile: '.pfx-tile', tb: '.pfx-tb', dur: '.pfx-dur', end: '.pfx-endu', endCard: '.pfx-end', loop: '.pfx-loop', hov: '.pfx-hov', empty: null };
  var NX = { nx: true, fil: '.nx-fil', slot: '.nx-it', card: '.nx-th', tile: '.nx-tile', tb: '.nx-tb', dur: '.nx-d', end: '.nx-addt', endCard: null, loop: '.nx-loop', hov: '.nx-hv', empty: '.nx-empty' };
  function sel(root) { return root && root.classList && root.classList.contains('plx') ? NX : OLD; }

  function reduced() { return !!(w.matchMedia && w.matchMedia('(prefers-reduced-motion: reduce)').matches); }
  function ms() { return reduced() ? 0 : 250; }

  function init(root, dotnet) {
    if (!root || root.__lkFlow) return;
    root.__lkFlow = true;

    var S = sel(root);
    root.addEventListener('dragstart', function (e) { if (e.target && e.target.closest && e.target.closest(S.card + ', ' + S.tile)) e.preventDefault(); });   // jamais de glisser natif d'image ou de texte
    root.addEventListener('pointerdown', function (e) {
      if (drag || (e.pointerType === 'mouse' && e.button !== 0)) return;
      var t = e.target;
      if (!t || !t.closest) return;
      var inter = t.closest('button, input, select, textarea, a, label');
      // seuls Régler, Supprimer, À configurer et la saisie de durée gardent leur geste ; partout ailleurs sur la carte (image, nom, durée, pastilles) on peut saisir la carte
      if (inter && !inter.classList.contains(S.tile.slice(1)) && !(inter.tagName === 'BUTTON' && inter.closest(S.dur))) return;
      if (S.nx && inter && inter.closest(S.hov)) return;
      var card = t.closest(S.slot + '[data-drag] ' + S.card);
      var tile = !card && t.closest(S.tile + '[data-add]');
      var fil = root.querySelector(S.fil);
      var empty = !fil && S.empty ? root.querySelector(S.empty) : null;   // liste vide : toute la zone de dépôt reçoit la vignette
      if ((!fil && !empty) || (!card && !tile)) return;
      var box = (card || tile.querySelector(S.tb)).getBoundingClientRect();
      drag = { root: root, S: S, dotnet: dotnet, fil: fil || empty, empty: !!empty, started: false, x0: e.clientX, y0: e.clientY, px: e.clientX, py: e.clientY, k: null, over: false, raf: 0 };
      if (card) {
        var slot = card.closest(S.slot);
        drag.kind = 'move'; drag.slot = slot; drag.from = +slot.getAttribute('data-i'); drag.k = drag.from;
        drag.ox = e.clientX - box.left; drag.oy = e.clientY - box.top;
      } else {
        drag.kind = 'add'; drag.tile = tile; drag.key = tile.getAttribute('data-add');
        drag.ox = Math.min(65, e.clientX - box.left); drag.oy = Math.min(65, e.clientY - box.top);
      }
      d.addEventListener('pointermove', onMove);
      d.addEventListener('pointerup', onUp);
      d.addEventListener('pointercancel', onCancel);
      d.addEventListener('keydown', onKey);
    });
  }

  // ---------- géométrie du fil (positions de mise en page : les transform en cours ne les changent pas) ----------
  function geometry() {
    var fil = drag.fil, S = drag.S;
    var slots = [].slice.call(fil.querySelectorAll(S.slot + '[data-i]'));
    var end = fil.querySelector(S.end), loop = fil.querySelector(S.loop);
    var first = slots[0] || end;
    var card = slots[0] ? slots[0].querySelector(S.card) : end && (S.endCard ? end.querySelector(S.endCard) : end);
    if (!first || !card) return null;
    var W = slots.length > 1 ? slots[1].offsetLeft - slots[0].offsetLeft : end && end !== first ? end.offsetLeft - first.offsetLeft : first.offsetWidth;
    return { fr: fil.getBoundingClientRect(), slots: slots, end: end, loop: loop, first: first, W: W, pad: card.offsetLeft, cw: card.offsetWidth, ch: card.offsetHeight, top: card.getBoundingClientRect().top, scroll: fil.scrollLeft };
  }
  function centerOf(g, s) {
    var c = s.querySelector(drag.S.card);
    return g.fr.left + s.offsetLeft + c.offsetLeft + c.offsetWidth / 2 - g.scroll;
  }

  function begin() {
    drag.started = true;
    d.body.classList.add('pfx-dragging');
    var g = document.createElement('div');
    if (drag.kind === 'move') {
      var src = drag.slot.querySelector(drag.S.card), r = src.getBoundingClientRect();
      var c = src.cloneNode(true);
      var hov = c.querySelector(drag.S.hov); if (hov) hov.remove();
      c.style.width = r.width + 'px'; c.style.height = r.height + 'px';
      g.className = 'pfx-ghost'; g.appendChild(c);
      if (drag.S.nx) drag.slot.style.visibility = 'hidden';   // la carte et son nom
      else {
        src.style.visibility = 'hidden';
        var wh = drag.slot.querySelector('.pfx-when'); if (wh) wh.style.visibility = 'hidden';
      }
    } else {
      var tb = drag.tile.querySelector(drag.S.tb), t = tb.cloneNode(true);
      [].forEach.call(t.querySelectorAll('.pfx-add, .pfx-used, .pfx-new, .pfx-len'), function (x) { x.remove(); });
      g.className = 'pfx-ghost tile'; g.appendChild(t);
      drag.tile.classList.add('dragging');
    }
    d.body.appendChild(g);
    drag.ghost = g;
    var ph = document.createElement('div'); ph.className = 'pfx-phold' + (drag.S.nx ? ' nx' : ''); ph.style.display = 'none';
    d.body.appendChild(ph);
    drag.ph = ph;
    drag.raf = w.requestAnimationFrame(tick);
  }

  function setShift(el, px, animate) {
    if (!el) return;
    el.style.transition = animate && !reduced() ? 'transform ' + ms() + 'ms ' + EASE : 'none';
    el.style.transform = px ? 'translateX(' + px + 'px)' : '';
  }

  // Place les cartes et le vide selon la position du pointeur
  function update() {
    if (drag.empty) {   // liste vide : la zone de dépôt s'allume, la vignette s'y pose en position 1
      var zr = drag.fil.getBoundingClientRect();
      var inz = drag.px > zr.left && drag.px < zr.right && drag.py > zr.top - 30 && drag.py < zr.bottom + 30;
      drag.over = inz; drag.k = inz ? 0 : null;
      drag.fil.classList.toggle('over', inz);
      drag.target = inz ? { x: zr.left + 28, y: zr.top + 26, cw: 168 } : null;
      return;
    }
    var g = geometry();
    if (!g) return;
    var fr = g.fr, px = drag.px, py = drag.py;
    var over = px > fr.left - 30 && px < fr.right + 30 && py > fr.top - 60 && py < fr.bottom + 60;
    drag.over = over;
    drag.fil.classList.toggle('pfx-over', over && drag.kind === 'add');
    var k = drag.k;
    if (over) {
      k = 0;
      g.slots.forEach(function (s) { if (s !== drag.slot && centerOf(g, s) < px) k++; });
    } else if (drag.kind === 'add') k = null;   // hors du fil : la vignette n'ajoute rien, les cartes se referment
    var changed = k !== drag.placed;
    if (changed) {
      drag.placed = k;
      g.slots.forEach(function (s, j) {
        var sh = 0;
        if (drag.kind === 'move') {
          if (s !== drag.slot) { var m = j < drag.from ? j : j - 1, fin = m < k ? m : m + 1; sh = (fin - j) * g.W; }
        } else if (k != null && j >= k) sh = g.W;
        setShift(s, sh, true);
      });
      var tail = drag.kind === 'add' && k != null ? g.W : 0;
      setShift(g.end, tail, true); setShift(g.loop, tail, true);
    }
    drag.k = k;
    // vide en pointillés (collé à l'écran, rogné aux bords du fil)
    var ph = drag.ph;
    if (k == null) { ph.style.display = 'none'; return; }
    var left = fr.left + g.first.offsetLeft - g.scroll + g.pad + k * g.W;
    var cl = Math.max(0, fr.left - left), cr = Math.max(0, left + g.cw - fr.right);
    if (cl >= g.cw || cr >= g.cw) { ph.style.display = 'none'; return; }
    ph.style.display = '';
    ph.style.width = g.cw + 'px'; ph.style.height = g.ch + 'px';
    ph.style.clipPath = cl || cr ? 'inset(0 ' + cr + 'px 0 ' + cl + 'px)' : '';
    ph.style.transition = changed && !reduced() ? 'transform ' + ms() + 'ms ' + EASE : 'none';
    ph.style.transform = 'translate(' + left + 'px,' + g.top + 'px)';
    drag.target = { x: left, y: g.top, cw: g.cw };
  }

  function place() { drag.ghost.style.transform = 'translate(' + (drag.px - drag.ox) + 'px,' + (drag.py - drag.oy) + 'px)'; }

  function onMove(e) {
    if (!drag) return;
    drag.px = e.clientX; drag.py = e.clientY;
    if (!drag.started) {
      if (Math.hypot(drag.px - drag.x0, drag.py - drag.y0) < 5) return;
      begin();
    }
    e.preventDefault();
    place(); update();
  }

  // Défilement du fil quand on s'approche de ses bords
  function tick() {
    if (!drag || !drag.started) return;
    var r = drag.fil.getBoundingClientRect(), v = 0;
    if (drag.over && !drag.empty) {
      if (drag.px < r.left + 80) v = -(80 - (drag.px - r.left)) / 5;
      else if (drag.px > r.right - 80) v = (80 - (r.right - drag.px)) / 5;
    }
    if (v) { drag.fil.scrollLeft += v; update(); }
    drag.raf = w.requestAnimationFrame(tick);
  }

  function stop() {
    d.removeEventListener('pointermove', onMove);
    d.removeEventListener('pointerup', onUp);
    d.removeEventListener('pointercancel', onCancel);
    d.removeEventListener('keydown', onKey);
    if (drag && drag.raf) w.cancelAnimationFrame(drag.raf);
  }

  function clearShifts(dr) {
    var els = [].slice.call(dr.fil.querySelectorAll(dr.S.slot + ', ' + dr.S.loop + (dr.S.nx ? ', ' + dr.S.end : '')));
    els.forEach(function (el) { el.style.transition = 'none'; el.style.transform = ''; });
  }

  function cleanup(dr) {
    clearShifts(dr);
    if (dr.ghost) dr.ghost.remove();
    if (dr.ph) dr.ph.remove();
    if (dr.slot) {
      dr.slot.style.visibility = '';
      var c = dr.slot.querySelector(dr.S.card), wh = dr.slot.querySelector('.pfx-when');
      if (c) c.style.visibility = ''; if (wh) wh.style.visibility = '';
    }
    if (dr.tile) dr.tile.classList.remove('dragging');
    [].forEach.call(dr.fil.querySelectorAll(dr.S.slot + ', ' + dr.S.loop + (dr.S.nx ? ', ' + dr.S.end : '')), function (el) { el.style.transition = ''; });
    dr.fil.classList.remove('pfx-over', 'over');
    d.body.classList.remove('pfx-dragging', 'pfx-landing');
  }

  // La carte vient se poser dans le vide ; puis, une fois la page redessinée, tout est remis en état
  function land(dr, needPage) {
    var t = dr.target;
    d.body.classList.remove('pfx-dragging');
    d.body.classList.add('pfx-landing');
    var g = dr.ghost;
    g.classList.add('land');
    if (dr.kind === 'add') g.style.transform = 'translate(' + t.x + 'px,' + t.y + 'px) scale(' + (t.cw / g.offsetWidth) + ')';
    else g.style.transform = 'translate(' + t.x + 'px,' + t.y + 'px)';
    var landed = false, updated = !needPage, done = false;
    function tryDone() { if (!done && landed && updated) { done = true; cleanup(dr); } }
    setTimeout(function () { landed = true; tryDone(); }, ms() + 30);
    if (needPage) {
      var mo = new MutationObserver(function () { mo.disconnect(); clearShifts(dr); updated = true; tryDone(); });   // la page a redessiné : sa mise en page est déjà la bonne
      mo.observe(dr.empty ? dr.root : dr.fil, { childList: true, subtree: dr.empty });
      setTimeout(function () { mo.disconnect(); clearShifts(dr); updated = true; tryDone(); }, 900);
    }
  }

  // Un clic qui suit un glisser ne doit pas ajouter ni régler quoi que ce soit
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
      var at = dr.k >= dr.from ? dr.k + 1 : dr.k;   // position comptée avant le retrait de la carte
      var noop = dr.k === dr.from;
      if (!noop) dr.dotnet.invokeMethodAsync('MoveItem', dr.from, at);
      land(dr, !noop);
    } else if (dr.k != null && dr.target) {
      dr.dotnet.invokeMethodAsync('DropAdd', dr.key, dr.k);
      land(dr, true);
    } else {
      // lâchée hors du fil : la vignette disparaît en fondu, rien n'est ajouté
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

  // ---------- le reste du fil ----------
  // Amène un contenu à l'écran (contenu qui vient d'être ajouté ou déplacé)
  function reveal(id) {
    var el = d.querySelector('.pf-u[data-id="' + id + '"], .nx-it[data-id="' + id + '"]');
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'center' });
  }

  // Média qui vient d'être importé : la bibliothèque remonte pour le montrer (il est en tête)
  function revealMedia() {
    var el = d.querySelector('[data-fresh]');
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }

  // Flèches du fil : environ trois cartes
  function scroll(root, dir) {
    var f = root && root.querySelector('.pfx-fil, .nx-fil');
    if (f) f.scrollBy({ left: dir * Math.max(200, f.clientWidth * 0.7), behavior: 'smooth' });
  }

  // Champ de durée ouvert : focus et texte sélectionné, prêt à être remplacé
  function select(el) { if (el) { el.focus(); if (el.select) el.select(); } }

  // molette verticale sur le fil : il défile à l'horizontale
  d.addEventListener('wheel', function (e) {
    var f = e.target.closest && e.target.closest('.pfx-fil, .nx-fil');
    if (!f || Math.abs(e.deltaY) <= Math.abs(e.deltaX) || f.scrollWidth <= f.clientWidth) return;
    f.scrollLeft += e.deltaY;
    e.preventDefault();
  }, { passive: false });

  // « + Ajouter » du fil : la bibliothèque remonte juste sous le fil (qui reste collé en haut), la recherche prend le curseur
  function toLib(lib, input) {
    if (!lib) return;
    var fb = d.querySelector('.nx-filbox'), cs = fb ? w.getComputedStyle(fb) : null;
    var stick = cs && cs.position === 'sticky' ? (parseFloat(cs.top) || 0) + fb.offsetHeight + 12 : 12;
    w.scrollTo({ top: lib.getBoundingClientRect().top + w.scrollY - stick, behavior: reduced() ? 'auto' : 'smooth' });
    if (input) setTimeout(function () { try { input.focus({ preventScroll: true }); } catch (e) {} }, 350);
  }

  w.linkiiFlow = { init: init, reveal: reveal, revealMedia: revealMedia, scroll: scroll, select: select, toLib: toLib };
})(window, document);
