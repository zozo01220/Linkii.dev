/* Éditeur de liste interactive (DEV-POC.md §168) : l'environnement tactile d'une borne, en pages (accueil + sous-pages), avec des tuiles, des textes et des images
   placés librement. Les tuiles ouvrent un contenu de la médiathèque ou une autre page. Interface en JS plein écran comme l'éditeur de compositions ;
   le serveur (InteractiveEditor.razor) garde le document. Tailles du texte : en % de la largeur de la page ; position et taille : en % de la page. */
(function (w) {
  'use strict';
  var S = null, net = null, root = null, ac = null;
  var $ = function (sel, el) { return (el || root).querySelector(sel); };
  var esc = function (s) { var d = document.createElement('div'); d.textContent = s == null ? '' : s; return d.innerHTML; };
  var uid = function () { return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) { var r = Math.random() * 16 | 0; return (c === 'x' ? r : (r & 3 | 8)).toString(16); }); };
  var clone = function (o) { return JSON.parse(JSON.stringify(o)); };
  var PAL = ['#3B82C4', '#2E7D5B', '#C77700', '#8A5CF6', '#D0453C', '#5A6B80', '#0B1F3A', '#FFFFFF'];
  var BGS = ['#0B1F3A', '#16335C', '#101822', '#FFFFFF', '#F5F7FA', '#2E7D5B', '#3A2A1C', '#8A5CF6'];
  var FONTS = [['', 'Par défaut'], ['arial', 'Arial'], ['verdana', 'Verdana'], ['trebuchet', 'Trebuchet MS'], ['georgia', 'Georgia'], ['times', 'Times New Roman'], ['courier', 'Courier New'], ['impact', 'Impact']];
  var FCSS = { arial: 'Arial,Helvetica,sans-serif', verdana: 'Verdana,Geneva,sans-serif', trebuchet: "'Trebuchet MS',Tahoma,sans-serif", georgia: "Georgia,'Times New Roman',serif", times: "'Times New Roman',Times,serif", courier: "'Courier New',Courier,monospace", impact: "Impact,'Arial Narrow Bold',sans-serif" };
  var SIZES_TILE = [['Petit', 2.4], ['Normal', 3.4], ['Grand', 4.6], ['Très grand', 6]], SIZES_TEXT = [['Petit', 2.2], ['Normal', 3.4], ['Grand', 6], ['Très grand', 9]];
  var CATS = [['media', 'Médias'], ['pdf', 'PDF'], ['pres', 'Présentations'], ['compo', 'Compositions'], ['web', 'Pages web']];
  var ICO = {
    back: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M19 12H5M11 6l-6 6 6 6"/></svg>',
    tile: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><rect x="4" y="4" width="16" height="16" rx="4"/></svg>',
    text: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><path d="M5 6V4h14v2M12 4v16M9 20h6"/></svg>',
    img: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="9" cy="10" r="2"/><path d="m21 16-5-5-9 9"/></svg>',
    btn: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><rect x="3" y="8" width="18" height="8" rx="4"/></svg>',
    bg: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><circle cx="12" cy="12" r="9"/><path d="M12 3v18M3 12h18" opacity=".4"/></svg>',
    play: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M7 4v16l13-8Z"/></svg>',
    dup: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><rect x="8" y="8" width="12" height="12" rx="2"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"/></svg>',
    del: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3"/></svg>',
    hand: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M9 11V5.5a1.5 1.5 0 0 1 3 0V10m0-1.5a1.5 1.5 0 0 1 3 0V11m0-1a1.5 1.5 0 0 1 3 0v5.5A6 6 0 0 1 12 21.5h-.5a6 6 0 0 1-4.7-2.3L4 15.6a1.6 1.6 0 0 1 2.4-2.1L9 15.5"/></svg>',
    more: '<svg viewBox="0 0 24 24" fill="currentColor"><circle cx="12" cy="5" r="1.8"/><circle cx="12" cy="12" r="1.8"/><circle cx="12" cy="19" r="1.8"/></svg>'
  };

  /* ---------- Document ---------- */
  function page() { return S.pages[S.cur]; }
  function elOf(id) { var p = page(); for (var i = 0; i < p.els.length; i++) if (p.els[i].id === id) return p.els[i]; return null; }
  function aspect() { return S.orientation === 'portrait' ? 9 / 16 : 16 / 9; }   // largeur / hauteur de la page
  function dirty() { S.dirty = true; var b = $('#ie-dirty'); if (b) b.textContent = 'Modifications non enregistrées'; }
  function img(id) { for (var i = 0; i < S.images.length; i++) if (S.images[i].id === id) return S.images[i]; return null; }
  function content(id) { for (var i = 0; i < S.contents.length; i++) if (S.contents[i].id === id) return S.contents[i]; return null; }
  function newEl(kind, o) {
    var e = { id: uid(), kind: kind, x: 10, y: 10, w: 30, h: 15, text: '', sub: null, color: '#3B82C4', imageId: null, textColor: '#FFFFFF', font: null, fontSize: 3.4, bold: false, align: 'left', valign: 'bottom', radius: 1.6, showFrom: null, showTo: null, action: { type: 'none', mediaId: null, pageId: null, item: { qrOn: false } } };
    for (var k in o) e[k] = o[k];
    return e;
  }
  function hIn(wPct) { return wPct * aspect(); }   // une longueur en % de la largeur, en % de la hauteur
  function place(e) {   // décale un nouvel élément pour qu'il ne se pose pas pile sur le précédent
    var n = page().els.length % 6;
    e.x = Math.max(0, Math.min(100 - e.w, e.x + n * 2)); e.y = Math.max(0, Math.min(100 - e.h, e.y + n * 2));
    return e;
  }
  function add(e) { page().els.push(e); S.sel = e.id; S.multi = []; dirty(); draw(); return e; }
  function addTile(o) { return add(place(newEl('tile', Object.assign({ x: 8, y: 30, w: 40, h: hIn(40) * 0.9, text: 'Nouvelle tuile', color: PAL[page().els.filter(function (x) { return x.kind === 'tile'; }).length % 7] }, o || {})))); }
  function lowest() { var m = 0; page().els.forEach(function (e) { m = Math.max(m, e.y + e.h); }); return m; }
  function layout(n, cols) {
    var gap = 3, cw = (90 - gap * (cols - 1)) / cols, rows = Math.ceil(n / cols), y0 = Math.min(lowest() + 2, 70);
    var ch = Math.min((97 - y0 - hIn(gap) * (rows - 1)) / rows, hIn(cw) * 1.1);
    for (var i = 0; i < n; i++) page().els.push(newEl('tile', { x: 5 + (i % cols) * (cw + gap), y: y0 + Math.floor(i / cols) * (ch + hIn(gap)), w: cw, h: ch, text: 'Tuile ' + (i + 1), color: PAL[i % 7] }));
    S.sel = null; S.multi = []; dirty(); draw();
  }

  /* ---------- Rendu d'un élément (scène, vignette, aperçu) : W = largeur de la page en pixels ---------- */
  function fontCss(k) { return k && FCSS[k] ? FCSS[k] : ''; }
  function elNode(e, W, live, len) {
    len = len || 1;
    var n, px = function (pct) { return (pct / 100 * W) + 'px'; };
    if (e.kind === 'image') {
      n = document.createElement('div'); var im = img(e.imageId);
      n.className = 'ie-el ie-img'; if (im) n.style.backgroundImage = 'url(' + im.url + ')'; else n.classList.add('empty');
    } else if (e.kind === 'text') {
      n = document.createElement('div'); n.className = 'ie-el ie-txt'; n.style.color = e.textColor;
      n.style.alignItems = e.align === 'center' ? 'center' : e.align === 'right' ? 'flex-end' : 'flex-start';
      var t = document.createElement('span'); t.className = 'ie-t'; t.textContent = e.text || (live ? '' : 'Texte'); n.appendChild(t);
    } else {
      n = document.createElement('div'); n.className = 'ie-el ie-tile'; n.style.background = e.color;
      var im2 = e.imageId ? img(e.imageId) : null;
      if (im2) { n.style.backgroundImage = 'url(' + im2.url + ')'; n.classList.add('shade'); }
      n.style.color = e.textColor; n.style.justifyContent = e.valign === 'top' ? 'flex-start' : e.valign === 'middle' ? 'center' : 'flex-end';
      n.style.borderRadius = px(e.radius); n.style.padding = px(e.fontSize * 0.7) + ' ' + px(e.fontSize * 0.8);
      var b = document.createElement('b'); b.className = 'ie-t'; b.textContent = e.text || ''; n.appendChild(b);
      if (e.sub) { var s = document.createElement('small'); s.textContent = e.sub; s.style.fontSize = '.56em'; n.appendChild(s); }
      n.style.textAlign = e.align;
      if (live && e.action && e.action.type !== 'none') n.style.cursor = 'pointer';
    }
    n.dataset.id = e.id;
    n.style.left = e.x + '%'; n.style.top = (e.y / len) + '%'; n.style.width = e.w + '%'; n.style.height = (e.h / len) + '%';
    n.style.fontSize = px(e.fontSize); n.style.fontWeight = e.bold ? 700 : 500;
    var f = fontCss(e.font); if (f) n.style.fontFamily = f;
    return n;
  }
  function pageNode(p, W, live) {
    var len = p.len || 1, H = W / aspect() * len, d = document.createElement('div');
    d.className = 'ie-page'; d.style.width = W + 'px'; d.style.height = H + 'px'; d.style.background = p.bg;
    if (p.bgImageId && img(p.bgImageId)) { d.style.backgroundImage = 'url(' + img(p.bgImageId).url + ')'; d.style.backgroundSize = 'cover'; d.style.backgroundPosition = 'center'; }
    p.els.forEach(function (e) { d.appendChild(elNode(e, W, live, len)); });
    for (var k = 1; k < len; k++) { var f = document.createElement('i'); f.className = 'ie-fold'; f.style.top = (k * 100 / len) + '%'; f.dataset.l = 'Bas de l\u2019écran ' + k; d.appendChild(f); }   // repères : ce que voit le visiteur sans faire défiler
    return d;
  }

  /* ---------- Fenêtre ---------- */
  var SHELL =
    '<div class="ie-top">' +
    '<button class="ie-tb ie-back" id="ie-back" title="Retour" aria-label="Retour">' + ICO.back + '</button>' +
    '<input class="ie-name" id="ie-name" maxlength="80" aria-label="Nom de la liste interactive" placeholder="Nom de la liste">' +
    '<button class="ie-fmt" id="ie-fmt"></button>' +
    '<span class="ie-sep"></span>' +
    '<button class="ie-tb" id="ie-bg">' + ICO.bg + ' Fond et longueur</button>' +
    '<span class="ie-grow"></span><span class="ie-dirty" id="ie-dirty"></span>' +
    '<button class="ie-tb" id="ie-prev">' + ICO.play + ' Aperçu tactile</button>' +
    '<button class="ie-btn ghost" id="ie-cancel">Annuler</button><button class="ie-btn" id="ie-save">Enregistrer</button>' +
    '</div>' +
    '<div class="ie-main">' +
    '<div class="ie-rail" id="ie-rail">' +
    '<button data-t="tile">' + ICO.tile + 'Tuile</button><button data-t="text">' + ICO.text + 'Texte</button><button data-t="image">' + ICO.img + 'Image</button><button data-t="button">' + ICO.btn + 'Bouton</button>' +
    '</div>' +
    '<div class="ie-panel" id="ie-panel"></div>' +
    '<div class="ie-center" id="ie-center"><div class="ie-ctx" id="ie-ctx"></div><div class="ie-stagewrap" id="ie-stagewrap"></div></div>' +
    '</div>' +
    '<div class="ie-pages" id="ie-pages"></div>' +
    '<div class="ie-toast" id="ie-toast" role="status" aria-live="polite"></div>';

  function mount(el, o, dn) {
    unmount();
    root = el; net = dn; ac = new AbortController();
    var doc = {}; try { doc = JSON.parse(o.doc); } catch (x) { doc = {}; }
    S = {
      id: o.id || null, name: o.name || '', orientation: doc.orientation === 'landscape' ? 'landscape' : 'portrait',
      pages: (doc.pages && doc.pages.length ? doc.pages : [{ id: uid(), name: 'Accueil', bg: '#0B1F3A', len: 1, els: [] }]),
      cur: 0, sel: null, multi: [], tool: 'tile', dirty: false, images: o.images || [], contents: o.contents || [], growth: !!o.growth, saving: false, pop: null
    };
    S.pages.forEach(function (p) { p.els = p.els || []; p.len = p.len || 1; p.els.forEach(function (e) { e.action = e.action || { type: 'none', item: { qrOn: false } }; e.action.item = e.action.item || { qrOn: false }; }); });
    root.className = 'ie'; root.innerHTML = SHELL;
    $('#ie-name').value = S.name;
    bind(); draw();
    if (!S.id && !o.doc) { /* nouvelle liste : le nom reste à donner */ $('#ie-name').focus(); }
  }
  function unmount() {
    closePop(); if (ac) ac.abort();
    if (root) { root.innerHTML = ''; root.className = ''; }
    root = null; net = null; ac = null; S = null;
  }

  /** L’assistant a enregistré cette liste : l’éditeur reprend sa version (après accord s’il y a des modifications non enregistrées). */
  function replaced(docJson, name, images, contents) {
    if (!S || !root) return;
    if (S.dirty && !confirm('L’assistant a modifié cette liste. Charger sa version ? Vos modifications non enregistrées seront perdues.')) { toast('Version de l’assistant non chargée : si vous enregistrez, la vôtre la remplacera.', true); return; }
    var doc = {}; try { doc = JSON.parse(docJson); } catch (x) { return; }
    if (!doc.pages || !doc.pages.length) return;
    var curId = page() && page().id;
    closePop(); closePreview();
    S.name = name || S.name; S.orientation = doc.orientation === 'landscape' ? 'landscape' : 'portrait'; S.pages = doc.pages;
    S.pages.forEach(function (p) { p.els = p.els || []; p.len = p.len || 1; p.els.forEach(function (e) { e.action = e.action || { type: 'none', item: { qrOn: false } }; e.action.item = e.action.item || { qrOn: false }; }); });
    if (images) S.images = images; if (contents) S.contents = contents;
    S.cur = 0; S.pages.forEach(function (p, i) { if (p.id === curId) S.cur = i; });   // on reste sur la page regardée, si elle existe encore
    S.sel = null; S.multi = []; S.dirty = false;
    $('#ie-name').value = S.name; var d = $('#ie-dirty'); if (d) d.textContent = '';
    draw(); toast('Liste mise à jour par l’assistant.');
  }

  function toast(msg, err) {
    var t = $('#ie-toast'); if (!t) return;
    t.textContent = msg; t.className = 'ie-toast on' + (err ? ' err' : '');
    clearTimeout(t._h); t._h = setTimeout(function () { t.className = 'ie-toast'; }, err ? 6000 : 3000);
  }

  function leave() { if (!S.dirty || confirm('Quitter sans enregistrer les modifications ?')) { net.invokeMethodAsync('Leave'); } }
  function save() {
    if (S.saving) return;
    S.name = $('#ie-name').value.trim();
    if (!S.name) { toast('Donnez un nom à la liste interactive.', true); $('#ie-name').focus(); return; }
    S.saving = true; $('#ie-save').disabled = true;
    net.invokeMethodAsync('Save', S.name, JSON.stringify({ orientation: S.orientation, pages: S.pages })).then(function (r) {
      S.saving = false; var b = $('#ie-save'); if (b) b.disabled = false;
      if (r && r.error) return toast(r.error, true);
      S.dirty = false; var d = $('#ie-dirty'); if (d) d.textContent = '';
      toast('Liste enregistrée. Publiez les bornes concernées pour l’afficher.');
    }, function () { S.saving = false; var b = $('#ie-save'); if (b) b.disabled = false; toast('Enregistrement impossible : connexion perdue.', true); });
  }

  function bind() {
    var o = { signal: ac.signal };
    $('#ie-back').addEventListener('click', leave, o);
    $('#ie-cancel').addEventListener('click', leave, o);
    $('#ie-save').addEventListener('click', save, o);
    $('#ie-prev').addEventListener('click', openPreview, o);
    $('#ie-name').addEventListener('input', dirty, o);
    $('#ie-fmt').addEventListener('click', function (ev) { ev.stopPropagation(); popAt(ev.currentTarget, fmtPop); }, o);
    $('#ie-bg').addEventListener('click', function (ev) { ev.stopPropagation(); popAt(ev.currentTarget, bgPop); }, o);
    $('#ie-rail').addEventListener('click', function (ev) { var b = ev.target.closest('button'); if (!b) return; S.tool = b.dataset.t; drawPanel(); }, o);
    document.addEventListener('pointerdown', function (ev) { if (S && S.pop && !ev.target.closest('.ie-pop') && !ev.target.closest('[data-pop]')) closePop(); }, Object.assign({ capture: true }, o));
    document.addEventListener('keydown', onKey, o);
    window.addEventListener('resize', function () { drawStage(); }, o);
    window.addEventListener('beforeunload', function (ev) { if (S && S.dirty) { ev.preventDefault(); ev.returnValue = ''; } }, o);
  }

  function onKey(ev) {
    if (!S || !root) return;
    var tag = (ev.target.tagName || '').toLowerCase(), typing = tag === 'input' || tag === 'textarea' || tag === 'select' || ev.target.isContentEditable;
    if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 's') { ev.preventDefault(); save(); return; }
    if (ev.key === 'Escape') { if (S.preview) closePreview(); else closePop(); return; }
    if (typing) return;
    if (S.multi.length > 1) {   // plusieurs éléments sélectionnés : suppression, copie et flèches valent pour tous
      var k = ev.key.toLowerCase();
      if ((ev.ctrlKey || ev.metaKey) && k === 'd') { ev.preventDefault(); dup(); }
      else if (ev.key === 'Delete' || ev.key === 'Backspace') { ev.preventDefault(); remove(); }
      else if (ev.key.indexOf('Arrow') === 0) {
        ev.preventDefault(); var s2 = ev.shiftKey ? 3 : 0.5;
        S.multi.forEach(function (id) { var t = elOf(id); if (!t) return; if (ev.key === 'ArrowLeft') t.x -= s2; else if (ev.key === 'ArrowRight') t.x += s2; else if (ev.key === 'ArrowUp') t.y -= s2; else t.y += s2; });
        dirty(); drawStage();
      }
      return;
    }
    if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 'a') { ev.preventDefault(); setSel(page().els.map(function (x) { return x.id; })); return; }   // Ctrl+A : tout sélectionner
    if (!S.sel) return;
    var e = elOf(S.sel); if (!e) return;
    if (ev.key === 'Delete' || ev.key === 'Backspace') { ev.preventDefault(); remove(); }
    else if ((ev.ctrlKey || ev.metaKey) && ev.key.toLowerCase() === 'd') { ev.preventDefault(); dup(); }
    else if (ev.key.indexOf('Arrow') === 0) {
      ev.preventDefault(); var st = ev.shiftKey ? 3 : 0.5;
      if (ev.key === 'ArrowLeft') e.x -= st; else if (ev.key === 'ArrowRight') e.x += st; else if (ev.key === 'ArrowUp') e.y -= st; else e.y += st;
      dirty(); drawStage();
    }
  }
  function remove() { var p = page(), ids = S.multi.length > 1 ? S.multi : [S.sel]; p.els = p.els.filter(function (x) { return ids.indexOf(x.id) < 0; }); S.sel = null; S.multi = []; dirty(); draw(); }
  /** Sélection : un élément (poignées) ou plusieurs (Ctrl/Cmd/Maj + clic, rectangle à la souris). */
  function setSel(ids) {
    ids = ids.filter(function (id) { return elOf(id); });
    if (ids.length > 1) { S.multi = ids; S.sel = null; } else { S.multi = []; S.sel = ids[0] || null; }
    drawStage(); drawCtx();
  }
  function selIds() { return S.multi.length > 1 ? S.multi.slice() : S.sel ? [S.sel] : []; }
  function dup() {
    if (S.multi.length > 1) {
      var ids = [];
      S.multi.forEach(function (id) { var o = elOf(id); if (!o) return; var c = clone(o); c.id = uid(); c.x = Math.min(c.x + 3, 100 - c.w); c.y = c.y + hIn(3); if (c.action && c.action.item) { delete c.action.item.qrCode; } page().els.push(c); ids.push(c.id); });
      dirty(); draw(); setSel(ids); return;
    }
    var e = elOf(S.sel); if (!e) return; var c = clone(e); c.id = uid(); c.x = Math.min(c.x + 3, 100 - c.w); c.y = Math.min(c.y + hIn(3), 100 - c.h); if (c.action && c.action.item) { delete c.action.item.qrCode; } page().els.push(c); S.sel = c.id; dirty(); draw(); }

  /* ---------- Rendu général ---------- */
  function draw() { drawFmt(); drawPanel(); drawStage(); drawCtx(); drawPages(); }
  function drawFmt() { $('#ie-fmt').innerHTML = (S.orientation === 'portrait' ? '▯ Portrait' : '▭ Paysage') + ' ▾'; $('#ie-fmt').dataset.pop = '1'; $('#ie-bg').dataset.pop = '1'; }

  function drawPanel() {
    var p = $('#ie-panel'), t = S.tool, h = '';
    [].forEach.call($('#ie-rail').children, function (b) { b.classList.toggle('on', b.dataset.t === t); });
    if (t === 'tile') {
      h = '<h4>Tuile</h4><p class="ie-h">Une tuile ouvre un contenu ou une page. Cliquez pour l’ajouter, puis déplacez-la sur la page.</p>' +
        '<button class="ie-pick" data-add="tile-sq"><b>Tuile carrée</b><small>Titre, couleur ou image</small></button>' +
        '<button class="ie-pick" data-add="tile-wide"><b>Tuile large</b><small>Pleine largeur</small></button>' +
        '<div class="ie-lbl">Mises en page prêtes</div>' +
        '<button class="ie-pick" data-add="grid4"><b>Grille 2 × 2</b><small>Quatre tuiles égales</small></button>' +
        '<button class="ie-pick" data-add="list3"><b>Liste verticale</b><small>Trois tuiles l’une sous l’autre</small></button>';
    } else if (t === 'text') {
      h = '<h4>Texte</h4><p class="ie-h">Un texte ne se touche pas : titre, consigne, légende.</p>' +
        '<button class="ie-pick" data-add="title"><b style="font-size:18px">Titre</b></button>' +
        '<button class="ie-pick" data-add="subtitle"><b>Sous-titre</b></button>' +
        '<button class="ie-pick" data-add="body"><b style="font-weight:400">Texte courant</b></button>';
    } else if (t === 'image') {
      h = '<h4>Image</h4><p class="ie-h">Logo, photo, pictogramme de votre médiathèque.</p>';
      if (!S.images.length) h += '<div class="ie-empty">Aucune image dans la médiathèque. Ajoutez-en dans Contenus.</div>';
      h += '<div class="ie-igrid">' + S.images.map(function (i) { return '<button class="ie-ith" data-img="' + i.id + '" title="' + esc(i.name) + '" style="background-image:url(' + i.url + ')"></button>'; }).join('') + '</div>';
    } else {
      h = '<h4>Bouton</h4><p class="ie-h">Des boutons de navigation déjà réglés.</p>' +
        '<button class="ie-pick" data-add="btn-page"><b>Bouton vers une page</b><small>À relier à une page de la liste</small></button>' +
        '<button class="ie-pick" data-add="btn-home"><b>Bouton « Accueil »</b><small>Retourne à la première page</small></button>' +
        '<button class="ie-pick" data-add="btn-back"><b>Bouton « Retour »</b><small>Revient à la page précédente</small></button>';
    }
    p.innerHTML = h;
    p.onclick = function (ev) {
      var b = ev.target.closest('button'); if (!b) return;
      if (b.dataset.img) { var im = img(b.dataset.img), wd = 40; return add(place(newEl('image', { imageId: im.id, w: wd, h: Math.min(hIn(wd) * (im.h && im.w ? im.h / im.w : 0.75), 50), x: 30, y: 30 }))); }
      var a = b.dataset.add, tx = '#FFFFFF';
      if (a === 'tile-sq') addTile();
      else if (a === 'tile-wide') addTile({ x: 5, w: 90, h: hIn(90) * 0.28 });
      else if (a === 'grid4') layout(4, 2);
      else if (a === 'list3') layout(3, 1);
      else if (a === 'title') add(place(newEl('text', { x: 5, y: 4, w: 90, h: hIn(10), text: 'Titre', fontSize: 6, bold: true })));
      else if (a === 'subtitle') add(place(newEl('text', { x: 5, y: 12, w: 90, h: hIn(7), text: 'Sous-titre', fontSize: 3.4, textColor: '#C5CED9' })));
      else if (a === 'body') add(place(newEl('text', { x: 5, y: 20, w: 90, h: hIn(7), text: 'Votre texte', fontSize: 2.4, textColor: '#C5CED9' })));
      else if (a === 'btn-page') add(place(newEl('tile', { x: 25, y: 85, w: 50, h: hIn(50) * 0.2, text: 'Ouvrir', align: 'center', valign: 'middle', radius: 6, fontSize: 3, color: '#E8833A', action: { type: 'page', pageId: null, item: { qrOn: false } } })));
      else if (a === 'btn-home') add(place(newEl('tile', { x: 25, y: 90, w: 50, h: hIn(50) * 0.18, text: 'Accueil', align: 'center', valign: 'middle', radius: 6, fontSize: 3, color: '#E8833A', action: { type: 'home', item: { qrOn: false } } })));
      else if (a === 'btn-back') add(place(newEl('tile', { x: 25, y: 90, w: 50, h: hIn(50) * 0.18, text: '← Retour', align: 'center', valign: 'middle', radius: 6, fontSize: 3, color: '#5A6B80', action: { type: 'back', item: { qrOn: false } } })));
    };
  }

  /* ---------- Scène : sélection, déplacement, redimensionnement ---------- */
  var stageW = 300;
  function drawStage() {
    var wrap = $('#ie-stagewrap'), c = $('#ie-center'); if (!wrap) return;
    var availW = c.clientWidth - 48, availH = c.clientHeight - 130;
    stageW = Math.max(160, Math.min(availW, availH * aspect()));
    wrap.innerHTML = '';
    var pg = pageNode(page(), stageW, false); pg.id = 'ie-stage'; pg.classList.add('ie-stage');
    wrap.appendChild(pg);
    // bandeau de rappel : sous-page = bouton Retour automatique sur la borne
    if (S.cur > 0) { var bk = document.createElement('div'); bk.className = 'ie-backpill'; bk.style.top = (97 / (page().len || 1)) + '%'; bk.style.bottom = 'auto'; bk.style.transform = 'translate(-50%,-100%)'; bk.textContent = '← Retour'; bk.title = 'La borne ajoute ce bouton sur les pages autres que l’accueil'; pg.appendChild(bk); }
    var sel = S.sel && elOf(S.sel);
    if (sel) { var n = pg.querySelector('[data-id="' + sel.id + '"]'); if (n) { n.classList.add('sel'); handles(pg, sel); } }
    S.multi.forEach(function (id) { var m = pg.querySelector('[data-id="' + id + '"]'); if (m) m.classList.add('sel'); });
    if (S.multi.length > 1 && (!drag || drag.mode === 'gap')) pg.insertAdjacentHTML('beforeend', LxSnap.gapPills(LxSnap.arrange(grpEls()), snapO()));   // écarts d'une rangée / colonne sélectionnée (§177)
    pg.addEventListener('pointerdown', onDown);
    pg.addEventListener('dblclick', onDbl);
  }
  function handles(pg, e) {
    var len = page().len || 1;
    ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w'].forEach(function (d) {
      var hd = document.createElement('i'); hd.className = 'ie-hd'; hd.dataset.d = d;
      var x = d.indexOf('w') >= 0 ? e.x : d.indexOf('e') >= 0 ? e.x + e.w : e.x + e.w / 2, y = d.indexOf('n') >= 0 ? e.y : d.indexOf('s') >= 0 ? e.y + e.h : e.y + e.h / 2;
      hd.style.left = x + '%'; hd.style.top = (y / len) + '%'; pg.appendChild(hd);
    });
  }
  var drag = null;
  function onDown(ev) {
    if (ev.button !== 0) return;
    var gp = ev.target.closest('.lx-gp');
    if (gp) {   // pastille d'écart : tous les écarts de la rangée / colonne changent ensemble (§177)
      var ar = LxSnap.arrange(grpEls()); if (!ar) return;
      ev.preventDefault();
      drag = { mode: 'gap', A: ar.A, items: ar.items.map(function (t) { return t.id; }), g0: ar.gaps[+gp.dataset.gap], x0: ev.clientX, y0: ev.clientY, moved: false };
      document.addEventListener('pointermove', onMove, true); document.addEventListener('pointerup', onUp, true);
      return;
    }
    var hd = ev.target.closest('.ie-hd'), n = ev.target.closest('.ie-el'), add = ev.ctrlKey || ev.metaKey || ev.shiftKey;
    if (!hd && add && n) {   // Ctrl/Cmd/Maj + clic : ajoute l'élément à la sélection, ou le retire
      var ids = selIds(), at = ids.indexOf(n.dataset.id); if (at >= 0) ids.splice(at, 1); else ids.push(n.dataset.id);
      ev.preventDefault(); setSel(ids);
      if (S.multi.indexOf(n.dataset.id) < 0) return;
    }
    if (!hd && n && S.multi.length > 1 && S.multi.indexOf(n.dataset.id) >= 0) {   // glisser un élément de la sélection : tous suivent
      ev.preventDefault();
      drag = { mode: 'group', id: n.dataset.id, x0: ev.clientX, y0: ev.clientY, moved: false, os: S.multi.map(function (id) { var t = elOf(id); return { id: id, x: t.x, y: t.y }; }) };
      document.addEventListener('pointermove', onMove, true); document.addEventListener('pointerup', onUp, true);
      return;
    }
    if (!hd && !n) {   // page vide : on désélectionne et on trace un rectangle de sélection
      ev.preventDefault();
      var base = add ? selIds() : []; if (!add) { S.sel = null; S.multi = []; drawStage(); drawCtx(); }
      drag = { mode: 'band', add: add, base: base, x0: ev.clientX, y0: ev.clientY, rect: $('#ie-stage').getBoundingClientRect(), moved: false, hits: [] };
      document.addEventListener('pointermove', onMove, true); document.addEventListener('pointerup', onUp, true);
      return;
    }
    var id = hd ? S.sel : n.dataset.id, e = elOf(id); if (!e) return;
    ev.preventDefault();
    if (S.sel !== id) { S.sel = id; drawStage(); drawCtx(); }
    drag = { id: id, mode: hd ? hd.dataset.d : 'move', x0: ev.clientX, y0: ev.clientY, o: { x: e.x, y: e.y, w: e.w, h: e.h }, moved: false };
    document.addEventListener('pointermove', onMove, true); document.addEventListener('pointerup', onUp, true);
  }
  function onMove(ev) {
    if (!drag) return;
    if (drag.mode === 'gap') {
      var so0 = snapO(), dd = drag.A ? (ev.clientX - drag.x0) / so0.sx : (ev.clientY - drag.y0) / so0.sy;
      if (Math.abs(ev.clientX - drag.x0) + Math.abs(ev.clientY - drag.y0) > 2) drag.moved = true;
      if (!drag.moved) return;
      var g = Math.max(0, drag.g0 + dd), px = so0.unit(1000, drag.A) / 1000;   // px réels de l'écran par unité
      if (ev.shiftKey) g = Math.round(g * px / 10) * 10 / px;
      LxSnap.setGap(drag.items.map(elOf).filter(Boolean), drag.A, g); drawStage(); return;
    }
    if (drag.mode === 'band' || drag.mode === 'group') {
      var Hp = stageW / aspect(), pdx = (ev.clientX - drag.x0) / stageW * 100, pdy = (ev.clientY - drag.y0) / Hp * 100;
      if (Math.abs(ev.clientX - drag.x0) + Math.abs(ev.clientY - drag.y0) > 3) drag.moved = true;
      if (!drag.moved) return;
      var pg0 = $('#ie-stage');
      if (drag.mode === 'group') {
        // l'ensemble s'aimante comme un seul bloc (§177) ; Alt : sans aimantation, Maj : déplacement droit
        var lk = null; if (ev.shiftKey) { if (Math.abs(pdx) * aspect() > Math.abs(pdy)) { pdy = 0; lk = false; } else { pdx = 0; lk = true; } }
        var gs = drag.os.map(function (o) { var t = elOf(o.id) || { w: 0, h: 0 }; return { x: o.x, y: o.y, w: t.w, h: t.h }; }), bb = boundsOf(gs), so = snapO(), os = others(drag.os.map(function (o) { return o.id; }));
        var bx = { x: bb.x + pdx, y: bb.y + pdy, w: bb.w, h: bb.h };
        if (!ev.altKey) LxSnap.move(bx, os, so, lk);
        var mx = bx.x - bb.x, my = bx.y - bb.y;
        drag.os.forEach(function (o) { var t = elOf(o.id), nd = t && pg0.querySelector('[data-id="' + o.id + '"]'); if (!t) return; t.x = o.x + mx; t.y = o.y + my; if (nd) { nd.style.left = t.x + '%'; nd.style.top = (t.y / (page().len || 1)) + '%'; } });
        [].forEach.call(pg0.querySelectorAll('.lx-gp'), function (n) { n.remove(); });
        drawSnap(ev.altKey ? null : LxSnap.guides(bx, os, so, false));
        return;
      }
      var r = drag.rect, ax = (drag.x0 - r.left) / stageW * 100, ay = (drag.y0 - r.top) / Hp * 100, bx = (ev.clientX - r.left) / stageW * 100, by = (ev.clientY - r.top) / Hp * 100;
      var x1 = Math.min(ax, bx), x2 = Math.max(ax, bx), y1 = Math.min(ay, by), y2 = Math.max(ay, by);
      drag.hits = page().els.filter(function (t) { return t.x < x2 && t.x + t.w > x1 && t.y < y2 && t.y + t.h > y1; }).map(function (t) { return t.id; });
      var bd = pg0.querySelector('.ie-band'); if (!bd) { bd = document.createElement('i'); bd.className = 'ie-band'; pg0.appendChild(bd); }
      bd.style.left = x1 + '%'; bd.style.width = (x2 - x1) + '%'; bd.style.top = (y1 / (page().len || 1)) + '%'; bd.style.height = ((y2 - y1) / (page().len || 1)) + '%';
      [].forEach.call(pg0.querySelectorAll('.ie-el'), function (nd) { nd.classList.toggle('sel', drag.hits.indexOf(nd.dataset.id) >= 0 || drag.base.indexOf(nd.dataset.id) >= 0); });
      return;
    }
    var e = elOf(drag.id), len = page().len || 1, H = stageW / aspect(), dx = (ev.clientX - drag.x0) / stageW * 100, dy = (ev.clientY - drag.y0) / H * 100, o = drag.o;
    if (Math.abs(ev.clientX - drag.x0) + Math.abs(ev.clientY - drag.y0) > 3) drag.moved = true;
    if (!drag.moved) return;
    var d = drag.mode, so = snapO(), os = others([e.id]);
    if (d === 'move') {   // bords et centres de la page et des autres éléments, écarts égaux (§177) ; Alt : sans aimantation, Maj : déplacement droit
      var lock = null; if (ev.shiftKey) { if (Math.abs(dx) * aspect() > Math.abs(dy)) { dy = 0; lock = false; } else { dx = 0; lock = true; } }
      var b = { x: o.x + dx, y: o.y + dy, w: e.w, h: e.h };
      if (!ev.altKey) LxSnap.move(b, os, so, lock);
      e.x = b.x; e.y = b.y; drawSnap(ev.altKey ? null : LxSnap.guides(b, os, so, false));
    } else {
      var x = o.x, y = o.y, wd = o.w, ht = o.h;
      if (d.indexOf('e') >= 0) wd = o.w + dx;
      if (d.indexOf('s') >= 0) ht = o.h + dy;
      if (d.indexOf('w') >= 0) { wd = o.w - dx; x = o.x + dx; }
      if (d.indexOf('n') >= 0) { ht = o.h - dy; y = o.y + dy; }
      if (!ev.altKey) { var rb = { x: x, y: y, w: wd, h: ht }; LxSnap.resize(rb, d, os, so); x = rb.x; y = rb.y; wd = rb.w; ht = rb.h; }   // le bord tiré s'aimante, et à la même taille (§177)
      if (wd < 3) { if (d.indexOf('w') >= 0) x = o.x + o.w - 3; wd = 3; }
      if (ht < 2) { if (d.indexOf('n') >= 0) y = o.y + o.h - 2; ht = 2; }
      e.x = x; e.y = y; e.w = wd; e.h = ht;
      drawSnap(ev.altKey ? null : LxSnap.guides({ x: x, y: y, w: wd, h: ht }, os, so, true));
    }
    var pg = $('#ie-stage'), nd = pg.querySelector('[data-id="' + e.id + '"]');
    if (nd) { nd.style.left = e.x + '%'; nd.style.top = (e.y / len) + '%'; nd.style.width = e.w + '%'; nd.style.height = (e.h / len) + '%'; }
    [].forEach.call(pg.querySelectorAll('.ie-hd'), function (hd) { pg.removeChild(hd); }); handles(pg, e);
  }
  // ---------- aide à l'alignement (snap-guides.js, §177) ----------
  // x et w en % de la largeur, y et h en % de la hauteur d'une page ; les valeurs affichées sont en px de l'écran réel
  function snapO() {
    var len = page().len || 1, Hp = stageW / aspect(), r = S.orientation === 'portrait' ? [1080, 1920] : [1920, 1080], ys = [];
    for (var q = 0; q <= len * 2; q++) ys.push(q * 50);
    return { W: 100, H: 100 * len, sx: stageW / 100, sy: Hp / 100, xs: [0, 50, 100], ys: ys, unit: function (v, A) { return Math.round(v * (A ? r[0] : r[1]) / 100); } };
  }
  function others(skip) { return page().els.filter(function (t) { return skip.indexOf(t.id) < 0; }).map(function (t) { return { x: t.x, y: t.y, w: t.w, h: t.h }; }); }
  function boundsOf(L) { var x = Math.min.apply(null, L.map(function (r) { return r.x; })), y = Math.min.apply(null, L.map(function (r) { return r.y; })); return { x: x, y: y, w: Math.max.apply(null, L.map(function (r) { return r.x + r.w; })) - x, h: Math.max.apply(null, L.map(function (r) { return r.y + r.h; })) - y }; }
  function drawSnap(g) { var pg = $('#ie-stage'); if (!pg) return; [].forEach.call(pg.querySelectorAll('.lx-snap'), function (n) { n.remove(); }); if (g) pg.insertAdjacentHTML('beforeend', LxSnap.svg(g, snapO())); }
  function grpEls() { return S.multi.map(elOf).filter(Boolean); }
  /** outils de la sélection multiple : aligner, répartir, écart (px de l'écran), ranger en grille, même taille */
  function grpDo(a, val) {
    var g = grpEls(); if (g.length < 2) return;
    var b = boundsOf(g), so = snapO(), pxX = so.unit(1000, true) / 1000, pxY = so.unit(1000, false) / 1000;
    var al = { l: function (t) { t.x = b.x; }, c: function (t) { t.x = b.x + (b.w - t.w) / 2; }, r: function (t) { t.x = b.x + b.w - t.w; }, t: function (t) { t.y = b.y; }, m: function (t) { t.y = b.y + (b.h - t.h) / 2; }, b: function (t) { t.y = b.y + b.h - t.h; } };
    if (al[a]) g.forEach(al[a]);
    else if (a === 'dh' || a === 'dv') { if (g.length < 3) return; if (LxSnap.distribute(g, a === 'dh') == null) { toast('Pas assez de place pour répartir : indiquez un écart ou rangez en grille.', true); return; } }
    else if (a === 'sw' || a === 'sh') LxSnap.sameSize(g, a === 'sw');
    else if (a === 'gap' || a === 'grid') {
      var px = Math.max(0, Math.round(+val || 0)), ar = a === 'gap' ? LxSnap.arrange(g) : null;
      if (ar) LxSnap.setGap(ar.items, ar.A, px / (ar.A ? pxX : pxY)); else LxSnap.tidy(g, px / pxX, px / pxY);
    }
    g.forEach(function (t) { t.x = Math.round(t.x * 100) / 100; t.y = Math.round(t.y * 100) / 100; t.w = Math.round(t.w * 100) / 100; t.h = Math.round(t.h * 100) / 100; });
    dirty(); drawStage(); drawCtx();
  }
  function multiTools() {
    var g = grpEls(), ar = LxSnap.arrange(g), so = snapO(), I = LxSnap.ICONS, n = g.length;
    var gv = ar ? (ar.gaps.every(function (v) { return Math.abs(so.unit(v, ar.A) - so.unit(ar.gaps[0], ar.A)) < 1; }) ? so.unit(ar.gaps[0], ar.A) : '') : 40;
    var bt = function (a, t, d, dis) { return '<button class="ie-ib" data-grp="' + a + '" title="' + t + '" aria-label="' + t + '"' + (dis ? ' disabled' : '') + '><svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="' + d + '"/></svg></button>'; };
    return bt('l', 'Aligner à gauche', 'M4 4v16M8 7h12M8 13h7') + bt('c', 'Centrer horizontalement', 'M12 3v18M5 7h14M8 13h8') + bt('r', 'Aligner à droite', 'M20 4v16M4 7h12M9 13h7') +
      bt('t', 'Aligner en haut', 'M4 4h16M7 8v12M13 8v7') + bt('m', 'Centrer verticalement', 'M3 12h18M7 5v14M13 8v8') + bt('b', 'Aligner en bas', 'M4 20h16M7 4v12M13 9v7') + '<span class="ie-sep sm"></span>' +
      bt('dh', 'Répartir horizontalement : écarts égaux', I.dh, n < 3) + bt('dv', 'Répartir verticalement : écarts égaux', I.dv, n < 3) +
      '<label class="ie-gapf" title="' + (ar ? 'Écart entre chaque élément, en px de l’écran' : 'Écart utilisé pour ranger en grille, en px de l’écran') + '">Écart <input type="number" min="0" data-grpgap="1" value="' + gv + '" placeholder="—" aria-label="Écart en px"> px</label>' +
      bt('grid', 'Ranger en grille, avec l’écart indiqué', I.grid) + '<span class="ie-sep sm"></span>' + bt('sw', 'Même largeur (la plus grande)', I.sw) + bt('sh', 'Même hauteur (la plus grande)', I.sh);
  }
  function onUp() {
    document.removeEventListener('pointermove', onMove, true); document.removeEventListener('pointerup', onUp, true);
    if (drag && drag.mode === 'band') {   // fin du rectangle : les éléments touchés forment la sélection
      var bd = drag; drag = null;
      if (bd.moved) setSel(bd.add ? bd.base.concat(bd.hits.filter(function (id) { return bd.base.indexOf(id) < 0; })) : bd.hits); else drawStage();
      return;
    }
    if (drag && drag.mode === 'group') {
      var gd = drag; drag = null;
      if (gd.moved) { dirty(); S.multi.forEach(function (id) { var t = elOf(id); if (t) { t.x = Math.round(t.x * 100) / 100; t.y = Math.round(t.y * 100) / 100; } }); drawStage(); drawCtx(); }
      else setSel([gd.id]);   // simple clic sur un élément de la sélection : lui seul reste
      return;
    }
    if (drag && drag.mode === 'gap') { var gg = drag; drag = null; if (gg.moved) { dirty(); gg.items.forEach(function (id) { var t = elOf(id); if (t) { t.x = Math.round(t.x * 100) / 100; t.y = Math.round(t.y * 100) / 100; } }); } drawStage(); drawCtx(); return; }
    if (drag && drag.moved) { dirty(); var e = elOf(drag.id); if (e) { e.x = Math.round(e.x * 100) / 100; e.y = Math.round(e.y * 100) / 100; e.w = Math.round(e.w * 100) / 100; e.h = Math.round(e.h * 100) / 100; } }
    drag = null; drawStage();
  }
  function onDbl(ev) {   // double clic : modifier le texte sur place
    var n = ev.target.closest('.ie-el'); if (!n) return;
    var e = elOf(n.dataset.id); if (!e || e.kind === 'image') return;
    var t = n.querySelector('.ie-t'); if (!t) return;
    t.contentEditable = 'true'; t.classList.add('editing'); t.focus();
    var r = document.createRange(); r.selectNodeContents(t); var s = getSelection(); s.removeAllRanges(); s.addRange(r);
    var done = function () { t.contentEditable = 'false'; e.text = t.innerText.replace(/\n+$/, ''); dirty(); drawStage(); drawCtx(); };
    t.addEventListener('blur', done, { once: true });
    t.addEventListener('keydown', function (k) { if (k.key === 'Enter' && !k.shiftKey) { k.preventDefault(); t.blur(); } if (k.key === 'Escape') { k.preventDefault(); t.blur(); } });
  }

  /* ---------- Barre contextuelle ---------- */
  function chip(label, key, extra) { return '<button class="ie-chip ' + (extra || '') + '" data-pop="' + key + '">' + label + '</button>'; }
  function drawCtx() {
    var c = $('#ie-ctx'), e = S.sel && elOf(S.sel), h;
    if (S.multi.length > 1) {
      c.className = 'ie-ctx';
      c.innerHTML = '<span class="ie-hint">' + S.multi.length + ' éléments</span><span class="ie-sep sm"></span>' + multiTools() + '<span class="ie-sep sm"></span><button class="ie-ib" data-do="dup" title="Dupliquer (Ctrl+D)">' + ICO.dup + '</button><button class="ie-ib" data-do="del" title="Supprimer">' + ICO.del + '</button>';
      c.onclick = function (ev) {
        var gb = ev.target.closest('[data-grp]'); if (gb) { var f = c.querySelector('[data-grpgap]'); grpDo(gb.dataset.grp, f && f.value !== '' ? f.value : 40); return; }
        var d = ev.target.closest('[data-do]'); if (d) { if (d.dataset.do === 'del') remove(); else dup(); }
      };
      c.onchange = function (ev) { if (ev.target.dataset.grpgap) grpDo('gap', ev.target.value); };
      return;
    }
    if (!e) { c.className = 'ie-ctx empty'; c.innerHTML = '<span class="ie-hint">Touchez un élément pour le modifier</span>'; return; }
    c.className = 'ie-ctx';
    var tail = '<span class="ie-sep sm"></span><button class="ie-ib" data-do="dup" title="Dupliquer (Ctrl+D)">' + ICO.dup + '</button><button class="ie-ib" data-do="del" title="Supprimer">' + ICO.del + '</button>';
    if (e.kind === 'tile') h = chip('Aa Texte ▾', 'text') + chip('<i class="ie-dot" style="background:' + e.color + '"></i> Fond ▾', 'fill') + '<span class="ie-sep sm"></span>' + chip(ICO.hand + ' ' + esc(actionLabel(e)) + ' ▾', 'action', 'act') + tail;
    else if (e.kind === 'text') h = chip('Aa Texte ▾', 'text') + tail;
    else h = chip('Changer l’image ▾', 'pick') + tail;
    c.innerHTML = h;
    c.onclick = function (ev) {
      var d = ev.target.closest('[data-do]'); if (d) { if (d.dataset.do === 'del') remove(); else dup(); return; }
      var b = ev.target.closest('[data-pop]'); if (!b) return;
      ev.stopPropagation();
      var f = { text: textPop, fill: fillPop, action: actionPop, pick: pickPop }[b.dataset.pop]; popAt(b, f);
    };
  }
  function actionLabel(e) {
    var a = e.action || {};
    if (a.type === 'media') { var c = content(a.mediaId); return 'Ouvre · ' + (c ? c.name : 'contenu à choisir'); }
    if (a.type === 'page') { var p = S.pages.filter(function (x) { return x.id === a.pageId; })[0]; return 'Page · ' + (p ? p.name : 'à choisir'); }
    if (a.type === 'home') return 'Retour à l’accueil';
    if (a.type === 'back') return 'Revient en arrière';
    return 'Choisir une action';
  }

  /* ---------- Menus flottants ---------- */
  function closePop() { var p = document.querySelector('.ie-pop'); if (p && p.parentNode) p.parentNode.removeChild(p); if (S) S.pop = null; }
  function popAt(btn, fn) {
    if (S.pop === fn && document.querySelector('.ie-pop')) return closePop();
    closePop(); S.pop = fn;
    var p = document.createElement('div'); p.className = 'ie-pop'; document.body.appendChild(p);
    var r = btn.getBoundingClientRect();
    p.style.top = (r.bottom + 6) + 'px'; p.style.left = Math.max(8, Math.min(r.left, innerWidth - 340)) + 'px';
    fn(p, rerender);
    function rerender() { var keep = p.scrollTop; fn(p, rerender); p.scrollTop = keep; }
  }
  function bindPop(p, handler) { p.onclick = function (ev) { var b = ev.target.closest('[data-k]'); if (b) handler(b, ev); }; }

  function fmtPop(p) {
    p.innerHTML = '<div class="ie-pt">Format de la page</div><button class="ie-mi" data-k="portrait">▯ Portrait <small>' + (S.orientation === 'portrait' ? '✓' : '') + '</small></button><button class="ie-mi" data-k="landscape">▭ Paysage <small>' + (S.orientation === 'landscape' ? '✓' : '') + '</small></button><div class="ie-hn">Doit correspondre à l’orientation de la borne.</div>';
    bindPop(p, function (b) { S.orientation = b.dataset.k; dirty(); closePop(); draw(); });
  }

  function swatches(cur, list, key) { return '<div class="ie-sw">' + list.map(function (c) { return '<button class="' + (c.toLowerCase() === (cur || '').toLowerCase() ? 'on' : '') + '" data-k="' + key + '" data-v="' + c + '" style="background:' + c + '" aria-label="' + c + '"></button>'; }).join('') + '</div>'; }
  function imgGrid(cur, key) { return '<div class="ie-ig">' + S.images.slice(0, 60).map(function (i) { return '<button class="' + (i.id === cur ? 'on' : '') + '" data-k="' + key + '" data-v="' + i.id + '" title="' + esc(i.name) + '" style="background-image:url(' + i.url + ')"></button>'; }).join('') + (S.images.length ? '' : '<span class="ie-hn">Aucune image dans la médiathèque.</span>') + '</div>'; }

  function bgPop(p, again) {
    var pg = page();
    p.innerHTML = '<div class="ie-pt">Fond de la page</div>' + swatches(pg.bg, BGS, 'bg') +
      '<label class="ie-row">Autre couleur <input type="color" data-k="bgc" value="' + pg.bg + '"></label>' +
      '<div class="ie-pt">Image de fond</div>' + imgGrid(pg.bgImageId, 'bgi') +
      (pg.bgImageId ? '<button class="ie-mi" data-k="bgi-off">Retirer l’image</button>' : '') +
      '<div class="ie-pt">Longueur de la page</div><div class="ie-seg">' + [1, 2, 3, 4].map(function (n) { return '<button data-k="len" data-v="' + n + '" class="' + ((pg.len || 1) === n ? 'on' : '') + '">' + (n === 1 ? '1 écran' : n + ' écrans') + '</button>'; }).join('') + '</div><div class="ie-hn">Au-delà d\u2019un écran, le visiteur fait défiler la page vers le bas.</div>' +
      '<hr><button class="ie-mi" data-k="bg-all">Appliquer ce fond à toutes les pages</button>';
    var ci = p.querySelector('input[type=color]'); if (ci) ci.oninput = function () { page().bg = ci.value; dirty(); drawStage(); drawPages(); };
    bindPop(p, function (b) {
      var k = b.dataset.k, v = b.dataset.v;
      if (k === 'bg') { page().bg = v; page().bgImageId = null; } else if (k === 'bgi') page().bgImageId = v; else if (k === 'bgi-off') page().bgImageId = null;
      else if (k === 'len') { var nl = parseInt(v, 10); pg.len = nl; pg.els.forEach(function (e) { e.y = Math.max(0, Math.min(e.y, nl * 100 - e.h)); if (e.h > nl * 100) e.h = nl * 100; }); }
      else if (k === 'bg-all') { S.pages.forEach(function (x) { x.bg = pg.bg; x.bgImageId = pg.bgImageId; }); toast('Fond appliqué à toutes les pages.'); }
      else return;
      dirty(); drawStage(); drawPages(); again();
    });
  }

  function textPop(p, again) {
    var e = elOf(S.sel); if (!e) return closePop();
    var isTile = e.kind === 'tile', sz = e.fontSize;
    p.innerHTML = '<div class="ie-pt">' + (isTile ? 'Titre' : 'Texte') + '</div>' +
      (isTile ? '<input class="ie-in" id="ie-t1" maxlength="120" value="' + esc(e.text) + '"><div class="ie-pt">Sous-titre (facultatif)</div><input class="ie-in" id="ie-t2" maxlength="160" value="' + esc(e.sub || '') + '">'
        : '<textarea class="ie-in" id="ie-t1" rows="2" maxlength="120">' + esc(e.text) + '</textarea>') +
      '<div class="ie-pt">Taille</div><div class="ie-seg">' + (isTile ? SIZES_TILE : SIZES_TEXT).map(function (s) { return '<button data-k="size" data-v="' + s[1] + '" class="' + (Math.abs(sz - s[1]) < 0.6 ? 'on' : '') + '">' + s[0] + '</button>'; }).join('') + '</div>' +
      '<div class="ie-pt">Couleur du texte</div>' + swatches(e.textColor, ['#FFFFFF', '#0B1F3A', '#C5CED9', '#E8833A', '#3B82C4', '#2E7D5B', '#D0453C', '#000000'], 'tc') +
      '<div class="ie-row"><select class="ie-in" data-k="font" aria-label="Police">' + FONTS.map(function (f) { return '<option value="' + f[0] + '"' + ((e.font || '') === f[0] ? ' selected' : '') + '>' + f[1] + '</option>'; }).join('') + '</select>' +
      '<button class="ie-tg ' + (e.bold ? 'on' : '') + '" data-k="bold" title="Gras"><b>G</b></button></div>' +
      '<div class="ie-seg">' + [['left', 'Gauche'], ['center', 'Centre'], ['right', 'Droite']].map(function (a) { return '<button data-k="align" data-v="' + a[0] + '" class="' + (e.align === a[0] ? 'on' : '') + '">' + a[1] + '</button>'; }).join('') + '</div>' +
      (isTile ? '<div class="ie-pt">Position du texte</div><div class="ie-seg">' + [['top', 'Haut'], ['middle', 'Milieu'], ['bottom', 'Bas']].map(function (a) { return '<button data-k="valign" data-v="' + a[0] + '" class="' + (e.valign === a[0] ? 'on' : '') + '">' + a[1] + '</button>'; }).join('') + '</div>' : '');
    var t1 = p.querySelector('#ie-t1'), t2 = p.querySelector('#ie-t2');
    t1.oninput = function () { e.text = t1.value; dirty(); drawStage(); drawPages(); }; if (t2) t2.oninput = function () { e.sub = t2.value || null; dirty(); drawStage(); };
    p.querySelector('select').onchange = function (ev) { e.font = ev.target.value || null; dirty(); drawStage(); drawPages(); };
    bindPop(p, function (b) {
      var k = b.dataset.k, v = b.dataset.v;
      if (k === 'size') e.fontSize = parseFloat(v);
      else if (k === 'tc') e.textColor = v; else if (k === 'bold') e.bold = !e.bold; else if (k === 'align') e.align = v; else if (k === 'valign') e.valign = v; else return;
      dirty(); drawStage(); drawPages(); again();
    });
  }

  function fillPop(p, again) {
    var e = elOf(S.sel); if (!e) return closePop();
    p.innerHTML = '<div class="ie-pt">Couleur de la tuile</div>' + swatches(e.color, PAL, 'fc') +
      '<label class="ie-row">Autre couleur <input type="color" value="' + e.color + '"></label>' +
      '<div class="ie-pt">Ou une image</div>' + imgGrid(e.imageId, 'fi') + (e.imageId ? '<button class="ie-mi" data-k="fi-off">Retirer l’image</button>' : '') +
      '<div class="ie-pt">Forme</div><div class="ie-seg">' + [['Carrée', 0], ['Arrondie', 1.6], ['Très arrondie', 3.6]].map(function (r) { return '<button data-k="rad" data-v="' + r[1] + '" class="' + (Math.abs(e.radius - r[1]) < 0.3 ? 'on' : '') + '">' + r[0] + '</button>'; }).join('') + '</div>';
    var ci = p.querySelector('input[type=color]'); ci.oninput = function () { e.color = ci.value; dirty(); drawStage(); drawPages(); };
    bindPop(p, function (b) {
      var k = b.dataset.k, v = b.dataset.v;
      if (k === 'fc') e.color = v; else if (k === 'fi') e.imageId = v; else if (k === 'fi-off') e.imageId = null; else if (k === 'rad') e.radius = parseFloat(v); else return;
      dirty(); drawStage(); drawCtx(); drawPages(); again();
    });
  }

  function pickPop(p, again) {
    var e = elOf(S.sel); if (!e) return closePop();
    p.innerHTML = '<div class="ie-pt">Image</div>' + imgGrid(e.imageId, 'pi');
    bindPop(p, function (b) { if (b.dataset.k !== 'pi') return; e.imageId = b.dataset.v; dirty(); drawStage(); drawPages(); again(); });
  }

  var cq = '', ccat = null;
  function actionPop(p, again) {
    var e = elOf(S.sel); if (!e) return closePop();
    var a = e.action, t = a.type, others = S.pages.filter(function (x) { return x.id !== page().id; });
    var rows = [['media', 'Ouvre un contenu', 'média, composition, PDF, page web…'], ['page', 'Ouvre une page', 'une autre page de cette liste'], ['home', 'Retourne à l’accueil', ''], ['back', 'Revient en arrière', ''], ['none', 'Ne fait rien', '']];
    var h = '<div class="ie-pt">Au toucher, cette tuile…</div>' + rows.map(function (r) { return '<button class="ie-mi ' + (t === r[0] ? 'on' : '') + '" data-k="type" data-v="' + r[0] + '"><span class="ie-rd"></span><span>' + r[1] + (r[2] ? '<small>' + r[2] + '</small>' : '') + '</span></button>'; }).join('');
    if (t === 'page') {
      h += others.length ? '<select class="ie-in" data-k="pg" aria-label="Page à ouvrir"><option value="">— Choisir la page —</option>' + others.map(function (x) { return '<option value="' + x.id + '"' + (a.pageId === x.id ? ' selected' : '') + '>' + esc(x.name) + '</option>'; }).join('') + '</select>' : '<div class="ie-hn">Ajoutez une page avec « + Page » en bas, puis revenez ici.</div>';
    }
    if (t === 'media') {
      var cur = content(a.mediaId), cats = CATS.filter(function (c) { return S.contents.some(function (x) { return x.cat === c[0]; }); });
      if (!ccat || !cats.some(function (c) { return c[0] === ccat; })) ccat = (cur && cur.cat) || (cats[0] && cats[0][0]) || 'media';
      h += '<div class="ie-pt">Contenu' + (cur ? ' : <b>' + esc(cur.name) + '</b>' : '') + '</div>' +
        (cats.length ? '<div class="ie-cats">' + cats.map(function (c) { return '<button data-k="cat" data-v="' + c[0] + '" class="' + (ccat === c[0] ? 'on' : '') + '">' + c[1] + '</button>'; }).join('') + '</div>' +
          '<input class="ie-in" id="ie-cq" placeholder="Rechercher" value="' + esc(cq) + '">' +
          '<div class="ie-cl">' + S.contents.filter(function (x) { return x.cat === ccat && (!cq || x.name.toLowerCase().indexOf(cq.toLowerCase()) >= 0); }).slice(0, 80).map(function (x) { return '<button class="ie-ci ' + (a.mediaId === x.id ? 'on' : '') + '" data-k="media" data-v="' + x.id + '"><span class="th"' + (x.thumb ? ' style="background-image:url(' + x.thumb + ')"' : '') + '></span><span>' + esc(x.name) + '</span></button>'; }).join('') + '</div>'
          : '<div class="ie-hn">Rien à ouvrir pour l’instant. Ajoutez des contenus dans la médiathèque.</div>');
      if (S.growth) {
        var q = a.item || (a.item = { qrOn: false });
        h += '<hr><button class="ie-sw2 ' + (q.qrOn ? 'on' : '') + '" data-k="qr"><span></span> QR code « Emporter sur mon téléphone »</button>' +
          (q.qrOn ? '<input class="ie-in" id="ie-qu" placeholder="https://… adresse ouverte par le visiteur" value="' + esc(q.qrUrl || '') + '"><input class="ie-in" id="ie-ql" maxlength="60" placeholder="Texte sous le QR code (facultatif)" value="' + esc(q.qrLabel || '') + '">' : '');
      }
    }
    h += '<details class="ie-adv"' + (e.showFrom || e.showTo ? ' open' : '') + '><summary>Afficher seulement entre deux dates</summary><div class="ie-row"><input type="date" class="ie-in" id="ie-d1" value="' + (e.showFrom || '') + '"><input type="date" class="ie-in" id="ie-d2" value="' + (e.showTo || '') + '"></div></details>';
    p.innerHTML = h;
    var sel = p.querySelector('select'); if (sel) sel.onchange = function (ev) { a.pageId = ev.target.value || null; dirty(); drawCtx(); drawPages(); };
    var qs = p.querySelector('#ie-cq'); if (qs) { qs.oninput = function () { cq = qs.value; var pos = qs.selectionStart; again(); var n = p.querySelector('#ie-cq'); n.focus(); n.setSelectionRange(pos, pos); }; }
    var qu = p.querySelector('#ie-qu'), ql = p.querySelector('#ie-ql');
    if (qu) qu.oninput = function () { a.item.qrUrl = qu.value; dirty(); }; if (ql) ql.oninput = function () { a.item.qrLabel = ql.value; dirty(); };
    var d1 = p.querySelector('#ie-d1'), d2 = p.querySelector('#ie-d2');
    d1.onchange = function () { e.showFrom = d1.value || null; dirty(); }; d2.onchange = function () { e.showTo = d2.value || null; dirty(); };
    bindPop(p, function (b) {
      var k = b.dataset.k, v = b.dataset.v;
      if (k === 'type') { a.type = v; if (v !== 'media') a.mediaId = null; if (v !== 'page') a.pageId = null; }
      else if (k === 'cat') ccat = v;
      else if (k === 'media') { a.mediaId = v; var c = content(v); if (c && (!e.text || e.text === 'Nouvelle tuile')) { e.text = c.name; drawStage(); } }
      else if (k === 'qr') { a.item.qrOn = !a.item.qrOn; }
      else return;
      dirty(); drawCtx(); again();
    });
  }

  /* ---------- Pages ---------- */
  function drawPages() {
    var bar = $('#ie-pages'); if (!bar) return;
    var h = '<div class="ie-pgh">Pages de la liste</div>';
    S.pages.forEach(function (p, i) {
      h += '<div class="ie-pg ' + (i === S.cur ? 'on' : '') + '" data-i="' + i + '"><div class="th" data-th="' + i + '"></div><span>' + (i === 0 ? '⌂ ' : '') + esc(p.name) + '</span>' + (i === S.cur ? '<button class="ie-pgm" data-pop="pgm" data-i="' + i + '" aria-label="Options de la page">' + ICO.more + '</button>' : '') + '</div>';
    });
    h += '<button class="ie-pg add" data-add="1"><div class="th">+</div><span>Page</span></button>';
    bar.innerHTML = h;
    var tw = 44; S.pages.forEach(function (p, i) { var th = bar.querySelector('[data-th="' + i + '"]'); th.style.width = tw + 'px'; th.style.height = (tw / aspect()) + 'px'; th.appendChild(pageNode(p, tw, false)); });
    bar.onclick = function (ev) {
      var m = ev.target.closest('.ie-pgm'); if (m) { ev.stopPropagation(); return popAt(m, pageMenu); }
      var add_ = ev.target.closest('[data-add]');
      if (add_) { if (S.pages.length >= 30) return toast('Au plus 30 pages par liste.', true); var bgc = page().bg; S.pages.push({ id: uid(), name: 'Page ' + (S.pages.length + 1), bg: bgc, bgImageId: page().bgImageId || null, len: 1, els: [] }); S.cur = S.pages.length - 1; S.sel = null; dirty(); draw(); return; }
      var g = ev.target.closest('.ie-pg'); if (g && !g.classList.contains('on')) { S.cur = parseInt(g.dataset.i, 10); S.sel = null; S.multi = []; draw(); }
    };
    bar.ondblclick = function (ev) { var g = ev.target.closest('.ie-pg[data-i]'); if (g) rename(parseInt(g.dataset.i, 10)); };
  }
  function rename(i) { var n = prompt('Nom de la page', S.pages[i].name); if (n && n.trim()) { S.pages[i].name = n.trim().slice(0, 40); dirty(); draw(); } }
  function pageMenu(p) {
    var i = S.cur, first = i === 0;
    p.innerHTML = '<button class="ie-mi" data-k="ren">Renommer…</button><button class="ie-mi" data-k="dup">Dupliquer la page</button>' + (first ? '' : '<button class="ie-mi" data-k="first">Définir comme accueil</button>') +
      (S.pages.length > 1 ? '<hr><button class="ie-mi danger" data-k="del">Supprimer la page</button>' : '');
    bindPop(p, function (b) {
      var k = b.dataset.k; closePop();
      if (k === 'ren') rename(i);
      else if (k === 'dup') { var c = clone(S.pages[i]); c.id = uid(); c.name = (c.name + ' (copie)').slice(0, 40); c.els.forEach(function (e) { e.id = uid(); }); S.pages.splice(i + 1, 0, c); S.cur = i + 1; dirty(); draw(); }
      else if (k === 'first') { var pg = S.pages.splice(i, 1)[0]; S.pages.unshift(pg); S.cur = 0; dirty(); draw(); }
      else if (k === 'del') {
        var used = S.pages.some(function (x) { return x.els.some(function (e) { return e.action && e.action.type === 'page' && e.action.pageId === S.pages[i].id; }); });
        if (!confirm('Supprimer la page « ' + S.pages[i].name + ' » ?' + (used ? ' Des tuiles y mènent : elles devront être reliées à une autre page.' : ''))) return;
        var gone = S.pages.splice(i, 1)[0];
        S.pages.forEach(function (x) { x.els.forEach(function (e) { if (e.action && e.action.pageId === gone.id) { e.action.pageId = null; } }); });
        S.cur = Math.max(0, i - 1); S.sel = null; S.multi = []; dirty(); draw();
      }
    });
  }

  /* ---------- Aperçu tactile ---------- */
  function openPreview() {
    closePop(); S.preview = { cur: 0, stack: [] };
    var d = document.createElement('div'); d.className = 'ie-pv'; d.id = 'ie-pv'; root.appendChild(d);
    d.innerHTML = '<button class="ie-pvx" aria-label="Fermer l’aperçu">✕</button><div class="ie-pvw"><div class="ie-pvs"></div></div><div class="ie-pvh">Aperçu : touchez les tuiles comme un visiteur. Les contenus ne s’ouvrent pas ici.</div>';
    d.querySelector('.ie-pvx').onclick = closePreview; pvDraw();
  }
  function closePreview() { if (!S || !S.preview) return; S.preview = null; var d = $('#ie-pv'); if (d && d.parentNode) d.parentNode.removeChild(d); }
  function pvDraw() {
    var d = $('#ie-pv'), box = d.querySelector('.ie-pvs'), pv = S.preview, p = S.pages[pv.cur];
    var W = Math.min(innerWidth - 80, (innerHeight - 110) * aspect());
    box.innerHTML = ''; var pg = pageNode(p, W, true); box.appendChild(pg);
    box.style.width = W + 'px'; box.style.height = (W / aspect()) + 'px';   // un écran visible ; la page défile si elle est plus longue
    var wrap = box.parentNode; [].forEach.call(wrap.querySelectorAll('.ie-backpill'), function (x) { wrap.removeChild(x); });
    if (pv.cur > 0) { var bk = document.createElement('div'); bk.className = 'ie-backpill live'; bk.textContent = '← Retour'; bk.onclick = function () { pv.cur = pv.stack.length ? pv.stack.pop() : 0; pvDraw(); }; wrap.appendChild(bk); }
    pg.onclick = function (ev) {
      var n = ev.target.closest('.ie-tile'); if (!n) return;
      var e = null; p.els.forEach(function (x) { if (x.id === n.dataset.id) e = x; }); if (!e) return;
      var a = e.action || {};
      if (a.type === 'page' && a.pageId) { pv.stack.push(pv.cur); pv.cur = Math.max(0, S.pages.findIndex(function (x) { return x.id === a.pageId; })); }
      else if (a.type === 'home') { pv.cur = 0; pv.stack = []; }
      else if (a.type === 'back') { pv.cur = pv.stack.length ? pv.stack.pop() : 0; }
      else if (a.type === 'media') { var c = content(a.mediaId); var h = d.querySelector('.ie-pvh'); h.textContent = 'Ouvrirait : ' + (c ? c.name : 'contenu à choisir'); return; }
      else return;
      pvDraw();
    };
  }

  w.LinkiiIListEditor = { mount: mount, unmount: unmount, replaced: replaced };
})(window);
