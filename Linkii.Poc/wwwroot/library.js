/* Listes de lecture et médiathèque (§51) : raccourcis clavier et dépôt de fichiers n'importe où sur la page.
   « / » place le curseur dans la recherche ; « Échap » prévient la page (désélectionner, fermer l'aperçu).
   Glisser des fichiers depuis l'ordinateur prévient la page (Drag(true)) : elle affiche un voile qui reçoit le dépôt. */
(function (w, d) {
  'use strict';
  var current = null;   // une page à la fois

  function hasFiles(e) {
    var t = e.dataTransfer && e.dataTransfer.types;
    return !!t && Array.prototype.indexOf.call(t, 'Files') >= 0;
  }
  // une fenêtre (modale) réellement affichée : la fenêtre d'import reste dans la page, cachée
  function modalOpen() {
    return Array.prototype.some.call(d.querySelectorAll('.modal'), function (m) { return m.getClientRects().length > 0; });
  }
  function typing(el) {
    return el && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName));
  }

  function onKey(e) {
    if (!current) return;
    if (e.key === '/' && !typing(d.activeElement) && !e.ctrlKey && !e.metaKey && !e.altKey) {
      var s = d.querySelector('[data-lx-search]');
      if (s) { e.preventDefault(); s.focus(); s.select(); }
    } else if (e.key === 'Escape' && !modalOpen()) {
      current.ref.invokeMethodAsync('Escape');
    } else if ((e.key === 'ArrowLeft' || e.key === 'ArrowRight') && d.querySelector('.lx-drawer') && !typing(d.activeElement) && !modalOpen()) {
      e.preventDefault();   // aperçu ouvert : média précédent / suivant
      current.ref.invokeMethodAsync('StepFromKey',e.key === 'ArrowLeft' ? -1 : 1);
    }
  }

  var depth = 0;
  function onEnter(e) {
    if (!current || !current.drop || !hasFiles(e)) return;
    depth++;
    if (depth === 1) current.ref.invokeMethodAsync('Drag', true);
  }
  function onLeave(e) {
    if (!current || !current.drop || !hasFiles(e)) return;
    depth = Math.max(0, depth - 1);
    if (depth === 0) current.ref.invokeMethodAsync('Drag', false);
  }
  function onDrop(e) {
    depth = 0;
    if (current && current.drop && hasFiles(e) && !(e.target && e.target.type === 'file')) e.preventDefault();   // lâché à côté du voile
    if (current && current.drop) setTimeout(function () { current && current.ref.invokeMethodAsync('Drag', false); }, 0);
  }
  // hors du voile, un fichier lâché ne doit pas ouvrir dans l'onglet
  function onOver(e) { if (current && current.drop && hasFiles(e)) e.preventDefault(); }

  w.linkiiLibrary = {
    attach: function (ref, drop) {
      current = { ref: ref, drop: !!drop };
      depth = 0;
    },
    setDrop: function (drop) { if (current) current.drop = !!drop; },
    detach: function (ref) { if (current && current.ref === ref) current = null; }
  };
  d.addEventListener('keydown', onKey);
  d.addEventListener('dragenter', onEnter, true);
  d.addEventListener('dragleave', onLeave, true);
  d.addEventListener('dragover', onOver);
  d.addEventListener('drop', onDrop, true);
})(window, document);
