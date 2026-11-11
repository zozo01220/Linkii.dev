/* Bandeaux haut et bas de l'écran : trois cases (gauche, centre, droite) fixes, quelle que soit la liste jouée (maquettes/ecran-bandeaux.html).
   La scène d'un mur est l'image de tout le mur : un bandeau posé sur la scène court sur tous les écrans (« un seul fil »).
   Hauteur = part du petit côté d'UN écran (jamais de tout le mur) : le même bandeau sur un téléviseur et sur un totem.
   player.js prête : la scène, la taille d'un écran, l'heure du serveur, le fuseau et makeApp (météo, flux, agenda, donnée, QR, compte à rebours).
   Le bandeau réserve sa place : player.js réduit la zone de contenu de sa hauteur. Un message « place = banner » prend la place de la case réceptrice.
   JS volontairement « ancien » (var, pas d'async) pour les WebView anciennes. */
(function () {
  'use strict';

  var CSS_ID = 'lkbn-css';
  var hooks = null, cur = null, sig = '', root = null, stops = [], timers = [], notice = null;
  var H = { top: 0, bottom: 0 };

  function css() {
    if (document.getElementById(CSS_ID)) return;
    var st = document.createElement('style');
    st.id = CSS_ID;
    st.textContent =
      '#bands{position:absolute;left:0;top:0;right:0;bottom:0;z-index:6;pointer-events:none;font-family:"IBM Plex Sans","Segoe UI",Arial,sans-serif}' +
      '#bands .bn{position:absolute;left:0;right:0;display:grid;grid-template-columns:minmax(0,1fr) minmax(0,2fr) minmax(0,1fr);line-height:1.15;color:#fff;background:#0B1F3A;overflow:hidden}' +
      '#bands .bn.top{top:0}#bands .bn.bottom{bottom:0}' +
      '#bands .bn.bg-blanc{background:#fff;color:#0B1F3A}#bands .bn.bg-signal{background:#2F6FAD}#bands .bn.bg-noir{background:#000}' +
      '#bands .bc{position:relative;display:flex;align-items:center;gap:.5em;padding:0 .85em;min-width:0;white-space:nowrap;overflow:hidden}' +
      '#bands.port .bn{grid-template-columns:minmax(0,1fr) minmax(0,1.3fr) minmax(0,1fr)}#bands.port .bd-date{display:none}' +
      '#bands .bc.center{justify-content:center}' +
      '#bands .bc+.bc{border-left:1px solid rgba(255,255,255,.16)}#bands .bn.bg-blanc .bc+.bc{border-left-color:#DDE3EA}' +
      '#bands .bc .widget{position:relative;background:none;border:0;padding:0;gap:.5em;font-size:1em;color:inherit;min-width:0;flex:1 1 auto;justify-content:inherit}' +
      '#bands .bc .widget .time,#bands .bc .widget .temp,#bands .bc .widget .city{font-size:1em}' +
      '#bands .bc .widget .date,#bands .bc .widget .rng{color:inherit;opacity:.7;font-size:1em}' +
      '#bands .bc .widget .ico{font-size:1.15em}#bands .bc .widget .info{flex-direction:row;align-items:center;gap:.4em}#bands .bc .widget .rng{display:none}' +
      '#bands .bc .k-qrcode{flex-direction:row;background:none;padding:0;border:0}#bands .bc .k-qrcode svg{width:1.7em;height:1.7em;background:#fff;border-radius:.15em;padding:.1em}#bands .bc .k-qrcode .q-l{font-size:1em;max-width:none;text-align:left;white-space:nowrap;margin:0}' +
      '#bands .bc[data-kind=clockwx] .city{display:none}' +
      '#bands .bc .widget{flex-direction:row!important;align-items:center!important;white-space:nowrap!important;text-align:left!important;gap:.5em!important}#bands .bc .widget *{font-size:1em!important;line-height:1.15!important;margin:0!important;padding:0!important;color:inherit;flex:none}#bands .bc .widget{flex:none!important;min-width:max-content!important}#bands .bc .widget .ico{font-size:1.15em!important}#bands .bc .widget [class*="-l"],#bands .bc .widget .cd-u{opacity:.7}#bands .bc .widget .cd-n{font-weight:600}#bands .bc .widget .cd-u:empty{display:none!important}' +
      '#bands .bc .tk{font-size:1em;line-height:1.15;width:auto;flex:1;min-width:0}#bands .bc .tk em{font-size:1em}#bands .bc .tk b,#bands .bc .tk .tk-h{color:inherit;font-weight:600}#bands .bc .tk .sep{margin:0 .9em}' +
      '#bands .bd-mq{flex:1;min-width:0;overflow:hidden;display:flex;align-items:center;-webkit-mask-image:linear-gradient(90deg,transparent,#000 4%,#000 96%,transparent);mask-image:linear-gradient(90deg,transparent,#000 4%,#000 96%,transparent)}' +
      '#bands .bd-mqi{display:flex;align-items:center;gap:.5em;flex:none;white-space:nowrap;padding-left:100%;animation:lkbn-tk var(--dur,12s) linear infinite}' +
      '#bands .bc svg{width:1.1em;height:1.1em;flex:none}' +
      '#bands .bd-time{font-weight:500;font-variant-numeric:tabular-nums;letter-spacing:-.01em}' +
      '#bands .bd-dim{opacity:.7}#bands .bd-sepd{opacity:.35}' +
      '#bands .bd-logo{display:flex;align-items:center;gap:.45em;font-weight:500}' +
      '#bands .bd-logo img{height:1.7em;width:auto;max-width:6em;object-fit:contain;display:block}' +
      '#bands .bd-logo .lg{display:grid;place-items:center;width:1.55em;height:1.55em;border-radius:.3em;background:#fff;color:#0B1F3A;font-weight:600;font-size:.95em}' +
      '#bands .bn.bg-blanc .bd-logo .lg{background:#0B1F3A;color:#fff}' +
      '#bands .bd-tag{display:inline-flex;align-items:center;padding:.18em .55em;border-radius:.25em;background:#E8751A;color:#fff;font-weight:600;font-size:.82em;letter-spacing:.06em;text-transform:uppercase}' +
      '#bands .bd-tag.c-signal{background:#3B82C4}#bands .bd-tag.c-rouge{background:#C8372D}' +
      '#bands .bd-tk{flex:1;min-width:0;overflow:hidden;-webkit-mask-image:linear-gradient(90deg,transparent,#000 3%,#000 97%,transparent);mask-image:linear-gradient(90deg,transparent,#000 3%,#000 97%,transparent)}' +
      '#bands .bd-tk>span{display:inline-block;padding-left:100%;animation:lkbn-tk var(--dur,26s) linear infinite}' +
      '#bands .bd-tk i{font-style:normal;opacity:.45;margin:0 .9em}' +
      '@keyframes lkbn-tk{to{transform:translateX(-100%)}}' +
      '#bands .bd-txt{overflow:hidden;text-overflow:ellipsis}' +
      '#bands .bd-dot{width:.55em;height:.55em;border-radius:50%;background:#3CC48A;flex:none}' +
      '#bands .bd-qr{height:1.5em;width:auto;background:#fff;border-radius:.15em;padding:.12em;flex:none}' +
      '#bands .bc.ha-on{background:#FFF4DC;color:#6B4400;animation:lkbn-in .35s ease}#bands .bc.ha-on.information{background:#fff;color:#0B1F3A}' +
      '#bands .bc.ha-on b{font-weight:600}' +
      '#bands .bc.ha-on .bd-hbar{position:absolute;left:0;bottom:0;height:.16em;background:#B7791F}#bands .bc.ha-on.information .bd-hbar{background:#3B82C4}' +
      '@keyframes lkbn-in{from{filter:brightness(1.6)}to{filter:none}}';
    document.head.appendChild(st);
  }

  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
  function S(c, k, d) { var v = c.s && c.s[k]; return v == null || v === '' ? d : v; }
  function tzOpts(o) { var tz = hooks.tz && hooks.tz(); if (tz) o.timeZone = tz; return o; }
  function nowTxt() { try { return new Date(hooks.now()).toLocaleTimeString('fr-CH', tzOpts({ hour: '2-digit', minute: '2-digit' })); } catch (e) { return ''; } }
  function dayTxt() { try { return new Date(hooks.now()).toLocaleDateString('fr-CH', tzOpts({ weekday: 'short', day: 'numeric', month: 'short' })); } catch (e) { return ''; } }
  var SUN = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.41-1.41M17.66 6.34l1.41-1.41"/></svg>';

  /* Texte défilant : le plus long des messages en boucle (un mur : plus lent, le fil est plus long) */
  function tickerHtml(items, speed) {
    var txt = items.map(esc).join('<i>•</i>'), len = items.join(' ').length;
    var f = { lente: 1.6, normal: 1, rapide: .6 }[speed || 'normal'] || 1, w = hooks.wall && hooks.wall();
    var dur = Math.round((10 + len * .2) * f * (w && w.cols > 1 ? 1 + .35 * (w.cols - 1) : 1));
    return '<span class="bd-tk" style="--dur:' + dur + 's"><span>' + (txt || 'Aucun message') + '</span></span>';
  }

  function drawCell(el, c) {
    var k = c.k, wxItem = c.item;
    function app(item) {
      var w = hooks.makeApp(item, el.getAttribute('data-place'));
      if (!w) return;
      stops.push(w.stop);
      el.appendChild(w.el);
    }
    switch (k) {
      case 'text':
        el.innerHTML = '<span class="bd-txt">' + esc(S(c, 'text', '')) + '</span>';
        timers.push(setTimeout(function () {   // plus long que la case : il défile tout seul
          var t = el.firstChild;
          if (t && t.scrollWidth > t.clientWidth + 1) el.innerHTML = tickerHtml([S(c, 'text', '')], 'normal');
        }, 80));
        break;
      case 'ticker': el.innerHTML = tickerHtml(String(S(c, 'items', '')).split('\n').map(function (x) { return x.trim(); }).filter(Boolean), S(c, 'speed', 'normal')); break;
      case 'label': el.innerHTML = '<span class="bd-tag c-' + esc(S(c, 'color', 'orange')) + '">' + esc(S(c, 'text', '')) + '</span>'; break;
      case 'logo':
        el.innerHTML = '<span class="bd-logo">' + (c.img ? '<img alt="" src="' + esc(c.img) + '">' : '<span class="lg">' + esc((S(c, 'text', 'L')).charAt(0).toUpperCase()) + '</span>') + esc(S(c, 'text', '')) + '</span>';
        break;
      case 'clock': case 'clockwx':
        el.innerHTML = '<b class="bd-time"></b><span class="bd-dim bd-date"></span>';
        var tick = function () { var b = el.querySelector('.bd-time'), d = el.querySelector('.bd-date'); if (b) b.textContent = nowTxt(); if (d) d.textContent = dayTxt(); };
        tick(); timers.push(setInterval(tick, 10000));
        if (k === 'clockwx' && wxItem) { el.insertAdjacentHTML('beforeend', '<span class="bd-sepd">|</span>'); app(wxItem); }
        break;
      default:   // météo, flux, agenda, donnée en direct, compte à rebours, QR code : l'app du catalogue, en compact
        if (wxItem) app(wxItem);
    }
  }

  function clear() {
    for (var i = 0; i < stops.length; i++) { try { stops[i](); } catch (e) {} }
    for (var j = 0; j < timers.length; j++) { clearTimeout(timers[j]); clearInterval(timers[j]); }
    stops = []; timers = [];
    if (root && root.parentNode) root.parentNode.removeChild(root);
    root = null;
  }

  function build() {
    clear();
    if (!cur) { H.top = H.bottom = 0; return; }
    css();
    root = document.createElement('div');
    root.id = 'bands';
    ['top', 'bottom'].forEach(function (pos) {
      var b = cur[pos];
      if (!b) return;
      var bn = document.createElement('div');
      bn.className = 'bn ' + pos + ' bg-' + b.bg;
      ['left', 'center', 'right'].forEach(function (cp) {
        var c = b.cells[cp], el = document.createElement('div');
        el.className = 'bc ' + cp;
        el.setAttribute('data-place', pos);
        el.setAttribute('data-cell', pos + '-' + cp);
        if (c) el.setAttribute('data-kind', c.k);
        if (c) { if (c.ha) el.setAttribute('data-ha', '1'); drawCell(el, c); }
        bn.appendChild(el);
      });
      root.appendChild(bn);
    });
    hooks.stage.appendChild(root);
    place();
    fitCells(); timers.push(setTimeout(fitCells, 600)); timers.push(setInterval(fitCells, 2500));
    if (notice) showNotice(notice);
  }

  /* Une case dont le contenu dépasse défile : il est mis dans une piste qui traverse la case */
  function fitCells() {
    if (!root) return;
    var cs = root.querySelectorAll('.bc');
    for (var i = 0; i < cs.length; i++) {
      var el = cs[i], kd = el.getAttribute('data-kind'), w = el.firstChild, key = el.clientWidth + '|' + el.style.fontSize + '|' + (el.parentNode.style.fontSize || '') + '|' + el.textContent.length;
      if (el._fit === key && !el._dirty) continue;   // rien n'a changé : le défilement continue sans à-coup
      el._fit = key;
      if (w && w.className === 'bd-mq') {   // rendre le contenu avant de mesurer
        var inn = w.firstChild;
        while (inn.firstChild) el.insertBefore(inn.firstChild, w);
        el.removeChild(w);
      }
      if (!kd || kd === 'ticker' || el.classList.contains('ha-on')) continue;
      if (el.scrollWidth > el.clientWidth + 1) {
        var mq = document.createElement('div'), mi = document.createElement('div');
        mq.className = 'bd-mq'; mi.className = 'bd-mqi';
        while (el.firstChild) mi.appendChild(el.firstChild);
        mq.appendChild(mi); el.appendChild(mq);
        mi.style.setProperty('--dur', Math.round(8 + (mi.scrollWidth - el.clientWidth) / Math.max(40, el.clientWidth) * 6 + mi.scrollWidth / 60) + 's');
      }
    }
  }

  /* Taille et position : la scène est tout le mur, la hauteur se calcule sur la case d'un écran */
  function place() {
    if (!root || !hooks) return;
    var c = hooks.cell(), short = Math.min(c.w, c.h);
    root.className = c.h > c.w ? 'port' : '';
    ['top', 'bottom'].forEach(function (pos) {
      var b = cur && cur[pos], bn = root.querySelector('.bn.' + pos);
      if (!b || !bn) { H[pos] = 0; return; }
      var h = Math.round((b.h || .09) * short);
      bn.style.height = h + 'px';
      bn.style.fontSize = (h * 0.4) + 'px';
      H[pos] = h;
    });
    fitCells();
  }

  /* Message d'API / Home Assistant dans la case réceptrice : prend la place du contenu, avec la barre du temps restant, puis le fil reprend */
  function receiver() { return root ? root.querySelector('.bc[data-ha="1"]') : null; }

  function showNotice(n) {
    var el = receiver();
    if (!el) return;
    if (!el.hasAttribute('data-saved')) { el.setAttribute('data-saved', '1'); el.__saved = [].slice.call(el.childNodes); }
    el.textContent = '';
    el.classList.add('ha-on', n.look === 'attention' ? 'attention' : 'information');
    el.classList.toggle('information', n.look !== 'attention');
    el.classList.toggle('attention', n.look === 'attention');
    var ic = n.look === 'attention'
      ? '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="M10.3 3.9L1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z"/><path d="M12 9v4M12 17h.01"/></svg>'
      : '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M12 16v-4M12 8h.01"/></svg>';
    el.innerHTML = ic + (n.title ? '<b>' + esc(n.title) + '</b>' : '') + (n.message ? '<span class="bd-dim">' + (n.title ? '· ' : '') + esc(n.message) + '</span>' : '') + '<i class="bd-hbar"></i>';
    var bar = el.querySelector('.bd-hbar'), left = Math.max(0, n.until - hooks.now()), total = Math.max(1, n.until - n.startedAt);
    bar.style.width = Math.min(100, left * 100 / total) + '%';
    setTimeout(function () { bar.style.transition = 'width ' + (left / 1000) + 's linear'; bar.style.width = '0%'; }, 50);
  }

  function hideNotice() {
    var el = receiver();
    if (!el || !el.hasAttribute('data-saved')) return;
    el.textContent = '';
    (el.__saved || []).forEach(function (x) { el.appendChild(x); });
    el.removeAttribute('data-saved'); el.__saved = null;
    el.classList.remove('ha-on', 'attention', 'information');
  }

  window.LinkiiBands = {
    /* h : { stage, preview, cell(), wall(), now(), tz(), makeApp(item, place) } */
    init: function (h) { hooks = h; },
    /* b : { top, bottom, hasHa } ou null (aucun bandeau). Redessiné seulement s'il a changé. */
    apply: function (b) {
      if (!hooks) return;
      var s = b ? JSON.stringify(b) : '';
      if (s === sig) { place(); return; }
      sig = s; cur = b || null;
      build();
    },
    place: place,
    height: function (pos) { return H[pos] || 0; },
    has: function (pos) { return !!(cur && cur[pos]); },
    hasReceiver: function () { return !!(cur && cur.hasHa && root && receiver()); },
    notice: function (n) { notice = n; if (n) showNotice(n); else hideNotice(); }
  };
})();
