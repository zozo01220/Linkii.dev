// Bouton « maintenir pour confirmer » (alerte d'urgence) : <button data-hold="1500"> ne répond qu'à un appui tenu (souris, doigt ou touche Entrée/Espace) ; un simple clic ne fait rien.
// À la fin du délai le clic est déclenché une seule fois, et Blazor reçoit son @onclick comme pour un bouton ordinaire.
(function () {
  if (window.__holdConfirm) return; window.__holdConfirm = true;
  var timer = null, btn = null;

  function stop() {
    if (timer) clearTimeout(timer);
    timer = null;
    if (btn) { btn.classList.remove('holding'); btn = null; }
  }
  function start(el) {
    stop();
    btn = el;
    var ms = parseInt(el.getAttribute('data-hold'), 10) || 1500;
    el.style.setProperty('--hold-ms', ms + 'ms');
    void el.offsetWidth;
    el.classList.add('holding');
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
  ['pointerup', 'pointercancel', 'pointerleave', 'blur'].forEach(function (n) { document.addEventListener(n, function (e) { if (btn && (n !== 'pointerleave' || target(e) === btn)) stop(); }, true); });
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
