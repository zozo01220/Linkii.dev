/* Apps du catalogue Linkii : un module de rendu par app (JS volontairement "ancien" : var, pas d'async/await).
   player.js appelle chaque rendu avec un objet "host" :
     host.el, host.item, host.settings   élément DOM, élément publié, paramètres publiés (jamais de secrets)
     host.place, host.compact            position ('full', 'top-left'…) ; true hors pleine page
     host.cfg                            configuration de l'écran (fuseau horaire…)
     host.every(fn, ms)                  minuteur arrêté automatiquement quand l'app disparaît
     host.data(draw, refreshMs)          lit /api/data/<id> (dernière valeur gardée hors ligne) et appelle draw(data)
     host.fit()                          réduit le texte d'une pleine page pour qu'il tienne dans la zone libre
     host.youtube(o), host.note(text)    lecteur YouTube (API IFrame) et message d'erreur
     host.media(url) -> Promise {src, revoke}   fichier de la médiathèque (copie locale hors ligne) ; host.onStop(fn) nettoyage
     host.opts.onEnded()                 durée « content » : à appeler quand le contenu est terminé (sinon l'app boucle)
     host.esc, host.pad, host.shortDate  utilitaires                                                                 */
(function (w) {
  'use strict';
  var registry = {};
  w.LinkiiApps = {
    register: function (id, render) { registry[id] = render; },
    get: function (id) { return registry[id]; }
  };
  var A = w.LinkiiApps;

  /* ---------- Horloge ---------- */
  A.register('clock', function (h) {
    var s = h.settings;
    h.el.innerHTML = '<div class="time"></div><div class="date"></div>';
    var tEl = h.el.firstChild, dEl = h.el.lastChild;
    var tick = function () {
      var d = new Date(), t = '', dt = '';
      var tz = s.timezone || h.cfg.timezone || undefined;
      try {
        var o = { hour: '2-digit', minute: '2-digit', hour12: s.format === '12', timeZone: tz };
        if (s.showSeconds === 'true') o.second = '2-digit';
        t = d.toLocaleTimeString('fr-CH', o);
        if (s.showDate !== 'false') dt = d.toLocaleDateString('fr-CH', { weekday: 'long', day: 'numeric', month: 'long', timeZone: tz });
      } catch (e) { t = h.pad(d.getHours()) + ':' + h.pad(d.getMinutes()); }
      tEl.textContent = t; dEl.textContent = dt;
    };
    tick(); h.every(tick, 1000);
  });

  /* ---------- Météo ---------- */
  var WMO = [
    [[0], '☀️'], [[1, 2], '🌤️'], [[3], '☁️'], [[45, 48], '🌫️'], [[51, 53, 55, 56, 57], '🌦️'],
    [[61, 63, 65, 66, 67, 80, 81, 82], '🌧️'], [[71, 73, 75, 77, 85, 86], '❄️'], [[95, 96, 99], '⛈️']
  ];
  function wmoIcon(code) {
    for (var i = 0; i < WMO.length; i++) if (WMO[i][0].indexOf(code) !== -1) return WMO[i][1];
    return '🌡️';
  }
  A.register('weather', function (h) {
    h.el.innerHTML = '<div class="ico">…</div><div class="temp"></div><div class="info"><span class="city"></span><span class="rng"></span></div>';
    var ico = h.el.querySelector('.ico'), temp = h.el.querySelector('.temp'), city = h.el.querySelector('.city'), rng = h.el.querySelector('.rng');
    h.data(function (d) {
      if (!d) return;
      ico.textContent = wmoIcon(d.code);
      temp.textContent = Math.round(d.temp) + '°';
      city.textContent = d.city;
      rng.textContent = '↓ ' + Math.round(d.min) + '°  ↑ ' + Math.round(d.max) + '°  ·  💨 ' + Math.round(d.wind) + ' km/h';
    }, 10 * 60 * 1000);
  });

  /* ---------- Agenda en bandeau (pleine page et angles : liste, voir player.js) ---------- */
  A.register('agenda', function (h) {
    var title = h.settings.title || 'Agenda';
    h.data(function (d) {
      var now = Date.now(), tomorrow = h.dayKey(now + 86400000), today = h.dayKey(now);
      var ev = ((d && d.events) || []).map(function (e) {
        return { title: e.title, start: Date.parse(e.start), end: Date.parse(e.end), allDay: e.allDay, location: e.location };
      }).filter(function (e) { return e.end > now; }).sort(function (a, b) { return a.start - b.start; });
      var parts = ev.slice(0, 15).map(function (e) {
        var k = h.dayKey(e.start), when;
        if (e.start <= now && !e.allDay) when = 'En cours · jusqu\'à ' + h.fmtT(e.end);
        else {
          when = k === today ? 'Aujourd\'hui' : k === tomorrow ? 'Demain' : h.cap(h.fmtDay(e.start, { weekday: 'short', day: 'numeric' }));
          if (!e.allDay) when += ' ' + h.fmtT(e.start);
        }
        return '<b>' + h.esc(when) + '</b> ' + h.esc(e.title) + (e.location ? ' <em>(' + h.esc(e.location) + ')</em>' : '');
      });
      var stale = d && d.fetchedAt && Date.now() - Date.parse(d.fetchedAt) > 10 * 60 * 1000 ? ' <em>⚠ données de ' + h.fmtT(Date.parse(d.fetchedAt)) + '</em>' : '';
      h.ticker('<b class="tk-h">' + h.esc(title) + '</b>' + (parts.length ? '<i class="sep">:</i>' + parts.join('<i class="sep">•</i>') : ' — rien de prévu') + stale, false);
    }, 60 * 1000);
  });

  /* ---------- Texte / annonce ---------- */
  A.register('text', function (h) {
    var s = h.settings, el = h.el;
    var k = { s: 0.7, m: 1, l: 1.4, xl: 1.9 }[s.size] || 1;
    el.className += ' ann';
    el.style.background = s.bg || '#0B1F3A';
    el.style.color = s.fg || '#ffffff';
    if (s.scroll === 'loop') {   // défilement continu : titre et texte sur une seule ligne
      el.className += ' ann-scroll';
      el.style.fontSize = 1.7 * k + 'em';
      var line = (s.title ? '<b>' + h.esc(s.title) + '</b><span class="sep">·</span>' : '') + h.esc((s.body || '').replace(/\s*\n+\s*/g, ' · '));
      h.ticker(line, true, { slow: 1.6, normal: 1, fast: 0.6 }[s.scrollSpeed] || 1);
      return;
    }
    el.style.textAlign = s.align === 'left' ? 'left' : 'center';
    el.style.alignItems = s.align === 'left' ? 'flex-start' : 'center';
    if (s.title) {
      var t = document.createElement('div');
      t.className = 'ann-t'; t.textContent = s.title;
      t.style.fontSize = (h.compact ? 2.2 : 5.5) * k + 'em';
      el.appendChild(t);
    }
    var b = document.createElement('div');
    b.className = 'ann-b'; b.textContent = s.body || '';
    b.style.fontSize = (h.compact ? 1.7 : 3.6) * k + 'em';
    el.appendChild(b);
    h.fit();
  });

  /* ---------- QR code : le serveur calcule le tracé (« côté|tracé »), l'écran le dessine sans bibliothèque ---------- */
  A.register('qrcode', function (h) {
    var s = h.settings, parts = (s.qr || '').split('|');
    var n = parseInt(parts[0], 10), path = parts[1] || '';
    if (!(n > 0) || !/^[0-9MhvzH\- ]+$/.test(path)) return;
    h.el.innerHTML = '<svg viewBox="0 0 ' + n + ' ' + n + '" shape-rendering="crispEdges" aria-hidden="true"><path d="' + path + '" fill="#0B1F3A"/></svg>';
    if (s.label) {
      var l = document.createElement('div');
      l.className = 'q-l'; l.textContent = s.label;
      h.el.appendChild(l);
    }
  });

  /* ---------- Compte à rebours (même règle que WidgetLogic.cs « Countdown ») ---------- */
  A.register('countdown', function (h) {
    var s = h.settings, el = h.el;
    var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(s.date || '');
    if (!m) return;
    var detailed = s.format === 'detailed', done = s.doneText || 'C\'est le grand jour !';
    el.innerHTML = '<div class="cd-l"></div><div class="cd-n"></div><div class="cd-u"></div>';
    var l = el.querySelector('.cd-l'), n = el.querySelector('.cd-n'), u = el.querySelector('.cd-u');
    l.textContent = s.label || '';
    function p2(v) { return v < 10 ? '0' + v : '' + v; }
    var tick = function () {
      var now = new Date(), t = new Date(+m[1], +m[2] - 1, +m[3]), today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
      var days = Math.round((t - today) / 864e5), big, unit = '', hidden = false;
      if (detailed && now < t) {
        var left = t - now, d = Math.floor(left / 864e5), hh = Math.floor(left / 36e5) % 24, mm = Math.floor(left / 6e4) % 60, ss = Math.floor(left / 1e3) % 60;
        big = d >= 1 ? d + ' j ' + p2(hh) + ' h ' + p2(mm) + ' min' : p2(hh) + ':' + p2(mm) + ':' + p2(ss);
      } else if (!detailed && days > 0) { big = '' + days; unit = days === 1 ? 'jour' : 'jours'; }
      else { big = done; hidden = days < 0 && s.after !== 'keep'; }
      el.style.display = hidden ? 'none' : '';
      n.textContent = big; u.textContent = unit;
      n.className = 'cd-n' + (unit ? '' : ' txt');
    };
    tick(); h.every(tick, detailed ? 1000 : 30000);
  });

  /* ---------- Citation du jour (même règle que WidgetLogic.cs « Quotes ») ---------- */
  A.register('quote', function (h) {
    var s = h.settings, el = h.el;
    var list = (s.quotes || '').split('\n').map(function (x) { return x.trim(); }).filter(function (x) { return x; }).map(function (x) {
      var m = /^(.*\S)\s+[—–-]\s+(\S.*)$/.exec(x);
      return m ? { t: m[1], a: m[2] } : { t: x, a: '' };
    });
    if (!list.length) return;
    el.innerHTML = '<div class="q-t"></div><div class="q-a"></div>';
    var t = el.querySelector('.q-t'), a = el.querySelector('.q-a');
    var rotate = s.mode === 'rotate', every = Math.max(5, +s.interval || 30);
    var tick = function () {
      var now = new Date(), i;
      if (rotate) i = Math.floor(now.getTime() / 1000 / every) % list.length;
      else i = Math.floor(Date.UTC(now.getFullYear(), now.getMonth(), now.getDate()) / 864e5) % list.length;
      t.textContent = '“' + list[i].t + '”'; a.textContent = list[i].a ? '— ' + list[i].a : '';
    };
    tick(); h.every(tick, rotate ? 1000 : 60000);
  });

  /* ---------- Flux RSS / actualités ---------- */
  A.register('rss', function (h) {
    var s = h.settings, el = h.el;
    if (!h.compact) el.className += ' dl';
    h.data(function (d) {
      var items = (d && d.items) || [];
      if (h.compact) {   // bandeau : titres défilants
        var txt = items.map(function (i) { return h.esc(i.title); }).join('<i class="sep">•</i>');
        h.ticker(txt || h.esc('Aucune actualité'), true);
        return;
      }
      var html = '<div class="dl-h">' + h.esc((d && d.title) || 'Actualités') + '</div>';
      for (var i = 0; i < items.length; i++) {
        var it = items[i];
        html += '<div class="row"><b>' + (it.published ? h.shortDate(Date.parse(it.published)) : '') + '</b><span>' + h.esc(it.title) +
          (s.showSummary !== 'false' && it.summary ? '<i>' + h.esc(it.summary) + '</i>' : '') + '</span></div>';
      }
      el.innerHTML = html + (items.length ? '' : '<div class="dl-empty">Aucune actualité</div>');
      h.fit();
    }, 5 * 60 * 1000);
  });

  /* ---------- YouTube : API IFrame (lecture automatique, enchaînement à la fin, erreurs visibles) ---------- */
  A.register('youtube', function (h) {
    var s = h.settings, u = s.url || '';
    var v = /[?&]v=([A-Za-z0-9_-]{11})/.exec(u), l = /[?&]list=([A-Za-z0-9_-]{10,64})/.exec(u);
    if (!v && !l) { h.note('YouTube : lien de vidéo manquant — republiez la liste de lecture'); return; }
    h.youtube({
      videoId: v ? v[1] : null, playlistId: !v && l ? l[1] : null,
      sound: s.sound === 'true', start: parseInt(s.startAt, 10) || 0, end: parseInt(s.endAt, 10) || 0
    });
  });

  /* ---------- Médiathèque : image, vidéo, diaporamas ---------- */
  // Les fichiers (item.urls) sont résolus à la publication et préchargés par le player : lecture hors ligne.
  function mediaBox(h) {
    h.el.className += ' mlib fit-' + (h.settings.fit === 'cover' ? 'cover' : 'contain');
    var urls = (h.item.urls || []).slice();
    if (h.settings.shuffle === 'true') {
      for (var i = urls.length - 1; i > 0; i--) { var j = Math.floor(Math.random() * (i + 1)); var t = urls[i]; urls[i] = urls[j]; urls[j] = t; }
    }
    var srcs = [];
    h.onStop(function () { for (var k = 0; k < srcs.length; k++) srcs[k].revoke(); });
    return {
      urls: urls,
      load: function (url, cb) { h.media(url).then(function (m) { srcs.push(m); cb(m.src); }); },
      ended: function () { if (h.opts && h.opts.onEnded) { h.opts.onEnded(); return true; } return false; },   // false : pas de fin (seul ou en incrustation) -> on boucle
      failed: function () { if (h.opts && h.opts.onFailed) h.opts.onFailed(); }
    };
  }

  function makeVideo(h, src, onEnd, onErr) {
    var v = document.createElement('video');
    v.muted = h.settings.sound !== 'true'; v.autoplay = true; v.setAttribute('playsinline', '');
    v.onended = onEnd; v.onerror = onErr;
    v.src = src;
    var p = v.play();
    if (p && p.catch) p.catch(function () { v.muted = true; v.play().catch(function () {}); });   // son refusé par le navigateur : lecture muette
    h.onStop(function () { try { v.pause(); v.removeAttribute('src'); v.load(); } catch (e) {} });
    return v;
  }

  A.register('image', function (h) {
    var b = mediaBox(h);
    if (!b.urls.length) { h.note('Image introuvable dans la médiathèque — republiez l\'écran'); return; }
    var img = new Image();
    img.onerror = b.failed;
    h.el.appendChild(img);
    b.load(b.urls[0], function (src) { img.src = src; });
  });

  A.register('video', function (h) {
    var b = mediaBox(h);
    if (!b.urls.length) { h.note('Vidéo introuvable dans la médiathèque — republiez l\'écran'); return; }
    b.load(b.urls[0], function (src) {
      var v = makeVideo(h, src, function () { if (!b.ended()) { v.currentTime = 0; v.play().catch(function () {}); } }, b.failed);
      h.el.appendChild(v);
    });
  });

  // Transition entre deux médias d'un diaporama : type (fondu, glissement, zoom, aucune) et vitesse.
  // maxMs : plafond (une image ne doit pas passer plus de temps en transition qu'affichée).
  var SPEEDS = { fast: 400, normal: 800, slow: 1500, 'very-slow': 3000 };
  function swapper(h, maxMs) {
    var kind = /^(fade|slide|zoom|none)$/.test(h.settings.transition || '') ? h.settings.transition : 'fade';
    var ms = kind === 'none' ? 0 : Math.min(SPEEDS[h.settings.speed] || 800, maxMs || Infinity);
    h.el.className += ' tr-' + kind;
    h.el.style.setProperty('--tr', ms + 'ms');
    var front = null;
    // el est déjà dans le DOM (état d'entrée) : on l'amène à l'écran, et le média précédent sort puis est retiré.
    return function (el, onGone) {
      var old = front; front = el;
      el.offsetWidth;   // force l'état d'entrée avant l'animation
      el.classList.add('on');
      if (!old) return;
      old.classList.remove('on');
      old.classList.add('out');
      setTimeout(function () { if (old.parentNode) old.parentNode.removeChild(old); if (onGone) onGone(old); }, ms + 60);
    };
  }

  A.register('slideshow-images', function (h) {
    var b = mediaBox(h);
    if (!b.urls.length) { h.note('Images introuvables dans la médiathèque — republiez l\'écran'); return; }
    var ms = Math.max(2, parseInt(h.settings.interval, 10) || 8) * 1000;
    var swap = swapper(h, ms * 0.6);
    var idx = 0;
    function show() {
      var img = new Image();
      img.className = 'mlib-slide';
      img.onload = function () { h.el.appendChild(img); swap(img); };
      b.load(b.urls[idx], function (src) { img.src = src; });
    }
    show();
    h.every(function () {
      idx++;
      if (idx >= b.urls.length) { idx = 0; if (b.ended()) return; }   // tour complet : contenu suivant de la liste de lecture
      if (b.urls.length > 1) show();
    }, ms);
  });

  A.register('slideshow-videos', function (h) {
    var b = mediaBox(h);
    if (!b.urls.length) { h.note('Vidéos introuvables dans la médiathèque — republiez l\'écran'); return; }
    var swap = swapper(h);
    var idx = 0;
    function play() {
      b.load(b.urls[idx], function (src) {
        var shown = false, v;
        // la nouvelle vidéo n'entre qu'une fois lancée : la transition part de la dernière image de la précédente
        function reveal() {
          if (shown) return; shown = true;
          swap(v, function (old) { try { old.pause(); old.removeAttribute('src'); old.load(); } catch (e) {} });
        }
        function failed() {   // une vidéo illisible est sautée
          if (!shown && v.parentNode) v.parentNode.removeChild(v);
          shown = true;
          nextVideo();
        }
        v = makeVideo(h, src, nextVideo, failed);
        v.className = 'mlib-slide';
        v.addEventListener('playing', reveal);
        setTimeout(reveal, 2000);   // lecture bloquée ou lente à démarrer : on l'affiche quand même
        h.el.appendChild(v);
      });
    }
    function nextVideo() {
      idx++;
      if (idx >= b.urls.length) { idx = 0; if (b.ended()) return; }
      play();
    }
    play();
  });

  /* ---------- Canva : design exporté dans la médiathèque (pages en images ou vidéo) ----------
     Mêmes rendus que les diaporamas : les fichiers (item.urls) sont résolus à la publication. */
  A.register('canva', function (h) {
    var r = A.get(h.settings.kind === 'video' ? 'slideshow-videos' : 'slideshow-images');
    if (r) r(h);
  });

  /* ---------- Dossier Drive : images et vidéos d'un dossier synchronisé ----------
     Les fichiers (item.urls) sont copiés sur le serveur et tenus à jour à chaque synchronisation : l'écran reçoit
     une nouvelle version (préchargée) quand le dossier change. Une image reste « interval » secondes, une vidéo jusqu'à sa fin. */
  var VIDEO_EXT = /\.(mp4|m4v|webm|mov|ogv)$/i;
  A.register('drive', function (h) {
    var b = mediaBox(h);
    if (!b.urls.length) {   // dossier vide (ou fichiers encore en cours de copie) : on passe au contenu suivant
      h.note('Dossier vide pour l\'instant');
      b.ended();   // seul à l'écran : la note reste jusqu'à la prochaine synchronisation
      return;
    }
    var ms = Math.max(2, parseInt(h.settings.interval, 10) || 8) * 1000;
    var swap = swapper(h, ms * 0.6);
    var idx = 0, timer = null;
    h.onStop(function () { clearTimeout(timer); });
    function next() {
      clearTimeout(timer);
      idx++;
      if (idx >= b.urls.length) { idx = 0; if (b.ended()) return; }   // tour complet : contenu suivant de la liste de lecture
      show();
    }
    function show() {
      var url = b.urls[idx];
      b.load(url, function (src) {
        if (VIDEO_EXT.test(url)) {
          var shown = false, v;
          var reveal = function () { if (shown) return; shown = true; swap(v, function (old) { try { old.pause && old.pause(); } catch (e) {} }); };
          v = makeVideo(h, src, next, function () { if (!shown && v.parentNode) v.parentNode.removeChild(v); shown = true; next(); });   // vidéo illisible : sautée
          v.className = 'mlib-slide';
          v.addEventListener('playing', reveal);
          setTimeout(reveal, 2000);
          h.el.appendChild(v);
        } else {
          var img = new Image();
          img.className = 'mlib-slide';
          img.onload = function () { h.el.appendChild(img); swap(img); };
          img.onerror = function () { clearTimeout(timer); timer = setTimeout(next, 1000); };   // image illisible : sautée
          img.src = src;
          if (b.urls.length > 1 || h.opts && h.opts.onEnded) timer = setTimeout(next, ms);
        }
      });
    }
    show();
  });

  /* ---------- Page web ---------- */
  A.register('webpage', function (h) {
    var s = h.settings, el = h.el;
    if (!/^https?:\/\//i.test(s.url || '')) { el.textContent = 'Adresse de page invalide'; return; }
    var f = document.createElement('iframe');
    f.setAttribute('referrerpolicy', 'no-referrer');
    f.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-forms allow-popups');
    f.style.cssText = 'width:100%;height:100%;border:0;background:#fff';
    f.src = s.url;
    el.appendChild(f);
    var minutes = parseInt(s.refreshMin, 10) || 0;
    if (minutes > 0) h.every(function () { f.src = s.url; }, minutes * 60000);
  });
})(window);
