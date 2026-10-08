/* Player Linkii (POC) — JS volontairement "ancien" (var, promesses, pas d'async/await) pour les WebView anciennes. */
(function () {
  'use strict';

  var LS = window.localStorage;
  var CACHE = 'linkii-media-v1';
  var state = {
    screenId: LS.getItem('lk_screen'),
    token: LS.getItem('lk_token'),
    current: null,      // playlist en cours de diffusion {version, items, screen, settings}
    pending: null,      // playlist prête, à appliquer entre deux contenus
    acked: LS.getItem('lk_acked'),
    index: 0,
    active: 'layerA',
    timer: null,
    cleanup: null,
    overlayStops: []
  };
  var cfg = { orientation: 'landscape', resolution: 'auto', timezone: null, reloadHour: 4 };

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
  window.addEventListener("online", function () { api("/api/player/version").catch(function () {}); });

  /* ---------- HTTP ---------- */
  function api(path, opts) {
    opts = opts || {};
    opts.headers = opts.headers || {};
    if (state.token) opts.headers['X-Token'] = state.token;
    return fetch(path, opts).then(function (r) { setOnline(true); return r; }, function (e) { setOnline(false); throw e; }).then(function (r) {
      if (r.status === 401) { unpair(); throw new Error('401'); }
      if (r.status === 403) { $('suspended').style.display = 'flex'; throw new Error('403'); }   // client ou revendeur suspendu
      $('suspended').style.display = 'none';
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.text().then(function (t) { return t ? JSON.parse(t) : null; });
    });
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
  if ('serviceWorker' in navigator) {
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
    if (!navigator.wakeLock || document.visibilityState !== 'visible') return;
    navigator.wakeLock.request('screen').catch(function () {});
  }
  keepAwake();
  document.addEventListener('visibilitychange', keepAwake);

  // Hors application installée, un toucher passe en plein écran (Android ; Safari iPhone ne le permet pas : il faut installer).
  if (!installed() && ('ontouchstart' in window || navigator.maxTouchPoints > 0)) {
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
    if (installed() || (!installEvent && !isIos())) { el.style.display = 'none'; return; }
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
  window.addEventListener('beforeinstallprompt', function (e) { e.preventDefault(); installEvent = e; showInstall(); });
  window.addEventListener('appinstalled', function () { installEvent = null; showInstall(); });
  showInstall();

  /* ---------- Scène : format, résolution, rotation ---------- */
  function layout() {
    var stage = $('stage');
    var vw = window.innerWidth, vh = window.innerHeight;
    var portrait = cfg.orientation === 'portrait';
    // écran physiquement dans l'autre sens que le format demandé : on pivote la scène
    var rot = (portrait && vw > vh) ? 90 : (!portrait && vh > vw) ? -90 : 0;
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
    stage.style.width = W + 'px';
    stage.style.height = H + 'px';
    stage.style.fontSize = (W / 100) + 'px';   // 1em = 1 % de la largeur de la scène
    stage.style.webkitTransform = stage.style.transform =
      'translate(-50%, -50%) rotate(' + rot + 'deg) scale(' + s + ')';
  }
  window.addEventListener('resize', layout);

  function applyConfig(pl) {
    if (!pl) return;
    if (pl.screen) { cfg.orientation = pl.screen.orientation || 'landscape'; cfg.resolution = pl.screen.resolution || 'auto'; }
    if (pl.settings) { cfg.timezone = pl.settings.timezone || null; if (pl.settings.reloadHour != null) cfg.reloadHour = pl.settings.reloadHour; }
    layout();
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
    if (!window.caches) return Promise.resolve();
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
    var online = url + '?t=' + encodeURIComponent(state.token || '');   // repli en ligne : jeton de l'écran dans l'adresse
    if (!window.caches || !window.URL || !URL.createObjectURL) return Promise.resolve({ src: online, revoke: function () {} });
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
    api('/api/player/playlist').then(function (pl) {
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
    if (!state.current || state.acked === state.current.version) return;
    var v = state.current.version, d = deviceSize();
    api('/api/player/ack', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ version: v, w: d.w, h: d.h }) })
      .then(function () { state.acked = v; LS.setItem('lk_acked', v); }).catch(function () {});
  }

  /* ---------- Utilitaires des apps ---------- */
  function pad(n) { return ('0' + n).slice(-2); }

  /* ---------- Listes de données des apps : agenda et menu (la porte de salle a son propre rendu dans apps.js) ---------- */
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

    menu: function (el, item, d, compact) {
      var title = (d && d.title) || item.title || 'Menu du jour';
      var rows = (d && d.rows) || [], today = todayIso();
      var day = today, sel = rows.filter(function (r) { return r.date === today; });
      if (!sel.length && rows.length) {   // pas de menu aujourd'hui : prochain jour renseigné
        day = rows[0].date; sel = rows.filter(function (r) { return r.date === day; });
      }
      if (compact) {
        el.innerHTML = '<div class="info"><span class="city">' + esc(title) + '</span>' + (sel.slice(0, 3).map(function (r) {
          return '<span class="rng">' + esc(r.name) + (r.price ? ' · ' + esc(r.price) : '') + '</span>';
        }).join('') || '<span class="rng">Menu non renseigné</span>') + '</div>';
        return;
      }
      var cats = [], by = {};
      sel.forEach(function (r) { var c = r.category || 'Plat'; if (!by[c]) { by[c] = []; cats.push(c); } by[c].push(r); });
      var html = cats.map(function (c) {
        return '<div class="cat">' + esc(c) + '</div>' + by[c].map(function (r) {
          return '<div class="dish"><span>' + esc(r.name) + (r.allergens ? '<i>' + esc(r.allergens) + '</i>' : '') + '</span><b>' + esc(r.price) + '</b></div>';
        }).join('');
      }).join('');
      var when = day === today ? '' : '<small>' + cap(fmtDay(Date.parse(day + 'T12:00:00Z'), { weekday: 'long', day: 'numeric', month: 'long', timeZone: 'UTC' })) + '</small>';
      el.innerHTML = '<div class="dl-h">' + esc(title) + when + '</div>' + (html || '<div class="dl-empty">Menu non renseigné</div>') + staleNote(d);
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
      api('/api/data/' + item.dataId).then(function (d) { data = d; LS.setItem(key, JSON.stringify(d)); draw(); })
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
    if (item.appId === 'menu-csv' || item.appId === 'menu-api') {   // menus de restaurant : moteur « menu » (pleine page ou angle)
      item.appKind = 'menu'; item.title = settings.title;
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
      ticker: function (html, always) {
        if (html === tickerHtml) return;
        tickerHtml = html;
        el.innerHTML = '<div class="tk"><div class="tk-track"><span></span></div></div>';
        var box = el.firstChild, track = box.firstChild, span = track.firstChild;
        span.innerHTML = html;
        setTimeout(function () {   // après l'insertion dans la page : la largeur est connue
          if (always || span.offsetWidth > box.clientWidth) {
            track.appendChild(span.cloneNode(true));
            track.style.animationDuration = Math.max(20, Math.round(span.textContent.length * 0.22)) + 's';
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
          api('/api/data/' + item.dataId)
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
        fz.style.left = (+it.x || 0) + "%"; fz.style.top = (+it.y || 0) + "%";
        fz.style.fontSize = (+it.scale || 1) + "em";
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

  /* ---------- Diffusion (double buffer) ---------- */
  function next() {
    clearTimeout(state.timer); state.timer = null;

    if (state.pending) {            // remplacement entre deux contenus
      state.current = state.pending; state.pending = null; state.index = 0;
      applyConfig(state.current);
      setOverlays(state.current.items);
    }
    var pl = state.current;
    if (pl && overlaySig(pl.items) !== state.ovSig) setOverlays(pl.items);   // une app permanente entre ou sort de sa période de validité
    var slides = pl ? pl.items.filter(isSlide).filter(isValidNow) : [];
    var hasOverlay = pl ? pl.items.some(function (i) { return !isSlide(i) && isValidNow(i); }) : false;

    if (!slides.length) {
      $('idle').style.display = hasOverlay ? 'none' : 'flex';
      state.timer = setTimeout(next, 5000);
      return;
    }
    $('idle').style.display = 'none';

    var item = slides[state.index % slides.length];
    state.index++;
    ack();
    // un seul contenu (ex. porte de salle) : on ne le redessine pas à chaque tour, il se met à jour seul
    if (slides.length === 1 && state.shownItem === item) {
      state.timer = setTimeout(next, Math.max(1, item.durationSec) * 1000);
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
      var ms = Math.max(1, item.durationSec) * 1000;
      // durationMode « content » : l'app décide (fin de la vidéo) ; durationSec est le plafond de sécurité
      state.timer = setTimeout(next, ms);
    }
    function skip() { if (!shown) { shown = true; state.timer = setTimeout(next, 1000); } }

    if (item.type === 'image') {
      var img = new Image();
      img.onload = show; img.onerror = skip;
      layer.appendChild(img);
      var imgSrc = null;
      newCleanup = function () { if (imgSrc) imgSrc.revoke(); };
      mediaSrc(item.url).then(function (m) { imgSrc = m; img.src = m.src; });
    } else if (item.type === 'video') {
      var v = document.createElement('video');
      v.muted = true; v.loop = true; v.autoplay = true; v.setAttribute('playsinline', '');
      v.oncanplay = function () { show(); try { v.play(); } catch (e) {} };
      v.onerror = skip;
      layer.appendChild(v);
      var vSrc = null;
      newCleanup = function () { try { v.pause(); v.removeAttribute('src'); v.load(); } catch (e) {} if (vSrc) vSrc.revoke(); };
      mediaSrc(item.url).then(function (m) { vSrc = m; v.src = m.src; });
    } else if (item.type === 'app') {
      var w = makeApp(item, 'full', item.durationMode === 'content' ? {
        onEnded: function () { if (slides.length > 1 && state.shownItem === item) next(); else if (slides.length <= 1 && state.shownItem === item) restartYoutube(layer); },
        onFailed: function () { if (state.shownItem === item) next(); }
      } : null);
      layer.appendChild(w.el);
      newCleanup = w.stop;
      show();
    } else {
      skip();
    }
  }

  /* ---------- Temps réel : SignalR + filet de sécurité 60 s ---------- */
  function connectHub() {
    if (!window.signalR) return;  // pas de lib (CDN injoignable et pas en cache) -> sondage seul
    var conn = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/screen', { accessTokenFactory: function () { return state.token; } })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .build();
    conn.on('PlaylistChanged', function () { refresh(); });
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
    setInterval(function () { api("/api/player/version").catch(function () {}); }, 15000);   // détecte vite une coupure
    nightlyReload();
  }

  /* ---------- Démarrage ---------- */
  layout();
  if (state.token) startPlayback(); else startPairing();
})();
