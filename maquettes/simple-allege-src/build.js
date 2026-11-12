// Assemble les maquettes « mode Simple allégé » : app.css + gabarit + page.
const fs = require('fs'), path = require('path');
const SP = __dirname, ROOT = 'C:/DEV/En-cours/Linkii', MQ = ROOT + '/maquettes/';
const ref = fs.readFileSync(MQ + 'liste-page-epuree.html', 'utf8').split('\n');
const css = fs.readFileSync(ROOT + '/Linkii.Poc/wwwroot/app.css', 'utf8');
const brand = ref.slice(9674, 9679).join('\n');
const rep = (t, a, b) => { if (!t.includes(a)) throw new Error('Introuvable : ' + a.slice(0, 70)); return t.split(a).join(b); };
const out = (file, title, part) => {
  const head = ref.slice(0, 5).join('\n') + `\n<title>${title}</title>\n` + ref[6] + "\n<style>/* app.css intégré pour que la maquette s'ouvre seule */\n";
  fs.writeFileSync(MQ + file, head + css + '\n' + rep(part, '<!--BRAND-->', brand));
  console.log(file, Math.round(fs.statSync(MQ + file).size / 1024) + ' Ko');
};
const NAV_APPS = '\n    <a href="applications-simple-allegee.html" class="nav" data-nav="apps"><i data-i="grid"></i> Applications</a>';

/* ---- Écrans ---- */
let E = fs.readFileSync(SP + '/ecrans-part.src', 'utf8');
E = rep(E, 'href="compositions-simple-allegee.html" class="nav"><i data-i="image"></i> Contenus</a>', 'href="mediatheque-simple-allegee.html" class="nav"><i data-i="image"></i> Contenus</a>' + NAV_APPS);
out('ecrans-simple-allegee.html', 'Écrans allégés (mode Simple)', E);

/* ---- gabarit des pages en cartes ---- */
let L = fs.readFileSync(SP + '/listes-part.src', 'utf8');
L = rep(L, '<a href="compositions-simple-allegee.html" class="nav" data-nav="contenus"><i data-i="image"></i> Contenus</a>', '<a href="mediatheque-simple-allegee.html" class="nav" data-nav="contenus"><i data-i="image"></i> Contenus</a>' + NAV_APPS);
L = rep(L, "tabs: [['Médiathèque', '#'], ['Compositions', 'compositions-simple-allegee.html']],", "tabs: [['Médiathèque', 'mediatheque-simple-allegee.html'], ['Compositions', 'compositions-simple-allegee.html']],");
// filtres génériques de la ligne d'en-tête
L = rep(L, "const shown = () => S.filter(s => (!q || s.name.toLowerCase().includes(q.toLowerCase())) && (filter !== 'pend' || s.pend));",
  "const filters = () => P.filters ? P.filters(S).filter(f => f[4]) : (P.publish && S.some(s => s.pend) ? [['pend', S.filter(s => s.pend).length + ' à publier', 'pe', s => s.pend, 1]] : []);\n" +
  "const picked = new Set();\n" +
  "const shown = () => S.filter(s => (!q || s.name.toLowerCase().includes(q.toLowerCase())) && (!filter || (filters().find(f => f[0] === filter) || [0, 0, 0, () => true])[3](s)));");
L = rep(L, "${pend ? `<span>·</span><button type=\"button\" class=\"es-f pe ${filter === 'pend' ? 'on' : ''}\" data-a=\"f\" data-f=\"pend\" title=\"N'afficher que les listes à publier\">${pend} à publier${filter === 'pend' ? ico('x', 13) : ''}</button>` : ''}</p></div>",
  "${picked.size ? `<span>·</span><b style=\"color:var(--ink)\">${pl(picked.size, 'sélectionné')}</b><button type=\"button\" class=\"es-f bad\" data-a=\"delsel\">Supprimer</button><button type=\"button\" class=\"es-f\" data-a=\"unsel\">Annuler</button>` : filters().map(f => `<span>·</span><button type=\"button\" class=\"es-f ${f[2]} ${filter === f[0] ? 'on' : ''}\" data-a=\"f\" data-f=\"${f[0]}\">${f[1]}${filter === f[0] ? ico('x', 13) : ''}</button>`).join('')}</p></div>");
