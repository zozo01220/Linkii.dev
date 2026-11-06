ANNOT(() => {
  $a('.sc-intro').textContent = "Vos plans de bâtiment. Ouvrez un plan pour le dessiner, régler ses affichages (diffusion, borne, compact) et choisir où il s'affiche.";
  const used = $a('.hpp-used'); used.lastChild.textContent = ' 3 affichages · utilisé sur 2 écrans';
  const trash = $a('.hpp-a .icon').outerHTML;
  $a('.hpp-a').innerHTML = `<button type="button" class="btn sm">Ouvrir le plan</button> <button type="button" class="btn ghost sm">Dupliquer</button> ${trash}`;
  MARK('.hub-tab.active', 1, '<b>Ne bouge pas</b> : Contenus › Plans, en mode expert, seulement si Home Assistant est connecté. Pas de nouveau menu.', 'below');
  MARK('.head-actions .btn', 2, '<b>Nouveau plan</b> ouvre l’assistant en 4 pas : étages et pièces repris de Home Assistant, esquisse, appareils, où l’afficher.', 'bl');
  MARK('.hpp-card', 3, '<b>Un clic sur la carte ouvre la page du plan.</b> « Modifier » et « Vues · 2 » disparaissent : dessin, appareils, affichages et diffusion sont dans cette page.', 'right');
});
