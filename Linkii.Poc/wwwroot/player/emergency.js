/* Urgence (application « Alerte d'urgence », §159, maquettes/urgence.html).
   Plein écran rouge, par-dessus tout : titre en capitales, message, heure de début, cadre qui clignote doucement. Pas de son, pas de bouton.
   Il reste affiché jusqu'à ce que le serveur dise qu'il n'y en a plus (le back-office ou l'API la lève) ; seul l'essai (un écran, « ESSAI ») s'arrête seul.
   Hors ligne, il reste affiché avec « Connexion perdue depuis hh:mm ». JS volontairement « ancien » (var, pas d'async) pour les WebView anciennes.
   player.js lui prête : la scène, le moment de l'affichage et du retrait (arrêt et reprise des contenus), l'accusé de réception, l'heure du serveur. */
(function () {
  'use strict';

  var CSS_ID = 'lkem-css', KEY = 'lk_emergency', LOST_AFTER_MS = 20000;
  var el = null, cur = null, hooks = null, acked = null, lastOk = Date.now(), ticker = null, endTimer = null;

  var WARN = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M10.3 3.9L1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/><path d="M12 9v4M12 17h.01"/></svg>';

  function css() {
    if (document.getElementById(CSS_ID)) return;
    var st = document.createElement('style');
    st.id = CSS_ID;
    st.textContent =
      '#emergencyview{position:absolute;z-index:100000;background:radial-gradient(120% 120% at 50% 40%,#C4001F 0%,#B00020 55%,#7A0016 100%);color:#fff;display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;box-sizing:border-box;overflow:hidden;cursor:none;font-family:"IBM Plex Sans","Segoe UI",Arial,sans-serif}' +
      '#emergencyview .lkem-frame{position:absolute;left:2.2em;top:2.2em;right:2.2em;bottom:2.2em;border:.9em solid rgba(255,255,255,.85);border-radius:1.2em;-webkit-animation:lkem-pulse 2.4s ease-in-out infinite;animation:lkem-pulse 2.4s ease-in-out infinite;pointer-events:none}' +
      '@-webkit-keyframes lkem-pulse{0%,100%{opacity:.25}50%{opacity:.95}}@keyframes lkem-pulse{0%,100%{opacity:.25}50%{opacity:.95}}' +
      '@media (prefers-reduced-motion:reduce){#emergencyview .lkem-frame{-webkit-animation:none;animation:none;opacity:.6}}' +
      '#emergencyview .lkem-ico{width:15em;height:15em;margin-bottom:3em}#emergencyview .lkem-ico svg{width:100%;height:100%}' +
      '#emergencyview .lkem-ti{font-size:9em;font-weight:700;letter-spacing:.04em;text-transform:uppercase;line-height:1.05;max-width:90%;word-wrap:break-word}' +
      '#emergencyview .lkem-me{font-size:5.4em;font-weight:500;line-height:1.3;max-width:88%;margin-top:3em;word-wrap:break-word;white-space:pre-line}' +
      '#emergencyview.long .lkem-ti{font-size:7em}#emergencyview.long .lkem-me{font-size:4em}' +
      '#emergencyview.port .lkem-ti{font-size:11em}#emergencyview.port .lkem-me{font-size:7em}#emergencyview.port.long .lkem-ti{font-size:9em}#emergencyview.port.long .lkem-me{font-size:5.6em}#emergencyview.port .lkem-ico{width:30em;height:30em}' +
      '#emergencyview .lkem-ft{position:absolute;left:0;right:0;bottom:3.6em;font-size:2.6em;opacity:.85;letter-spacing:.02em}' +
      '#emergencyview .lkem-test{position:absolute;left:0;right:0;top:0;background:#fff;color:#B00020;font-weight:700;font-size:3em;padding:.4em;letter-spacing:.06em}' +
      '#emergencyview .lkem-lost{position:absolute;right:3em;bottom:3em;background:rgba(0,0,0,.55);color:#fff;border-radius:.8em;padding:.4em .9em;font-size:2.4em;display:none}';
    document.head.appendChild(st);
  }

  function pad(n) { return ('0' + n).slice(-2); }
  function hm(ms) { var d = new Date(ms); return pad(d.getHours()) + ':' + pad(d.getMinutes()); }
  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

  /* Position et taille : comme le visuel de pause, la case de cet écran dans la scène (un mur : le même message complet sur chaque écran). */
  function place() {
    if (!el || !hooks) return;
    var c = hooks.cell(), w = hooks.wall(), st = el.style;
    if (w && w.cols > 0 && !w.whole) { st.left = (w.col * c.w) + 'px'; st.top = (w.row * c.h) + 'px'; st.width = c.w + 'px'; st.height = c.h + 'px'; st.right = st.bottom = 'auto'; }
    else { st.left = st.top = st.right = st.bottom = '0'; st.width = st.height = ''; }
    st.fontSize = (Math.min(c.w, c.h) / 100) + 'px';   // 1em = 1 % du petit côté de l'écran
    el.className = (c.h > c.w ? 'port ' : '') + (cur && (cur.message.length > 110 || cur.title.length > 22) ? 'long' : '');
  }

  function render() {
    if (!el) {
      css();
      el = document.createElement('div');
      el.id = 'emergencyview';
      el.setAttribute('role', 'alert');
      var stop = function (ev) { ev.stopPropagation(); ev.preventDefault(); };
      ['click', 'mousedown', 'mouseup', 'touchstart', 'touchend', 'pointerdown', 'pointerup', 'contextmenu', 'wheel'].forEach(function (n) { el.addEventListener(n, stop, { passive: false }); });
      hooks.stage.appendChild(el);
    }
    var lbl = cur.test ? '<div class="lkem-test">ESSAI · ceci n\'est pas une vraie alerte</div>' : '';
    el.innerHTML = lbl + '<div class="lkem-frame"></div><div class="lkem-ico">' + WARN + '</div><div class="lkem-ti">' + esc(cur.title) + '</div><div class="lkem-me">' + esc(cur.message) +
      '</div><div class="lkem-ft">Depuis ' + hm(cur.startedAt) + (cur.test ? ' · s\'arrête dans 30 s' : '') + '</div><div class="lkem-lost"></div>';
    place();
  }

  function tick() {
    if (!cur) return;
    if (cur.until && hooks.now() >= cur.until) { apply(null); return; }   // l'essai s'arrête seul
    if (!acked || acked !== cur.id) sendAck();
    var lost = el && el.querySelector('.lkem-lost');
    if (!lost) return;
    var off = Date.now() - lastOk > LOST_AFTER_MS;
    lost.style.display = off ? 'block' : 'none';
    if (off) lost.textContent = 'Connexion perdue depuis ' + hm(lastOk);
  }

  function sendAck() {
    var id = cur && cur.id;
    if (!id || !hooks.ack) return;
    hooks.ack(id).then(function () { acked = id; }, function () {});
  }

  function save() {
    try { if (cur) localStorage.setItem(KEY, JSON.stringify(cur)); else localStorage.removeItem(KEY); } catch (e) {}
  }

  /* e : { id, title, message, startedAt, test, until } ou null (rien à afficher). Appelé à chaque sondage du player et à chaque message temps réel. */
  function apply(e) {
    if (!hooks || hooks.preview) return;
    if (e && e.until && hooks.now() >= e.until) e = null;
    if (!e) {
      if (!cur) return;
      cur = null; acked = null; clearTimeout(endTimer); endTimer = null;
      if (el && el.parentNode) el.parentNode.removeChild(el);
      el = null;
      save();
      hooks.leave();
      return;
    }
    if (cur && cur.id === e.id) { if (!acked) sendAck(); return; }
    var first = !cur;
    cur = { id: e.id, title: e.title || '', message: e.message || '', startedAt: e.startedAt || Date.now(), test: !!e.test, until: e.until || 0 };
    acked = null;
    clearTimeout(endTimer);
    if (cur.until) endTimer = setTimeout(function () { apply(null); }, Math.max(0, cur.until - hooks.now()));
    render();
    save();
    if (first) hooks.enter();
    sendAck();
  }

  window.LinkiiEmergency = {
    /* h : { stage, preview, cell(), wall(), now(), enter(), leave(), ack(id) } */
    init: function (h) {
      hooks = h;
      if (!ticker && !h.preview) ticker = setInterval(tick, 1000);
    },
    apply: apply,
    place: place,
    active: function () { return !!cur; },
    /* le serveur a répondu : la connexion est bonne */
    reachable: function () { lastOk = Date.now(); },
    /* au démarrage : l'urgence connue avant l'arrêt (le player peut démarrer hors ligne) */
    restore: function () {
      try { var s = JSON.parse(localStorage.getItem(KEY) || 'null'); if (s && s.id) apply(s); } catch (e) {}
    }
  };
})();
