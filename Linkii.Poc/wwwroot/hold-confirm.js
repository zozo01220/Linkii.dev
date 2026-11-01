// Bouton « maintenir pour confirmer » (alerte d'urgence) : <button data-hold="1500"> ne répond qu'à un appui tenu (souris, doigt ou touche Entrée/Espace) ; un simple clic ne fait rien.
// À la fin du délai le clic est déclenché une seule fois, et Blazor reçoit son @onclick comme pour un bouton ordinaire.
(function () {
  if (window.__holdConfirm) return; window.__holdConfirm = true;
  var timer = null, raf = 0, btn = null, t0 = 0, ms = 1500;

  function fill(el) { return el.querySelector('.em-hold-fill'); }
  function stop() {
    if (timer) clearTimeout(timer);
    if (raf) cancelAnimationFrame(raf);
    timer = null; raf = 0;
    if (btn) { btn.classList.remove('holding'); var f = fill(btn); if (f) f.style.width = '0'; btn = null; }
  }
  // La barre de progression est tracée image par image (pas de transition CSS) : elle suit l'appui même si le navigateur ralentit les animations.
  function step() {
    if (!btn) return;
    var f = fill(btn), p = Math.min(1, (performance.now() - t0) / ms);
    if (f) f.style.width = (p * 100) + '%';
    if (p < 1) raf = requestAnimationFrame(step);
  }
  function start(el) {
    stop();
    btn = el;
    ms = parseInt(el.getAttribute('data-hold'), 10) || 1500;
    t0 = performance.now();
    el.classList.add('holding');
    step();
    timer = setTimeout(function () {
      var b = btn; stop();
      if (!b || b.disabled) return;
      b.setAttribute('data-ok', '1');
      b.click();
      b.removeAttribute('data-ok');
    }, ms);
  }
  function target(e) { return e.target && e.target.closest ? e.target.closest('[data-hold]') : null; }

  document.addEventListener('pointerdown', function (e) {
    var el = target(e);
    if (el && e.button === 0 && !el.disabled) { start(el); }
  }, true);
  ['pointerup', 'pointercancel', 'blur'].forEach(function (n) { document.addEventListener(n, function () { if (btn) stop(); }, true); });
  document.addEventListener('pointerleave', function (e) { if (btn && e.target === btn) stop(); }, true);
  document.addEventListener('keydown', function (e) {
    var el = target(e);
    if (el && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); if (!e.repeat && !el.disabled) start(el); }
  }, true);
  document.addEventListener('keyup', function (e) { if (btn && (e.key === 'Enter' || e.key === ' ')) stop(); }, true);
  document.addEventListener('contextmenu', function (e) { if (target(e)) e.preventDefault(); }, true);
  // Un clic réel (relâchement, Entrée) ne confirme rien : seul celui du minuteur passe.
  document.addEventListener('click', function (e) {
    var el = target(e);
    if (el && el.getAttribute('data-ok') !== '1') { e.stopPropagation(); e.preventDefault(); }
  }, true);
})();
