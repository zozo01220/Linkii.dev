/* Back-office : déplacement des widgets d'écran (horloge, météo) à la souris, au doigt ou au clavier.
   window.linkiiWidgets.attach(canvas, dotnet) : chaque élément [data-wid] du canevas devient déplaçable ;
   à la fin du déplacement, dotnet.invokeMethodAsync('Moved', id, x, y) reçoit la position du coin haut-gauche en % du canevas. */
(function (w) {
  'use strict';

  function clamp(v, min, max) { return Math.max(min, Math.min(max, v)); }

  function attach(canvas, dotnet) {
    if (!canvas || canvas.__lkw) return;
    canvas.__lkw = true;
    var drag = null;

    function pos(el, left, top) {
      var cw = canvas.clientWidth, ch = canvas.clientHeight;
      var x = clamp(left, 0, Math.max(0, cw - el.offsetWidth)), y = clamp(top, 0, Math.max(0, ch - el.offsetHeight));
      return { x: Math.round(x / cw * 1000) / 10, y: Math.round(y / ch * 1000) / 10 };
    }

    canvas.addEventListener('pointerdown', function (e) {
      var el = e.target.closest('[data-wid]');
      if (!el || !canvas.contains(el) || e.button > 0) return;
      e.preventDefault();
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
      if (d.moved && d.p) dotnet.invokeMethodAsync('Moved', d.el.getAttribute('data-wid'), d.p.x, d.p.y);
    }
    canvas.addEventListener('pointerup', end);
    canvas.addEventListener('pointercancel', end);

    // Clavier : flèches = 1 %, Maj + flèches = 5 %
    canvas.addEventListener('keydown', function (e) {
      var el = e.target.closest && e.target.closest('[data-wid]');
      if (!el) return;
      var k = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key];
      if (!k) return;
      e.preventDefault();
      var step = e.shiftKey ? 5 : 1;
      var cw = canvas.clientWidth, ch = canvas.clientHeight;
      var p = pos(el, el.offsetLeft + k[0] * step * cw / 100, el.offsetTop + k[1] * step * ch / 100);
      el.style.left = p.x + '%'; el.style.top = p.y + '%';
      dotnet.invokeMethodAsync('Moved', el.getAttribute('data-wid'), p.x, p.y);
    });
  }

  w.linkiiWidgets = { attach: attach };
})(window);
