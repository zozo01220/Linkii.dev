/* Apps du catalogue Linkivi : un module de rendu par app (JS volontairement "ancien" : var, pas d'async/await).
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
     host.opts.sync {origin, now()}      zone synchronisée ou mur : heure commune (now) et origine du contenu ; la lecture s'en déduit
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

  /* ---------- Météo d'une ville : contenu de liste de lecture (pleine page ou angle) ----------
     Le serveur envoie tout (aujourd'hui, 3 jours, air, alerte) ; les interrupteurs des options avancées sont appliqués ici, dans host.settings. */
  A.register('weather-slide', function (h) {
    var s = h.settings, el = h.el;
    el.className += ' wx' + (s.theme === 'light' ? ' wx-light' : '') + (h.cfg && h.cfg.orientation === 'portrait' ? ' wx-tall' : '');
    function on(k, dflt) { return s[k] === undefined || s[k] === '' ? dflt : s[k] === 'true'; }
    var DAYS = ['Dim', 'Lun', 'Mar', 'Mer', 'Jeu', 'Ven', 'Sam'];
    h.data(function (d) {
      if (!d || d.error) { if (!el.firstChild) el.textContent = ''; return; }
      var det = [];
      if (on('feel', true)) det.push('Ressenti ' + Math.round(d.feel) + '°');
      if (on('wind', true)) det.push('Vent ' + Math.round(d.wind) + ' km/h');
      if (on('humidity', false)) det.push('Humidité ' + Math.round(d.humidity) + ' %');
      if (on('uv', false)) det.push('UV ' + Math.round(d.uv));
      if (on('sun', false) && d.sunrise) det.push('☀ ' + d.sunrise + ' · ' + d.sunset);
      var detH = det.length ? '<div class="wx-det">' + det.map(function (x) { return '<span>' + h.esc(x) + '</span>'; }).join('') + '</div>' : '';
      var daysH = '';
      if (on('forecast', true) && d.days && d.days.length) {
        daysH = '<div class="wx-days">' + d.days.map(function (x) {
          var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(x.date || ''), n = m ? DAYS[new Date(+m[1], +m[2] - 1, +m[3]).getDay()] : '';
          return '<div class="wx-day"><span>' + n + '</span><i>' + wmoIcon(x.code) + '</i><b>' + Math.round(x.max) + '°</b> <em>' + Math.round(x.min) + '°</em></div>';
        }).join('') + '</div>';
      }
      var alH = '';
      if (on('alerts', true) && d.alert) {
        var until = d.alert.until ? Date.parse(d.alert.until) : 0;
        if (!until || until > Date.now())
          alH = '<div class="wx-al wx-sev-' + h.esc(String(d.alert.severity || '').toLowerCase()) + '">⚠ ' + h.esc(d.alert.title || d.alert.headline || 'Alerte météo') +
            (until ? ' · jusqu\'à ' + h.fmtT(until) : '') + '</div>';
      }
      var aqH = on('airQuality', true) && d.air ? '<span class="wx-aqi"><i class="a' + d.air.index + '"></i>Air · ' + h.esc(d.air.label) + '</span>' : '<span></span>';
      var stale = d.fetchedAt && Date.now() - Date.parse(d.fetchedAt) > 3 * 3600 * 1000 ? ' · ⚠ données de ' + h.fmtT(Date.parse(d.fetchedAt)) : '';
      var bot = '<div class="wx-bot">' + aqH + '<span class="wx-att">WeatherAPI.com' + stale + '</span></div>';
      var now = '<div class="wx-big"><span>' + wmoIcon(d.code) + '</span><b>' + Math.round(d.temp) + '°</b></div>';
      if (h.compact) {
        el.innerHTML = '<div class="wx-city">' + h.esc(d.city) + '<small> · ' + h.esc(d.text) + '</small></div>' + now + detH + daysH + alH + bot;
      } else {
        el.innerHTML = '<div class="wx-top"><div class="wx-city">' + h.esc(d.city) + '<small>' + h.esc(d.region || d.country) + '</small></div>' + alH + '</div>' +
          '<div class="wx-mid"><div class="wx-now">' + now + '<div class="wx-txt">' + h.esc(d.text) + '</div>' + detH + '</div>' + daysH + '</div>' + bot;
      }
    }, 10 * 60 * 1000);
  });

  /* ---------- Home Assistant : capteurs, interrupteurs, lumières, thermostats, volets, scènes, serrures ----------
     Un seul rendu pour les sept widgets : le serveur décrit chaque entité (état, valeurs utiles, commandes permises « ctrl »).
     Les données arrivent en direct (le serveur prévient l'écran quand une entité change) ; le sondage de 60 s reste une sécurité.
     Une commande affiche tout de suite l'état voulu et le garde jusqu'à ce que Home Assistant le confirme (8 s au plus). */
  var HA_ICONS = {
    thermo: '<path d="M14 14.76V3.5a2.5 2.5 0 0 0-5 0v11.26a4.5 4.5 0 1 0 5 0z"/>',
    drop: '<path d="M12 2.7l5.7 5.6a8 8 0 1 1-11.4 0z"/>',
    bolt: '<path d="M13 2L3 14h9l-1 8 10-12h-9l1-8z"/>',
    battery: '<rect x="2" y="7" width="18" height="10" rx="2"/><path d="M22 11v2"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
    air: '<path d="M9.6 4.6A2 2 0 1 1 11 8H2M12.6 19.4A2 2 0 1 0 14 16H2M17.7 7.7A2.5 2.5 0 1 1 19.5 12H2"/>',
    gauge: '<path d="M12 14l4-4"/><path d="M3.3 19a10 10 0 1 1 17.4 0"/>',
    door: '<rect x="5" y="3" width="14" height="18" rx="1"/><path d="M15 12v.01"/>',
    window: '<rect x="3" y="3" width="18" height="18" rx="2"/><path d="M12 3v18M3 12h18"/>',
    motion: '<circle cx="13" cy="4" r="2"/><path d="M7 21l3-6 3 3v3M10 15l1-5 4 3 3-1M8 9l3-1"/>',
    person: '<circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/>',
    alert: '<path d="M12 3L2 21h20z"/><path d="M12 10v4M12 17v.01"/>',
    bulb: '<path d="M9 18h6M10 22h4M12 2a7 7 0 0 0-4 12.7V17h8v-2.3A7 7 0 0 0 12 2z"/>',
    plug: '<path d="M9 2v6M15 2v6M6 8h12v4a6 6 0 0 1-12 0zM12 18v4"/>',
    toggle: '<rect x="2" y="7" width="20" height="10" rx="5"/><circle cx="16" cy="12" r="3"/>',
    fan: '<circle cx="12" cy="12" r="2"/><path d="M12 10c0-4 1-7 4-7s3 4-2 7M14 12c4 0 7 1 7 4s-4 3-7-2M12 14c0 4-1 7-4 7s-3-4 2-7M10 12c-4 0-7-1-7-4s4-3 7 2"/>',
    cover: '<rect x="3" y="3" width="18" height="18" rx="1"/><path d="M3 8h18M3 13h18M12 13v5"/>',
    scene: '<path d="M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9z"/>',
    script: '<path d="M7 4l13 8-13 8z"/>',
    lock: '<rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/>',
    unlock: '<rect x="4" y="11" width="16" height="10" rx="2"/><path d="M8 11V7a4 4 0 0 1 7.5-2"/>'
  };
  var HA_BINARY = { door: ['Ouverte', 'Fermée'], garage_door: ['Ouverte', 'Fermée'], window: ['Ouverte', 'Fermée'], opening: ['Ouvert', 'Fermé'],
    motion: ['Mouvement', 'Calme'], occupancy: ['Présence', 'Absence'], presence: ['Présent', 'Absent'], moisture: ['Humide', 'Sec'],
    smoke: ['Fumée !', 'OK'], gas: ['Gaz !', 'OK'], problem: ['Problème', 'OK'], safety: ['Danger', 'OK'], battery: ['Faible', 'OK'],
    lock: ['Ouvert', 'Verrouillé'], connectivity: ['Connecté', 'Déconnecté'], plug: ['Branché', 'Débranché'], power: ['Sous tension', 'Coupé'],
    running: ['En marche', 'Arrêté'], vibration: ['Vibration', 'Calme'], light: ['Lumière', 'Sombre'] };
  var HA_COVER = { open: 'Ouvert', closed: 'Fermé', opening: 'S’ouvre…', closing: 'Se ferme…' };
  var HA_LOCK = { locked: 'Verrouillée', unlocked: 'Déverrouillée', locking: 'Verrouillage…', unlocking: 'Déverrouillage…', jammed: 'Bloquée !', open: 'Ouverte', opening: 'Ouverture…' };
  var HA_HVAC = { heating: 'chauffe', cooling: 'refroidit', idle: 'au repos', off: 'arrêté', drying: 'déshumidifie', fan: 'ventile', preheating: 'préchauffe' };
  var HA_COLORS = ['#FFFFFF', '#FFC27A', '#FF5A4D', '#FFD93D', '#4CD964', '#3B8BD4', '#A66CFF'];
  var HA_APPS = ['ha-sensors', 'ha-switches', 'ha-lights', 'ha-climate', 'ha-covers', 'ha-scenes', 'ha-locks'];

  function haIcon(e) {
    var dc = e.deviceClass || '', u = e.unit || '', d = e.domain, k;
    if (d === 'light') k = 'bulb';
    else if (d === 'switch') k = 'plug';
    else if (d === 'input_boolean') k = 'toggle';
    else if (d === 'fan') k = 'fan';
    else if (d === 'climate') k = 'thermo';
    else if (d === 'cover') k = dc === 'window' ? 'window' : dc === 'door' || dc === 'garage' || dc === 'gate' ? 'door' : 'cover';
    else if (d === 'scene') k = 'scene';
    else if (d === 'script') k = 'script';
    else if (d === 'lock') k = e.on ? 'unlock' : 'lock';
    else if (d === 'binary_sensor') k = ({ door: 'door', garage_door: 'door', opening: 'door', window: 'window', motion: 'motion', occupancy: 'person', presence: 'person',
      moisture: 'drop', smoke: 'alert', gas: 'alert', problem: 'alert', safety: 'alert', battery: 'battery', lock: 'lock', plug: 'plug', power: 'bolt', light: 'sun' })[dc] || 'gauge';
    else k = ({ temperature: 'thermo', humidity: 'drop', moisture: 'drop', power: 'bolt', energy: 'bolt', current: 'bolt', voltage: 'bolt', apparent_power: 'bolt',
      battery: 'battery', illuminance: 'sun', irradiance: 'sun', carbon_dioxide: 'air', pm25: 'air', pm10: 'air', aqi: 'air', volatile_organic_compounds: 'air',
      pressure: 'gauge', atmospheric_pressure: 'gauge' })[dc] || (/°[CF]$/.test(u) ? 'thermo' : /^(k?W|kWh|Wh|V|A|mA)$/.test(u) ? 'bolt' : /^(lx|lm)$/.test(u) ? 'sun' : /ppm|µg/.test(u) ? 'air' : 'gauge');
    return '<svg class="ha-i" viewBox="0 0 24 24" aria-hidden="true">' + HA_ICONS[k] + '</svg>';
  }
  /** Nombre au format suisse : « auto » = une décimale sous 100, aucune au-delà (zéros inutiles retirés). */
  function haNum(v, dec) {
    var n = Number(v), auto = dec === 'auto' || dec == null || dec === '', d = auto ? (Math.abs(n) < 100 ? 1 : 0) : +dec;
    try { return n.toLocaleString('fr-CH', { minimumFractionDigits: auto ? 0 : d, maximumFractionDigits: d }); } catch (x) { return n.toFixed(d); }
  }
  function haRel(iso, now) {
    var t = Date.parse(iso); if (isNaN(t)) return iso;
    var s = Math.round((t - now) / 1000), a = Math.abs(s);
    try {
      var rf = new Intl.RelativeTimeFormat('fr', { numeric: 'auto' });
      if (a < 60) return rf.format(s, 'second');
      if (a < 3600) return rf.format(Math.round(s / 60), 'minute');
      if (a < 86400) return rf.format(Math.round(s / 3600), 'hour');
      if (a < 7 * 86400) return rf.format(Math.round(s / 86400), 'day');
    } catch (x) {}
    return new Date(t).toLocaleDateString('fr-CH', { day: 'numeric', month: 'short' });
  }
  function haDeg(v) { return v == null ? '—' : haNum(v, Math.round(v) === v ? 0 : 1) + '°'; }
  /** Texte de la valeur d'une entité (capteur, ou état lisible des autres domaines). */
  function haValue(e, dec, now) {
    if (e.available === false) return 'Indisponible';
    var d = e.domain, st = e.state;
    if (d === 'binary_sensor') { var l = HA_BINARY[e.deviceClass] || ['Actif', 'Inactif']; return e.on ? l[0] : l[1]; }
    if (d === 'switch' || d === 'light' || d === 'input_boolean' || d === 'fan') return e.on ? (d === 'light' && e.brightness != null ? e.brightness + ' %' : 'Allumé') : 'Éteint';
    if (d === 'climate') return haDeg(e.current);
    if (d === 'cover') return (HA_COVER[st] || st) + (e.position != null && st !== 'closed' ? ' ' + e.position + ' %' : '');
    if (d === 'lock') return HA_LOCK[st] || st;
    if (d === 'scene' || d === 'script') return '';
    if (e.deviceClass === 'timestamp' || /^\d{4}-\d{2}-\d{2}T/.test(st)) return haRel(st, now);
    if (st !== '' && !isNaN(Number(st))) return haNum(st, dec) + (e.unit ? ' ' + e.unit : '');
    return st + (e.unit ? ' ' + e.unit : '');
  }
  function haCan(e, a) { return (e.ctrl || []).indexOf(a) >= 0; }

  function haApp(h) {
    var s = h.settings, el = h.el, now = function () { return h.now ? h.now() : Date.now(); };
    el.className += ' ha';
    el.innerHTML = (s.title ? '<div class="ha-t"></div>' : '') + '<div class="ha-list"></div><div class="ha-err"></div><div class="ha-age"></div>';
    var titleEl = el.querySelector('.ha-t'), list = el.querySelector('.ha-list'), err = el.querySelector('.ha-err'), age = el.querySelector('.ha-age');
    if (titleEl) titleEl.textContent = s.title;
    var last = null, pending = {}, confirm = {}, holding = false;

    /* état voulu en attente de confirmation : { field, value, until } ; oublié dès que Home Assistant le confirme, ou après 8 s */
    function want(e) {
      var p = pending[e.id];
      if (p && (Date.now() > p.until || e[p.field] === p.value)) { delete pending[e.id]; p = null; }
      return p;
    }
    function send(e, body, field, value) {
      if (field) pending[e.id] = { field: field, value: value, until: Date.now() + 8000 };
      err.textContent = '';
      body.entityId = e.id;
      draw();
      return h.command('ha', body).then(function () { setTimeout(draw, 8100); }, function () {
        delete pending[e.id];
        err.textContent = 'Commande non envoyée. Réessayez.';
        draw();
      });
    }
    function btn(cls, label, aria, fn, disabled) {
      var b = document.createElement('button');
      b.type = 'button'; b.className = 'ha-b ' + cls; b.textContent = label; b.setAttribute('aria-label', aria);
      if (disabled) b.disabled = true;
      b.onclick = fn;
      return b;
    }
    function toggle(e, on) {
      var b = document.createElement('button');
      b.type = 'button'; b.className = 'ha-sw' + (on ? ' on' : ''); b.setAttribute('role', 'switch');
      b.setAttribute('aria-checked', on ? 'true' : 'false'); b.setAttribute('aria-label', e.name); b.innerHTML = '<i></i>';
      if (pending[e.id]) b.disabled = true;
      b.onclick = function () { send(e, { action: on ? 'off' : 'on' }, 'on', !on); };
      return b;
    }
    function slider(e, value, action, label) {
      var r = document.createElement('input');
      r.type = 'range'; r.min = action === 'brightness' ? 1 : 0; r.max = 100; r.step = 1; r.value = value == null ? 0 : value;
      r.className = 'ha-range'; r.setAttribute('aria-label', label);
      r.onpointerdown = function () { holding = true; };
      r.ontouchstart = function () { holding = true; };
      r.onchange = function () { holding = false; send(e, { action: action, value: +r.value }, action, +r.value); };
      r.onblur = function () { holding = false; };
      return r;
    }

    function row(e) {
      var p = want(e), view = {}, k;
      for (k in e) view[k] = e[k];
      if (p) { view[p.field] = p.value; if (p.field === 'state' && e.domain === 'lock') view.on = p.value === 'unlocked'; }
      var on = view.on, d = e.domain;
      var r = document.createElement('div');
      r.className = 'ha-row' + (e.available === false ? ' off' : '') + (on ? ' is-on' : '');
      r.setAttribute('data-id', e.id);
      var main = document.createElement('div');
      main.className = 'ha-main';
      main.innerHTML = haIcon(view) + '<span class="ha-n"></span><span class="ha-v"></span><span class="ha-c"></span>';
      main.querySelector('.ha-n').textContent = e.name;
      main.querySelector('.ha-v').textContent = haValue(view, last && last.decimals, now());
      var c = main.querySelector('.ha-c');
      if (d === 'switch' || d === 'input_boolean' || d === 'fan' || d === 'light') {
        if (haCan(e, 'on')) c.appendChild(toggle(e, on));
      } else if (d === 'climate') {
        var sub = document.createElement('small');
        sub.className = 'ha-sub';
        sub.textContent = 'Consigne ' + haDeg(view.target) + (e.hvac && HA_HVAC[e.hvac] ? ' · ' + HA_HVAC[e.hvac] : '');
        main.querySelector('.ha-n').appendChild(sub);
        if (haCan(e, 'temp') && view.target != null) {
          var step = e.step || 0.5, lo = e.min == null ? 5 : e.min, hi = e.max == null ? 30 : e.max;
          c.appendChild(btn('ha-sq', '−', 'Baisser la consigne', function () { var v = Math.max(lo, Math.round((view.target - step) * 10) / 10); send(e, { action: 'temp', value: v }, 'target', v); }, view.target <= lo));
          c.appendChild(btn('ha-sq', '+', 'Monter la consigne', function () { var v = Math.min(hi, Math.round((view.target + step) * 10) / 10); send(e, { action: 'temp', value: v }, 'target', v); }, view.target >= hi));
        }
      } else if (d === 'cover') {
        if (haCan(e, 'open')) {
          c.appendChild(btn('ha-sq', '▲', 'Ouvrir', function () { send(e, { action: 'open' }, 'state', 'open'); }, e.state === 'open' && (e.position == null || e.position >= 100)));
          if (e.stoppable) c.appendChild(btn('ha-sq', '■', 'Arrêter', function () { send(e, { action: 'stop' }); }));
          c.appendChild(btn('ha-sq', '▼', 'Fermer', function () { send(e, { action: 'close' }, 'state', 'closed'); }, e.state === 'closed'));
        }
      } else if (d === 'scene' || d === 'script') {
        if (haCan(e, 'activate')) {
          var go = btn('ha-go', 'Lancer', 'Lancer ' + e.name, function () {
            go.disabled = true; go.textContent = 'Lancé ✓';
            h.command('ha', { entityId: e.id, action: 'activate' }).then(function () { setTimeout(draw, 2500); }, function () { err.textContent = 'Commande non envoyée. Réessayez.'; draw(); });
          });
          c.appendChild(go);
        }
      } else if (d === 'lock') {
        var act = view.state === 'locked' ? 'unlock' : 'lock';
        if (haCan(e, act) && e.state !== 'jammed') {
          var asking = confirm[e.id] > Date.now();
          c.appendChild(btn('ha-go' + (asking ? ' ask' : ''), asking ? 'Confirmer ?' : act === 'lock' ? 'Verrouiller' : 'Déverrouiller', (act === 'lock' ? 'Verrouiller ' : 'Déverrouiller ') + e.name, function () {
            if (!(confirm[e.id] > Date.now())) { confirm[e.id] = Date.now() + 4000; draw(); setTimeout(draw, 4100); return; }   // premier appui : on demande confirmation
            delete confirm[e.id];
            send(e, { action: act }, 'state', act === 'lock' ? 'locked' : 'unlocked');   // affiché tout de suite, jusqu'à la confirmation
          }));
        }
      }
      r.appendChild(main);
      // ligne de réglage : intensité, couleurs, position
      if (d === 'light' && on && haCan(e, 'brightness') && last.dimmer && e.dimmable) {
        var dim = document.createElement('div'); dim.className = 'ha-more';
        dim.appendChild(slider(e, view.brightness, 'brightness', 'Intensité de ' + e.name));
        r.appendChild(dim);
      }
      if (d === 'light' && on && haCan(e, 'color') && last.colors && e.colorable) {
        var dots = document.createElement('div'); dots.className = 'ha-more ha-dots';
        HA_COLORS.forEach(function (col) {
          var b = btn('ha-dot' + (e.color && e.color.toUpperCase() === col ? ' sel' : ''), '', 'Couleur ' + col, function () { send(e, { action: 'color', color: col }); });
          b.style.background = col;
          dots.appendChild(b);
        });
        r.appendChild(dots);
      }
      if (d === 'cover' && haCan(e, 'position') && e.positionable) {
        var pos = document.createElement('div'); pos.className = 'ha-more';
        pos.appendChild(slider(e, view.position, 'position', 'Position de ' + e.name));
        r.appendChild(pos);
      }
      return r;
    }

    function draw() {
      if (!last || holding) return;
      list.innerHTML = '';
      (last.entities || []).forEach(function (e) { if (e.state !== 'absent') list.appendChild(row(e)); });   // entité supprimée ou renommée : signalée dans le back-office, pas à l'écran
      var t = Date.parse(last.fetchedAt), m = isNaN(t) ? 0 : Math.floor((now() - t) / 60000);
      age.textContent = m >= 2 ? 'Dernière valeur il y a ' + (m < 60 ? m + ' min' : Math.floor(m / 60) + ' h') : '';
    }
    h.data(function (d) { if (!d) return; last = d; draw(); }, 60 * 1000);
    h.every(function () { if (!holding) draw(); }, 30 * 1000);   // « il y a … » et dates relatives
  }
  for (var hai = 0; hai < HA_APPS.length; hai++) A.register(HA_APPS[hai], haApp);

  /* ---------- Home Assistant : plan du bâtiment (§193) ----------
     Les étages sont des plaques empilées en 3D (CSS, sans bibliothèque : tient sur les petits appareils) ; chaque appareil est une pastille posée sur son plan,
     toujours face à l'écran. Le serveur renvoie le plan nettoyé (d.plan) et l'état de chaque entité ; une commande suit les mêmes règles que haApp. */
  function haPlanState(e) {   // '' | on | alert | move | off : couleur de la pastille
    if (e.available === false) return 'off';
    var d = e.domain, dc = e.deviceClass || '';
    if (d === 'binary_sensor') { if (!e.on) return ''; return dc === 'motion' || dc === 'occupancy' || dc === 'presence' ? 'move' : 'alert'; }
    if (d === 'lock') return e.on ? 'alert' : '';
    if (d === 'light' || d === 'switch' || d === 'input_boolean' || d === 'fan') return e.on ? 'on' : '';
    if (d === 'climate') return e.hvac === 'heating' ? 'on' : '';
    return '';
  }
  function haPlanApp(h) {
    var el = h.el, now = function () { return h.now ? h.now() : Date.now(); };
    el.className += ' hpl';
    el.innerHTML = '<div class="hpl-view"><div class="hpl-world"></div></div><div class="hpl-bar"></div><div class="hpl-toast"></div><div class="hpl-sum"></div><div class="hpl-sheet"></div>';
    var view = el.querySelector('.hpl-view'), world = el.querySelector('.hpl-world'), bar = el.querySelector('.hpl-bar'), toast = el.querySelector('.hpl-toast'),
      sum = el.querySelector('.hpl-sum'), sheet = el.querySelector('.hpl-sheet');
    var PW = 480, GAP = 175, FLAT = 14, RX_MIN = 40, RX_MAX = 78;
    var last = null, built = '', byId = {}, pending = {}, confirm = {}, panelId = null, holding = false, prev = null;
    var cam = { rz: -32, rx: 62, sel: 'all' }, userUntil = 0, focusUntil = 0, focusTimer = 0, toastTimer = 0, pulseId = null, scale = 1;

    function eView(e) {   // état affiché : celui qu'on vient de commander, jusqu'à confirmation de Home Assistant (8 s au plus)
      var p = pending[e.id];
      if (p && (Date.now() > p.until || e[p.field] === p.value)) { delete pending[e.id]; p = null; }
      if (!p) return e;
      var v = {}, k; for (k in e) v[k] = e[k];
      v[p.field] = p.value;
      if (p.field === 'state' && e.domain === 'lock') v.on = p.value === 'unlocked';
      return v;
    }
    function send(e, body, field, value) {
      if (field) pending[e.id] = { field: field, value: value, until: Date.now() + 8000 };
      body.entityId = e.id;
      refresh();
      return h.command('ha', body).then(function () { setTimeout(refresh, 8100); }, function () {
        delete pending[e.id];
        say('Commande non envoyée. Réessayez.', true);
        refresh();
      });
    }
    function say(text, bad, ms) {
      toast.textContent = text; toast.className = 'hpl-toast show' + (bad ? ' bad' : '');
      clearTimeout(toastTimer); toastTimer = setTimeout(function () { toast.className = 'hpl-toast'; }, ms || 5000);
    }

    /* ----- construction de la scène (à chaque changement du plan) ----- */
    function build(plan) {
      var ratio = 1.6, i;
      for (i = 0; i < plan.floors.length; i++) ratio = i === 0 ? plan.floors[i].ratio : Math.max(ratio, plan.floors[i].ratio);
      var PH = Math.round(PW / ratio);
      world.style.width = PW + 'px'; world.style.height = PH + 'px';
      world.innerHTML = '';
      plan.floors.forEach(function (f, n) {
        var fl = document.createElement('div');
        fl.className = 'hpl-floor'; fl.setAttribute('data-f', f.id);
        var pl = document.createElement('div');
        pl.className = 'hpl-plate' + (f.img ? ' has-img' : '');
        if (f.img) { var im = document.createElement('div'); im.className = 'hpl-img'; im.style.backgroundImage = 'url("' + f.img.replace(/"/g, '') + '")'; pl.appendChild(im); }
        else { var nm = document.createElement('span'); nm.className = 'hpl-fname'; nm.textContent = f.name; pl.appendChild(nm); }
        fl.appendChild(pl);
        var tag = document.createElement('div');
        tag.className = 'hpl-ftag'; tag.innerHTML = '<span></span>'; tag.firstChild.textContent = f.name;
        fl.appendChild(tag);
        plan.devices.filter(function (d) { return d.floor === f.id; }).forEach(function (d) {
          var g = document.createElement('div'); g.className = 'hpl-glow'; g.setAttribute('data-g', d.id);
          g.style.left = d.x * 100 + '%'; g.style.top = d.y * 100 + '%';
          fl.appendChild(g);
          var pin = document.createElement('div'); pin.className = 'hpl-pin'; pin.setAttribute('data-p', d.id);
          pin.style.left = d.x * 100 + '%'; pin.style.top = d.y * 100 + '%';
          pin.innerHTML = '<div class="hpl-pin-in"><button type="button" class="hpl-chip"></button><i class="hpl-stem"></i></div>';
          fl.appendChild(pin);
        });
        world.appendChild(fl);
      });
      cam.sel = plan.floors.some(function (f) { return f.id === cam.sel; }) ? cam.sel : 'all';
      camera(false);
      drawBar();
    }

    function floorZ(i) { return last.plan.explode ? i * GAP : i * FLAT; }
    function camera() {
      if (!last || !last.plan) return;
      var floors = last.plan.floors, selI = cam.sel === 'all' ? -1 : floors.findIndex(function (f) { return f.id === cam.sel; });
      var zs = [], PH = world.offsetHeight || PW / 1.6;
      floors.forEach(function (f, i) {
        var fe = world.querySelector('.hpl-floor[data-f="' + f.id + '"]'); if (!fe) return;
        fe.style.transform = 'translateZ(' + floorZ(i) + 'px)';
        fe.className = 'hpl-floor' + (selI >= 0 && i < selI ? ' ghost' : '') + (selI >= 0 && i > selI ? ' hide' : '');
        if (selI < 0 || i <= selI) zs.push(floorZ(i));
      });
      var z0 = Math.min.apply(null, zs), z1 = Math.max.apply(null, zs) + 40;
      var rx = cam.rx * Math.PI / 180, diag = Math.sqrt(PW * PW + PH * PH);
      var w = view.clientWidth || 640, hh = view.clientHeight || 360;
      var need = diag * Math.cos(rx) + (z1 - z0) * Math.sin(rx) + 90;
      scale = Math.min(w * 0.92 / diag, hh * 0.92 / need);
      world.classList.toggle("all", selI < 0 && floors.length > 1);
      world.style.setProperty('--rz', cam.rz + 'deg'); world.style.setProperty('--rx', cam.rx + 'deg');
      world.style.transform = 'translate(-50%,-50%) scale(' + scale.toFixed(3) + ') rotateX(' + cam.rx + 'deg) rotateZ(' + cam.rz + 'deg) translateZ(' + (-(z0 + z1) / 2) + 'px)';
    }

    /* ----- états ----- */
    function update() {
      if (!last || !last.plan) return;
      var lab = last.plan.labels || 'all';
      last.plan.devices.forEach(function (d) {
        var raw = byId[d.id]; if (!raw || raw.state === 'absent') { var gone = world.querySelector('[data-p="' + d.id + '"]'); if (gone) gone.style.display = 'none'; return; }
        var e = eView(raw), st = haPlanState(e);
        var pin = world.querySelector('[data-p="' + d.id + '"]'); if (!pin) return;
        pin.style.display = '';
        var chip = pin.querySelector('.hpl-chip');
        chip.className = 'hpl-chip ' + st + (lab === 'none' || (lab === 'active' && !st) ? ' novals' : '') + (pulseId === d.id ? ' pulse' : '');
        chip.setAttribute('aria-label', e.name);
        chip.innerHTML = haIcon(e) + '<span class="hpl-v"></span>';
        chip.querySelector('.hpl-v').textContent = haValue(e, last.decimals, now());
        var g = world.querySelector('[data-g="' + d.id + '"]');
        if (g) {
          if (e.domain === 'light' && e.on && e.available !== false) {
            var b = e.brightness == null ? 100 : e.brightness, R = (0.07 + 0.11 * b / 100) * PW, col = e.color || '#FFC870';
            g.style.width = g.style.height = 2 * R + 'px'; g.style.marginLeft = g.style.marginTop = -R + 'px';
            g.style.background = 'radial-gradient(circle, ' + haRgba(col, 0.7) + ' 0%, ' + haRgba(col, 0.22) + ' 45%, ' + haRgba(col, 0) + ' 70%)'; g.style.opacity = 1;
          } else g.style.opacity = 0;
        }
      });
      drawSummary();
      if (panelId) drawSheet();
    }
    function haRgba(hex, a) { var n = parseInt(String(hex).replace('#', ''), 16); if (isNaN(n)) n = 0xFFC870; return 'rgba(' + (n >> 16) + ',' + ((n >> 8) & 255) + ',' + (n & 255) + ',' + a + ')'; }

    function drawSummary() {
      var ents = last.plan.devices.map(function (d) { return byId[d.id]; }).filter(function (e) { return e && e.state !== 'absent'; }).map(eView);
      var lights = ents.filter(function (e) { return e.domain === 'light' && e.on; }).length;
      var open = ents.filter(function (e) { return e.domain === 'binary_sensor' && e.on && haPlanState(e) === 'alert'; });
      var temps = ents.filter(function (e) { return e.domain === 'climate' && e.current != null; });
      var avg = temps.length ? temps.reduce(function (s, e) { return s + e.current; }, 0) / temps.length : null;
      var h2 = '<span class="hpl-s' + (lights ? ' on' : '') + '">' + lights + (lights > 1 ? ' lumières allumées' : lights ? ' lumière allumée' : ' lumière allumée') + '</span>';
      h2 += '<span class="hpl-s' + (open.length ? ' alert' : '') + '"></span>';
      if (avg != null) h2 += '<span class="hpl-s">Température ' + haDeg(Math.round(avg * 10) / 10) + '</span>';
      sum.innerHTML = h2;
      var o = sum.children[1];
      o.textContent = open.length ? open.map(function (e) { return e.name; }).slice(0, 3).join(', ') + (open.length > 1 ? ' : ouverts' : ' : ouvert') : 'Portes et fenêtres fermées';
    }

    /* ----- barre du haut (étages, rotation) : seulement avec la commande à l'écran ----- */
    function drawBar() {
      bar.innerHTML = '';
      if (!last || !last.plan || !last.plan.touch) return;   // écran sans commande : ni boutons ni panneau
      function b(label, key, on) {
        var x = document.createElement('button'); x.type = 'button'; x.className = 'hpl-k' + (on ? ' on' : ''); x.textContent = label;
        x.onclick = function () { pick(key); }; return x;
      }
      bar.appendChild(b('Tous', 'all', cam.sel === 'all'));
      last.plan.floors.slice().reverse().forEach(function (f) { bar.appendChild(b(f.name, f.id, cam.sel === f.id)); });
      var sp = document.createElement('span'); sp.className = 'hpl-sp'; bar.appendChild(sp);
      bar.appendChild(b('⟲', 'rl', false)); bar.appendChild(b('⟳', 'rr', false));
    }
    function pick(key) {
      userUntil = Date.now() + 8000;
      if (key === 'rl') cam.rz -= 45; else if (key === 'rr') cam.rz += 45; else cam.sel = key;
      world.classList.add('ease'); camera(); setTimeout(function () { world.classList.remove('ease'); }, 900); drawBar();
    }

    /* ----- panneau de commande d'un appareil ----- */
    function drawSheet() {
      var raw = byId[panelId]; if (!raw || raw.state === 'absent') { closeSheet(); return; }
      if (holding) return;
      var e = eView(raw), d = e.domain, c = function (a) { return haCan(raw, a); };
      sheet.className = 'hpl-sheet open'; sheet.innerHTML = '';
      function add(tag, cls, text) { var x = document.createElement(tag); x.className = cls; if (text != null) x.textContent = text; sheet.appendChild(x); return x; }
      function rowBtn(cls, label, fn, disabled) { var x = add('button', 'hpl-sb ' + cls, label); x.type = 'button'; if (disabled) x.disabled = true; x.onclick = fn; return x; }
      function range(value, min, action, label) {
        var r = add('input', 'hpl-range'); r.type = 'range'; r.min = min; r.max = 100; r.step = 1; r.value = value == null ? 0 : value; r.setAttribute('aria-label', label);
        r.onpointerdown = function () { holding = true; }; r.ontouchstart = function () { holding = true; };
        r.onchange = function () { holding = false; send(raw, { action: action, value: +r.value }, action === 'brightness' ? 'brightness' : 'position', +r.value); };
        r.onblur = function () { holding = false; };
      }
      var head = add('div', 'hpl-sh');
      head.innerHTML = haIcon(e) + '<span class="hpl-sn"></span><span class="hpl-sv"></span><button type="button" class="hpl-sx" aria-label="Fermer">×</button>';
      head.querySelector('.hpl-sn').textContent = e.name;
      head.querySelector('.hpl-sv').textContent = haValue(e, last.decimals, now());
      head.querySelector('.hpl-sx').onclick = closeSheet;
      if (raw.available === false) return;
      if (d === 'switch' || d === 'input_boolean' || d === 'fan' || d === 'light') {
        if (c('on')) rowBtn(e.on ? 'on' : '', e.on ? 'Allumé · éteindre' : 'Éteint · allumer', function () { send(raw, { action: e.on ? 'off' : 'on' }, 'on', !e.on); }, !!pending[raw.id]);
        if (d === 'light' && e.on && c('brightness') && last.dimmer && raw.dimmable) { add('div', 'hpl-sl', 'Intensité'); range(e.brightness, 1, 'brightness', 'Intensité de ' + e.name); }
        if (d === 'light' && e.on && c('color') && raw.colorable) {
          var dots = add('div', 'hpl-dots');
          HA_COLORS.forEach(function (col) {
            var x = document.createElement('button'); x.type = 'button'; x.className = 'hpl-dot' + (raw.color && raw.color.toUpperCase() === col ? ' sel' : ''); x.style.background = col;
            x.setAttribute('aria-label', 'Couleur ' + col); x.onclick = function () { send(raw, { action: 'color', color: col }); }; dots.appendChild(x);
          });
        }
      } else if (d === 'climate') {
        add('div', 'hpl-sl', 'Consigne ' + haDeg(e.target) + (raw.hvac && HA_HVAC[raw.hvac] ? ' · ' + HA_HVAC[raw.hvac] : ''));
        if (c('temp') && e.target != null) {
          var step = raw.step || 0.5, lo = raw.min == null ? 5 : raw.min, hi = raw.max == null ? 30 : raw.max, row = add('div', 'hpl-two');
          var m = document.createElement('button'); m.type = 'button'; m.className = 'hpl-sb'; m.textContent = '−'; m.disabled = e.target <= lo;
          m.onclick = function () { var v = Math.max(lo, Math.round((e.target - step) * 10) / 10); send(raw, { action: 'temp', value: v }, 'target', v); };
          var p = document.createElement('button'); p.type = 'button'; p.className = 'hpl-sb'; p.textContent = '+'; p.disabled = e.target >= hi;
          p.onclick = function () { var v = Math.min(hi, Math.round((e.target + step) * 10) / 10); send(raw, { action: 'temp', value: v }, 'target', v); };
          row.appendChild(m); row.appendChild(p);
        }
      } else if (d === 'cover') {
        if (c('open')) {
          var r3 = add('div', 'hpl-two');
          [['▲ Ouvrir', 'open', 'open', raw.state === 'open' && (raw.position == null || raw.position >= 100)], ['■', 'stop', null, !raw.stoppable], ['▼ Fermer', 'close', 'closed', raw.state === 'closed']].forEach(function (a) {
            if (a[1] === 'stop' && !raw.stoppable) return;
            var x = document.createElement('button'); x.type = 'button'; x.className = 'hpl-sb'; x.textContent = a[0]; x.disabled = !!a[3];
            x.onclick = function () { send(raw, { action: a[1] }, a[2] ? 'state' : null, a[2]); }; r3.appendChild(x);
          });
        }
        if (c('position') && raw.positionable) { add('div', 'hpl-sl', 'Position'); range(e.position, 0, 'position', 'Position de ' + e.name); }
      } else if (d === 'lock') {
        var act = e.state === 'locked' ? 'unlock' : 'lock';
        if (c(act) && raw.state !== 'jammed') {
          var asking = confirm[raw.id] > Date.now();
          rowBtn(asking ? 'ask' : '', asking ? 'Confirmer ?' : act === 'lock' ? 'Verrouiller' : 'Déverrouiller', function () {
            if (!(confirm[raw.id] > Date.now())) { confirm[raw.id] = Date.now() + 4000; drawSheet(); setTimeout(drawSheet, 4100); return; }
            delete confirm[raw.id];
            send(raw, { action: act }, 'state', act === 'lock' ? 'locked' : 'unlocked');
          });
        }
      }
    }
    function closeSheet() { panelId = null; holding = false; sheet.className = 'hpl-sheet'; sheet.innerHTML = ''; }
    function openSheet(id) { panelId = id; drawSheet(); }

    /* ----- rotation au doigt, toucher d'une pastille ----- */
    var drag = null;
    view.addEventListener('pointerdown', function (ev) {
      var chip = ev.target.closest && ev.target.closest('.hpl-chip');
      drag = { x: ev.clientX, y: ev.clientY, rz: cam.rz, rx: cam.rx, id: chip ? chip.parentNode.parentNode.getAttribute('data-p') : null, moved: false };
      try { view.setPointerCapture(ev.pointerId); } catch (x) {}
    });
    view.addEventListener('pointermove', function (ev) {
      if (!drag) return;
      var dx = ev.clientX - drag.x, dy = ev.clientY - drag.y;
      if (!drag.moved && Math.sqrt(dx * dx + dy * dy) < 6) return;
      drag.moved = true; userUntil = Date.now() + 6000;
      cam.rz = drag.rz + dx * 0.35; cam.rx = Math.max(RX_MIN, Math.min(RX_MAX, drag.rx - dy * 0.2)); camera();
    });
    function endDrag() {
      if (!drag) return;
      if (!drag.moved) { if (drag.id) { if (last && last.plan && last.plan.touch) openSheet(drag.id); } else closeSheet(); }
      drag = null;
    }
    view.addEventListener('pointerup', endDrag); view.addEventListener('pointercancel', function () { drag = null; });

    /* ----- événements : porte, fenêtre ou mouvement qui change ----- */
    function events() {
      var snap = {};
      (last.entities || []).forEach(function (e) { snap[e.id] = haPlanState(e); });
      if (prev && last.plan.focus !== false) {
        last.plan.devices.forEach(function (d) {
          var e = byId[d.id], was = prev[d.id], now2 = snap[d.id];
          if (!e || (now2 !== 'alert' && now2 !== 'move') || was === now2) return;
          say((e.domain === 'lock' ? e.name + ' : déverrouillée' : e.domain === 'binary_sensor' && now2 === 'move' ? e.name + ' : mouvement' : e.name + ' : ouvert'), now2 === 'alert');
          pulseId = d.id; setTimeout(function () { if (pulseId === d.id) { pulseId = null; update(); } }, 6000);
          if (!panelId && Date.now() > userUntil) {   // l'écran montre l'étage concerné quelques secondes
            var back = cam.sel; cam.sel = d.floor; focusUntil = Date.now() + 6000;
            world.classList.add('ease'); camera();
            clearTimeout(focusTimer);
            focusTimer = setTimeout(function () { cam.sel = back; focusUntil = 0; camera(); setTimeout(function () { world.classList.remove('ease'); }, 900); }, 6000);
          }
        });
      }
      prev = snap;
    }

    function refresh() { update(); }
    h.data(function (d) {
      if (!d || !d.plan) { if (!last) { sum.textContent = ''; world.innerHTML = '<div class="hpl-none">Plan non configuré</div>'; } return; }
      last = d; byId = {};
      (d.entities || []).forEach(function (e) { byId[e.id] = e; });
      d.plan.touch = (d.entities || []).some(function (e) { return (e.ctrl || []).length; });
      var sig = JSON.stringify([d.plan.floors, d.plan.devices]);
      if (sig !== built) { built = sig; build(d.plan); }
      events(); update();
      camera();
    }, 60 * 1000);
    h.every(function () { if (!holding && last) update(); }, 30 * 1000);

    var t0 = Date.now(), raf = 0, dead = false;
    function spin() {
      if (dead) return;
      if (last && last.plan && last.plan.spin && !last.plan.touch && Date.now() > userUntil && !focusUntil) { cam.rz = -32 + 24 * Math.sin((Date.now() - t0) / 7000); camera(); }
      raf = requestAnimationFrame(spin);
    }
    raf = requestAnimationFrame(spin);
    if (window.ResizeObserver) { var ro = new ResizeObserver(function () { camera(); }); ro.observe(view); h.onStop(function () { ro.disconnect(); }); }
    h.onStop(function () { dead = true; cancelAnimationFrame(raf); clearTimeout(toastTimer); clearTimeout(focusTimer); });
  }
  A.register('ha-plan', haPlanApp);

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
  // « rss » : contenu de la liste de lecture (pleine page ou bandeau) ; « rss-widget » : widget d'écran, toujours en bandeau défilant
  function rssApp(h) {
    var s = h.settings, el = h.el;
    if (!h.compact) el.className += ' dl';
    h.data(function (d) {
      var items = (d && d.items) || [];
      if (s.count > 0) items = items.slice(0, +s.count);
      if (h.compact) {   // bandeau : titres défilants
        var txt = items.map(function (i) { return h.esc(i.title); }).join('<i class="sep">•</i>');
        h.ticker(txt || h.esc('Aucune actualité'), true, { slow: 1.6, normal: 1, fast: 0.6 }[s.scrollSpeed] || 1);
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
  }
  A.register('rss', rssApp);
  A.register('rss-widget', rssApp);

  /* ---------- Facebook : publications d'une Page (mur de 4, carte unique, dernière publication) ---------- */
  A.register('facebook', function (h) {
    var s = h.settings, el = h.el, mode = s.mode === 'single' || s.mode === 'latest' ? s.mode : 'wall';
    var posts = [], name = 'Facebook', page = 0, sig = null, per = mode === 'wall' ? 4 : 1;
    var pageCount = function () { return Math.max(1, Math.ceil((mode === 'latest' ? Math.min(1, posts.length) : posts.length) / per)); };

    function card(p) {
      var c = document.createElement('div');
      c.className = 'fb-card' + (p.picture ? '' : ' txt');
      if (p.picture) {
        var img = document.createElement('img');
        img.alt = ''; img.src = p.picture;
        img.onerror = function () { img.remove(); c.className += ' txt'; };   // photo expirée ou hors ligne : la carte garde son texte
        c.appendChild(img);
      }
      var t = document.createElement('div'); t.className = 'fb-t';
      var m = document.createElement('div'); m.className = 'fb-x'; m.textContent = p.message || '';
      var d = document.createElement('div'); d.className = 'fb-m';
      d.textContent = name + (p.created ? ' · ' + h.shortDate(Date.parse(p.created)) : '');
      t.appendChild(m); t.appendChild(d); c.appendChild(t);
      return c;
    }

    function draw() {
      var list = mode === 'latest' ? posts.slice(0, 1) : posts.slice(page * per, page * per + per);
      var key = page + '|' + name + '|' + list.map(function (p) { return p.id + p.picture; }).join(',');
      if (key === sig) return;   // rien de changé : pas de redessin (les photos ne clignotent pas)
      sig = key;
      if (h.compact) {   // bandeau : textes défilants
        var txt = posts.filter(function (p) { return p.message; }).map(function (p) { return h.esc(p.message); }).join('<i class="sep">•</i>');
        h.ticker(txt || h.esc('Aucune publication'), true, 1.6);
        return;
      }
      el.textContent = '';
      var head = document.createElement('div'); head.className = 'fb-h';
      var logo = document.createElement('b'); logo.textContent = 'f';
      var title = document.createElement('span'); title.textContent = name;
      head.appendChild(logo); head.appendChild(title); el.appendChild(head);
      if (!list.length) {
        var e = document.createElement('div'); e.className = 'dl-empty'; e.textContent = 'Aucune publication'; el.appendChild(e);
        return;
      }
      var grid = document.createElement('div');
      var rows = Math.max(1, list.length - 1);
      grid.className = 'fb-grid' + (list.length === 1 ? ' one' : '');
      if (list.length > 1) grid.style.gridTemplateRows = 'repeat(' + rows + ', minmax(0, 1fr))';
      for (var i = 0; i < list.length; i++) {
        var c = card(list[i]);
        if (i === 0 && list.length > 1) c.style.gridRow = '1 / span ' + rows;
        grid.appendChild(c);
      }
      el.appendChild(grid);
    }

    h.data(function (d) {
      posts = (d && d.posts) || [];
      name = (d && d.page) || 'Facebook';
      if (page >= pageCount()) page = 0;
      draw();
    }, 15 * 60 * 1000);
    if (mode !== 'latest') h.every(function () {
      if (pageCount() > 1) { page = (page + 1) % pageCount(); draw(); }
    }, Math.max(5, parseInt(s.interval, 10) || 12) * 1000);
  });

  /* ---------- YouTube : API IFrame (lecture automatique, enchaînement à la fin, erreurs visibles) ---------- */
  A.register('youtube', function (h) {
    var s = h.settings, u = s.url || '';
    var v = /[?&]v=([A-Za-z0-9_-]{11})/.exec(u), l = /[?&]list=([A-Za-z0-9_-]{10,64})/.exec(u);
    if (!v && !l) { h.note('YouTube : lien de vidéo manquant — republiez la liste de lecture'); return; }
    h.youtube({
      videoId: v ? v[1] : null, playlistId: !v && l ? l[1] : null,
      sound: s.sound === 'true', hide: s.hideOverlays === 'true', start: parseInt(s.startAt, 10) || 0, end: parseInt(s.endAt, 10) || 0
    });
  });

  /* ---------- Médiathèque : image, vidéo, diaporamas ---------- */
  // Les fichiers (item.urls) sont résolus à la publication et préchargés par le player : lecture hors ligne.
  function mediaBox(h) {
    h.el.className += ' mlib fit-' + (h.settings.fit === 'cover' ? 'cover' : 'contain');
    var urls = (h.item.urls || []).slice();
    if (h.settings.shuffle === 'true') {
      // lecture synchronisée : même ordre « aléatoire » sur tous les écrans (tirage déterminé par l'identifiant du contenu)
      var seed = 0, sid = h.opts && h.opts.sync ? String(h.item.id || '') : null;
      if (sid) for (var c = 0; c < sid.length; c++) seed = (seed * 31 + sid.charCodeAt(c)) >>> 0;
      var rnd = sid ? function () { seed = (seed * 1664525 + 1013904223) >>> 0; return seed / 4294967296; } : Math.random;
      for (var i = urls.length - 1; i > 0; i--) { var j = Math.floor(rnd() * (i + 1)); var t = urls[i]; urls[i] = urls[j]; urls[j] = t; }
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

  /* ---------- Lecture synchronisée (zone synchronisée, mur d'écrans) ----------
     Tous les écrans du groupe partagent l'heure du serveur et l'origine du contenu : le fichier affiché et la position
     dans une vidéo s'en déduisent, sans échange entre écrans. Une image dure « interval » ; une vidéo, sa durée réelle
     (lue dans ses métadonnées : la même partout, puisque ce sont les mêmes fichiers). La liste boucle sans fin :
     c'est la liste de lecture qui passe au contenu suivant, à l'heure commune. */
  function syncOf(h) { return h.opts && h.opts.sync ? h.opts.sync : null; }

  // Recale une vidéo sur la position voulue : petit écart -> vitesse ajustée (invisible), grand écart -> saut.
  function align(v, want) {
    if (!v.duration || !isFinite(v.duration) || v.readyState < 1) return;
    var diff = v.currentTime - want;
    if (diff > v.duration / 2) diff -= v.duration; else if (diff < -v.duration / 2) diff += v.duration;   // passage de la boucle
    if (Math.abs(diff) > 0.5) { try { v.currentTime = want; } catch (e) {} v.playbackRate = 1; }
    else if (Math.abs(diff) > 0.04) v.playbackRate = diff > 0 ? 0.95 : 1.05;
    else v.playbackRate = 1;
    if (v.paused) { var p = v.play(); if (p && p.catch) p.catch(function () {}); }
  }

  function syncedList(h, b, imgMs, isVideo) {
    var sync = syncOf(h), swap = swapper(h, imgMs ? imgMs * 0.6 : 800);
    var n = b.urls.length, durs = [], srcs = [], pending = n, cur = -1, curVideo = null, lastAlign = 0;
    function done() { if (--pending === 0) { tick(); h.every(tick, 100); } }
    b.urls.forEach(function (u, i) {
      b.load(u, function (src) {
        srcs[i] = src;
        if (!isVideo(u)) { durs[i] = imgMs; done(); return; }
        var probe = document.createElement('video');
        probe.preload = 'metadata'; probe.muted = true;
        probe.onloadedmetadata = function () { durs[i] = isFinite(probe.duration) && probe.duration > 0 ? probe.duration * 1000 : 0; probe.removeAttribute('src'); done(); };
        probe.onerror = function () { durs[i] = 0; done(); };   // vidéo illisible : sautée
        probe.src = src;
      });
    });
    // Fichier en cours et position dedans, à l'heure commune
    function where() {
      var total = 0, k;
      for (k = 0; k < n; k++) total += durs[k] || 0;
      if (!total) return null;
      var t = (((sync.now() - sync.origin) % total) + total) % total, acc = 0;
      for (k = 0; k < n; k++) {
        if (durs[k] && t < acc + durs[k]) return { i: k, off: t - acc };
        acc += durs[k] || 0;
      }
      return null;
    }
    function tick() {
      var w = where();
      if (!w) return;
      if (w.i !== cur) { cur = w.i; show(w.i, w.off); return; }
      if (curVideo && Date.now() - lastAlign > 1000) { lastAlign = Date.now(); align(curVideo, w.off / 1000); }
    }
    function show(i, off) {
      if (isVideo(b.urls[i])) {
        var v = document.createElement('video'), shown = false;
        v.muted = h.settings.sound !== 'true'; v.autoplay = true; v.loop = n === 1; v.setAttribute('playsinline', '');
        v.className = 'mlib-slide';
        var reveal = function () { if (shown) return; shown = true; swap(v, stopMedia); };
        v.addEventListener('loadedmetadata', function () { var w = where(); try { v.currentTime = (w && w.i === i ? w.off : off) / 1000; } catch (e) {} });
        v.addEventListener('playing', reveal);
        setTimeout(reveal, 1500);
        v.src = srcs[i];
        h.el.appendChild(v);
        var p = v.play();
        if (p && p.catch) p.catch(function () { v.muted = true; v.play().catch(function () {}); });
        curVideo = v; lastAlign = Date.now();
      } else {
        curVideo = null;
        var img = new Image();
        img.className = 'mlib-slide';
        img.onload = function () { h.el.appendChild(img); swap(img, stopMedia); };
        img.src = srcs[i];
      }
    }
    function stopMedia(old) { try { if (old.pause) { old.pause(); old.removeAttribute('src'); old.load(); } } catch (e) {} }
    h.onStop(function () { if (curVideo) stopMedia(curVideo); });
  }
  function always() { return true; }
  function never() { return false; }

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
    if (syncOf(h)) { syncedList(h, { urls: b.urls.slice(0, 1), load: b.load }, 0, always); return; }
    b.load(b.urls[0], function (src) {
      var v = makeVideo(h, src, function () { if (!b.ended()) { v.currentTime = 0; v.play().catch(function () {}); } }, b.failed);
      h.el.appendChild(v);
    });
  });

  // Transition entre deux médias d'un diaporama : type (fondu, glissements, volet, iris, zooms, flou, retournement, aucune) et vitesse.
  // maxMs : plafond (une image ne doit pas passer plus de temps en transition qu'affichée).
  var SPEEDS = { fast: 400, normal: 800, slow: 1500, 'very-slow': 3000 };
  function swapper(h, maxMs) {
    var kind = /^(fade|slide|slide-up|wipe|iris|zoom|zoom-out|blur|flip|none)$/.test(h.settings.transition || '') ? h.settings.transition : 'fade';
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
    if (syncOf(h)) { syncedList(h, b, ms, never); return; }
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
    if (syncOf(h)) { syncedList(h, b, 0, always); return; }
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

  /* ---------- Document PDF : pages converties en images par le serveur (diaporama) ---------- */
  A.register('pdf', function (h) {
    var r = A.get('slideshow-images');
    if (r) r(h);
  });

  /* ---------- Google Slides, Google Docs et Microsoft PowerPoint : convertis en PDF (Google, Microsoft) puis en images (même diaporama que le PDF) ---------- */
  ['google-slides', 'google-docs', 'ms-powerpoint'].forEach(function (id) {
    A.register(id, function (h) {
      var r = A.get('slideshow-images');
      if (r) r(h);
    });
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
    if (syncOf(h)) { syncedList(h, b, ms, function (u) { return VIDEO_EXT.test(u); }); return; }
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
