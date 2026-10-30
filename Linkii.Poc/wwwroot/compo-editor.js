/* Éditeur de compositions (DEV-POC.md §80), monté par la page /compositions/{id} (Components/Pages/CompositionEditor.razor).
   Maquette validée : maquettes/editeur-compositions.html. Rendu des calques : player/composition.js, le même que sur l'écran.
   Appels au serveur (DotNetObjectReference) : Save(nom, document) -> message d'erreur ou null, Qr(texte) -> « côté|tracé »,
   Images() -> images de la médiathèque, Leave() -> retour à la page d'origine.
   Étape 3 (§85, maquette editeur-compositions-etape3.html) : Sources() -> contenus de Mes intégrations utilisables (météo, calendrier, RSS, capteurs),
   SourceData(id) -> { data, error } : leurs données actuelles, pour les blocs d'intégration et les champs reliés des textes. */
(function (w) {
  'use strict';
  const WEIGHTS = [[300, 'Léger'], [400, 'Normal'], [500, 'Moyen'], [600, 'Demi-gras'], [700, 'Gras'], [800, 'Extra-gras'], [900, 'Noir']];
  const WNAMES = { clock: 'Horloge', date: 'Date', ticker: 'Bandeau défilant', countdown: 'Compte à rebours' };
  const RES = {
    land: [[1920, 1080, '1920 × 1080 · Full HD (recommandé)'], [3840, 2160, '3840 × 2160 · 4K'], [1280, 720, '1280 × 720 · HD']],
    port: [[1080, 1920, '1080 × 1920 · Full HD (recommandé)'], [2160, 3840, '2160 × 3840 · 4K']]
  };
  const esc = s => String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  // pastille d'une source : vrai logo si elle en a un (rss, Home Assistant, météo), sinon le pictogramme sur sa couleur
  const wi = m => m.logo ? `<span class="wi is-brand"><svg width="100%" height="100%"><use href="/brand-logos.svg#${m.logo}" width="100%" height="100%"/></svg></span>` : `<span class="wi" style="background:${m.color}">${m.icon}</span>`;
  const LC = () => w.LinkiiCompo;

  // ---------- état de l'éditeur monté ----------
  let root = null, net = null, ac = null, timer = null;
  let D, name = '', savedDoc = '', savedName = '', H = [], hi = -1, uid = 1;
  let selId = null, zoom = 1, z = 1, panelName = 'modeles', panelOpen = true, layersOpen = false, mediaMode = 'add', editing = false;
  let myTpl = [], tplTab = 'galerie', canImport = false;   // « Mes modèles » (§84), import d'images permis par le niveau de droits
  let drag = null, guides = null, images = [], tplCat = 'Tous', mediaSearch = '', popCb = null, nudge = null, qrWait = {}, fmtNew = false, fmtO = 'land';
  let sources = [], srcData = {}, srcBusy = {}, srcAt = {}, names = false, fieldAnchor = null;   // sources de données (§85) et mode « Noms des champs »
  let dv = { view: 'sources', set: null, row: 0, open: new Set(['@used']), hidden: false, q: '', ren: null };   // panneau Données (§140) : vue, jeu ouvert, ligne montrée, groupes ouverts, recherche, champ renommé
  let lcOpen = new Set(), lcFor = null, lcDrag = null, sheet = null, canEditData = false;   // cartes de colonnes ouvertes (liste lcFor), tableau en grand, droit de renommer les champs d'un jeu
  let previewScreen = '';   // « Cet écran » (§139) : écran dont les paramètres servent à l'aperçu (vide : le premier qui en a)
  const q = s => root.querySelector(s);
  const cur = () => D.els.find(e => e.id === selId) || null;
  const byId = id => D.els.find(e => e.id === id);
  const textual = e => LC().textual(e);
  const nid = () => 'e' + (uid++);
  const k = () => Math.min(D.w, D.h) / 1080;   // échelle des éléments ajoutés : même aspect en Full HD et en 4K

  // ---------- fabriques de calques ----------
  const T = o => Object.assign({ id: nid(), type: 'text', x: 0, y: 0, w: 600, h: 80, text: 'Texte', font: 'Montserrat', size: 60, weight: 400, color: '#0B1F3A', align: 'left', ls: 0, lh: 1.15, italic: false, opacity: 1 }, o);
  const R = o => Object.assign({ id: nid(), type: 'shape', kind: 'rect', x: 0, y: 0, w: 300, h: 200, fill: '#3B82C4', stroke: '', sw: 6, radius: 0, rot: 0, opacity: 1 }, o);
  const I = o => Object.assign({ id: nid(), type: 'image', media: null, x: 0, y: 0, w: 640, h: 480, fit: 'cover', radius: 0, opacity: 1 }, o);
  const Q = o => Object.assign({ id: nid(), type: 'qr', x: 0, y: 0, w: 220, h: 220, fg: '#000000', bg: '#ffffff', url: 'https://www.exemple.ch', opacity: 1 }, o);
  const W = (kind, o) => Object.assign({ id: nid(), type: 'widget', kind, x: 0, y: 0, w: 420, h: 90, font: 'IBM Plex Sans', size: 56, weight: 500, color: '#0B1F3A', align: 'left', ls: 0, lh: 1.1, italic: false, opacity: 1 },
    kind === 'ticker' ? { w: 1920, h: 100, size: 44, bg: '#0B1F3A', color: '#ffffff', speed: 18, text: 'Votre message défile ici · Ajoutez autant d\'informations que nécessaire' } : {},
    kind === 'countdown' ? { until: new Date(Date.now() + 30 * 864e5).toISOString().slice(0, 10), w: 320 } : {}, o);

  // ---------- modèles (sans photo : les emplacements d'image se remplissent depuis la médiathèque) ----------
  const TPL = [
    { name: "Horaires d'ouverture", cat: 'Information', w: 1920, h: 1080, bg: '#EEF3EC', make: () => {
      const g = '#1F3B33', a = '#C9B98A', els = [
        R({ x: 0, y: 0, w: 820, h: 1080, fill: g }),
        R({ x: 80, y: 80, w: 200, h: 110, fill: g, stroke: '#ffffff', sw: 4, radius: 10 }),
        T({ x: 80, y: 97, w: 200, text: 'VOTRE\nLOGO', size: 34, weight: 800, align: 'center', color: '#ffffff', lh: 1.1 }),
        T({ x: 80, y: 260, w: 740, text: 'NOUVEAUX\nHORAIRES', size: 110, weight: 900, color: '#ffffff', lh: .98 }),
        R({ x: 80, y: 500, w: 640, h: 8, fill: a }),
        T({ x: 80, y: 540, w: 660, text: 'Merci de prendre note de nos horaires mis à jour.', size: 44, weight: 500, color: '#ffffff', font: 'Poppins' }),
        Q({ x: 80, y: 720, w: 200, h: 200 }),
        T({ x: 310, y: 760, w: 420, text: 'Scannez pour\nplus d\'infos', size: 50, weight: 500, color: '#ffffff', font: 'Poppins' }),
        R({ x: 960, y: 50, w: 880, h: 130, fill: '#ffffff', radius: 24 }),
        W('date', { x: 1000, y: 88, w: 520, size: 40, weight: 600, color: g, font: 'Poppins' }),
        W('clock', { x: 1540, y: 76, w: 260, size: 64, weight: 700, color: g, align: 'right', font: 'Poppins' })];
      [['Lundi – Vendredi', '09:00 – 18:00'], ['Samedi', '10:00 – 16:00'], ['Dimanche', 'Fermé']].forEach(([d, h], i) => {
        const y = 250 + i * 180;
        els.push(R({ x: 960, y, w: 880, h: 150, fill: '#ffffff', radius: 24 }), R({ kind: 'circle', x: 1000, y: y + 60, w: 30, h: 30, fill: g }),
          T({ x: 1050, y: y + 46, w: 420, text: d, size: 42, weight: 600, color: g, font: 'Poppins' }), R({ x: 1478, y: y + 30, w: 4, h: 90, fill: a }),
          T({ x: 1505, y: y + 46, w: 330, text: h, size: 42, color: g, font: 'Poppins' }));
      });
      els.push(T({ x: 960, y: 830, w: 880, text: 'Merci de votre compréhension.', size: 46, italic: true, color: g, align: 'center', font: 'Poppins' }),
        W('ticker', { x: 0, y: 980, w: 1920, h: 100, bg: '#14271F', font: 'Poppins', text: 'Horaires spéciaux pendant les jours fériés · Merci de votre patience · Retrouvez-nous sur notre site' }));
      return els;
    } },
    { name: 'Bienvenue', cat: 'Accueil', w: 1920, h: 1080, bg: '#0B1F3A', make: () => [
      R({ kind: 'circle', x: 1180, y: -260, w: 1000, h: 1000, fill: '#16304F' }),
      R({ kind: 'circle', x: 1460, y: 560, w: 700, h: 700, fill: '#1D3A63' }),
      T({ x: 140, y: 90, w: 600, text: 'VOTRE LOGO', size: 36, weight: 700, ls: .2, color: '#ffffff', font: 'IBM Plex Sans' }),
      T({ x: 140, y: 330, w: 1100, text: 'Bienvenue', size: 200, weight: 900, color: '#ffffff', font: 'Playfair Display' }),
      R({ x: 150, y: 600, w: 160, h: 10, fill: '#3B82C4' }),
      T({ x: 140, y: 650, w: 1000, text: "Nous sommes ravis de vous accueillir aujourd'hui.", size: 56, weight: 300, color: '#ffffff', font: 'IBM Plex Sans' }),
      W('clock', { x: 1380, y: 80, w: 400, size: 110, weight: 300, color: '#ffffff', align: 'right' }),
      W('date', { x: 1180, y: 230, w: 600, size: 40, weight: 400, color: '#ffffff', align: 'right' })] },
    { name: 'Orientation', cat: 'Signalétique', w: 1920, h: 1080, bg: '#151515', make: () => {
      const els = [];
      const rows = (x, y, list, right) => list.forEach(([n, t], i) => {
        const yy = y + i * 72;
        if (right) els.push(T({ x, y: yy, w: 440, text: t, size: 30, weight: 600, ls: .25, color: '#ffffff' }), T({ x: x + 460, y: yy - 4, w: 80, text: n, size: 40, weight: 800, color: '#ffffff', align: 'right' }));
        else els.push(T({ x, y: yy - 4, w: 80, text: n, size: 40, weight: 800, color: '#ffffff' }), T({ x: x + 110, y: yy, w: 440, text: t, size: 30, weight: 600, ls: .25, color: '#ffffff' }));
      });
      els.push(R({ kind: 'arrow', x: 120, y: 120, w: 100, h: 60, fill: '#ffffff', rot: 180 }), R({ kind: 'arrow', x: 1700, y: 120, w: 100, h: 60, fill: '#ffffff' }));
      rows(130, 240, [['0', 'ACCUEIL'], ['0', 'CAFÉTÉRIA'], ['1', 'SALLE A'], ['1', 'SALLE B']], false);
      rows(1200, 240, [['2', 'BUREAUX A'], ['2', 'BUREAUX B'], ['3', 'DIRECTION'], ['3', 'TERRASSE']], true);
      els.push(R({ kind: 'line', x: 130, y: 560, w: 600, h: 10, fill: '#555555', sw: 3 }), R({ kind: 'line', x: 1190, y: 560, w: 600, h: 10, fill: '#555555', sw: 3 }),
        R({ kind: 'arrow', x: 140, y: 610, w: 100, h: 60, fill: '#ffffff', rot: 135 }), R({ kind: 'arrow', x: 1690, y: 610, w: 100, h: 60, fill: '#ffffff', rot: 45 }));
      rows(130, 720, [['-1', 'PARKING'], ['-1', 'VESTIAIRES'], ['-2', 'ARCHIVES']], false);
      rows(1200, 720, [['4', 'SALLE PANORAMA'], ['4', 'ESPACE DÉTENTE'], ['5', 'TOIT']], true);
      els.push(R({ x: 760, y: 0, w: 400, h: 980, fill: '#000000' }),
        T({ x: 780, y: 80, w: 360, text: 'Vous cherchez\nvotre chemin ?', size: 54, weight: 800, align: 'center', color: '#ffffff', lh: 1.05 }),
        T({ x: 790, y: 240, w: 340, text: 'SCANNEZ LE CODE POUR LE PLAN INTERACTIF', size: 20, weight: 500, ls: .2, align: 'center', color: '#ffffff' }),
        Q({ x: 840, y: 340, w: 240, h: 240 }), I({ x: 800, y: 620, w: 320, h: 320, radius: 24 }),
        R({ x: 0, y: 980, w: 1920, h: 100, fill: '#ffffff' }),
        T({ x: 0, y: 1012, w: 1920, text: 'PLAN INTERACTIF SUR NOTRE SITE · WWW.VOTRESITE.CH', size: 28, weight: 600, ls: .25, align: 'center', color: '#151515' }));
      return els;
    } },
    { name: 'Annonce', cat: 'Promotion', w: 1920, h: 1080, bg: '#FFFFFF', make: () => [
      I({ x: 960, y: 0, w: 960, h: 1080 }),
      R({ x: 140, y: 200, w: 260, h: 70, radius: 35, fill: '#E07B1A' }),
      T({ x: 140, y: 213, w: 260, text: 'NOUVEAU', size: 32, weight: 700, ls: .15, align: 'center', color: '#ffffff' }),
      T({ x: 140, y: 320, w: 780, text: "Votre titre\nd'annonce ici", size: 110, weight: 900, lh: 1, color: '#0B1F3A' }),
      T({ x: 140, y: 580, w: 720, text: 'Décrivez votre offre ou votre message en une ou deux phrases.', size: 40, color: '#5A6B80', font: 'IBM Plex Sans' }),
      Q({ x: 140, y: 760, w: 180, h: 180 }),
      T({ x: 350, y: 795, w: 420, text: 'Scannez pour\nen savoir plus', size: 40, weight: 600, color: '#0B1F3A' })] },
    { name: 'Chiffres clés', cat: 'Information', w: 1920, h: 1080, bg: '#0B1F3A', make: () => {
      const els = [
        T({ x: 140, y: 110, w: 1200, text: 'Nos chiffres clés', size: 90, weight: 800, color: '#ffffff' }),
        T({ x: 140, y: 230, w: 900, text: 'Mis à jour chaque mois', size: 36, color: '#9FB4CC', font: 'IBM Plex Sans' })];
      [['98 %', 'de visiteurs satisfaits'], ['1 200', 'visites par semaine'], ['24/7', 'service disponible']].forEach(([n, l], i) => {
        const x = 140 + i * 560;
        els.push(R({ x, y: 360, w: 520, h: 420, fill: '#16304F', radius: 24 }), T({ x: x + 44, y: 430, w: 440, text: n, size: 140, weight: 800, color: '#ffffff' }),
          R({ x: x + 48, y: 620, w: 80, h: 8, fill: '#3B82C4' }), T({ x: x + 44, y: 660, w: 440, text: l, size: 38, color: '#C5D3E3', font: 'IBM Plex Sans' }));
      });
      els.push(W('date', { x: 140, y: 900, w: 700, size: 32, weight: 400, color: '#9FB4CC' }), W('clock', { x: 1500, y: 880, w: 280, size: 60, color: '#ffffff', align: 'right' }));
      return els;
    } },
    { name: 'Journée portes ouvertes', cat: 'Événement', w: 1080, h: 1920, bg: '#111827', make: () => [
      I({ x: 0, y: 0, w: 1080, h: 1000 }),
      R({ x: 0, y: 1000, w: 1080, h: 920, fill: '#111827' }),
      T({ x: 80, y: 1070, w: 900, text: 'SAMEDI 14 NOVEMBRE', size: 36, weight: 700, ls: .2, color: '#F2B134' }),
      T({ x: 80, y: 1140, w: 920, text: 'Journée\nportes ouvertes', size: 120, weight: 900, lh: .95, color: '#ffffff' }),
      T({ x: 80, y: 1440, w: 900, text: 'De 10 h à 17 h · Entrée libre · Animations pour petits et grands', size: 40, color: '#CBD5E1', font: 'IBM Plex Sans' }),
      Q({ x: 80, y: 1620, w: 220, h: 220 }),
      T({ x: 340, y: 1660, w: 420, text: 'Inscription\net programme', size: 44, weight: 600, color: '#ffffff' }),
      W('countdown', { x: 720, y: 1665, w: 280, size: 96, weight: 800, color: '#F2B134', align: 'right', font: 'Montserrat' })] }
  ].concat(w.LinkiiCompoTemplates ? w.LinkiiCompoTemplates({ T, R, I, Q, W }) : []);   // modèles par secteur : compo-templates.js
  let samples = null;   // vignettes : un exemplaire de chaque modèle
  const ensureSamples = () => { if (!samples) samples = TPL.map(t => t.make()); };

  // ---------- montage ----------
  function mount(el, o, dotnet) {
    unmount();
    root = el; net = dotnet; ac = new AbortController();
    LC().ensure();
    images = o.images || [];
    sources = o.sources || []; srcData = {}; srcBusy = {}; srcAt = {}; names = false;
    myTpl = o.myTemplates || []; canImport = !!o.canImport; canEditData = !!o.canEditData; tplTab = 'galerie';
    dv = { view: 'sources', set: null, row: 0, open: new Set(['@used']), hidden: false, q: '', ren: null }; lcOpen = new Set(); lcFor = null; lcDrag = null; sheet = null;
    name = savedName = o.name || 'Composition';
    let doc = null;
    try { doc = o.doc ? JSON.parse(o.doc) : null; } catch (x) { doc = null; }
    const fresh = !doc || !Array.isArray(doc.els);
    const tpl = fresh && Number.isInteger(o.tpl) && TPL[o.tpl] ? TPL[o.tpl] : null;   // départ depuis la galerie de la page Compositions
    D = tpl ? { v: 1, w: tpl.w, h: tpl.h, bg: tpl.bg, els: tpl.make() }
      : fresh ? { v: 1, w: 1920, h: 1080, bg: '#FFFFFF', els: [] } : { v: 1, w: doc.w, h: doc.h, bg: doc.bg || '#FFFFFF', els: doc.els };
    if (tpl && !o.name) name = savedName = tpl.name;
    uid = 1 + D.els.reduce((m, e) => Math.max(m, parseInt(String(e.id || '').replace(/\D/g, ''), 10) || 0), 0);
    D.els.forEach(e => { if (!e.id) e.id = nid(); });
    ensureSamples();
    H = []; hi = -1; selId = null; zoom = 1; panelName = fresh && !tpl ? 'modeles' : 'texte'; panelOpen = true; layersOpen = false; mediaMode = 'add';
    editing = false; drag = null; guides = null; tplCat = 'Tous'; mediaSearch = ''; qrWait = {};
    root.innerHTML = SHELL;
    q('#ce-name').value = name;
    bind();
    push();
    savedDoc = tpl || o.fromTemplate ? '' : H[0];   // un modèle choisi (galerie ou Mes modèles) n'est pas encore enregistré
    renderPanel(); render(); renderCtx(); setDirty();
    D.els.filter(e => e.type === 'qr').forEach(fetchQr);
    LC().sources(D).forEach(id => loadSource(id));
    timer = setInterval(() => { const st = q('#ce-stage'); if (st) LC().tick(st); }, 1000);
    if (fresh && !tpl) openFormat(true);
    if (document.fonts) document.fonts.ready.then(() => { if (root) render(); });
  }

  function unmount() {
    closePreview();
    if (ac) ac.abort();
    if (timer) clearInterval(timer);
    if (root) root.innerHTML = '';
    root = null; net = null; ac = null; timer = null; sheet = null;
  }

  const ICO = {
    back: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M19 12H5M11 6l-6 6 6 6"/></svg>',
    undo: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M9 14 4 9l5-5"/><path d="M4 9h10a6 6 0 0 1 0 12h-3"/></svg>',
    redo: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="m15 14 5-5-5-5"/><path d="M20 9H10a6 6 0 0 0 0 12h3"/></svg>',
    layers: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="m12 3 9 5-9 5-9-5 9-5Z"/><path d="m3 13 9 5 9-5"/></svg>',
    eye: '<svg viewBox="0 0 24 24" width="17" height="17" fill="none" stroke="currentColor" stroke-width="2"><path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12Z"/><circle cx="12" cy="12" r="3"/></svg>',
    tpl: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><rect x="3" y="3" width="18" height="18" rx="2"/><path d="M3 9h18M9 9v12"/></svg>',
    img: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="9" cy="10" r="2"/><path d="m21 16-5-5-9 9"/></svg>',
    txt: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><path d="M5 6V4h14v2M12 4v16M9 20h6"/></svg>',
    shp: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><path d="M8 3 3 11h10L8 3Z"/><circle cx="17" cy="16" r="4"/><rect x="3" y="14" width="7" height="7"/></svg>',
    wid: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></svg>',
    data: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><ellipse cx="12" cy="6" rx="8" ry="3"/><path d="M4 6v6c0 1.7 3.6 3 8 3s8-1.3 8-3V6"/><path d="M4 12v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6"/></svg>'
  };
  const SHELL = `
<div class="ce-top">
  <button class="ce-tb ce-back" data-top="back" title="Retour">${ICO.back}</button>
  <input class="ce-name" id="ce-name" maxlength="80" aria-label="Nom de la composition">
  <button class="ce-fmt" id="ce-fmt" data-top="format" title="Format de la composition"></button>
  <span class="ce-sep"></span>
  <button class="ce-tb" id="ce-undo" data-top="undo" title="Annuler (Ctrl+Z)">${ICO.undo}</button>
  <button class="ce-tb" id="ce-redo" data-top="redo" title="Rétablir (Ctrl+Y)">${ICO.redo}</button>
  <span class="ce-sep"></span>
  <button class="ce-tb" data-top="zout" title="Dézoomer">−</button><span class="ce-zoomv" id="ce-zoomv">100%</span><button class="ce-tb" data-top="zin" title="Zoomer">+</button>
  <span class="ce-sep"></span>
  <button class="ce-tb" data-top="bg"><span class="ce-sw" id="ce-bgsw"></span>Fond</button>
  <button class="ce-tb" id="ce-lybtn" data-top="layers">${ICO.layers}Calques</button>
  <span style="flex:1"></span>
  <span class="ce-seg" id="ce-names" title="Afficher les valeurs actuelles des sources, ou le nom des champs reliés"><button data-top="names0" class="on">Données réelles</button><button data-top="names1">Noms des champs</button></span>
  <span class="ce-dirty" id="ce-dirty"></span>
  <button class="ce-btn ghost" data-top="preview">${ICO.eye}Aperçu</button>
  <button class="ce-btn ghost" data-top="revert" id="ce-revert">Annuler</button>
  <span class="ce-split"><button class="ce-btn" id="ce-save" data-top="save">Enregistrer</button><button class="ce-btn" data-top="savemenu" title="Autres options" aria-label="Autres options d'enregistrement">▾</button></span>
</div>
<div class="ce-main">
  <nav class="ce-rail" id="ce-rail">
    <button data-p="modeles">${ICO.tpl}Modèles</button>
    <button data-p="medias">${ICO.img}Médias</button>
    <button data-p="texte">${ICO.txt}Texte</button>
    <button data-p="formes">${ICO.shp}Éléments</button>
    <button data-p="widgets">${ICO.wid}Widgets</button>
    <button data-p="donnees">${ICO.data}Données</button>
  </nav>
  <aside class="ce-panel" id="ce-panel"></aside>
  <div class="ce-center">
    <button class="ce-pull" id="ce-pull" title="Replier le panneau">‹</button>
    <div class="ce-ctx" id="ce-ctx"></div>
    <div class="ce-area" id="ce-area"><div class="ce-frame" id="ce-frame"><div class="ce-stage" id="ce-stage"></div><div class="ce-ov" id="ce-ov"></div></div></div>
  </div>
  <aside class="ce-layers closed" id="ce-layers"></aside>
</div>
<div class="ce-scrim" id="ce-fmtscrim" hidden><div class="ce-modal" role="dialog" aria-modal="true" aria-labelledby="ce-fmttitle">
  <div class="ce-mh"><h4 id="ce-fmttitle">Nouvelle composition</h4><button class="ce-xb" data-fmt="close" aria-label="Fermer">×</button></div>
  <div class="ce-mb">
    <div class="ce-oris" id="ce-oris">
      <button class="ce-ori" data-o="land"><span class="scr" style="width:86px;height:50px">16:9</span>Paysage</button>
      <button class="ce-ori" data-o="port"><span class="scr" style="width:32px;height:56px">9:16</span>Portrait</button>
      <button class="ce-ori" data-o="custom"><span class="scr" style="width:62px;height:50px;border-style:dashed">⤡</span>Personnalisé</button>
    </div>
    <div class="ce-field" id="ce-resfield"><label for="ce-res">Résolution</label><select id="ce-res"></select></div>
    <div class="ce-field" id="ce-customfield"><label>Taille en pixels</label>
      <div class="ce-wh"><input id="ce-cw" type="number" min="320" max="7680" value="3840" aria-label="Largeur"> × <input id="ce-ch" type="number" min="320" max="7680" value="1080" aria-label="Hauteur"></div>
      <p class="ce-help" style="margin:0">Pour un mur de 2 écrans Full HD côte à côte : 3840 × 1080. Pour un mur 2 × 2 : 3840 × 2160.</p></div>
    <div class="ce-field" id="ce-startfield"><label>Point de départ</label>
      <div class="ce-radios"><label><input type="radio" name="ce-start" value="tpl" checked> Choisir un modèle</label><label><input type="radio" name="ce-start" value="blank"> Page vide</label></div></div>
    <p class="ce-help" id="ce-fmthelp">Si le rapport d'aspect change, les éléments gardent leur place : vérifiez la mise en page.</p>
  </div>
  <div class="ce-mf"><button class="ce-btn ghost" data-fmt="close">Annuler</button><button class="ce-btn" data-fmt="ok" id="ce-fmtok">Créer</button></div>
</div></div>
<div class="ce-scrim" id="ce-tplscrim" hidden><div class="ce-modal" role="dialog" aria-modal="true" aria-labelledby="ce-tpltitle">
  <div class="ce-mh"><h4 id="ce-tpltitle">Enregistrer comme modèle</h4><button class="ce-xb" data-tplm="close" aria-label="Fermer">×</button></div>
  <div class="ce-mb">
    <div class="ce-field"><label for="ce-tplname">Nom du modèle</label><input type="text" id="ce-tplname" maxlength="80"></div>
    <p class="ce-help" style="margin:0">Le modèle rejoint « Mes modèles » : il servira de point de départ aux nouvelles compositions de cette aire. Cette composition ne change pas.</p>
  </div>
  <div class="ce-mf"><button class="ce-btn ghost" data-tplm="close">Annuler</button><button class="ce-btn" data-tplm="ok">Enregistrer le modèle</button></div>
</div></div>
<div class="ce-pvw" id="ce-pvw" hidden><button class="close" data-top="closepv">× Fermer l'aperçu (Échap)</button><div class="pf" id="ce-pf"></div></div>
<div class="ce-toast" id="ce-toast" role="status" aria-live="polite"></div>`;

  // ---------- rendu ----------
  const mediaUrl = id => { const m = images.find(x => x.id === id); return m ? m.url : null; };
  const srcMap = () => { const m = {}; sources.forEach(s => m[s.id] = s); return m; };
  const opts = extra => Object.assign({ mediaUrl, data: srcData, sources: srcMap(), names }, extra);   // rendu : images, données des sources, noms des champs
  const stageHTML = d => LC().html(d, opts());
  const loadImages = st => LC().images(st, (u, cb) => cb(u));

  function render() {
    if (!root) return;
    const A = q('#ce-area').getBoundingClientRect();
    const fit = Math.max(.02, Math.min((A.width - 80) / D.w, (A.height - 110) / D.h));
    z = fit * zoom;
    const fw = D.w * z, fh = D.h * z, fr = q('#ce-frame');
    Object.assign(fr.style, { width: fw + 'px', height: fh + 'px', left: Math.max(40, (A.width - fw) / 2) + 'px', top: Math.max(72, (A.height - fh) / 2 + 26) + 'px', marginBottom: '40px' });
    const st = q('#ce-stage');
    Object.assign(st.style, { width: D.w + 'px', height: D.h + 'px', transform: `scale(${z})`, background: LC().bg(D) });
    st.classList.toggle('ce-names', names);
    if (!editing) {
      st.innerHTML = stageHTML(D);
      loadImages(st);
      st.querySelectorAll('.lc-auto').forEach(n => { const e = byId(n.dataset.id); if (e) e.h = n.offsetHeight; });
    }
    drawOverlay(); renderLayers();
    q('#ce-zoomv').textContent = Math.round(zoom * 100) + '%';
    q('#ce-undo').disabled = hi <= 0; q('#ce-redo').disabled = hi >= H.length - 1;
    q('#ce-bgsw').style.background = LC().bg(D);
    const land = D.w >= D.h;
    q('#ce-fmt').innerHTML = `<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-dasharray="3 2"><rect x="${land ? 3 : 6}" y="${land ? 6 : 3}" width="${land ? 18 : 12}" height="${land ? 12 : 18}"/></svg>${ratio(D.w, D.h)} · ${D.w} × ${D.h}`;
  }
  function ratio(a, b) { const g = (x, y) => y ? g(y, x % y) : x; const r = `${a / g(a, b)}:${b / g(a, b)}`; return r.length > 7 ? (a / b).toFixed(2) + ':1' : r; }

  function drawOverlay() {
    const e = cur(), ov = q('#ce-ov');
    let h = '';
    if (e && !e.hidden) {
      const L = e.x * z, Tp = e.y * z, Wd = e.w * z, Ht = e.h * z, r = +e.rot || 0;
      // cadre, poignées et poignée de rotation tournent avec l'élément (même centre que le rendu)
      h += `<div class="ce-selwrap" style="left:${L}px;top:${Tp}px;width:${Wd}px;height:${Ht}px;transform:rotate(${r}deg)"><div class="ce-selbox${e.locked ? ' locked' : ''}"></div>`;
      if (!e.locked && !editing) {
        const P = { nw: [0, 0], n: [.5, 0], ne: [1, 0], e: [1, .5], se: [1, 1], s: [.5, 1], sw: [0, 1], w: [0, .5] };
        (textual(e) ? ['w', 'e'] : Object.keys(P)).forEach(d => h += `<div class="ce-hd" data-d="${d}" style="left:${P[d][0] * 100}%;top:${P[d][1] * 100}%"></div>`);
        h += '<div class="ce-hd ce-rot" data-d="rot" title="Faire pivoter (Maj : par pas de 15°)"></div>';
      }
      h += '</div>';
      if (drag && drag.moved) h += `<div class="ce-tag" style="left:${L + Wd / 2}px;top:${Tp + Ht}px">${drag.mode === 'rot' ? `${Math.round(r)}°` : drag.mode === 'r' ? `${Math.round(e.w)} × ${Math.round(e.h)}` : `x ${Math.round(e.x)} · y ${Math.round(e.y)}`}</div>`;
    }
    (guides || []).forEach(g => h += g.v != null
      ? `<div class="ce-guide" style="left:${g.v * z}px;top:0;width:1px;height:100%"></div>`
      : `<div class="ce-guide" style="top:${g.h * z}px;left:0;height:1px;width:100%"></div>`);
    ov.innerHTML = h;
  }

  // ---------- historique et enregistrement ----------
  function push() { H = H.slice(0, hi + 1); H.push(JSON.stringify(D)); hi = H.length - 1; if (H.length > 100) { H.shift(); hi--; } }
  function commit() { if (!root) return; render(); push(); setDirty(); q('#ce-undo').disabled = hi <= 0; q('#ce-redo').disabled = true; }
  function restore(json) { D = JSON.parse(json); if (!cur()) selId = null; render(); renderCtx(); setDirty(); if (panelName === 'donnees' && panelOpen) renderPanel(); }
  function undo() { if (hi > 0) restore(H[--hi]); }
  function redo() { if (hi < H.length - 1) restore(H[++hi]); }
  const isDirty = () => JSON.stringify(D) !== savedDoc || name !== savedName;
  function setDirty() {
    const d = isDirty();
    q('#ce-dirty').textContent = d ? 'Modifications non enregistrées' : 'Enregistré';
    q('#ce-revert').disabled = !d;
  }

  async function save(thenLeave) {
    if (editing && document.activeElement) document.activeElement.blur();
    const n = q('#ce-name').value.trim();
    if (!n) { toast('Donnez un nom à la composition.', true); q('#ce-name').focus(); return; }
    name = n;
    const btn = q('#ce-save');
    btn.disabled = true;
    const json = JSON.stringify(D);
    let err;
    try { err = await net.invokeMethodAsync('Save', name, json); }
    catch (x) { err = 'Enregistrement impossible : la connexion au serveur est interrompue. Réessayez dans un instant.'; }
    if (!root) return;
    btn.disabled = false;
    if (err) { toast(err, true); return; }
    savedDoc = json; savedName = name; setDirty();
    if (thenLeave) { leave(true); return; }
    toast('Composition enregistrée. Publiez les écrans concernés pour la diffuser.');
  }
  function leave(force) {
    if (!force && isDirty() && !confirm('Quitter sans enregistrer vos modifications ?')) return;
    savedDoc = JSON.stringify(D); savedName = name;   // plus d'avertissement en quittant la page
    net.invokeMethodAsync('Leave');
  }
  function revert() {
    if (!isDirty() || !confirm('Revenir à la dernière version enregistrée ?')) return;
    name = savedName; q('#ce-name').value = name;
    restore(savedDoc); push();
    toast('Dernière version enregistrée rétablie');
  }

  // ---------- événements ----------
  function on(el, type, fn) { el.addEventListener(type, fn, { signal: ac.signal }); }
  function bind() {
    const frame = q('#ce-frame'), area = q('#ce-area'), ctx = q('#ce-ctx'), panel = q('#ce-panel'), layers = q('#ce-layers');
    on(root, 'click', ev => {
      const t = ev.target.closest('[data-top]');
      if (t) top(t.dataset.top, t);
      const tm = ev.target.closest('[data-tplm]');
      if (tm) { if (tm.dataset.tplm === 'ok') saveTemplate(); else q('#ce-tplscrim').hidden = true; }
      const f = ev.target.closest('[data-fmt]');
      if (f) { if (f.dataset.fmt === 'ok') applyFormat(); else closeFormat(); }
      const o = ev.target.closest('.ce-ori');
      if (o) { fmtO = o.dataset.o; fmtUI(); }
    });
    on(q('#ce-name'), 'input', ev => { name = ev.target.value; setDirty(); });
    on(q('#ce-rail'), 'click', ev => { const b = ev.target.closest('button'); if (!b) return; if (panelName === b.dataset.p && panelOpen) togglePanel(); else openPanel(b.dataset.p); });
    on(q('#ce-pull'), 'click', togglePanel);

    on(frame, 'pointerdown', ev => {
      if (ev.target.closest('[contenteditable=true]')) return;
      closePop();
      const hd = ev.target.closest('.ce-hd'), e0 = cur();
      if (hd && e0 && hd.dataset.d === 'rot') {   // rotation autour du centre de l'élément
        const fr = frame.getBoundingClientRect();
        drag = { mode: 'rot', sx: ev.clientX, sy: ev.clientY, cx: fr.left + (e0.x + e0.w / 2) * z, cy: fr.top + (e0.y + e0.h / 2) * z, o: Object.assign({}, e0) };
        ev.preventDefault(); return;
      }
      if (hd && e0) { drag = { mode: 'r', dir: hd.dataset.d, sx: ev.clientX, sy: ev.clientY, o: Object.assign({}, e0) }; ev.preventDefault(); return; }
      const n = ev.target.closest('.lc-el');
      if (n) {
        if (selId !== n.dataset.id) select(n.dataset.id);
        const e = cur();
        if (e && !e.locked) drag = { mode: 'm', sx: ev.clientX, sy: ev.clientY, o: Object.assign({}, e) };
        ev.preventDefault();
      } else select(null);
    });
    on(area, 'pointerdown', ev => { if (ev.target === area) { closePop(); if (editing) document.activeElement.blur(); select(null); } });
    on(w, 'pointermove', move);
    on(w, 'pointerup', () => { if (!drag) return; const m = drag.moved; drag = null; guides = null; if (m) { commit(); renderCtx(); } else drawOverlay(); });   // renderCtx : la rotation et la taille affichées suivent
    on(frame, 'dblclick', ev => {
      const n = ev.target.closest('.lc-el'); if (!n) return;
      const lh = ev.target.closest('[data-lh]'), le = byId(n.dataset.id);
      if (lh && le && le.type === 'list' && !le.locked) { renameHead(le, lh); return; }
      const e = le; if (!e || e.type !== 'text' || e.locked) return;
      const tx = n.querySelector('.lc-tx');
      tx.querySelectorAll('.lc-fd').forEach(f => f.contentEditable = 'false');   // champ relié : une pastille, supprimée ou gardée d'un bloc
      editing = true; tx.contentEditable = 'true'; tx.focus();
      const r = document.createRange(); r.selectNodeContents(tx); const s = getSelection(); s.removeAllRanges(); s.addRange(r);
      drawOverlay();
      tx.addEventListener('input', () => { e.h = n.offsetHeight; drawOverlay(); });
      tx.addEventListener('keydown', ev => { if (ev.key === '{') { ev.preventDefault(); fieldMenu(q('[data-act=field]') || tx); } });   // « { » : liste des champs
      tx.addEventListener('blur', () => { e.text = serialize(tx).replace(/\n$/, ''); editing = false; closePop(); commit(); renderCtx(); }, { once: true });
    });

    // glisser depuis le panneau vers la page
    on(panel, 'dragstart', ev => {
      const gr = ev.target.closest && ev.target.closest('.grip');
      if (gr) { lcDrag = +gr.closest('[data-ci]').dataset.ci; ev.dataTransfer.setData('text/plain', ''); ev.dataTransfer.effectAllowed = 'move'; return; }   // ordre des colonnes de la liste
      const a = ev.target.closest('[data-add]'); if (a) ev.dataTransfer.setData('text/linkii-compo', a.dataset.add);
    });
    on(panel, 'dragover', ev => { if (lcDrag != null && ev.target.closest('[data-ci]')) ev.preventDefault(); });
    on(panel, 'drop', ev => {
      const t = ev.target.closest('[data-ci]'), e = cur(); if (lcDrag == null || !t || !e || e.type !== 'list') return;
      ev.preventDefault();
      const to = +t.dataset.ci; if (to !== lcDrag) { e.cols.splice(to, 0, e.cols.splice(lcDrag, 1)[0]); lcOpen = new Set(); commit(); renderPanel(); }
      lcDrag = null;
    });
    on(panel, 'dragend', () => { lcDrag = null; });
    on(area, 'dragover', ev => { if (ev.dataTransfer.types.includes('text/linkii-compo')) ev.preventDefault(); });
    on(area, 'drop', ev => {
      const key = ev.dataTransfer.getData('text/linkii-compo'); if (!key) return;
      ev.preventDefault();
      const r = frame.getBoundingClientRect();
      addEl(key, { x: (ev.clientX - r.left) / z, y: (ev.clientY - r.top) / z });
    });
    on(panel, 'click', panelClick);
    on(panel, 'change', ev => { if (ev.target.id === 'ce-scrsel') { previewScreen = ev.target.value; loadSource(LC().SCREEN, true); return; } listInput(ev.target, true); });
    on(panel, 'input', ev => {
      const t = ev.target;
      if (t.id === 'ce-msearch') { mediaSearch = t.value; renderMediaGrid(); return; }
      if (t.id === 'ce-dq') { dv.q = t.value; renderPanel(); const n = q('#ce-dq'); if (n) { n.focus(); n.setSelectionRange(n.value.length, n.value.length); } return; }
      if (t.id !== 'ce-fren' && t.id !== 'ce-cpq') listInput(t, false);
    });

    on(ctx, 'input', ev => {
      const t = ev.target, key = t.dataset.k, e = cur(); if (!key || !e) return;
      let v = t.value; if (t.dataset.t === 'n') v = +v;
      e[key] = v;
      if (e.type === 'list' && key === 'source') resetList(e);
      if (key === 'font') t.style.fontFamily = `'${v}'`;
      if (e.type === 'qr' && key === 'url') { e.qr = ''; fetchQr(e, 500); }
      render();
    });
    on(ctx, 'change', ev => { if (ev.target.dataset.k) commit(); if (ev.target.dataset.k === 'source') loadSource(ev.target.value); });
    on(ctx, 'mousedown', ev => { if (editing && ev.target.closest('[data-act=field]')) ev.preventDefault(); });   // le texte en cours d'écriture garde le curseur
    on(ctx, 'click', ev => {
      const b = ev.target.closest('button'), e = cur(); if (!b || !e) return;
      if (b.dataset.step) { const [key, d] = b.dataset.step.split(':'); e[key] = Math.max(8, Math.round(e[key] + +d * k())); commit(); renderCtx(); }
      else if (b.dataset.set) { const [key, v] = b.dataset.set.split(':'); e[key] = v; commit(); renderCtx(); }
      else if (b.dataset.tog) { e[b.dataset.tog] = !e[b.dataset.tog]; commit(); renderCtx(); }
      else if (b.dataset.act) act(b.dataset.act, b);
    });

    on(layers, 'click', ev => {
      const r = ev.target.closest('.ce-ly'); if (!r) return;
      const e = byId(r.dataset.id), b = ev.target.closest('[data-ly]');
      if (b) { e[b.dataset.ly] = !e[b.dataset.ly]; if (e.hidden && selId === e.id) selId = null; commit(); renderCtx(); }
      else select(e.id);
    });
    let lyDrag = null;
    on(layers, 'dragstart', ev => { const r = ev.target.closest('.ce-ly'); if (r) lyDrag = r.dataset.id; });
    on(layers, 'dragover', ev => {
      const r = ev.target.closest('.ce-ly'); if (!r || !lyDrag) return;
      ev.preventDefault(); layers.querySelectorAll('.over').forEach(x => x.classList.remove('over')); r.classList.add('over');
    });
    on(layers, 'drop', ev => {
      const r = ev.target.closest('.ce-ly'); if (!r || !lyDrag) return;
      ev.preventDefault();
      const m = byId(lyDrag), t = byId(r.dataset.id); lyDrag = null;
      if (m === t) { renderLayers(); return; }
      D.els.splice(D.els.indexOf(m), 1); D.els.splice(D.els.indexOf(t) + 1, 0, m); commit();
    });

    on(document, 'pointerdown', ev => { const p = q('#ce-pop'); if (p && !p.contains(ev.target) && !ev.target.closest('[data-top],[data-act]')) closePop(); }, true);
    on(document, 'keydown', keys);
    on(w, 'resize', render);
    on(w, 'beforeunload', ev => { if (isDirty()) { ev.preventDefault(); ev.returnValue = ''; } });
  }

  function top(a, b) {
    if (a === 'back') leave(false);
    else if (a === 'format') openFormat(false);
    else if (a === 'undo') undo();
    else if (a === 'redo') redo();
    else if (a === 'zin') setZoom(zoom + .1);
    else if (a === 'zout') setZoom(zoom - .1);
    else if (a === 'bg') bgMenu(b);
    else if (a === 'layers') toggleLayers();
    else if (a === 'preview') openPreview();
    else if (a === 'closepv') closePreview();
    else if (a === 'revert') revert();
    else if (a === 'save') save(false);
    else if (a === 'names0' || a === 'names1') { names = a === 'names1'; root.querySelectorAll('#ce-names button').forEach(x => x.classList.toggle('on', x.dataset.top === a)); render(); }
    else if (a === 'savemenu') openPop(b, '<button class="ce-mi" data-o="s">Enregistrer</button><button class="ce-mi" data-o="l">Enregistrer et revenir</button><hr><button class="ce-mi" data-o="t">Enregistrer comme modèle…</button>',
      o => { if (o === 't') openTemplateDialog(); else save(o === 'l'); });
  }

  function move(ev) {
    if (!drag) return;
    const e = cur(); if (!e) return;
    const dx = (ev.clientX - drag.sx) / z, dy = (ev.clientY - drag.sy) / z, o = drag.o;
    if (!drag.moved && Math.abs(dx) + Math.abs(dy) < 2) return;
    drag.moved = true; guides = [];
    if (drag.mode === 'rot') {
      let a = Math.atan2(ev.clientY - drag.cy, ev.clientX - drag.cx) * 180 / Math.PI + 90;
      if (ev.shiftKey) a = Math.round(a / 15) * 15;
      else { const n = Math.round(a / 45) * 45; if (Math.abs(a - n) < 4) a = n; }   // s'aimante doucement aux angles droits et à 45°
      e.rot = Math.round(((a % 360) + 540) % 360 - 180);
    } else if (drag.mode === 'm') {
      let x = o.x + dx, y = o.y + dy;
      if (!ev.altKey) {   // Alt : sans aimantation
        // bords et centres de la page et des autres éléments visibles
        const xs = [0, D.w / 2, D.w], ys = [0, D.h / 2, D.h], tol = 8 / z;
        D.els.forEach(t => { if (t.id === e.id || t.hidden) return; xs.push(t.x, t.x + t.w / 2, t.x + t.w); ys.push(t.y, t.y + t.h / 2, t.y + t.h); });
        const snap = (pos, size, lines) => {
          let best = null;
          [0, size / 2, size].forEach(off => lines.forEach(l => { const d = l - (pos + off); if (Math.abs(d) < tol && (!best || Math.abs(d) < Math.abs(best.d))) best = { d, l }; }));
          return best;
        };
        const bx = snap(x, o.w, xs); if (bx) { x += bx.d; guides.push({ v: bx.l }); }
        const by = snap(y, o.h, ys); if (by) { y += by.d; guides.push({ h: by.l }); }
      }
      e.x = Math.round(x); e.y = Math.round(y);
    } else {
      // redimensionnement dans les axes de l'élément (il peut être tourné) ; le côté opposé reste fixe à l'écran
      const d = drag.dir, th = (+o.rot || 0) * Math.PI / 180, cs = Math.cos(th), sn = Math.sin(th);
      const lx = dx * cs + dy * sn, ly = -dx * sn + dy * cs;
      const sx = d.includes('e') ? 1 : d.includes('w') ? -1 : 0, sy = d.includes('s') ? 1 : d.includes('n') ? -1 : 0;
      let ww = Math.max(20, o.w + sx * lx), hh = Math.max(20, o.h + sy * ly);
      if (ev.shiftKey && sx && sy) hh = Math.max(20, ww * o.h / o.w);   // Maj : proportions gardées
      if (textual(e)) hh = o.h;   // texte : hauteur automatique
      const ax = -sx * o.w / 2, ay = -sy * o.h / 2, Ax = o.x + o.w / 2 + ax * cs - ay * sn, Ay = o.y + o.h / 2 + ax * sn + ay * cs;
      const bx = -sx * ww / 2, by = -sy * hh / 2, cx = Ax - (bx * cs - by * sn), cy = Ay - (bx * sn + by * cs);
      Object.assign(e, { x: Math.round(cx - ww / 2), y: Math.round(cy - hh / 2), w: Math.round(ww), h: Math.round(hh) });
    }
    render();
  }

  function keys(ev) {
    if (!root) return;
    if (ev.key === 'Escape') {
      if (!q('#ce-pvw').hidden) return closePreview();
      if (sheet) return closeSheet();
      if (!q('#ce-fmtscrim').hidden) return closeFormat();
      if (!q('#ce-tplscrim').hidden) { q('#ce-tplscrim').hidden = true; return; }
      closePop();
      if (editing) return document.activeElement.blur();
      return select(null);
    }
    if (ev.target.closest && ev.target.closest('input,select,textarea,[contenteditable=true]')) return;
    const key = ev.key.toLowerCase(), mod = ev.ctrlKey || ev.metaKey;
    if (mod && key === 's') { ev.preventDefault(); save(false); return; }
    if (mod && key === 'z') { ev.preventDefault(); if (ev.shiftKey) redo(); else undo(); return; }
    if (mod && key === 'y') { ev.preventDefault(); redo(); return; }
    const e = cur(); if (!e) return;
    if (mod && key === 'd') { ev.preventDefault(); dup(); return; }
    if (ev.key === 'Delete' || ev.key === 'Backspace') { ev.preventDefault(); del(); return; }
    const step = ev.shiftKey ? 10 : 1, mv = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] }[ev.key];
    if (mv && !e.locked) { ev.preventDefault(); e.x += mv[0]; e.y += mv[1]; render(); clearTimeout(nudge); nudge = setTimeout(commit, 400); }
  }

  // ---------- sélection, ajout ----------
  function select(id) {
    const was = cur(); selId = id; const e = cur();
    drawOverlay(); renderLayers(); renderCtx();
    if (e && e.type === 'list') {   // les données de la liste s'ouvrent à gauche (§140)
      const reopen = !panelOpen; if (panelName !== 'donnees' || reopen) { panelName = 'donnees'; panelOpen = true; }
      if (e !== was) { dv.ren = null; renderPanel(); loadSource(e.source); }
      if (reopen) render();
    } else if (was && was.type === 'list' && panelName === 'donnees') { Object.assign(dv, { view: 'set', set: was.source, q: '', ren: null }); renderPanel(); }
  }
  function make(key) {
    const dark = lum(D.bg) < .5, ink = dark ? '#ffffff' : '#0B1F3A', s = k();
    const [kind, v] = key.split(':');
    let e;
    if (kind === 'text') e = v === 'title' ? T({ w: 1000, text: 'Ajouter un titre', size: 120, weight: 800, color: ink })
      : v === 'sub' ? T({ w: 900, text: 'Ajouter un sous-titre', size: 64, weight: 500, color: ink })
      : v === 'body' ? T({ w: 800, text: 'Ajouter un corps de texte. Double-cliquez pour écrire.', size: 40, color: ink, font: 'IBM Plex Sans' })
      : T({ w: 700, text: 'Saisissez ici', size: 72, weight: 700, color: ink, align: 'center' });
    else if (kind === 'icon') e = T({ w: 160, text: v, size: 120, color: ink, align: 'center', font: 'IBM Plex Sans' });
    else if (kind === 'shape') e = v === 'rect' ? R({}) : v === 'round' ? R({ radius: 32 }) : v === 'pill' ? R({ w: 360, h: 90, radius: 45, fill: '#E07B1A' })
      : v === 'circle' ? R({ kind: 'circle', w: 240, h: 240 }) : v === 'line' ? R({ kind: 'line', w: 500, h: 20, sw: 6, fill: ink })
      : v === 'frame' ? R({ fill: D.bg, stroke: ink, sw: 6, radius: 12, w: 420, h: 260 })
      : R({ kind: 'arrow', w: 200, h: 120, fill: ink, rot: +v.replace('arrow', '') || 0 });
    else if (kind === 'img') { const m = images.find(x => x.id === v); const r = m && m.w && m.h ? m.h / m.w : .75; e = I({ media: v, w: 720, h: Math.round(720 * r) }); }
    else if (kind === 'qr') e = Q({});
    else if (kind === 'app') {   // bloc d'une intégration de Mes intégrations (§85)
      const src = sources.find(x => x.id === v) || { app: 'weather-slide' };
      const S = { 'weather-slide': [640, 250, 56], agenda: [900, 400, 40], rss: [900, 340, 38], 'ha-sensors': [620, 300, 40] }[src.app] || [600, 300, 40];
      e = { id: nid(), type: 'app', app: src.app, source: v, x: 0, y: 0, w: S[0], h: S[1], font: 'IBM Plex Sans', size: S[2], weight: 500, color: ink, align: 'left', ls: 0, lh: 1.2, italic: false, bg: 'none', view: 'days', rows: 4, opacity: 1 };
    }
    else if (kind === 'list') {   // liste reliée à un jeu de données (§139) : ses trois premières colonnes, 5 lignes, pages de 8 s
      e = { id: nid(), type: 'list', source: v, x: 0, y: 0, w: 1100, h: 520, font: 'IBM Plex Sans', size: 40, weight: 400, color: ink, align: 'left', ls: 0, lh: 1.2, italic: false,
        rows: 5, page: 'pages', sec: 8, head: true, accent: '#3B82C4', line: dark ? '#23395A' : '#DDE3EA', empty: 'Aucune ligne à afficher', opacity: 1 };
      resetList(e);
      e.name = nextName((sources.find(x => x.id === v) || {}).name || 'Liste');
    }
    else if (kind === 'field') {   // colonne glissée du panneau Données : un texte avec ce champ (clé « source:champ »)
      e = T({ w: 640, text: '{{' + key.slice(6) + '}}', size: 60, weight: 500, color: ink });
    }
    else e = W(v, { color: v === 'ticker' ? '#ffffff' : ink, w: v === 'ticker' ? D.w / s : v === 'date' ? 640 : 420 });
    ['x', 'y', 'w', 'h', 'size', 'radius'].forEach(p => { if (typeof e[p] === 'number') e[p] = Math.round(e[p] * s); });
    if (e.type === 'shape' && e.kind === 'line') e.sw = Math.max(1, Math.round(e.sw * s));
    return e;
  }
  // ---------- liste reliée (§139, §140) ----------
  const OPS = [['eq', 'est'], ['ne', 'n\'est pas'], ['contains', 'contient'], ['gt', 'plus grand que'], ['lt', 'plus petit que'], ['empty', 'est vide'], ['notempty', 'n\'est pas vide']];
  const OPSYM = { eq: '=', ne: '≠', contains: 'contient', gt: '>', lt: '<', empty: 'vide', notempty: 'non vide' };
  const ALIGNS = [['left', 'À gauche'], ['center', 'Au centre'], ['right', 'À droite']];
  const opt = (list, v) => list.map(([a, b]) => `<option value="${esc(a)}" ${String(a) === String(v == null ? '' : v) ? 'selected' : ''}>${esc(b)}</option>`).join('');
  /** Colonnes d'un jeu : celles des données lues (sinon connues du serveur), avec le nom lisible et le masquage donnés au jeu. */
  function colsOf(id) {
    const s = sources.find(x => x.id === id) || {}, known = s.cols || [], d = srcData[id];
    return ((d && !d.error && d.columns) || known).map(c => {
      const kn = known.find(x => x.key === c.key) || {};
      return { key: c.key, label: c.label || kn.label || c.key, type: c.type || kn.type || 'text', name: kn.name || '', hidden: !!kn.hidden };
    });
  }
  const segs = c => String(c.label || c.key).split('.').filter(Boolean);
  const technical = s => /[._]|[a-z][A-Z]/.test(s || '');
  /** « LastStartedAt » → « Last started at », « next_run » → « Next run » ; les sigles restent en capitales. */
  const humanize = s => String(s || '').replace(/_/g, ' ').replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2').trim().split(/\s+/)
    .map((x, i) => /^[A-Z0-9]{2,}$/.test(x) ? x : i ? x.toLowerCase() : x.charAt(0).toUpperCase() + x.slice(1).toLowerCase()).join(' ');
  /** Nom lisible d'un champ : celui donné au jeu, sinon son nom d'origine rendu lisible (« Health.LastStartedAt » → « Last started at »). */
  const fname = c => !c ? '' : c.name || (technical(c.label) ? humanize(segs(c).pop() || c.key) : c.label || c.key);
  /** Groupe d'un champ : son préfixe (« Config.CustomSettings »), vide s'il n'en a pas. */
  const fgroup = c => segs(c).slice(0, -1).join('.');
  const keyName = (id, key) => fname(colsOf(id).find(c => c.key === key)) || key;
  /** Lignes gardées par une liste (filtre, tri), comme sur l'écran. */
  const keptRows = e => { const d = srcData[e.source]; return d && !d.error && d.columns ? LC().tableRows(d, e, opts()) : []; };
  function colValues(e, key, rows) {
    const d = srcData[e.source], i = d && d.columns ? d.columns.findIndex(c => c.key === key) : -1, t = (colsOf(e.source).find(c => c.key === key) || {}).type;
    return i < 0 ? [] : (rows || keptRows(e)).map(r => LC().fmt(r[i], t));
  }
  function resetList(e) {
    const cs = colsOf(e.source).filter(c => !c.hidden && !autoHidden(e.source, c)).slice(0, 3);
    e.cols = cs.map((c, i) => boolColors({ key: c.key, label: fname(c), as: 'text', align: i && (c.type === 'number' || c.type === 'date') ? 'right' : 'left', w: i ? 1 : 2 }, c));
    e.sort = null; e.filter = []; e.rules = [];
  }
  /** Colonne Oui / Non : vert et rouge proposés (modifiables dans Couleurs). */
  function boolColors(col, c) { if (c && c.type === 'bool') { if (!col.yc) col.yc = '#1E8E5A'; if (!col.nc) col.nc = '#C8372D'; } return col; }
  /** Nom suivant libre pour une liste (« Plugins » → « Plugins 2 »). */
  function nextName(base) {
    const used = D.els.filter(x => x.type === 'list').map(x => x.name), b = String(base || 'Liste').replace(/ \d+$/, '');
    if (!used.includes(b)) return b;
    let n = 2; while (used.includes(b + ' ' + n)) n++;
    return b + ' ' + n;
  }
  const filterText = e => (e.filter || []).filter(f => f && f.key).map(f => keyName(e.source, f.key) + ' ' + (OPSYM[f.op] || '=') +
    (f.op === 'empty' || f.op === 'notempty' ? '' : ' ' + (String(f.val || '').charAt(0) === '@' ? 'cet écran' : f.val || '…'))).join(', ');
  /** Fenêtre d'apparence de la liste sélectionnée (barre du haut) : lignes à la fois, colonnes (style, alignement, largeur), couleurs et règles. Reste ouverte pendant les changements. */
  function listPop(which) {
    const e = cur(), anchor = q('[data-act=l' + which + ']'); if (!e || e.type !== 'list' || !anchor) return;
    const cs = colsOf(e.source), cOpts = v => opt(cs.map(c => [c.key, fname(c)]), v), type = key => (cs.find(c => c.key === key) || {}).type;
    const title = c => esc(c.label || keyName(e.source, c.key));
    let h = '';
    if (which === 'rows') {
      h = `<div class="ce-pt">Lignes à l'écran</div>
        <div class="ce-lrow"><span class="ce-lk">À la fois</span><select data-p="rows" data-t="n" aria-label="Lignes à la fois">${opt([3, 4, 5, 6, 8, 10, 12, 15].map(n => [n, n + ' lignes']), e.rows)}</select></div>
        <div class="ce-lrow"><span class="ce-lk">S'il y en a plus</span><select data-p="page" aria-label="S'il y a plus de lignes">${opt([['pages', 'Pages qui alternent'], ['scroll', 'Défilement continu'], ['cut', 'Couper']], e.page || 'pages')}</select></div>` +
        (e.page === 'cut' ? '' : `<div class="ce-lrow"><span class="ce-lk">${e.page === 'scroll' ? 'Vitesse' : 'Durée d\'une page'}</span><select data-p="sec" data-t="n" aria-label="Durée">${opt(e.page === 'scroll' ? [[2, 'Rapide'], [4, 'Normal'], [8, 'Lent']] : [[5, '5 s'], [8, '8 s'], [12, '12 s'], [20, '20 s'], [30, '30 s']], e.sec)}</select></div>`);
    } else if (which === 'cols') {
      h = '<div class="ce-pt">Apparence de chaque colonne. Le champ, le titre et les valeurs se règlent dans le panneau Données, à gauche.</div>' + (e.cols || []).map((c, i) => {
        const b = type(c.key) === 'bool';
        return `<div class="ce-lrow" data-i="${i}"><span class="ce-lk" title="${title(c)}">${title(c)}</span>
          <select data-f="as" aria-label="Style">${opt(b ? [['text', 'Texte coloré'], ['pill', 'Pastille'], ['small', 'Sous la précédente']] : [['text', 'Texte'], ['small', 'Sous la précédente'], ['pill', 'Pastille'], ['image', 'Image']], c.as || 'text')}</select>
          <select data-f="align" aria-label="Alignement">${opt(ALIGNS, c.align || 'left')}</select>
          <select data-f="w" data-t="n" title="Largeur relative" aria-label="Largeur">${opt([[1, 'Étroite'], [2, 'Moyenne'], [3, 'Large'], [4, 'Très large']], c.w || 1)}</select></div>`;
      }).join('') + '<div class="ce-pt">« Sous la précédente » : en petit sous la colonne de gauche. Une pastille ou une image prend sa propre largeur.</div>';
    } else {
      const bools = (e.cols || []).map((c, i) => [c, i]).filter(([c]) => type(c.key) === 'bool');
      h = `<div class="ce-pt">Couleurs de la liste</div>
        <div class="ce-lrow"><span class="ce-lk">Pastilles</span><input type="color" data-p="accent" value="${esc(e.accent || '#3B82C4')}" aria-label="Couleur des pastilles"></div>
        <div class="ce-lrow"><span class="ce-lk">Séparateurs</span><input type="color" data-p="line" value="${esc(e.line || '#DDE3EA')}" aria-label="Couleur des séparateurs"></div>` +
        bools.map(([c, i]) => `<div class="ce-lrow" data-i="${i}"><span class="ce-lk">${title(c)} · « ${esc(c.yes || 'Oui')} »</span><input type="color" data-f="yc" value="${esc(c.yc || '#1E8E5A')}" aria-label="Couleur de ${esc(c.yes || 'Oui')}">
          <span class="ce-lk">« ${esc(c.no || 'Non')} »</span><input type="color" data-f="nc" value="${esc(c.nc || '#C8372D')}" aria-label="Couleur de ${esc(c.no || 'Non')}"></div>`).join('') +
        '<div class="ce-pt">Règles : couleur des pastilles (ou du texte de la ligne) quand… La première vraie l\'emporte.</div>' +
        (e.rules || []).map((f, i) => {
          const noVal = f.op === 'empty' || f.op === 'notempty';
          return `<div class="ce-lrow" data-r="${i}"><select data-g="key" aria-label="Champ">${cOpts(f.key)}</select><select data-g="op" aria-label="Condition">${opt(OPS, f.op || 'eq')}</select>` +
            (noVal ? '' : `<input data-g="val" value="${esc(f.val || '')}" placeholder="valeur" aria-label="Valeur">`) +
            `<input type="color" data-g="color" value="${esc(f.color || '#C8372D')}" aria-label="Couleur"><button class="ce-tb" data-o="del:${i}" data-keep="1" title="Retirer">×</button></div>`;
        }).join('') + (cs.length ? '<button class="ce-mi" data-o="add" data-keep="1">+ Ajouter une règle</button>' : '');
    }
    const done = () => { commit(); renderCtx(); listPop(which); };
    openPop(anchor, h, o => {
      const [op, i] = String(o).split(':'); e.rules = e.rules || [];
      if (op === 'add') e.rules.push({ key: cs[0].key, op: 'eq', val: '', color: '#C8372D' });
      if (op === 'del') e.rules.splice(+i, 1);
      done();
    });
    const p = q('#ce-pop'); p.classList.add('ce-lpop');
    const apply = t => {
      let v = t.value; if (t.dataset.t === 'n') v = +v;
      if (t.dataset.p) { e[t.dataset.p] = v; return; }
      const rr = t.closest('[data-r]'), row = t.closest('[data-i]');
      if (rr && t.dataset.g) { const it = e.rules[+rr.dataset.r]; if (it) it[t.dataset.g] = v; return; }
      if (row && t.dataset.f) { const c = e.cols[+row.dataset.i]; if (c) c[t.dataset.f] = v; }
    };
    p.addEventListener('input', ev => { if (ev.target.type === 'color') { apply(ev.target); render(); } });
    p.addEventListener('change', ev => { apply(ev.target); done(); });
  }
  /** Titre d'une colonne modifié sur la page : double-clic sur l'en-tête de la liste. */
  function renameHead(e, lh) {
    const c = (e.cols || [])[+lh.dataset.lh]; if (!c) return;
    editing = true; lh.contentEditable = 'true'; lh.classList.add('ce-lhedit'); lh.focus();
    const r = document.createRange(); r.selectNodeContents(lh); const s = getSelection(); s.removeAllRanges(); s.addRange(r);
    drawOverlay();
    lh.addEventListener('keydown', ev => { if (ev.key === 'Enter') { ev.preventDefault(); lh.blur(); } if (ev.key === 'Escape') { ev.stopPropagation(); lh.textContent = c.label || ''; lh.blur(); } });
    lh.addEventListener('blur', () => { const v = lh.textContent.trim().slice(0, 60); editing = false; if (v) c.label = v; commit(); renderCtx(); if (panelName === 'donnees') renderPanel(); }, { once: true });
  }
  /** Liste dupliquée à côté (sinon dessous), sous un nouveau nom : même source, à filtrer autrement. */
  function dupList(e) {
    const c = Object.assign(JSON.parse(JSON.stringify(e)), { id: nid(), locked: false, name: nextName(e.name || 'Liste') }), gap = Math.round(40 * k());
    if (e.x + e.w * 2 + gap <= D.w) c.x = e.x + e.w + gap;   // à droite s'il y a la place, sinon dessous, sinon un peu décalée
    else if (e.y + e.h * 2 + gap <= D.h) c.y = e.y + e.h + gap;
    else { c.x = Math.min(D.w - e.w, e.x + gap); c.y = Math.min(D.h - e.h, e.y + gap); }
    D.els.splice(D.els.indexOf(e) + 1, 0, c); selId = c.id; commit(); renderCtx(); renderPanel();
    toast('Liste dupliquée : donnez-lui un nom et choisissez ses lignes gardées.');
  }

  function addEl(key, at) {
    if (key.startsWith('img:') && mediaMode !== 'add') {
      const id = key.slice(4), e = cur();
      if (mediaMode === 'replace' && e && e.type === 'image') { e.media = id; mediaMode = 'add'; commit(); renderPanel(); renderCtx(); toast('Image remplacée'); return; }
      if (mediaMode === 'bg') {
        const b = I({ media: id, x: 0, y: 0, w: D.w, h: D.h, locked: true });
        D.els.unshift(b); selId = b.id; mediaMode = 'add'; commit(); renderPanel(); renderCtx(); toast('Image de fond ajoutée (calque verrouillé, tout en bas)'); return;
      }
    }
    const e = make(key);
    D.els.push(e);
    const cx = at ? at.x : D.w / 2, cy = at ? at.y : D.h / 2;
    e.x = Math.round(cx - e.w / 2); e.y = Math.round(cy - e.h / 2);
    if (e.kind === 'ticker') { e.x = 0; e.y = D.h - e.h; }
    selId = e.id;
    render();
    if (textual(e)) { e.y = Math.round(cy - e.h / 2); }
    commit(); renderCtx();
    if (e.type === 'qr') fetchQr(e);
    if (e.type === 'list') { panelName = 'donnees'; panelOpen = true; renderPanel(); }   // ses données s'ouvrent à gauche (§140)
    LC().sources({ els: [e] }).forEach(id => loadSource(id));   // bloc, liste reliée, champ glissé depuis le panneau Données
  }
  function lum(hex) {
    const h = String(hex || '#ffffff').replace('#', ''), n = parseInt(h.length === 3 ? h.split('').map(c => c + c).join('') : h.slice(0, 6), 16) || 0;
    return (.299 * (n >> 16 & 255) + .587 * (n >> 8 & 255) + .114 * (n & 255)) / 255;
  }
  // QR code : tracé calculé par le serveur (le même que sur l'écran), demandé après une courte pause de saisie
  function fetchQr(e, delay) {
    clearTimeout(qrWait[e.id]);
    qrWait[e.id] = setTimeout(async () => {
      if (!net || !e.url) return;
      let r = null;
      try { r = await net.invokeMethodAsync('Qr', e.url); } catch (x) { return; }
      if (!root || !byId(e.id)) return;
      e.qr = r || '';
      render();
      H[hi] = JSON.stringify(D);   // pas une étape d'annulation : on complète l'état courant
      if (!isDirty()) savedDoc = H[hi];
      setDirty();
    }, delay || 0);
  }

  // ---------- sources de données (§85) ----------
  async function loadSource(id, force) {
    if (!net || !id || srcBusy[id] || (!force && srcData[id])) return;
    srcBusy[id] = true;
    if (panelName === 'donnees' && panelOpen) renderPanel();
    let r = null;
    try { r = await net.invokeMethodAsync('SourceData', id === LC().SCREEN && previewScreen ? id + '@' + previewScreen : id); } catch (x) { r = { error: 'la connexion au serveur est interrompue' }; }
    if (!root) return;
    srcBusy[id] = false; srcAt[id] = Date.now();
    srcData[id] = r && r.data ? r.data : { error: (r && r.error) || 'aucune donnée' };
    if (!editing) render();
    if (panelName === 'donnees' && panelOpen) renderPanel();
    if (q('#ce-pop.ce-fields')) drawFieldMenu();
    if (sheet && sheet.set === id) drawSheet();
  }
  // Texte en cours d'écriture -> texte du document : les pastilles redeviennent « {{source:clé}} »
  function serialize(n) {
    let s = '';
    n.childNodes.forEach(c => {
      if (c.nodeType === 3) s += c.nodeValue.replace(/\u00a0/g, ' ');
      else if (c.nodeName === 'BR') s += '\n';
      else if (c.classList && c.classList.contains('lc-fd')) s += '{{' + c.dataset.f + '}}';
      else if (c.nodeType === 1) { if (/^(DIV|P)$/.test(c.nodeName) && s && !s.endsWith('\n')) s += '\n'; s += serialize(c); }
    });
    return s;
  }
  // Liste des champs de toutes les sources, avec leur valeur actuelle ; un clic insère le champ au curseur (ou en fin de texte)
  function fieldMenu(anchor) { fieldAnchor = anchor; sources.forEach(s => loadSource(s.id)); drawFieldMenu(); }
  function drawFieldMenu() {
    const h = sources.length ? sources.map(s => {
      const m = LC().SRC[s.app] || {}, d = srcData[s.id], f = LC().fields(s.app, d);
      return `<div class="ce-pt"><b>${m.icon || ''} ${esc(s.name)}</b></div>` + (d && d.error ? `<div class="ce-pt">Données indisponibles : ${esc(d.error)}</div>`
        : !f.length ? `<div class="ce-pt">${d ? 'Aucun champ' : 'Lecture des données…'}</div>`
        : f.map(x => `<button class="ce-mi" data-o="${s.id}:${esc(x.key)}">${esc(x.label)}<small>${d ? esc(LC().fieldText(s.id, x.key, opts({ names: false }), '—')) : ''}</small></button>`).join(''));
    }).join('<hr>') : '<div class="ce-pt">Aucune source : ajoutez un jeu de données (Sources de données), une Météo, un Calendrier partagé, un Flux RSS ou des Capteurs Home Assistant.</div>';
    openPop(fieldAnchor, h, o => insertField(o));
    const p = q('#ce-pop'); p.classList.add('ce-fields');
    p.addEventListener('mousedown', ev => ev.preventDefault());   // le texte en cours d'écriture garde le curseur
  }
  function insertField(f) {
    const e = cur(); if (!e || e.type !== 'text') return;
    const tx = editing ? root.querySelector('.lc-tx[contenteditable=true]') : null;
    if (!tx) { e.text = (e.text || '') + (!e.text || /\s$/.test(e.text) ? '' : ' ') + '{{' + f + '}}'; commit(); renderCtx(); return; }
    const i = f.indexOf(':'), span = document.createElement('span');
    span.className = 'lc-fd'; span.contentEditable = 'false'; span.dataset.f = f;
    span.textContent = LC().fieldText(f.slice(0, i), f.slice(i + 1), opts(), e.fb);
    const sel = getSelection();
    let r = sel.rangeCount && tx.contains(sel.getRangeAt(0).startContainer) ? sel.getRangeAt(0) : null;
    if (r && !r.collapsed && r.toString() === tx.textContent) r.collapse(false);   // tout le texte sélectionné (double-clic) : on ajoute à la fin, sans effacer
    if (!r) { r = document.createRange(); r.selectNodeContents(tx); r.collapse(false); }
    r.deleteContents(); r.insertNode(span); r.setStartAfter(span); r.collapse(true); sel.removeAllRanges(); sel.addRange(r);
    e.h = tx.closest('.lc-el').offsetHeight; drawOverlay();
  }
  // ---------- panneau Données (§140) : sources, un jeu ouvert (champs groupés et valeurs), recherche ; données de la liste sélectionnée ----------
  const DI = {
    pen: '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 20h4L19 9l-4-4L4 16z"/></svg>',
    hide: '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2"><path d="M3 3l18 18M10.6 5.1A10 10 0 0 1 12 5c6.5 0 10 7 10 7a17 17 0 0 1-3.2 4.1M6.6 6.6C3.8 8.4 2 12 2 12s3.5 7 10 7c1.9 0 3.5-.6 4.9-1.4"/></svg>',
    show: '<svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/></svg>',
    big: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2"><path d="M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7"/></svg>'
  };
  const TYI = { text: 'Aa', number: '12', date: '◷', bool: '✓', image: '▣', link: '↗' };
  const GUIDV = /^\{?[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}\}?$/i;
  /** Champ masqué d'office : vide sur toutes les lignes, ou identifiant technique (GUID) ; '' sinon. */
  function autoHidden(id, c) {
    const d = srcData[id]; if (!d || d.error || !d.columns || !(d.rows || []).length) return '';
    const i = d.columns.findIndex(x => x.key === c.key); if (i < 0) return '';
    const vs = d.rows.map(r => r[i] == null ? '' : String(r[i]).trim());
    if (vs.every(v => v === '')) return 'vide sur toutes les lignes';
    if (vs.every(v => v === '' || GUIDV.test(v))) return 'identifiant technique';
    return '';
  }
  const hiddenWhy = (id, c) => c.hidden ? 'masqué' : autoHidden(id, c);
  const at = id => srcAt[id] ? new Date(srcAt[id]).toLocaleTimeString('fr-CH', { hour: '2-digit', minute: '2-digit' }) : '';
  const srcState = s => { const d = srcData[s.id]; return srcBusy[s.id] ? 'lecture…' : d && d.error ? d.error : d ? (d.readAt ? 'copie du ' + new Date(d.readAt).toLocaleString('fr-CH', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }) : 'à jour, lue à ' + at(s.id)) : 'pas encore lue'; };
  const cellRaw = (id, c, r) => { const d = srcData[id], i = d && d.columns ? d.columns.findIndex(x => x.key === c.key) : -1; return r && i >= 0 ? r[i] : null; };
  function valHtml(id, c, r) {
    const v = cellRaw(id, c, r);
    if (v == null || v === '') return '<span class="ce-fe">vide</span>';
    if (c.type === 'bool' && (v === 'true' || v === 'false')) return `<span class="ce-yn ${v === 'true' ? 'y' : 'n'}">${v === 'true' ? 'Oui' : 'Non'}</span>`;
    return esc(LC().fmt(v, c.type));
  }
  const usesOf = id => D.els.filter(e => e.type === 'list' && e.source === id);
  /** Champs d'un jeu utilisés par la page : colonnes, filtres, tri et règles de ses listes, champs des textes. */
  function usedKeys(id) {
    const ks = new Set();
    usesOf(id).forEach(e => { (e.cols || []).concat(e.filter || [], e.rules || []).forEach(x => { if (x && x.key) ks.add(x.key); }); if (e.sort && e.sort.key) ks.add(e.sort.key); });
    D.els.forEach(e => { if (e.type !== 'text') return; const re = new RegExp(LC().TOKEN.source, 'g'); let m; while ((m = re.exec(String(e.text || '')))) if (m[1] === id) ks.add(m[2].replace(/^(r\d+|sum|avg|min|max)\./, '')); });
    return ks;
  }
  function groupsOf(cs) { const g = new Map(); cs.forEach(c => { const k = fgroup(c); if (!g.has(k)) g.set(k, []); g.get(k).push(c); }); return g; }
  const gTitle = k => { if (!k) return '<b>Champs</b>'; const p = k.split('.'); return (p.length > 1 ? `<span class="path">${esc(p.slice(0, -1).map(humanize).join(' › '))} › </span>` : '') + `<b>${esc(humanize(p[p.length - 1]))}</b>`; };
  const searchBox = ph => `<label class="ce-search ce-dq">⌕<input id="ce-dq" placeholder="${ph}" value="${esc(dv.q)}" autocomplete="off" aria-label="${ph}">${dv.q ? '<button class="ce-x" data-dclear="1" title="Effacer la recherche" aria-label="Effacer la recherche">×</button>' : ''}</label>`;
  /** Un champ : nom lisible, valeur entière de la ligne choisie, nom d'origine au survol ; glissé ou cliqué, il se pose sur la page. */
  function fieldRow(id, c, r, o = {}) {
    const hid = hiddenWhy(id, c), ren = dv.ren === id + ':' + c.key;
    const nm = ren ? `<input class="ce-fren" id="ce-fren" value="${esc(fname(c))}" maxlength="60" aria-label="Nom lisible du champ">`
      : `<span>${o.hl ? o.hl(fname(c)) : esc(fname(c))}</span>${c.name ? '<i class="ce-ftag">renommé</i>' : ''}${hid ? `<i class="ce-ftag off">${esc(hid)}</i>` : ''}`;
    return `<div class="ce-fr${hid ? ' hid' : ''}" draggable="true" data-add="field:${id}:${esc(c.key)}" title="Glissez sur la page, ou cliquez : ajouté au texte sélectionné, sinon dans un nouveau texte">
      <em>${TYI[c.type] || 'Aa'}</em><div class="fx"><div class="fl">${nm}</div><div class="fv">${o.val != null ? o.val : valHtml(id, c, r)}</div><div class="fk">${o.hl ? o.hl(c.label) : esc(c.label)}</div></div>` +
      (canEditData && !ren && id !== LC().SCREEN ? `<span class="fa"><button class="ce-ib" data-fren="${id}:${esc(c.key)}" title="Nom lisible, proposé dans toutes les compositions" aria-label="Renommer ${esc(fname(c))}">${DI.pen}</button>` +
        `<button class="ce-ib" data-fhide="${id}:${esc(c.key)}" title="${c.hidden ? 'Afficher' : 'Masquer'} ce champ dans toutes les compositions" aria-label="${c.hidden ? 'Afficher' : 'Masquer'} ${esc(fname(c))}">${c.hidden ? DI.show : DI.hide}</button></span>` : '') + '</div>';
  }
  const dsCard = s => {
    const d = srcData[s.id], ls = usesOf(s.id), nf = usedKeys(s.id).size;
    const use = ls.length ? `Sur la page : ${ls.length} liste${ls.length > 1 ? 's' : ''} (${ls.map(x => esc(x.name || 'sans nom')).join(', ')})` : nf ? `Sur la page : ${nf} champ${nf > 1 ? 's' : ''}` : '';
    return `<button class="ce-dsrc" data-dset="${s.id}">${wi(LC().SRC.dataset)}<span class="t"><b><span>${esc(s.name)}</span><i class="dot ${d && d.error ? 'bad' : d ? 'ok' : ''}"></i></b>
      <small>${d && d.rows ? d.rows.length + ' lignes · ' + (d.columns || []).length + ' champs · ' : ''}${esc(srcState(s))}</small>${use ? `<span class="use">${use}</span>` : ''}</span><span class="chev">›</span></button>`;
  };
  const appCard = s => {
    const m = LC().SRC[s.app] || {}, n = D.els.filter(e => e.type === 'app' && e.source === s.id).length;
    return `<div class="ce-src"><div class="h">${wi(m)}<b>${esc(s.name)}</b><span class="dot ${srcData[s.id] && srcData[s.id].error ? 'bad' : srcData[s.id] ? 'ok' : ''}"></span></div>
      <div class="meta">${esc(m.name)} · ${esc(srcState(s))}</div><div class="uses">${n ? `<span>${n} bloc${n > 1 ? 's' : ''} sur la page</span>` : ''}<button class="ce-link" data-add="app:${s.id}">+ Poser un bloc</button></div></div>`;
  };
  function dataPanel() {
    const e = cur();
    if (e && e.type === 'list') return listPanel(e);
    if (dv.q.trim()) return searchPanel();
    if (dv.view === 'set' && dv.set === LC().SCREEN && sources.some(s => s.id === dv.set)) return screenPanel();
    if (dv.view === 'set' && sources.some(s => s.id === dv.set && s.app === 'dataset')) return setPanel(dv.set);
    dv.view = 'sources';
    return sourcesPanel();
  }
  function sourcesPanel() {
    const used = LC().sources(D), sets = sources.filter(s => s.app === 'dataset'), scr = sources.find(s => s.app === 'screen'), apps = sources.filter(s => s.app !== 'dataset' && s.app !== 'screen');
    const missing = used.filter(id => id !== LC().SCREEN && !sources.some(s => s.id === id)).length;
    let h = `<div class="ce-ph"><b>Données</b><button class="ce-ib" data-srclist="1" title="Actualiser la liste des sources" aria-label="Actualiser la liste des sources">↻</button></div>` +
      searchBox('Chercher un champ ou une valeur…') +
      '<p class="ce-help" style="margin:0">Ouvrez une source pour voir ses champs et leurs valeurs, puis glissez-les sur la page. Sur l\'écran, tout se met à jour seul ; sans connexion, l\'écran garde la dernière copie reçue.</p>';
    h += `<div class="ce-grp">Jeux de données${sets.length ? `<small>${sets.length}</small>` : ''}</div>` + (sets.length ? sets.map(dsCard).join('')
      : '<div class="ce-empty">Aucun jeu de données.<br>Branchez une API, une base ou un fichier dans <a href="/datasources" target="_blank" rel="noopener">Sources de données</a> (plan Pro).</div>');
    if (scr) h += `<div class="ce-grp">Cet écran</div><button class="ce-dsrc" data-dset="${scr.id}">${wi(LC().SRC.screen)}<span class="t"><b><span>Valeurs de l'écran</span></b><small>Nom, zone, paramètres… de l'écran qui affiche la composition</small></span><span class="chev">›</span></button>`;
    if (apps.length) h += `<div class="ce-grp">Intégrations</div>${apps.map(appCard).join('')}`;
    if (missing) h += `<div class="ce-note">${missing} source${missing > 1 ? 's' : ''} introuvable${missing > 1 ? 's' : ''} (supprimée ou d'une autre aire) : ses blocs et listes ne sont pas diffusés, ses champs affichent leur valeur de secours.</div>`;
    return h + `<div class="ce-row" style="margin-top:14px"><a class="ce-btn ghost" href="/datasources" target="_blank" rel="noopener">+ Ajouter une source</a></div>
      <p class="ce-help">Intégrations : contenus de Mes intégrations (Météo, Calendrier partagé, Flux RSS, Capteurs). Jeux de données : vos API, bases et fichiers. Les modifier change toutes les compositions qui les utilisent.</p>`;
  }
  function setPanel(id) {
    const s = sources.find(x => x.id === id), d = srcData[id], cs = colsOf(id), rows = (d && !d.error && d.rows) || [];
    if (dv.row >= rows.length) dv.row = 0;
    const r = rows[dv.row], shown = c => !hiddenWhy(id, c), vis = cs.filter(c => dv.hidden || shown(c)), nh = cs.filter(c => !shown(c)).length;
    const ls = usesOf(id), uk = usedKeys(id), used = cs.filter(c => uk.has(c.key));
    const tc = cs.find(c => c.type === 'text' && shown(c) && cellRaw(id, c, r)), rowName = tc ? LC().fmt(cellRaw(id, tc, r), 'text') : '';
    let h = `<button class="ce-crumb" data-dback="1">‹ Toutes les sources</button>
      <div class="ce-sethead">${wi(LC().SRC.dataset)}<div class="t"><b>${esc(s.name)} <i class="dot ${d && d.error ? 'bad' : d ? 'ok' : ''}"></i></b><small>${d && !d.error ? rows.length + ' lignes · ' + cs.length + ' champs · ' : ''}${esc(srcState(s))}</small></div>
      <button class="ce-ib" data-srcref="${id}" title="Relire les données" aria-label="Relire les données">↻</button></div>`;
    if (!d) return h + '<div class="ce-empty">Lecture des données…</div>';
    if (d.error) return h + `<div class="ce-note">Données indisponibles : ${esc(d.error)}</div>`;
    h += `<div class="ce-tiles"><button class="ce-tile" draggable="true" data-add="list:${id}" title="Glissez sur la page, ou cliquez : une liste de plus"><span class="g">▦</span><span><b>Liste des lignes</b><small>les ${rows.length} lignes</small></span></button>
      <button class="ce-tile" draggable="true" data-add="field:${id}:_count" title="Glissez sur la page, ou cliquez"><span class="g">${rows.length}</span><span><b>Nombre de lignes</b><small>un chiffre</small></span></button></div>` +
      searchBox('Chercher dans ce jeu…');
    if (rows.length) h += `<div class="ce-rownav"><span>Valeurs de la ligne</span><span class="pick"><button data-drow="-1" ${dv.row ? '' : 'disabled'} aria-label="Ligne précédente">‹</button>
      <span title="${esc(rowName)}"><em>${dv.row + 1}/${rows.length}</em>${esc(rowName)}</span><button data-drow="1" ${dv.row < rows.length - 1 ? '' : 'disabled'} aria-label="Ligne suivante">›</button></span>
      <button class="ce-ib" data-sheet="${id}" title="Voir toutes les lignes en tableau" aria-label="Voir toutes les lignes en tableau">${DI.big}</button></div>`;
    if (ls.length || used.length) {
      const op = dv.open.has('@used');
      h += `<div class="ce-fg used${op ? ' open' : ''}"><div class="fgh" data-dg="@used"><span class="cv">›</span><b>Sur la page</b><span class="n">${ls.length ? ls.length + ' liste' + (ls.length > 1 ? 's' : '') + ' · ' : ''}${used.length} champ${used.length > 1 ? 's' : ''}</span></div><div class="fgb">` +
        (op ? ls.map(e => `<button class="ce-lst" data-sel="${e.id}"><span class="g">▦</span><span class="t"><b>${esc(e.name || 'Liste sans nom')}</b><small>${esc(filterText(e) || 'toutes les lignes')} · ${keptRows(e).length} lignes · ${(e.cols || []).length} colonnes</small></span><span class="chev">›</span></button>`).join('') +
          used.map(c => fieldRow(id, c, r)).join('') : '') + '</div></div>';
    }
    const g = groupsOf(vis);
    g.forEach((list, key) => {
      const op = g.size === 1 || dv.open.has(key);
      h += `<div class="ce-fg${op ? ' open' : ''}"><div class="fgh" data-dg="${esc(key)}"><span class="cv">›</span>${gTitle(key)}<span class="n">${list.length}</span></div><div class="fgb">${op ? list.map(c => fieldRow(id, c, r)).join('') : ''}</div></div>`;
    });
    if (nh) h += `<button class="ce-hidlink" data-dhid="1">${dv.hidden ? 'Cacher' : 'Afficher'} ${nh > 1 ? 'les ' + nh + ' champs masqués' : 'le champ masqué'} (vides, identifiants techniques ou masqués)</button>`;
    return h + `<p class="ce-help">Glissez un champ sur la page, ou cliquez dessus : il s'ajoute au texte sélectionné, sinon dans un nouveau texte (valeur de la 1re ligne).${canEditData ? ' Survolez un champ : ✎ lui donne un nom lisible, proposé ensuite partout ; les titres déjà donnés aux listes ne changent pas.' : ''}</p>`;
  }
  function screenPanel() {
    const scr = sources.find(s => s.id === LC().SCREEN), d = srcData[scr.id];
    const fs = d && !d.error ? LC().fields('screen', d) : (scr.cols || []).map(c => ({ key: c.key, label: c.label }));
    return `<button class="ce-crumb" data-dback="1">‹ Toutes les sources</button>
      <div class="ce-sethead">${wi(LC().SRC.screen)}<div class="t"><b>Valeurs de l'écran</b><small>Celles de l'écran qui affiche la composition (Écrans › Paramètres de l'écran)</small></div></div>
      ${(scr.screens || []).length ? `<label class="ce-lbl ce-scr" style="margin:12px 0 4px">Aperçu avec<select id="ce-scrsel">${['<option value="">le premier écran qui a des paramètres</option>'].concat(scr.screens.map(x => `<option value="${esc(x.id)}" ${x.id === previewScreen ? 'selected' : ''}>${esc(x.name)}${x.has ? '' : ' (sans paramètre)'}</option>`)).join('')}</select></label>` : ''}
      <div class="ce-fg open"><div class="fgh"><span class="cv">›</span><b>Champs</b><span class="n">${fs.length}</span></div><div class="fgb">` +
      fs.map(f => fieldRow(scr.id, { key: f.key, label: f.label, type: 'text', name: '' }, null, { val: d && !d.error ? esc(LC().fieldText(scr.id, f.key, opts({ names: false }), '')) || '<span class="ce-fe">vide</span>' : '' })).join('') + '</div></div>';
  }
  function searchPanel() {
    const t = dv.q.trim().toLowerCase(), re = new RegExp('(' + t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + ')', 'ig'), hl = s => esc(s).replace(re, '<mark>$1</mark>');
    const inSet = dv.view === 'set' && sources.some(s => s.id === dv.set && s.app === 'dataset');
    const scope = inSet ? sources.filter(s => s.id === dv.set) : sources.filter(s => s.app === 'dataset');
    let h = (inSet ? '<button class="ce-crumb" data-dback="1">‹ Toutes les sources</button>' : '<div class="ce-ph"><b>Données</b></div>') + searchBox(inSet ? 'Chercher dans ce jeu…' : 'Chercher un champ ou une valeur…'), body = '';
    scope.forEach(s => {
      const d = srcData[s.id]; if (!d || d.error || !d.columns) return;
      const hits = [];
      colsOf(s.id).forEach(c => {
        if (!dv.hidden && hiddenWhy(s.id, c)) return;
        const i = d.columns.findIndex(x => x.key === c.key), inName = (fname(c) + ' ' + c.label).toLowerCase().includes(t);
        const ri = (d.rows || []).findIndex(r => LC().fmt(r[i], c.type).toLowerCase().includes(t));
        if (inName || ri >= 0) hits.push({ c, ri: ri >= 0 ? ri : 0, inVal: ri >= 0 });
      });
      if (!hits.length) return;
      body += `<div class="ce-rsrc" data-dset="${s.id}">${wi(LC().SRC.dataset)}<b>${esc(s.name)}</b><span>· ${hits.length} résultat${hits.length > 1 ? 's' : ''}</span></div>`;
      groupsOf(hits.map(x => x.c)).forEach((list, key) => {
        body += `<div class="ce-fg open"><div class="fgh"><span class="cv">›</span>${gTitle(key)}<span class="n">${list.length}</span></div><div class="fgb">` + list.map(c => {
          const x = hits.find(y => y.c === c), r = d.rows[x.ri];
          const v = c.type === 'bool' ? valHtml(s.id, c, r) : (x.inVal && x.ri ? `<small class="where">ligne ${x.ri + 1}</small>` : '') + (hl(LC().fmt(cellRaw(s.id, c, r), c.type) || '') || '<span class="ce-fe">vide</span>');
          return fieldRow(s.id, c, r, { hl, val: v });
        }).join('') + '</div></div>';
      });
    });
    const waiting = scope.some(s => !srcData[s.id] || srcBusy[s.id]);
    return h + (body || `<div class="ce-empty">Aucun champ ni valeur ne contient « ${esc(dv.q.trim())} ».${waiting ? '<br>Des sources sont encore en lecture.' : ''}</div>`) +
      '<p class="ce-help">La recherche porte sur les noms (lisibles et d\'origine) et sur les valeurs de toutes les lignes.</p>';
  }

  /** Données de la liste sélectionnée : nom (propre à la liste), source, lignes gardées, ordre, colonnes (champ, titre, valeurs), texte si vide. L'apparence est dans la barre du haut. */
  function listPanel(e) {
    if (lcFor !== e.id) { lcFor = e.id; lcOpen = new Set(); }
    const s = sources.find(x => x.id === e.source), d = srcData[e.source], cs = colsOf(e.source), sets = sources.filter(x => x.app === 'dataset');
    const fOpts = v => opt(cs.filter(c => !hiddenWhy(e.source, c) || c.key === v).map(c => [c.key, fname(c) + (fgroup(c) ? ' · ' + fgroup(c) : '')]), v);
    const scr = sources.find(x => x.app === 'screen'), scrOpts = scr ? (scr.cols || []).map(c => ['@' + c.key, 'Cet écran · ' + c.label]) : [];
    let h = `<button class="ce-crumb" data-dset="${esc(e.source)}">‹ ${esc(s ? s.name : 'Jeu introuvable')}</button>
      <div class="ce-ph"><b>Données de la liste</b></div>
      <label class="ce-lbl2" for="ce-lname">Nom de la liste</label><input class="ce-lname" id="ce-lname" data-l="name" value="${esc(e.name || '')}" maxlength="60" placeholder="ex. Plugins">
      <p class="ce-help" style="margin:4px 0 0">Propre à cette liste : il la distingue dans les calques et dans ce panneau. Il ne s'affiche pas sur l'écran.</p>
      <div class="ce-lsrc">${wi(LC().SRC.dataset)}<span class="t"><small>Source</small><select class="ce-sel" data-l="source" aria-label="Jeu de données">${(sets.some(x => x.id === e.source) ? '' : `<option value="${esc(e.source)}" selected>Jeu introuvable</option>`) + opt(sets.map(x => [x.id, x.name]), e.source)}</select></span></div>`;
    if (!d) return h + '<div class="ce-empty">Lecture des données…</div>';
    if (d.error || !d.columns) return h + `<div class="ce-note">Données indisponibles : ${esc(d.error || 'aucune colonne')}</div>`;
    const kept = keptRows(e), total = (d.rows || []).length;
    h += `<div class="ce-grp">Lignes gardées <span class="ce-cnt">${kept.length} sur ${total}</span></div>` + (e.filter || []).map((f, i) => {
      const c = cs.find(x => x.key === f.key), noVal = f.op === 'empty' || f.op === 'notempty', fv = String(f.val == null ? '' : f.val);
      let val = '<span></span>';
      if (!noVal) {
        const counts = new Map(), ci = c ? d.columns.findIndex(x => x.key === c.key) : -1;
        if (ci >= 0) d.rows.forEach(r => { const v = r[ci] == null ? '' : String(r[ci]); if (v !== '') counts.set(v, (counts.get(v) || 0) + 1); });
        if ((counts.size && counts.size <= 50 && ((f.op || 'eq') === 'eq' || f.op === 'ne')) || fv.charAt(0) === '@') {
          const list = [...counts].sort((a, b) => b[1] - a[1]).map(([v, n]) => [v, LC().fmt(v, c ? c.type : 'text') + ' (' + n + ')']);
          if (fv && fv.charAt(0) !== '@' && !counts.has(fv)) list.unshift([fv, fv]);
          val = `<select class="ce-sel" data-lf="val:${i}" aria-label="Valeur"><option value="">(choisir)</option>${opt(list.concat(scrOpts), fv)}</select>`;
        } else val = `<input class="ce-sel" data-lf="val:${i}" value="${esc(fv)}" placeholder="valeur" aria-label="Valeur">`;
      }
      return `<div class="ce-fil"><select class="ce-sel" data-lf="key:${i}" aria-label="Champ">${fOpts(f.key)}</select><select class="ce-sel" data-lf="op:${i}" aria-label="Condition">${opt(OPS, f.op || 'eq')}</select>${val}<button class="ce-ib" data-lfdel="${i}" title="Retirer la condition" aria-label="Retirer la condition">×</button></div>`;
    }).join('') + '<button class="ce-link" data-lfadd="1">+ Ajouter une condition</button>' +
      ((e.filter || []).length ? '<p class="ce-help" style="margin:2px 0 0">Les valeurs proposées viennent de la source, avec leur nombre de lignes. Toutes les conditions doivent être vraies ; « Cet écran » garde les lignes de l\'écran qui affiche la page.</p>' : '');
    h += `<div class="ce-grp">Ordre</div><div class="ce-fil two"><select class="ce-sel" data-ls="key" aria-label="Trier par"><option value="">Ordre de la source</option>${fOpts(e.sort && e.sort.key)}</select>
      <select class="ce-sel" data-ls="dir" aria-label="Sens">${opt([['asc', 'Croissant ↑'], ['desc', 'Décroissant ↓']], e.sort && e.sort.dir)}</select></div>`;
    const sug = (e.cols || []).map((c, i) => [c, i]).find(([c]) => { const t = (cs.find(x => x.key === c.key) || {}).type; if (t !== 'text' && t !== 'link') return false; const cm = LC().common(colValues(e, c.key, kept)); return (cm.pre && !c.pre) || (cm.suf && !c.suf); });
    if (sug) {
      const cm = LC().common(colValues(e, sug[0].key, kept));
      h += `<div class="ce-sug"><span>Les valeurs de « ${esc(sug[0].label || keyName(e.source, sug[0].key))} » ${cm.pre ? `commencent toutes par <b>${esc(cm.pre)}</b>` : ''}${cm.pre && cm.suf ? ' et ' : ''}${cm.suf ? `finissent toutes par <b>${esc(cm.suf)}</b>` : ''}.</span><button class="ce-btn sm" data-lclean="${sug[1]}">Raccourcir</button></div>`;
    }
    h += `<div class="ce-grp">Colonnes <small>⋮⋮ pour l'ordre · ▾ pour les valeurs</small></div>` + (e.cols || []).map((c, i) => colCard(e, c, i, cs, kept)).join('') +
      (cs.length ? '<button class="ce-addcol" data-lcadd="1">+ Ajouter une colonne</button>' : '');
    return h + `<div class="ce-grp">Si aucune ligne n'est gardée</div><input class="ce-lname sm" data-l="empty" value="${esc(e.empty || '')}" maxlength="80" placeholder="Aucune ligne à afficher" aria-label="Texte si aucune ligne">
      <button class="ce-btn ghost ce-wide" data-ldup="1">⧉ Dupliquer la liste pour un autre filtre</button>
      <p class="ce-help">Ici, seulement les données : quelles lignes, dans quel ordre, quelles colonnes, sous quels titres. L'apparence (police, couleurs, largeur des colonnes, lignes à la fois) se règle dans la barre au-dessus de la page.</p>`;
  }
  function colCard(e, c, i, cs, kept) {
    const f = cs.find(x => x.key === c.key) || { key: c.key, label: c.key, type: 'text', name: '' }, open = lcOpen.has(i), d = srcData[e.source];
    const first = kept[0] || (d.rows || [])[0], ci = d.columns.findIndex(x => x.key === c.key), raw = first && ci >= 0 ? first[ci] : null;
    let b = '';
    if (open) {
      b += `<label class="ce-lbl2">Champ</label><select class="ce-sel" data-lc="key:${i}" aria-label="Champ">${opt(cs.map(x => [x.key, fname(x) + (fgroup(x) ? ' · ' + fgroup(x) : '')]), c.key)}</select>`;
      if (f.type === 'text' || f.type === 'link') {
        const cm = LC().common(colValues(e, c.key, kept));
        b += `<span class="ce-lbl2">Rendre les valeurs lisibles${kept.length > 1 ? ` <i>· détecté sur les ${kept.length} lignes gardées</i>` : ''}</span>` +
          (cm.pre || c.pre ? `<label class="ce-chk"><input type="checkbox" data-lk="pre:${i}" ${c.pre ? 'checked' : ''}><span>Retirer le début commun <q>${esc(cm.pre || '—')}</q></span></label>` : '') +
          (cm.suf || c.suf ? `<label class="ce-chk"><input type="checkbox" data-lk="suf:${i}" ${c.suf ? 'checked' : ''}><span>Retirer la fin commune <q>${esc(cm.suf || '—')}</q></span></label>` : '') +
          `<label class="ce-chk"><input type="checkbox" data-lk="space:${i}" ${c.space ? 'checked' : ''}><span>Espacer les mots collés (DatabaseBackup → Database Backup)</span></label>` +
          (c.map || []).map((m, j) => `<div class="ce-map"><input class="ce-sel" data-lm="a:${i}:${j}" value="${esc(m.a || '')}" placeholder="remplacer…" aria-label="Texte à remplacer"><span>→</span><input class="ce-sel" data-lm="b:${i}:${j}" value="${esc(m.b || '')}" placeholder="par… (vide : retirer)" aria-label="Remplacé par"><button class="ce-ib" data-lmdel="${i}:${j}" title="Retirer" aria-label="Retirer le remplacement">×</button></div>`).join('') +
          `<button class="ce-link" data-lmadd="${i}">+ Remplacer un mot ou une valeur</button>` +
          (raw != null && raw !== '' ? `<div class="ce-prev"><s>${esc(LC().fmt(raw, f.type))}</s><br>→ <b>${esc(LC().cellText(raw, c, f.type, undefined, cm))}</b></div>` : '');
      } else if (f.type === 'date') {
        const ex = fm => raw ? LC().cellText(raw, Object.assign({}, c, { fmt: fm }), 'date') : '';
        b += `<span class="ce-lbl2">Écrire la date</span><div class="ce-segs">${[['auto', 'Automatique'], ['time', 'Heure'], ['day', 'Jour'], ['rel', 'Relatif']].map(([fm, t]) => `<button data-lfmt="${i}:${fm}" class="${(c.fmt || 'auto') === fm ? 'on' : ''}">${t}${ex(fm) ? `<small>${esc(ex(fm))}</small>` : ''}</button>`).join('')}</div>`;
      } else if (f.type === 'bool') {
        b += `<span class="ce-lbl2">Écrire à la place de Oui / Non <i>· couleurs : barre du haut, Couleurs</i></span>
          <div class="ce-map"><span class="ce-yn y">Oui</span><span>→</span><input class="ce-sel" data-lc="yes:${i}" value="${esc(c.yes || '')}" placeholder="Oui" maxlength="40" aria-label="Texte à la place de Oui"></div>
          <div class="ce-map"><span class="ce-yn n">Non</span><span>→</span><input class="ce-sel" data-lc="no:${i}" value="${esc(c.no || '')}" placeholder="Non" maxlength="40" aria-label="Texte à la place de Non"></div>`;
      } else b += '<p class="ce-help" style="margin:0">Rien d\'autre à régler pour ce type de champ.</p>';
    }
    return `<div class="ce-cc${open ? ' open' : ''}" data-ci="${i}"><div class="cch"><span class="grip" draggable="true" title="Glissez pour changer l'ordre">⋮⋮</span>
      <input class="cti" data-lc="label:${i}" value="${esc(c.label || fname(f))}" maxlength="60" aria-label="Titre de la colonne">
      <button class="ce-ib" data-lco="${i}" title="Champ et valeurs de la colonne" aria-expanded="${open}">${open ? '▴' : '▾'}</button><button class="ce-ib" data-lcdel="${i}" title="Retirer la colonne" aria-label="Retirer la colonne">×</button></div>
      <div class="ccs"><em>${TYI[f.type] || 'Aa'}</em><span>${esc(fname(f))}</span><span class="k">${esc(f.label)}</span></div>${open ? `<div class="ccb">${b}</div>` : ''}</div>`;
  }
  /** Réglage de données de la liste sélectionnée (panneau) : en direct pendant la saisie, enregistré au changement (le panneau suit pour les menus et cases). */
  function listInput(t, done) {
    const e = cur(); if (!e || e.type !== 'list') return false;
    const ds = t.dataset, menu = t.tagName === 'SELECT';
    if (menu && !done) return true;
    if (ds.l) {
      if (ds.l === 'source') { e.source = t.value; resetList(e); lcOpen = new Set(); loadSource(e.source); }
      else e[ds.l] = t.value;
    } else if (ds.lc) {
      const [key, i] = ds.lc.split(':'), c = (e.cols || [])[+i]; if (!c) return true;
      if (key === 'key') { const old = colsOf(e.source).find(x => x.key === c.key); if (!c.label || (old && c.label === fname(old))) c.label = keyName(e.source, t.value); c.key = t.value; delete c.pre; delete c.suf; boolColors(c, colsOf(e.source).find(x => x.key === t.value)); }
      else c[key] = t.value;
    } else if (ds.lk) { const [key, i] = ds.lk.split(':'); if (e.cols[+i]) e.cols[+i][key] = t.checked; }
    else if (ds.lm) { const [key, i, j] = ds.lm.split(':'), m = ((e.cols[+i] || {}).map || [])[+j]; if (m) m[key] = t.value; }
    else if (ds.lf) { const [key, i] = ds.lf.split(':'), f = (e.filter || [])[+i]; if (!f) return true; f[key] = t.value; if (key === 'key') f.val = ''; }
    else if (ds.ls) { e.sort = Object.assign({ key: '', dir: 'asc' }, e.sort || {}, { [ds.ls]: t.value }); if (!e.sort.key) e.sort = null; }
    else return false;
    if (done) { commit(); renderCtx(); if (menu || t.type === 'checkbox' || ds.lf) renderPanel(); }
    else { render(); if (ds.l === 'name') renderCtx(); }
    return true;
  }
  /** Clics du panneau Données ; true si traité. */
  function dataClick(ev) {
    const g = s => ev.target.closest(s), e = cur();
    let x;
    if (g('#ce-fren')) return true;
    if ((x = g('[data-fren]'))) { dv.ren = x.dataset.fren; renderPanel(); return true; }
    if ((x = g('[data-fhide]'))) { const [id, key] = x.dataset.fhide.split(':'), c = colsOf(id).find(y => y.key === key); if (c) colPref(id, key, c.name, !c.hidden); return true; }
    if ((x = g('[data-dset]'))) {
      if (e) { selId = null; drawOverlay(); renderLayers(); renderCtx(); }
      Object.assign(dv, { view: 'set', set: x.dataset.dset, row: 0, q: '', ren: null }); loadSource(dv.set); renderPanel(); return true;
    }
    if (g('[data-dback]')) { Object.assign(dv, { view: 'sources', q: '', ren: null }); renderPanel(); return true; }
    if (g('[data-dclear]')) { dv.q = ''; renderPanel(); return true; }
    if ((x = g('[data-drow]'))) { dv.row = Math.max(0, dv.row + +x.dataset.drow); renderPanel(); return true; }
    if ((x = g('[data-dg]'))) { const key = x.dataset.dg; if (dv.open.has(key)) dv.open.delete(key); else dv.open.add(key); renderPanel(); return true; }
    if (g('[data-dhid]')) { dv.hidden = !dv.hidden; renderPanel(); return true; }
    if ((x = g('[data-sheet]'))) { openSheet(x.dataset.sheet); return true; }
    if ((x = g('[data-sel]'))) { select(x.dataset.sel); return true; }
    if (!e || e.type !== 'list') return false;
    if ((x = g('[data-lco]'))) { const i = +x.dataset.lco; if (lcOpen.has(i)) lcOpen.delete(i); else lcOpen.add(i); renderPanel(); return true; }
    if ((x = g('[data-lcdel]'))) { e.cols.splice(+x.dataset.lcdel, 1); lcOpen = new Set(); commit(); renderPanel(); return true; }
    if (g('[data-lfadd]')) { const c = colsOf(e.source).find(y => !hiddenWhy(e.source, y)); if (c) { (e.filter = e.filter || []).push({ key: c.key, op: 'eq', val: '' }); commit(); renderPanel(); } return true; }
    if ((x = g('[data-lfdel]'))) { e.filter.splice(+x.dataset.lfdel, 1); commit(); renderPanel(); return true; }
    if ((x = g('[data-lclean]'))) { const i = +x.dataset.lclean; Object.assign(e.cols[i], { pre: true, suf: true, space: true }); lcOpen.add(i); commit(); renderPanel(); return true; }
    if ((x = g('[data-lmadd]'))) { const c = e.cols[+x.dataset.lmadd]; (c.map = c.map || []).push({ a: '', b: '' }); renderPanel(); return true; }   // enregistré avec la saisie
    if ((x = g('[data-lmdel]'))) { const [i, j] = x.dataset.lmdel.split(':').map(Number); e.cols[i].map.splice(j, 1); commit(); renderPanel(); return true; }
    if ((x = g('[data-lfmt]'))) { const [i, fm] = x.dataset.lfmt.split(':'); e.cols[+i].fmt = fm; commit(); renderPanel(); return true; }
    if ((x = g('[data-lcadd]'))) { colPicker(x, e); return true; }
    if (g('[data-ldup]')) { dupList(e); return true; }
    return false;
  }
  /** Champ ajouté à la liste : choisi dans les champs du jeu, groupés, avec une recherche. */
  function colPicker(anchor, e) {
    const cs = colsOf(e.source), g = groupsOf(cs.filter(c => !hiddenWhy(e.source, c))), d = srcData[e.source], r = d && d.rows ? d.rows[0] : null;
    let h = '<label class="ce-search" style="margin:4px 6px 6px">⌕<input id="ce-cpq" placeholder="Chercher un champ" aria-label="Chercher un champ"></label>';
    g.forEach((list, key) => {
      h += `<div class="ce-pt">${gTitle(key)}</div>` + list.map(c => `<button class="ce-mi" data-o="${esc(c.key)}" data-cpn="${esc((fname(c) + ' ' + c.label).toLowerCase())}">${esc(fname(c))}<small>${esc(LC().fmt(cellRaw(e.source, c, r), c.type))}</small></button>`).join('');
    });
    openPop(anchor, h, key => {
      const c = cs.find(x => x.key === key); if (!c) return;
      (e.cols = e.cols || []).push(boolColors({ key, label: fname(c), as: 'text', align: c.type === 'number' || c.type === 'date' ? 'right' : 'left', w: 1 }, c));
      lcOpen.add(e.cols.length - 1); commit(); renderPanel();
    });
    const p = q('#ce-pop'), s = q('#ce-cpq'); p.classList.add('ce-cpick'); s.focus();
    s.addEventListener('input', () => { const t = s.value.trim().toLowerCase(); p.querySelectorAll('[data-cpn]').forEach(b => { b.hidden = !!t && !b.dataset.cpn.includes(t); }); });
  }
  /** Nom lisible et masquage d'un champ, enregistrés pour le jeu (toutes les compositions). */
  async function colPref(id, key, nm, hidden) {
    let err;
    try { err = await net.invokeMethodAsync('ColumnPref', id, key, nm || '', !!hidden); } catch (x) { err = 'Enregistrement impossible : la connexion au serveur est interrompue.'; }
    if (!root) return;
    dv.ren = null;
    if (err) { toast(err, true); renderPanel(); return; }
    const s = sources.find(x => x.id === id);
    if (s) { s.cols = s.cols || []; let c = s.cols.find(x => x.key === key); if (!c) { c = { key, label: key }; s.cols.push(c); } c.name = nm || ''; c.hidden = !!hidden; }
    renderPanel();
  }
  /** Champ en cours de renommage : Entrée ou clic ailleurs enregistre (vide : nom d'origine), Échap annule. */
  function bindRename(p) {
    const n = p.querySelector('#ce-fren'); if (!n) return;
    n.focus(); n.select();
    let over = false;
    const end = ok => {
      if (over) return; over = true;
      const [id, key] = String(dv.ren).split(':'), c = colsOf(id).find(x => x.key === key), v = n.value.trim();
      if (ok && c && v !== fname(c)) colPref(id, key, v === humanize(segs(c).pop() || '') || v === c.label ? '' : v, c.hidden);
      else { dv.ren = null; renderPanel(); }
    };
    n.addEventListener('keydown', ev => { if (ev.key === 'Enter') { ev.preventDefault(); end(true); } if (ev.key === 'Escape') { ev.stopPropagation(); end(false); } });
    n.addEventListener('blur', () => end(true));
  }

  // ---------- tableau en grand d'un jeu (par-dessus la page) ----------
  function openSheet(id) { sheet = { set: id, all: false, q: '' }; loadSource(id); drawSheet(); }
  function closeSheet() { sheet = null; const s = root && q('#ce-sheet'); if (s) s.remove(); }
  function drawSheet() {
    if (!sheet || !root) return;
    let el = q('#ce-sheet');
    if (!el) { el = document.createElement('div'); el.id = 'ce-sheet'; el.className = 'ce-sheet'; el.setAttribute('role', 'dialog'); el.setAttribute('aria-label', 'Données du jeu'); q('.ce-center').appendChild(el); bindSheet(el); }
    const id = sheet.set, s = sources.find(x => x.id === id), d = srcData[id];
    const head = `<div class="sh">${wi(LC().SRC.dataset)}<div class="t"><b>${esc(s ? s.name : 'Jeu introuvable')}</b><small>${d && d.rows ? d.rows.length + ' lignes · ' + (d.columns || []).length + ' champs · ' : ''}${s ? esc(srcState(s)) : ''}</small></div>`;
    if (!s || !d || d.error || !d.columns) { el.innerHTML = head + `<button class="ce-xb" data-sh="x" aria-label="Fermer">×</button></div><div class="ce-empty" style="margin:20px">${d && d.error ? 'Données indisponibles : ' + esc(d.error) : 'Lecture des données…'}</div>`; return; }
    const t = sheet.q.trim().toLowerCase(), cs = colsOf(id), uk = usedKeys(id), ix = c => d.columns.findIndex(x => x.key === c.key);
    let vis = cs.filter(c => sheet.all || !hiddenWhy(id, c));
    if (t) vis = vis.filter(c => (fname(c) + ' ' + c.label).toLowerCase().includes(t) || d.rows.some(r => LC().fmt(r[ix(c)], c.type).toLowerCase().includes(t)));
    const g = groupsOf(vis), order = [...g.values()].flat(), MAX = 300;
    let h1 = '<th class="n"></th>', h2 = '<th class="n">#</th>';
    g.forEach((list, key) => {
      h1 += `<th colspan="${list.length}">${esc(key ? key.split('.').map(humanize).join(' › ') : '')}</th>`;
      h2 += list.map(c => `<th class="${uk.has(c.key) ? 'used' : ''}" draggable="true" data-add="field:${id}:${esc(c.key)}" title="Glissez sur la page, ou cliquez pour poser ce champ">${esc(fname(c))}<small>${esc(segs(c).pop() || c.key)}</small></th>`).join('');
    });
    const body = d.rows.slice(0, MAX).map((r, i) => `<tr><td class="n">${i + 1}</td>${order.map(c => `<td>${valHtml(id, c, r)}</td>`).join('')}</tr>`).join('');
    el.innerHTML = head + `<span class="ce-seg"><button data-sh="0" class="${sheet.all ? '' : 'on'}">Champs visibles</button><button data-sh="1" class="${sheet.all ? 'on' : ''}">Tous les champs</button></span>
      <label class="ce-search">⌕<input id="ce-shq" placeholder="Filtrer les colonnes ou les valeurs" value="${esc(sheet.q)}" aria-label="Filtrer les colonnes ou les valeurs"></label><button class="ce-xb" data-sh="x" aria-label="Fermer">×</button></div>
      <div class="tw">${order.length ? `<table><thead><tr class="g">${h1}</tr><tr class="c">${h2}</tr></thead><tbody>${body}</tbody></table>` : '<div class="ce-empty" style="margin:20px">Aucune colonne ne correspond.</div>'}</div>
      <div class="sf">Glissez un en-tête sur la page, ou cliquez dessus, pour y poser le champ. Souligné : déjà sur la page.${d.rows.length > MAX ? ` ${MAX} premières lignes sur ${d.rows.length}.` : ''}</div>`;
  }
  function bindSheet(el) {
    on(el, 'click', ev => {
      const b = ev.target.closest('[data-sh]');
      if (b) { if (b.dataset.sh === 'x') closeSheet(); else { sheet.all = b.dataset.sh === '1'; drawSheet(); } return; }
      const a = ev.target.closest('[data-add]'); if (!a) return;
      closeSheet();
      if (cur() && cur().type === 'text') insertField(a.dataset.add.slice(6)); else addEl(a.dataset.add);
    });
    on(el, 'input', ev => { if (ev.target.id !== 'ce-shq') return; sheet.q = ev.target.value; drawSheet(); const n = q('#ce-shq'); n.focus(); n.setSelectionRange(n.value.length, n.value.length); });
    on(el, 'dragstart', ev => { const a = ev.target.closest('[data-add]'); if (!a) return; ev.dataTransfer.setData('text/linkii-compo', a.dataset.add); setTimeout(() => { el.style.visibility = 'hidden'; }, 0); });   // la page reçoit le dépôt
    on(el, 'dragend', closeSheet);
  }

  // ---------- barre contextuelle ----------
  const sepH = '<span class="ce-sep"></span>';
  const fontSel = e => `<select data-k="font" title="Police" aria-label="Police" style="width:150px;font-family:'${e.font}'">${LC().FONTS.map(f => `<option ${f === e.font ? 'selected' : ''} style="font-family:'${f}'">${f}</option>`).join('')}</select>`;
  const sizeNum = e => `<span class="ce-num" title="Taille"><button data-step="size:-4" aria-label="Plus petit">−</button><input data-k="size" data-t="n" value="${e.size}" aria-label="Taille"><button data-step="size:4" aria-label="Plus grand">+</button></span>`;
  const colorIn = (key, v, t) => `<input type="color" data-k="${key}" value="${esc(v)}" title="${t}" aria-label="${t}">`;
  const pick = (key, opts, v, t) => `<select data-k="${key}" ${typeof opts[0][0] === 'number' ? 'data-t="n"' : ''} title="${t}" aria-label="${t}">${opts.map(([a, b]) => `<option value="${a}" ${a == v ? 'selected' : ''}>${b}</option>`).join('')}</select>`;
  const range = (key, t, mn, mx, st, v) => `<label class="ce-lbl">${t}<input type="range" data-k="${key}" data-t="n" min="${mn}" max="${mx}" step="${st}" value="${v}"></label>`;
  const alignBtns = e => [['left', 'M4 6h16M4 12h10M4 18h14', 'à gauche'], ['center', 'M4 6h16M7 12h10M5 18h14', 'au centre'], ['right', 'M4 6h16M10 12h10M6 18h14', 'à droite']]
    .map(([a, p, t]) => `<button class="ce-tb ${e.align === a ? 'on' : ''}" data-set="align:${a}" title="Aligner ${t}"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="${p}"/></svg></button>`).join('');
  const textTools = e => fontSel(e) + sizeNum(e) + colorIn('color', e.color, 'Couleur du texte') + pick('weight', WEIGHTS, e.weight, 'Graisse') +
    `<button class="ce-tb ${e.italic ? 'on' : ''}" data-tog="italic" title="Italique"><i style="font-family:serif;font-size:17px">I</i></button>` + alignBtns(e) +
    pick('ls', [[-0.02, 'Serré'], [0, 'Normal'], [0.1, 'Large'], [0.25, 'Très large']], e.ls, 'Interlettrage');
  function renderCtx() {
    if (!root) return;
    const e = cur(), c = q('#ce-ctx');
    let h = '';
    if (!e) h = '<span class="ce-hint">Cliquez sur un élément pour le modifier · double-cliquez sur un texte pour écrire · glissez depuis le panneau de gauche</span>';
    else if (textual(e)) {
      h = textTools(e);
      if (e.type === 'text') h += sepH + `<button class="ce-tb" data-act="field" title="Insérer un champ relié à une source (ou tapez « { » pendant l'écriture)">{ } Champ</button>` +
        (LC().sources({ els: [e] }).length ? '<button class="ce-tb" data-act="fb" title="Texte affiché quand la source n\'a pas de valeur">Valeur de secours…</button>' : '');
      if (e.kind === 'clock') h += sepH + `<button class="ce-tb ${e.sec ? 'on' : ''}" data-tog="sec">Secondes</button>`;
      if (e.kind === 'countdown') h += sepH + `<label class="ce-lbl">Jusqu'au<input type="date" data-k="until" value="${esc(e.until)}"></label>`;
    }
    else if (e.type === 'image') h = `<button class="ce-tb" data-act="replace">⇄ ${e.media ? 'Remplacer' : 'Choisir une image'}</button>` + pick('fit', [['cover', 'Remplir'], ['contain', 'Image entière']], e.fit, 'Cadrage') +
      range('radius', 'Arrondi', 0, 300, 2, e.radius) + range('opacity', 'Opacité', 0, 1, .05, e.opacity);
    else if (e.type === 'shape') h = colorIn('fill', e.fill, 'Couleur') +
      (e.kind === 'rect' ? range('radius', 'Arrondi', 0, 300, 2, e.radius) + (e.stroke ? colorIn('stroke', e.stroke, 'Contour') : '') : '') +
      (e.kind === 'arrow' ? pick('rot', [[0, '→ Droite'], [45, '↘ Bas droite'], [90, '↓ Bas'], [135, '↙ Bas gauche'], [180, '← Gauche'], [225, '↖ Haut gauche'], [270, '↑ Haut'], [315, '↗ Haut droite']], e.rot, 'Direction') : '') +
      (e.kind === 'line' ? range('sw', 'Épaisseur', 1, 40, 1, e.sw) : '') + range('opacity', 'Opacité', 0, 1, .05, e.opacity);
    else if (e.type === 'app') {
      const m = LC().SRC[e.app] || {}, same = sources.filter(s => s.app === e.app);
      if (!same.some(s => s.id === e.source)) same.unshift({ id: e.source, name: 'Source introuvable' });
      h = `<span class="ce-kind">${m.icon || ''} Bloc ${esc(m.name || '')}</span>` + sepH +
        `<label class="ce-lbl">Source<select data-k="source" aria-label="Source">${same.map(s => `<option value="${esc(s.id)}" ${s.id === e.source ? 'selected' : ''}>${esc(s.name)}</option>`).join('')}</select></label>` +
        (e.app === 'weather-slide' ? pick('view', [['days', 'Actuelle + 3 jours'], ['now', 'Actuelle seulement']], e.view, 'Affichage')
          : pick('rows', [[2, '2 lignes'], [3, '3 lignes'], [4, '4 lignes'], [5, '5 lignes'], [6, '6 lignes'], [8, '8 lignes']], e.rows, 'Nombre de lignes')) +
        pick('bg', [['none', 'Fond transparent'], ['light', 'Fond clair'], ['dark', 'Fond sombre']], e.bg, 'Fond') + sepH +
        fontSel(e) + sizeNum(e) + colorIn('color', e.color, 'Couleur du texte') + pick('weight', WEIGHTS, e.weight, 'Graisse') + sepH +
        '<button class="ce-tb" data-act="srcset" title="Ouvrir les réglages de cette source (ville, calendrier…) dans un nouvel onglet">⚙ Réglages</button>';
    }
    else if (e.type === 'list') {   // liste reliée : apparence seulement (§140) ; ses données se règlent dans le panneau Données
      const nr = (e.rules || []).length;
      h = `<span class="ce-kind" title="Nom de la liste (panneau Données)">▦ ${esc(e.name || 'Liste')}</span>` + sepH +
        fontSel(e) + sizeNum(e) + colorIn('color', e.color, 'Couleur du texte') + pick('weight', WEIGHTS, e.weight, 'Graisse') + sepH +
        `<button class="ce-tb ${e.head ? 'on' : ''}" data-tog="head" title="Titres des colonnes au-dessus des lignes">En-tête</button>` +
        `<button class="ce-tb" data-act="lrows" title="Lignes affichées à la fois, et s'il y en a plus">${e.rows} lignes · ${e.page === 'scroll' ? 'défilement' : e.page === 'cut' ? 'coupé' : 'pages ' + e.sec + ' s'} ▾</button>` +
        '<button class="ce-tb" data-act="lcols" title="Style, alignement et largeur des colonnes">Colonnes ▾</button>' +
        `<button class="ce-tb ${nr ? 'on' : ''}" data-act="lcolors" title="Couleurs des pastilles, des séparateurs et de Oui / Non ; règles de couleur">Couleurs${nr ? ' (' + nr + ')' : ''} ▾</button>`;
    }
    else if (e.type === 'qr') h = `<label class="ce-lbl">Lien<input type="text" data-k="url" value="${esc(e.url)}" style="width:240px" maxlength="500"></label>` + colorIn('fg', e.fg, 'Couleur') + colorIn('bg', e.bg, 'Fond');
    else if (e.kind === 'ticker') h = `<label class="ce-lbl">Texte<input type="text" data-k="text" value="${esc(e.text)}" style="width:260px" maxlength="1000"></label>` + fontSel(e) + sizeNum(e) +
      colorIn('color', e.color, 'Couleur du texte') + colorIn('bg', e.bg, 'Fond') + pick('speed', [[30, 'Lent'], [18, 'Normal'], [10, 'Rapide']], e.speed, 'Vitesse');
    if (e) h += sepH + `<label class="ce-lbl" title="Rotation en degrés (ou la poignée ronde au-dessus de l'élément)">↻<input type="number" data-k="rot" data-t="n" min="-180" max="180" step="1" value="${Math.round(+e.rot || 0)}" style="width:62px"></label><button class="ce-tb" data-act="pos">Position ▾</button><button class="ce-tb ${e.locked ? 'on' : ''}" data-act="lock" title="Verrouiller">${e.locked ? '🔒 Verrouillé' : '🔓'}</button>` +
      '<button class="ce-tb" data-act="dup" title="Dupliquer (Ctrl+D)">⧉</button><button class="ce-tb" data-act="del" title="Supprimer (Suppr)">🗑</button>';
    c.innerHTML = h;
    c.classList.toggle('empty', !e);
  }
  function act(a, b) {
    const e = cur(); if (!e) return;
    if (a === 'pos') posMenu(b);
    else if (a === 'lock') { e.locked = !e.locked; commit(); renderCtx(); }
    else if (a === 'dup') dup();
    else if (a === 'del') del();
    else if (a === 'replace') { mediaMode = 'replace'; openPanel('medias'); }
    else if (a === 'field') fieldMenu(b);
    else if (a === 'fb') { const v = prompt('Texte affiché à la place d\'un champ quand sa source n\'a pas de valeur :', e.fb || '—'); if (v != null) { e.fb = v.slice(0, 40); commit(); } }
    else if (a === 'lrows' || a === 'lcols' || a === 'lcolors') listPop(a.slice(1));
    else if (a === 'srcset' && e.source) w.open('/apps/instance/' + encodeURIComponent(e.source), '_blank', 'noopener');
  }
  function dup() {
    const e = cur(); if (!e) return;
    const c = Object.assign(JSON.parse(JSON.stringify(e)), { id: nid(), x: e.x + Math.round(40 * k()), y: e.y + Math.round(40 * k()), locked: false });
    if (c.type === 'list') c.name = nextName(e.name || 'Liste');
    D.els.splice(D.els.indexOf(e) + 1, 0, c); selId = c.id; commit(); renderCtx();
  }
  function del() { const e = cur(); if (!e) return; D.els = D.els.filter(x => x !== e); selId = null; commit(); renderCtx(); }
  function order(e, to) {
    const i = D.els.indexOf(e); D.els.splice(i, 1);
    const j = to === 'top' ? D.els.length : to === 'bottom' ? 0 : to === 'up' ? Math.min(D.els.length, i + 1) : Math.max(0, i - 1);
    D.els.splice(j, 0, e); commit();
  }
  function posMenu(b) {
    const e = cur();
    openPop(b, `<button class="ce-mi" data-o="top">Premier plan</button><button class="ce-mi" data-o="up">Avancer</button><button class="ce-mi" data-o="down">Reculer</button><button class="ce-mi" data-o="bottom">Arrière-plan</button><hr>
      <button class="ce-mi" data-o="ch">Centrer horizontalement</button><button class="ce-mi" data-o="cv">Centrer verticalement</button>${e.type === 'image' || e.type === 'shape' ? '<button class="ce-mi" data-o="full">Remplir la page</button>' : ''}
      <hr><div class="ce-pt">x ${e.x} · y ${e.y} · ${e.w} × ${Math.round(e.h)} px</div>`, o => {
      if (['top', 'up', 'down', 'bottom'].includes(o)) order(e, o);
      if (o === 'ch') { e.x = Math.round(D.w / 2 - e.w / 2); commit(); }
      if (o === 'cv') { e.y = Math.round(D.h / 2 - e.h / 2); commit(); }
      if (o === 'full') { Object.assign(e, { x: 0, y: 0, w: D.w, h: D.h }); commit(); }
    });
  }

  // ---------- menus ----------
  function openPop(anchor, html, cb) {
    closePop();
    const p = document.createElement('div'); p.className = 'ce-pop'; p.id = 'ce-pop'; p.innerHTML = html; root.appendChild(p);
    const r = anchor.getBoundingClientRect();
    p.style.top = (r.bottom + 6) + 'px'; p.style.left = Math.max(8, Math.min(r.left, innerWidth - p.offsetWidth - 10)) + 'px';
    popCb = cb;
    p.addEventListener('click', ev => { const b = ev.target.closest('[data-o]'); if (b) { const keep = b.dataset.keep; popCb(b.dataset.o); if (!keep) closePop(); } });
  }
  function closePop() { const p = root && q('#ce-pop'); if (p) p.remove(); }
  function bgMenu(b) {
    const cols = ['#FFFFFF', '#F5F7FA', '#EEF3EC', '#FFF4E5', '#E07B1A', '#C0392B', '#3B82C4', '#0B1F3A', '#1F3B33', '#111827', '#151515', '#000000'];
    openPop(b, `<div class="ce-pt">Couleur de fond</div><div class="ce-swgrid">${cols.map(c => `<button data-o="${c}" data-keep="1" class="${D.bg.toUpperCase() === c ? 'on' : ''}" style="background:${c}" aria-label="${c}"></button>`).join('')}</div>
      <label class="ce-mi">Autre couleur <input type="color" id="ce-bgpick" value="${esc(D.bg)}" style="margin-left:auto"></label><hr>
      <button class="ce-mi" data-o="img">Image de fond depuis la médiathèque…</button>`, o => {
      if (o === 'img') { mediaMode = 'bg'; openPanel('medias'); return; }
      D.bg = o; commit(); bgMenu(b);
    });
    const pk = q('#ce-bgpick');
    pk.addEventListener('input', ev => { D.bg = ev.target.value; render(); });
    pk.addEventListener('change', commit);
  }

  // ---------- panneaux ----------
  function openPanel(n) {
    panelName = n; panelOpen = true; if (n !== 'medias') mediaMode = 'add'; renderPanel();
    if (n === 'donnees') sources.filter(s => s.app === 'dataset' || s.app === 'screen').forEach(s => loadSource(s.id));   // exemples de valeurs des colonnes
  }
  function togglePanel() { panelOpen = !panelOpen; renderPanel(); render(); }
  function thumb(t, i, big) {
    if (big) return `<div class="ce-thumb ce-mc" data-tpl="${i}" role="button" tabindex="0" aria-label="Aperçu de ${esc(t.name)}"><div class="ce-mcb"><div class="tv ${t.w >= t.h ? 'land' : 'port'}"><div class="mini" data-w="${t.w}" style="width:${t.w}px;height:${t.h}px;background:${t.bg}">${LC().html({ els: samples[i] }, {})}</div></div>
      <div class="ce-mca"><button type="button" class="a" data-act="prev" tabindex="-1">${ICO.eye} Aperçu</button><button type="button" class="a p" data-act="use" tabindex="-1">Utiliser</button></div></div><div class="tn"><span>${esc(t.name)}</span></div></div>`;
    const tw = t.w >= t.h ? (big ? 250 : 136) : (big ? 140 : 77), s = tw / t.w;
    return `<button class="ce-thumb" data-tpl="${i}"><div class="tv" style="height:${Math.round(t.h * s)}px;width:${tw}px"><div class="mini" style="width:${t.w}px;height:${t.h}px;transform:scale(${s});background:${t.bg}">${LC().html({ els: samples[i] }, {})}</div></div><div class="tn"><span>${esc(t.name)}</span></div></button>`;
  }
  function renderPanel() {
    const p = q('#ce-panel');
    p.classList.toggle('closed', !panelOpen);
    q('#ce-pull').textContent = panelOpen ? '‹' : '›';
    root.querySelectorAll('#ce-rail button').forEach(b => b.classList.toggle('on', panelOpen && b.dataset.p === panelName));
    let h = '';
    if (panelName === 'modeles') {
      const cats = ['Tous', ...new Set(TPL.map(t => t.cat))];
      h = `<div class="ce-tabs2"><button data-tt="galerie" class="${tplTab === 'galerie' ? 'on' : ''}">Galerie</button><button data-tt="mine" class="${tplTab === 'mine' ? 'on' : ''}">Mes modèles <span>${myTpl.length}</span></button></div>`;
      if (tplTab === 'mine') h += myTpl.length
        ? `<div class="ce-tgrid">${myTpl.map(myThumb).join('')}</div><p class="ce-help">Un modèle remplace la page en cours ; Ctrl+Z revient en arrière. Le modèle lui-même ne change pas.</p>`
        : '<div class="ce-empty">Aucun modèle pour l\'instant.<br>Utilisez <b>Enregistrer ▾ › Enregistrer comme modèle…</b> pour réutiliser une composition.</div>';
      else h += `<div class="ce-chips">${cats.map(c => `<button data-cat="${c}" class="${c === tplCat ? 'on' : ''}">${c}</button>`).join('')}</div>
        <div class="ce-tgrid">${TPL.map((t, i) => [t, i]).filter(([t]) => tplCat === 'Tous' || t.cat === tplCat).map(([t, i]) => thumb(t, i)).join('')}</div>
        <p class="ce-help">Un modèle remplace la page en cours (format compris) ; tout reste modifiable ensuite, et Ctrl+Z revient en arrière.</p>`;
    }
    if (panelName === 'medias') {
      const note = mediaMode === 'replace' ? "Choisissez l'image de remplacement" : mediaMode === 'bg' ? "Choisissez l'image de fond" : '';
      h = (note ? `<div class="ce-note">${note}<button data-mmode="add">Annuler</button></div>` : '') +
        `<div class="ce-row"><button class="ce-btn ghost" data-import="1" ${canImport ? '' : 'disabled title="Votre niveau de droits ne permet pas d\'ajouter des fichiers à la médiathèque"'}>⇪ Importer…</button><button class="ce-btn ghost" data-refresh="1">↻ Actualiser</button></div>
        <label class="ce-search">⌕<input id="ce-msearch" placeholder="Rechercher une image" value="${esc(mediaSearch)}"></label>
        <div id="ce-mgrid"></div>
        <p class="ce-help">Cliquez pour ajouter au centre, ou glissez sur la page. « Importer » ajoute vos images à la médiathèque de l'aire et place la première sur la page.</p>`;
    }
    if (panelName === 'texte') {
      h = `<button class="ce-btn ce-add" data-add="text:free">+ Ajouter une zone de texte</button><div class="ce-grp">Styles par défaut</div>
        <button class="ce-tstyle" draggable="true" data-add="text:title" style="font:800 26px Montserrat">Ajouter un titre</button>
        <button class="ce-tstyle" draggable="true" data-add="text:sub" style="font:500 19px Montserrat">Ajouter un sous-titre</button>
        <button class="ce-tstyle" draggable="true" data-add="text:body" style="font:400 14px 'IBM Plex Sans'">Ajouter un corps de texte</button>
        <div class="ce-grp">Polices disponibles</div>${LC().FONTS.map(f => `<div style="font-family:'${f}';font-size:17px;padding:5px 2px">${f}</div>`).join('')}`;
    }
    if (panelName === 'formes') {
      const S = [['rect', '■', 'Rectangle'], ['round', '▢', 'Rectangle arrondi'], ['circle', '●', 'Cercle'], ['pill', '⬭', 'Pastille'], ['frame', '□', 'Cadre'], ['line', '—', 'Ligne'],
        ['arrow0', '→', 'Flèche'], ['arrow180', '←', 'Flèche'], ['arrow270', '↑', 'Flèche'], ['arrow90', '↓', 'Flèche'], ['arrow315', '↗', 'Flèche'], ['arrow45', '↘', 'Flèche']];
      const IC = ['✓', '✕', '★', '♥', '☎', '✉', '⌂', '☕', '♿', '⚠', 'ℹ', 'P'];
      h = `<div class="ce-grp" style="margin-top:0">Formes et flèches</div><div class="ce-sgrid">${S.map(([key, c, t]) => `<button class="ce-shp" draggable="true" data-add="shape:${key}" title="${t}">${c}</button>`).join('')}</div>
        <div class="ce-grp">Pictogrammes</div><div class="ce-sgrid">${IC.map(c => `<button class="ce-shp" draggable="true" data-add="icon:${c}">${c}</button>`).join('')}</div>`;
    }
    if (panelName === 'widgets') {
      const SM = LC().SRC;
      const L = [['clock', '#0B1F3A', '◷', 'Horloge', 'Heure en direct, avec ou sans secondes'], ['date', '#0B1F3A', '▦', 'Date', 'Date du jour, mise à jour à minuit'],
        ['ticker', '#4F6BED', '⇢', 'Bandeau défilant', 'Texte qui défile en continu'], ['countdown', '#E8590C', '⏳', 'Compte à rebours', "Jours restants jusqu'à une date"]];
      h = '<div class="ce-grp" style="margin-top:0">Widgets</div>' + L.map(([key, c, i, n, d]) => `<button class="ce-wrow" draggable="true" data-add="w:${key}"><span class="wi" style="background:${c}">${i}</span><span><b>${n}</b><small>${d}</small></span></button>`).join('') +
        `<button class="ce-wrow" draggable="true" data-add="qr:"><span class="wi" style="background:#5A6B80">▣</span><span><b>QR code</b><small>Lien au choix. Pour compter les scans : onglet « QR code » du contenu, dans la liste de lecture</small></span></button>
        <div class="ce-grp">Mes intégrations <small>déjà configurées</small></div>
        <p class="ce-help" style="margin:0 0 10px">Posées comme un bloc : les données se mettent à jour seules sur l'écran. La source garde ses réglages (ville, calendrier…).</p>
        ${sources.length ? sources.map(s => { const m = SM[s.app] || {}; return `<button class="ce-wrow" draggable="true" data-add="app:${esc(s.id)}">${wi(m)}<span><b>${esc(s.name)}</b><small>${esc(m.name)}</small></span><em class="ce-btag">bloc</em></button>`; }).join('')
          : '<div class="ce-empty">Aucune intégration à poser.<br>Ajoutez une Météo, un Calendrier partagé, un Flux RSS ou des Capteurs Home Assistant dans Mes intégrations.</div>'}
        <a class="ce-link" href="/integrations" target="_blank" rel="noopener">+ Ajouter une intégration depuis le Store</a>
        <div class="ce-grp">Plein écran seulement</div>
        <div class="ce-wrow soon"><span class="wi" style="background:#8A97A8">⌁</span><span><b>Page web, Facebook</b><small>Ne se posent pas dans une composition</small></span></div>`;
    }
    if (panelName === 'donnees') h = dataPanel();
    p.innerHTML = h;
    if (panelName === 'donnees') bindRename(p);
    if (panelName === 'medias') renderMediaGrid();
    if (panelName === 'modeles' && tplTab === 'mine') LC().images(p, (u, cb) => cb(u + '?w=320'));
  }
  function renderMediaGrid() {
    const g = q('#ce-mgrid'); if (!g) return;
    const s = mediaSearch.trim().toLowerCase(), list = images.filter(m => !s || (m.name || '').toLowerCase().includes(s));
    g.innerHTML = !images.length ? '<div class="ce-empty">Aucune image dans la médiathèque.<br>Importez-en avec « Importer… ».</div>'
      : !list.length ? '<div class="ce-empty">Aucune image trouvée.</div>'
      : `<div class="ce-pgrid">${list.map(m => `<button class="ce-photo" draggable="true" data-add="img:${m.id}" title="${esc(m.name)}"><img src="${esc(m.url)}?w=320" alt="" loading="lazy" draggable="false"><span>${esc(m.name)}</span></button>`).join('')}</div>`;
  }
  async function panelClick(ev) {
    if (panelName === 'donnees' && dataClick(ev)) return;
    const a = ev.target.closest('[data-add]');
    if (a && a.dataset.add.startsWith('field:') && cur() && cur().type === 'text') { insertField(a.dataset.add.slice(6)); return; }   // texte sélectionné : le champ s'y ajoute
    if (a) { addEl(a.dataset.add); return; }
    const c = ev.target.closest('[data-cat]'); if (c) { tplCat = c.dataset.cat; renderPanel(); return; }
    const m = ev.target.closest('[data-mmode]'); if (m) { mediaMode = m.dataset.mmode; renderPanel(); return; }
    if (ev.target.closest('[data-refresh]')) {
      try { images = await net.invokeMethodAsync('Images'); } catch (x) { toast('Actualisation impossible pour le moment.', true); return; }
      if (root) { renderMediaGrid(); render(); toast('Médiathèque actualisée'); }
      return;
    }
    const sr = ev.target.closest('[data-srcref]'); if (sr) { loadSource(sr.dataset.srcref, true); return; }
    if (ev.target.closest('[data-srclist]')) {
      try { sources = await net.invokeMethodAsync('Sources'); } catch (x) { toast('Actualisation impossible pour le moment.', true); return; }
      if (root) { renderPanel(); render(); toast('Sources actualisées'); }
      return;
    }
    if (ev.target.closest('[data-import]')) { const f = document.getElementById('ce-file'); if (f && canImport) f.click(); return; }
    const tt = ev.target.closest('[data-tt]'); if (tt) { tplTab = tt.dataset.tt; renderPanel(); return; }
    const dt = ev.target.closest('[data-deltpl]');
    if (dt) {
      const t = myTpl.find(x => x.id === dt.dataset.deltpl);
      if (!t || !confirm(`Supprimer le modèle « ${t.name} » ? Les compositions créées à partir de lui ne changent pas.`)) return;
      try { myTpl = await net.invokeMethodAsync('DeleteTemplate', t.id); } catch (x) { toast('Suppression impossible pour le moment.', true); return; }
      if (root) { renderPanel(); toast('Modèle supprimé'); }
      return;
    }
    const mt = ev.target.closest('[data-mytpl]'); if (mt) { const t = myTpl.find(x => x.id === mt.dataset.mytpl); if (t) loadDoc(t); return; }
    const t = ev.target.closest('[data-tpl]');
    if (t) { const l = tplIndexes(tplCat); openPreview({ list: l, at: l.indexOf(+t.dataset.tpl), useLabel: 'Remplacer la page', use: i => { closePreview(); loadTpl(TPL[i]); } }); }
  }
  // vignette d'un modèle de « Mes modèles » (même rendu que l'écran)
  function myThumb(t) {
    let d = null; try { d = JSON.parse(t.doc); } catch (x) { d = null; }
    if (!d || !(d.w > 0) || !(d.h > 0)) return '';
    const tw = d.w >= d.h ? 136 : 77, s = tw / d.w;
    return `<div class="ce-thumb ce-mythumb" data-mytpl="${esc(t.id)}" role="button" tabindex="0"><div class="tv" style="height:${Math.round(d.h * s)}px;width:${tw}px"><div class="mini" style="width:${d.w}px;height:${d.h}px;transform:scale(${s});background:${LC().bg(d)}">${LC().html(d, { mediaUrl, player: true })}</div></div><div class="tn"><span>${esc(t.name)}</span><button type="button" class="ce-del" data-deltpl="${esc(t.id)}" title="Supprimer ce modèle" aria-label="Supprimer le modèle ${esc(t.name)}">×</button></div></div>`;
  }
  // part d'un modèle de « Mes modèles » : la page en cours est remplacée (Ctrl+Z revient en arrière)
  function loadDoc(t) {
    let d = null; try { d = JSON.parse(t.doc); } catch (x) { d = null; }
    if (!d || !Array.isArray(d.els)) { toast('Ce modèle est illisible.', true); return; }
    D = { v: 1, w: d.w, h: d.h, bg: d.bg || '#FFFFFF', els: d.els };
    uid = 1 + D.els.reduce((m, e) => Math.max(m, parseInt(String(e.id || '').replace(/\D/g, ''), 10) || 0), 0);
    D.els.forEach(e => { if (!e.id) e.id = nid(); });
    selId = null; zoom = 1; commit(); renderCtx();
    D.els.filter(e => e.type === 'qr').forEach(e => fetchQr(e));
    toast(`Modèle « ${t.name} » appliqué`);
  }
  function openTemplateDialog() {
    q('#ce-tplname').value = (q('#ce-name').value || name || '').trim();
    q('#ce-tplscrim').hidden = false;
    q('#ce-tplname').focus(); q('#ce-tplname').select();
  }
  async function saveTemplate() {
    const n = q('#ce-tplname').value.trim();
    if (!n) { toast('Donnez un nom au modèle.', true); return; }
    let r;
    try { r = await net.invokeMethodAsync('SaveTemplate', n, JSON.stringify(D)); }
    catch (x) { toast('Enregistrement du modèle impossible : la connexion au serveur est interrompue.', true); return; }
    if (!root) return;
    if (r && r.error) { toast(r.error, true); return; }
    myTpl = (r && r.templates) || myTpl;
    q('#ce-tplscrim').hidden = true;
    if (panelName === 'modeles') renderPanel();
    toast(`Modèle « ${n} » ajouté à Mes modèles`);
  }
  function loadTpl(t) {
    D = { v: 1, w: t.w, h: t.h, bg: t.bg, els: t.make() };
    selId = null; zoom = 1; commit(); renderCtx();
    D.els.filter(e => e.type === 'qr').forEach(e => fetchQr(e));
    toast(`Modèle « ${t.name} » appliqué`);
  }

  // ---------- calques ----------
  function icon(e) {
    if (e.type === 'app') return (LC().SRC[e.app] || {}).icon || '◇';
    if (e.type === 'list') return '▦';
    return e.type === 'text' ? 'T' : e.type === 'image' ? '▨' : e.type === 'qr' ? '▣' : e.type === 'widget' ? ({ clock: '◷', date: '▦', ticker: '⇢', countdown: '⏳' })[e.kind] || '◷'
      : ({ circle: '●', line: '—', arrow: '→' })[e.kind] || '■';
  }
  function label(e) {
    if (e.type === 'text') return String(e.text || '').replace(LC().TOKEN, (m, id, key) => LC().fieldText(id, key, opts({ names: true }))).replace(/\n/g, ' ') || 'Texte';
    if (e.type === 'app') { const s = sources.find(x => x.id === e.source); return 'Bloc · ' + (s ? s.name : (LC().SRC[e.app] || {}).name || 'intégration'); }
    if (e.type === 'list') { const s = sources.find(x => x.id === e.source); return 'Liste · ' + (e.name || (s ? s.name : 'jeu introuvable')); }
    if (e.type === 'image') { const m = images.find(x => x.id === e.media); return 'Image' + (m ? ' · ' + m.name : ' à choisir'); }
    if (e.type === 'qr') return 'QR code';
    if (e.type === 'widget') return WNAMES[e.kind] || 'Widget';
    return ({ circle: 'Cercle', line: 'Ligne', arrow: 'Flèche' })[e.kind] || (e.radius ? 'Rectangle arrondi' : 'Rectangle');
  }
  function toggleLayers() { layersOpen = !layersOpen; q('#ce-layers').classList.toggle('closed', !layersOpen); q('#ce-lybtn').classList.toggle('on', layersOpen); render(); }
  function renderLayers() {
    if (!layersOpen) return;
    q('#ce-layers').innerHTML = `<div class="ce-lh"><b>Calques</b><span>${D.els.length} élément${D.els.length > 1 ? 's' : ''}</span><button class="ce-tb" data-top="layers" aria-label="Fermer les calques">×</button></div>` +
      [...D.els].reverse().map(e => `<div class="ce-ly${e.id === selId ? ' on' : ''}${e.hidden ? ' hid' : ''}" data-id="${esc(e.id)}" draggable="true"><span class="ic">${icon(e)}</span><span class="nm">${esc(label(e))}</span>
        <button data-ly="hidden" class="${e.hidden ? 'on' : ''}" title="${e.hidden ? 'Afficher' : 'Masquer'}">${e.hidden ? '◌' : '👁'}</button><button data-ly="locked" class="${e.locked ? 'on' : ''}" title="${e.locked ? 'Déverrouiller' : 'Verrouiller'}">${e.locked ? '🔒' : '🔓'}</button></div>`).join('') +
      '<p class="ce-lnote">Glissez une ligne pour changer l\'ordre. Le calque du haut s\'affiche au premier plan. Un calque masqué n\'est pas diffusé.</p>';
  }

  // ---------- format ----------
  function openFormat(isNew) {
    fmtNew = isNew;
    fmtO = isNew ? 'land' : D.w * 9 === D.h * 16 ? 'land' : D.h * 9 === D.w * 16 ? 'port' : 'custom';
    q('#ce-fmttitle').textContent = isNew ? 'Nouvelle composition' : 'Format de la composition';
    q('#ce-fmtok').textContent = isNew ? 'Créer' : 'Appliquer';
    q('#ce-startfield').style.display = isNew ? '' : 'none';
    q('#ce-fmthelp').style.display = isNew ? 'none' : '';
    if (fmtO === 'custom') { q('#ce-cw').value = D.w; q('#ce-ch').value = D.h; }
    fmtUI();
    q('#ce-fmtscrim').hidden = false;
  }
  function fmtUI() {
    root.querySelectorAll('.ce-ori').forEach(b => b.classList.toggle('on', b.dataset.o === fmtO));
    const c = fmtO === 'custom';
    q('#ce-resfield').style.display = c ? 'none' : ''; q('#ce-customfield').style.display = c ? '' : 'none';
    if (!c) q('#ce-res').innerHTML = RES[fmtO].map(([ww, hh, l], i) => `<option value="${i}" ${!fmtNew && ww === D.w && hh === D.h ? 'selected' : ''}>${l}</option>`).join('');
  }
  function closeFormat() { q('#ce-fmtscrim').hidden = true; }
  function applyFormat() {
    let ww, hh;
    if (fmtO === 'custom') { ww = Math.min(7680, Math.max(320, +q('#ce-cw').value || 0)); hh = Math.min(7680, Math.max(320, +q('#ce-ch').value || 0)); }
    else [ww, hh] = RES[fmtO][+q('#ce-res').value];
    closeFormat();
    if (fmtNew) {
      const tpl = (root.querySelector('input[name=ce-start]:checked') || {}).value === 'tpl';
      D.w = ww; D.h = hh; selId = null; zoom = 1; commit(); renderCtx();
      if (tpl) { tplCat = 'Tous'; openPanel('modeles'); } else openPanel('texte');
      return;
    }
    if (Math.abs(ww / hh - D.w / D.h) < .01) {   // même proportions : tout suit (positions, tailles, textes)
      const f = ww / D.w;
      D.els.forEach(e => { ['x', 'y', 'w', 'h'].forEach(p => e[p] = Math.round(e[p] * f)); if (e.size) e.size = Math.round(e.size * f); if (e.sw) e.sw = Math.max(1, Math.round(e.sw * f)); if (e.radius) e.radius = Math.round(e.radius * f); });
    }
    D.w = ww; D.h = hh; commit();
  }

  // ---------- aperçu, divers ----------
  function openPreview() {
    const s = Math.min(innerWidth / D.w, innerHeight / D.h), pf = q('#ce-pf');
    pf.style.cssText = `width:${D.w * s}px;height:${D.h * s}px;left:${(innerWidth - D.w * s) / 2}px;top:${(innerHeight - D.h * s) / 2}px;background:${LC().bg(D)}`;
    pf.innerHTML = `<div class="lc-stage" style="width:${D.w}px;height:${D.h}px;transform:scale(${s});background:${LC().bg(D)}">${LC().html(D, opts({ player: true, names: false }))}</div>`;
    loadImages(pf);
    q('#ce-pvw').hidden = false;
  }
  function closePreview() { q('#ce-pvw').hidden = true; q('#ce-pf').innerHTML = ''; }
  function setZoom(v) { zoom = Math.min(3, Math.max(.3, Math.round(v * 10) / 10)); render(); }
  let tt;
  function toast(m, err) {
    const t = q('#ce-toast'); if (!t) return;
    t.textContent = m; t.classList.toggle('err', !!err); t.classList.add('on');
    clearTimeout(tt); tt = setTimeout(() => t.classList.remove('on'), err ? 5000 : 2600);
  }

  // ---------- aperçu d'un modèle (§115) : fenêtre centrée, page vivante, « page seule » ou « sur un écran », images d'exemple ----------
  // openPreview({ list: numéros de modèles parcourus, at: position, useLabel, use(numéro) }) ; depuis la galerie et depuis le panneau « Modèles » de l'éditeur.
  let pv = null, pvSamples = true;
  const tplIndexes = cat => TPL.map((t, i) => i).filter(i => cat === 'Tous' || TPL[i].cat === cat);
  const PV_PAL = [['#F2C18D', '#C8693F', '#7A3B2A'], ['#9DC7E8', '#4F86B8', '#24476B'], ['#B9D8A8', '#5E9B63', '#2D5B3A'], ['#E7B7C6', '#B9677F', '#6B2F45'], ['#C9C3E8', '#7C70B8', '#3D3478'], ['#F0DD9A', '#D4A23C', '#7A5A14']];
  // visuel d'exemple dessiné (paysage stylisé) : sert à l'aperçu seulement, jamais enregistré avec le modèle
  function sampleImg(t, e) {
    const p = PV_PAL[(t.name.length + e.w) % PV_PAL.length];
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 600" preserveAspectRatio="xMidYMid slice"><defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${p[0]}"/><stop offset="1" stop-color="${p[1]}"/></linearGradient></defs><rect width="800" height="600" fill="url(#g)"/><circle cx="560" cy="170" r="70" fill="#fff" opacity=".55"/><path d="M0 600V400L190 250 360 420 520 300 800 470V600Z" fill="${p[2]}" opacity=".5"/><path d="M0 600V470L230 360 430 500 640 400 800 520V600Z" fill="${p[2]}" opacity=".8"/></svg>`;
    return 'data:image/svg+xml,' + encodeURIComponent(svg);
  }
  const PV_LIVE = { clock: ['◷', 'heure en direct'], date: ['▦', 'mise à jour à minuit'], ticker: ['⇢', 'texte qui défile'], countdown: ['⏳', 'jours restants'] };
  function pvRows(t) {
    const els = t.make(), c = { text: 0, shape: 0, image: 0, qr: 0 }, live = [];
    els.forEach(e => { if (e.type === 'widget') { if (!live.includes(e.kind)) live.push(e.kind); } else c[e.type] = (c[e.type] || 0) + 1; });
    const row = (ic, cls, b, s, n) => `<div class="ce-pvr"><span class="ci ${cls}">${ic}</span><div><b>${b}</b>${s ? `<small>${s}</small>` : ''}</div>${n ? `<span class="n">${n}</span>` : ''}</div>`;
    return row('T', '', 'Textes', '', c.text) + (c.shape ? row('▢', '', 'Formes et fonds', '', c.shape) : '') +
      (c.image ? row(ICO.img, 'ph', 'Images à choisir', 'dans votre médiathèque', c.image) : '') + (c.qr ? row('▣', '', 'QR code', "lien à régler dans l'éditeur", c.qr) : '') +
      live.map(k => row(PV_LIVE[k][0], 'live', WNAMES[k], PV_LIVE[k][1] + ' · en direct', '')).join('');
  }
  function openPreview(o) {
    closePreview();
    if (!o.list.length || o.at < 0) return;
    LC().ensure();
    const ov = document.createElement('div');
    ov.className = 'ce-pv';
    ov.innerHTML = `<div class="ce-pvd" role="dialog" aria-modal="true" aria-labelledby="ce-pvt">
      <div class="ce-pvh"><div class="ce-pvtt"><h2 id="ce-pvt"></h2><div class="ce-pvm"></div></div>
        <div class="ce-pvseg" role="group" aria-label="Affichage"><button type="button" data-v="page">Page seule</button><button type="button" data-v="screen">Sur un écran</button></div>
        <button type="button" class="ce-pvx" data-pv="close" aria-label="Fermer l'aperçu">×</button></div>
      <div class="ce-pvb"><div class="ce-pvs"><div class="ce-pva"><div class="ce-pvho"></div></div>
          <button type="button" class="ce-pvn prev" data-pv="prev" aria-label="Modèle précédent">‹</button><button type="button" class="ce-pvn next" data-pv="next" aria-label="Modèle suivant">›</button><div class="ce-pvc"></div></div>
        <aside class="ce-pvi"><h3>Ce que contient ce modèle</h3><div class="ce-pvl"></div>
          <div class="ce-pvbx">Tout reste modifiable après coup (textes, couleurs, format). Rien n'est enregistré avant « Enregistrer ».</div>
          <div class="ce-pvsw"><button type="button" class="ce-pvtg" data-pv="samples" role="switch" aria-label="Images d'exemple"></button><div><b>Images d'exemple</b><small>Pour l'aperçu seulement. À la création, les emplacements restent « Image à choisir » : vous mettez vos photos.</small></div></div>
          <div class="ce-pvcta"><button type="button" class="ce-pvbtn" data-pv="use"></button><button type="button" class="ce-pvbtn ghost" data-pv="close">Fermer</button></div></aside></div>
      <div class="ce-pvmob"><button type="button" class="ce-pvbtn ghost" data-pv="close">Fermer</button><button type="button" class="ce-pvbtn" data-pv="use"></button></div></div>`;
    document.body.appendChild(ov);
    pv = { ov, o, pos: o.at, view: 'page', from: document.activeElement, timer: setInterval(() => LC().tick(ov.querySelector('.ce-pvho')), 1000) };
    ov.querySelectorAll('[data-pv=use]').forEach(b => { b.textContent = o.useLabel || 'Utiliser ce modèle'; });
    pv.keys = ev => {
      ev.stopImmediatePropagation();   // l'éditeur dessous ne doit pas réagir (flèches, Suppr, Ctrl+Z)
      if (ev.key === 'Escape') { ev.preventDefault(); closePreview(); }
      else if (ev.key === 'ArrowRight') { ev.preventDefault(); pvGo(1); }
      else if (ev.key === 'ArrowLeft') { ev.preventDefault(); pvGo(-1); }
      else if (ev.key === 'Tab') {   // le focus reste dans la fenêtre
        const f = [...ov.querySelectorAll('button:not(:disabled)')].filter(b => b.offsetParent), a = document.activeElement;
        if (!f.length) return;
        if (!ov.contains(a)) { ev.preventDefault(); f[0].focus(); }
        else if (ev.shiftKey && a === f[0]) { ev.preventDefault(); f[f.length - 1].focus(); }
        else if (!ev.shiftKey && a === f[f.length - 1]) { ev.preventDefault(); f[0].focus(); }
      }
    };
    pv.fit = () => pvFit();
    document.addEventListener('keydown', pv.keys, true);
    addEventListener('resize', pv.fit);
    ov.addEventListener('click', ev => {
      if (ev.target === ov) { closePreview(); return; }
      const v = ev.target.closest('[data-v]'); if (v) { pv.view = v.dataset.v; pvShow(); return; }
      const b = ev.target.closest('[data-pv]'); if (!b) return;
      const a = b.dataset.pv;
      if (a === 'close') closePreview();
      else if (a === 'prev') pvGo(-1);
      else if (a === 'next') pvGo(1);
      else if (a === 'samples') { pvSamples = !pvSamples; pvShow(); }
      else if (a === 'use') pv.o.use(pv.o.list[pv.pos]);
    });
    pvShow();
    ov.querySelector('.ce-pvx').focus();
  }
  function closePreview() {
    if (!pv) return;
    const p = pv; pv = null;
    clearInterval(p.timer);
    document.removeEventListener('keydown', p.keys, true);
    removeEventListener('resize', p.fit);
    p.ov.remove();
    if (p.from && p.from.isConnected && p.from.focus) p.from.focus();
  }
  function pvGo(d) { if (!pv) return; const n = pv.pos + d; if (n >= 0 && n < pv.o.list.length) { pv.pos = n; pvShow(); } }
  function pvShow() {
    const ov = pv.ov, l = pv.o.list, t = TPL[l[pv.pos]];
    ov.querySelector('#ce-pvt').textContent = t.name;
    ov.querySelector('.ce-pvm').innerHTML = `<span class="pill blue">${esc(t.cat)}</span><span class="pill">${t.w} × ${t.h} · ${t.w >= t.h ? 'Paysage' : 'Portrait'}</span>`;
    ov.querySelectorAll('.ce-pvseg button').forEach(b => { const on = b.dataset.v === pv.view; b.classList.toggle('on', on); b.setAttribute('aria-pressed', on); });
    ov.querySelector('.ce-pvs').classList.toggle('wall', pv.view === 'screen');
    ov.querySelector('.ce-pvc').innerHTML = `${pv.pos + 1} / ${l.length}<span class="ce-pvk"> · flèches du clavier pour parcourir · Échap pour fermer</span>`;
    ov.querySelector('.ce-pvn.prev').disabled = pv.pos === 0;
    ov.querySelector('.ce-pvn.next').disabled = pv.pos === l.length - 1;
    const tg = ov.querySelector('.ce-pvtg'); tg.classList.toggle('on', pvSamples); tg.setAttribute('aria-checked', pvSamples);
    const els = t.make();
    if (pvSamples) els.forEach(e => { if (e.type === 'image' && !e.media) e.src = sampleImg(t, e); });
    const inner = `<div class="st" style="width:${t.w}px;height:${t.h}px;background:${t.bg}">${LC().html({ els }, {})}</div>`;
    ov.querySelector('.ce-pvho').innerHTML = pv.view === 'page' ? `<div class="ce-pvpg">${inner}</div>`
      : `<div class="ce-pvtv"><div class="ce-pvbz"><div class="ce-pvsc">${inner}</div></div><div class="ce-pvst"></div></div>`;
    LC().images(ov.querySelector('.ce-pvho'), (u, cb) => cb(u));
    ov.querySelector('.ce-pvl').innerHTML = pvRows(t);
    pvFit();
  }
  function pvFit() {
    if (!pv) return;
    const ov = pv.ov, t = TPL[pv.o.list[pv.pos]], a = ov.querySelector('.ce-pva'), phone = matchMedia('(max-width:720px)').matches;
    const aw = a.clientWidth - (phone ? 24 : 130), ah = a.clientHeight - (phone ? 40 : 56);
    const b = pv.view === 'screen' ? (phone ? 8 : 14) : 0, stand = pv.view === 'screen' ? (phone ? 16 : 24) : 0;
    const s = Math.max(.05, Math.min((aw - 2 * b) / t.w, (ah - 2 * b - stand) / t.h)), w2 = t.w * s, h2 = t.h * s;
    ov.querySelector('.ce-pvho .st').style.transform = `scale(${s})`;
    const box = ov.querySelector(pv.view === 'page' ? '.ce-pvpg' : '.ce-pvsc');
    box.style.width = w2 + 'px'; box.style.height = h2 + 'px';
    if (pv.view === 'screen') ov.querySelector('.ce-pvbz').style.padding = b + 'px';
  }

  // Galerie de modèles de la page Compositions : dotnet.Template(index) ouvre l'éditeur sur le modèle choisi.
  let galCat = 'Tous';
  function gallery(el, dotnet) {
    LC().ensure(); ensureSamples();
    const fitGal = () => el.querySelectorAll('.ce-mc .tv').forEach(tv => { const m = tv.firstElementChild; if (tv.clientWidth) m.style.transform = `scale(${tv.clientWidth / +m.dataset.w})`; });
    const draw = () => {
      const cats = ['Tous', ...new Set(TPL.map(t => t.cat))];
      el.innerHTML = `<div class="ce-chips">${cats.map(c => `<button type="button" data-cat="${c}" class="${c === galCat ? 'on' : ''}">${c}</button>`).join('')}</div>
        <div class="ce-tgrid ce-gal">${tplIndexes(galCat).map(i => thumb(TPL[i], i, true)).join('')}</div>`;
      fitGal();
    };
    const open = i => { const l = tplIndexes(galCat); openPreview({ list: l, at: l.indexOf(i), use: n => { closePreview(); dotnet.invokeMethodAsync('Template', n); } }); };
    el.onclick = ev => {
      const c = ev.target.closest('[data-cat]'); if (c) { galCat = c.dataset.cat; draw(); return; }
      const t = ev.target.closest('[data-tpl]'); if (!t) return;
      const i = +t.dataset.tpl;
      if (ev.target.closest('[data-act=use]')) dotnet.invokeMethodAsync('Template', i); else open(i);
    };
    el.onkeydown = ev => { const t = ev.target.closest && ev.target.closest('.ce-mc'); if (t && ev.target === t && (ev.key === 'Enter' || ev.key === ' ')) { ev.preventDefault(); open(+t.dataset.tpl); } };
    if (typeof ResizeObserver === 'function') new ResizeObserver(fitGal).observe(el);
    draw();
  }

  // Import d'images (CompositionEditor.razor, InputFile) : le serveur prévient au début et à la fin
  function importing(n) { if (root) toast(`Import de ${n} image${n > 1 ? 's' : ''} en cours…`); }
  function imported(list, ids, errors) {
    if (!root) return;
    images = list || images;
    if (panelName === 'medias') renderMediaGrid();
    render();
    if (ids && ids.length) addEl('img:' + ids[0]);   // la première image importée se place (ou remplace, selon le mode du panneau)
    const msg = (ids && ids.length ? `${ids.length} image${ids.length > 1 ? 's' : ''} importée${ids.length > 1 ? 's' : ''} dans la médiathèque.` : '') + ((errors || []).length ? ' ' + errors.join(' ') : '');
    if (msg.trim()) toast(msg.trim(), !(ids && ids.length));
  }

  w.LinkiiCompoEditor = { mount, unmount, gallery, importing, imported };
})(window);
