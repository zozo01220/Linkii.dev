/* Simulateur d'écrans du back-office : le player des écrans, en mode aperçu, dans une fenêtre intégrée réduite.
   - Un écran : la même publication, au même moment s'il est synchronisé (heure commune).
   - Un mur : le player dessine tout le mur en une seule image ; les cadres et numéros des écrans sont tracés par-dessus.
   - Une liste de lecture : son état actuel, sur un format choisi, éventuellement à une date choisie.
   Le player envoie son état (contenu affiché, temps restant) ; la frise, la pause et « suivant » lui renvoient des commandes. */
(function (w) {
  'use strict';

  var ICO = {
    prev: '<path d="M6 5v14M19 5L9 12l10 7z"/>',
    next: '<path d="M18 5v14M5 5l10 7-10 7z"/>',
    play: '<path d="M7 4l13 8-13 8z"/>',
    pause: '<path d="M8 5v14M16 5v14"/>',
    muted: '<path d="M4 9h4l5-4v14l-5-4H4zM17 9l5 6M22 9l-5 6"/>',
    sound: '<path d="M4 9h4l5-4v14l-5-4H4zM17 8a5 5 0 0 1 0 8M19.5 5.5a8.5 8.5 0 0 1 0 13"/>',
    full: '<path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/>'
  };
  function ico(n) { return '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + ICO[n] + '</svg>'; }
  function esc(s) { var d = document.createElement('div'); d.textContent = s == null ? '' : s; return d.innerHTML; }
  function mmss(ms) { var s = Math.max(0, Math.floor(ms / 1000)); return Math.floor(s / 60) + ':' + ('0' + s % 60).slice(-2); }
  function day(iso, o) { try { return new Date(iso + 'T12:00:00').toLocaleDateString('fr-CH', o); } catch (e) { return iso; } }
  function period(i) {
    if (i.from && i.to) return 'du ' + day(i.from, { day: 'numeric', month: 'short' }) + ' au ' + day(i.to, { day: 'numeric', month: 'short' });
    if (i.from) return 'à partir du ' + day(i.from, { day: 'numeric', month: 'short' });
    return i.to ? "jusqu'au " + day(i.to, { day: 'numeric', month: 'short' }) : '';
  }

  function Sim(host, o) {
    this.host = host; this.o = {}; this.st = null; this.at = 0; this.sound = false;
    host.innerHTML =
      '<div class="sim-box"><div class="sim-frame"><iframe title="Aperçu de l\'écran" allow="autoplay; fullscreen"></iframe><div class="sim-grid" aria-hidden="true"></div></div></div>' +
      '<div class="sim-bar">' +
        '<button type="button" class="sim-b" data-a="prev" title="Contenu précédent" aria-label="Contenu précédent">' + ico('prev') + '</button>' +
        '<button type="button" class="sim-b" data-a="play" title="Pause" aria-label="Pause">' + ico('pause') + '</button>' +
        '<button type="button" class="sim-b" data-a="next" title="Contenu suivant" aria-label="Contenu suivant">' + ico('next') + '</button>' +
        '<span class="badge sim-state"></span><button type="button" class="sim-link" data-a="live"></button>' +
        '<span class="sim-grow"></span><span class="sim-time"></span>' +
        '<button type="button" class="sim-b txt" data-a="sound"></button>' +
        '<button type="button" class="sim-b txt" data-a="full">' + ico('full') + ' Plein écran</button>' +
      '</div><div class="sim-tl" role="list" aria-label="Contenus de la boucle"></div><div class="sim-note"></div>';
    this.frame = host.querySelector('iframe');
    var self = this;
    host.querySelector('.sim-bar').addEventListener('click', function (e) {
      var b = e.target.closest('[data-a]');
      if (b) self.act(b.getAttribute('data-a'));
    });
    host.querySelector('.sim-tl').addEventListener('click', function (e) {
      var b = e.target.closest('[data-k]');
      if (b) self.send({ cmd: 'goto', index: +b.getAttribute('data-k') });
    });
    this.onMsg = function (e) {
      if (e.source !== self.frame.contentWindow || e.origin !== location.origin || !e.data || e.data.lk !== 'sim') return;
      self.st = e.data; self.at = Date.now(); self.render();
    };
    w.addEventListener('message', this.onMsg);
    this.timer = setInterval(function () {
      if (!self.host.isConnected) { self.dispose(); host.__sim = null; return; }   // fenêtre fermée sans démontage
      self.tick();
    }, 200);
    this.update(o);
  }

  Sim.prototype.send = function (m) {
    m.lk = 'sim';
    try { this.frame.contentWindow.postMessage(m, location.origin); } catch (e) {}
  };

  Sim.prototype.act = function (a) {
    var st = this.st;
    if (a === 'play') this.send({ cmd: st && st.paused ? 'play' : 'pause' });
    else if (a === 'prev' || a === 'next') this.send({ cmd: a });
    else if (a === 'live') this.send(this.o.mode === 'try' ? { cmd: 'goto', index: 0 } : { cmd: 'live' });
    else if (a === 'sound') { this.sound = !this.sound; this.load(); }
    else if (a === 'full') {
      var box = this.host.querySelector('.sim-box'), rq = box.requestFullscreen || box.webkitRequestFullscreen;
      if (rq) { try { var p = rq.call(box); if (p && p.catch) p.catch(function () {}); } catch (e) {} }
    }
  };

  Sim.prototype.load = function () {
    var src = this.o.src + (this.sound ? '&sound=1' : '');
    if (this.frame.getAttribute('src') !== src) { this.st = null; this.frame.setAttribute('src', src); this.render(); }
    var b = this.host.querySelector('[data-a=sound]');
    b.innerHTML = this.sound ? ico('sound') + ' Son activé' : ico('muted') + ' Son coupé';
    b.title = this.sound ? 'Couper le son' : 'Activer le son (la lecture reprend)';
  };

  // opts : src, cols, rows, ratio (largeur / hauteur d'un écran), mode (live | try), view (img | sep), bezel (px), nums, real, cells [{state, title}]
  Sim.prototype.update = function (o) {
    this.o = o;
    var cols = Math.max(1, o.cols || 1), rows = Math.max(1, o.rows || 1), n = cols * rows;
    var ratio = (o.ratio || 16 / 9) * cols / rows;
    var f = this.host.querySelector('.sim-frame');
    f.style.aspectRatio = String(ratio);
    f.style.setProperty('--ratio', String(ratio));
    var g = this.host.querySelector('.sim-grid');
    g.className = 'sim-grid' + (n > 1 && o.view === 'sep' ? ' sep' : '');
    g.style.gridTemplateColumns = 'repeat(' + cols + ', 1fr)';
    g.style.setProperty('--bz', (n > 1 ? (o.view === 'sep' ? 12 : o.bezel || 0) : 0) + 'px');
    var html = '';
    if (n > 1) for (var i = 0; i < n; i++) {
      var c = (o.cells || [])[i] || {}, mask = o.real && c.state && c.state !== 'ok';
      html += '<span class="sim-cell" title="' + esc(c.title || '') + '">' + (o.nums ? '<span class="sim-num">' + (i + 1) + '</span>' : '') +
        (mask ? '<span class="sim-mask"><b>' + (c.state === 'empty' ? 'Place libre' : 'Hors ligne') + '</b></span>' : '') + '</span>';
    }
    g.innerHTML = html;
    this.load();
  };

  Sim.prototype.render = function () {
    var st = this.st, live = this.o.mode !== 'try', h = this.host;
    var badge = h.querySelector('.sim-state'), link = h.querySelector('[data-a=live]'), play = h.querySelector('[data-a=play]');
    play.innerHTML = ico(st && st.paused ? 'play' : 'pause');
    play.title = st && st.paused ? 'Reprendre' : 'Pause';
    play.setAttribute('aria-label', play.title);
    var b = !st ? ['neutre', 'Chargement…', ''] :
      st.paused ? ['free', live ? "En pause · l'écran continue" : 'En pause', ''] :
      st.live ? ['live', "En direct · comme l'écran", "Même contenu, au même moment, que l'écran (lecture synchronisée)"] :
      st.synced ? ['free', 'Lecture libre', ''] :
      live ? ['neutre', 'Liste publiée', "L'écran n'est pas synchronisé : il joue la même liste, à son propre rythme"] : ['neutre', 'Essai', ''];
    badge.className = 'badge sim-state ' + b[0];
    badge.textContent = b[1];
    badge.title = b[2];
    link.textContent = !st ? '' : live ? (st.synced && !st.live ? 'Revenir au direct' : '') : 'Recommencer';

    var tl = h.querySelector('.sim-tl'), note = h.querySelector('.sim-note');
    if (!st) { tl.innerHTML = ''; note.textContent = ''; return; }
    tl.innerHTML = st.items.length ? st.items.map(function (it, k) {
      var flex = it.content ? Math.min(it.sec, 60) : it.sec;
      return '<button type="button" role="listitem" class="sim-seg' + (k === st.index ? ' on' : '') + '" data-k="' + k + '" style="flex:' + Math.max(1, flex) + ' 1 0" title="' + esc(it.name) + '">' +
        '<span class="sim-fill"></span><span class="sim-lbl"><i style="background:' + esc(it.color) + '"></i>' + esc(it.name) + '</span>' +
        '<span class="sim-d">' + (it.content ? "jusqu'à la fin" : it.sec + ' s') + '</span></button>';
    }).join('') : '<span class="sim-empty">Aucun contenu à diffuser à ce moment.</span>';

    var parts = [];
    if (this.o.mode === 'try' && st.today)
      parts.push(esc(day(st.today, { weekday: 'long', day: 'numeric', month: 'long' }).replace(/^./, function (c) { return c.toUpperCase(); })) + ' : <b>' +
        st.items.length + ' contenu' + (st.items.length > 1 ? 's' : '') + ' joué' + (st.items.length > 1 ? 's' : '') + '</b>' +
        (st.skipped.length ? ', ' + st.skipped.length + ' sauté' + (st.skipped.length > 1 ? 's' : '') + ' (hors de sa période)' : '') + '.');
    st.skipped.forEach(function (i) { parts.push('Sauté à cette date : <s>' + esc(i.name) + '</s>' + (period(i) ? ' · valable ' + period(i) : '')); });
    note.innerHTML = parts.join('<br>');
    this.tick();
  };

  Sim.prototype.tick = function () {
    var st = this.st;
    if (!st || st.index < 0 || !st.items[st.index]) { this.host.querySelector('.sim-time').textContent = ''; return; }
    var spent = st.dur - st.left + (st.paused ? 0 : Date.now() - this.at);
    var p = st.dur > 0 ? Math.min(1, Math.max(0, spent / st.dur)) : 0;
    var fill = this.host.querySelector('.sim-seg.on .sim-fill');
    if (fill) fill.style.width = (p * 100) + '%';
    var before = 0, total = 0;
    st.items.forEach(function (it, k) { total += it.sec * 1000; if (k < st.index) before += it.sec * 1000; });
    this.host.querySelector('.sim-time').textContent = mmss(before + Math.min(spent, st.dur)) + ' / ' + mmss(total);
  };

  Sim.prototype.dispose = function () {
    clearInterval(this.timer);
    w.removeEventListener('message', this.onMsg);
    this.frame.setAttribute('src', 'about:blank');
  };

  w.linkiiSim = {
    mount: function (host, o) { if (host.__sim) host.__sim.update(o); else host.__sim = new Sim(host, o); },
    update: function (host, o) { if (host && host.__sim) host.__sim.update(o); },
    unmount: function (host) { if (host && host.__sim) { host.__sim.dispose(); host.__sim = null; } }
  };
})(window);
