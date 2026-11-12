// Assemble la maquette « refonte du menu latéral et de la barre du haut » à partir de deux pages allégées déjà assemblées
// (Écrans : page sans onglets ; Médiathèque : page d'un hub, avec onglets). Lancer après build.js : node menu-build.js
const fs = require('fs');
const MQ = 'C:/DEV/En-cours/Linkii/maquettes/';
const [css, js] = fs.readFileSync(__dirname + '/menu-part.src', 'utf8').split('<!--SPLIT-->');
const sub = (t, re, by) => { if (!re.test(t)) throw new Error('Introuvable : ' + re); return t.replace(re, () => by); };

const NOTE = `<div class="mk-note">
  <b>Maquette</b> · Menu latéral et barre du haut — rien n'est codé
  <span class="grow"></span>
  <span class="mk-seg" id="mm-ver"><button type="button" class="mk-btn" data-v="old">Aujourd'hui</button><button type="button" class="mk-btn" data-v="new">Proposition</button></span>
  <span class="mk-lab">Mode :</span>
  <span class="mk-seg" id="mm-mode"><button type="button" class="mk-btn" data-v="simple">Simple</button><button type="button" class="mk-btn" data-v="expert">Expert</button></span>
  <span class="mk-lab">Menu :</span>
  <span class="mk-seg" id="mm-fold"><button type="button" class="mk-btn" data-v="open">déplié</button><button type="button" class="mk-btn" data-v="shut">replié</button></span>
  <span hidden><span id="mk-title"></span><span id="mk-ver"></span><span id="mk-demo"></span><span id="mk-view"></span></span>
</div>`;

function make(src, file, here) {
  let t = fs.readFileSync(MQ + src, 'utf8');
  const logo = c => { const m = t.match(new RegExp('<img class="' + c + '" src="([^"]+)"')); if (!m) throw new Error('logo ' + c); return m[1]; };
  const L = logo('bl-long'), S = logo('bl-short');
  t = sub(t, /<title>[^<]*<\/title>/, '<title>Menu latéral et barre du haut : refonte</title>');
  t = sub(t, /<div class="mk-note">[\s\S]*?<\/div>/, NOTE);
  t = sub(t, /<aside class="sidebar">[\s\S]*?<\/aside>/, '<aside class="sidebar" id="nm-sb"></aside>');
  t = sub(t, /<header class="qbar"[^>]*>[\s\S]*?<\/header>/, '<header class="qbar" id="nm-qb" aria-label="Accès rapides"></header>');
  t = sub(t, /<\/head>/, css + '</head>');
  t = sub(t, /<\/body>/, js.split('__LOGO_L__').join(L).split('__LOGO_S__').join(S).split('__HERE__').join(here) + '</body>');
  fs.writeFileSync(MQ + file, t);
  console.log(file, Math.round(fs.statSync(MQ + file).size / 1024) + ' Ko');
}
make('ecrans-simple-allegee.html', 'menu-barre-refonte.html', 'ecrans');
make('mediatheque-simple-allegee.html', 'menu-barre-refonte-contenus.html', 'contenus');
