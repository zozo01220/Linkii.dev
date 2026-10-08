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

  /* ---------- Météo : prévisions sur plusieurs jours (liste ; bandeau défilant en haut / en bas) ---------- */
  A.register('weather-forecast', function (h) {
    var s = h.settings, el = h.el;
    el.classList.add('wf');
    function dayName(d, i, long) {
      if (i === 0) return long ? 'Aujourd\'hui' : 'Auj.';
      if (i === 1) return 'Demain';
      try { return h.cap(new Date(d.date + 'T12:00:00Z').toLocaleDateString('fr-CH', { weekday: long ? 'long' : 'short', timeZone: 'UTC' })); } catch (e) { return d.date; }
    }
    h.data(function (d) {
      if (!d || !d.days || !d.days.length) return;
      var rain = s.showRain !== 'false', deg = '°', city = h.esc(d.city);
      function temps(x) { return '<b>' + Math.round(x.max) + deg + '</b> <i>' + Math.round(x.min) + deg + '</i>'; }
      function rainTxt(x) { return rain ? '<span class="wf-r">💧 ' + x.rain + ' %</span>' : ''; }

      if (h.place === 'top' || h.place === 'bottom') {   // bandeau : une ligne qui défile si elle est trop longue
        var parts = d.days.map(function (x, i) {
          return '<b>' + h.esc(dayName(x, i, true)) + '</b> ' + wmoIcon(x.code) + ' ' + Math.round(x.max) + deg + '/' + Math.round(x.min) + deg + (rain ? ' · ' + x.rain + ' %' : '');
        });
        h.ticker('<b class="tk-h">Météo ' + city + '</b><i class="sep">:</i>' + parts.join('<i class="sep">•</i>'), false);
        return;
      }
      var rows = d.days.slice(0, h.compact ? 3 : 7).map(function (x, i) {
        return '<div class="wf-row"><span class="wf-day">' + h.esc(dayName(x, i, !h.compact)) + '</span><span class="wf-ico">' + wmoIcon(x.code) +
          '</span><span class="wf-t">' + temps(x) + '</span>' + rainTxt(x) + '</div>';
      }).join('');
      var head = h.compact
        ? '<div class="wf-city">' + city + '</div>'
        : '<div class="wf-head"><span class="wf-city">' + city + '</span><span class="wf-now">' + wmoIcon(d.now.code) + ' ' + Math.round(d.now.temp) + deg + h.esc(d.unit) + '</span></div>';
      el.innerHTML = head + rows;
      if (!h.compact) h.fit();
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

  /* ---------- Occupation de salle : fond vert (libre) / rouge (occupée) / orange (bientôt occupée) ----------
     Le serveur n'envoie que des horaires (jamais d'intitulé). Il envoie aussi son heure : on corrige l'horloge de la tablette. */
  var ROOM_L = {
    fr: { free: 'Libre', busy: 'Occupée', soon: 'Bientôt occupée', unknown: 'Indisponible', until: 'jusqu\'à ', from: 'dès ', allDay: 'Libre toute la journée',
          down: 'Calendrier injoignable', next: 'Ensuite', booked: 'Réservée', seats: ' places' },
    en: { free: 'Available', busy: 'Occupied', soon: 'Occupied soon', unknown: 'Unavailable', until: 'until ', from: 'from ', allDay: 'Free all day',
          down: 'Calendar unreachable', next: 'Next', booked: 'Booked', seats: ' seats' },
    de: { free: 'Frei', busy: 'Belegt', soon: 'Bald belegt', unknown: 'Nicht verfügbar', until: 'bis ', from: 'ab ', allDay: 'Ganztägig frei',
          down: 'Kalender nicht erreichbar', next: 'Danach', booked: 'Gebucht', seats: ' Plätze' }
  };
  // Fusionne les créneaux qui se touchent ou se chevauchent : « occupée jusqu'à » est la fin du dernier créneau enchaîné.
  function roomBlocks(d) {
    var ev = ((d && d.events) || []).map(function (e) { return { start: Date.parse(e.start), end: Date.parse(e.end) }; })
      .filter(function (e) { return e.end > e.start; }).sort(function (a, b) { return a.start - b.start; });
    var out = [];
    ev.forEach(function (e) {
      var last = out[out.length - 1];
      if (last && e.start <= last.end) last.end = Math.max(last.end, e.end); else out.push({ start: e.start, end: e.end });
    });
    return out;
  }
  A.register('room', function (h) {
    var el = h.el, offset = 0;
    el.classList.add('dl', 'rm');
    h.data(function (d, fresh) {
      var L = ROOM_L[(d && d.lang) || h.settings.lang] || ROOM_L.fr;
      var title = (d && d.title) || h.settings.title || '';
      if (fresh && d && d.serverNow) offset = Date.parse(d.serverNow) - Date.now();
      var now = Date.now() + offset, blocks = roomBlocks(d), cur = null, next = null, i;
      for (i = 0; i < blocks.length; i++) {
        if (blocks[i].start <= now && now < blocks[i].end) cur = blocks[i];
        else if (blocks[i].start > now && !next) next = blocks[i];
      }
      var soonMs = ((d && d.soonMin) || 0) * 60000;
      var stale = !d || !d.fetchedAt || (Date.now() + offset - Date.parse(d.fetchedAt) > 30 * 60 * 1000);
      var state = stale ? 'unknown' : cur ? 'busy' : (next && soonMs > 0 && next.start - now <= soonMs) ? 'soon' : 'free';
      var sub = state === 'busy' ? L.until + h.fmtT(cur.end)
        : state === 'soon' ? L.from + h.fmtT(next.start)
        : state === 'unknown' ? L.down
        : next ? L.until + h.fmtT(next.start) : L.allDay;
      el.setAttribute('data-state', state);
      if (h.compact) {
        el.innerHTML = '<span class="dot"></span><div class="info"><span class="city">' + h.esc(title) + '</span><span class="rng">' + L[state] +
          (state === 'unknown' ? '' : ' · ' + h.esc(sub)) + '</span></div>';
        return;
      }
      var up = state === 'unknown' ? [] : blocks.filter(function (b) { return b.start > now; }).slice(0, (d && d.upcoming) || 0);
      var list = up.map(function (b) {
        return '<div class="r"><b>' + (h.dayKey(b.start) === h.dayKey(now) ? '' : h.cap(h.fmtDay(b.start, { weekday: 'short' })) + ' ') +
          h.fmtT(b.start) + ' – ' + h.fmtT(b.end) + '</b><span>' + L.booked + '</span></div>';
      }).join('');
      el.innerHTML = '<div class="rm-name">' + h.esc(title) + '</div><div class="rm-state">' + L[state] + '</div><div class="rm-sub">' + h.esc(sub) + '</div>' +
        (d && d.capacity ? '<div class="rm-cap">' + d.capacity + L.seats + '</div>' : '') +
        (list ? '<div class="rm-next"><div class="rm-h">' + L.next + '</div>' + list + '</div>' : '');
    }, 30 * 1000);
  });

  /* ---------- Texte / annonce ---------- */
  A.register('text', function (h) {
    var s = h.settings, el = h.el;
    var k = { s: 0.7, m: 1, l: 1.4, xl: 1.9 }[s.size] || 1;
    el.className += ' ann';
    el.style.background = s.bg || '#0B1F3A';
    el.style.color = s.fg || '#ffffff';
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

  /* ---------- LinkedIn : publications choisies (texte saisi dans le back-office, aucune lecture de LinkedIn) ----------
     Format du champ « posts » : blocs séparés par une ligne « --- » ; lignes « date: », « image: », « lien: » facultatives,
     le reste est le texte (première ligne = titre). */
  // Lien d'une publication LinkedIn -> adresse d'intégration officielle (seul l'identifiant numérique entre dans l'adresse).
  function linkedinEmbed(url) {
    if (!/^https?:\/\/([a-z0-9-]+\.)?linkedin\.com\//i.test(url)) return null;
    var m = /urn:li:(share|ugcPost|activity):(\d{10,25})/i.exec(url) || /[-_\/](share|ugcPost|activity)[-:](\d{10,25})/i.exec(url);
    if (!m) return null;
    var t = m[1].toLowerCase(), type = t === 'ugcpost' ? 'ugcPost' : t;
    return 'https://www.linkedin.com/embed/feed/update/urn:li:' + type + ':' + m[2];
  }
  function isLinkedinUrl(l) { return /^https?:\/\/([a-z0-9-]+\.)?(linkedin\.com|lnkd\.in)\/\S*$/i.test(l); }

  // Séparateur de publications : « --- » seul sur sa ligne, mais aussi collé aux liens ou entre guillemets (« --- »).
  function linkedinBlocks(raw) {
    return String(raw || '').replace(/\r/g, '').split(/[«“"]\s*-{3,}\s*[»”"]|^[ \t]*-{3,}[ \t]*$|[ \t]-{3,}[ \t]/m);
  }
  function parseLinkedinPosts(raw, maxAgeDays, now) {
    var blocks = linkedinBlocks(raw), out = [], i, j;
    for (i = 0; i < blocks.length; i++) {
      var lines = blocks[i].split('\n'), p = { title: '', body: '', date: '', ms: null, image: '', link: '', embed: null, qr: '', useEmbed: false }, text = [];
      for (j = 0; j < lines.length; j++) {
        var m = /^\s*(date|image|lien)\s*:\s*(.*?)\s*$/i.exec(lines[j]);
        var u = lines[j].replace(/^\s*linkedin\s*:\s*/i, '').trim();
        if (isLinkedinUrl(u)) { p.embed = linkedinEmbed(u) || p.embed; if (p.embed) p.qr = p.embed.replace('/embed/feed/update/', '/feed/update/'); }   // lien collé : aperçu officiel (un lien non reconnu est ignoré)
        else if (m) {
          var k = m[1].toLowerCase(), v = m[2];
          if (k === 'date' && /^\d{4}-\d{2}-\d{2}$/.test(v) && !isNaN(Date.parse(v + 'T12:00:00Z'))) { p.date = v; p.ms = Date.parse(v + 'T12:00:00Z'); }
          else if (k === 'image' && /^https?:\/\/\S+$/i.test(v)) p.image = v;
          else if (k === 'lien') { p.link = v; if (/^https?:\/\/\S+$/i.test(v)) p.qr = v; }   // adresse complète : sert au QR code
        } else text.push(lines[j]);
      }
      while (text.length && !text[0].trim()) text.shift();
      if (!text.length && !p.embed) continue;
      p.title = text.length ? text[0].trim() : '';
      p.body = text.slice(1).join('\n').trim();
      p.useEmbed = !!p.embed && !p.title && !p.image;   // lien seul : aperçu officiel ; lien + texte ou image : carte Linkii + QR code
      if (maxAgeDays > 0 && p.ms !== null && now - p.ms > maxAgeDays * 86400000) continue;
      p.order = i;
      out.push(p);
    }
    out.sort(function (a, b) {   // les plus récentes d'abord ; sans date : à la suite, dans l'ordre saisi
      if (a.ms !== null && b.ms !== null) return b.ms - a.ms || a.order - b.order;
      if (a.ms !== null) return -1;
      if (b.ms !== null) return 1;
      return a.order - b.order;
    });
    return out;
  }
  A.parseLinkedinPosts = parseLinkedinPosts;
  A.linkedinEmbed = linkedinEmbed;

  A.register('linkedin', function (h) {
    var s = h.settings, el = h.el;
    var count = Math.max(1, Math.min(6, parseInt(s.count, 10) || 3));
    var rot = Math.max(5, Math.min(120, parseInt(s.rotateSec, 10) || 12)) * 1000;
    var maxAge = parseInt(s.maxAgeDays, 10); if (isNaN(maxAge)) maxAge = 30;
    var showImg = s.showImages !== 'false', showDate = s.showDate !== 'false';
    var title = s.title || 'Actualités';
    var pageName = s.pageName || '';
    var timers = [];   // minuteurs du rendu courant (rotation, ajustement) : arrêtés avant chaque nouveau rendu

    function node(tag, cls, text) { var n = document.createElement(tag); if (cls) n.className = cls; if (text != null) n.textContent = text; return n; }
    function dateTxt(p) { return showDate && p.ms !== null ? h.fmtDay(p.ms, { day: 'numeric', month: 'long', year: 'numeric' }) : ''; }
    function image(p, cls) {
      if (!showImg || !p.image) return null;
      var box = node('div', cls), img = document.createElement('img');
      img.setAttribute('referrerpolicy', 'no-referrer');
      img.onerror = function () { if (box.parentNode) box.parentNode.removeChild(box); };   // hors ligne ou lien mort : la publication reste lisible sans image
      img.src = p.image;
      box.appendChild(img);
      return box;
    }
    // QR code (bibliothèque qr.js, hors ligne) : modules Nuit sur tuile blanche, pour un scan fiable.
    function qrNode(url, cls, caption) {
      if (!url || !window.qrcode) return null;
      try {
        var q = window.qrcode(0, 'M'); q.addData(url); q.make();
        var n = q.getModuleCount(), m = 3, NS = 'http://www.w3.org/2000/svg', d = '', r, c;
        for (r = 0; r < n; r++) for (c = 0; c < n; c++) if (q.isDark(r, c)) d += 'M' + (c + m) + ',' + (r + m) + 'h1v1h-1z';
        var svg = document.createElementNS(NS, 'svg');
        svg.setAttribute('viewBox', '0 0 ' + (n + 2 * m) + ' ' + (n + 2 * m));
        svg.setAttribute('shape-rendering', 'crispEdges');
        var bg = document.createElementNS(NS, 'rect'); bg.setAttribute('width', n + 2 * m); bg.setAttribute('height', n + 2 * m); bg.setAttribute('fill', '#fff');
        var pa = document.createElementNS(NS, 'path'); pa.setAttribute('d', d); pa.setAttribute('fill', '#0B1F3A');
        svg.appendChild(bg); svg.appendChild(pa);
        var box = node('div', 'lk-qr ' + (cls || ''));
        box.appendChild(svg);
        if (caption) box.appendChild(node('span', null, caption));
        return box;
      } catch (e) { return null; }
    }
    function head(compactCard) {
      var hd = node('div', 'lk-h');
      hd.appendChild(node('span', 'lk-in', 'in'));
      var t = node('div', 'lk-ht');
      t.appendChild(node('div', 'lk-t', title));
      if (pageName && !compactCard) t.appendChild(node('div', 'lk-s', pageName));
      hd.appendChild(t);
      return hd;
    }
    function every(fn, ms) { var id = h.every(fn, ms); timers.push(id); return id; }

    function render(all) {
      var i;
      for (i = 0; i < timers.length; i++) clearInterval(timers[i]);
      timers = [];
      all = all.slice(0, count);
      var posts = all, withTitle = all.filter(function (p) { return p.title; });   // bandeaux et angles : seulement les publications qui ont un texte

      if (h.place === 'top' || h.place === 'bottom') {   // bandeau : titres défilants
        h.ticker('<b class="tk-h">' + h.esc(pageName || title) + '</b><i class="sep">:</i>' +
          (withTitle.map(function (p) { return h.esc(p.title); }).join('<i class="sep">•</i>') || h.esc('Aucune publication')), false);
        return;
      }

      el.innerHTML = '';
      el.classList.add('lk');
      el.classList.remove('lk-single', 'lk-embed', 'lk-ed', 'lk-el');

      if (!all.length) {
        if (!h.compact) { el.appendChild(head(false)); el.appendChild(node('div', 'lk-empty', 'Aucune publication')); }
        return;
      }

      if (h.compact) {   // angle : une carte, la plus récente d'abord, puis rotation
        posts = withTitle;
        if (!posts.length) return;
        var idx = 0, card = node('div', 'lk-card');
        el.appendChild(card);
        var drawCard = function () {
          var p = posts[idx % posts.length];
          card.innerHTML = '';
          card.appendChild(head(true));
          var im = image(p, 'lk-img'); if (im) card.appendChild(im);
          card.appendChild(node('div', 'lk-pt', p.title));
          var d = dateTxt(p); if (d) card.appendChild(node('div', 'lk-d', d));
        };
        drawCard();
        if (posts.length > 1) every(function () { idx++; drawCard(); }, rot);
        return;
      }

      if (s.layout === 'single') {   // pleine page, une publication à la fois
        var n = 0, sizeIv = null;
        el.classList.add('lk-single');
        var drawOne = function () {
          var p = posts[n % posts.length];
          el.innerHTML = '';
          el.classList.remove('lk-embed', 'lk-ed', 'lk-el');
          if (sizeIv) { clearInterval(sizeIv); sizeIv = null; }
          if (p.useEmbed && navigator.onLine !== false) {   // aperçu officiel LinkedIn : demande une connexion sur l'écran
            el.classList.add('lk-embed', s.embedBg === 'dark' ? 'lk-ed' : 'lk-el');
            // LinkedIn dessine l'aperçu à sa largeur native (504 px) : on le charge à cette taille puis on l'agrandit pour remplir la zone.
            // Le haut (bandeau « cookies » de LinkedIn, que l'écran ne peut pas fermer) est rogné ; rognage et hauteur utile sont des réglages.
            var EW = 504, CROP = Math.max(0, Math.min(400, isNaN(parseInt(s.embedCrop, 10)) ? 100 : parseInt(s.embedCrop, 10))), V = Math.max(300, Math.min(1000, parseInt(s.embedHeight, 10) || 520));
            var box = node('div', 'lk-ebox'), f = document.createElement('iframe');
            f.className = 'lk-frame';
            f.setAttribute('referrerpolicy', 'no-referrer');
            f.setAttribute('scrolling', 'no');
            f.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-popups');
            f.style.width = EW + 'px'; f.style.height = (V + CROP) + 'px';
            f.src = p.embed;
            box.appendChild(f); el.appendChild(box);
            var eq = qrNode(p.qr, 'v', 'Scannez pour ouvrir la publication'); if (eq) el.appendChild(eq);
            var size = function () {
              var bh = el.clientHeight, bw = el.clientWidth;
              if (!bh || !bw) return;
              var k = Math.min(bh * 0.94 / V, bw * (eq ? 0.62 : 0.8) / EW);
              box.style.width = Math.round(EW * k) + 'px'; box.style.height = Math.round(V * k) + 'px';
              f.style.transform = 'scale(' + k + ')'; f.style.top = Math.round(-CROP * k) + 'px';
            };
            size(); setTimeout(size, 0); sizeIv = every(size, 3000);
            return;
          }
          var im = image(p, 'lk-img');
          if (im) el.appendChild(im);
          var col = node('div', 'lk-col');
          col.appendChild(head(false));
          col.appendChild(node('div', 'lk-pt', p.title || 'Publication LinkedIn'));
          if (p.body) col.appendChild(node('div', 'lk-b', p.body));
          var d = dateTxt(p); if (d) col.appendChild(node('div', 'lk-d', d));
          if (p.link && !p.qr) col.appendChild(node('div', 'lk-l', p.link));
          var qn = qrNode(p.qr, '', 'Scannez pour lire la publication'); if (qn) col.appendChild(qn);
          el.appendChild(col);
          h.fit();
        };
        drawOne();
        if (posts.length > 1) every(function () { n++; drawOne(); }, rot);
        return;
      }

      el.appendChild(head(false));   // pleine page, liste
      for (i = 0; i < posts.length; i++) {
        var p = posts[i], row = node('div', 'lk-row');
        var im = image(p, 'lk-img'); if (im) row.appendChild(im);
        var col = node('div', 'lk-col');
        col.appendChild(node('div', 'lk-pt', p.title || 'Publication LinkedIn'));
        var d = dateTxt(p); if (d) col.appendChild(node('div', 'lk-d', d));
        row.appendChild(col);
        var ql = qrNode(posts[i].qr, 'sm'); if (ql) row.appendChild(ql);
        el.appendChild(row);
      }
      h.fit();
    }

    if (s.source === 'page') {   // page entreprise : publications lues par le serveur (API officielle), dernière version gardée hors ligne
      var last = null;
      h.data(function (d) {
        if (!d || d.mode !== 'page' || !d.posts) return;
        var sig = JSON.stringify(d.posts);
        if (sig === last) return;   // inchangé : la rotation en cours n'est pas interrompue
        last = sig;
        if (!pageName && d.pageName) pageName = d.pageName;
        render(d.posts.map(function (x) {
          var ms = x.date ? Date.parse(x.date + 'T12:00:00Z') : NaN;
          return { title: x.title || '', body: x.body || '', date: x.date || '', ms: isNaN(ms) ? null : ms, image: /^https:\/\//i.test(x.image || '') ? x.image : '',
                   link: '', embed: null, qr: /^https:\/\/www\.linkedin\.com\//i.test(x.qr || '') ? x.qr : '', useEmbed: false };
        }));
      }, 15 * 60 * 1000);
      return;
    }
    render(parseLinkedinPosts(s.posts, maxAge, Date.now()));
  });

  /* ---------- YouTube : API IFrame (lecture automatique, enchaînement à la fin, erreurs visibles) ---------- */
  A.register('youtube', function (h) {
    var s = h.settings, u = s.url || '';
    var v = /[?&]v=([A-Za-z0-9_-]{11})/.exec(u), l = /[?&]list=([A-Za-z0-9_-]{10,64})/.exec(u);
    if (!v && !l) { h.note('YouTube : lien de vidéo manquant — republiez la playlist'); return; }
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

  A.register('slideshow-images', function (h) {
    var b = mediaBox(h);
    if (!b.urls.length) { h.note('Images introuvables dans la médiathèque — republiez l\'écran'); return; }
    var ms = Math.max(2, parseInt(h.settings.interval, 10) || 8) * 1000;
    var idx = 0, front = null;
    function show() {
      var img = new Image();
      img.className = 'mlib-slide';
      img.onload = function () {
        h.el.appendChild(img);
        setTimeout(function () { img.classList.add('on'); }, 30);
        var old = front; front = img;
        if (old) setTimeout(function () { if (old.parentNode) old.parentNode.removeChild(old); }, 900);
      };
      b.load(b.urls[idx], function (src) { img.src = src; });
    }
    show();
    h.every(function () {
      idx++;
      if (idx >= b.urls.length) { idx = 0; if (b.ended()) return; }   // tour complet : contenu suivant de la playlist
      show();
    }, ms);
  });

  A.register('slideshow-videos', function (h) {
    var b = mediaBox(h);
    if (!b.urls.length) { h.note('Vidéos introuvables dans la médiathèque — republiez l\'écran'); return; }
    var idx = 0, cur = null;
    function play() {
      b.load(b.urls[idx], function (src) {
        var v = makeVideo(h, src, nextVideo, nextVideo);   // une vidéo illisible est sautée
        if (cur && cur.parentNode) { try { cur.pause(); } catch (e) {} cur.parentNode.removeChild(cur); }
        cur = v;
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
