/* Back-office : place le QR code de comptage des scans dans l'aperçu de l'écran (composant QrPlacer).
   Glisser à la souris ou au doigt ; aimanté aux axes du centre (guides bleus) ; flèches du clavier (Maj = 5 %).
   Position en % de l'aperçu (coin haut gauche du QR code), bornée pour qu'il reste entier dans l'écran.
   Pendant le geste, seul le style change : Blazor (méthode .NET « Moved(x, y) ») n'est prévenu qu'au lâcher. */
(function (w) {
  'use strict';
  var SNAP = 2;   // % : distance d'aimantation au centre

  function init(stage, box, dotnet) {
    if (!stage || !box || box.__lkQr) return;
    box.__lkQr = true;
    var gv = stage.querySelector('.qr-guide.v'), gh = stage.querySelector('.qr-guide.h');
    var dx = 0, dy = 0, dragging = false, x = 0, y = 0;

    function size() {
      var r = stage.getBoundingClientRect();
      return { r: r, w: box.offsetWidth / r.width * 100, h: box.offsetHeight / r.height * 100 };
    }
    function clamp(v, max) { return Math.max(0, Math.min(max, v)); }
    function put() { box.style.left = x + '%'; box.style.top = y + '%'; }
    function guides(on) { if (gv) gv.classList.toggle('on', !!on.v); if (gh) gh.classList.toggle('on', !!on.h); }

    box.addEventListener('pointerdown', function (e) {
      if (e.button !== 0) return;
      var b = box.getBoundingClientRect();
      dx = e.clientX - b.left; dy = e.clientY - b.top;
      dragging = true;
      try { box.setPointerCapture(e.pointerId); } catch (err) {}
      box.classList.add('drag'); stage.classList.add('dragging');
      e.preventDefault();
    });
    box.addEventListener('pointermove', function (e) {
      if (!dragging) return;
      var s = size();
      x = clamp((e.clientX - dx - s.r.left) / s.r.width * 100, 100 - s.w);
      y = clamp((e.clientY - dy - s.r.top) / s.r.height * 100, 100 - s.h);
      var sv = Math.abs(x + s.w / 2 - 50) < SNAP, sh = Math.abs(y + s.h / 2 - 50) < SNAP;
      if (sv) x = 50 - s.w / 2;
      if (sh) y = 50 - s.h / 2;
      guides({ v: sv, h: sh });
      put();
    });
    function end() {
      if (!dragging) return;
      dragging = false;
      box.classList.remove('drag'); stage.classList.remove('dragging'); guides({});
      dotnet.invokeMethodAsync('Moved', x, y);
    }
    box.addEventListener('pointerup', end);
    box.addEventListener('pointercancel', end);

    box.addEventListener('keydown', function (e) {
      var step = e.shiftKey ? 5 : 1, mx = 0, my = 0;
      if (e.key === 'ArrowLeft') mx = -step; else if (e.key === 'ArrowRight') mx = step;
      else if (e.key === 'ArrowUp') my = -step; else if (e.key === 'ArrowDown') my = step;
      else return;
      e.preventDefault();
      var s = size();
      x = clamp((parseFloat(box.style.left) || 0) + mx, 100 - s.w);
      y = clamp((parseFloat(box.style.top) || 0) + my, 100 - s.h);
      put();
      dotnet.invokeMethodAsync('Moved', x, y);
    });
  }

  w.linkiiQr = { init: init };
})(window);
