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
    '.lc-wait{opacity:.55;font-size:.7em;text-align:center}' +
    // liste reliée à un jeu de données (§139) : en-tête facultatif, lignes de hauteur fixe, pages qui alternent ou défilement continu
    '.lc-list{width:100%;height:100%;display:flex;flex-direction:column;overflow:hidden;box-sizing:border-box}' +
    '.lc-lh,.lc-lr{display:grid;column-gap:.8em;align-items:center;box-sizing:border-box;padding:0 .25em;min-width:0}' +
    '.lc-lh{font-size:.55em;opacity:.65;text-transform:uppercase;letter-spacing:.06em;flex:none}.lc-lh span{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +
    '.lc-lb{position:relative;flex:1;min-height:0;overflow:hidden}.lc-pg,.lc-ls{position:absolute;left:0;right:0;top:0}' +
    '.lc-ls{animation:lc-up linear infinite}@keyframes lc-up{from{transform:translateY(0)}to{transform:translateY(-50%)}}' +
    '.lc-lr{overflow:hidden}.lc-lt{min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}' +
    '.lc-lt small{display:block;font-size:.62em;opacity:.7;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-top:.1em}' +
    '.lc-lp{justify-self:end;font-size:.72em;padding:.18em .65em;border-radius:.3em;color:#fff;white-space:nowrap;line-height:1.2}' +
    '.lc-li{height:78%;aspect-ratio:1/1;background-position:center;background-size:cover;background-repeat:no-repeat;border-radius:.2em;justify-self:start}' +
    // « Afficher si… » : un élément masqué par sa condition ; dans l'éditeur il reste visible, en pâle
    '.lc-ifoff{opacity:.28!important}' +
    // graphique relié : dessin SVG à la taille du calque ; chiffre clé en texte
    '.lc-ch{width:100%;height:100%;box-sizing:border-box;overflow:hidden}.lc-ch svg{display:block;width:100%;height:100%}' +
    '.lc-kpi{display:flex;flex-direction:column;justify-content:center;line-height:1.1}.lc-kpi .l{opacity:.75}.lc-kpi .v{font-weight:600;line-height:1.05;margin:.1em 0}.lc-kpi .v small{font-size:.35em;font-weight:400;opacity:.7}.lc-kpi .dl{font-size:.9em}.lc-kpi .dl span{opacity:.65;margin-left:.5em}';

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
    'ha-sensors': { name: 'Capteurs', icon: '🌡', logo: 'ha', color: '#41BDF5', refresh: 30000 },
    dataset: { name: 'Jeu de données', icon: '▦', color: '#5A6B80', refresh: 60000 },   // §139 : copie gardée par le serveur, relue chaque minute
    screen: { name: 'Cet écran', icon: '⌂', color: '#0B1F3A', refresh: 5 * 60000 }
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
  /* ---------- Jeux de données et « Cet écran » (§139) ----------
     Jeu : { kind: 'table', columns: [{ key, label, type }], rows: [[…]], readAt } (valeurs en texte : nombres invariants, dates ISO, true/false).
     Cet écran : { kind: 'screen', name, zone, area, params: { clé: valeur }, labels: { clé: nom } } ; filtres « @p.clé », « @name », « @zone ».
     Champs d'un jeu : « col » (1re ligne), « r2.col » (2e ligne), « _count », « sum.col », « avg.col », « min.col », « max.col ». */
  var SCREEN = '5c2ee0005c2e4e00a000000000000000';
  function colIdx(d, key) { var c = (d && d.columns) || []; for (var i = 0; i < c.length; i++) if (c[i].key === key) return i; return -1; }
  function colOf(d, key) { var i = colIdx(d, key); return i < 0 ? null : d.columns[i]; }
  /* Nombres (§142) : milliers séparés (15’000), virgule décimale, symbole monétaire facultatif. nf = { dec: 'auto' | 0..3, th: false (sans séparateur de milliers),
     cur: CHF | EUR | USD, pos: 'before' (CHF 16,90 ; sinon 16,90 CHF) } ; nd : décimales communes d'une colonne en automatique, sinon 0 pour un entier et 2 sinon. */
  var CUR = { CHF: 'CHF', EUR: '€', USD: '$' };
  function numFmt(n, nf, nd) {
    nf = nf || {};
    var d = nf.dec === 0 || nf.dec > 0 ? Math.min(3, +nf.dec) : (nd != null ? nd : (n % 1 === 0 ? 0 : 2)), s = Math.abs(n).toFixed(d), p = s.split('.');
    var i = nf.th === false ? p[0] : p[0].replace(/\B(?=(\d{3})+(?!\d))/g, '’');
    s = (n < 0 && +s !== 0 ? '-' : '') + i + (p[1] ? ',' + p[1] : '');
    var c = CUR[nf.cur];
    return c ? (nf.pos === 'before' ? c + ' ' + s : s + ' ' + c) : s;
  }
  function fmtNum(n) { return numFmt(n); }
  /** Décimales d'une colonne en automatique : 0 si toutes ses valeurs sont entières, sinon 2 pour toutes (les montants restent alignés). */
  function numDec(vs) { for (var i = 0; i < vs.length; i++) { var n = parseFloat(vs[i]); if (isFinite(n) && n % 1 !== 0) return 2; } return 0; }
  /** Format d'un champ de texte, écrit après « | » dans le jeton : « d2,t0,cCHF,pb » (décimales, sans milliers, monnaie, symbole avant). */
  function parseNf(s) {
    var nf = {};
    String(s || '').split(',').forEach(function (p) {
      var m;
      if ((m = /^d(auto|[0-3])$/.exec(p))) { if (m[1] !== 'auto') nf.dec = +m[1]; }
      else if (p === 't0') nf.th = false;
      else if ((m = /^c(CHF|EUR|USD)$/.exec(p))) nf.cur = m[1];
      else if (p === 'pb') nf.pos = 'before';
    });
    return nf;
  }
  function nfString(nf) {
    var a = [];
    if (nf && (nf.dec === 0 || nf.dec > 0)) a.push('d' + nf.dec);
    if (nf && nf.th === false) a.push('t0');
    if (nf && CUR[nf.cur]) { a.push('c' + nf.cur); if (nf.pos === 'before') a.push('pb'); }
    return a.join(',');
  }
  /** Valeur d'une cellule mise en forme selon le type de sa colonne. */
  function fmt(v, type, tz) {
    if (v == null || v === '') return '';
    var s = String(v);
    if (type === 'number') { var n = parseFloat(s); return isFinite(n) ? fmtNum(n) : s; }
    if (type === 'bool') return s === 'true' ? 'Oui' : s === 'false' ? 'Non' : s;
    if (type === 'date') {
      var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(s), ms = m ? new Date(+m[1], +m[2] - 1, +m[3]).getTime() : Date.parse(s);
      if (!isFinite(ms)) return s;
      var k = dayKey(ms, tz), today = dayKey(Date.now(), tz), day;
      try { day = new Date(ms).toLocaleDateString('fr-CH', tzo({ day: 'numeric', month: 'short' }, tz)); } catch (x) { day = k; }
      if (m) return k === today ? 'aujourd\'hui' : k === dayKey(Date.now() + 864e5, tz) ? 'demain' : day;
      return k === today ? hm(ms, tz) : (k === dayKey(Date.now() + 864e5, tz) ? 'demain' : day) + ' ' + hm(ms, tz);
    }
    return s;
  }
  function cmpVal(a, b, type) {
    if (type === 'number') { var x = parseFloat(a), y = parseFloat(b); if (isFinite(x) && isFinite(y)) return x - y; }
    if (type === 'date') { var p = Date.parse(a), q = Date.parse(b); if (isFinite(p) && isFinite(q)) return p - q; }
    return String(a == null ? '' : a).localeCompare(String(b == null ? '' : b), 'fr', { sensitivity: 'base', numeric: true });
  }
  /** Valeur de « Cet écran » citée par un filtre (« @p.etage », « @name »…) ; undefined tant que l'écran n'est pas connu. */
  function screenVal(o, ref) {
    var s = o.data && o.data[SCREEN];
    if (!s || s.error) return undefined;
    var k = String(ref).slice(1);
    return k.indexOf('p.') === 0 ? (s.params && s.params[k.slice(2)]) || '' : s[k] || '';
  }
  /** Condition d'une ligne (filtre ou règle de couleur) ; une colonne absente ne filtre pas. */
  function cond(f, d, row, o) {
    var c = colOf(d, f.key); if (!c) return true;
    var v = row[colIdx(d, f.key)], w = f.val == null ? '' : String(f.val);
    if (w.charAt(0) === '@') { var sv = screenVal(o, w); if (sv === undefined) return true; w = String(sv); }
    v = v == null ? '' : String(v).trim(); w = w.trim();
    var t = c.type === 'number' && !isFinite(parseFloat(w)) ? 'text' : c.type;
    switch (f.op) {
      case 'ne': return cmpVal(v, w, t) !== 0;
      case 'contains': return w !== '' && v.toLowerCase().indexOf(w.toLowerCase()) !== -1;
      case 'gt': return cmpVal(v, w, t) > 0;
      case 'lt': return cmpVal(v, w, t) < 0;
      case 'empty': return v === '';
      case 'notempty': return v !== '';
      default: return cmpVal(v, w, t) === 0;
    }
  }
  /**
   * « Afficher si… » : e.show = liste de conditions, toutes vraies pour afficher l'élément.
   *   { k: 'time', from: 'HH:MM', to: 'HH:MM' }  plage horaire (passe minuit si from > to)
   *   { k: 'days', days: [0..6] }                 jours de la semaine (0 = dimanche)
   *   { k: 'field', source, key, op, val }        valeur d'un champ d'un jeu (ou d'une source) : eq, ne, contains, gt, lt, empty, notempty
   *   { k: 'empty', source, not }                 le jeu n'a aucune ligne (not : il en a)
   * Plage et jours se réévaluent chaque seconde (tick) ; les champs au dessin, quand les données changent.
   */
  function nowIn(tz) {
    var d = new Date();
    try {
      var t = d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', hour12: false, timeZone: tz || undefined }).split(':'),
        wd = d.toLocaleDateString('en-US', { weekday: 'short', timeZone: tz || undefined });
      return { min: (+t[0] % 24) * 60 + (+t[1]), dow: ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'].indexOf(wd) };
    } catch (x) { return { min: d.getHours() * 60 + d.getMinutes(), dow: d.getDay() }; }
  }
  function hm(s) { var m = /^(\d{1,2}):(\d{2})$/.exec(String(s || '').trim()); return m ? Math.min(23, +m[1]) * 60 + Math.min(59, +m[2]) : null; }
  function timeCond(c, now) {
    if (c.k === 'days') return !(c.days || []).length || c.days.indexOf(now.dow) !== -1;
    var a = hm(c.from), b = hm(c.to);
    if (a == null || b == null || a === b) return true;
    return a < b ? now.min >= a && now.min < b : now.min >= a || now.min < b;
  }
  function numOf(s) { s = String(s == null ? '' : s).replace(/[’'  ]/g, '').replace(',', '.'); return s !== '' && isFinite(+s) ? +s : null; }
  function dataCond(c, o) {
    if (!c.source) return true;
    var d = o.data && o.data[c.source];
    if (c.k === 'empty') { var n = d && d.rows ? d.rows.length : 0; return c.not ? n > 0 : n === 0; }
    if (o.names) return true;
    var raw = fieldText(c.source, c.key || '', o, ''), w = String(c.val == null ? '' : c.val).trim(), v = String(raw == null ? '' : raw).trim();
    if (c.op === 'empty') return v === '';
    if (c.op === 'notempty') return v !== '';
    var nv = numOf(v), nw = numOf(w);
    if (c.op === 'contains') return w !== '' && v.toLowerCase().indexOf(w.toLowerCase()) !== -1;
    if (c.op === 'gt') return nv != null && nw != null ? nv > nw : v > w;
    if (c.op === 'lt') return nv != null && nw != null ? nv < nw : v < w;
    var eq = nv != null && nw != null ? nv === nw : v.toLowerCase() === w.toLowerCase();
    return c.op === 'ne' ? !eq : eq;
  }
  function showConds(e) { return (e.show || []).filter(function (c) { return c && (c.k === 'time' || c.k === 'days' || ((c.k === 'field' || c.k === 'empty') && c.source)); }); }
  function showOk(conds, o, tz) {
    var now = nowIn(tz);
    for (var i = 0; i < conds.length; i++) if (!(conds[i].k === 'time' || conds[i].k === 'days' ? timeCond(conds[i], now) : dataCond(conds[i], o))) return false;
    return true;
  }
  /** Lignes d'une liste : filtrées (toutes les conditions), triées. */
  function tableRows(d, e, o) {
    var rows = ((d && d.rows) || []).slice(), fl = e.filter || [];
    rows = rows.filter(function (r) { for (var i = 0; i < fl.length; i++) if (fl[i] && fl[i].key && !cond(fl[i], d, r, o)) return false; return true; });
    var so = e.sort && colOf(d, e.sort.key);
    if (so) {
      var i = colIdx(d, e.sort.key), dir = e.sort.dir === 'desc' ? -1 : 1;
      rows.sort(function (a, b) {
        var x = a[i], y = b[i];
        if (x == null || x === '') return y == null || y === '' ? 0 : 1;   // vides en dernier
        if (y == null || y === '') return -1;
        return dir * cmpVal(x, y, so.type);
      });
    }
    return rows;
  }
  /** Valeur d'un champ d'un jeu de données (null : pas de valeur). */
  function tableField(d, key, tz, nf) {
    var rows = d.rows || [], nn = tableNumber(d, key);
    if (nn != null) return nf ? numFmt(nn, nf) : fmtNum(nn);   // nombre, avec le format du champ (§142)
    var r = /^r(\d+)\.(.+)$/.exec(key), n = r ? +r[1] - 1 : 0, c = colOf(d, r ? r[2] : key);
    if (!c || !rows[n]) return null;
    return fmt(rows[n][colIdx(d, c.key)], c.type, tz);
  }
  /** Valeur numérique d'un champ d'un jeu (« _count », « sum.col », « r2.col » d'une colonne nombre…) ; null si ce n'est pas un nombre. */
  function tableNumber(d, key) {
    var rows = d.rows || [];
    if (key === '_count') return rows.length;
    var m = /^(sum|avg|min|max)\.(.+)$/.exec(key);
    if (m) {
      var i = colIdx(d, m[2]); if (i < 0) return null;
      var ns = rows.map(function (r) { return parseFloat(r[i]); }).filter(isFinite);
      if (!ns.length) return null;
      var v = m[1] === 'sum' || m[1] === 'avg' ? ns.reduce(function (a, b) { return a + b; }, 0) : m[1] === 'min' ? Math.min.apply(null, ns) : Math.max.apply(null, ns);
      return m[1] === 'avg' ? v / ns.length : v;
    }
    var r = /^r(\d+)\.(.+)$/.exec(key), n = r ? +r[1] - 1 : 0, c = colOf(d, r ? r[2] : key);
    if (!c || c.type !== 'number' || !rows[n]) return null;
    var x = parseFloat(rows[n][colIdx(d, c.key)]);
    return isFinite(x) ? x : null;
  }
  /** Nom d'un champ d'un jeu (mode « Noms des champs ») ; src : données reçues, ou la source connue de l'éditeur (cols). */
  function tableLabel(src, key) {
    var cols = (src && (src.columns || src.cols)) || [], L = function (k) { for (var i = 0; i < cols.length; i++) if (cols[i].key === k) return cols[i].label || k; return k; };
    if (key === '_count') return 'Nombre de lignes';
    var m = /^(sum|avg|min|max)\.(.+)$/.exec(key);
    if (m) return ({ sum: 'Somme', avg: 'Moyenne', min: 'Minimum', max: 'Maximum' })[m[1]] + ' · ' + L(m[2]);
    var r = /^r(\d+)\.(.+)$/.exec(key);
    return r ? L(r[2]) + ' (ligne ' + r[1] + ')' : L(key);
  }

  /** Champs d'une source : [{ key, label, value(données, fuseau) }] ; les capteurs dépendent des données reçues (une entrée par entité). */
  function fields(app, d) {
    if (app === 'dataset') {
      var cs = (d && d.columns) || [], out = [];
      cs.forEach(function (c) { out.push({ key: c.key, label: c.label || c.key, value: function (d, tz) { return tableField(d, c.key, tz); } }); });
      out.push({ key: '_count', label: 'Nombre de lignes', value: function (d) { return tableField(d, '_count'); } });
      cs.forEach(function (c) { if (c.type === 'number') out.push({ key: 'sum.' + c.key, label: 'Somme · ' + (c.label || c.key), value: function (d) { return tableField(d, 'sum.' + c.key); } }); });
      return out;
    }
    if (app === 'screen') {
      var f = [
        { key: 'name', label: 'Nom de l\'écran', value: function (d) { return d.name; } },
        { key: 'zone', label: 'Zone', value: function (d) { return d.zone; } },
        { key: 'area', label: 'Aire', value: function (d) { return d.area; } }];
      for (var pk in ((d && d.labels) || {})) (function (k) { f.push({ key: 'p.' + k, label: d.labels[k], value: function (d) { return d.params ? d.params[k] : null; } }); })(pk);
      return f;
    }
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
  var TOKEN = /\{\{([0-9a-f]{32}):([A-Za-z0-9_.:-]{1,120})(?:\|([A-Za-z0-9,]{1,30}))?\}\}/g;   // 3e groupe : format du nombre (§142), « d2,cCHF »
  function srcOf(o, id) { return (o.sources && o.sources[id]) || null; }   // { app, name } : sources connues (éditeur) ; sur l'écran, déduites des données
  /** Valeur (ou nom, mode « Noms des champs ») d'un champ relié. */
  function fieldText(id, key, o, fb, nfs) {
    var d = o.data && o.data[id], s = srcOf(o, id), app = s ? s.app : d ? ({ weather: 'weather-slide', agenda: 'agenda', rss: 'rss', table: 'dataset', screen: 'screen' })[d.kind] || (d.entities ? 'ha-sensors' : '') : '';
    if (app === 'dataset') {   // jeu de données : toutes les clés (« r2.col », « avg.col »…), pas seulement celles du menu
      if (o.names) return '{' + tableLabel(d && d.columns ? d : s, key) + '}';
      var tv = d && !d.error ? tableField(d, key, o.tz, nfs ? parseNf(nfs) : null) : null;
      return tv == null || tv === '' ? (fb || '—') : String(tv);
    }
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
      out += esc(t.slice(last, m.index)) + '<span class="lc-fd" data-f="' + m[1] + ':' + esc(m[2]) + (m[3] ? '|' + m[3] : '') + '">' + esc(fieldText(m[1], m[2], o, e.fb, m[3])) + '</span>';
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

  /* Liste reliée (§139, §140) : e = { name, source, cols: [{ key, label, as: text|small|pill|image, align, w, pre, suf, space, map, fmt, yes, no, yc, nc }], rows,
     sort: { key, dir }, filter: [{ key, op, val }], rules: [{ key, op, val, color }] (1re règle vraie : couleur des pastilles, sinon du texte de la ligne),
     page: pages|scroll|cut, sec, head, accent, line, empty }. name et label (titre de la colonne) sont propres à la liste.
     Valeurs d'une colonne : pre / suf retirent le début / la fin communs aux lignes gardées, space espace les mots collés, map remplace des mots
     ([{ a, b }]), fmt écrit les dates (auto, time, day, rel), yes / no remplacent Oui / Non et yc / nc colorent ces deux valeurs.
     Hauteur d'une ligne = hauteur du calque / nombre de lignes : la même dans l'éditeur et sur l'écran. */
  function safeUrl(u) { u = String(u || ''); return /^(https?:\/\/|\/media\/)[^"'()<>\s]+$/.test(u) ? u : ''; }
  /** Début et fin communs à des valeurs (au moins deux) : coupés sur un séparateur, la fin peut aussi commencer à une majuscule. */
  function common(vs) {
    vs = vs.map(function (v) { return v == null ? '' : String(v); }).filter(function (v) { return v !== ''; });
    if (vs.length < 2) return { pre: '', suf: '' };
    var p = vs[0], s = vs[0];
    vs.forEach(function (v) { while (p && v.indexOf(p) !== 0) p = p.slice(0, -1); while (s && v.slice(-s.length) !== s) s = s.slice(1); });
    var cut = Math.max(p.lastIndexOf('.'), p.lastIndexOf('_'), p.lastIndexOf('-'), p.lastIndexOf('/'), p.lastIndexOf('\\'), p.lastIndexOf(' '), p.lastIndexOf(':'));
    p = cut < 0 ? '' : p.slice(0, cut + 1);
    var u = s.search(/[A-Z._\-\/\\ :]/); s = u < 0 ? '' : s.slice(u);
    var short = vs.reduce(function (m, v) { return Math.min(m, v.length); }, 1e9);   // jamais une valeur vidée
    if (p.length + s.length >= short) { if (p.length >= short) p = ''; if (p.length + s.length >= short) s = ''; }
    return { pre: p, suf: s };
  }
  function relTime(ms) {
    var d = Math.round((ms - Date.now()) / 60000), a = Math.abs(d), t;
    if (a < 1) return 'maintenant';
    t = a < 60 ? a + ' min' : a < 1440 ? Math.round(a / 60) + ' h' : Math.round(a / 1440) + ' j';
    return (d > 0 ? 'dans ' : 'il y a ') + t;
  }
  /** Valeur affichée d'une cellule d'après les réglages de sa colonne ; cm : début et fin communs de la colonne (pre / suf). */
  function cellText(v, c, type, tz, cm) {
    if (type === 'bool' && (v === 'true' || v === 'false')) return v === 'true' ? (c.yes || 'Oui') : (c.no || 'Non');
    if (type === 'date' && c.fmt && c.fmt !== 'auto' && v) {
      var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(v)), ms = m ? new Date(+m[1], +m[2] - 1, +m[3]).getTime() : Date.parse(v);
      if (isFinite(ms)) {
        if (c.fmt === 'rel') return relTime(ms);
        if (c.fmt === 'time' && !m) return hm(ms, tz);
        if (c.fmt === 'dt' && !m) { try { return new Date(ms).toLocaleDateString('fr-CH', tzo({ day: 'numeric', month: 'short' }, tz)) + ' ' + hm(ms, tz); } catch (x) { return dayKey(ms, tz) + ' ' + hm(ms, tz); } }
        try { return new Date(ms).toLocaleDateString('fr-CH', tzo({ day: 'numeric', month: 'short' }, tz)); } catch (x) { return dayKey(ms, tz); }
      }
    }
    if (type === 'number' && v != null && v !== '') { var nn = parseFloat(v); if (isFinite(nn)) return numFmt(nn, c.nf, cm && cm.nd); }   // format de la colonne (§142)
    var s = fmt(v, type, tz);
    if (type !== 'text' && type !== 'link') return s;
    if (c.pre && cm && cm.pre && s.indexOf(cm.pre) === 0) s = s.slice(cm.pre.length);
    if (c.suf && cm && cm.suf && s.slice(-cm.suf.length) === cm.suf) s = s.slice(0, -cm.suf.length);
    if (c.space) s = s.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2').replace(/_/g, ' ');
    (c.map || []).forEach(function (x) { if (x && x.a) s = s.split(String(x.a)).join(String(x.b == null ? '' : x.b)); });
    return s;
  }
  function listHtml(e, o) {
    var d = o.data && o.data[e.source], s = srcOf(o, e.source), st = txs(e).replace(/text-align:[a-z]+;/, '');
    if (!d || d.error) {
      var msg = d && d.error ? 'Données indisponibles' : o.player ? '' : 'Chargement des données…';
      return '<div class="lc-app" style="' + st + '"><div class="lc-wait">▦ ' + esc(s ? s.name : 'Jeu de données') + (msg ? '<br>' + msg : '') + '</div></div>';
    }
    var cols = (e.cols || []).filter(function (c) { return c && colOf(d, c.key); });
    if (!cols.length) return '<div class="lc-app" style="' + st + '"><div class="lc-wait">Choisissez les colonnes de la liste</div></div>';
    var all = tableRows(d, e, o);
    if (!all.length) return '<div class="lc-app" style="' + st + '"><div class="lc-wait" style="font-size:.8em">' + esc(e.empty || 'Aucune ligne à afficher') + '</div></div>';
    var tbl = e.look === 'table', tot = tbl && e.total;   // présentation « Tableau » : en-tête marqué, lignes alternées, traits, ligne de total
    var n = Math.round(num(e.rows, 5, 1, 40)), headH = e.head ? num(e.size, 40, 4, 2000) * 1.1 : 0, rowH = Math.max(4, (num(e.h, 400, 10, 1e5) - headH) / (n + (tot ? 1 : 0)));
    var line = tbl ? (e.tline === false ? '' : 'border-bottom:1px solid ' + (col(e.line, '') || 'rgba(127,127,127,.4)') + ';') : col(e.line, '') ? 'border-bottom:1px solid ' + e.line + ';' : '', accent = col(e.accent, '#3B82C4');
    // colonnes de la grille : un texte prend sa part (w), une pastille ou une image sa taille ; « small » se range sous la colonne précédente
    var groups = [];
    cols.forEach(function (c) { if (c.as === 'small' && groups.length) groups[groups.length - 1].sub.push(c); else groups.push({ c: c, sub: [] }); });
    var grid = groups.map(function (g) { return g.c.as === 'pill' || g.c.as === 'image' ? 'auto' : num(g.c.w, 1, .2, 10) + 'fr'; }).join(' ');
    function rule(r) { var l = e.rules || []; for (var i = 0; i < l.length; i++) if (l[i] && l[i].key && cond(l[i], d, r, o)) return col(l[i].color, ''); return ''; }
    var cms = {};   // début et fin communs, calculés sur toutes les lignes gardées (pas seulement la page affichée)
    cols.forEach(function (c) { if ((c.pre || c.suf) && !cms[c.key]) { var i = colIdx(d, c.key); cms[c.key] = common(all.map(function (r) { return fmt(r[i], colOf(d, c.key).type, o.tz); })); } });
    cols.forEach(function (c) { if (colOf(d, c.key).type === 'number' && !cms[c.key]) { var i = colIdx(d, c.key); cms[c.key] = { nd: numDec(all.map(function (r) { return r[i]; })) }; } });   // décimales communes à toutes les lignes (§142)
    function val(c, r) { var cc = colOf(d, c.key); return cellText(r[colIdx(d, c.key)], c, cc.type, o.tz, cms[c.key]); }
    // alignement d'une colonne : le même pour son titre et ses valeurs ; une pastille sans alignement choisi reste à droite, une image à gauche (§141)
    function side(c) { return c.align ? align(c.align) : c.as === 'pill' ? 'right' : 'left'; }
    function self(c) { var a = side(c); return a === 'right' ? 'end' : a === 'center' ? 'center' : 'start'; }
    function boolCol(c, r) { var cc = colOf(d, c.key), v = r[colIdx(d, c.key)]; return cc.type === 'bool' ? col(v === 'true' ? c.yc : v === 'false' ? c.nc : '', '') : ''; }
    function row(r, ri) {
      var rc = rule(r), pill = false, h = '';
      groups.forEach(function (g) {
        var c = g.c, al = 'text-align:' + side(c), bc = boolCol(c, r);
        if (c.as === 'image') { var u = safeUrl(r[colIdx(d, c.key)]); h += '<span class="lc-li" style="justify-self:' + self(c) + (u ? ';background-image:url(\'' + esc(u) + '\')' : '') + '"></span>'; return; }
        if (c.as === 'pill') { pill = true; h += '<span class="lc-lp" style="justify-self:' + self(c) + ';background:' + (rc || bc || accent) + '">' + esc(val(c, r)) + '</span>'; return; }
        h += '<span class="lc-lt" style="' + al + (bc && !rc ? ';color:' + bc : '') + '">' + esc(val(c, r)) + g.sub.map(function (x) { return '<small style="text-align:' + align(x.align) + '">' + esc(val(x, r)) + '</small>'; }).join('') + '</span>';
      });
      return '<div class="lc-lr" style="grid-template-columns:' + grid + ';height:' + rowH + 'px;' + line + (tbl && e.zebra !== false && ri % 2 ? 'background:rgba(127,127,127,.13);' : '') + (rc && !pill ? 'color:' + rc + ';' : '') + '">' + h + '</div>';
    }
    function totalRow() {   // ligne de total : somme, moyenne ou nombre sur chaque colonne de nombres ; « Total » dans la première colonne
      var fn = e.totfn === 'avg' || e.totfn === 'count' ? e.totfn : 'sum', h = '';
      groups.forEach(function (g, gi) {
        var c = g.c, cc = colOf(d, c.key), al = 'text-align:' + side(c), t = '';
        if (!gi) t = e.totlabel != null && e.totlabel !== '' ? String(e.totlabel).slice(0, 30) : 'Total';
        else if (c.as !== 'pill' && c.as !== 'image' && cc.type === 'number') {
          var i = colIdx(d, c.key), ns = all.map(function (r) { return parseFloat(r[i]); }).filter(isFinite), sm = ns.reduce(function (a, b) { return a + b; }, 0);
          t = fn === 'count' ? String(ns.length) : ns.length ? cellText(String(fn === 'avg' ? sm / ns.length : sm), c, 'number', o.tz, cms[c.key]) : '';
        }
        h += '<span class="lc-lt" style="' + al + ';font-weight:600">' + esc(t) + '</span>';
      });
      return '<div class="lc-lr lc-tot" style="grid-template-columns:' + grid + ';height:' + rowH + 'px;border-top:2px solid currentColor">' + h + '</div>';
    }
    var head = e.head ? '<div class="lc-lh" style="' + (tbl ? 'background:rgba(127,127,127,.22);font-weight:600;opacity:1;text-transform:none;letter-spacing:0;font-size:.7em;' : '') + 'grid-template-columns:' + grid + ';height:' + headH + 'px">' + groups.map(function (g) {
      var cc = colOf(d, g.c.key); return '<span data-lh="' + (e.cols || []).indexOf(g.c) + '" style="text-align:' + side(g.c) + '">' + esc(g.c.label || cc.label || cc.key) + '</span>';
    }).join('') + '</div>' : '';
    var body, mode = e.page === 'scroll' || e.page === 'cut' ? e.page : 'pages', sec = num(e.sec, 8, 2, 120);
    if (mode === 'cut' || all.length <= n) body = '<div class="lc-pg">' + all.slice(0, n).map(row).join('') + '</div>';
    else if (mode === 'scroll') {
      var rs = all.map(row).join('');   // deux fois la liste : le défilement boucle sans saut
      body = '<div class="lc-ls" style="animation-duration:' + Math.round(all.length * sec / n * 10) / 10 + 's">' + rs + rs + '</div>';
    } else {
      var pages = Math.ceil(all.length / n), cur = Math.floor(Date.now() / (sec * 1000)) % pages;
      body = '';
      for (var p = 0; p < pages; p++) body += '<div class="lc-pg" data-pg="' + p + '"' + (p === cur ? '' : ' style="display:none"') + '>' + all.slice(p * n, p * n + n).map(row).join('') + '</div>';
      return '<div class="lc-list" style="' + st + '">' + head + '<div class="lc-lb" data-live="pages" data-n="' + pages + '" data-sec="' + sec + '">' + body + '</div>' + (tot ? totalRow() : '') + '</div>';
    }
    return '<div class="lc-list" style="' + st + '">' + head + '<div class="lc-lb">' + body + '</div>' + (tot ? totalRow() : '') + '</div>';
  }

  // QR code : tracé du serveur ; en attendant (éditeur), un motif neutre
  function qrSvg(e) {
    var fg = col(e.fg, '#000000'), parts = (e.qr || '').split('|'), n = parseInt(parts[0], 10), path = parts[1] || '';
    if (n > 0 && /^[0-9MhvzH\- ]+$/.test(path))
      return '<svg viewBox="0 0 ' + n + ' ' + n + '" shape-rendering="crispEdges"><path d="' + path + '" fill="' + fg + '"/></svg>';
    return '<svg viewBox="0 0 10 10"><rect width="10" height="10" fill="none" stroke="' + fg + '" stroke-width=".4" stroke-dasharray=".8 .6"/><text x="5" y="6" font-size="2" text-anchor="middle" fill="' + fg + '">QR</text></svg>';
  }

  // ---------- graphiques reliés à un jeu de données : barres, courbe, aire, secteurs, anneau, jauge, chiffre clé ----------
  var PALS = {
    ocean: ['#3B82C4', '#1E8E5A', '#E07B1A', '#C8372D', '#7A5CC4', '#2AA6A6'],
    warm: ['#C8372D', '#E07B1A', '#E8B730', '#9C4A8F', '#7A5CC4', '#3B82C4'],
    mono: ['#0B1F3A', '#2B5C8F', '#3B82C4', '#7FB2E0', '#B9D3EC', '#5A6B80']
  };
  var AGGN = { sum: 'Somme', avg: 'Moyenne', count: 'Nombre de lignes', min: 'Minimum', max: 'Maximum' };
  function aggOf(rows, i, agg) {
    if (agg === 'count') return rows.length;
    var ns = [];
    rows.forEach(function (r) { var n = parseFloat(r[i]); if (isFinite(n)) ns.push(n); });
    if (!ns.length) return null;
    var s = ns.reduce(function (a, b) { return a + b; }, 0);
    return agg === 'avg' ? s / ns.length : agg === 'min' ? Math.min.apply(null, ns) : agg === 'max' ? Math.max.apply(null, ns) : s;
  }
  /**
   * Ce que reçoit un graphique : lignes gardées (e.filter), regroupées par la colonne e.cat (sans colonne : un seul groupe), un calcul par série
   * (e.series : { key, agg, name, color }), ordre e.order { by: source|cat|val, dir }, e.max points dont « Autres » si e.other.
   * Rend { cats: [libellés], series: [{ name, agg, color, vals }], rows: lignes gardées }.
   */
  function chartData(d, e, o) {
    var fl = (e.filter || []).filter(function (f) { return f && f.key && (f.op === 'empty' || f.op === 'notempty' || (f.val != null && f.val !== '')); });   // une condition encore sans valeur n'écarte rien
    var rows = tableRows(d, { filter: fl }, o), ci = colIdx(d, e.cat), cc = colOf(d, e.cat);
    var ser = (e.series || []).filter(function (s) { return s && (s.agg === 'count' || colIdx(d, s.key) >= 0); });
    var groups = [], map = Object.create(null);
    if (ci >= 0) rows.forEach(function (r) {
      var raw = r[ci] == null ? '' : String(r[ci]), g = map[raw];
      if (!g) { g = map[raw] = { raw: raw, label: fmt(raw, cc.type, o && o.tz) || '—', rows: [] }; groups.push(g); }
      g.rows.push(r);
    });
    else groups.push({ raw: '', label: '', rows: rows });
    function calc(g) { g.vals = ser.map(function (s) { return aggOf(g.rows, colIdx(d, s.key), s.agg); }); }
    groups.forEach(calc);
    var od = e.order || {}, dir = od.dir === 'desc' ? -1 : 1;
    if (od.by === 'cat' && ci >= 0) groups.sort(function (a, b) { return dir * cmpVal(a.raw, b.raw, cc.type); });
    else if (od.by === 'val' && ser.length) groups.sort(function (a, b) {
      if (a.vals[0] == null) return b.vals[0] == null ? 0 : 1;
      if (b.vals[0] == null) return -1;
      return dir * (a.vals[0] - b.vals[0]);
    });
    var n = Math.round(num(e.max, 0, 0, 200));
    if (n && groups.length > n) {
      var rest = groups.slice(n);
      groups = groups.slice(0, n);
      if (e.other) { var og = { raw: '', label: 'Autres', rows: [].concat.apply([], rest.map(function (g) { return g.rows; })) }; calc(og); groups.push(og); }
    }
    return {
      cats: groups.map(function (g) { return g.label; }), rows: rows.length,
      series: ser.map(function (s, k) {
        var c = colOf(d, s.key);
        return { name: s.name || (s.agg === 'count' ? 'Nombre de lignes' : c ? c.label || s.key : s.key), agg: s.agg, color: s.color, vals: groups.map(function (g) { return g.vals[k]; }) };
      })
    };
  }
  function nice(m) { if (!(m > 0)) return 1; var p = Math.pow(10, Math.floor(Math.log10(m))), l = [1, 2, 2.5, 5, 10]; for (var i = 0; i < l.length; i++) if (l[i] * p >= m) return l[i] * p; return 10 * p; }
  function arcPath(cx, cy, r0, r1, a0, a1) {
    function p(r, a) { return (cx + r * Math.cos(a)).toFixed(2) + ',' + (cy + r * Math.sin(a)).toFixed(2); }
    var big = a1 - a0 > Math.PI ? 1 : 0;
    return 'M' + p(r1, a0) + 'A' + r1 + ',' + r1 + ' 0 ' + big + ' 1 ' + p(r1, a1) + (r0 > 0 ? 'L' + p(r0, a1) + 'A' + r0 + ',' + r0 + ' 0 ' + big + ' 0 ' + p(r0, a0) + 'Z' : 'L' + cx + ',' + cy + 'Z');
  }
  function svgT(x, y, t, fs, anchor, extra) { return '<text x="' + x.toFixed(1) + '" y="' + y.toFixed(1) + '" font-size="' + fs.toFixed(1) + '" text-anchor="' + (anchor || 'start') + '" fill="currentColor"' + (extra || '') + '>' + esc(t) + '</text>'; }
  function cut(s, n) { s = String(s); return s.length > n ? s.slice(0, n - 1) + '…' : s; }
  /** Dessin d'un graphique à axes ou en parts ; w × h : taille du calque en pixels, fs : taille du texte. */
  function chartSvg(e, cd, o, w, h, fs) {
    var K = e.kind, pie = K === 'pie' || K === 'donut', pal = PALS[e.pal] || PALS.ocean, ser = pie || K === 'hbars' ? cd.series.slice(0, 1) : cd.series, cats = cd.cats, n = cats.length, g = '';
    function sc(k) { return (ser[k] && col(ser[k].color, '')) || pal[k % pal.length]; }
    var all = []; ser.forEach(function (s) { s.vals.forEach(function (v) { if (v != null) all.push(v); }); });
    var nd = numDec(all), nf = e.nf, showV = e.vals !== false, grid = e.grid !== false;
    function F(v) { return v == null ? '' : numFmt(v, nf, nd); }
    var legend = e.legend || 'bottom', items = pie ? cats : ser.map(function (s) { return s.name; });
    var hasLeg = legend !== 'none' && (pie || ser.length > 1), lw = 0;
    items.forEach(function (t) { lw = Math.max(lw, cut(t, 22).length); });
    var legW = hasLeg && legend === 'right' ? lw * fs * .55 + fs * 2.4 : 0, legH = hasLeg && legend === 'bottom' ? fs * 2 : 0;
    var top = e.title ? fs * 2.1 : fs * .5, x0 = fs * .4, plotW = w - legW - fs * .4, plotH = h - top - legH - fs * .3;
    if (e.title) g += svgT(w / 2, fs * 1.5, e.title, fs * 1.4, 'middle', ' font-weight="600"');
    if (hasLeg) {
      var cx0 = legend === 'right' ? w - legW + fs * .4 : x0, xx = cx0, yy = legend === 'right' ? top + fs : h - fs * .5;
      items.forEach(function (t, i) {
        var col0 = pie ? pal[i % pal.length] : sc(i), tw = cut(t, 22).length * fs * .55 + fs * 2.4;
        g += '<rect x="' + xx.toFixed(1) + '" y="' + (yy - fs * .8).toFixed(1) + '" width="' + (fs * .9).toFixed(1) + '" height="' + (fs * .9).toFixed(1) + '" rx="' + (fs * .15).toFixed(1) + '" fill="' + col0 + '"/>' + svgT(xx + fs * 1.3, yy, cut(t, 22), fs * .95);
        if (legend === 'right') yy += fs * 1.7; else xx += tw;
      });
    }
    if (pie) {
      var vs = ser[0].vals.map(function (v) { return v > 0 ? v : 0; }), tot = vs.reduce(function (a, b) { return a + b; }, 0);
      if (!(tot > 0)) return null;
      var r = Math.max(10, Math.min(plotW, plotH) / 2 * .94), cx = x0 + plotW / 2, cy = top + plotH / 2, a = -Math.PI / 2, ri = K === 'donut' ? r * (1 - num(e.thick, 38, 10, 85) / 100) : 0;
      vs.forEach(function (v, i) {
        if (!v) return;
        var b = a + v / tot * Math.PI * 2, full = v / tot > .9999;
        g += '<path d="' + arcPath(cx, cy, ri, r, a, full ? a + Math.PI * 2 - .0001 : b - (vs.length > 1 ? .012 : 0)) + '" fill="' + pal[i % pal.length] + '"/>';
        if (showV && v / tot >= .04) {
          var m = (a + b) / 2, rr = ri ? (r + ri) / 2 : r * .62;
          g += svgT(cx + rr * Math.cos(m), cy + rr * Math.sin(m) + fs * .3, Math.round(v / tot * 100) + ' %', fs * .9, 'middle', ' font-weight="600" style="fill:#fff;stroke:rgba(0,0,0,.35);stroke-width:' + (fs * .12).toFixed(1) + 'px;paint-order:stroke"');
        }
        a = b;
      });
      if (K === 'donut') g += svgT(cx, cy + fs * .5, F(tot) + (e.unit ? ' ' + e.unit : ''), Math.min(ri * .42, fs * 2.4), 'middle', ' font-weight="600"') + svgT(cx, cy + fs * 1.7 + Math.min(ri * .12, fs * .6), ser[0].name, fs * .85, 'middle', ' opacity=".7"');
      return g;
    }
    var mnv = all.length ? Math.min.apply(null, all) : 0, hi = nice(all.length ? Math.max.apply(null, all.concat([0])) : 1), lo = mnv < 0 ? -nice(-mnv) : 0, span = hi - lo || 1;
    if (K === 'hbars') {
      var lab = 0; cats.forEach(function (t) { lab = Math.max(lab, cut(t, 18).length); });
      var lx = x0 + Math.min(plotW * .35, lab * fs * .56 + fs * .8), ex = fs * (String(F(hi)).length * .6 + .8), px = lx, pw = plotW - (lx - x0) - (showV ? ex : fs * .3), gh = plotH / Math.max(1, n);
      if (grid) for (var i = 0; i <= 4; i++) g += '<line x1="' + (px + pw * i / 4).toFixed(1) + '" x2="' + (px + pw * i / 4).toFixed(1) + '" y1="' + top.toFixed(1) + '" y2="' + (top + plotH).toFixed(1) + '" stroke="currentColor" stroke-opacity=".15"/>';
      cats.forEach(function (c, j) {
        var v = ser[0].vals[j] || 0, bw = Math.max(0, pw * Math.max(v, 0) / span), y = top + gh * j + gh * .16;
        g += svgT(lx - fs * .5, top + gh * (j + .5) + fs * .32, cut(c, 18), fs * .95, 'end') + '<rect x="' + px.toFixed(1) + '" y="' + y.toFixed(1) + '" width="' + bw.toFixed(1) + '" height="' + (gh * .68).toFixed(1) + '" rx="' + Math.min(4, gh * .1).toFixed(1) + '" fill="' + sc(0) + '"/>';
        if (showV) g += svgT(px + bw + fs * .4, top + gh * (j + .5) + fs * .3, F(ser[0].vals[j]), fs * .85);
      });
      return g;
    }
    var tl = 0; for (var t = 0; t <= 4; t++) tl = Math.max(tl, String(F(lo + span * t / 4)).length);
    var L = x0 + tl * fs * .58 + fs * .7, bot = fs * 1.9, pw2 = plotW - (L - x0) - fs * .3, ph = plotH - bot;
    function Y(v) { return top + ph - ph * ((v == null ? 0 : v) - lo) / span; }
    for (var q = 0; q <= 4; q++) {
      var yv = top + ph - ph * q / 4;
      if (grid) g += '<line x1="' + L.toFixed(1) + '" x2="' + (L + pw2).toFixed(1) + '" y1="' + yv.toFixed(1) + '" y2="' + yv.toFixed(1) + '" stroke="currentColor" stroke-opacity=".15"/>';
      g += svgT(L - fs * .5, yv + fs * .3, F(lo + span * q / 4), fs * .8, 'end', ' opacity=".7"');
    }
    var gw = pw2 / Math.max(1, n), step = Math.max(1, Math.ceil((Math.min(12, Math.max.apply(null, cats.map(function (c) { return String(c).length; }).concat([1]))) * fs * .56) / gw));
    cats.forEach(function (c, j) { if (j % step === 0) g += svgT(L + gw * (j + .5), top + ph + fs * 1.4, cut(c, 12), fs * .9, 'middle', ' opacity=".8"'); });
    if (lo < 0) g += '<line x1="' + L.toFixed(1) + '" x2="' + (L + pw2).toFixed(1) + '" y1="' + Y(0).toFixed(1) + '" y2="' + Y(0).toFixed(1) + '" stroke="currentColor" stroke-opacity=".5"/>';
    if (K === 'bars') {
      var bw2 = Math.min(fs * 5, gw * .72 / ser.length);
      ser.forEach(function (s, k) {
        s.vals.forEach(function (v, j) {
          var x = L + gw * (j + .5) - bw2 * ser.length / 2 + k * bw2, y1 = Y(v), y0 = Y(0);
          g += '<rect x="' + x.toFixed(1) + '" y="' + Math.min(y0, y1).toFixed(1) + '" width="' + Math.max(1, bw2 - 3).toFixed(1) + '" height="' + Math.abs(y0 - y1).toFixed(1) + '" rx="' + Math.min(4, bw2 / 6).toFixed(1) + '" fill="' + sc(k) + '"/>';
          if (showV && v != null && F(v).length * fs * .5 <= bw2 + gw * .25) g += svgT(x + (bw2 - 3) / 2, v < 0 ? y1 + fs : y1 - fs * .35, F(v), fs * .8, 'middle');
        });
      });
    } else {
      var area = K === 'area', sm = !!e.smooth;
      ser.forEach(function (s, k) {
        var P = s.vals.map(function (v, j) { return [L + gw * (j + .5), Y(v)]; }), d = '';
        P.forEach(function (p, j) {
          if (!j) { d = 'M' + p[0].toFixed(1) + ',' + p[1].toFixed(1); return; }
          var q0 = P[j - 1], mx = (q0[0] + p[0]) / 2;
          d += sm ? 'C' + mx.toFixed(1) + ',' + q0[1].toFixed(1) + ' ' + mx.toFixed(1) + ',' + p[1].toFixed(1) + ' ' + p[0].toFixed(1) + ',' + p[1].toFixed(1) : 'L' + p[0].toFixed(1) + ',' + p[1].toFixed(1);
        });
        if (area && P.length) g += '<path d="' + d + 'L' + P[P.length - 1][0].toFixed(1) + ',' + Y(0).toFixed(1) + 'L' + P[0][0].toFixed(1) + ',' + Y(0).toFixed(1) + 'Z" fill="' + sc(k) + '" fill-opacity=".28"/>';
        g += '<path d="' + d + '" fill="none" stroke="' + sc(k) + '" stroke-width="' + Math.max(2, fs * .17).toFixed(1) + '" stroke-linejoin="round" stroke-linecap="round"/>';
        P.forEach(function (p, j) {
          g += '<circle cx="' + p[0].toFixed(1) + '" cy="' + p[1].toFixed(1) + '" r="' + Math.max(3, fs * .24).toFixed(1) + '" fill="' + sc(k) + '"/>';
          if (showV && s.vals[j] != null && gw >= F(s.vals[j]).length * fs * .5) g += svgT(p[0], p[1] - fs * .7, F(s.vals[j]), fs * .8, 'middle');
        });
      });
    }
    return g;
  }
  /** Jauge : demi-cercle gradué entre gmin et gmax, coloré selon les seuils (en % de l'échelle). */
  function gaugeSvg(e, v, name, w, h, fs, F) {
    var mn = num(e.gmin, 0, -1e9, 1e9), mx = num(e.gmax, 100, -1e9, 1e9); if (mx <= mn) mx = mn + 1;
    var f = Math.min(1, Math.max(0, ((v == null ? mn : v) - mn) / (mx - mn))), thr = e.thr || [50, 80], on = e.thrOn !== false;
    var color = !on ? (PALS[e.pal] || PALS.ocean)[0] : thrCol(e, f * 100);
    var cy = h * .68, r = Math.max(20, Math.min(w / 2 - fs * 1.5, cy - fs * .5)), cx = w / 2, a1 = Math.PI + f * Math.PI;
    var g = '<path d="' + arcPath(cx, cy, r * .78, r, Math.PI, 2 * Math.PI) + '" fill="currentColor" fill-opacity=".14"/>';
    if (f > 0) g += '<path d="' + arcPath(cx, cy, r * .78, r, Math.PI, f >= 1 ? 2 * Math.PI - .0001 : a1) + '" fill="' + color + '"/>';
    g += svgT(cx, cy - r * .08, v == null ? '—' : F(v) + (e.unit ? ' ' + e.unit : ''), r * .42, 'middle', ' font-weight="600"');
    if (name) g += svgT(cx, cy + fs * 1.6, name, fs, 'middle', ' opacity=".75"');
    g += svgT(cx - r * .89, cy + fs * 1.1, F(mn), fs * .8, 'middle', ' opacity=".6"') + svgT(cx + r * .89, cy + fs * 1.1, F(mx), fs * .8, 'middle', ' opacity=".6"');
    return g;
  }
  /** Couleur selon les seuils e.thr (deux valeurs) : vert / orange / rouge, inversés si e.inv (« plus bas = mieux »). */
  function thrCol(e, x) {
    var thr = e.thr || [50, 80], a = num(thr[0], 50, -1e9, 1e9), b = num(thr[1], 80, -1e9, 1e9), lo = col(e.cLow, '#C8372D'), mi = col(e.cMid, '#E0A030'), hi = col(e.cHigh, '#1E8E5A');
    if (e.inv) return x <= a ? hi : x <= b ? mi : lo;
    return x < a ? lo : x < b ? mi : hi;
  }
  /** Tracé d'une mini-courbe (sans axes) dans une boîte x, y, w, h ; vals peut contenir des null. */
  function sparkPath(vals, x, y, w, h) {
    var ns = vals.filter(function (v) { return v != null; }); if (ns.length < 2) return null;
    var mx = Math.max.apply(null, ns), mn = Math.min.apply(null, ns), n = vals.length, P = [];
    vals.forEach(function (v, i) { if (v != null) P.push([x + w * i / (n - 1), y + h - h * (v - mn) / ((mx - mn) || 1)]); });
    return { d: 'M' + P.map(function (p) { return p[0].toFixed(1) + ',' + p[1].toFixed(1); }).join('L'), last: P[P.length - 1], x: x, y: y, w: w, h: h };
  }
  /** Jauge circulaire : anneau rempli entre gmin et gmax, valeur au centre. */
  function ringSvg(e, v, name, w, h, fs, F) {
    var mn = num(e.gmin, 0, -1e9, 1e9), mx = num(e.gmax, 100, -1e9, 1e9); if (mx <= mn) mx = mn + 1;
    var f = Math.min(1, Math.max(0, ((v == null ? mn : v) - mn) / (mx - mn))), on = e.thrOn !== false, color = !on ? (PALS[e.pal] || PALS.ocean)[0] : thrCol(e, f * 100);
    var lab = name ? fs * 1.8 : 0, cx = w / 2, cy = (h - lab) / 2, r = Math.max(20, Math.min(w / 2 - fs * .5, cy - fs * .3)), ri = r * (1 - num(e.thick, 24, 8, 60) / 100);
    var g = '<path d="' + arcPath(cx, cy, ri, r, -Math.PI / 2, 1.5 * Math.PI - .0001) + '" fill="currentColor" fill-opacity=".14"/>';
    if (f > 0) g += '<path d="' + arcPath(cx, cy, ri, r, -Math.PI / 2, f >= 1 ? 1.5 * Math.PI - .0002 : -Math.PI / 2 + f * 2 * Math.PI) + '" fill="' + color + '"/>';
    g += svgT(cx, cy + ri * .22, v == null ? '—' : F(v) + (e.unit ? ' ' + e.unit : ''), ri * .6, 'middle', ' font-weight="600"');
    if (name) g += svgT(cx, h - fs * .5, name, fs, 'middle', ' opacity=".75"');
    return g;
  }
  /** Barre de progression face à un objectif (e.target, sinon gmax). */
  function progressSvg(e, v, name, w, h, fs, F) {
    var goal = +e.target > 0 ? +e.target : num(e.gmax, 100, 1, 1e12), f = v == null ? 0 : Math.max(0, v / goal), on = e.thrOn !== false, color = !on ? (PALS[e.pal] || PALS.ocean)[0] : thrCol(e, f * 100);
    var bh = Math.max(10, fs * 1.3), by = h / 2 - bh / 2 + fs * .2, pad = fs * .6, bw = w - pad * 2, u = e.unit ? ' ' + e.unit : '';
    var g = svgT(pad, by - fs * .9, name, fs * 1.1) + svgT(w - pad, by - fs * .9, (v == null ? '—' : F(v) + u) + ' / ' + F(goal) + u, fs * 1.1, 'end', ' opacity=".8"');
    g += '<rect x="' + pad + '" y="' + by.toFixed(1) + '" width="' + bw.toFixed(1) + '" height="' + bh.toFixed(1) + '" rx="' + (bh / 2).toFixed(1) + '" fill="currentColor" fill-opacity=".14"/>';
    if (f > 0) g += '<rect x="' + pad + '" y="' + by.toFixed(1) + '" width="' + Math.max(bh, bw * Math.min(f, 1)).toFixed(1) + '" height="' + bh.toFixed(1) + '" rx="' + (bh / 2).toFixed(1) + '" fill="' + color + '"/>';
    if (v != null) g += svgT(pad, by + bh + fs * 1.5, Math.round(f * 100) + ' % de l\'objectif' + (f < 1 ? ' · il reste ' + F(goal - v) + u : ''), fs * .9, 'start', ' opacity=".75"');
    return g;
  }
  /** Pastille de statut : couleur et mot selon deux seuils sur la valeur (e.rules : trois textes). */
  function statusSvg(e, v, name, w, h, fs, F) {
    var thr = e.thr || [10, 15], a = num(thr[0], 10, -1e9, 1e9), b = num(thr[1], 15, -1e9, 1e9), rl = e.rules || ['Normal', 'Attention', 'Critique'], rank, c;
    if (v == null) { c = '#8A97A8'; rank = -1; } else { c = thrCol(e, v); rank = e.inv ? (v <= a ? 0 : v <= b ? 1 : 2) : (v < a ? 2 : v < b ? 1 : 0); }
    var r = Math.min(h * .3, w * .12), cx = r * 1.6, cy = h / 2, tx = cx + r * 1.7;
    return '<circle cx="' + cx.toFixed(1) + '" cy="' + cy.toFixed(1) + '" r="' + (r * 1.45).toFixed(1) + '" fill="' + c + '" fill-opacity=".22"/><circle cx="' + cx.toFixed(1) + '" cy="' + cy.toFixed(1) + '" r="' + r.toFixed(1) + '" fill="' + c + '"/>'
      + (name ? svgT(tx, cy - fs * .5, name, fs, 'start', ' opacity=".75"') : '') + svgT(tx, cy + fs * 1.5, rank < 0 ? '—' : rl[rank] || '', fs * 2.2, 'start', ' font-weight="600"')
      + (v == null ? '' : svgT(w - fs * .6, cy + fs * 1.5, F(v) + (e.unit ? ' ' + e.unit : ''), fs * 1.6, 'end', ' opacity=".75"'));
  }
  /** Mini-courbe : la série de la 1re valeur, sans axes, dernière valeur en haut à droite. */
  function trendSvg(e, cd, name, w, h, fs, F) {
    var vals = cd.series[0].vals, last = null; vals.forEach(function (v) { if (v != null) last = v; });
    var top = fs * 2.4, sp = sparkPath(vals, fs * .6, top + fs * .5, w - fs * 1.2, h - top - fs * 1.2), g = svgT(fs * .6, fs * 1.6, name, fs, 'start', ' opacity=".75"') + (last == null ? '' : svgT(w - fs * .6, fs * 1.9, F(last) + (e.unit ? ' ' + e.unit : ''), fs * 1.7, 'end', ' font-weight="600"'));
    if (!sp) return g;
    var c = (PALS[e.pal] || PALS.ocean)[0];
    return g + (e.fillArea !== false ? '<path d="' + sp.d + 'L' + (sp.x + sp.w) + ',' + (sp.y + sp.h) + 'L' + sp.x + ',' + (sp.y + sp.h) + 'Z" fill="' + c + '" fill-opacity=".25"/>' : '') + '<path d="' + sp.d + '" fill="none" stroke="' + c + '" stroke-width="' + Math.max(2, fs * .17).toFixed(1) + '" stroke-linejoin="round" stroke-linecap="round"/><circle cx="' + sp.last[0].toFixed(1) + '" cy="' + sp.last[1].toFixed(1) + '" r="' + Math.max(3, fs * .26).toFixed(1) + '" fill="' + c + '"/>';
  }
  /** Valeur d'une jauge ou d'un chiffre clé : calcul de la 1re série ; avec une colonne de regroupement, la dernière valeur (et l'avant-dernière pour comparer). */
  function singleValue(d, e, o) {
    var cd = chartData(d, e, o), s = cd.series[0];
    if (!s) return null;
    var n = s.vals.length;
    return { v: n ? s.vals[n - 1] : null, prev: n > 1 ? s.vals[n - 2] : null, name: s.name, cd: cd };
  }
  function chartHtml(e, o) {
    var d = o.data && o.data[e.source], s = srcOf(o, e.source), st = txs(e).replace(/text-align:[a-z]+;/, ''), wait = function (m) { return '<div class="lc-app" style="' + st + '"><div class="lc-wait">' + m + '</div></div>'; };
    if (!d || d.error) return wait('▦ ' + esc(s ? s.name : 'Jeu de données') + (d && d.error ? '<br>Données indisponibles' : o.player ? '' : '<br>Chargement des données…'));
    var W = num(e.w, 600, 10, 1e5), H = num(e.h, 400, 10, 1e5), fs = num(e.size, 26, 8, 400), K = e.kind;
    if (!(e.series || []).length) return wait('Choisissez les données du graphique');
    if (K === 'gauge' || K === 'kpi' || K === 'ring' || K === 'progress' || K === 'status') {
      var sv = singleValue(d, e, o); if (!sv) return wait('Choisissez les données du graphique');
      var all = []; sv.cd.series[0].vals.forEach(function (x) { if (x != null) all.push(x); });
      var F = function (v) { return numFmt(v, e.nf, numDec(all)); }, name = e.title || sv.name;
      var one = { gauge: gaugeSvg, ring: ringSvg, progress: progressSvg, status: statusSvg }[K];
      if (one) return '<div class="lc-ch" style="' + st + '"><svg viewBox="0 0 ' + W + ' ' + H + '" font-family="inherit">' + one(e, sv.v, name, W, H, fs, F) + '</svg></div>';
      var dl = '';
      if (e.cmp === 'prev' && sv.v != null && sv.prev) { var pc = (sv.v - sv.prev) / Math.abs(sv.prev) * 100; dl = '<div class="dl" style="color:' + (pc > 0 ? '#1E8E5A' : pc < 0 ? '#C8372D' : 'inherit') + '">' + (pc > 0 ? '▲ ' : pc < 0 ? '▼ ' : '') + numFmt(Math.abs(pc), { dec: 1 }) + ' %<span>par rapport à la période précédente</span></div>'; }
      else if (e.cmp === 'target' && sv.v != null && +e.target) dl = '<div class="dl">' + Math.round(sv.v / e.target * 100) + ' %<span>de l\'objectif · ' + esc(numFmt(+e.target, e.nf)) + '</span></div>';
      var sk = e.spark && sv.cd.cats.length > 1 ? sparkPath(sv.cd.series[0].vals, 2, 4, 96, 32) : null, sc0 = (PALS[e.pal] || PALS.ocean)[0];
      if (sk) dl += '<svg viewBox="0 0 100 40" preserveAspectRatio="none" style="position:absolute;right:5%;bottom:14%;width:38%;height:38%;overflow:visible"><path d="' + sk.d + 'L98,36L2,36Z" fill="' + sc0 + '" fill-opacity=".25"/><path d="' + sk.d + '" fill="none" stroke="' + sc0 + '" stroke-width="3" vector-effect="non-scaling-stroke" stroke-linejoin="round"/></svg>';
      return '<div class="lc-ch lc-kpi" style="position:relative;' + st + '"><div class="l">' + esc(name) + '</div><div class="v" style="font-size:' + (fs * 2.6) + 'px">' + (sv.v == null ? '—' : esc(F(sv.v)) + (e.unit ? '<small> ' + esc(e.unit) + '</small>' : '')) + '</div>' + dl + '</div>';
    }
    var cd = chartData(d, e, o);
    if (!cd.series.length) return wait('Choisissez les données du graphique');
    if (!cd.cats.length) return wait(esc(e.empty || 'Aucune ligne à afficher'));
    var body;
    if (K === 'trend') { var tall = []; cd.series[0].vals.forEach(function (x) { if (x != null) tall.push(x); }); body = trendSvg(e, cd, e.title || cd.series[0].name, W, H, fs, function (v) { return numFmt(v, e.nf, numDec(tall)); }); }
    else body = chartSvg(e, cd, o, W, H, fs);
    if (body == null) return wait('Aucune valeur à répartir');
    return '<div class="lc-ch" style="' + st + '"><svg viewBox="0 0 ' + W + ' ' + H + '" font-family="inherit">' + body + '</svg></div>';
  }

  function inner(e, o) {
    var t = e.type;
    if (t === 'text') return '<div class="lc-tx" style="' + txs(e) + '">' + textHtml(e, o) + '</div>';
    if (t === 'app') return block(e, o);
    if (t === 'list') return listHtml(e, o);
    if (t === 'chart') return chartHtml(e, o);
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
    if (t === 'icon') {   // icône de la bibliothèque : trait (fin, normal, épais) ou plein, couleur du calque
      var ic = ICON_BY[e.icon], sw = { fine: 1.2, thick: 2.6 }[e.stroke] || 1.8;
      if (!ic) return o.player ? '' : '<div class="lc-img empty">Icône à choisir</div>';
      return '<svg viewBox="0 0 24 24" width="100%" height="100%" preserveAspectRatio="xMidYMid meet" fill="' + (e.fill ? 'currentColor' : 'none') + '" stroke="currentColor" stroke-width="' + sw + '" stroke-linecap="round" stroke-linejoin="round" style="display:block;color:' + col(e.color, '#ffffff') + '">' + ic[3] + '</svg>';
    }
    if (t === 'video') {   // vidéo de la médiathèque (MP4 sans son, convertie à l'import) : toujours muette ; début et fin facultatifs
      var vu = e.src || (o.mediaUrl ? o.mediaUrl(e.media) : null), vr = 'border-radius:' + num(e.radius, 0, 0, 4000) + 'px;';
      if (!vu) return o.player ? '' : '<div class="lc-img empty" style="' + vr + '">Vidéo à choisir</div>';
      return '<video class="lc-vid" data-vsrc="' + esc(vu) + '" data-vs="' + num(e.vstart, 0, 0, 86400) + '" data-ve="' + num(e.vend, 0, 0, 86400) + '" data-vloop="' + (e.loop === false ? 0 : 1) + '" muted playsinline preload="auto" style="width:100%;height:100%;display:block;' + vr + 'object-fit:' + (e.fit === 'contain' ? 'contain' : 'cover') + '"></video>';
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

  /** Bibliothèque d'icônes (tracés sur une grille de 24, trait fin arrondi) : [identifiant, catégorie, nom, tracé]. Seuls ces tracés sont dessinés : un identifiant inconnu ne donne rien. */
  var ICONS = (function () {
    function P(d) { return '<path d="' + d + '"/>'; }
    function Ci(x, y, r) { return '<circle cx="' + x + '" cy="' + y + '" r="' + r + '"/>'; }
    return [
      ['fleche-droite', 'Flèches', 'Flèche vers la droite', P('M4 12h16M14 6l6 6-6 6')], ['fleche-gauche', 'Flèches', 'Flèche vers la gauche', P('M20 12H4M10 6l-6 6 6 6')], ['fleche-haut', 'Flèches', 'Flèche vers le haut', P('M12 20V4M6 10l6-6 6 6')],
      ['fleche-bas', 'Flèches', 'Flèche vers le bas', P('M12 4v16M6 14l6 6 6-6')], ['chevron', 'Flèches', 'Chevron', P('M9 5l7 7-7 7')], ['fleche-diagonale', 'Flèches', 'Flèche en diagonale', P('M7 17L17 7M8 7h9v9')], ['tourner', 'Flèches', 'Tourner à droite', P('M4 20v-6a4 4 0 0 1 4-4h12M16 6l4 4-4 4')],
      ['wifi', 'Services', 'Wi-Fi', P('M2 9a15 15 0 0 1 20 0M5 12.5a10 10 0 0 1 14 0M8.5 16a5 5 0 0 1 7 0') + Ci(12, 19.5, 1)], ['cafe', 'Services', 'Café', P('M4 8h13v6a5 5 0 0 1-5 5H9a5 5 0 0 1-5-5zM17 10h2a2 2 0 0 1 0 4h-2M8 3v2M12 3v2')],
      ['toilettes', 'Services', 'Toilettes', Ci(8, 5, 2) + Ci(16, 5, 2) + P('M8 8v10M5 12h6M16 8l-3 10h6z')], ['ascenseur', 'Services', 'Ascenseur', '<rect x="5" y="3" width="14" height="18" rx="2"/>' + P('M9 9l3-3 3 3M9 15l3 3 3-3')],
      ['parking', 'Services', 'Parking', '<rect x="4" y="3" width="16" height="18" rx="3"/>' + P('M10 17V8h3a3 3 0 0 1 0 6h-3')], ['accessibilite', 'Services', 'Accessibilité', Ci(12, 4.5, 1.8) + P('M12 8v6l4 6M7 11h10M9 20a5 5 0 0 1 0-8')],
      ['telephone', 'Services', 'Téléphone', P('M5 4h4l2 5-2.5 1.5a11 11 0 0 0 5 5L15 13l5 2v4a2 2 0 0 1-2 2A15 15 0 0 1 3 6a2 2 0 0 1 2-2')], ['escalier', 'Services', 'Escalier', P('M3 20h5v-4h5v-4h5V8h3')],
      ['sortie', 'Services', 'Sortie de secours', P('M14 4h6v16h-6M4 12h10M10 8l4 4-4 4')], ['restaurant', 'Services', 'Restaurant', P('M7 3v8a2 2 0 0 0 2 2v8M5 3v5M9 3v5M17 21V3c-2 1-3 4-3 8h3')],
      ['accueil', 'Services', 'Accueil', P('M18 16v-5a6 6 0 0 0-12 0v5l-2 2h16zM10 21h4')], ['sante', 'Services', 'Santé', P('M9 3h6v6h6v6h-6v6H9v-6H3V9h6z')],
      ['bus', 'Lieux et transports', 'Bus', '<rect x="4" y="4" width="16" height="13" rx="2"/>' + P('M4 11h16M7 20v-3M17 20v-3M8 14h.01M16 14h.01')], ['train', 'Lieux et transports', 'Train', '<rect x="5" y="3" width="14" height="15" rx="3"/>' + P('M5 11h14M8 21l2-3M16 21l-2-3M9 14.5h.01M15 14.5h.01')],
      ['velo', 'Lieux et transports', 'Vélo', Ci(6, 17, 3) + Ci(18, 17, 3) + P('M6 17l4-8h5l3 8M10 9L8 6H6M13 17l2-8')], ['voiture', 'Lieux et transports', 'Voiture', P('M5 16h14l-1.5-6a2 2 0 0 0-2-1.5h-7a2 2 0 0 0-2 1.5zM5 16v3M19 16v3M8 13h.01M16 13h.01')],
      ['avion', 'Lieux et transports', 'Avion', P('M10 14L3 17l1-2 6-3V6a2 2 0 0 1 4 0v6l6 3 1 2-7-3v4l2 2v1l-3-1-3 1v-1l2-2z')], ['batiment', 'Lieux et transports', 'Bâtiment', P('M4 21V4a1 1 0 0 1 1-1h9a1 1 0 0 1 1 1v17M15 9h4a1 1 0 0 1 1 1v11M3 21h18M8 7h3M8 11h3M8 15h3')],
      ['maison', 'Lieux et transports', 'Maison', P('M3 11l9-8 9 8M5 10v10h14V10M10 20v-6h4v6')], ['position', 'Lieux et transports', 'Position', P('M12 21s-7-6-7-11a7 7 0 0 1 14 0c0 5-7 11-7 11z') + Ci(12, 10, 2)], ['carte', 'Lieux et transports', 'Plan', P('M9 4L3 6v14l6-2 6 2 6-2V4l-6 2zM9 4v14M15 6v14')],
      ['soleil', 'Météo', 'Soleil', Ci(12, 12, 4) + P('M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4')], ['nuage', 'Météo', 'Nuage', P('M7 18a4 4 0 0 1-.5-8A6 6 0 0 1 18 11a3.5 3.5 0 0 1-.5 7z')],
      ['pluie', 'Météo', 'Pluie', P('M7 14a4 4 0 0 1-.5-8A6 6 0 0 1 18 7a3.5 3.5 0 0 1-.5 7zM8 17l-1 3M12 17l-1 3M16 17l-1 3')], ['neige', 'Météo', 'Neige', P('M12 3v18M4.2 7.5l15.6 9M4.2 16.5l15.6-9')],
      ['vent', 'Météo', 'Vent', P('M3 8h10a3 3 0 1 0-3-3M3 12h15a3 3 0 1 1-3 3M3 16h7')], ['orage', 'Météo', 'Orage', P('M7 14a4 4 0 0 1-.5-8A6 6 0 0 1 18 7a3.5 3.5 0 0 1-.5 7M12 12l-2 4h4l-2 4')],
      ['lune', 'Météo', 'Lune', P('M20 14A8 8 0 0 1 10 4a8 8 0 1 0 10 10z')], ['thermometre', 'Météo', 'Température', P('M14 14.8V5a2 2 0 0 0-4 0v9.8a4 4 0 1 0 4 0z')],
      ['information', 'Alertes', 'Information', Ci(12, 12, 9) + P('M12 11v6M12 7.5v.5')], ['attention', 'Alertes', 'Attention', P('M12 3l10 18H2zM12 10v5M12 18v.5')], ['interdit', 'Alertes', 'Interdit', Ci(12, 12, 9) + P('M5.6 5.6l12.8 12.8')],
      ['valide', 'Alertes', 'Validé', Ci(12, 12, 9) + P('M8 12.5l3 3 5-6')], ['danger', 'Alertes', 'Danger', P('M8 3h8l5 5v8l-5 5H8l-5-5V8zM12 8v5M12 16v.5')], ['question', 'Alertes', 'Question', Ci(12, 12, 9) + P('M9.5 9.5a2.5 2.5 0 1 1 3.5 2.3c-.7.4-1 1-1 1.7M12 17v.5')],
      ['coche', 'Alertes', 'Coche', P('M5 12.5l4.5 4.5L19 7')], ['croix', 'Alertes', 'Croix', P('M6 6l12 12M18 6L6 18')], ['feu', 'Alertes', 'Feu', P('M12 3c1 4 5 5 5 10a5 5 0 0 1-10 0c0-2 1-3 2-4 0 2 1 3 2 3 0-3-1-5 1-9z')],
      ['horloge', 'Divers', 'Horloge', Ci(12, 12, 9) + P('M12 7v5l3 2')], ['calendrier', 'Divers', 'Calendrier', '<rect x="3" y="5" width="18" height="16" rx="2"/>' + P('M3 10h18M8 3v4M16 3v4')], ['personne', 'Divers', 'Personne', Ci(12, 8, 4) + P('M4 21a8 8 0 0 1 16 0')],
      ['coeur', 'Divers', 'Cœur', P('M12 20s-8-5-8-11a4.5 4.5 0 0 1 8-2.5A4.5 4.5 0 0 1 20 9c0 6-8 11-8 11z')], ['etoile', 'Divers', 'Étoile', P('M12 3l2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1L3.2 9.5l6.1-.9z')],
      ['courrier', 'Divers', 'Courrier', '<rect x="3" y="6" width="18" height="12" rx="1"/>' + P('M3 7l9 7 9-7')], ['cle', 'Divers', 'Clé', Ci(8, 15, 4) + P('M11 12l8-8M16 7l2 2M14 9l2 2')], ['cadenas', 'Divers', 'Cadenas', '<rect x="5" y="11" width="14" height="10" rx="1"/>' + P('M8 11V8a4 4 0 0 1 8 0v3')],
      ['appareil-photo', 'Divers', 'Appareil photo', P('M4 8h3l2-3h6l2 3h3v11H4z') + Ci(12, 13.5, 3.5)], ['imprimante', 'Divers', 'Imprimante', P('M7 9V3h10v6M7 17H4v-7h16v7h-3') + '<rect x="7" y="14" width="10" height="7"/>'],
      ['ampoule', 'Divers', 'Idée', P('M9 18h6M10 21h4M12 3a6 6 0 0 0-4 10.5c.7.7 1 1.5 1 2.5h6c0-1 .3-1.8 1-2.5A6 6 0 0 0 12 3z')], ['drapeau', 'Divers', 'Drapeau', P('M5 21V4M5 5h13l-2 4 2 4H5')],
      ['panier', 'Divers', 'Panier', P('M3 4h2l2 12h11l2-8H6') + Ci(9, 20, .6) + Ci(17, 20, .6)], ['trophee', 'Divers', 'Trophée', P('M8 4h8v5a4 4 0 0 1-8 0zM8 6H4v1a3 3 0 0 0 3 3M16 6h4v1a3 3 0 0 1-3 3M12 13v4M8 21h8M10 17h4')],
      ['recherche', 'Divers', 'Recherche', Ci(11, 11, 7) + P('M21 21l-5-5')], ['reglages', 'Divers', 'Réglages', Ci(12, 12, 3) + P('M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M5.6 18.4l2.1-2.1M16.3 7.7l2.1-2.1')],
      ['euro', 'Divers', 'Euro', P('M17 6a7 7 0 1 0 0 12M5 10h9M5 14h9')], ['pouce', 'Divers', 'J’aime', P('M7 11v9H4v-9zM7 11l4-8c1.5 0 2.5 1 2.5 2.5V9h5a2 2 0 0 1 2 2.3l-1 6a2 2 0 0 1-2 1.7H7')]
    ];
  })();
  var ICON_BY = {}; ICONS.forEach(function (i) { ICON_BY[i[0]] = i; });

  var C = w.LinkiiCompo = {
    ICONS: ICONS,
    FONTS: FONTS,
    textual: textual,
    SRC: SRC,
    fields: fields,
    fieldText: fieldText,
    TOKEN: TOKEN,
    SCREEN: SCREEN,
    tableLabel: tableLabel,
    /** Liste reliée (§140) : lignes gardées (filtre, tri), début et fin communs de valeurs, valeur affichée d'une cellule. */
    tableRows: tableRows,
    common: common,
    cellText: cellText,
    fmt: fmt,
    numFmt: numFmt,
    numDec: numDec,
    parseNf: parseNf,
    nfString: nfString,
    tableNumber: tableNumber,
    /** Graphiques : données regroupées (aperçu de l'éditeur), calculs et palettes. */
    chartData: chartData,
    AGG: AGGN,
    PALS: PALS,
    CUR: CUR,
    /** Sources utilisées par un document (blocs, listes, champs ; « Cet écran » si un filtre de liste cite l'écran), sans doublon. */
    sources: function (doc) {
      var ids = [], els = (doc && doc.els) || [];
      function add(id) { if (id && ids.indexOf(id) === -1) ids.push(id); }
      for (var i = 0; i < els.length; i++) {
        var e = els[i], m;
        if (e.type === 'app' || e.type === 'list' || e.type === 'chart') add(e.source);
        if (e.type === 'list' || e.type === 'chart') (e.filter || []).forEach(function (f) { if (f && String(f.val || '').charAt(0) === '@') add(SCREEN); });
        if (e.type === 'text') { TOKEN.lastIndex = 0; while ((m = TOKEN.exec(String(e.text || '')))) add(m[1]); }
        showConds(e).forEach(function (c) { if (c.k === 'field' || c.k === 'empty') add(c.source); });
      }
      return ids;
    },
    /** Redessine les calques reliés à des données (blocs, listes, textes à champs) sans toucher aux autres (bandeaux, images). */
    refresh: function (root, doc, o) {
      var els = (doc && doc.els) || [];
      for (var i = 0; i < els.length; i++) {
        var e = els[i];
        if (e.type !== 'app' && e.type !== 'list' && e.type !== 'chart' && !hasFields(e)) continue;
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
      var sc = showConds(e), sattr = '', soff = false;
      if (sc.length && !o.names) {
        var stat = sc.filter(function (c) { return c.k === 'field' || c.k === 'empty'; }), dyn = sc.filter(function (c) { return c.k === 'time' || c.k === 'days'; }), sok = showOk(stat, o, o.tz);
        soff = !(sok && showOk(dyn, o, o.tz));
        sattr = ' data-if="' + (o.player ? 'hide' : 'dim') + '" data-ifs="' + (sok ? 1 : 0) + '" data-ift="' + esc(JSON.stringify(dyn)) + '"';
        if (!sok && o.player) return '';   // un champ ne change qu'avec les données : l'écran redessine alors ; plage et jours sont rendus puis suivis par tick
      }
      var st ='left:' + num(e.x, 0, -1e5, 1e5) + 'px;top:' + num(e.y, 0, -1e5, 1e5) + 'px;width:' + num(e.w, 100, 1, 1e5) + 'px;' +
        (textual(e) ? '' : 'height:' + num(e.h, 100, 1, 1e5) + 'px;') + 'opacity:' + num(e.opacity, 1, 0, 1) + ';' +
        (e.rot ? 'transform:rotate(' + num(e.rot, 0, -360, 360) + 'deg);' : '') + (e.hidden || (soff && o.player) ? 'display:none;' : '');
      return '<div class="lc-el' + (textual(e) ? ' lc-auto' : '') + (soff && !o.player ? ' lc-ifoff' : '') + '" data-id="' + esc(e.id) + '"' + sattr + ' style="' + st + '">' + inner(e, o) + '</div>';
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
    /** Branche les vidéos : adresse (copie locale hors ligne côté player), lecture muette, boucle, début et fin. */
    videos: function (root, loader) {
      var list = root.querySelectorAll('video.lc-vid[data-vsrc]');
      for (var i = 0; i < list.length; i++) (function (v) {
        if (v.getAttribute('data-vdone')) return;
        v.setAttribute('data-vdone', '1');
        var a = +v.getAttribute('data-vs') || 0, b = +v.getAttribute('data-ve') || 0, loop = v.getAttribute('data-vloop') !== '0';
        v.muted = true; v.defaultMuted = true;
        v.addEventListener('loadedmetadata', function () { if (a) { try { v.currentTime = a; } catch (x) { /* pas encore prête */ } } });
        v.addEventListener('timeupdate', function () { if (b && b > a && v.currentTime >= b) { if (loop) { v.currentTime = a; v.play().catch(function () {}); } else v.pause(); } });
        v.addEventListener('ended', function () { if (loop) { v.currentTime = a; v.play().catch(function () {}); } });
        loader(v.getAttribute('data-vsrc'), function (src) { v.src = src; var pr = v.play(); if (pr && pr.catch) pr.catch(function () {}); });
      })(list[i]);
    },
    /** Met à jour horloges, dates et comptes à rebours. */
    tick: function (root, tz) {
      var ifs = root.querySelectorAll('[data-if]'), now0 = ifs.length ? nowIn(tz) : null;
      for (var j = 0; j < ifs.length; j++) {   // « Afficher si… » : plage horaire et jours
        var z = ifs[j], zc, zok = z.getAttribute('data-ifs') === '1';
        try { zc = JSON.parse(z.getAttribute('data-ift') || '[]'); } catch (x) { zc = []; }
        for (var q = 0; zok && q < zc.length; q++) if (!timeCond(zc[q], now0)) zok = false;
        if (z.getAttribute('data-if') === 'hide') { var dz = zok ? '' : 'none'; if (z.style.display !== dz) z.style.display = dz; }
        else if (z.classList.contains('lc-ifoff') === zok) z.classList.toggle('lc-ifoff', !zok);
      }
      var list = root.querySelectorAll('[data-live]');
      for (var i = 0; i < list.length; i++) {
        var n = list[i], k = n.getAttribute('data-live'), v;
        if (k === 'pages') {   // liste reliée en pages : la page de l'instant (la même sur tous les écrans à l'heure)
          var np = +n.getAttribute('data-n') || 1, cur = Math.floor(Date.now() / ((+n.getAttribute('data-sec') || 8) * 1000)) % np;
          for (var c = n.firstChild; c; c = c.nextSibling) { var on = +c.getAttribute('data-pg') === cur; if ((c.style.display !== 'none') !== on) c.style.display = on ? '' : 'none'; }
          continue;
        }
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
      var app = id === SCREEN ? 'screen' : h.settings['ds:' + id] != null ? 'dataset' : null;   // jeu de données : réglage « ds:… » posé à la publication
      for (var j = 0; j < doc.els.length; j++) if (doc.els[j].type === 'app' && doc.els[j].source === id) app = doc.els[j].app;
      h.source(id, function (d) { if (!d) return; o.data[id] = d; C.refresh(st, doc, o); }, (SRC[app] || {}).refresh || 5 * 60000);
    })(ids[si]);
    addQr(st, h.item.qr, doc);
    var srcs = [];
    h.onStop(function () { for (var i = 0; i < srcs.length; i++) srcs[i].revoke(); });
    C.images(st, function (url, cb) { h.media(url).then(function (m) { srcs.push(m); cb(m.src); }); });
    C.videos(st, function (url, cb) { h.media(url).then(function (m) { srcs.push(m); cb(m.src); }); });
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
