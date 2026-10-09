/* Pages de connexion et de création d'espace (rendu statique) : fenêtre des conditions générales et décompte avant un nouvel envoi.
   Délégation d'événements : fonctionne aussi après une navigation améliorée de Blazor, qui n'exécute pas les scripts des pages. */
(function () {
  document.addEventListener('click', function (e) {
    var open = e.target.closest('[data-open-dialog]');
    if (open) {
      var d = document.getElementById(open.getAttribute('data-open-dialog'));
      if (d && d.showModal) { e.preventDefault(); d.showModal(); }
      return;
    }
    if (e.target.closest('[data-close-dialog]')) { var c = e.target.closest('dialog'); if (c) c.close(); return; }
    if (e.target.closest('[data-accept-terms]')) {
      var box = document.querySelector('[data-terms]');
      if (box && !box.checked) { box.checked = true; box.dispatchEvent(new Event('change', { bubbles: true })); }
      var dlg = e.target.closest('dialog'); if (dlg) dlg.close();
      return;
    }
    // clic sur le fond de la fenêtre : fermeture
    if (e.target.tagName === 'DIALOG' && e.target.open) e.target.close();
  });

  // « Renvoyer l'e-mail » : réactivé à la fin du délai
  function countdowns() {
    document.querySelectorAll('[data-countdown]').forEach(function (b) {
      if (b._lk) return;
      var left = parseInt(b.getAttribute('data-countdown'), 10) || 0;
      if (left <= 0) return;
      b._lk = true;
      var text = b.parentElement.querySelector('[data-countdown-text]');
      var t = setInterval(function () {
        left--;
        if (text) text.textContent = left > 0 ? 'Nouvel envoi possible dans ' + (left < 90 ? left + ' s' : Math.ceil(left / 60) + ' min') : '';
        if (left <= 0) { clearInterval(t); b.disabled = false; }
      }, 1000);
    });
  }
  if (location.pathname.indexOf('/login') !== 0) return;   // le back-office (interactif) n'en a pas besoin
  countdowns();
  new MutationObserver(countdowns).observe(document.documentElement, { childList: true, subtree: true });
})();
