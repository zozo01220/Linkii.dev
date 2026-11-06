ANNOT(() => {
  const card = $a('#cat-ha-home');
  card.classList.remove('err');
  $a('.int-sub', card).textContent = 'Jeton d’accès · 38 appareils';
  $a('.int-foot', card).innerHTML = `<button type="button" class="linklike grow" id="an-plan-link" style="display:inline-flex;align-items:center;gap:6px;flex:0 1 auto;margin-right:auto">${ICON.layers} Plan du bâtiment · 1 plan</button><span class="int-pill ok"><i></i>Active</span>`;
  const tab = $a('.tab-alert'); if (tab) tab.remove();
  MARK('#an-plan-link', 1, '<b>Raccourci</b> vers Contenus › Plans. Sans plan : « Créer le plan du bâtiment », qui ouvre l’assistant.', 'below');
  MARK('.kebab-btn', 2, 'Même entrée dans le menu ⋮ de la carte, et dans la fenêtre du compte (« Ce que l’intégration apporte »).', 'right');
});
