/* Formatage Home Assistant commun aux widgets (apps.js) et à la vue du bâtiment (building-view.js) : icônes, valeurs lisibles, degrés, commandes permises.
   JS volontairement « ancien » (var, pas de fonctions fléchées) : il tourne aussi dans la WebView Android. Chargeable sous Node pour les tests. */
(function (root) {
  'use strict';
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

  var api = { ICONS: HA_ICONS, BINARY: HA_BINARY, COVER: HA_COVER, LOCK: HA_LOCK, HVAC: HA_HVAC, COLORS: HA_COLORS,
    icon: haIcon, num: haNum, rel: haRel, deg: haDeg, value: haValue, can: haCan };
  root.LinkiiHa = api;
  if (typeof module === 'object' && module.exports) module.exports = api;
})(typeof window !== 'undefined' ? window : globalThis);
