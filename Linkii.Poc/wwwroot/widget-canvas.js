/* Back-office : déplacement des widgets d'écran (horloge, météo) à la souris, au doigt ou au clavier.
   window.linkiiWidgets.attach(canvas, dotnet) : chaque élément [data-wid] du canevas devient déplaçable ;
   à la fin du déplacement, dotnet.invokeMethodAsync('Moved', id, x, y) reçoit la position du coin haut-gauche en % du canevas. */
(function (w) {
  'use strict';

  function clamp(v, min, max) { return Math.max(min, Math.min(max, v)); }


    // Comme le player (keepFreeInside) : un widget ne déborde jamais de l'écran, sa taille réelle dépendant des données (ville, vent…).
    // Affichage seulement (translate) : la position enregistrée ne change pas.
    function fit(canvas) {
      if (!canvas) return;
      var cw = canvas.clientWidth, ch = canvas.clientHeight;
      canvas.querySelectorAll('[data-wid]').forEach(function (el) {
        if (el.classList.contains('dragging')) return;
        el.style.translate = '';
        var dx = Math.min(0, cw - (el.offsetLeft + el.offsetWidth)), dy = Math.min(0, ch - (el.offsetTop + el.offsetHeight));
        var x = Math.max(dx, -el.offsetLeft), y = Math.max(dy, -el.offsetTop);
        if (x || y) { el.style.translate = x + 'px ' + y + 'px'; el.__fit = { x: x, y: y }; } else el.__fit = null;
      });
    }
  function attach(canvas, dotnet) {
    if (!canvas || canvas.__lkw) return;
    canvas.__lkw = true;
    var drag = null;

    function pos(el, left, top) {
      var cw = canvas.clientWidth, ch = canvas.clientHeight;
      var x = clamp(left, 0, Math.max(0, cw - el.offsetWidth)), y = clamp(top, 0, Math.max(0, ch - el.offsetHeight));
      return { x: Math.round(x / cw * 1000) / 10, y: Math.round(y / ch * 1000) / 10 };
    }

    // la position affichée peut différer de l'enregistrée (translate de fit) : on la fige avant de déplacer
    function bake(el) {
      if (!el.__fit) return;
      var cw = canvas.clientWidth, ch = canvas.clientHeight;
      el.style.left = Math.round((el.offsetLeft + el.__fit.x) / cw * 1000) / 10 + '%';
      el.style.top = Math.round((el.offsetTop + el.__fit.y) / ch * 1000) / 10 + '%';
      el.style.translate = ''; el.__fit = null;
    }

    canvas.addEventListener('pointerdown', function (e) {
      var el = e.target.closest('[data-wid]');
      if (!el || !canvas.contains(el) || e.button > 0 || e.target.closest('.wg-gear')) return;
      e.preventDefault();
      bake(el);
      el.setPointerCapture(e.pointerId);
      drag = { el: el, dx: e.clientX - el.offsetLeft, dy: e.clientY - el.offsetTop, moved: false, p: null };
      el.classList.add('dragging');
    });

    // aide à l'alignement (§177) : bords et centres de l'écran et des autres widgets, écarts égaux ; Alt : sans aimantation
    function rectOf(el) { var f = el.__fit || { x: 0, y: 0 }; return { x: el.offsetLeft + f.x, y: el.offsetTop + f.y, w: el.offsetWidth, h: el.offsetHeight }; }
    function snapOpts() { var cw = canvas.clientWidth, ch = canvas.clientHeight; return { W: cw, H: ch, sx: 1, sy: 1, unit: function (v, A) { return Math.round(v / (A ? cw : ch) * 1000) / 10 + ' %'; } }; }
    function drawSnap(g) { var old = canvas.querySelector(':scope > .lx-snap'); if (old) old.remove(); if (g) canvas.insertAdjacentHTML('beforeend', w.LxSnap.svg(g, snapOpts())); }
    function snapped(el, left, top, ev) {
      if (!w.LxSnap || ev.altKey) { drawSnap(null); return { left: left, top: top }; }
      var os = [].filter.call(canvas.querySelectorAll('[data-wid]'), function (o) { return o !== el && o.offsetWidth; }).map(rectOf), o = snapOpts();
      var b = { x: left, y: top, w: el.offsetWidth, h: el.offsetHeight };
      w.LxSnap.move(b, os, o, null);
      b.x = clamp(b.x, 0, Math.max(0, o.W - b.w)); b.y = clamp(b.y, 0, Math.max(0, o.H - b.h));
      drawSnap(w.LxSnap.guides(b, os, o, false));
      return { left: b.x, top: b.y };
    }

    canvas.addEventListener('pointermove', function (e) {
      if (!drag) return;
      var sp = snapped(drag.el, e.clientX - drag.dx, e.clientY - drag.dy, e);
      var p = pos(drag.el, sp.left, sp.top);
      drag.el.style.left = p.x + '%';
      drag.el.style.top = p.y + '%';
      drag.p = p; drag.moved = true;
    });

    function end() {
      if (!drag) return;
      var d = drag; drag = null;
      d.el.classList.remove('dragging'); drawSnap(null);
      if (d.moved && d.p) { justDragged = true; setTimeout(function () { justDragged = false; }, 0); dotnet.invokeMethodAsync('Moved', d.el.getAttribute('data-wid'), d.p.x, d.p.y); }
    }
    // Le clic qui suit un déplacement ne doit pas ouvrir les réglages du widget
    var justDragged = false;
    canvas.addEventListener('click', function (e) { if (justDragged) { e.stopPropagation(); e.preventDefault(); } }, true);

    // Dépôt d'un widget glissé depuis la liste : dotnet.Dropped(appId, x, y), point de dépôt en % du canevas.
    canvas.addEventListener('dragover', function (e) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; canvas.classList.add('drop'); });
    canvas.addEventListener('dragleave', function (e) { if (!canvas.contains(e.relatedTarget)) canvas.classList.remove('drop'); });
    canvas.addEventListener('drop', function (e) {
      e.preventDefault(); canvas.classList.remove('drop');
      var id = e.dataTransfer.getData('text/plain');
      if (!id) return;
      var r = canvas.getBoundingClientRect();
      dotnet.invokeMethodAsync('Dropped', id, clamp((e.clientX - r.left) / r.width * 100, 0, 100), clamp((e.clientY - r.top) / r.height * 100, 0, 100));
    });
    canvas.addEventListener('pointerup', end);
    canvas.addEventListener('pointercancel', end);

    // Clavier : flèches = 1 %, Maj + flèches = 5 %
    canvas.addEventListener('keydown', function (e) {
      var el = e.target.closest && e.target.closest('[data-wid]');
      if (!el) return;
      var k = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key];
      if (!k) return;
      e.preventDefault();
      bake(el);
      var step = e.shiftKey ? 5 : 1;
      var cw = canvas.clientWidth, ch = canvas.clientHeight;
      var p = pos(el, el.offsetLeft + k[0] * step * cw / 100, el.offsetTop + k[1] * step * ch / 100);
      el.style.left = p.x + '%'; el.style.top = p.y + '%';
      dotnet.invokeMethodAsync('Moved', el.getAttribute('data-wid'), p.x, p.y);
    });
  }

  // Bouton « Touchez l'écran » de la borne : le calque .kt se décale depuis le centre de l'écran d'attente.
  // À la fin du déplacement, dotnet.TouchMoved(x, y) reçoit le centre du bouton en % du canevas.
  function attachTouch(canvas, dotnet) {
    if (!canvas || canvas.__lkt) return;
    canvas.__lkt = true;
    var d = null;
    canvas.addEventListener('pointerdown', function (e) {
      var kt = canvas.querySelector('.kt'), h = e.target.closest && e.target.closest('.kt > *');
      if (!kt || !h || e.button > 0) return;
      e.preventDefault();
      h.setPointerCapture(e.pointerId);
      var r = canvas.getBoundingClientRect();
      d = { kt: kt, h: h, sx: e.clientX, sy: e.clientY, w: r.width, ht: r.height, x0: parseFloat(kt.getAttribute('data-x')) || 50, y0: parseFloat(kt.getAttribute('data-y')) || 50, band: kt.classList.contains('kt-band') };
      d.x = d.x0; d.y = d.y0; d.moved = false;
      kt.classList.add('dragging');
    });
    canvas.addEventListener('pointermove', function (e) {
      if (!d) return;
      d.x = d.band ? 50 : clamp(d.x0 + (e.clientX - d.sx) / d.w * 100, 5, 95);
      d.y = clamp(d.y0 + (e.clientY - d.sy) / d.ht * 100, 5, 95);
      d.kt.style.transform = 'translate(' + (d.x - 50) + '%,' + (d.y - 50) + '%)';
      d.moved = true;
    });
    function end() {
      if (!d) return;
      var m = d; d = null;
      m.kt.classList.remove('dragging');
      if (m.moved) dotnet.invokeMethodAsync('TouchMoved', Math.round(m.x * 10) / 10, Math.round(m.y * 10) / 10);
    }
    canvas.addEventListener('pointerup', end);
    canvas.addEventListener('pointercancel', end);
  }

  w.linkiiWidgets = { attach: attach, fit: fit, attachTouch: attachTouch };
})(window);
