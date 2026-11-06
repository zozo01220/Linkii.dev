ANNOT(() => {
  const tile = $a('[data-add="app:ha-building"]');
  tile.scrollIntoView({ block:'center' });
  const p = POP(`<h4>${ICON.layers} Vue du bâtiment</h4>
    <div><label class="l">Quel plan ?</label><div class="an-cards"><div class="an-card on"><i></i><b>Maison</b><small>1 étage · 6 pièces</small></div><div class="an-card add">+ Nouveau plan</div></div></div>
    <div><label class="l">Affichage</label><div class="an-seg"><span class="on">Diffusion</span><span>Borne</span><span>Compact</span></div></div>
    <small>Durée dans la liste : 42 s, la visite complète (elle suit si l’affichage change).</small>
    <div class="an-foot"><button type="button" class="btn ghost sm">Annuler</button><button type="button" class="btn sm">Ajouter au fil</button></div>`, tile);
  const r = tile.getBoundingClientRect();
  p.style.left = (r.left + scrollX - p.offsetWidth - 24) + 'px';
  p.style.top = Math.max(10, r.bottom + scrollY - p.offsetHeight) + 'px';
  MARK(tile, 1, 'Même tuile qu’aujourd’hui (onglet Intégrations du fil).', 'below');
  MARK(p, 2, '<b>Au clic, une petite fenêtre « plan + affichage »</b>, la même partout. Aujourd’hui : le contenu s’ajoute puis se règle (plan, puis vue).', 'left');
});
