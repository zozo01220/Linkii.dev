/* Porte d'un écran de salle (application « Réservation de salle », maquettes/salle-app-parcours.html vue 6) — JS « ancien » pour les WebView anciennes.
   LinkiiRoom.apply(cfg, hooks) : cfg = { state, theme, buttons, name } reçu dans le flux du player (null : autre type d'écran).
   hooks = { preview, data(), book(minutes), tz(), now(), online(), store(key[, value]) }.
   La porte lit /api/player/room (créneaux, durées possibles, mode) toutes les 30 s et dès que le serveur la prévient (« RoomChanged ») ;
   l'état (libre, bientôt occupée, occupée) est recalculé chaque seconde sur la dernière copie : il reste juste hors ligne.
   Réserver : le serveur décide (il relit l'agenda, écrit, répond) ; la porte n'affiche jamais « Réservée » sans sa réponse. Pas de prolongation ni de fin anticipée.
   Deux couches : Logique (fonctions pures, testées sous Node : Linkii.Poc.Tests/js/room-door.test.js) et Affichage. */
(function (w) {
  'use strict';

  /* ---------- Logique ---------- */
  var L = {
    dayKey: function (ms, tz) {
      try { return new Date(ms).toLocaleDateString('sv-SE', { timeZone: tz || undefined }); } catch (e) { return new Date(ms).toDateString(); }
    },
    hm: function (ms, tz) {
      try { return new Date(ms).toLocaleTimeString('fr-CH', { hour: '2-digit', minute: '2-digit', timeZone: tz || undefined }); } catch (e) { var d = new Date(ms); return (d.getHours() < 10 ? '0' : '') + d.getHours() + ':' + (d.getMinutes() < 10 ? '0' : '') + d.getMinutes(); }
    },
    label: function (min) { return min < 60 ? min + ' min' : (min % 60 === 0 ? (min / 60) + ' h' : Math.floor(min / 60) + ' h ' + (min % 60 < 10 ? '0' : '') + (min % 60)); },
    /** Créneaux occupés lisibles (ms), triés ; un rendez-vous sur la journée entière ne compte que si la salle le veut. */
    list: function (events, allDay) {
      var l = [];
      (events || []).forEach(function (x) {
        var s = Date.parse(x.start), e = Date.parse(x.end);
        if (!isFinite(s) || !isFinite(e) || e <= s || (x.allDay && !allDay)) return;
        l.push({ s: s, e: e, title: x.title || '', lk: !!x.linkivi });
      });
      l.sort(function (a, b) { return a.s - b.s || a.e - b.e; });
      return l;
    },
    /** État à l'instant now : { st: free | soon | busy, until, title, lk, next } (même règle que RoomRules.State côté serveur). */
    state: function (events, now, soonMin, allDay, tz) {
      var l = L.list(events, allDay), cur = null, i, end = now;
      for (i = 0; i < l.length; i++) if (l[i].s <= now && now < l[i].e) { cur = l[i]; break; }
      if (cur) {
        end = cur.e;
        var grew = true;
        while (grew) { grew = false; for (i = 0; i < l.length; i++) if (l[i].s <= end && l[i].e > end) { end = l[i].e; grew = true; } }
      }
      var today = L.dayKey(now, tz), from = cur ? end : now;
      var next = l.filter(function (x) { return x.s >= from && L.dayKey(x.s, tz) === today; }).slice(0, 6);
      if (cur) return { st: 'busy', until: end, title: cur.title, lk: cur.lk, next: next };
      var nx = null;
      for (i = 0; i < l.length; i++) if (l[i].s > now) { nx = l[i]; break; }
      var soon = soonMin == null ? 10 : +soonMin;
      if (nx && soon > 0 && nx.s - now <= soon * 60000) return { st: 'soon', until: nx.s, title: nx.title, lk: false, next: next };
      return { st: 'free', until: nx && L.dayKey(nx.s, tz) === today ? nx.s : null, title: '', lk: false, next: next };
    },
    /** Créneaux du panneau « Aujourd'hui » : la réunion en cours puis les suivantes de la journée (4 au plus). */
    slots: function (events, now, allDay, tz) {
      var today = L.dayKey(now, tz);
      return L.list(events, allDay).filter(function (x) { return x.e > now && L.dayKey(x.s, tz) === today; }).slice(0, 4);
    },
    /** Texte de l'état : mot et ligne « jusqu'à… » (titre montré seulement s'il a été envoyé). */
    words: function (st, tz) {
      if (st.st === 'busy') return { word: 'OCCUPÉE', sub: (st.title ? st.title + ' · ' : '') + 'jusqu’à ' + L.hm(st.until, tz) };
      if (st.st === 'soon') return { word: 'BIENTÔT OCCUPÉE', sub: 'à ' + L.hm(st.until, tz) + (st.title ? ' · ' + st.title : '') };
      return { word: 'LIBRE', sub: st.until ? 'jusqu’à ' + L.hm(st.until, tz) : 'toute la journée' };
    },
    /** Écran gris (pas de porte à afficher) : [mot, ligne] ou null. */
    grey: function (state, d) {
      if (state === 'none') return ['APPAIRÉE', 'En attente de configuration'];
      if (state === 'off') return ['SALLE INDISPONIBLE', 'Réservation impossible'];
      if (state === 'retired') return ['APPLICATION RETIRÉE', 'Contactez l’administrateur'];
      if (!d) return ['LECTURE…', 'Lecture de l’agenda de la salle'];
      if (d.stale) return ['DONNÉES INDISPONIBLES', 'L’agenda de la salle ne répond pas'];
      return null;
    },
    /** La zone de réservation s'affiche : salle réservable, boutons activés, libre ou bientôt occupée, au moins une durée proposée. */
    bookable: function (d, st, buttons) {
      return !!(d && d.canBook && buttons !== false && st && st.st !== 'busy' && d.options && d.options.length);
    },
    /** Réponse du serveur à une réservation → message de la porte. */
    answer: function (status, body) {
      if (status === 200) return null;
      if (status === 409) return 'Déjà réservée : la salle vient d’être prise.';
      if (status === 429) return 'Trop de réservations depuis cet écran : réessayez dans un instant.';
      if (status === 403 && body && body.code === 'read') return 'Cette salle s’affiche seulement : la réservation n’est pas proposée ici.';
      return 'Réservation impossible. Prévenez l’administrateur.';
    }
  };

  /* ---------- Affichage ---------- */
  var CSS =
    '#roomdoor{position:fixed;left:0;top:0;right:0;bottom:0;z-index:25;display:grid;grid-template-columns:1.5fr 1fr;font-family:"IBM Plex Sans","Segoe UI",Arial,sans-serif;color:#fff;font-size:min(1.55vw,2.7vh);-webkit-user-select:none;user-select:none;touch-action:manipulation;-webkit-tap-highlight-color:transparent;overflow:hidden}' +
    '#roomdoor.portrait{grid-template-columns:1fr;grid-template-rows:1.4fr 1fr;font-size:min(2.6vw,1.6vh)}' +
    '#roomdoor .rd-pa{padding:2.2em 2.6em;display:flex;flex-direction:column;background:#1E8E5A;transition:background .4s;min-width:0}' +
    '#roomdoor .rd-pa.soon{background:#D9822B}#roomdoor .rd-pa.busy{background:#C8372D}#roomdoor .rd-pa.grey{background:#7A8696}' +
    '#roomdoor .rd-rn{font-size:1.25em;opacity:.92}' +
    '#roomdoor .rd-st{font-size:4.6em;font-weight:600;line-height:1;margin:.25em 0 .12em;letter-spacing:.01em}' +
    '#roomdoor .rd-un{font-size:1.6em;opacity:.95}' +
    '#roomdoor .rd-bk{margin-top:auto}' +
    '#roomdoor .rd-lab{font-size:1em;letter-spacing:.08em;text-transform:uppercase;opacity:.9;margin-bottom:.6em}' +
    '#roomdoor .rd-du{display:flex;gap:.6em;flex-wrap:wrap}' +
    '#roomdoor .rd-du button{font:inherit;font-size:1.35em;font-weight:600;min-width:5.2em;min-height:3.6em;border:.12em solid rgba(255,255,255,.75);background:rgba(255,255,255,.14);color:#fff;border-radius:.6em;padding:.3em .6em;display:flex;flex-direction:column;align-items:center;justify-content:center;line-height:1.15;cursor:pointer}' +
    '#roomdoor .rd-du button.on{background:#fff;color:#0B1F3A}#roomdoor .rd-du button[disabled]{opacity:.45;cursor:default}' +
    '#roomdoor .rd-du small{font-size:.55em;font-weight:500}' +
    '#roomdoor .rd-go{font:inherit;margin-top:.8em;width:100%;min-height:3.6em;border:0;border-radius:.6em;background:#fff;color:#0B1F3A;font-size:1.5em;font-weight:600;cursor:pointer}' +
    '#roomdoor .rd-go[disabled]{opacity:.65;cursor:default}' +
    '#roomdoor .rd-msg{margin-top:auto;background:rgba(0,0,0,.22);border-radius:.6em;padding:.8em 1em;font-size:1.3em}' +
    '#roomdoor .rd-small{margin-top:.6em;font-size:.9em;opacity:.85}' +
    '#roomdoor .rd-pz{background:#fff;color:#0B1F3A;padding:2em;display:flex;flex-direction:column;min-width:0;position:relative}' +
    '#roomdoor.dark .rd-pz{background:#0B1F3A;color:#fff}' +
    '#roomdoor .rd-clk{font-size:3.3em;font-weight:500;line-height:1}' +
    '#roomdoor .rd-day{font-size:.9em;letter-spacing:.1em;color:#5A6B80;margin:1.4em 0 .4em}#roomdoor.dark .rd-day{color:#9FB0C4}' +
    '#roomdoor .rd-sl{display:flex;justify-content:space-between;gap:.6em;padding:.75em 0;border-bottom:1px solid #DDE3EA;font-size:1.2em}#roomdoor.dark .rd-sl{border-color:#26405F}' +
    '#roomdoor .rd-sl b{font-weight:500;white-space:nowrap}#roomdoor .rd-sl span{color:#5A6B80;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}#roomdoor.dark .rd-sl span{color:#9FB0C4}' +
    '#roomdoor .rd-ov{position:absolute;left:0;top:0;right:0;bottom:0;background:rgba(30,142,90,.97);display:flex;flex-direction:column;align-items:center;justify-content:center;font-size:2.3em;font-weight:600;z-index:3;gap:.3em;text-align:center;padding:1em}' +
    '#roomdoor .rd-qrbk{display:flex;align-items:center;gap:1.4em}#roomdoor .rd-qr{background:#fff;border-radius:.7em;padding:.5em;width:11em;height:11em;flex:none}#roomdoor .rd-qr svg{display:block;width:100%;height:100%}#roomdoor .rd-qrt{font-size:1.25em;line-height:1.35}' +
    '#roomdoor .rd-undo{font:inherit;margin-top:1em;align-self:flex-start;min-height:2.8em;padding:.4em 1.4em;border:.12em solid rgba(255,255,255,.85);background:rgba(255,255,255,.16);color:#fff;border-radius:.6em;font-size:1.1em;font-weight:600;cursor:pointer}' +
    '#roomdoor .rd-ov .rd-undo{font-size:.45em;margin-top:1.4em;align-self:center}#roomdoor .rd-undo[disabled]{opacity:.6}' +
    '#roomdoor .rd-ov b{font-size:2.2em;line-height:1}#roomdoor .rd-ov small{font-size:.5em;font-weight:500;opacity:.9}' +
    '#roomdoor .rd-offb{position:absolute;left:0;top:0;right:0;background:rgba(0,0,0,.35);font-size:1em;padding:.3em 2.6em;z-index:2}';

  var H = null, cfg = null, sig = '', root = null, data = null, ticker = null, poller = null;
  var last = null, undoTimer = null;   // dernière réservation faite ici : annulable pendant une minute
  var sel = null, selTimer = null, busy = false, msg = null, msgTimer = null, ok = null, okTimer = null, drawn = '';

  function tz() { return H && H.tz ? H.tz() : null; }
  function now() { return H && H.now ? H.now() : Date.now(); }
  function online() { return !H || !H.online || H.online(); }
  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

  function build() {
    if (!document.getElementById('roomdoor-css')) { var st = document.createElement('style'); st.id = 'roomdoor-css'; st.textContent = CSS; document.head.appendChild(st); }
    root = document.createElement('div');
    root.id = 'roomdoor';
    root.addEventListener('click', onClick);
    document.body.appendChild(root);
    var saved = H.store ? H.store('lk_room') : null;   // dernière copie : la porte s'affiche aussitôt au redémarrage, même hors ligne
    if (saved) { try { data = JSON.parse(saved); } catch (e) { data = null; } }
    load();
    ticker = setInterval(draw, 1000);
    poller = setInterval(load, 30000);
    draw();
  }

  function teardown() {
    clearInterval(ticker); clearInterval(poller); clearTimeout(selTimer); clearTimeout(msgTimer); clearTimeout(okTimer);
    ticker = poller = null;
    if (root && root.parentNode) root.parentNode.removeChild(root);
    root = null; data = null; sel = null; msg = null; ok = null; busy = false; drawn = '';
  }

  function load() {
    if (!H || !cfg) return;
    H.data().then(function (d) {
      if (!d) return;
      data = d;
      if (H.store && !H.preview) { try { H.store('lk_room', JSON.stringify(d)); } catch (e) {} }
      if (sel && !(d.options || []).some(function (o) { return o.min === sel && o.ok; })) sel = null;   // durée devenue impossible
      if (d.refused && H.store && H.store('lk_room_refused') !== d.refused) { H.store('lk_room_refused', d.refused); say('La salle a refusé la réservation (elle était sans doute déjà prise).', 10000); }
      draw(true);
    }, function () { draw(true); });
  }

  function say(text, ms) {
    msg = text; clearTimeout(msgTimer);
    msgTimer = setTimeout(function () { msg = null; draw(true); }, ms || 8000);
    draw(true);
  }

  function onClick(e) {
    var b = e.target.closest ? e.target.closest('button') : null;
    if (!b || b.disabled || busy) return;
    if (b.getAttribute('data-min')) {
      sel = parseInt(b.getAttribute('data-min'), 10);
      clearTimeout(selTimer);
      selTimer = setTimeout(function () { sel = null; draw(true); }, 20000);   // choix oublié après 20 s sans toucher
      draw(true);
    } else if (b.getAttribute('data-act') === 'book' && sel) book(sel);
    else if (b.getAttribute('data-act') === 'cancel') cancel();
  }

  function book(minutes) {
    busy = true; draw(true);
    var sp = spanOf(minutes, now()), start = sp.from, done = function () { busy = false; sel = null; clearTimeout(selTimer); };
    if (H.preview) {   // simulateur : rien n'est réservé
      setTimeout(function () {
        done();
        if (data) data.events = (data.events || []).concat([{ title: 'Réservé sur place', start: new Date(start).toISOString(), end: new Date(sp.to).toISOString(), linkivi: true }]);
        setLast('preview', start, sp.to);
        confirmOk(sp.to, 'Aperçu : rien n’est réservé');
      }, 500);
      return;
    }
    H.book(minutes).then(function (r) {
      done();
      if (r.status === 200) {
        var to = r.body && r.body.to ? Date.parse(r.body.to) : sp.to;
        if (data) data.events = (data.events || []).concat([{ title: 'Réservé sur place', start: r.body && r.body.from ? r.body.from : new Date(start).toISOString(), end: new Date(to).toISOString(), linkivi: true }]);
        setLast(r.body && r.body.id, r.body && r.body.from ? Date.parse(r.body.from) : start, to);
        confirmOk(to);
        load();
      } else {
        say(L.answer(r.status, r.body), 8000);
        load();
      }
    }, function () { done(); say('Réservation impossible : la tablette ne joint pas le serveur.', 8000); });
  }

  /** Début et fin d'une durée proposée : ceux du serveur (arrondi au quart d'heure selon les règles), sinon maintenant + durée. */
  function spanOf(min, t) {
    var o = data && data.options ? data.options.filter(function (x) { return x.min === min; })[0] : null;
    return o && o.from ? { from: o.from, to: o.to } : { from: t, to: t + min * 60000 };
  }
  function touch() { return !H || !H.touch || H.touch() !== false; }
  function qrSvg() {
    var p = data && data.qr ? String(data.qr).split('|') : [], n = parseInt(p[0], 10);
    return n > 0 && /^[0-9MhvzH\- ]+$/.test(p[1] || '') ? '<svg viewBox="0 0 ' + n + ' ' + n + '" shape-rendering="crispEdges"><path d="' + p[1] + '" fill="#0B1F3A"/></svg>' : '';
  }
  function setLast(id, from, to) {
    clearTimeout(undoTimer);
    last = id ? { id: id, from: from, to: to, till: Date.now() + 60000 } : null;
    if (last) undoTimer = setTimeout(function () { last = null; draw(true); }, 60500);
  }
  function canUndo() { return !!last && Date.now() < last.till; }

  /** Annule la réservation qui vient d'être faite (erreur de manipulation) : possible pendant une minute. */
  function cancel() {
    if (!canUndo() || busy) return;
    var l = last;
    busy = true; draw(true);
    var drop = function () { if (data && data.events) data.events = data.events.filter(function (e) { return !(e.linkivi && Math.abs(Date.parse(e.start) - l.from) <= 120000 && Math.abs(Date.parse(e.end) - l.to) <= 120000); }); };
    var finish = function () { busy = false; clearTimeout(undoTimer); last = null; ok = null; clearTimeout(okTimer); };
    if (H.preview) { finish(); drop(); say('Aperçu : réservation annulée', 4000); return; }
    H.cancel(l.id).then(function (r) {
      if (r.status === 200) { finish(); drop(); say('Réservation annulée', 5000); }
      else { busy = false; say((r.body && r.body.error) || 'Annulation impossible.', 8000); }
      load();
    }, function () { busy = false; say('Annulation impossible : la tablette ne joint pas le serveur.', 8000); });
  }

  function confirmOk(to, note) {
    ok = { to: to, note: note || '' };
    clearTimeout(okTimer);
    okTimer = setTimeout(function () { ok = null; draw(true); }, canUndo() ? 8000 : 4000);
    draw(true);
  }

  function draw(force) {
    if (!root || !cfg) return;
    var t = now(), z = tz(), state = data && data.state ? data.state : cfg.state, net = online();
    var grey = L.grey(state, data);
    var room = (data && data.room) || { name: cfg.name || '' };
    var st = grey ? null : L.state(data.events, t, room.soon, room.allDay, z);
    var canBook = !grey && L.bookable(data, st, cfg.buttons);
    var key = [Math.floor(t / 60000), state, grey && grey[0], st && st.st, st && st.until, sel, busy, msg, ok && ok.to, canUndo(), data && data.qr, net, canBook, data && data.readAt, data && data.error, cfg.theme].join('|');
    if (!force && key === drawn) return;
    drawn = key;
    root.className = (cfg.theme === 'dark' ? 'dark' : '') + (innerHeight > innerWidth ? ' portrait' : '');
    var nameLine = esc(room.name || 'Salle') + (room.cap ? ' · ' + room.cap + ' places' : '') + (room.place ? ' · ' + esc(room.place) : '');
    var h = '';
    if (!net) h += '<div class="rd-offb">● Hors ligne</div>';
    if (grey) {
      h += '<div class="rd-pa grey"><div class="rd-rn">' + (state === 'none' ? 'Écran de salle' : nameLine) + '</div><div class="rd-st">' + grey[0] + '</div><div class="rd-un">' + grey[1] + '</div></div>';
    } else {
      var wd = L.words(st, z);
      h += '<div class="rd-pa ' + st.st + '"><div class="rd-rn">' + nameLine + '</div><div class="rd-st">' + wd.word + '</div><div class="rd-un">' + esc(wd.sub) + '</div>';
      if (canUndo() && !ok && !busy) h += '<button type="button" class="rd-undo" data-act="cancel">Annuler ma réservation</button><div class="rd-small">Possible pendant une minute</div>';
      if (msg) h += '<div class="rd-msg">' + esc(msg) + '</div>';
      else if (canBook && qrSvg() && !touch()) {   // tablette non tactile : un QR, la durée se choisit sur le téléphone
        h += '<div class="rd-bk rd-qrbk"><div class="rd-qr">' + qrSvg() + '</div><div class="rd-qrt"><div class="rd-lab">Réserver maintenant</div>Scannez ce code, choisissez la durée sur votre téléphone, puis confirmez.</div></div>';
      }
      else if (canBook) {
        h += '<div class="rd-bk"><div class="rd-lab">Réserver maintenant, pour…</div><div class="rd-du">';
        data.options.forEach(function (o) {
          h += '<button type="button" data-min="' + o.min + '" class="' + (sel === o.min ? 'on' : '') + '"' + (o.ok && net && !busy ? '' : ' disabled') + '>' + L.label(o.min) + (o.ok ? '' : '<small>' + esc(o.why || '') + '</small>') + '</button>';
        });
        h += '</div>';
        if (!net) h += '<div class="rd-msg">Hors ligne : réservation impossible</div>';
        else h += '<button type="button" class="rd-go" data-act="book"' + (sel && !busy ? '' : ' disabled') + '>' + (busy ? 'Réservation…' : sel ? 'Réserver ' + L.hm(spanOf(sel, t).from, z) + ' – ' + L.hm(spanOf(sel, t).to, z) : 'Choisissez une durée') + '</button>';
        h += '</div>';
      }
      if (data && data.error && !data.stale) h += '<div class="rd-small">Mise à jour impossible : dernière lecture à ' + (data.readAt ? L.hm(data.readAt, z) : '—') + '</div>';
      h += '</div>';
    }
    h += '<div class="rd-pz"><div class="rd-clk">' + L.hm(t, z) + '</div><div class="rd-day">AUJOURD’HUI</div>';
    if (!grey) {
      var sl = L.slots(data.events, t, room.allDay, z);
      if (!sl.length) h += '<div class="rd-sl"><span>Rien d’autre aujourd’hui</span></div>';
      sl.forEach(function (x) { h += '<div class="rd-sl"><b>' + L.hm(x.s, z) + ' – ' + L.hm(x.e, z) + '</b><span>' + esc(x.title || (x.lk ? 'Réservé sur place' : 'Occupée')) + '</span></div>'; });
    }
    h += '</div>';
    if (ok) h += '<div class="rd-ov"><b>✓</b>Réservée jusqu’à ' + L.hm(ok.to, z) + (ok.note ? '<small>' + esc(ok.note) + '</small>' : '') + (canUndo() ? '<button type="button" class="rd-undo" data-act="cancel"' + (busy ? ' disabled' : '') + '>Annuler la réservation</button>' : '') + '</div>';
    root.innerHTML = h;
  }

  /* ---------- Entrée ---------- */
  function apply(c, hooks) {
    H = hooks;
    var s = c ? JSON.stringify(c) : '';
    if (s === sig) { load(); return; }
    sig = s;
    teardown();
    cfg = c || null;
    if (cfg) build();
  }

  /** Le serveur prévient qu'un créneau de la salle a changé (réservation faite sur une autre tablette, invitation refusée). */
  function changed() { if (cfg) load(); }
  function active() { return !!cfg; }

  var api = { apply: apply, changed: changed, active: active, logic: L };
  w.LinkiiRoom = api;
  if (typeof module === 'object' && module.exports) module.exports = api;
})(typeof window !== 'undefined' ? window : globalThis);
