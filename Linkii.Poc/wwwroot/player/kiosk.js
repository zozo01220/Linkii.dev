/* Borne interactive du player Linkivi : menu de tuiles au toucher, par-dessus la diffusion habituelle de l'écran.
   Sans toucher, la diffusion (liste, widgets, planning) tourne comme sur un écran ordinaire ; le premier toucher affiche l'accueil, une tuile ouvre son contenu
   pleine page, et les délais ramènent à l'accueil puis à la diffusion. JS volontairement « ancien » (var, pas d'async/await). 1em = 1 % de la largeur de la scène.

   player.js appelle LinkiiKiosk.apply(kiosk, hooks) à chaque configuration reçue :
     hooks = { stage, render(layer, item, appOpts, onShow, onSkip), preview, send(events), native(name, json) } */
(function (w) {
  'use strict';

  var cfg = null, H = null, sig = '', root = null, touchEl = null, mainEl = null, homeEl = null, pageEl = null, backEl = null;
  var state = 'idle', timer = null, cleanup = null, visit = null, pageStart = 0, curTile = null, queue = [], flushT = null;

  function el(tag, cls, html) { var e = document.createElement(tag); if (cls) e.className = cls; if (html != null) e.innerHTML = html; return e; }
  function esc(s) { var d = document.createElement('div'); d.textContent = s == null ? '' : s; return d.innerHTML; }

  var CSS =
    '#kiosk{position:absolute;left:0;top:0;right:0;bottom:0;z-index:20;display:none;font-family:inherit;-webkit-user-select:none;user-select:none;touch-action:manipulation}' +
    '#kiosk.on{display:block}' +
    'body.k-open #idle{display:none!important}' +   /* l'écran « Rien à afficher » de la diffusion ne recouvre pas l'accueil */
    '#kiosk.nocursor,#kiosk.nocursor *{cursor:none!important}' +
    '#k-touch{position:absolute;left:0;top:0;right:0;bottom:0;display:none}' +
    '#k-touch .k-prompt{position:absolute;left:50%;bottom:4em;transform:translateX(-50%);background:rgba(11,31,58,.82);color:#fff;border:.15em solid rgba(255,255,255,.85);border-radius:3em;padding:.9em 2.4em;font-size:2.2em;white-space:nowrap;animation:kp 1.8s infinite}' +
    '@keyframes kp{50%{opacity:.45}}' +
    '#k-main{position:absolute;left:0;top:0;right:0;bottom:0;display:none;color:#0B1F3A;overflow:hidden}' +
    '#k-home{position:absolute;left:0;top:0;right:0;bottom:0;display:flex;flex-direction:column;padding:4.5em 5em 5em;overflow-y:auto;overflow-x:hidden;-webkit-overflow-scrolling:touch;touch-action:pan-y}' +
    '#k-home .k-logo{display:flex;gap:1em;align-items:center;font-size:2.2em;color:#5A6B80;font-weight:500;min-height:2.2em}' +
    '#k-home .k-logo img{height:2.6em;width:auto;max-width:14em;object-fit:contain}' +
    '#k-home h1{font-size:6em;margin:.5em 0 .15em;font-weight:500;line-height:1.1}' +
    '#k-home p{font-size:2.4em;margin:0 0 1em;color:#5A6B80}' +
    '#k-tiles{flex:1 0 auto;display:grid;gap:2.4em;grid-auto-rows:minmax(24em,1fr)}' +
    '#k-tiles.cols2{grid-template-columns:repeat(2,1fr)}#k-tiles.cols3{grid-template-columns:repeat(3,1fr)}#k-tiles.cols1{grid-template-columns:1fr}' +
    '#k-tiles.low{flex:1 0 52%;margin-top:auto}' +
    '.k-tile{position:relative;border:0;border-radius:1.6em;color:#fff;text-align:left;font-family:inherit;cursor:pointer;padding:2em 2.4em;display:flex;flex-direction:column;justify-content:flex-end;overflow:hidden;background-size:cover;background-position:center;-webkit-tap-highlight-color:transparent;transition:transform .1s}' +
    '.k-tile:active{transform:scale(.96)}' +
    '.k-tile.shade::before{content:"";position:absolute;left:0;top:0;right:0;bottom:0;background:linear-gradient(transparent 40%,rgba(0,0,0,var(--sh,.62)))}' +
    '.k-tile.shade.vt::before{background:linear-gradient(rgba(0,0,0,var(--sh,.62)),transparent 60%)}.k-tile.shade.vm::before{background:rgba(0,0,0,calc(var(--sh,.62)*.7))}' +
    '.k-tile b,.k-tile small{position:relative;display:block}' +
    '.k-tile b{font-size:3.4em;font-weight:500;line-height:1.15}.k-tile small{font-size:1.9em;font-weight:400;opacity:.9;margin-top:.35em}' +
    '#k-tiles.big .k-tile b{font-size:4.2em}#k-tiles.big .k-tile small{font-size:2.3em}' +
    '#k-page{position:absolute;left:0;top:0;right:0;bottom:0;display:none;background:#fff}' +
    '#k-page .k-layer{position:absolute;left:0;top:0;right:0;bottom:0;opacity:1}' +
    '#k-page .k-layer img,#k-page .k-layer video{width:100%;height:100%;object-fit:contain}' +
    '#k-back{position:absolute;left:50%;bottom:3em;transform:translateX(-50%);z-index:30;display:none;align-items:center;gap:.55em;background:rgba(11,31,58,.92);color:#fff;border:.1em solid rgba(255,255,255,.4);border-radius:3em;font-family:inherit;font-size:2.4em;line-height:1;padding:.55em .75em .55em .7em;cursor:pointer;box-shadow:0 .25em 1em rgba(0,0,0,.5);-webkit-tap-highlight-color:transparent}' +
    '#k-back:active{transform:translateX(-50%) scale(.96)}#k-back svg{width:1.1em;height:1.1em;flex:none}' +
    '#k-back .k-ring circle{fill:none;stroke-width:3.2}#k-back .k-ring .bg{stroke:rgba(255,255,255,.28)}#k-back .k-ring .fg{stroke:#E8710A;stroke-dasharray:56.5;stroke-dashoffset:0;transform:rotate(-90deg);transform-origin:center}' +
    '#k-back.run .k-ring .fg{animation:kring var(--ret,30s) linear forwards}@keyframes kring{to{stroke-dashoffset:56.5}}' +
    '#k-main.paged #k-home{display:none}#k-main.paged #k-page{display:block}#k-main.paged #k-back{display:flex}' +
    '#k-corner{position:absolute;left:0;top:0;width:9em;height:9em;z-index:40}' +
    '#k-pin{position:absolute;left:0;top:0;right:0;bottom:0;z-index:50;background:rgba(11,31,58,.86);display:none;place-items:center;color:#0B1F3A}' +
    '#k-pin.on{display:grid}' +
    '#k-pin .k-box{background:#fff;border-radius:1.6em;padding:3em 4em;width:36em;text-align:center}' +
    '#k-pin h2{font-size:3em;margin:0 0 .3em;font-weight:500}#k-pin p{font-size:1.8em;color:#5A6B80;margin:.2em 0 .8em}' +
    '#k-pin .k-dots{display:flex;gap:.8em;justify-content:center;margin:.6em 0 1em;min-height:1.4em}' +
    '#k-pin .k-dots i{width:1.4em;height:1.4em;border-radius:50%;background:#C5CED9}#k-pin .k-dots i.f{background:#0B1F3A}' +
    '#k-pin .k-keys{display:grid;grid-template-columns:repeat(3,1fr);gap:.7em}' +
    '#k-pin button{font-family:inherit;border:0;border-radius:.8em;background:#EDF1F5;color:#0B1F3A;font-size:3em;padding:.35em 0;cursor:pointer}' +
    '#k-pin button.k-act{background:#0B1F3A;color:#fff;font-size:2.2em;padding:.6em 0;margin-top:.5em;width:100%}' +
    '#k-pin button.k-ghost{background:#fff;border:.08em solid #C5CED9;color:#0B1F3A}' +
    '#k-pin .k-err{color:#C8372D;font-size:1.8em;min-height:1.2em}';

  function css() {
    if (document.getElementById('k-css')) return;
    var s = el('style'); s.id = 'k-css'; s.textContent = CSS; document.head.appendChild(s);
  }

  /* ---------- Statistiques : lots d'événements, gardés en local hors ligne (rien de personnel) ---------- */
  function loadQ() { try { return JSON.parse(localStorage.getItem('lk_kiosk') || '[]'); } catch (e) { return []; } }
  function saveQ(q) { try { localStorage.setItem('lk_kiosk', JSON.stringify(q.slice(-500))); } catch (e) {} }
  function ev(type, tile, title, sec, how) {
    if (H && H.preview) return;
    var q = loadQ(); q.push({ at: Date.now(), type: type, tile: tile || null, title: title || null, sec: sec || 0, how: how || null }); saveQ(q);
    if (!flushT) flushT = setTimeout(flush, 15000);
  }
  function flush() {
    flushT = null;
    var q = loadQ(); if (!q.length || !H || !H.send) return;
    H.send(q).then(function () { saveQ(loadQ().slice(q.length)); }, function () {});
  }

  /* ---------- Accès technicien : appui long de 5 s dans le coin en haut à gauche, puis le code PIN de l'organisation (9999 par défaut) ----------
     Permet de lever le verrou (quitter le mode borne) ou de redémarrer. Le visiteur ne voit rien : le coin est invisible. */
  var pinEl = null, pinTimer = null, pinBuf = '';
  function pinWanted() { var p = H && H.techPin ? H.techPin() : null; return p && typeof p.pin === 'string' ? p.pin : '9999'; }
  function pinClose() { if (pinEl && pinEl.parentNode) pinEl.parentNode.removeChild(pinEl); pinEl = null; pinBuf = ''; }
  function pinOpen() {
    if (pinEl) return;
    pinBuf = '';
    pinEl = el('div'); pinEl.id = 'k-pin'; pinEl.className = 'on';
    function draw(msg) {
      var want = pinWanted().length || 4, dots = '';
      for (var i = 0; i < Math.max(want, 4); i++) dots += '<i class="' + (i < pinBuf.length ? 'f' : '') + '"></i>';
      var keys = ''; ['1','2','3','4','5','6','7','8','9','','0','⌫'].forEach(function (c) { keys += c ? '<button type="button" data-k="' + c + '">' + c + '</button>' : '<span></span>'; });
      pinEl.innerHTML = '<div class="k-box"><h2>Accès technicien</h2><p>Code PIN</p><div class="k-dots">' + dots + '</div><div class="k-err">' + (msg || '') + '</div><div class="k-keys">' + keys + '</div><button type="button" class="k-act k-ghost" data-k="x">Annuler</button></div>';
    }
    function menu() {
      pinEl.innerHTML = '<div class="k-box"><h2>Accès technicien</h2><p>Que voulez-vous faire ?</p><button type="button" class="k-act" data-k="unlock">Quitter le mode borne</button><button type="button" class="k-act" data-k="restart">Redémarrer la borne</button><button type="button" class="k-act k-ghost" data-k="x">Fermer</button></div>';
    }
    pinEl.addEventListener('click', function (e) {
      var b = e.target.closest('button'); if (!b) return;
      var c = b.getAttribute('data-k');
      if (c === 'x') return pinClose();
      if (c === 'unlock') { if (H.native) H.native('kioskUnlock', ''); pinClose(); setState('idle'); return; }
      if (c === 'restart') { if (H.native) H.native('restart', ''); pinClose(); return; }
      if (c === '⌫') pinBuf = pinBuf.slice(0, -1); else if (pinBuf.length < 8) pinBuf += c;
      var want = pinWanted();
      if (want === '') return menu();
      if (pinBuf.length === want.length) { if (pinBuf === want) return menu(); pinBuf = ''; return draw('Code incorrect'); }
      draw();
    });
    root.appendChild(pinEl);
    if (pinWanted() === '') menu(); else draw();
  }
  function corner() {
    var c = el('div'); c.id = 'k-corner';
    c.addEventListener('pointerdown', function (e) { e.stopPropagation(); clearTimeout(pinTimer); pinTimer = setTimeout(pinOpen, 5000); }, true);
    ['pointerup', 'pointercancel', 'pointerleave'].forEach(function (n) { c.addEventListener(n, function () { clearTimeout(pinTimer); }); });
    return c;
  }

  /* ---------- Construction ---------- */
  function build() {
    css();
    root = document.getElementById('kiosk');
    if (!root) { root = el('div'); root.id = 'kiosk'; H.stage.appendChild(root); }
    root.innerHTML = '';
    root.className = (cfg.hideCursor ? 'nocursor ' : '') + 'on';
    touchEl = el('div'); touchEl.id = 'k-touch';
    touchEl.appendChild(el('div', 'k-prompt', 'Touchez l’écran pour commencer'));
    mainEl = el('div'); mainEl.id = 'k-main'; mainEl.style.background = cfg.bg || '#fff';
    homeEl = el('div'); homeEl.id = 'k-home';
    pageEl = el('div'); pageEl.id = 'k-page';
    backEl = el('button'); backEl.id = 'k-back'; backEl.type = 'button'; backEl.setAttribute('aria-label', 'Retour à l\x27accueil');
    // pilule « Accueil » en bas au centre ; l\x27anneau se vide pendant le délai avant le retour automatique (il repart à chaque toucher)
    backEl.innerHTML = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"><path d="M15 5l-7 7 7 7"/></svg><span>Accueil</span><svg class="k-ring" viewBox="0 0 24 24"><circle class="bg" cx="12" cy="12" r="9"/><circle class="fg" cx="12" cy="12" r="9"/></svg>';
    mainEl.appendChild(homeEl); mainEl.appendChild(pageEl); mainEl.appendChild(backEl);
    root.appendChild(touchEl); root.appendChild(mainEl);
    if (cfg.lockApp && !(H && H.preview)) root.appendChild(corner());   // borne verrouillée : accès technicien par le coin
    drawHome();
    // un toucher n'importe où repart pour un tour de délai ; sur l'écran d'attente, il ouvre l'accueil
    root.addEventListener('pointerdown', function (e) {
      if (state === 'idle') { e.preventDefault(); e.stopPropagation(); ev('touch'); openHome(true); return; }
      arm();
    }, true);
    root.addEventListener('contextmenu', function (e) { e.preventDefault(); });
    if (cfg.noZoom) {
      root.addEventListener('gesturestart', function (e) { e.preventDefault(); });
      root.addEventListener('touchmove', function (e) { if (e.touches && e.touches.length > 1) e.preventDefault(); }, { passive: false });
      root.addEventListener('wheel', function (e) { if (e.ctrlKey) e.preventDefault(); }, { passive: false });
    }
    backEl.addEventListener('click', function () { goHome('back'); });
    setState('idle');
    if (H.preview) openHome(false);   // simulateur : on voit l'accueil, ce que voit le visiteur (la diffusion reprend après le délai sans toucher)
  }

  function today() {
    var d = new Date(); function p(n) { return ('0' + n).slice(-2); }
    return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate());
  }
  function shown(t) { var d = today(); return !(t.from && t.from > d) && !(t.to && t.to < d); }

  function drawHome() {
    var tiles = (cfg.tiles || []).filter(shown);
    var ts = cfg.tileStyle || {};   // aspect commun des tuiles (paramètres avancés de la fenêtre de la borne)
    var cols = ts.cols > 0 ? ts.cols : tiles.length <= 1 ? 1 : (H.portrait() ? 2 : (tiles.length <= 4 ? 2 : 3));
    var logo = cfg.logo ? '<img src="' + esc(cfg.logo) + '" alt="">' : '';
    homeEl.innerHTML = '<div class="k-logo">' + logo + '</div><h1>' + esc(cfg.title) + '</h1><p>' + esc(cfg.prompt) + '</p>';
    var h1 = homeEl.querySelector('h1'), pp = homeEl.querySelector('p');   // police et couleur du titre et du message d'invite (réglées dans la fenêtre de la borne)
    if (cfg.titleFont) h1.style.fontFamily = cfg.titleFont;
    if (cfg.titleColor) h1.style.setProperty('color', cfg.titleColor, 'important');
    if (cfg.promptFont) pp.style.fontFamily = cfg.promptFont;
    if (cfg.promptColor) pp.style.setProperty('color', cfg.promptColor, 'important');
    var g = el('div'); g.id = 'k-tiles'; g.className = 'cols' + cols + (cfg.accessible ? ' low' : '') + (cfg.bigText ? ' big' : '');
    if (ts.gap) g.style.gap = ts.gap + 'em';
    if (ts.shade != null) g.style.setProperty('--sh', ts.shade);
    var vj = { top: 'flex-start', middle: 'center', bottom: 'flex-end' }[ts.valign] || 'flex-end';
    tiles.forEach(function (t) {
      var b = el('button', 'k-tile' + (t.image ? ' shade' : '') + (ts.valign === 'top' ? ' vt' : ts.valign === 'middle' ? ' vm' : ''));
      b.type = 'button';
      b.style.backgroundColor = t.color || '#3B82C4';
      if (t.image) b.style.backgroundImage = 'url(' + t.image + ')';
      b.innerHTML = '<b>' + esc(t.title) + '</b>' + (t.subtitle ? '<small>' + esc(t.subtitle) + '</small>' : '');
      if (ts.radius != null) b.style.borderRadius = ts.radius + 'em';
      if (ts.font) b.style.fontFamily = ts.font;
      if (ts.color) b.style.setProperty('color', ts.color, 'important');
      b.style.textAlign = ts.align || 'left'; b.style.justifyContent = vj;
      var sc = (t.scale || 1) * (ts.scale || 1);   // taille propre à la tuile x taille commune
      var tb = b.querySelector('b'), tsm = b.querySelector('small');
      if (ts.weight) tb.style.fontWeight = ts.weight;
      if (sc !== 1) {
        tb.style.fontSize = ((cfg.bigText ? 4.2 : 3.4) * sc) + 'em';
        if (tsm) tsm.style.fontSize = ((cfg.bigText ? 2.3 : 1.9) * sc) + 'em';
      }
      b.addEventListener('click', function () { openTile(t); });
      g.appendChild(b);
    });
    homeEl.appendChild(g);
    mainEl.style.background = cfg.bg || '#fff';
  }

  /* ---------- États : idle (diffusion) · home · page ---------- */
  function setState(s) {
    state = s;
    document.body.classList.toggle('k-open', s !== 'idle');
    touchEl.style.display = s === 'idle' ? 'block' : 'none';
    mainEl.style.display = s === 'idle' ? 'none' : 'block';
    mainEl.className = s === 'page' ? 'paged' : '';
    if (H.native) H.native('kioskState', s);
    arm();
  }

  function arm() {
    clearTimeout(timer); timer = null;
    if (backEl) {   // anneau du bouton Accueil : repart à zéro à chaque armement du délai
      backEl.classList.remove('run');
      if (state === 'page') { backEl.style.setProperty('--ret', Math.max(10, cfg.returnSec || 30) + 's'); void backEl.offsetWidth; backEl.classList.add('run'); }
    }
    if (state === 'page') timer = setTimeout(function () { goHome('auto'); }, Math.max(10, cfg.returnSec || 30) * 1000);
    else if (state === 'home') timer = setTimeout(goIdle, Math.max(30, cfg.idleSec || 120) * 1000);
  }

  function stopPage() {
    if (cleanup) { try { cleanup(); } catch (e) {} cleanup = null; }
    if (pageEl) pageEl.innerHTML = '';
    if (cfg && cfg.wipe) {   // remise à zéro : le contenu est recréé à chaque ouverture (page 1, vidéo à zéro) ; historique et cookies des sites effacés par l'application
      if (H.native && state === 'page') H.native('kioskWipe', '');
    }
  }

  function endVisit(how) {
    if (!visit) return;
    ev('visit', null, null, Math.round((Date.now() - visit) / 1000), how);
    visit = null;
  }

  function openHome(fresh) {
    if (fresh && !visit) visit = Date.now();
    drawHome();
    setState('home');
  }

  function goHome(how) {
    if (curTile) { stopPage(); curTile = null; }
    if (how === 'auto' || how === 'back') pendingHow = how;
    drawHome();
    setState('home');
  }
  var pendingHow = null;

  function goIdle() {
    if (curTile) { stopPage(); curTile = null; }
    endVisit(pendingHow || 'auto'); pendingHow = null;
    if (cfg.wipe && H.native) H.native('kioskWipe', '');
    setState('idle');
  }

  function openTile(t) {
    curTile = t;
    pageEl.innerHTML = '';
    var layer = el('div', 'k-layer'); pageEl.appendChild(layer);
    ev('tile', t.id, t.title);
    pageStart = Date.now();
    H.render(layer, t.item, null, function (clean) {
      cleanup = clean || null;
      // son des vidéos : autorisé et réglé par la borne (le player diffuse muet par défaut)
      [].forEach.call(layer.querySelectorAll('video'), function (v) { v.loop = false; v.muted = !cfg.sound; v.volume = Math.max(0, Math.min(1, (cfg.volume == null ? 60 : cfg.volume) / 100)); try { v.play(); } catch (e) {} });
      if (t.item && t.item.qr) ev('qr', t.id, t.title);   // le QR est affiché : le comptage des scans se fait côté serveur ; ici l'affichage seul (statistique d'usage)
    }, function () { goHome('auto'); });
    setState('page');
  }

  function teardown() {
    clearTimeout(timer); timer = null; clearTimeout(pinTimer); pinEl = null;
    if (curTile) { stopPage(); curTile = null; }
    if (root && root.parentNode) root.parentNode.removeChild(root);
    root = touchEl = mainEl = homeEl = pageEl = backEl = null;
    visit = null; state = 'idle';
    document.body.classList.remove('k-open');
  }

  /* ---------- Entrée : configuration reçue (ou retirée : l'écran redevient un écran de diffusion) ---------- */
  function apply(k, hooks) {
    H = hooks;
    var s = k ? JSON.stringify(k) : '';
    if (s === sig) return;
    sig = s;
    teardown();
    cfg = k || null;
    if (!cfg) {
      if (H.native) H.native('kiosk', '');
      return;
    }
    build();
    if (H.native) H.native('kiosk', JSON.stringify({ lock: !!cfg.lockApp, noZoom: !!cfg.noZoom, hideCursor: !!cfg.hideCursor, wake: !!cfg.wake }));
  }

  /* Fichiers des tuiles, à précharger comme ceux des listes (menu utilisable hors ligne). */
  function items(k) {
    var l = [];
    ((k && k.tiles) || []).forEach(function (t) { if (t.item) l.push(t.item); if (t.image) l.push({ url: t.image }); });
    if (k && k.logo) l.push({ url: k.logo });
    return l;
  }

  /* Un toucher réveille la borne pendant la veille : le player la rallume (player.js), l'accueil s'ouvre au toucher suivant. */
  function active() { return !!cfg; }

  w.LinkiiKiosk = { apply: apply, items: items, active: active, flush: flush };
})(window);
