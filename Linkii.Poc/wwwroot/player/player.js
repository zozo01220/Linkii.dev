/* Player Linkii (POC) — JS volontairement "ancien" (var, promesses, pas d'async/await) pour les WebView anciennes. */
(function () {
  'use strict';

  /* ---------- Mode aperçu (simulateur du back-office) ----------
     /player/?preview=jeton : le player est affiché dans le back-office. Il lit les mêmes données que l'écran avec un jeton d'aperçu,
     mais n'écrit rien dans la mémoire du navigateur (un écran appairé sur le même poste n'est pas touché), ne compte aucune diffusion,
     n'accuse aucune réception et ne reçoit pas les ordres envoyés aux écrans. &at=ms : date et heure simulées. &sound=1 : son permis. */
  var Q = location.search;
  var PREVIEW = (/[?&]preview=([A-Za-z0-9_-]+)/.exec(Q) || [])[1] || null;
  var SIM_AT = PREVIEW ? parseInt((/[?&]at=(\d+)/.exec(Q) || [])[1] || '0', 10) : 0;
  var SOUND = !PREVIEW || /[?&]sound=1/.test(Q);
  function memoryStore() {
    var m = {};
    return {
      getItem: function (k) { return Object.prototype.hasOwnProperty.call(m, k) ? m[k] : null; },
      setItem: function (k, v) { m[k] = String(v); },
      removeItem: function (k) { delete m[k]; }
    };
  }
  // Date simulée : toute la page (horloges, agendas, périodes de validité) vit à cette date, l'heure continue d'avancer.
  if (SIM_AT) (function (D, delta) {
    function SimDate() {
      var a = [null].concat(Array.prototype.slice.call(arguments));
      var d = arguments.length ? new (Function.prototype.bind.apply(D, a))() : new D(D.now() + delta);
      return this instanceof SimDate ? d : d.toString();
    }
    SimDate.prototype = D.prototype;
    SimDate.now = function () { return D.now() + delta; };
    SimDate.parse = D.parse; SimDate.UTC = D.UTC;
    window.Date = SimDate;
  })(window.Date, SIM_AT - Date.now());

  var LS = PREVIEW ? memoryStore() : window.localStorage;
  var CACHE = 'linkii-media-v1';
  var state = {
    screenId: LS.getItem('lk_screen'),
    token: LS.getItem('lk_token'),
    current: null,      // playlist en cours de diffusion {version, items, screen, settings}
    pending: null,      // playlist prête, à appliquer entre deux contenus
    build: null,        // version du code du player sur le serveur : quand elle change (mise à jour de Linkii), la page se recharge
    acked: LS.getItem('lk_acked'),
    index: 0,
    active: 'layerA',
    timer: null,
    cleanup: null,
    overlayStops: []
  };
  var cfg = { orientation: 'landscape', resolution: 'auto', timezone: null, reloadHour: 4, wall: null };   // wall : place de l'écran dans un mur {cols, rows, col, row}

  function $(id) { return document.getElementById(id); }
  function deviceSize() {
    var r = window.devicePixelRatio || 1;
    return { w: Math.round(screen.width * r), h: Math.round(screen.height * r) };
  }

  /* ---------- Indicateur hors ligne ---------- */
  var netFails = 0;
  function showOffline() { $("offline").style.display = netFails >= 2 ? "block" : "none"; }
  function setOnline(ok) { netFails = ok ? 0 : netFails + 1; showOffline(); }
  function markOffline() { netFails = 2; showOffline(); }
  window.addEventListener("offline", markOffline);
  window.addEventListener("online", function () { if (PREVIEW) refresh(); else api("/api/player/version").catch(function () {}); });

  /* ---------- HTTP ---------- */
  var PLAYLIST_API = PREVIEW ? '/api/preview/playlist' : '/api/player/playlist';
  var DATA_API = PREVIEW ? '/api/preview/data/' : '/api/data/';
  function api(path, opts) {
    opts = opts || {};
    opts.headers = opts.headers || {};
    if (state.token) opts.headers['X-Token'] = state.token;
    if (PREVIEW) opts.headers['X-Preview'] = PREVIEW;
    var t0 = Date.now();
    return fetch(path, opts).then(function (r) { setOnline(true); return r; }, function (e) { setOnline(false); throw e; }).then(function (r) {
      if (PREVIEW && (r.status === 401 || r.status === 404) && path === PLAYLIST_API) {   // aperçu expiré, ou écran / liste supprimés
        $('suspended-title').textContent = r.status === 401 ? 'Aperçu expiré' : 'Plus rien à simuler';
        $('suspended-text').textContent = r.status === 401 ? 'Fermez puis rouvrez le simulateur.' : "Cet écran ou cette liste n'existe plus.";
        $('suspended').style.display = 'flex';
        throw new Error(String(r.status));
      }
      if (r.status === 401 && !PREVIEW) { unpair(); throw new Error('401'); }
      if (r.status === 403) {   // client ou revendeur suspendu, ou essai gratuit terminé
        return r.json().catch(function () { return {}; }).then(function (d) {
          var trial = d && d.reason === 'trial';
          $('suspended-title').textContent = trial ? 'Essai terminé' : 'Service suspendu';
          $('suspended-text').textContent = trial ? "L'essai gratuit de cet espace est terminé. Contactez votre administrateur." : 'Contactez votre administrateur.';
          $('suspended').style.display = 'flex';
          throw new Error('403');
        });
      }
      $('suspended').style.display = 'none';
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.text().then(function (t) {
        var d = t ? JSON.parse(t) : null;
        if (d && d.serverNow) noteClock(t0, Date.now(), d.serverNow);
        return d;
      });
    });
  }

  /* ---------- Horloge du serveur (lecture synchronisée entre écrans) ----------
     Chaque réponse du serveur donne son heure : l'écart avec l'horloge de l'appareil est estimé au milieu de l'aller-retour.
     On garde la mesure la plus fiable (aller-retour le plus court) et on la renouvelle toutes les 2 minutes. Sans réseau, la dernière estimation reste valable. */
  var clock = { offset: parseFloat(LS.getItem('lk_clock')) || 0, rtt: 1e9, at: 0 };
  function noteClock(t0, t1, srv) {
    var rtt = t1 - t0, now = Date.now();
    if (rtt > 3000) return;   // réponse trop lente : mesure inutilisable
    if (rtt <= clock.rtt + 20 || now - clock.at > 120000) {
      clock.offset = srv - (t0 + t1) / 2; clock.rtt = rtt; clock.at = now;
      try { LS.setItem('lk_clock', String(clock.offset)); } catch (e) {}
    }
  }
  function serverNow() { return Date.now() + clock.offset; }

  // Position commune dans une boucle de contenus : se déduit de l'heure du serveur et de l'origine de la zone. { index, left } (ms restantes sur le contenu).
  // start : heure commune du début de ce créneau (origine de la lecture du contenu, pour les apps).
  function syncPick(slides, epoch) {
    var total = 0, k, now = serverNow();
    for (k = 0; k < slides.length; k++) total += Math.max(1, slides[k].durationSec) * 1000;
    var t = (((now - epoch) % total) + total) % total, acc = 0;
    for (k = 0; k < slides.length; k++) {
      var d = Math.max(1, slides[k].durationSec) * 1000;
      if (t < acc + d) return { index: k, left: acc + d - t, start: now - (t - acc) };
      acc += d;
    }
    return { index: 0, left: total, start: now - t };
  }

  // Options passées au rendu d'un contenu. Zone synchronisée : origine commune (le contenu se déroule à l'heure commune,
  // sans fin anticipée : c'est le créneau qui le remplace). Sinon : fin décidée par l'app pour une durée « content ».
  function slideOpts(item, origin, onEnded, onFailed) {
    if (origin != null) return { sync: { origin: origin, now: serverNow } };
    return item.durationMode === 'content' ? { onEnded: onEnded, onFailed: onFailed } : null;
  }
  // Durée à laisser à un contenu affiché : jusqu'à la fin de son créneau commun (1 ms s'il est déjà en retard : le suivant le remplace aussitôt).
  function syncLeft(slides, item, epoch) {
    var p = syncPick(slides, epoch);
    return slides[p.index] === item ? p.left : 1;
  }

  /* ---------- Marque du revendeur (white-label) : écran d'appairage compris, mémorisée pour un démarrage hors ligne ---------- */
  var WORDMARK = '<span class="wm" aria-label="Linkii">link<span class="i"></span><span class="i s"></span></span>';
  function applyBrand(b) {
    if (!b) return;
    try { LS.setItem('lk_brand', JSON.stringify(b)); } catch (e) {}
    // logo du revendeur + nom ; sans logo : wordmark Linkii, ou initiale du revendeur + nom
    var html = b.logoUrl ? '<img src="' + esc(b.logoUrl) + '" alt="" style="height:8vmin;max-width:30vmin;object-fit:contain"> ' + esc(b.name)
             : b.name === 'Linkii' ? WORDMARK
             : '<span class="mark">' + esc(b.name.charAt(0).toUpperCase()) + '</span> ' + esc(b.name);
    var els = document.querySelectorAll('.brand');
    for (var i = 0; i < els.length; i++) els[i].innerHTML = html;
    document.documentElement.style.setProperty('--accent', b.color || '#0B1F3A');
    var tag = $('powered');
    if (tag) tag.textContent = b.poweredBy ? 'Propulsé par Linkii' : 'Affichage dynamique en libre-service';
  }
  try { applyBrand(JSON.parse(LS.getItem('lk_brand') || 'null')); } catch (e) {}

  function unpair() {
    LS.removeItem('lk_token'); LS.removeItem('lk_screen'); LS.removeItem('lk_playlist'); LS.removeItem('lk_acked'); LS.removeItem('lk_brand');
    location.reload();
  }

  /* ---------- Service worker (démarrage hors ligne) ---------- */
  if ('serviceWorker' in navigator && !PREVIEW) {
    navigator.serviceWorker.register('sw.js').catch(function () {});
  }

  /* ---------- Téléphone : écran allumé, plein écran au toucher, installation sur l'écran d'accueil ---------- */
  function installed() {
    return (window.matchMedia && window.matchMedia('(display-mode: fullscreen), (display-mode: standalone)').matches) || window.navigator.standalone === true;
  }
  function isIos() {
    var ua = navigator.userAgent || '';
    return /iPad|iPhone|iPod/.test(ua) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
  }

  // L'écran ne doit pas s'éteindre (nécessite https). Le verrou saute quand la page passe en arrière-plan : on le redemande au retour.
  function keepAwake() {
    if (PREVIEW || !navigator.wakeLock || document.visibilityState !== 'visible') return;
    navigator.wakeLock.request('screen').catch(function () {});
  }
  keepAwake();
  document.addEventListener('visibilitychange', keepAwake);

  // Hors application installée, un toucher passe en plein écran (Android ; Safari iPhone ne le permet pas : il faut installer).
  if (!PREVIEW && !installed() && ('ontouchstart' in window || navigator.maxTouchPoints > 0)) {
    document.addEventListener('click', function () {
      var el = document.documentElement, rq = el.requestFullscreen || el.webkitRequestFullscreen;
      if (rq && !document.fullscreenElement && !document.webkitFullscreenElement) {
        try { var p = rq.call(el); if (p && p.catch) p.catch(function () {}); } catch (e) {}
      }
      keepAwake();
    });
  }

  var installEvent = null;
  var SHARE = '<svg viewBox="0 0 24 24" style="width:3vmin;height:3vmin;vertical-align:-.6vmin;fill:none;stroke:currentColor;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round"><path d="M12 15V3M8 7l4-4 4 4M5 12v7a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-7"/></svg>';
  function showInstall() {
    var el = $('install');
    if (!el) return;
    if (PREVIEW || installed() || (!installEvent && !isIos())) { el.style.display = 'none'; return; }
    el.style.display = 'block';
    el.innerHTML = installEvent
      ? '<button type="button" id="installBtn" style="font:inherit;color:#fff;background:transparent;border:.25vmin solid #3B82C4;border-radius:.6vmin;padding:1.6vmin 3vmin">Installer en plein écran</button>'
      : '<b>Plein écran :</b> touchez ' + SHARE + ' puis « Sur l\'écran d\'accueil », et ouvrez « Écran » depuis l\'accueil.';
    var b = $('installBtn');
    if (b) b.onclick = function () {
      var ev = installEvent; installEvent = null; showInstall();
      ev.prompt();
    };
  }
  window.addEventListener('beforeinstallprompt', function (e) { if (PREVIEW) return; e.preventDefault(); installEvent = e; showInstall(); });
  window.addEventListener('appinstalled', function () { installEvent = null; showInstall(); });
  showInstall();

  /* ---------- Scène : format, résolution, rotation ---------- */
  function layout() {
    var stage = $('stage');
    var vw = window.innerWidth, vh = window.innerHeight;
    var portrait = cfg.orientation === 'portrait';
    // écran physiquement dans l'autre sens que le format demandé : on pivote la scène
    var rot = PREVIEW ? 0 : (portrait && vw > vh) ? 90 : (!portrait && vh > vw) ? -90 : 0;   // aperçu : la fenêtre a déjà le format de l'écran
    var availW = rot ? vh : vw, availH = rot ? vw : vh;
    var W, H, phone = false;
    if (cfg.resolution && cfg.resolution !== 'auto') {
      var p = cfg.resolution.split('x');
      var a = parseInt(p[0], 10), b = parseInt(p[1], 10);
      W = portrait ? Math.min(a, b) : Math.max(a, b);
      H = portrait ? Math.max(a, b) : Math.min(a, b);
    } else if (availW < 1200) {
      // « Auto » sur un petit écran (téléphone) : toile virtuelle de la largeur d'un écran d'affichage (1080 en portrait, 1920 en paysage),
      // de la hauteur du téléphone. Les tailles restent proportionnées à l'écran et rien n'est rogné ni entouré de bandes noires.
      var dw = portrait ? 1080 : 1920;
      W = dw; H = Math.round(availH * dw / availW);
      phone = true;
    } else { W = availW; H = availH; }
    stage.classList.toggle('phone', phone);   // images et vidéos remplissent l'écran du téléphone (recadrées) au lieu de laisser des bandes
    var s = Math.min(availW / W, availH / H);
    // Mur d'écrans : la scène est l'image de tout le mur (colonnes × lignes écrans) ; elle est décalée pour que seule
    // la portion de cet écran (sa colonne, sa ligne) soit au centre de l'écran. Le reste déborde, hors de la vue.
    var wall = cfg.wall, SW = W, SH = H, dx = 0, dy = 0;
    if (wall && wall.cols > 0 && wall.rows > 0) {
      SW = W * wall.cols; SH = H * wall.rows;
      if (wall.whole) s = Math.min(availW / SW, availH / SH);   // simulateur du mur : tout le mur dans la fenêtre
      else {
        dx = W * (wall.cols - 1 - 2 * wall.col) / 2;
        dy = H * (wall.rows - 1 - 2 * wall.row) / 2;
      }
    }
    stage.style.width = SW + 'px';
    stage.style.height = SH + 'px';
    stage.style.fontSize = (SW / 100) + 'px';   // 1em = 1 % de la largeur de la scène
    stage.style.webkitTransform = stage.style.transform =
      'translate(-50%, -50%) rotate(' + rot + 'deg) scale(' + s + ')' + (dx || dy ? ' translate(' + dx + 'px, ' + dy + 'px)' : '');
    sizeZones();
  }
  window.addEventListener('resize', layout);

  function applyConfig(pl) {
    if (!pl) return;
    if (pl.screen) { cfg.orientation = pl.screen.orientation || 'landscape'; cfg.resolution = pl.screen.resolution || 'auto'; }
    if (pl.settings) { cfg.timezone = pl.settings.timezone || null; if (pl.settings.reloadHour != null) cfg.reloadHour = pl.settings.reloadHour; }
    cfg.wall = pl.wall || null;
    layout();
  }

  /* ---------- Identification (assemblage d'un mur) : grand numéro pendant 5 s ---------- */
  var identTimer = null;
  function identify(label) {
    var el = $('ident');
    if (!el) {
      el = document.createElement('div');
      el.id = 'ident';
      el.style.cssText = 'position:fixed;left:0;top:0;right:0;bottom:0;z-index:1000;display:flex;align-items:center;justify-content:center;' +
        'background:rgba(11,31,58,.92);color:#fff;font-weight:500;font-size:60vmin;line-height:1;font-family:"IBM Plex Sans","Segoe UI",Arial,sans-serif';
      document.body.appendChild(el);
    }
    el.textContent = label;
    el.style.display = 'flex';
    clearTimeout(identTimer);
    identTimer = setTimeout(function () { el.style.display = 'none'; }, 5000);
  }

  /* ---------- Appairage ---------- */
  function setCode(c) {
    var el = $('code');
    if (el.getAttribute('data-c') === c) return;
    el.setAttribute('data-c', c);
    el.innerHTML = '';
    for (var i = 0; i < c.length; i++) {
      var s = document.createElement('span'); s.textContent = c.charAt(i); s.style.animationDelay = (i * 60) + 'ms'; el.appendChild(s);
    }
  }

  function startPairing() {
    $('pairing').style.display = 'flex';
    function begin() {
      var d = deviceSize();
      var m = /[?&]r=([a-z0-9-]+)/i.exec(location.search);   // ?r=revendeur : marque de l'écran d'appairage sur l'adresse neutre
      return fetch('/api/pairing/start' + (m ? '?r=' + m[1] : ''), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ w: d.w, h: d.h }) })
        .then(function (r) { return r.json(); }).then(function (d) {
          state.screenId = d.screenId; LS.setItem('lk_screen', d.screenId); setCode(d.code); applyBrand(d.brand);
        });
    }
    function poll() {
      if (!state.screenId) { begin().catch(function () {}).then(function () { setTimeout(poll, 3000); }); return; }
      fetch('/api/pairing/status?screenId=' + state.screenId).then(function (r) {
        if (r.status === 404) { state.screenId = null; LS.removeItem('lk_screen'); return null; }
        return r.json();
      }).then(function (d) {
        if (d && d.paired) {
          state.token = d.token; LS.setItem('lk_token', d.token);
          $('pairing').style.display = 'none';
          $('ok').style.display = 'flex'; setTimeout(function () { $('ok').style.display = 'none'; }, 3500);
          startPlayback();
          return;
        }
        if (d && d.code) setCode(d.code);
        setTimeout(poll, 3000);
      }).catch(function () { setTimeout(poll, 3000); });
    }
    poll();
  }

  /* ---------- Cache des médias ---------- */
  function prefetch(items) {
    if (!window.caches || PREVIEW) return Promise.resolve();   // aperçu : lecture en ligne, le cache d'un écran du même poste n'est pas touché
    return caches.open(CACHE).then(function (cache) {
      var urls = [];
      items.forEach(function (i) { if (i.url) urls.push(i.url); (i.urls || []).forEach(function (u) { if (urls.indexOf(u) === -1) urls.push(u); }); });   // apps de la médiathèque : tous leurs fichiers
      var adds = urls.map(function (u) {
        return cache.match(u).then(function (hit) {
          if (hit) return;
          return fetch(u, { headers: { 'X-Token': state.token || '' } }).then(function (r) { if (r.ok) return cache.put(u, r); });   // les médias sont protégés : jeton de l'écran
        }).catch(function () { /* quota atteint ou réseau coupé : ce média sera lu en ligne */ });
      });
      return Promise.all(adds).then(function () {
        // purge : on retire les médias qui ne sont plus dans la playlist
        return cache.keys().then(function (keys) {
          return Promise.all(keys.map(function (k) {
            var p = new URL(k.url).pathname;
            if (urls.indexOf(p) === -1) return cache.delete(k);
          }));
        });
      });
    });
  }

  /* ---------- Lecture depuis le cache local (indépendante du service worker) ---------- */
  // Le média téléchargé est relu depuis le cache du navigateur sous forme de blob : lecture hors ligne fiable,
  // sans réseau ni service worker, pour les images comme pour les vidéos (tous formats convertis en MP4 H.264 par le serveur).
  function mediaSrc(url) {
    var online = PREVIEW ? url : url + '?t=' + encodeURIComponent(state.token || '');   // repli en ligne : jeton de l'écran dans l'adresse (aperçu : session du back-office)
    if (PREVIEW || !window.caches || !window.URL || !URL.createObjectURL) return Promise.resolve({ src: online, revoke: function () {} });
    return caches.open(CACHE).then(function (c) { return c.match(url); }).then(function (hit) {
      if (!hit) return { src: online, revoke: function () {} };
      return hit.blob().then(function (b) {
        var u = URL.createObjectURL(b);
        return { src: u, revoke: function () { try { URL.revokeObjectURL(u); } catch (e) {} } };
      });
    }).catch(function () { return { src: online, revoke: function () {} }; });
  }

  /* ---------- Synchronisation playlist ---------- */
  var refreshing = false;
  function refresh() {
    if (refreshing) return;
    refreshing = true;
    api(PLAYLIST_API).then(function (pl) {
      if (PREVIEW) previewPrepare(pl);
      applyBrand(pl.brand);
      if (state.current && pl.version === state.current.version && !state.pending) return prefetch(pl.items);   // même version : on re-télécharge seulement ce que le navigateur aurait purgé du cache
      if (state.pending && pl.version === state.pending.version) return;
      return prefetch(pl.items).then(function () {
        state.pending = pl;
        LS.setItem('lk_playlist', JSON.stringify(pl));
        if (!state.timer) next(); // rien ne tourne : on démarre tout de suite
      });
    }).catch(function () {}).then(function () { refreshing = false; });
  }

  function ack() {
    if (PREVIEW || !state.current || state.acked === state.current.version) return;
    var v = state.current.version, d = deviceSize();
    api('/api/player/ack', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ version: v, w: d.w, h: d.h }) })
      .then(function () { state.acked = v; LS.setItem('lk_acked', v); }).catch(function () {});
  }

  /* ---------- Utilitaires des apps ---------- */
  function pad(n) { return ('0' + n).slice(-2); }

  /* ---------- Listes de données des apps : agenda ---------- */
  function tzOpts(o) { o = o || {}; if (cfg.timezone) o.timeZone = cfg.timezone; return o; }
  function fmtT(ms) {
    try { return new Date(ms).toLocaleTimeString('fr-CH', tzOpts({ hour: '2-digit', minute: '2-digit' })); }
    catch (e) { var d = new Date(ms); return pad(d.getHours()) + ':' + pad(d.getMinutes()); }
  }
  function fmtDay(ms, o) {
    try { return new Date(ms).toLocaleDateString('fr-CH', tzOpts(o)); } catch (e) { return new Date(ms).toDateString(); }
  }
  function dayKey(ms) { return fmtDay(ms, { year: 'numeric', month: '2-digit', day: '2-digit' }); }
  function todayIso() {   // aaaa-mm-jj dans le fuseau du tenant
    try { return new Date().toLocaleDateString('sv-SE', tzOpts()); }
    catch (e) { var d = new Date(); return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()); }
  }
  function esc(s) { var d = document.createElement('div'); d.textContent = s == null ? '' : s; return d.innerHTML; }
  function cap(s) { return s ? s.charAt(0).toUpperCase() + s.slice(1) : s; }

  function parseEvents(d) {
    return (d && d.events ? d.events : []).map(function (e) {
      return { title: e.title, start: Date.parse(e.start), end: Date.parse(e.end), allDay: e.allDay, location: e.location };
    }).sort(function (a, b) { return a.start - b.start; });
  }

  function staleNote(d) {
    if (!d || !d.fetchedAt) return '';
    var age = Date.now() - Date.parse(d.fetchedAt);
    return age > 10 * 60 * 1000 ? '<div class="stale">⚠ Données de ' + fmtT(Date.parse(d.fetchedAt)) + '</div>' : '';
  }

  var DRAW = {
    agenda: function (el, item, d, compact) {
      var title = (d && d.title) || item.title || 'Agenda';
      var now = Date.now(), ev = parseEvents(d).filter(function (e) { return e.end > now; });
      if (compact) {
        el.innerHTML = '<div class="info"><span class="city">' + esc(title) + '</span>' + (ev.slice(0, 3).map(function (e) {
          return '<span class="rng">' + cap(fmtDay(e.start, { weekday: 'short', day: 'numeric' })) + ' · ' + esc(e.title) + '</span>';
        }).join('') || '<span class="rng">Rien de prévu</span>') + '</div>';
        return;
      }
      var html = '', last = '', n = 0;
      for (var i = 0; i < ev.length && n < 9; i++) {
        var k = dayKey(ev[i].start);
        if (k !== last) { last = k; html += '<div class="day">' + cap(fmtDay(ev[i].start, { weekday: 'long', day: 'numeric', month: 'long' })) + '</div>'; n++; }
        html += '<div class="row"><b>' + (ev[i].allDay ? 'Journée' : fmtT(ev[i].start)) + '</b><span>' + esc(ev[i].title) +
          (ev[i].location ? '<i>' + esc(ev[i].location) + '</i>' : '') + '</span></div>';
        n++;
      }
      el.innerHTML = '<div class="dl-h">' + esc(title) + '</div>' + (html || '<div class="dl-empty">Rien de prévu cette semaine</div>') + staleNote(d);
    },

  };

  // Une liste pleine page doit tenir dans la zone libre (les bandeaux et angles réservent de la place) : on réduit le texte si besoin.
  function fitList(el) {
    if (!el || !el.classList.contains("p-full")) return;
    el.style.fontSize = "";
    var base = parseFloat(window.getComputedStyle(el).fontSize) || 16;
    for (var f = 1; f >= 0.3; f -= 0.05) {
      el.style.fontSize = (base * f) + "px";
      if (el.scrollHeight <= el.clientHeight + 1) break;
    }
  }
  function refitAll() {
    var els = document.querySelectorAll(".layer .dl.p-full, .layer .fit.p-full");
    for (var i = 0; i < els.length; i++) fitList(els[i]);
  }

  function buildData(el, item, ivs, place) {
    var kind = item.appKind, compact = place !== 'full';
    el.className += ' dl';
    var key = 'lk_d_' + item.dataId, data = null;
    try { data = JSON.parse(LS.getItem(key) || 'null'); } catch (e) {}
    var draw = function () {
      try { DRAW[kind](el, item, data, compact); } catch (e) { el.textContent = ''; }
      if (!compact) setTimeout(function () { fitList(el); }, 0);
    };
    var load = function () {
      api(DATA_API + item.dataId).then(function (d) { data = d; LS.setItem(key, JSON.stringify(d)); draw(); })
        .catch(function () { draw(); });
    };
    draw(); load();
    ivs.push(setInterval(load, 60000));
    ivs.push(setInterval(draw, 15000));   // les « Aujourd'hui », « En cours » évoluent sans réseau
  }

  /* ---------- YouTube : API IFrame (démarrage fiable, enchaînement à la fin de la vidéo, erreurs visibles) ---------- */
  var ytCallbacks = null, ytTried = false;
  function ytLoad(cb) {
    if (window.YT && window.YT.Player) { cb(window.YT); return; }
    if (!ytCallbacks) ytCallbacks = [];
    ytCallbacks.push(cb);
    if (ytTried) return;
    ytTried = true;
    window.onYouTubeIframeAPIReady = function () { var q = ytCallbacks; ytCallbacks = null; for (var i = 0; i < q.length; i++) q[i](window.YT); };
    var s = document.createElement('script');
    s.src = 'https://www.youtube.com/iframe_api';
    s.onerror = function () { var q = ytCallbacks || []; ytCallbacks = null; for (var i = 0; i < q.length; i++) q[i](null); ytTried = false; };
    document.head.appendChild(s);
    setTimeout(function () { if (ytCallbacks) { var q = ytCallbacks; ytCallbacks = null; for (var i = 0; i < q.length; i++) q[i](null); ytTried = false; } }, 12000);
  }

  var YT_ERRORS = { 2: 'lien invalide', 5: 'erreur du lecteur', 100: 'vidéo introuvable ou privée', 101: 'intégration désactivée par le propriétaire', 150: 'intégration désactivée par le propriétaire', 153: 'configuration du lecteur' };

  // o : { videoId | playlistId, sound, start, end } ; opts : { onEnded, onFailed } (diaporama) — sans opts, la vidéo boucle.
  function makeYoutube(el, o, ivs, opts) {
    var dead = false, ytp = null;
    var host = document.createElement('div');
    host.id = 'yt' + Math.random().toString(36).slice(2, 9);
    host.style.cssText = 'width:100%;height:100%';
    el.appendChild(host);
    function note(text) {
      var n = document.createElement('div');
      n.className = 'yt-note'; n.textContent = text;
      el.appendChild(n);
    }
    function fallbackIframe() {   // API injoignable : lecteur intégré simple
      host.innerHTML = '';
      var fr = document.createElement('iframe');
      var q = '?autoplay=1&mute=' + (o.sound ? 0 : 1) + '&controls=0&modestbranding=1&rel=0&playsinline=1&iv_load_policy=3&disablekb=1';
      if (o.playlistId) fr.src = 'https://www.youtube-nocookie.com/embed/videoseries' + q + '&list=' + o.playlistId + '&loop=1';
      else fr.src = 'https://www.youtube-nocookie.com/embed/' + o.videoId + q + '&loop=1&playlist=' + o.videoId + (o.start > 0 ? '&start=' + o.start : '') + (o.end > o.start ? '&end=' + o.end : '');
      fr.setAttribute('allow', 'autoplay; encrypted-media; picture-in-picture');
      fr.setAttribute('frameborder', '0');
      fr.setAttribute('referrerpolicy', 'strict-origin-when-cross-origin');
      host.appendChild(fr);
    }
    function restart(p) {
      try { if (o.playlistId) p.playVideoAt(0); else p.seekTo(o.start || 0); p.playVideo(); } catch (x) {}
    }
    ytLoad(function (YT) {
      if (dead) return;
      if (!YT) { fallbackIframe(); return; }
      // Quand l'API est déjà chargée (2e vidéo d'une boucle), ce rappel s'exécute avant que le widget soit inséré dans la page :
      // YT.Player ne trouvait alors pas son élément par identifiant et laissait un écran vide. On attend l'insertion et on passe l'élément lui-même.
      setTimeout(function () {
        if (dead) return;
        create(YT);
      }, 0);
    });
    function create(YT) {
      var vars = { autoplay: 1, mute: o.sound ? 0 : 1, controls: 0, rel: 0, modestbranding: 1, playsinline: 1, iv_load_policy: 3, disablekb: 1, origin: location.origin };
      var params = { host: 'https://www.youtube-nocookie.com', playerVars: vars };
      if (o.playlistId) { vars.listType = 'playlist'; vars.list = o.playlistId; }
      else {
        params.videoId = o.videoId;
        if (o.start > 0) vars.start = o.start;
        if (o.end > o.start) vars.end = o.end;
      }
      params.events = {
        onReady: function (e) {
          try { if (o.sound) e.target.unMute(); else e.target.mute(); e.target.playVideo(); } catch (x) {}
          if (o.sound) setTimeout(function () {   // la lecture automatique avec son est refusée : on repasse en muet plutôt que de rester figé
            try { var st = e.target.getPlayerState(); if (!dead && st !== 1 && st !== 3) { e.target.mute(); e.target.playVideo(); } } catch (x) {}
          }, 2500);
        },
        onStateChange: function (e) {
          if (e.data === 0) {                       // terminée
            if (opts && opts.onEnded) opts.onEnded(); // diaporama : on passe au contenu suivant
            else restart(e.target);                 // seule / en incrustation : boucle
          } else if (e.data === 5 || (e.data === -1 && !o.sound)) {   // prête mais pas lancée : on insiste
            try { if (!o.sound) e.target.mute(); e.target.playVideo(); } catch (x) {}
          }
        },
        onError: function (e) {
          note('YouTube : ' + (YT_ERRORS[e.data] || ('erreur ' + e.data)));
          if (opts && opts.onFailed) setTimeout(opts.onFailed, 5000);
        }
      };
      ytp = new YT.Player(host, params);
    }
    return function () { dead = true; try { if (ytp && ytp.destroy) ytp.destroy(); } catch (x) {} };
  }

  // App du catalogue : son rendu vient de apps.js (LinkiiApps) ; l'agenda réutilise le moteur des listes de données.
  function makeApp(item, place, opts) {
    var el = document.createElement('div');
    el.className = 'widget p-' + place + ' k-' + item.appId;
    var ivs = [];
    var settings = item.settings || {};
    var tickerHtml = null;
    function stop() { for (var i = 0; i < ivs.length; i++) { if (typeof ivs[i] === 'function') ivs[i](); else clearInterval(ivs[i]); } }

    if (item.appId === 'agenda' && place !== 'top' && place !== 'bottom') {   // en bandeau : rendu « agenda » d'apps.js
      item.appKind = 'agenda'; item.title = settings.title;
      buildData(el, item, ivs, place);
      return { el: el, stop: stop };
    }
    var render = window.LinkiiApps && window.LinkiiApps.get(item.appId);
    if (!render) { el.textContent = ''; return { el: el, stop: stop }; }

    var host = {
      el: el, item: item, settings: settings, place: place, compact: place !== 'full', cfg: cfg,
      esc: esc, pad: pad, cap: cap, opts: opts,
      fmtT: fmtT, fmtDay: fmtDay, dayKey: dayKey,
      // Bandeau défilant. Idempotent : le texte inchangé n'est pas redessiné (le défilement ne repart pas à zéro).
      // always = true : défile toujours ; sinon seulement si le texte est plus large que le bandeau (sinon centré, immobile).
      ticker: function (html, always, speed) {   // speed : facteur de durée (1 normal, > 1 plus lent)
        if (html === tickerHtml) return;
        tickerHtml = html;
        el.innerHTML = '<div class="tk"><div class="tk-track"><span></span></div></div>';
        var box = el.firstChild, track = box.firstChild, span = track.firstChild;
        span.innerHTML = html;
        setTimeout(function () {   // après l'insertion dans la page : la largeur est connue
          if (always || span.offsetWidth > box.clientWidth) {
            track.appendChild(span.cloneNode(true));
            track.style.animationDuration = Math.max(8, Math.round(Math.max(20, span.textContent.length * 0.22) * (speed || 1))) + 's';
            track.className = 'tk-track run';
          } else box.style.textAlign = 'center';
        }, 50);
      },
      note: function (text) { var n = document.createElement('div'); n.className = 'yt-note'; n.textContent = text; el.appendChild(n); },
      youtube: function (o) { ivs.push(makeYoutube(el, o, ivs, opts)); },
      onStop: function (fn) { ivs.push(fn); },   // nettoyage quand l'app disparaît (lecteurs vidéo, adresses blob)
      media: mediaSrc,   // fichier de la médiathèque : copie locale (hors ligne) si elle existe, sinon en ligne -> { src, revoke }
      shortDate: function (ms) {
        try { return new Date(ms).toLocaleDateString('fr-CH', tzOpts({ day: '2-digit', month: '2-digit' })); } catch (e) { return ''; }
      },
      every: function (fn, ms) { var id = setInterval(fn, ms); ivs.push(id); return id; },
      fit: function () { el.classList.add('fit'); setTimeout(function () { fitList(el); }, 0); },
      data: function (draw, refreshMs) {
        var key = 'lk_d_' + item.dataId, data = null;
        try { data = JSON.parse(LS.getItem(key) || 'null'); } catch (e) {}
        var run = function (fresh) { try { draw(data, fresh === true); } catch (e) {} };   // fresh : données tout juste reçues du serveur (et non relues du cache)
        var load = function () {
          api(DATA_API + item.dataId)
            .then(function (d) { data = d; try { LS.setItem(key, JSON.stringify(d)); } catch (e) {} run(true); })
            .catch(function () { run(); });
        };
        run(); load();
        ivs.push(setInterval(load, refreshMs || 60000));
        ivs.push(setInterval(run, 15000));
      }
    };
    try { render(host); } catch (e) { el.textContent = ''; }
    return { el: el, stop: stop };
  }

  // Bandeaux ET apps d angle réservent leur place : la zone de contenu est réduite en conséquence (rien ne se superpose).
  // Les angles forment une rangée sous le bandeau du haut / au-dessus du bandeau du bas ; le contenu occupe le reste.
  var bandObserver = null;
  function zoneH(name) {
    var el = document.querySelector("#overlays .z-" + name);
    return el ? el.offsetHeight : 0;
  }
  function fitBands() {
    keepFreeInside();
    var gap = (parseFloat($("stage").style.fontSize) || 19) * 1.2;
    var bt = zoneH("top"), bb = zoneH("bottom");
    var ct = Math.max(zoneH("top-left"), zoneH("top-right"));
    var cb = Math.max(zoneH("bottom-left"), zoneH("bottom-right"));
    var pos = [["top-left", "top", bt], ["top-right", "top", bt], ["bottom-left", "bottom", bb], ["bottom-right", "bottom", bb]];
    for (var i = 0; i < pos.length; i++) {
      var el = document.querySelector("#overlays .z-" + pos[i][0]);
      if (el) el.style[pos[i][1]] = (pos[i][2] + gap) + "px";   // l angle se place sous / au-dessus du bandeau
    }
    var c = $("content");
    c.style.top = (bt + (ct ? ct + 2 * gap : 0)) + "px";
    c.style.bottom = (bb + (cb ? cb + 2 * gap : 0)) + "px";
    setTimeout(refitAll, 450);   // après la transition de la zone de contenu
  }

  // Widgets d'écran : la position choisie dans le back-office est gardée, mais un widget ne déborde jamais de l'écran
  // (sa largeur réelle dépend des données : ville, vent…).
  function keepFreeInside() {
    var host = $("overlays"), W = host.clientWidth, H = host.clientHeight;
    var zs = host.querySelectorAll(".z-free");
    for (var i = 0; i < zs.length; i++) {
      var z = zs[i];
      var left = (+z.getAttribute("data-x") || 0) / 100 * W, top = (+z.getAttribute("data-y") || 0) / 100 * H;
      z.style.left = Math.max(0, Math.min(left, W - z.offsetWidth)) + "px";
      z.style.top = Math.max(0, Math.min(top, H - z.offsetHeight)) + "px";
    }
  }

  function restartYoutube(layer) {   // un seul contenu : on relance la vidéo (le lecteur est recréé)
    var cur = state.shownItem; state.shownItem = null;
    clearTimeout(state.timer); state.timer = null;
    state.index = 0;
    next();
    if (!state.shownItem) state.shownItem = cur;
  }

  function isSlide(i) { return !i.placement || i.placement === 'full'; }
  // période de validité (aaaa-mm-jj, dans le fuseau de l'organisation) : hors période, l'élément n'est pas diffusé
  function isValidNow(i) {
    var t = todayIso();
    if (i.validFrom && t < i.validFrom) return false;
    if (i.validTo && t > i.validTo) return false;
    return true;
  }
  function overlaySig(items) {
    return items.filter(function (it) { return !isSlide(it) && it.type === 'app' && isValidNow(it); }).map(function (it) { return it.id || it.placement; }).join('|');
  }

  // Apps en surimpression (angles / bandeaux) : permanents pendant toute la boucle
  function setOverlays(items) {
    var host = $('overlays');
    for (var i = 0; i < state.overlayStops.length; i++) state.overlayStops[i]();
    state.overlayStops = [];
    host.innerHTML = '';
    state.ovSig = overlaySig(items);
    items.filter(function (it) { return !isSlide(it) && it.type === 'app' && isValidNow(it); }).forEach(function (it) {
      var w = makeApp(it, it.placement);
      if (it.placement === 'free') {   // widget d écran : sa propre zone, à la position choisie (ne réserve pas de place au contenu)
        var fz = document.createElement("div");
        fz.className = "zone z-free";
        fz.setAttribute("data-x", +it.x || 0); fz.setAttribute("data-y", +it.y || 0);
        fz.style.left = (+it.x || 0) + "%"; fz.style.top = (+it.y || 0) + "%";
        fz.style.fontSize = (+it.scale || 1) + "em";
        var sw = it.settings && +it.settings.width;   // texte libre : largeur choisie, en % de l'écran
        if (sw > 0) { fz.style.width = Math.min(100, sw) + "%"; w.el.style.width = "100%"; w.el.style.maxWidth = "none"; }
        fz.appendChild(w.el); host.appendChild(fz);
        state.overlayStops.push(w.stop);
        return;
      }
      var zone = host.querySelector(".z-" + it.placement);
      if (!zone) { zone = document.createElement("div"); zone.className = "zone z-" + it.placement; host.appendChild(zone); }
      zone.appendChild(w.el);
      state.overlayStops.push(w.stop);
    });
    fitBands();
    setTimeout(fitBands, 300); setTimeout(fitBands, 1500);   // polices / météo qui arrive après coup
    if (window.ResizeObserver) {
      if (bandObserver) bandObserver.disconnect();
      bandObserver = new ResizeObserver(fitBands);
      var zs = host.querySelectorAll(".zone");
      for (var k = 0; k < zs.length; k++) bandObserver.observe(zs[k]);
    }
  }

  /* ---------- Historique de diffusion ----------
     Chaque contenu affiché en plein écran est noté (contenu, début, durée) dans le stockage local, puis envoyé par lots :
     hors ligne, rien n'est perdu (2 000 diffusions au plus en attente). */
  var playing = {}, flushingPlays = false;   // diffusion en cours, par zone (z0 = zone principale)
  function loadPlays() { try { return JSON.parse(LS.getItem('lk_plays') || '[]'); } catch (e) { return []; } }
  function savePlays(q) { try { LS.setItem('lk_plays', JSON.stringify(q)); } catch (e) {} }
  function playStart(item, key) {
    key = key || 'z0';
    playEnd(key);
    playing[key] = { mediaId: item.mediaId || null, appId: item.appId || null, start: Date.now() };
  }
  function playEnd(key) {
    key = key || 'z0';
    var p = playing[key];
    if (!p) return;
    var sec = Math.round((Date.now() - p.start) / 1000);
    delete playing[key];
    if (sec < 1 || PREVIEW) return;   // aperçu : aucune diffusion comptée
    var q = loadPlays();
    q.push({ mediaId: p.mediaId, appId: p.appId, start: p.start, sec: sec });
    if (q.length > 2000) q = q.slice(q.length - 2000);
    savePlays(q);
    if (q.length >= 50) flushPlays();
  }
  function flushPlays() {
    var q = loadPlays();
    if (!q.length || flushingPlays || !state.token) return;
    flushingPlays = true;
    var batch = q.slice(0, 500);
    api('/api/player/plays', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(batch) })
      .then(function () { savePlays(loadPlays().slice(batch.length)); })   // les diffusions notées pendant l'envoi restent en attente
      .catch(function () {})
      .then(function () { flushingPlays = false; });
  }
  window.addEventListener('pagehide', function () { for (var k in playing) playEnd(k); });   // rechargement nocturne, fermeture : la diffusion en cours est notée

  /* ---------- Diffusion (double buffer) ---------- */
  function next() {
    clearTimeout(state.timer); state.timer = null;
    if (state.paused) return;   // simulateur en pause

    if (state.pending) {            // remplacement entre deux contenus
      state.current = state.pending; state.pending = null; state.index = 0;
      applyConfig(state.current);
      setOverlays(state.current.items);
      applyZones(state.current);
    }
    var pl = state.current;
    if (pl && overlaySig(pl.items) !== state.ovSig) setOverlays(pl.items);   // une app permanente entre ou sort de sa période de validité
    var slides = pl ? pl.items.filter(isSlide).filter(isValidNow) : [];
    var hasOverlay = pl ? pl.items.some(function (i) { return !isSlide(i) && isValidNow(i); }) : false;
    if (zoneLoops.some(function (z) { return z.hasSlides(); })) hasOverlay = true;   // écran découpé : les autres zones diffusent

    if (!slides.length) {
      playEnd();
      $('idle').style.display = hasOverlay ? 'none' : 'flex';
      state.timer = setTimeout(next, 5000);
      previewReport();
      return;
    }
    $('idle').style.display = 'none';

    // zone synchronisée : le contenu et sa durée se déduisent de l'heure commune (simulateur en lecture libre : ordre de la liste)
    var epoch = pl.sync && slides.length > 1 && !state.free ? pl.sync.epoch : null;
    var item;
    if (epoch != null) {
      var pick = syncPick(slides, epoch);
      if (pick.left < 80) { state.timer = setTimeout(next, pick.left + 5); return; }   // à quelques ms d'une frontière : on attend qu'elle passe
      item = slides[pick.index];
      state.index = pick.index + 1;
    } else {
      item = slides[state.index % slides.length];
      state.index++;
    }
    ack();
    // un seul contenu (ex. porte de salle) : on ne le redessine pas à chaque tour, il se met à jour seul
    if (slides.length === 1 && state.shownItem === item) {
      playStart(item);   // un tour de plus : une diffusion de plus
      state.timer = setTimeout(next, Math.max(1, item.durationSec) * 1000);
      state.slideEnd = Date.now() + Math.max(1, item.durationSec) * 1000;
      previewReport();
      return;
    }

    var layerId = state.active === 'layerA' ? 'layerB' : 'layerA';
    var layer = $(layerId), old = $(state.active), oldCleanup = state.cleanup;
    layer.innerHTML = '';
    var newCleanup = null, shown = false;

    function show() {
      if (shown) return; shown = true;
      layer.classList.add('on'); old.classList.remove('on');
      setTimeout(function () { if (oldCleanup) oldCleanup(); old.innerHTML = ''; }, 400);
      state.active = layerId; state.cleanup = newCleanup;
      state.shownItem = item;
      playStart(item);
      var ms = epoch != null ? syncLeft(slides, item, epoch) : Math.max(1, item.durationSec) * 1000;
      // durationMode « content » : l'app décide (fin de la vidéo) ; durationSec est le plafond de sécurité
      state.timer = setTimeout(next, ms);
      state.slideEnd = Date.now() + ms;
      previewReport();
    }
    function skip() { if (!shown) { shown = true; state.timer = setTimeout(next, 1000); } }

    var origin = epoch != null ? pick.start : pl.sync && !state.free ? pl.sync.epoch : null;   // un seul contenu synchronisé : il se déroule depuis l'origine de la zone
    renderSlide(layer, item, slideOpts(item, origin,
      function () { if (slides.length > 1 && state.shownItem === item) next(); else if (slides.length <= 1 && state.shownItem === item) restartYoutube(layer); },
      function () { if (state.shownItem === item) next(); }
    ), function (cleanup) { newCleanup = cleanup; show(); }, skip);
  }

  // Affiche un contenu dans un calque : onShow(nettoyage) quand il est prêt, onSkip s'il est illisible.
  // Partagé par la zone principale et les zones 2 et 3 d'un écran découpé.
  function renderSlide(layer, item, appOpts, onShow, onSkip) {
    if (item.type === 'image') {
      var img = new Image(), imgSrc = null;
      var imgClean = function () { if (imgSrc) imgSrc.revoke(); };
      img.onload = function () { onShow(imgClean); }; img.onerror = onSkip;
      layer.appendChild(img);
      mediaSrc(item.url).then(function (m) { imgSrc = m; img.src = m.src; });
    } else if (item.type === 'video') {
      var v = document.createElement('video'), vSrc = null;
      v.muted = true; v.loop = true; v.autoplay = true; v.setAttribute('playsinline', '');
      var vSync = appOpts && appOpts.sync, vAlign = null;
      var vClean = function () { clearInterval(vAlign); try { v.pause(); v.removeAttribute('src'); v.load(); } catch (e) {} if (vSrc) vSrc.revoke(); };
      if (vSync) {   // zone synchronisée : position de la vidéo = temps écoulé depuis l'origine commune (modulo sa durée)
        var vWant = function () { return ((vSync.now() - vSync.origin) / 1000) % v.duration; };
        v.onloadedmetadata = function () { try { v.currentTime = vWant(); } catch (e) {} };
        vAlign = setInterval(function () {
          if (!v.duration || !isFinite(v.duration)) return;
          var diff = v.currentTime - vWant();
          if (diff > v.duration / 2) diff -= v.duration; else if (diff < -v.duration / 2) diff += v.duration;
          if (Math.abs(diff) > 0.5) { try { v.currentTime = vWant(); } catch (e) {} v.playbackRate = 1; }
          else v.playbackRate = Math.abs(diff) > 0.04 ? (diff > 0 ? 0.95 : 1.05) : 1;
        }, 1000);
      }
      v.oncanplay = function () { onShow(vClean); try { v.play(); } catch (e) {} };
      v.onerror = onSkip;
      layer.appendChild(v);
      mediaSrc(item.url).then(function (m) { vSrc = m; v.src = m.src; });
    } else if (item.type === 'app') {
      var w = makeApp(item, 'full', appOpts);
      layer.appendChild(w.el);
      onShow(w.stop);
    } else {
      onSkip();
    }
  }

  /* ---------- Écran découpé : zones 2 et 3, chacune avec sa propre boucle ----------
     La zone 1 est #zone0 (boucle principale ci-dessus) ; les tailles viennent du découpage publié (en % de #content). */
  var zoneLoops = [], zoneSig = null;

  // Place les zones (côte à côte en paysage, empilées en portrait). 1em = 1 % de la largeur de la zone : les apps pleine page s'y adaptent.
  function sizeZones() {
    var lay = state.current && state.current.layout, sizes = (lay && lay.sizes) || [100], col = lay && lay.dir === 'col';
    var W = parseFloat($('stage').style.width) || 1920, pos = 0;
    var els = [$('zone0')].concat(zoneLoops.map(function (z) { return z.el; }));
    for (var i = 0; i < els.length; i++) {
      var s = sizes[i] || 0, st = els[i].style;
      if (col) { st.left = '0'; st.width = '100%'; st.top = pos + '%'; st.height = s + '%'; st.fontSize = (W / 100) + 'px'; }
      else { st.top = '0'; st.height = '100%'; st.left = pos + '%'; st.width = s + '%'; st.fontSize = (W * s / 100 / 100) + 'px'; }
      pos += s;
    }
  }

  function applyZones(pl) {
    var zones = (pl && pl.zones) || [];
    var sig = JSON.stringify([pl && pl.layout, zones, pl && pl.sync]);
    if (sig === zoneSig) { sizeZones(); return; }   // même découpage, mêmes contenus : les zones continuent sans recommencer
    zoneSig = sig;
    zoneLoops.forEach(function (z) { z.stop(); if (z.el.parentNode) z.el.parentNode.removeChild(z.el); });
    zoneLoops = zones.map(function (items, i) {
      var el = document.createElement('div');
      el.className = 'pzone';
      $('content').appendChild(el);
      return zoneLoop(el, items, 'z' + (i + 1), pl && pl.sync);
    });
    sizeZones();
  }

  function zoneLoop(el, items, key, sync) {
    el.innerHTML = '<div class="layer"></div><div class="layer"></div>';
    var layers = [el.firstChild, el.lastChild], active = 0, idx = 0, timer = null, cleanup = null, shownItem = null, dead = false;
    function step() {
      clearTimeout(timer);
      if (dead) return;
      var slides = items.filter(isSlide).filter(isValidNow);
      if (!slides.length) { playEnd(key); timer = setTimeout(step, 5000); return; }
      var epoch = sync && slides.length > 1 ? sync.epoch : null;   // zone synchronisée : même position que les autres écrans qui jouent cette liste
      var item;
      if (epoch != null) {
        var pick = syncPick(slides, epoch);
        if (pick.left < 80) { timer = setTimeout(step, pick.left + 5); return; }
        item = slides[pick.index]; idx = pick.index + 1;
      } else { item = slides[idx % slides.length]; idx++; }
      if (slides.length === 1 && shownItem === item) {   // un seul contenu : il continue (une vidéo seule boucle d'elle-même)
        playStart(item, key);
        timer = setTimeout(step, Math.max(1, item.durationSec) * 1000);
        return;
      }
      var layer = layers[1 - active], old = layers[active], oldCleanup = cleanup, shown = false;
      layer.innerHTML = '';
      var origin = epoch != null ? pick.start : sync ? sync.epoch : null;
      renderSlide(layer, item, origin != null || slides.length > 1 ? slideOpts(item, origin,
        function () { if (shownItem === item) step(); },
        function () { if (shownItem === item) step(); }
      ) : null, function (c) {
        if (shown || dead) return; shown = true;
        layer.classList.add('on'); old.classList.remove('on');
        setTimeout(function () { if (oldCleanup) oldCleanup(); old.innerHTML = ''; }, 400);
        active = 1 - active; cleanup = c; shownItem = item;
        playStart(item, key);
        timer = setTimeout(step, epoch != null ? syncLeft(slides, item, epoch) : Math.max(1, item.durationSec) * 1000);
      }, function () { if (!shown) { shown = true; timer = setTimeout(step, 1000); } });
    }
    step();
    return {
      el: el,
      hasSlides: function () { return items.filter(isSlide).filter(isValidNow).length > 0; },
      stop: function () { dead = true; clearTimeout(timer); if (cleanup) cleanup(); playEnd(key); }
    };
  }

  /* ---------- Temps réel : SignalR + filet de sécurité 60 s ---------- */
  function connectHub() {
    if (!window.signalR) return;  // pas de lib (CDN injoignable et pas en cache) -> sondage seul
    var conn = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/screen', { accessTokenFactory: function () { return state.token; } })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .build();
    conn.on('PlaylistChanged', function () { refresh(); });
    conn.on('Identify', function (label) { identify(String(label)); });
    conn.onreconnecting(markOffline);
    conn.onreconnected(function () { setOnline(true); refresh(); });
    function start() {
      conn.start().then(function () { refresh(); }).catch(function () { setTimeout(start, 10000); });
    }
    conn.onclose(function () { markOffline(); setTimeout(start, 10000); });
    start();
    setInterval(function () {
      if (conn.state === signalR.HubConnectionState.Connected) conn.invoke('Ping').catch(function () {});
    }, 30000);
  }

  function poll() {
    api('/api/player/version').then(function (d) {
      if (d.build) {   // nouveau code du player (nouveaux rendus d'apps…) : rechargé tout de suite, sans attendre la nuit
        if (state.build && d.build !== state.build) { location.reload(); return; }
        state.build = d.build;
      }
      var cur = state.pending ? state.pending.version : (state.current && state.current.version);
      if (d.version !== cur) refresh();
      ack();
    }).catch(function () {});
  }

  /* ---------- Rechargement nocturne (heure réglée dans les paramètres du tenant) ---------- */
  function nightlyReload() {
    setInterval(function () {
      var d = new Date(), key = d.getFullYear() + '-' + d.getMonth() + '-' + d.getDate();
      if (d.getHours() === cfg.reloadHour && LS.getItem('lk_reload') !== key) { LS.setItem('lk_reload', key); location.reload(); }
    }, 30000);
  }

  function startPlayback() {
    try { if (navigator.storage && navigator.storage.persist) navigator.storage.persist(); } catch (e) {}
    // démarrage immédiat sur la dernière playlist connue (fonctionne sans réseau)
    try {
      var saved = JSON.parse(LS.getItem('lk_playlist') || 'null');
      if (saved) { state.pending = saved; }   // passe par next() => config + apps appliquées
    } catch (e) {}
    layout();
    next();
    refresh();
    connectHub();
    setInterval(poll, 60000);
    flushPlays();
    setInterval(flushPlays, 60000);
    setInterval(function () { api("/api/player/version").catch(function () {}); }, 15000);   // détecte vite une coupure
    nightlyReload();
  }

  /* ---------- Simulateur : état envoyé au back-office, commandes reçues (pause, contenu choisi, retour au direct) ---------- */
  // Son coupé tant que le simulateur ne l'a pas activé (&sound=1) : vidéos et YouTube démarrent muets.
  function previewPrepare(pl) {
    if (SOUND) return;
    var lists = [pl.items || []].concat(pl.zones || []);
    for (var i = 0; i < lists.length; i++)
      for (var k = 0; k < lists[i].length; k++) { var s = lists[i][k].settings; if (s && s.sound) s.sound = 'false'; }
  }

  function previewReport() {
    if (!PREVIEW || window.parent === window) return;
    var pl = state.current, meta = (pl && pl.meta) || {};
    var all = pl ? pl.items.filter(isSlide) : [], slides = all.filter(isValidNow), item = state.shownItem;
    var sync = !!(pl && pl.sync && slides.length);   // un seul contenu synchronisé se déroule aussi à l'heure commune
    function info(i) {
      var m = meta[i.id] || {};
      return { name: m.name || 'Contenu', color: m.color || '#5A6B80', sec: i.durationSec, content: i.durationMode === 'content', from: i.validFrom || null, to: i.validTo || null };
    }
    window.parent.postMessage({
      lk: 'sim', ev: 'state', items: slides.map(info), skipped: all.filter(function (i) { return !isValidNow(i); }).map(info),
      index: item ? slides.indexOf(item) : -1,
      left: state.paused ? state.pausedLeft : Math.max(0, (state.slideEnd || 0) - Date.now()),
      dur: item ? Math.max(1, item.durationSec) * 1000 : 0,
      synced: sync, live: sync && !state.free, paused: !!state.paused, today: todayIso()
    }, location.origin);
  }

  function mediaIn(layerId, action) {
    var vs = $(layerId).querySelectorAll('video');
    for (var i = 0; i < vs.length; i++) { try { var p = vs[i][action](); if (p && p.catch) p.catch(function () {}); } catch (e) {} }
  }

  function previewControl(e) {
    if (e.origin !== location.origin || !e.data || e.data.lk !== 'sim' || !state.current) return;
    var c = e.data.cmd, slides = state.current.items.filter(isSlide).filter(isValidNow);
    if (c === 'pause' && !state.paused) {   // l'écran, lui, continue : le simulateur passe en lecture libre
      state.pausedLeft = Math.max(1, (state.slideEnd || 0) - Date.now());
      state.paused = true; state.free = true;
      clearTimeout(state.timer); state.timer = null;
      mediaIn(state.active, 'pause');
    } else if (c === 'play' && state.paused) {
      state.paused = false;
      mediaIn(state.active, 'play');
      state.slideEnd = Date.now() + state.pausedLeft;
      state.timer = setTimeout(next, state.pausedLeft);
    } else if ((c === 'goto' || c === 'next' || c === 'prev') && slides.length) {
      var cur = slides.indexOf(state.shownItem);
      var i = c === 'goto' ? +e.data.index : cur + (c === 'next' ? 1 : -1);
      state.index = ((i % slides.length) + slides.length) % slides.length;
      state.free = true; state.paused = false; state.shownItem = null;
      next();
    } else if (c === 'live') {   // retour au direct : même contenu, au même moment, que l'écran
      state.free = false; state.paused = false; state.shownItem = null;
      next();
    }
    previewReport();
  }

  function startPreview() {
    document.body.style.cursor = 'default';
    $('idle').querySelector('h1').textContent = 'Rien à afficher';
    $('idle').querySelector('p').textContent = 'Aucun contenu à diffuser à ce moment.';
    window.addEventListener('message', previewControl);
    layout();
    refresh();
    setInterval(refresh, 20000);   // pas d'ordres reçus : les publications sont reprises en relisant régulièrement
  }

  /* ---------- Démarrage ---------- */
  layout();
  if (PREVIEW) startPreview(); else if (state.token) startPlayback(); else startPairing();
})();
