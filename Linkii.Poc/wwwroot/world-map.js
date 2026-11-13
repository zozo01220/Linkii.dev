// Carte du monde de la console (WorldMap.razor) : zoom et déplacement côté navigateur, fluides, sans aller-retour serveur.
// Souris : molette = zoom vers le curseur, glisser = déplacer, double-clic = zoomer. Pavé tactile : pincement = zoom, deux doigts = déplacer.
// Écran tactile : un doigt = déplacer, deux doigts = zoom. Boutons + / − / ⌂. La bulle de détail suit son pays.
(function () {
  var W = 1000, H = 500, MINW = 90;

  function init(root) {
    if (root._wm) return root._wm;
    var svg = root.querySelector('svg');
    var s = { root: root, svg: svg, x: 0, y: 0, w: W, pts: {}, drag: null, pinch: null, anim: 0 };
    root._wm = s;

    function clamp() {
      s.w = Math.max(MINW, Math.min(W, s.w));
      var h = s.w / 2;
      s.x = Math.max(0, Math.min(W - s.w, s.x));
      s.y = Math.max(0, Math.min(H - h, s.y));
    }
    function apply() {
      clamp();
      svg.setAttribute('viewBox', s.x + ' ' + s.y + ' ' + s.w + ' ' + (s.w / 2));
      svg.style.setProperty('--s', (s.w / W).toFixed(4));   // taille des pastilles : constante à l'écran
      place();
    }
    function place() {   // bulle de détail posée sur son pays
      var pop = root.querySelector('.wm-pop');
      if (!pop) return;
      var px = parseFloat(pop.dataset.x), py = parseFloat(pop.dataset.y);
      pop.style.left = ((px - s.x) / s.w * 100) + '%';
      pop.style.top = ((py - s.y) / (s.w / 2) * 100) + '%';
      pop.classList.toggle('l', (px - s.x) / s.w > 0.62);
    }
    // point de l'écran (px) -> coordonnées de la carte
    function toMap(cx, cy) {
      var r = svg.getBoundingClientRect();
      return { x: s.x + (cx - r.left) / r.width * s.w, y: s.y + (cy - r.top) / r.height * (s.w / 2) };
    }
    function zoomAt(cx, cy, factor) {   // garde le point sous le curseur immobile
      var p = toMap(cx, cy), r = svg.getBoundingClientRect();
      var nw = Math.max(MINW, Math.min(W, s.w / factor));
      s.x = p.x - (cx - r.left) / r.width * nw;
      s.y = p.y - (cy - r.top) / r.height * (nw / 2);
      s.w = nw;
      apply();
    }
    function animateTo(x, y, w) {   // transition douce (boutons, recentrage)
      cancelAnimationFrame(s.anim);
      var from = { x: s.x, y: s.y, w: s.w }, t0 = performance.now(), dur = 320;
      if (window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches) { s.x = x; s.y = y; s.w = w; apply(); return; }
      (function step(t) {
        var k = Math.min(1, (t - t0) / dur); k = 1 - Math.pow(1 - k, 3);
        s.x = from.x + (x - from.x) * k; s.y = from.y + (y - from.y) * k; s.w = from.w + (w - from.w) * k;
        apply();
        if (k < 1) s.anim = requestAnimationFrame(step);
      })(t0);
    }
    s.focus = function (x, y) {   // centre un point en gardant un zoom d'au moins 2,4×
      var w = Math.min(s.w, 420);
      animateTo(x - w / 2, y - w / 4, w);
    };
    s.reset = function () { animateTo(0, 0, W); };
    s.zoomBy = function (f) {
      var nw = Math.max(MINW, Math.min(W, s.w / f));
      animateTo(s.x + s.w / 2 - nw / 2, s.y + s.w / 4 - nw / 4, nw);
    };

    // molette / pincement du pavé tactile (ctrl + molette)
    svg.addEventListener('wheel', function (e) {
      e.preventDefault();
      cancelAnimationFrame(s.anim);
      var unit = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? 400 : 1;
      if (!e.ctrlKey && Math.abs(e.deltaX) > Math.abs(e.deltaY) * 1.5) {   // défilement horizontal du pavé : déplacement
        var r = svg.getBoundingClientRect();
        s.x += e.deltaX * unit / r.width * s.w; apply(); return;
      }
      var d = -e.deltaY * unit * (e.ctrlKey ? 0.012 : 0.0016);   // pincement : plus sensible
      zoomAt(e.clientX, e.clientY, Math.exp(Math.max(-0.6, Math.min(0.6, d))));
    }, { passive: false });

    svg.addEventListener('dblclick', function (e) { e.preventDefault(); zoomAt(e.clientX, e.clientY, 1.9); });

    // pointeurs : un = déplacer, deux = pincer
    function dist() { var k = Object.keys(s.pts); var a = s.pts[k[0]], b = s.pts[k[1]]; return { d: Math.hypot(a.x - b.x, a.y - b.y), cx: (a.x + b.x) / 2, cy: (a.y + b.y) / 2 }; }
    svg.addEventListener('pointerdown', function (e) {
      if (e.pointerType === 'mouse' && e.button !== 0) return;
      cancelAnimationFrame(s.anim);
      s.pts[e.pointerId] = { x: e.clientX, y: e.clientY };
      var n = Object.keys(s.pts).length;
      if (n === 1) s.drag = { x: e.clientX, y: e.clientY, moved: false };
      else if (n === 2) { var p = dist(); s.pinch = { d: p.d }; s.drag.moved = true; }
    });
    svg.addEventListener('pointermove', function (e) {
      if (!s.pts[e.pointerId]) return;
      var prev = s.pts[e.pointerId];
      s.pts[e.pointerId] = { x: e.clientX, y: e.clientY };
      var n = Object.keys(s.pts).length;
      if (n === 2 && s.pinch) {
        var p = dist();
        if (s.pinch.d > 0) zoomAt(p.cx, p.cy, p.d / s.pinch.d);
        s.pinch.d = p.d;
      } else if (n === 1 && s.drag) {
        if (!s.drag.moved && Math.hypot(e.clientX - s.drag.x, e.clientY - s.drag.y) > 4) {
          s.drag.moved = true;
          try { svg.setPointerCapture(e.pointerId); } catch (_) { }
          root.classList.add('grabbing');
        }
        if (s.drag.moved) {
          var r = svg.getBoundingClientRect();
          s.x -= (e.clientX - prev.x) / r.width * s.w;
          s.y -= (e.clientY - prev.y) / r.height * (s.w / 2);
          apply();
        }
      }
    });
    function up(e) {
      delete s.pts[e.pointerId];
      if (Object.keys(s.pts).length < 2) s.pinch = null;
      if (Object.keys(s.pts).length === 0) { root.classList.remove('grabbing'); setTimeout(function () { if (s.drag) s.drag.moved = false; }, 0); }
    }
    svg.addEventListener('pointerup', up);
    svg.addEventListener('pointercancel', up);
    // un glissé ne doit pas valoir clic (sinon il fermerait la bulle ou ouvrirait un pays) : on l'arrête avant Blazor
    svg.addEventListener('click', function (e) { if (s.drag && s.drag.moved) { e.stopPropagation(); e.preventDefault(); } }, true);

    // boutons
    root.querySelectorAll('[data-wm]').forEach(function (b) {
      b.addEventListener('click', function () {
        var a = b.getAttribute('data-wm');
        if (a === 'in') s.zoomBy(1.7); else if (a === 'out') s.zoomBy(1 / 1.7); else s.reset();
      });
    });
    apply();
    return s;
  }

  window.wmInit = function (root) { if (root) init(root); };
  window.wmPlace = function (root) { if (root && root._wm) { var pop = root.querySelector('.wm-pop'); if (pop) { var s = root._wm; var px = parseFloat(pop.dataset.x), py = parseFloat(pop.dataset.y); pop.style.left = ((px - s.x) / s.w * 100) + '%'; pop.style.top = ((py - s.y) / (s.w / 2) * 100) + '%'; pop.classList.toggle('l', (px - s.x) / s.w > 0.62); } } };
  window.wmFocus = function (root, x, y) { if (root && root._wm) root._wm.focus(x, y); };
  window.wmReset = function (root) { if (root && root._wm) root._wm.reset(); };
})();
