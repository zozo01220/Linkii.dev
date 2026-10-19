/* Fil d'une liste de lecture (§73) : glisser une app de la colonne de gauche dans le fil, ou déplacer un contenu du fil.
   Un trait bleu montre l'endroit du dépôt (au-dessus / au-dessous en vue Lignes, à gauche / à droite en vue Cartes).
   Au lâcher, la page enregistre : DropApp(appId, position) ou MoveItem(de, vers). Le DOM n'est jamais réordonné ici. */
(function (w, d) {
  'use strict';
  var drag = null, target = null;

  function marks(root) {
    Array.prototype.forEach.call(root.querySelectorAll('.ins-before, .ins-after'), function (x) { x.classList.remove('ins-before', 'ins-after'); });
    Array.prototype.forEach.call(root.querySelectorAll('.pf-end.over'), function (x) { x.classList.remove('over'); });
  }

  function init(root, dotnet) {
    if (!root || root.__lkFlow) return;
    root.__lkFlow = true;

    function flow() { return root.querySelector('.pf-flow'); }
    function clear() {
      marks(root);
      Array.prototype.forEach.call(root.querySelectorAll('.dragging'), function (x) { x.classList.remove('dragging'); });
      var f = flow(); if (f) f.classList.remove('drag-app');
      drag = null; target = null;
    }

    root.addEventListener('dragstart', function (e) {
      var ap = e.target.closest && e.target.closest('.pf-app[data-app]');
      var u = e.target.closest && e.target.closest('.pf-u[data-i][draggable="true"]');
      if (ap) { drag = { kind: 'app', app: ap.getAttribute('data-app') }; ap.classList.add('dragging'); var f = flow(); if (f) f.classList.add('drag-app'); }
      else if (u) { drag = { kind: 'item', i: +u.getAttribute('data-i') }; u.classList.add('dragging'); }
      else return;
      e.dataTransfer.setData('text/plain', 'linkii');
      e.dataTransfer.effectAllowed = drag.kind === 'app' ? 'copy' : 'move';
    });
    root.addEventListener('dragend', clear);

    root.addEventListener('dragover', function (e) {
      var f = flow();
      if (!drag || !f || !f.contains(e.target)) return;
      e.preventDefault();
      marks(root);
      var u = e.target.closest('.pf-u');
      var count = f.querySelectorAll('.pf-u[data-i]').length;
      if (!u || !u.hasAttribute('data-i')) {
        target = count;
        var end = f.querySelector('.pf-end'); if (end) end.classList.add('over');
        return;
      }
      var r = u.getBoundingClientRect(), i = +u.getAttribute('data-i');
      var before = f.classList.contains('cards') ? e.clientX < r.left + r.width / 2 : e.clientY < r.top + r.height / 2;
      u.classList.add(before ? 'ins-before' : 'ins-after');
      target = before ? i : i + 1;
    });

    root.addEventListener('drop', function (e) {
      var f = flow();
      if (!drag || !f || !f.contains(e.target)) return;
      e.preventDefault();
      var dr = drag, at = target;
      clear();
      if (at == null) return;
      if (dr.kind === 'app') dotnet.invokeMethodAsync('DropApp', dr.app, at);
      else if (at !== dr.i && at !== dr.i + 1) dotnet.invokeMethodAsync('MoveItem', dr.i, at);
    });
  }

  // Amène un contenu à l'écran (clic sur la frise, contenu qui vient d'être ajouté)
  function reveal(id) {
    var el = d.querySelector('.pf-u[data-id="' + id + '"]');
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }

  // Champ de durée ouvert : focus et texte sélectionné, prêt à être remplacé
  function select(el) { if (el) { el.focus(); if (el.select) el.select(); } }

  w.linkiiFlow = { init: init, reveal: reveal, select: select };
})(window, document);
