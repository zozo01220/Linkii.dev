ANNOT(() => {
  const head = $a('.page-head');
  head.innerHTML = `<div><div class="pp-crumb"><a href="plans.html">‹ Plans</a></div><h1>Maison</h1>
    <p class="sub sc-intro">1 étage · 6 pièces · 14 appareils · relié à Home Assistant</p></div>
    <div class="head-actions"><button type="button" class="btn ghost">${ICON.eye} Essayer en borne</button> <button type="button" class="btn">Enregistrer</button></div>`;
  $a('.hpp-grid').remove();
  const tabs = document.createElement('nav'); tabs.className = 'tabs int-tabs'; tabs.setAttribute('role', 'tablist'); tabs.style.width = 'max-content';
  const T = [['draw', 'Dessin', ''], ['devs', 'Appareils', '14'], ['disp', 'Affichages', '3'], ['uses', 'Où il s’affiche', '3']];
  tabs.innerHTML = T.map(t => `<button type="button" role="tab" data-t="${t[0]}" class="${t[0] === 'uses' ? 'on' : ''}">${t[1]}${t[2] ? ` <span class="cat-count">${t[2]}</span>` : ''}</button>`).join('');
  head.after(tabs);
  const body = document.createElement('div'); body.className = 'pp-body'; tabs.after(body);
  const panes = {
    draw: `<div class="pp-same">L’éditeur actuel (étages, pièces, passages, calque) s’ouvre ici en pleine page, au lieu d’une fenêtre. Mêmes outils.</div>`,
    devs: `<div class="pp-same">La liste des appareils et « Placer dans leur pièce » (aujourd’hui dans la fenêtre de l’éditeur), en pleine page.</div>`,
    disp: `<div class="pp-cards">
      <div class="pp-card"><div class="th"><span>Listes et écrans</span></div><div class="b"><b>Diffusion</b><small>Visite des étages en plein écran, sans commande.</small><div class="u">✓ 2 utilisations</div></div></div>
      <div class="pp-card"><div class="th"><span>Bornes tactiles</span></div><div class="b"><b>Borne</b><small>Plein écran, zoom sur la pièce, commandes.</small><div class="u">✓ 1 utilisation</div></div></div>
      <div class="pp-card"><div class="th compact"><span>Petits widgets</span></div><div class="b"><b>Compact</b><small>Le résumé seul (lumières, températures, alertes).</small><div class="u">✓ 0 utilisation</div></div></div></div>`,
    uses: `<div style="display:flex;align-items:center;gap:12px;margin-bottom:12px"><div style="flex:1"><b style="font-weight:500">Où il s’affiche</b><div class="sub" style="margin:0">Modifier le plan ou un affichage met à jour tous ces écrans.</div></div>
        <button type="button" class="btn" id="pp-add">${ICON.plus} Ajouter à…</button></div>
      <div class="pp-uses"><div class="r"><span>Où</span><span>Affichage</span><span>Diffusé sur</span><span></span></div>
        <div class="r"><span>Liste « Accueil »</span><span class="pill">Diffusion</span><span>Mon écran</span><button class="btn ghost sm">Ouvrir</button></div>
        <div class="r"><span>Borne « Entrée » · tuile « Maison »</span><span class="pill">Borne</span><span>Tablette entrée</span><button class="btn ghost sm">Ouvrir</button></div>
        <div class="r"><span>Mon écran · widget</span><span class="pill">Compact</span><span>Mon écran</span><button class="btn ghost sm">Ouvrir</button></div></div>`
  };
  const show = t => {
    $$a('button', tabs).forEach(b => b.classList.toggle('on', b.dataset.t === t));
    body.innerHTML = panes[t];
    $$a('.pp-menu').forEach(m => m.remove());
    if (t === 'uses') {
      const add = $a('#pp-add'), r = add.getBoundingClientRect(), m = document.createElement('div'); m.className = 'pp-menu';
      m.innerHTML = `<div>${ICON.list}Une liste de diffusion<small>Contenu « Vue du bâtiment » (Diffusion)</small></div><div>${ICON.screen}Un écran<small>Widget posé sur l’écran (Compact ou Diffusion)</small></div><div>${ICON.hand}Une borne<small>Tuile qui ouvre le plan en grand (Borne)</small></div>`;
      document.body.appendChild(m); m.style.left = (r.right + scrollX - 300) + 'px'; m.style.top = (r.bottom + scrollY + 6) + 'px';
    }
    CLEARMARKS();
    MARK(tabs, 1, '<b>Quatre onglets</b> remplacent la fenêtre « Modifier » et la fenêtre « Vues ».', 'right');
    if (t === 'uses') MARK('#pp-add', 2, '<b>Ajouter à…</b> crée le contenu, le widget ou la tuile déjà reliés au plan et à l’affichage.', 'left');
    if (t === 'disp') MARK('.pp-cards', 2, '<b>Affichages</b> = les « vues » d’aujourd’hui + les réglages du widget, réunis. Trois prêts d’office.', 'below');
    PLACE();
  };
  tabs.addEventListener('click', e => { const b = e.target.closest('[data-t]'); if (b) show(b.dataset.t); });
  show('uses');
});
