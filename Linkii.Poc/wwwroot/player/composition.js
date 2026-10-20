/* Compositions (DEV-POC.md §80) : rendu commun à l'éditeur du back-office (compo-editor.js) et au player (app « composition »).
   JS volontairement "ancien" comme apps.js (var, pas de fonctions fléchées) : il tourne aussi dans la WebView Android.
   Une composition : { w, h, bg, els: [ … ] }, positions et tailles en pixels de la page ; l'appelant met la scène à l'échelle.
   Calques : text | shape (rect, circle, line, arrow) | image (media : identifiant ; src : adresse posée à la publication)
             | qr (url ; qr : « côté|tracé » calculé par le serveur) | widget (clock, date, ticker, countdown).
   Toutes les valeurs venant du document sont filtrées ici (couleurs, polices, nombres) : rien n'est injecté tel quel. */
(function (w) {
  'use strict';
  var FONTS = ['IBM Plex Sans', 'Montserrat', 'Poppins', 'Playfair Display', 'Oswald', 'Arial'];
  // Polices embarquées (player/fonts, licence OFL) : servies par Linkii et gardées par le Service Worker du player, donc lisibles hors ligne.
  var FONT_CSS = '/player/fonts/compo-fonts.css';
  var CSS = '.lc-stage{position:absolute;left:0;top:0;transform-origin:0 0;overflow:hidden}' +
    '.lc-el{position:absolute;box-sizing:border-box}' +
    '.lc-tx{white-space:pre-wrap;word-wrap:break-word;outline:none}' +
    '.lc-img{width:100%;height:100%;background-position:center;background-repeat:no-repeat}' +
    '.lc-img.empty{background:repeating-linear-gradient(45deg,#dfe5ec 0 16px,#eef2f6 16px 32px);display:flex;align-items:center;justify-content:center;text-align:center;color:#5A6B80;font:500 26px "IBM Plex Sans",sans-serif}' +
    '.lc-qr{width:100%;height:100%;box-sizing:border-box}.lc-qr svg{display:block;width:100%;height:100%}' +
    '.lc-tk{width:100%;height:100%;display:flex;align-items:center;overflow:hidden;white-space:nowrap}' +
    '.lc-tkin{display:inline-flex;animation:lc-tk linear infinite}.lc-tkin span{padding-right:2em}' +
    '@keyframes lc-tk{from{transform:translateX(0)}to{transform:translateX(-50%)}}';

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

  // QR code : tracé du serveur ; en attendant (éditeur), un motif neutre
  function qrSvg(e) {
    var fg = col(e.fg, '#000000'), parts = (e.qr || '').split('|'), n = parseInt(parts[0], 10), path = parts[1] || '';
    if (n > 0 && /^[0-9MhvzH\- ]+$/.test(path))
      return '<svg viewBox="0 0 ' + n + ' ' + n + '" shape-rendering="crispEdges"><path d="' + path + '" fill="' + fg + '"/></svg>';
    return '<svg viewBox="0 0 10 10"><rect width="10" height="10" fill="none" stroke="' + fg + '" stroke-width=".4" stroke-dasharray=".8 .6"/><text x="5" y="6" font-size="2" text-anchor="middle" fill="' + fg + '">QR</text></svg>';
  }

  function inner(e, o) {
    var t = e.type;
    if (t === 'text') return '<div class="lc-tx" style="' + txs(e) + '">' + esc(e.text) + '</div>';
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
    st.innerHTML = C.html(doc, { player: true, tz: tz });
    el.appendChild(st);
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
