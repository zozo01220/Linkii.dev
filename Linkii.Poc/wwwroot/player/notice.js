/* Message affiché un moment (API, Home Assistant : « Afficher un message », §224, maquettes/ha-actions-contenu.html).
   Plein écran par-dessus la diffusion : titre, message, fine barre du temps restant. Deux aspects : « information » (clair) et « attention » (ambre).
   Il se retire seul à l'échéance (heure du serveur), ou dès que le serveur dit qu'il n'y en a plus. L'urgence passe devant ; un écran en veille ne l'affiche pas
   (sauf si le message a réveillé l'écran). JS volontairement « ancien » (var, pas d'async) pour les WebView anciennes.
   player.js lui prête : la scène, l'heure du serveur, l'arrêt et la reprise des contenus. */
(function () {
  'use strict';

  var CSS_ID = 'lknt-css';
  var el = null, cur = null, hooks = null, ticker = null, endTimer = null;

  function css() {
    if (document.getElementById(CSS_ID)) return;
    var st = document.createElement('style');
    st.id = CSS_ID;
    st.textContent =
      '#noticeview{position:absolute;z-index:99000;display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;box-sizing:border-box;overflow:hidden;cursor:none;font-family:"IBM Plex Sans","Segoe UI",Arial,sans-serif;background:#fff;color:#0B1F3A}' +
      '#noticeview.attention{background:#FFF4DC;color:#6B4400}' +
      '#noticeview .lknt-ti{font-size:8em;font-weight:500;line-height:1.1;max-width:88%;word-wrap:break-word}' +
      '#noticeview .lknt-me{font-size:4.6em;line-height:1.3;max-width:84%;margin-top:.9em;opacity:.78;word-wrap:break-word;white-space:pre-line}' +
      '#noticeview .lknt-ti:empty,#noticeview .lknt-me:empty{display:none}' +
      '#noticeview .lknt-ti:empty+.lknt-me{margin-top:0;font-size:7em;opacity:1}' +
      '#noticeview.long .lknt-ti{font-size:6em}#noticeview.long .lknt-me{font-size:3.6em}' +
      '#noticeview.port .lknt-ti{font-size:10em}#noticeview.port .lknt-me{font-size:6em}#noticeview.port.long .lknt-ti{font-size:8em}#noticeview.port.long .lknt-me{font-size:4.8em}' +
      '#noticeview .lknt-bar{position:absolute;left:0;right:0;bottom:0;height:.7em;background:rgba(11,31,58,.1)}' +
      '#noticeview .lknt-bar i{display:block;height:100%;width:100%;background:#3B82C4}' +
      '#noticeview.attention .lknt-bar i{background:#B7791F}';
    document.head.appendChild(st);
  }

  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

  /* Position et taille : la case de cet écran dans la scène (un mur : le même message complet sur chaque écran). 1em = 1 % du petit côté de l'écran. */
  function place() {
    if (!el || !hooks) return;
    var c = hooks.cell(), w = hooks.wall(), st = el.style;
    if (w && w.cols > 0 && !w.whole) { st.left = (w.col * c.w) + 'px'; st.top = (w.row * c.h) + 'px'; st.width = c.w + 'px'; st.height = c.h + 'px'; st.right = st.bottom = 'auto'; }
    else { st.left = st.top = st.right = st.bottom = '0'; st.width = st.height = ''; }
    st.fontSize = (Math.min(c.w, c.h) / 100) + 'px';
    el.className = (cur ? cur.look : '') + (c.h > c.w ? ' port' : '') + (cur && (cur.message.length > 110 || cur.title.length > 28) ? ' long' : '');
  }

  function render() {
    if (!el) {
      css();
      el = document.createElement('div');
      el.id = 'noticeview';
      el.setAttribute('role', 'status');
      var stop = function (ev) { ev.stopPropagation(); ev.preventDefault(); };
      ['click', 'mousedown', 'mouseup', 'touchstart', 'touchend', 'pointerdown', 'pointerup', 'contextmenu', 'wheel'].forEach(function (n) { el.addEventListener(n, stop, { passive: false }); });
      hooks.stage.appendChild(el);
    }
    el.innerHTML = '<div class="lknt-ti">' + esc(cur.title) + '</div><div class="lknt-me">' + esc(cur.message) + '</div><div class="lknt-bar"><i></i></div>';
    place();
    var left = Math.max(0, cur.until - hooks.now()), total = Math.max(1, cur.until - cur.startedAt), bar = el.querySelector('.lknt-bar i');
    bar.style.width = Math.min(100, left * 100 / total) + '%';
    setTimeout(function () { if (!bar.parentNode) return; bar.style.transition = 'width ' + (left / 1000) + 's linear'; bar.style.width = '0%'; }, 50);
  }

  function clear() {
    clearTimeout(endTimer); endTimer = null;
    if (ticker) { clearInterval(ticker); ticker = null; }
    cur = null;
    if (el && el.parentNode) el.parentNode.removeChild(el);
    el = null;
  }

  /* L'écran s'est endormi sans que le message l'ait réveillé : on le retire ; l'urgence, elle, l'a déjà remplacé. */
  function tick() {
    if (!cur) return;
    if (hooks.now() >= cur.until || !hooks.canShow(cur)) apply(null);
  }

  /* n : { id, title, message, look, wake, startedAt, until } ou null (rien à afficher). Appelé à chaque sondage du player et à chaque message temps réel. */
  function apply(n) {
    if (!hooks || hooks.preview) return;
    if (n && n.until && hooks.now() >= n.until) n = null;
    if (!n) {
      if (!cur) return;
      clear();
      hooks.leave();
      return;
    }
    if (cur && cur.id === n.id) return;
    if (!hooks.canShow(n)) return;
    var first = !cur;
    cur = { id: n.id, title: n.title || '', message: n.message || '', look: n.look === 'attention' ? 'attention' : 'information', wake: !!n.wake, startedAt: n.startedAt || hooks.now(), until: n.until || (hooks.now() + 30000) };
    clearTimeout(endTimer);
    endTimer = setTimeout(function () { apply(null); }, Math.max(0, cur.until - hooks.now()));
    if (!ticker) ticker = setInterval(tick, 1000);
    render();
    if (first) hooks.enter(cur);
  }

  window.LinkiiNotice = {
    /* h : { stage, preview, cell(), wall(), now(), enter(), leave(), canShow(n) } */
    init: function (h) { hooks = h; },
    apply: apply,
    place: place,
    active: function () { return !!cur; },
    /* l'urgence prend la place : on retire le message sans rendre la main à la diffusion */
    drop: function () { if (cur) clear(); }
  };
})();
