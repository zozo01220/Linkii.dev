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

    canvas.addEventListener('pointermove', function (e) {
      if (!drag) return;
      var p = pos(drag.el, e.clientX - drag.dx, e.clientY - drag.dy);
      drag.el.style.left = p.x + '%';
      drag.el.style.top = p.y + '%';
      drag.p = p; drag.moved = true;
    });

    function end() {
      if (!drag) return;
      var d = drag; drag = null;
      d.el.classList.remove('dragging');
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

  w.linkiiWidgets = { attach: attach, fit: fit };
})(window);
