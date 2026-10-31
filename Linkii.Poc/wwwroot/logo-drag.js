/* Back-office : placer et redimensionner le logo de la borne dans l'aperçu de l'accueil (souris, stylet ou doigt).
   - Glisser le logo : il suit le pointeur ; au lâcher, Blazor enregistre sa position (« MoveLogo(x, y) », en % de l'écran, coin haut gauche).
   - Tirer une poignée d'angle : le logo grandit ou rétrécit en gardant ses proportions, le coin opposé reste fixe ;
     au lâcher, Blazor enregistre position et largeur (« ResizeLogo(x, y, w) », en % de la largeur de l'écran).
   Échap ou la perte du pointeur annulent. Même repère que le player : % de la largeur et de la hauteur de l'accueil. */
(function (w) {
  'use strict';
  var MIN_W = 4, MAX_W = 90;

  function init(stage, dotnet) {
    if (!stage) return;
    stage.__lkLd = dotnet;
    if (stage.__lkLdOn) return;
    stage.__lkLdOn = true;
    stage.addEventListener('pointerdown', function (e) { start(stage, e); });
    stage.addEventListener('dragstart', function (e) { if (e.target.closest('.kp-logo-box')) e.preventDefault(); });
  }

  function start(stage, e) {
    if (e.button !== 0) return;
    var rz = e.target.closest('[data-rz]');
    var box = (rz ? rz.parentElement : e.target.closest('.kp-logo-box'));
    if (!box || !box.classList.contains('kp-logo-box') || !stage.contains(box)) return;
    e.preventDefault();
    e.stopPropagation();

    var sr = stage.getBoundingClientRect(), br = box.getBoundingClientRect();
    var from = { left: box.style.left, top: box.style.top, width: box.style.width, cls: box.className };
    var x = (br.left - sr.left) / sr.width * 100, y = (br.top - sr.top) / sr.height * 100, wd = br.width / sr.width * 100;
    var ratio = br.height / br.width;   // proportions conservées au redimensionnement
    var left = br.left, top = br.top, right = br.right, bottom = br.bottom;
    var offX = e.clientX - br.left, offY = e.clientY - br.top;
    var corner = rz ? rz.getAttribute('data-rz') : null;

    box.classList.add('free', 'drag');
    box.style.left = x + '%'; box.style.top = y + '%';
    if (corner) { box.classList.add('sized'); box.style.width = wd + '%'; }
    var cap = e.currentTarget && e.currentTarget.setPointerCapture ? e.currentTarget : box;
    try { (rz || box).setPointerCapture(e.pointerId); } catch (x1) {}

    function moveDrag(ev) {
      var maxX = Math.max(0, 100 - wd), maxY = Math.max(0, 100 - ratio * wd * sr.width / sr.height);
      x = Math.min(maxX, Math.max(0, (ev.clientX - offX - sr.left) / sr.width * 100));
      y = Math.min(maxY, Math.max(0, (ev.clientY - offY - sr.top) / sr.height * 100));
      box.style.left = x + '%'; box.style.top = y + '%';
    }
    function moveResize(ev) {
      var px = ev.clientX, py = ev.clientY, pw;
      pw = (corner === 'se' || corner === 'ne') ? px - left : right - px;
      pw = Math.min(MAX_W / 100 * sr.width, Math.max(MIN_W / 100 * sr.width, pw));
      var ph = pw * ratio;
      var nl = (corner === 'se' || corner === 'ne') ? left : right - pw;
      var nt = (corner === 'se' || corner === 'sw') ? top : bottom - ph;
      // le logo reste dans l'écran
      nl = Math.min(sr.right - pw, Math.max(sr.left, nl)); nt = Math.min(sr.bottom - ph, Math.max(sr.top, nt));
      wd = pw / sr.width * 100; x = (nl - sr.left) / sr.width * 100; y = (nt - sr.top) / sr.height * 100;
      box.style.left = x + '%'; box.style.top = y + '%'; box.style.width = wd + '%';
    }
    var move = corner ? moveResize : moveDrag, target = rz || box;

    function end(cancel) {
      target.removeEventListener('pointermove', move);
      target.removeEventListener('pointerup', up);
      target.removeEventListener('pointercancel', cancelUp);
      window.removeEventListener('keydown', key, true);
      try { target.releasePointerCapture(e.pointerId); } catch (x2) {}
      box.classList.remove('drag');
      if (cancel) { box.style.left = from.left; box.style.top = from.top; box.style.width = from.width; box.className = from.cls; return; }
      var d = stage.__lkLd, r1 = function (v) { return Math.round(v * 10) / 10; };
      if (!d) return;
      if (corner) d.invokeMethodAsync('ResizeLogo', r1(x), r1(y), r1(wd)).catch(function () {});
      else d.invokeMethodAsync('MoveLogo', r1(x), r1(y)).catch(function () {});
    }
    function up() { end(false); }
    function cancelUp() { end(true); }
    function key(ev) { if (ev.key === 'Escape') { ev.stopPropagation(); end(true); } }
    target.addEventListener('pointermove', move);
    target.addEventListener('pointerup', up);
    target.addEventListener('pointercancel', cancelUp);
    window.addEventListener('keydown', key, true);
  }

  w.linkiiLogoDrag = { init: init };
})(window);
