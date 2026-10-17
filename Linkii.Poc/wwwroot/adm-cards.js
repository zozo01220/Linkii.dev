// Mobile : les tableaux .adm-tbl (console, espace revendeur) et .tbl (espace client : Membres, Aires, Zones) s'affichent en cartes (CSS).
// Chaque cellule reprend ici le titre de sa colonne (data-label) pour que la carte reste lisible. Sans effet sur l'affichage PC.
(function () {
  var queued = false;
  function label() {
    queued = false;
    document.querySelectorAll('table.adm-tbl, table.tbl:not(.st-tbl)').forEach(function (t) {
      var heads = Array.prototype.map.call(t.querySelectorAll('thead th'), function (th) { return th.textContent.trim(); });
      t.querySelectorAll('tbody tr, tfoot tr').forEach(function (tr) {
        var col = 0;
        Array.prototype.forEach.call(tr.children, function (td) {
          var span = td.colSpan || 1;
          if (span === 1 && col < heads.length) {
            if (td.getAttribute('data-label') !== heads[col]) td.setAttribute('data-label', heads[col]);
          } else if (td.hasAttribute('data-label')) td.removeAttribute('data-label');
          col += span;
        });
      });
    });
  }
  function queue() { if (!queued) { queued = true; requestAnimationFrame(label); } }
  new MutationObserver(queue).observe(document.documentElement, { childList: true, subtree: true });
  document.addEventListener('DOMContentLoaded', queue);
  queue();
})();
