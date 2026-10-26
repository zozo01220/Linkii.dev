/* Compositions (DEV-POC.md §80) : rendu commun à l'éditeur du back-office (compo-editor.js) et au player (app « composition »).
   JS volontairement "ancien" comme apps.js (var, pas de fonctions fléchées) : il tourne aussi dans la WebView Android.
   Une composition : { w, h, bg, els: [ … ] }, positions et tailles en pixels de la page ; l'appelant met la scène à l'échelle.
   Calques : text | shape (rect, circle, line, arrow) | image (media : identifiant ; src : adresse posée à la publication)
             | qr (url ; qr : « côté|tracé » calculé par le serveur) | widget (clock, date, ticker, countdown)
             | app (§85 : bloc d'une intégration de Mes intégrations ; source : identifiant du contenu, app : son intégration).
   Un texte peut contenir des champs reliés « {{source:clé}} » : valeur lue dans les données de la source (o.data[source]), sinon la valeur de secours.
   Toutes les valeurs venant du document sont filtrées ici (couleurs, polices, nombres) : rien n'est injecté tel quel. */
(function (w) {
  'use strict';
  var FONTS = ['IBM Plex Sans', 'Montserrat', 'Poppins', 'Playfair Display', 'Oswald', 'Arial'];
  // Polices embarquées (player/fonts, licence OFL) : servies par Linkivi et gardées par le Service Worker du player, donc lisibles hors ligne.
  var FONT_CSS = '/player/fonts/compo-fonts.css';
  var CSS = '.lc-stage{position:absolute;left:0;top:0;transform-origin:0 0;overflow:hidden}' +
    '.lc-el{position:absolute;box-sizing:border-box}' +
    '.lc-tx{white-space:pre-wrap;word-wrap:break-word;outline:none}' +
    '.lc-img{width:100%;height:100%;background-position:center;background-repeat:no-repeat}' +
    '.lc-img.empty{background:repeating-linear-gradient(45deg,#dfe5ec 0 16px,#eef2f6 16px 32px);display:flex;align-items:center;justify-content:center;text-align:center;color:#5A6B80;font:500 26px "IBM Plex Sans",sans-serif}' +
    '.lc-qr{width:100%;height:100%;box-sizing:border-box}.lc-qr svg{display:block;width:100%;height:100%}' +
    '.lc-tk{width:100%;height:100%;display:flex;align-items:center;overflow:hidden;white-space:nowrap}' +
    '.lc-tkin{display:inline-flex;animation:lc-tk linear infinite}.lc-tkin span{padding-right:2em}' +
    '@keyframes lc-tk{from{transform:translateX(0)}to{transform:translateX(-50%)}}' +
    '.lc-app{width:100%;height:100%;box-sizing:border-box;overflow:hidden;display:flex;flex-direction:column;justify-content:center;line-height:1.2}' +
    '.lc-app.bg-light,.lc-app.bg-dark{padding:.6em .8em;border-radius:.45em}.lc-app.bg-light{background:rgba(255,255,255,.92)}.lc-app.bg-dark{background:rgba(11,31,58,.88)}' +
    '.lc-wx{display:flex;align-items:center;gap:.4em}.lc-wx i{font-style:normal;font-size:2.4em;line-height:1}.lc-wx b{font-size:2.3em;font-weight:inherit;line-height:1}' +
    '.lc-wx span{display:flex;flex-direction:column;font-size:.75em;opacity:.85;min-width:0}.lc-wx span em{font-style:normal;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +
    '.lc-days{display:flex;gap:1.2em;margin-top:.5em;font-size:.62em;opacity:.85;white-space:nowrap}' +
    '.lc-rows{display:flex;flex-direction:column;gap:.35em;min-height:0}.lc-row{display:flex;gap:.7em;align-items:baseline;min-width:0}' +
    '.lc-row b{flex:none;font-weight:inherit;opacity:.75;font-variant-numeric:tabular-nums}.lc-row span{flex:1;min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +
    '.lc-row small{flex:none;font-size:.78em;opacity:.7;max-width:40%;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +
    '.lc-row.lc-news span{white-space:normal;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical}' +
    '.lc-ah{font-size:.8em;opacity:.7;margin-bottom:.4em;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +
    '.lc-wait{opacity:.55;font-size:.7em;text-align:center}';

  function esc(s) { return String(s == null ? '' : s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;'); }
  function num(v, d, lo, hi) { var n = parseFloat(v); if (!isFinite(n)) n = d; return Math.min(hi, Math.max(lo, n)); }
  function col(v, d) { return /^#[0-9a-fA-F]{3,8}$/.test(v || '') ? v : d; }
  function font(v) { return FONTS.indexOf(v) !== -1 ? v : FONTS[0]; }
  function weight(v) { var n = Math.round(num(v, 400, 100, 900) / 100) * 100; return n; }
  function align(v) { return v === 'center' || v === 'right' ? v : 'left'; }
  function textual(e) { return e.type === 'text' || (e.type === 'widget' && (e.kind === 'clock' || e.kind === 'date' || e.kind === 'countdown')); }

  function txs(e) {
    return 'font-family:\'' + font(e.font) + '\',sans-serif;font-size:' + num(e.size, 40, 4, 2000) + 'px;font-weight:' + weight(e.weight) +
      ';color:' + col(e.color, '#0B1F3A') + ';text-align:' + align(e.align) + ';letter-spacing:' + num(e.ls, 0, -0.2, 1) +
      'em;line-height:' + num(e.lh, 1.15, 0.5, 3) + ';font-style:' + (e.italic ? 'italic' : 'normal');
  }

  function pad(n) { return n < 10 ? '0' + n : '' + n; }
  function timeText(sec, tz) {
    var d = new Date();
    try { var o = { hour: '2-digit', minute: '2-digit', timeZone: tz || undefined }; if (sec) o.second = '2-digit'; return d.toLocaleTimeString('fr-CH', o); }
    catch (x) { return pad(d.getHours()) + ':' + pad(d.getMinutes()) + (sec ? ':' + pad(d.getSeconds()) : ''); }
  }
  function dateText(tz) {
    var s;
    try { s = new Date().toLocaleDateString('fr-CH', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric', timeZone: tz || undefined }); }
    catch (x) { s = new Date().toLocaleDateString(); }
    return s.charAt(0).toUpperCase() + s.slice(1);
  }
  function daysTo(until) {
    var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(until || '');
    if (!m) return 0;
    var now = new Date(), t = new Date(+m[1], +m[2] - 1, +m[3]), today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    return Math.max(0, Math.round((t - today) / 864e5));
  }

  /* ---------- Sources de données (§85) : blocs et champs ---------- */
  var SRC = {
    'weather-slide': { name: 'Météo', icon: '⛅', logo: 'weather', color: '#E0A526', refresh: 10 * 60000 },
    agenda: { name: 'Calendrier', icon: '📅', color: '#2E8B57', refresh: 60000 },
    rss: { name: 'Flux RSS', icon: '📰', logo: 'rss', color: '#C0485A', refresh: 5 * 60000 },
    'ha-sensors': { name: 'Capteurs', icon: '🌡', logo: 'ha', color: '#41BDF5', refresh: 30000 }
  };
  var WMO = [[[0], '☀️'], [[1, 2], '🌤️'], [[3], '☁️'], [[45, 48], '🌫️'], [[51, 53, 55, 56, 57], '🌦️'],
    [[61, 63, 65, 66, 67, 80, 81, 82], '🌧️'], [[71, 73, 75, 77, 85, 86], '❄️'], [[95, 96, 99], '⛈️']];
  function wmo(code) { for (var i = 0; i < WMO.length; i++) if (WMO[i][0].indexOf(code) !== -1) return WMO[i][1]; return '🌡️'; }
  function rnd(v) { var n = Math.round(+v); return isFinite(n) ? '' + n : ''; }
  function tzo(o, tz) { if (tz) o.timeZone = tz; return o; }
  function hm(ms, tz) { try { return new Date(ms).toLocaleTimeString('fr-CH', tzo({ hour: '2-digit', minute: '2-digit' }, tz)); } catch (x) { var d = new Date(ms); return pad(d.getHours()) + ':' + pad(d.getMinutes()); } }
  function dayKey(ms, tz) { try { return new Date(ms).toLocaleDateString('fr-CA', tzo({}, tz)); } catch (x) { return new Date(ms).toDateString(); } }
  function dayName(ms, tz) {
    var k = dayKey(ms, tz), now = Date.now();
    if (k === dayKey(now, tz)) return 'aujourd\'hui';
    if (k === dayKey(now + 864e5, tz)) return 'demain';
    try { return new Date(ms).toLocaleDateString('fr-CH', tzo({ weekday: 'long', day: 'numeric', month: 'long' }, tz)); } catch (x) { return k; }
  }
  // Événements d'un calendrier encore à venir (ou en cours), du plus proche au plus lointain
  function upcoming(d) {
    var now = Date.now(), l = [];
    for (var i = 0; i < ((d && d.events) || []).length; i++) { var e = d.events[i], end = Date.parse(e.end || e.start); if (!(end <= now)) l.push(e); }
    return l.sort(function (a, b) { return Date.parse(a.start) - Date.parse(b.start); });
  }
  function evTime(e, tz, withDay) {
    var s = Date.parse(e.start), t = e.allDay ? 'Journée' : hm(s, tz);
    return withDay && dayKey(s, tz) !== dayKey(Date.now(), tz) ? dayName(s, tz).replace(/ \d.*$/, '').slice(0, 3) + '. ' + t : t;
  }
  function haVal(x) {
    if (!x || x.state === 'absent' || x.state === 'unavailable' || x.state === 'unknown') return '—';
    if (x.state === 'on' || x.state === 'off') return x.state === 'on' ? 'Activé' : 'Désactivé';
    var n = parseFloat(x.state);
    return (isFinite(n) && /^-?[\d.]+$/.test(x.state) ? (Math.round(n * 10) / 10 + '').replace('.', ',') : x.state) + (x.unit ? ' ' + x.unit : '');
  }
  /** Champs d'une source : [{ key, label, value(données, fuseau) }] ; les capteurs dépendent des données reçues (une entrée par entité). */
  function fields(app, d) {
    if (app === 'weather-slide') return [
      { key: 'city', label: 'Ville', value: function (d) { return d.city; } },
      { key: 'temp', label: 'Température', value: function (d) { return rnd(d.temp); } },
      { key: 'tempu', label: 'Température avec unité', value: function (d) { return rnd(d.temp) + ' °' + (d.unit || 'C'); } },
      { key: 'feel', label: 'Ressenti', value: function (d) { return rnd(d.feel); } },
      { key: 'text', label: 'Conditions', value: function (d) { return d.text; } },
      { key: 'icon', label: 'Pictogramme', value: function (d) { return wmo(d.code); } },
      { key: 'min', label: 'Min. aujourd\'hui', value: function (d) { return rnd(d.min); } },
      { key: 'max', label: 'Max. aujourd\'hui', value: function (d) { return rnd(d.max); } },
      { key: 'wind', label: 'Vent', value: function (d) { return rnd(d.wind) + ' km/h'; } },
      { key: 'humidity', label: 'Humidité', value: function (d) { return rnd(d.humidity) + ' %'; } }];
    if (app === 'agenda') {
      var nx = function (d) { return upcoming(d)[0]; };
      return [
        { key: 'next.title', label: 'Prochain événement · titre', value: function (d) { var e = nx(d); return e ? e.title : null; } },
        { key: 'next.time', label: 'Prochain événement · heure', value: function (d, tz) { var e = nx(d); return e ? evTime(e, tz) : null; } },
        { key: 'next.day', label: 'Prochain événement · jour', value: function (d, tz) { var e = nx(d); return e ? dayName(Date.parse(e.start), tz) : null; } },
        { key: 'next.where', label: 'Prochain événement · lieu', value: function (d) { var e = nx(d); return e ? e.location : null; } },
        { key: 'today', label: 'Événements à venir aujourd\'hui', value: function (d, tz) { var k = dayKey(Date.now(), tz); return '' + upcoming(d).filter(function (e) { return dayKey(Date.parse(e.start), tz) === k; }).length; } },
        { key: 'title', label: 'Titre du calendrier', value: function (d) { return d.title; } }];
    }
    if (app === 'rss') return [
      { key: 'title', label: 'Nom du flux', value: function (d) { return d.title; } },
      { key: 'i1', label: 'Article 1 · titre', value: function (d) { return d.items && d.items[0] ? d.items[0].title : null; } },
      { key: 'i2', label: 'Article 2 · titre', value: function (d) { return d.items && d.items[1] ? d.items[1].title : null; } },
      { key: 'i3', label: 'Article 3 · titre', value: function (d) { return d.items && d.items[2] ? d.items[2].title : null; } }];
    if (app === 'ha-sensors') return ((d && d.entities) || []).map(function (x) {
      return { key: 'e:' + x.id, label: x.name || x.id, value: function (d) { for (var i = 0; i < (d.entities || []).length; i++) if (d.entities[i].id === x.id) return haVal(d.entities[i]); return null; } };
    });
    return [];
  }
  var TOKEN = /\{\{([0-9a-f]{32}):([A-Za-z0-9_.:-]{1,120})\}\}/g;
  function srcOf(o, id) { return (o.sources && o.sources[id]) || null; }   // { app, name } : sources connues (éditeur) ; sur l'écran, déduites des données
  /** Valeur (ou nom, mode « Noms des champs ») d'un champ relié. */
  function fieldText(id, key, o, fb) {
    var d = o.data && o.data[id], s = srcOf(o, id), app = s ? s.app : d ? ({ weather: 'weather-slide', agenda: 'agenda', rss: 'rss' })[d.kind] || (d.entities ? 'ha-sensors' : '') : '';
    var f = fields(app, d), def = null;
    for (var i = 0; i < f.length; i++) if (f[i].key === key) def = f[i];
    if (o.names) return '{' + (def ? def.label : key.replace(/^e:/, '')) + '}';
    var v = null;
    if (d && !d.error && def) { try { v = def.value(d, o.tz); } catch (x) { v = null; } }
    return v == null || v === '' ? (fb || '—') : String(v);
  }
  function textHtml(e, o) {
    var t = String(e.text == null ? '' : e.text), out = '', last = 0, m;
    TOKEN.lastIndex = 0;
    while ((m = TOKEN.exec(t))) {
      out += esc(t.slice(last, m.index)) + '<span class="lc-fd" data-f="' + m[1] + ':' + esc(m[2]) + '">' + esc(fieldText(m[1], m[2], o, e.fb)) + '</span>';
      last = TOKEN.lastIndex;
    }
    return out + esc(t.slice(last));
  }
  function hasFields(e) { TOKEN.lastIndex = 0; return e.type === 'text' && TOKEN.test(String(e.text || '')); }

  function block(e, o) {
    var d = o.data && o.data[e.source], meta = SRC[e.app] || { name: 'Intégration', icon: '◇' }, tz = o.tz, h = '';
    var cls = 'lc-app' + (e.bg === 'light' || e.bg === 'dark' ? ' bg-' + e.bg : ''), st = txs(e).replace(/text-align:[a-z]+;/, '');
    if (!d || d.error) {
      var msg = d && d.error ? 'Données indisponibles' : o.player ? '' : 'Chargement des données…';
      return '<div class="' + cls + '" style="' + st + '"><div class="lc-wait">' + meta.icon + ' ' + esc(srcOf(o, e.source) ? srcOf(o, e.source).name : meta.name) + (msg ? '<br>' + msg : '') + '</div></div>';
    }
    var rows = Math.round(num(e.rows, 4, 1, 12));
    if (e.app === 'weather-slide') {
      h = '<div class="lc-wx"><i>' + wmo(d.code) + '</i><b>' + rnd(d.temp) + '°</b><span><em>' + esc(d.city) + '</em><em>' + esc(d.text) + '</em></span></div>';
      if (e.view !== 'now' && d.days && d.days.length) {
        h += '<div class="lc-days">' + d.days.slice(0, 3).map(function (x) {
          var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(x.date || ''), n = m ? ['dim.', 'lun.', 'mar.', 'mer.', 'jeu.', 'ven.', 'sam.'][new Date(+m[1], +m[2] - 1, +m[3]).getDay()] : '';
          return '<span>' + n + ' ' + wmo(x.code) + ' ' + rnd(x.max) + '° / ' + rnd(x.min) + '°</span>';
        }).join('') + '</div>';
      }
    } else if (e.app === 'agenda') {
      var ev = upcoming(d).slice(0, rows);
      h = ev.length ? '<div class="lc-rows">' + ev.map(function (x) {
        return '<div class="lc-row"><b>' + esc(evTime(x, tz, true)) + '</b><span>' + esc(x.title) + '</span>' + (x.location ? '<small>' + esc(x.location) + '</small>' : '') + '</div>';
      }).join('') + '</div>' : '<div class="lc-wait">' + esc(e.empty || 'Aucun événement à venir') + '</div>';
    } else if (e.app === 'rss') {
      var it = (d.items || []).slice(0, rows);
      h = (d.title && e.head !== false ? '<div class="lc-ah">' + esc(d.title) + '</div>' : '') +
        '<div class="lc-rows">' + it.map(function (x) { return '<div class="lc-row lc-news"><b>•</b><span>' + esc(x.title) + '</span></div>'; }).join('') + '</div>';
    } else if (e.app === 'ha-sensors') {
      h = '<div class="lc-rows">' + (d.entities || []).slice(0, rows).map(function (x) { return '<div class="lc-row"><span>' + esc(x.name || x.id) + '</span><b>' + esc(haVal(x)) + '</b></div>'; }).join('') + '</div>';
    }
    return '<div class="' + cls + '" style="' + st + '">' + h + '</div>';
  }

  // QR code : tracé du serveur ; en attendant (éditeur), un motif neutre
  function qrSvg(e) {
    var fg = col(e.fg, '#000000'), parts = (e.qr || '').split('|'), n = parseInt(parts[0], 10), path = parts[1] || '';
    if (n > 0 && /^[0-9MhvzH\- ]+$/.test(path))
      return '<svg viewBox="0 0 ' + n + ' ' + n + '" shape-rendering="crispEdges"><path d="' + path + '" fill="' + fg + '"/></svg>';
    return '<svg viewBox="0 0 10 10"><rect width="10" height="10" fill="none" stroke="' + fg + '" stroke-width=".4" stroke-dasharray=".8 .6"/><text x="5" y="6" font-size="2" text-anchor="middle" fill="' + fg + '">QR</text></svg>';
  }

  function inner(e, o) {
    var t = e.type;
    if (t === 'text') return '<div class="lc-tx" style="' + txs(e) + '">' + textHtml(e, o) + '</div>';
    if (t === 'app') return block(e, o);
    if (t === 'shape') {
      var fill = col(e.fill, '#3B82C4');
      if (e.kind === 'arrow') return '<svg viewBox="0 0 100 60" preserveAspectRatio="none" width="100%" height="100%" style="display:block"><path d="M0 21H60V0L100 30L60 60V39H0Z" fill="' + fill + '"/></svg>';
      if (e.kind === 'line') { var sw = num(e.sw, 6, 1, 200); return '<div style="position:absolute;left:0;right:0;top:50%;height:' + sw + 'px;margin-top:-' + sw / 2 + 'px;background:' + fill + '"></div>'; }
      var r = e.kind === 'circle' ? '50%' : num(e.radius, 0, 0, 4000) + 'px';
      var b = e.stroke && col(e.stroke, '') ? 'border:' + num(e.sw, 6, 1, 200) + 'px solid ' + col(e.stroke, '#000000') + ';' : '';
      return '<div style="box-sizing:border-box;width:100%;height:100%;background:' + fill + ';border-radius:' + r + ';' + b + '"></div>';
    }
    if (t === 'image') {
      var url = e.src || (o.mediaUrl ? o.mediaUrl(e.media) : null), rr = 'border-radius:' + num(e.radius, 0, 0, 4000) + 'px;';
      if (!url) return o.player ? '' : '<div class="lc-img empty" style="' + rr + '">Image à choisir</div>';
      return '<div class="lc-img" data-src="' + esc(url) + '" style="' + rr + 'background-size:' + (e.fit === 'contain' ? 'contain' : 'cover') + '"></div>';
    }
    if (t === 'qr') return '<div class="lc-qr" style="background:' + col(e.bg, '#ffffff') + ';padding:6%">' + qrSvg(e) + '</div>';
    if (t === 'widget') {
      if (e.kind === 'clock') return '<div class="lc-tx" style="' + txs(e) + '" data-live="clock" data-sec="' + (e.sec ? 1 : 0) + '">' + timeText(e.sec, o.tz) + '</div>';
      if (e.kind === 'date') return '<div class="lc-tx" style="' + txs(e) + '" data-live="date">' + esc(dateText(o.tz)) + '</div>';
      if (e.kind === 'countdown') return '<div class="lc-tx" style="' + txs(e) + '" data-live="countdown" data-until="' + esc(e.until) + '">J-' + daysTo(e.until) + '</div>';
      if (e.kind === 'ticker') {
        var tx = esc(e.text);
        return '<div class="lc-tk" style="' + txs(e) + ';background:' + col(e.bg, '#0B1F3A') + '"><div class="lc-tkin" style="animation-duration:' + num(e.speed, 18, 3, 120) + 's"><span>' + tx + '</span><span>' + tx + '</span></div></div>';
      }
    }
    return '';
  }

  var C = w.LinkiiCompo = {
    FONTS: FONTS,
    textual: textual,
    SRC: SRC,
    fields: fields,
    fieldText: fieldText,
    TOKEN: TOKEN,
    /** Sources utilisées par un document (blocs et champs), sans doublon. */
    sources: function (doc) {
      var ids = [], els = (doc && doc.els) || [];
      function add(id) { if (id && ids.indexOf(id) === -1) ids.push(id); }
      for (var i = 0; i < els.length; i++) {
        var e = els[i], m;
        if (e.type === 'app') add(e.source);
        if (e.type === 'text') { TOKEN.lastIndex = 0; while ((m = TOKEN.exec(String(e.text || '')))) add(m[1]); }
      }
      return ids;
    },
    /** Redessine les calques reliés à des données (blocs, textes à champs) sans toucher aux autres (bandeaux, images). */
    refresh: function (root, doc, o) {
      var els = (doc && doc.els) || [];
      for (var i = 0; i < els.length; i++) {
        var e = els[i];
        if (e.type !== 'app' && !hasFields(e)) continue;
        var n = root.querySelector('.lc-el[data-id="' + String(e.id).replace(/["\\]/g, '') + '"]');
        if (n) { var h = inner(e, o); if (n.innerHTML !== h) n.innerHTML = h; }
      }
    },
    /** Feuille de style et polices, posées une seule fois dans la page. */
    ensure: function () {
      if (document.getElementById('lc-css')) return;
      var s = document.createElement('style'); s.id = 'lc-css'; s.textContent = CSS; document.head.appendChild(s);
      var l = document.createElement('link'); l.rel = 'stylesheet'; l.href = FONT_CSS; document.head.appendChild(l);
    },
    /** Un calque : options { mediaUrl(id) -> adresse (éditeur), player (image sans fichier omise), tz, attrs(e) -> attributs en plus }. */
    el: function (e, o) {
      o = o || {};
      if (!e || e.hidden && o.player) return '';
      var st = 'left:' + num(e.x, 0, -1e5, 1e5) + 'px;top:' + num(e.y, 0, -1e5, 1e5) + 'px;width:' + num(e.w, 100, 1, 1e5) + 'px;' +
        (textual(e) ? '' : 'height:' + num(e.h, 100, 1, 1e5) + 'px;') + 'opacity:' + num(e.opacity, 1, 0, 1) + ';' +
        (e.rot ? 'transform:rotate(' + num(e.rot, 0, -360, 360) + 'deg);' : '') + (e.hidden ? 'display:none;' : '');
      return '<div class="lc-el' + (textual(e) ? ' lc-auto' : '') + '" data-id="' + esc(e.id) + '" style="' + st + '">' + inner(e, o) + '</div>';
    },
    html: function (doc, o) { var s = ''; for (var i = 0; i < (doc.els || []).length; i++) s += C.el(doc.els[i], o); return s; },
    bg: function (doc) { return col(doc.bg, '#FFFFFF'); },
    /** Charge les images de la scène : loader(url, cb(src)). */
    images: function (root, loader) {
      var list = root.querySelectorAll('.lc-img[data-src]');
      for (var i = 0; i < list.length; i++) (function (d) {
        loader(d.getAttribute('data-src'), function (src) { d.style.backgroundImage = 'url("' + String(src).replace(/"/g, '%22') + '")'; });
      })(list[i]);
    },
    /** Vignettes des pages de l'application : chaque .cmp-th porte le document publié (data-doc), dessiné à l'échelle de sa largeur. */
    thumbs: function (root) {
      C.ensure();
      var list = (root || document).querySelectorAll('.cmp-th[data-doc]');
      for (var i = 0; i < list.length; i++) (function (n) {
        var raw = n.getAttribute('data-doc'), doc = null;
        if (n.getAttribute('data-done') === raw) return;
        try { doc = JSON.parse(raw); } catch (x) { doc = null; }
        n.setAttribute('data-done', raw);
        n.innerHTML = '';
        if (!doc || !(doc.w > 0) || !(doc.h > 0)) return;
        var st = document.createElement('div');
        st.className = 'lc-stage';
        st.style.cssText = 'width:' + doc.w + 'px;height:' + doc.h + 'px;background:' + C.bg(doc) + ';pointer-events:none';
        st.innerHTML = C.html(doc, { player: true });
        n.appendChild(st);
        var contain = n.hasAttribute('data-contain');   // case de taille fixe (ligne d'une liste) : page entière, centrée
        function fit() {
          var W = n.clientWidth || 1, H = n.clientHeight || 1;
          if (contain) {
            var k = Math.min(W / doc.w, H / doc.h);
            st.style.transform = 'translate(' + (W - doc.w * k) / 2 + 'px,' + (H - doc.h * k) / 2 + 'px) scale(' + k + ')';
            return;
          }
          var s = W / doc.w;
          n.style.height = Math.round(doc.h * s) + 'px';
          st.style.transform = 'scale(' + s + ')';
        }
        fit();
        if (w.ResizeObserver) new w.ResizeObserver(fit).observe(n);
        C.images(st, function (u, cb) { cb(u + (u.indexOf('?') < 0 ? '?w=480' : '')); });
      })(list[i]);
    },
    /** Met à jour horloges, dates et comptes à rebours. */
    tick: function (root, tz) {
      var list = root.querySelectorAll('[data-live]');
      for (var i = 0; i < list.length; i++) {
        var n = list[i], k = n.getAttribute('data-live'), v;
        v = k === 'clock' ? timeText(n.getAttribute('data-sec') === '1', tz) : k === 'date' ? dateText(tz) : 'J-' + daysTo(n.getAttribute('data-until'));
        if (n.textContent !== v) n.textContent = v;
      }
    }
  };

  // QR de comptage des scans : en % de la page (pas de l'écran), dessiné dans la scène mise à l'échelle ; tracé calculé par le serveur
  function addQr(st, q, doc) {
    if (!q || !q.d) return;
    var parts = String(q.d).split('|'), n = parseInt(parts[0], 10), path = parts[1] || '';
    if (!(n > 0) || !/^[0-9MhvzH\- ]+$/.test(path)) return;
    var size = Math.max(2, Math.min(60, Number(q.size) || 19)), x = Number(q.x), y = Number(q.y);
    var e = document.createElement('div');
    e.className = 'qr-ov';
    e.style.cssText = 'left:' + (isFinite(x) ? x : 78) + '%;top:' + (isFinite(y) ? y : 70) + '%;width:' + size + '%;font-size:' + (doc.w / 100) + 'px';   // 1em = 1 % de la largeur de la page
    e.innerHTML = '<svg viewBox="0 0 ' + n + ' ' + n + '" shape-rendering="crispEdges" aria-hidden="true"><path d="' + path + '" fill="#0B1F3A"/></svg>';
    if (q.label) {
      var t = document.createElement('div');
      t.className = 'q-t'; t.textContent = q.label; t.style.fontSize = (size * 0.11) + 'em';
      e.appendChild(t);
    }
    st.appendChild(e);
  }

  // Player : les fichiers de polices sont téléchargés une fois en arrière-plan (le Service Worker les garde) pour qu'une composition s'affiche
  // avec ses polices même si l'écran perd internet avant de l'avoir jouée.
  if (w.LinkiiApps && w.fetch) setTimeout(function () {
    fetch(FONT_CSS).then(function (r) { return r.text(); }).then(function (t) {
      var re = /url(([^)]+))/g, m;
      while ((m = re.exec(t))) fetch('/player/fonts/' + m[1].replace(/['"]/g, '')).catch(function () {});
    }).catch(function () {});
  }, 8000);

  /* ---------- Player : app « composition » (pleine page, mise à l'échelle sans déformation) ---------- */
  if (w.LinkiiApps) w.LinkiiApps.register('composition', function (h) {
    var doc = null;
    try { doc = JSON.parse(h.settings.doc || ''); } catch (x) { doc = null; }
    if (!doc || !(doc.w > 0) || !(doc.h > 0)) { h.note('Composition vide — ouvrez-la dans l\'éditeur puis republiez l\'écran'); return; }
    C.ensure();
    var el = h.el, tz = h.settings.timezone || (h.cfg && h.cfg.timezone) || undefined;
    el.style.overflow = 'hidden'; el.style.background = C.bg(doc);   // pleine page (.p-full) : la zone entière ; bandes de la couleur du fond si le format diffère
    if (h.place && h.place !== 'full') {   // angle : une fenêtre de 32 % de la largeur de la scène (18 % de haut au plus), au format de la composition
      var wEm = 32, hEm = wEm * doc.h / doc.w;
      if (hEm > 18) { wEm = wEm * 18 / hEm; hEm = 18; }
      el.style.width = wEm + 'em'; el.style.height = hEm + 'em'; el.style.borderRadius = '.4em';
    }
    var st = document.createElement('div');
    st.className = 'lc-stage';
    st.style.width = doc.w + 'px'; st.style.height = doc.h + 'px'; st.style.background = C.bg(doc);
    var o = { player: true, tz: tz, data: {} };
    st.innerHTML = C.html(doc, o);
    el.appendChild(st);
    // Blocs et champs reliés : données de chaque source (gardées hors ligne par le player), redessinées à chaque lecture
    var ids = C.sources(doc);
    for (var si = 0; si < ids.length; si++) (function (id) {
      if (!h.source) return;
      var app = null;
      for (var j = 0; j < doc.els.length; j++) if (doc.els[j].type === 'app' && doc.els[j].source === id) app = doc.els[j].app;
      h.source(id, function (d) { if (!d) return; o.data[id] = d; C.refresh(st, doc, o); }, (SRC[app] || {}).refresh || 5 * 60000);
    })(ids[si]);
    addQr(st, h.item.qr, doc);
    var srcs = [];
    h.onStop(function () { for (var i = 0; i < srcs.length; i++) srcs[i].revoke(); });
    C.images(st, function (url, cb) { h.media(url).then(function (m) { srcs.push(m); cb(m.src); }); });
    // La scène fait la taille de la page (ex. 2160 × 3840) : tant qu'elle n'est pas mise à l'échelle elle déborde de l'écran. Elle reste donc
    // cachée jusqu'au premier calcul réussi, et l'échelle suit la taille de la zone (ResizeObserver) : l'élément n'a pas encore de taille à l'insertion.
    st.style.visibility = 'hidden';
    var last = '';
    function fit() {
      var W = el.clientWidth, H = el.clientHeight;
      if (!W || !H) return;
      var s = Math.min(W / doc.w, H / doc.h), key = W + 'x' + H;
      if (key === last) return;
      last = key;
      st.style.transform = 'translate(' + (W - doc.w * s) / 2 + 'px,' + (H - doc.h * s) / 2 + 'px) scale(' + s + ')';
      st.style.visibility = '';
    }
    fit();
    if (w.requestAnimationFrame) w.requestAnimationFrame(fit);
    if (w.ResizeObserver) {
      var ro = new w.ResizeObserver(fit);
      ro.observe(el);
      h.onStop(function () { ro.disconnect(); });
    }
    h.every(function () { fit(); C.tick(st, tz); }, 1000);
  });
})(window);