L = rep(L, "<span>Aucune ${P.one} pour l'instant</span>", "<span>${P.none || 'Aucune ' + P.one + \" pour l'instant\"}</span>");
// sélection (médiathèque)
L = rep(L, "${neu ? '' : '<input type=\"checkbox\" class=\"lc-ck sc-check\" aria-label=\"Sélectionner\">'}", "${neu && !P.select ? '' : `<input type=\"checkbox\" class=\"lc-ck sc-check\" ${picked.has(s.id) ? 'checked' : ''} aria-label=\"Sélectionner\">`}");
L = rep(L, 'return `<div class="lc" data-id="${s.id}">', 'return `<div class="lc ${picked.has(s.id) ? \'picked\' : \'\'}" data-id="${s.id}">');
L = rep(L, "if (e.target.closest('#ni, #q, .lc-ck')) return;", "if (e.target.closest('.lc-ck')) { if (P.select && ver === 'new') { picked.has(s.id) ? picked.delete(s.id) : picked.add(s.id); render(); } return; }\n  if (e.target.closest('#ni, #q')) return;");
L = rep(L, "    case 'share':", "    case 'dl': toast(`Télécharge « ${s.name} »`); break;\n    case 'delsel': { const n = picked.size; S = S.filter(x => !picked.has(x.id)); picked.clear(); toast(`${pl(n, 'média supprimé', 'médias supprimés')} (après confirmation dans l'application)`); break; }\n    case 'unsel': picked.clear(); break;\n    case 'share':");
L = rep(L, "`« ${s.name} » supprimée (après confirmation dans l'application)`", "`« ${s.name} » : supprimé après confirmation dans l'application`");
L = rep(L, "seg('#mk-ver', v => ver = v);", "seg('#mk-ver', v => { ver = v; picked.clear(); });");
L = rep(L, "${PAGE === 'compos' ? `${s.dim} ·", "${s.old ? s.old : PAGE === 'compos' ? `${s.dim} ·");
L = rep(L, "document.title = P.title + ' allégées (mode Simple)';", '');
L = rep(L, "IC.edit2 = IC.sliders;", "IC.edit2 = IC.sliders;\nIC.dl = '<path d=\"M12 3v12M7 10l5 5 5-5\"/><path d=\"M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4\"/>';\nIC.eye = '<path d=\"M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z\"/><circle cx=\"12\" cy=\"12\" r=\"3\"/>';");
L = rep(L, '.es-f.pe{color:var(--warn-ink)}', '.es-f.pe{color:var(--warn-ink)}\n.es-f.bad{color:var(--danger)}\n.es-f.mu{color:var(--muted);font-weight:400}\n.es-f.mu.on{color:#fff}\n.lc.picked{border-color:#2F6FC0;box-shadow:0 0 0 2px rgba(47,111,192,.22)}\n.lc.picked .lc-ck{opacity:1}\n.a-ph{display:grid;place-items:center;color:rgba(255,255,255,.92);font-weight:600;font-size:14px}\n.a-ph::after{content:"";position:absolute;left:0;right:0;bottom:0;height:45%;background:linear-gradient(180deg,transparent,rgba(0,0,0,.28))}\n.lc-dur{position:absolute;right:10px;top:10px;z-index:3;background:rgba(14,23,38,.82);color:#fff;font:500 11.5px/1 var(--mono);padding:4px 7px;border-radius:6px}');
const MEDIA = `,
  media: {
    file: 'mediatheque-simple-allegee.html', nav: 'contenus', title: 'Médiathèque', mk: 'Page Médiathèque allégée',
    one: 'média', many: 'médias', none: "Aucun média pour l'instant", select: true,
    intro: 'Vos images, vidéos et documents, importés une fois et réutilisables dans toutes vos listes de lecture.',
    tabs: [['Médiathèque', 'mediatheque-simple-allegee.html'], ['Compositions', 'compositions-simple-allegee.html']],
    add: null, addToast: "Ouvre le choix des fichiers de l'ordinateur", openToast: n => \`Ouvre l'aperçu de « \${n} »\`,
    preview: null, publish: false,
    menu: [['eye', 'open', 'Aperçu'], ['edit', 'ren', 'Renommer'], ['dl', 'dl', 'Télécharger'], ['users', 'share', 'Partager…']],
    empty: ['image', 'Glissez ici vos images et vos vidéos', 'Elles serviront dans vos listes de lecture.', 'Choisir des fichiers'],
    data: () => [
      ['Lac au matin', 'g1', 'img', 2, '1920 × 1080 · 2,4 Mo'], ['Menu de la semaine', 'g3', 'img', 1, '1920 × 1080 · 1,1 Mo'], ['Film de présentation', 'g5', 'vid', 1, '0:45 · 38 Mo', '0:45'],
      ['Affiche soldes', 'g6', 'img', 2, '1080 × 1920 · 900 Ko'], ['Équipe', 'g4', 'img', 0, '1920 × 1080 · 3,2 Mo'], ['Recette en vidéo', 'g2', 'vid', 1, '0:30 · 21 Mo', '0:30'],
      ['Plan du site', 'g5', 'img', 1, '1920 × 1080 · 640 Ko'], ['Coucher de soleil', 'g2', 'img', 0, '1920 × 1080 · 2,9 Mo'], ['Logo', 'g1', 'img', 3, '800 × 800 · 120 Ko'],
      ['Visite des locaux', 'g3', 'vid', 0, '1:20 · 74 Mo', '1:20'], ['Agenda de juin', 'g4', 'img', 1, '1920 × 1080 · 1,4 Mo'], ['Vœux', 'g6', 'img', 0, '1920 × 1080 · 2,0 Mo', null, 'De Anne']
    ].map((m, i) => ({ id: i + 1, name: m[0], t: m[2], use: m[3], by: m[6], m: m[2] === 'vid' ? 'Vidéo · ' + m[5] : 'Image', old: (m[2] === 'vid' ? 'Vidéo · ' : 'Image · ') + m[4],
      art: \`<div class="lc-art a-ph \${m[1]}">\${m[2] === 'vid' ? ico('play', 26) : ''}</div>\${m[2] === 'vid' ? \`<span class="lc-dur">\${m[5]}</span>\` : ''}\` })),
    stat: s => s.use ? 'Dans ' + pl(s.use, 'liste') : null, statOff: null,
    oldSt: s => s.use ? ['', 'Utilisé'] : ['off', 'Non utilisé'],
    filters: S => [['img', pl(S.filter(s => s.t === 'img').length, 'image'), 'mu', s => s.t === 'img', S.some(s => s.t === 'img')], ['vid', pl(S.filter(s => s.t === 'vid').length, 'vidéo'), 'mu', s => s.t === 'vid', S.some(s => s.t === 'vid')],
      ['unused', pl(S.filter(s => !s.use).length, 'non utilisé'), 'pe', s => !s.use, S.some(s => !s.use)]]
  }
};
const P = PAGES[PAGE];`;
L = rep(L, '\n};\nconst P = PAGES[PAGE];', MEDIA);
const page = (file, title, key) => out(file, title, rep(L, '__PAGE__', key));
page('listes-lecture-simple-allegee.html', 'Listes de lecture allégées (mode Simple)', 'listes');
page('listes-interactives-simple-allegee.html', 'Listes interactives allégées (mode Simple)', 'inter');
page('compositions-simple-allegee.html', 'Compositions allégées (mode Simple)', 'compos');
page('mediatheque-simple-allegee.html', 'Médiathèque allégée (mode Simple)', 'media');

