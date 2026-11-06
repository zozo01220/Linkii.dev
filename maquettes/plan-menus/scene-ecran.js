ANNOT(() => {
  const b = $$a('.wg-placed-t b').find(x => x.textContent.includes('Plan du bâtiment'));
  b.textContent = 'Plan du bâtiment';
  const row = b.closest('.wg-lrow') || b.parentElement.parentElement.parentElement;
  const side = row.closest('.wg-side');
  side.scrollTop += row.getBoundingClientRect().top - side.getBoundingClientRect().top - 200;
  const p = POP(`<h4>${ICON.layers} Plan du bâtiment</h4>
    <div><label class="l">Plan</label><div class="an-sel"><span>Maison</span><span>▾</span></div></div>
    <div><label class="l">Affichage</label><div class="an-seg"><span class="on">Auto</span><span>Compact</span><span>Diffusion</span><span>Borne</span></div>
      <small>Auto : le résumé seul dans une petite case, le plan dès qu’il y a la place.</small></div>
    <a class="an-link" href="plan.html">Ouvrir le plan ›</a>
    <div class="an-foot"><button type="button" class="btn ghost sm">Annuler</button><button type="button" class="btn sm">Poser sur l’écran</button></div>`, row);
  const r = row.getBoundingClientRect();
  p.style.left = (r.left + scrollX - p.offsetWidth - 24) + 'px';
  p.style.top = Math.max(10, r.top + scrollY - 60) + 'px';
  MARK(row, 1, 'Reste dans <b>Widgets</b> de l’éditeur d’écran, et de la fenêtre de la borne.', 'below');
  MARK(p, 2, '<b>Réglages réduits à plan + affichage</b>, comme dans une liste. Plus de réglages d’affichage propres au widget.', 'left');
});
