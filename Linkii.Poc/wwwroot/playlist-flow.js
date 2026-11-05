/* Fil d'une liste de diffusion (§73, refondu en fil horizontal §186) : glisser une vignette de la bibliothèque dans le fil, ou déplacer une carte du fil.
   Un trait bleu montre l'endroit du dépôt (à gauche / à droite de la carte visée ; au-dessus / au-dessous dans un fil vertical).
   Au lâcher, la page enregistre : DropAdd(clé, position) pour une vignette (« med:… », « cmp:… », « inst:… », « app:… »), MoveItem(de, vers) pour une carte.
   Le DOM n'est jamais réordonné ici. */
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
      var t = e.target.closest ? e.target : e.target.parentElement;
      var add = t && t.closest('[data-add]');
      var u = t && t.closest('.pf-u[data-i][draggable="true"]');
      if (add) { drag = { kind: 'add', key: add.getAttribute('data-add') }; add.classList.add('dragging'); var f = flow(); if (f) f.classList.add('drag-app'); }
      else if (u) { drag = { kind: 'item', i: +u.getAttribute('data-i') }; u.classList.add('dragging'); }
      else return;
      e.dataTransfer.setData('text/plain', 'linkivi');
      e.dataTransfer.effectAllowed = drag.kind === 'item' ? 'move' : 'copy';
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
      // près des bords du fil : il défile tout seul
      var fr = f.getBoundingClientRect();
      if (e.clientX < fr.left + 60) f.scrollLeft -= 14; else if (e.clientX > fr.right - 60) f.scrollLeft += 14;
    });

    root.addEventListener('drop', function (e) {
      var f = flow();
      if (!drag || !f || !f.contains(e.target)) return;
      e.preventDefault();
      var dr = drag, at = target;
      clear();
      if (at == null) return;
      if (dr.kind === 'add') dotnet.invokeMethodAsync('DropAdd', dr.key, at);
      else if (at !== dr.i && at !== dr.i + 1) dotnet.invokeMethodAsync('MoveItem', dr.i, at);
    });

    // molette verticale sur le fil : il défile à l'horizontale
    root.addEventListener('wheel', function (e) {
      var f = flow();
      if (!f || !f.contains(e.target) || Math.abs(e.deltaY) <= Math.abs(e.deltaX) || f.scrollWidth <= f.clientWidth) return;
      f.scrollLeft += e.deltaY;
      e.preventDefault();
    }, { passive: false });
  }

  // Amène un contenu à l'écran (clic sur la frise, contenu qui vient d'être ajouté)
  function reveal(id) {
    var el = d.querySelector('.pf-u[data-id="' + id + '"]');
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'center' });
  }

  // Média qui vient d'être importé : la bibliothèque remonte pour le montrer (il est en tête)
  function revealMedia() {
    var el = d.querySelector('[data-fresh]');
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }

  // Flèches du fil : environ trois cartes
  function scroll(root, dir) {
    var f = root && root.querySelector('.pf-flow');
    if (f) f.scrollBy({ left: dir * Math.max(200, f.clientWidth * 0.7), behavior: 'smooth' });
  }

  // Champ de durée ouvert : focus et texte sélectionné, prêt à être remplacé
  function select(el) { if (el) { el.focus(); if (el.select) el.select(); } }

  w.linkiiFlow = { init: init, reveal: reveal, revealMedia: revealMedia, scroll: scroll, select: select };
})(window, document);