/* ---- Applications : même coquille, autre script ---- */
const cut = L.indexOf('/* ---------- les trois pages ---------- */');
if (cut < 0) throw new Error('repère du gabarit introuvable');
let A = L.slice(0, cut) + fs.readFileSync(SP + '/apps-js.src', 'utf8');
A = rep(A, '<span>Contenu :</span>\n  <span class="mk-seg" id="mk-demo"><button type="button" class="mk-btn on" data-v="full">rempli</button><button type="button" class="mk-btn" data-v="empty">vide</button></span>',
  '<span>Page :</span>\n  <span class="mk-seg" id="mk-view"><button type="button" class="mk-btn on" data-v="mine">Applications</button><button type="button" class="mk-btn" data-v="store" style="border-radius:0;border-left:0">Store</button><button type="button" class="mk-btn" data-v="app">Une application</button></span>');
A = rep(A, '/* état vide */', `/* Applications */
.ap-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:14px}
.ap{display:flex;align-items:center;gap:14px;background:#fff;border:1px solid #DDE3EA;border-radius:14px;padding:14px 12px 14px 16px;position:relative;cursor:pointer;min-width:0;transition:box-shadow .15s,border-color .15s}
.ap:hover{box-shadow:0 8px 22px rgba(11,31,58,.09);border-color:#CFD7E2}
.ap.st{cursor:default}
.ap:has(.kebab-menu){z-index:42}
.ap-ic{flex:none;width:44px;height:44px;border-radius:12px;display:grid;place-items:center;color:#fff;font-weight:700;font-size:14px}
.ap-ic.big{width:52px;height:52px;border-radius:14px;font-size:17px}
.ap-t{flex:1;min-width:0}
.ap-t b{display:block;font-size:15px;font-weight:500;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.ap-t small{display:block;color:#5A6B80;font-size:13px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-top:1px}
.ap .lc-kw{margin:0}
.ap .switch,.ar .switch{flex:none}
.ap-ck{flex:none}
.ap-hd{display:flex;align-items:center;gap:16px}
.ap-crumb{margin-bottom:10px}
.ap-crumb a{color:var(--muted);text-decoration:none;font-size:14px}
.ap-crumb a:hover{color:var(--ink)}
.ar-list{background:#fff;border:1px solid var(--line);border-radius:12px}
.ar{display:grid;grid-template-columns:112px minmax(0,1fr) auto 36px;align-items:center;gap:18px;padding:14px 20px;border-top:1px solid var(--row);position:relative}
.ar:first-child{border-top:0}
.ar:hover{background:#FAFBFD}
.ar:first-child{border-radius:11px 11px 0 0}.ar:last-child{border-radius:0 0 11px 11px}
.ar-th{width:112px;height:63px;border-radius:8px;display:grid;place-items:center;color:#fff;font-weight:700;font-size:20px}
.ar.off .ar-th,.ar.off .lc-name{opacity:.5}
.ar-n{min-width:0}
.ar-n small{display:block;color:#5A6B80;font-size:13.5px;margin-top:2px}
.ar-n .lc-name{flex:none;max-width:100%;font-weight:600}
.ar .lc-kw{margin:0}
.ap-ov{position:fixed;inset:0;z-index:2500;background:rgba(11,31,58,.45);display:grid;place-items:center;padding:16px}
.ap-win{background:#fff;border-radius:16px;width:min(480px,100%);padding:24px;box-shadow:0 24px 60px rgba(11,31,58,.3)}
.ap-win h2{font-size:20px;margin-bottom:16px}
.ap-f{display:grid;gap:6px;font-size:14px;font-weight:500;margin-bottom:14px}
.ap-info{color:#2B3A50;font-size:14px;margin-bottom:10px}
.ap-foot{display:flex;justify-content:flex-end;gap:10px;margin-top:20px}
@media (max-width:760px){.ar{grid-template-columns:72px minmax(0,1fr) auto 36px;gap:12px;padding:12px 14px}.ar-th{width:72px;height:41px;font-size:14px}}
/* état vide */`);
out('applications-simple-allegee.html', 'Applications allégées (mode Simple)', A);
