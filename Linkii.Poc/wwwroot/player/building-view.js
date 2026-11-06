/* Moteur « vue du bâtiment » (DEV-POC §199) : un seul code pour afficher un plan du bâtiment Home Assistant, où qu'il soit montré
   (widget du player, aperçu de l'éditeur ; demain contenu de liste, borne, page Plans).

     var vb = LinkiiBuilding.mount(élément, { command: function (body) { return Promise }, now: function () { return ms }, mode: 'auto' | 'signage' | 'interactive' });
     vb.setData(données)   // ce que renvoie le fournisseur Home Assistant : { title, live, decimals, dimmer, entities, plan } (HomeAssistantProvider.PlanView)
     vb.resize()           // après un changement de taille que l'élément ne signale pas lui-même
     vb.destroy()          // minuteurs, animation et écouteurs arrêtés

   Trois couches :
   - Logic : fonctions pures (état d'un appareil, étiquettes, résumé, couleurs des pièces, disposition), testées sous Node (Linkii.Poc.Tests/js) ;
   - un rendu (renderers.css3d aujourd'hui ; 2,5D ou WebGL plus tard) : build(plan) / camera(cam) / paint(vue) / hit(cible) ;
   - mount : l'habillage commun (résumé, barre tactile, panneau de commande, message, rotation, événements).
   JS volontairement « ancien » (var, pas de fonctions fléchées) : il tourne aussi dans la WebView Android. Dépend de ha-format.js (LinkiiHa). */
(function (root) {
  'use strict';
  var Ha = root.LinkiiHa || (typeof require === 'function' ? require('./ha-format.js') : null);

  /* =============================== logique pure =============================== */

  // capteurs binaires qui sont de vraies alertes ; « Connecté », « Branché », « En marche » restent neutres (§198)
  var ALERT_DC = ['door', 'garage_door', 'opening', 'window', 'smoke', 'gas', 'carbon_monoxide', 'moisture', 'problem', 'safety', 'tamper', 'lock', 'heat', 'cold', 'battery'];
  var MOVE_DC = ['motion', 'occupancy', 'presence', 'moving', 'vibration', 'sound'];

  /** Couleur de la pastille d'un appareil : '' | on | alert | move | off. */
  function state(e) {
    if (e.available === false) return 'off';
    var d = e.domain, dc = e.deviceClass || '';
    if (d === 'binary_sensor') {
      if (!e.on) return '';
      if (MOVE_DC.indexOf(dc) >= 0) return 'move';
      return ALERT_DC.indexOf(dc) >= 0 ? 'alert' : '';
    }
    if (d === 'lock') return e.on ? 'alert' : '';
    if (d === 'light' || d === 'switch' || d === 'input_boolean' || d === 'fan') return e.on ? 'on' : '';
    if (d === 'climate') return e.hvac === 'heating' ? 'on' : '';
    return '';
  }
  function alerting(st) { return st === 'alert' || st === 'move'; }
  function absent(e) { return !e || e.state === 'absent'; }

  /** Température d'une pièce lue sur un appareil : thermostat (mesure) ou capteur de température ; null sinon. */
  function roomTemp(e) {
    if (!e || e.available === false) return null;
    if (e.domain === 'climate') return e.current == null ? null : e.current;
    if (e.domain === 'sensor' && (e.deviceClass === 'temperature' || /°[CF]$/.test(e.unit || ''))) { var t = parseFloat(e.state); return isNaN(t) ? null : t; }
    return null;
  }

  function hex3(h) { var n = parseInt(String(h).replace('#', ''), 16); if (isNaN(n)) n = 0; return [n >> 16, (n >> 8) & 255, n & 255]; }
  function mix(a, b, k) {
    var A = hex3(a), B = hex3(b);
    return '#' + A.map(function (v, i) { return ('0' + Math.round(v + (B[i] - v) * k).toString(16)).slice(-2); }).join('');
  }
  function rgba(hex, a) { var c = hex3(hex || '#FFC870'); return 'rgba(' + c[0] + ',' + c[1] + ',' + c[2] + ',' + a + ')'; }

  var ROOM_BASE = '#21406a';
  /** Couleur d'une pièce dessinée : carte de chaleur (bleu sous 21 °C, orange au-dessus, plein à ±3 °C) puis teinte de sa lumière allumée la plus forte. */
  function roomColor(info, heat) {
    var c = ROOM_BASE;
    if (heat && info && info.t != null) c = info.t < 21 ? mix(ROOM_BASE, '#2f78c4', Math.min(1, (21 - info.t) / 3) * 0.85) : mix(ROOM_BASE, '#c8582c', Math.min(1, (info.t - 21) / 3) * 0.85);
    if (info && info.light) c = mix(c, info.col || '#ffc870', 0.18 + 0.32 * info.light / 100);
    return c;
  }

  function roomsOf(plan, fid) { var f = plan.floors.filter(function (x) { return x.id === fid; })[0]; return (f && f.rooms) || []; }
  function roomName(plan, d) { if (!d.room) return ''; var r = roomsOf(plan, d.floor).filter(function (x) { return x.id === d.room; })[0]; return r ? r.name : ''; }

  /**
   * Ce que la scène montre, sans DOM : pour chaque appareil, sa pastille (visible, nue, texte) ; pour chaque pièce dessinée, son étiquette et sa couleur.
   * ents : entités par identifiant, dans l'état affiché (commande en attente comprise). o : { labels, hideOff, heat, decimals, now, pulseId, panelId }.
   * Étiquettes « room » : une par pièce (lumières allumées, température) ; seuls les appareils en alerte ou en mouvement gardent la leur, avec leur nom ;
   * un appareil hors pièce (étage en image) se comporte comme en « active ». Indisponibles masqués si hideOff.
   */
  function scene(plan, ents, o) {
    // borne plein écran (§201) : o.kiosk = niveau montré. « floor » : une étiquette par pièce (même sans rien à dire), alertes à part ;
    // « room » : les appareils de la pièce choisie seulement, en gros boutons ; « all » (bâtiment) : aucune pastille.
    var K = o.kiosk || null, lab = K ? 'room' : o.labels || 'room', hideOff = o.hideOff !== false, pins = {}, rooms = {};
    plan.devices.forEach(function (d) {
      var e = ents[d.id];
      if (absent(e)) { pins[d.id] = { show: false }; return; }
      var st = state(e);
      if (d.room) {
        var k = d.floor + '/' + d.room, r = rooms[k] || (rooms[k] = { lights: 0, light: 0, col: null, t: null, ids: [] });
        r.ids.push(d.id);
        if (e.available !== false) {
          if (e.domain === 'light' && e.on) {
            r.lights++;
            var b = e.brightness == null ? 100 : e.brightness;
            if (b >= r.light) { r.light = b; r.col = e.color || '#ffc870'; }
          }
          if (r.t == null) r.t = roomTemp(e);
        }
      }
      if (hideOff && e.available === false) { pins[d.id] = { show: false }; return; }
      var own = alerting(st) || o.pulseId === d.id || o.panelId === d.id;
      var bare = lab === 'none' || ((lab === 'active' || lab === 'room') && !st);
      var show = lab === 'room' && d.room ? own : true, big = false;
      if (K === 'room') { big = !!d.room && d.floor + '/' + d.room === o.room; show = big; bare = false; }
      else if (K === 'all') show = false;
      pins[d.id] = {
        show: show, big: big, bare: bare, st: st, pulse: o.pulseId === d.id,
        dim: !K && !!o.room && d.floor + '/' + d.room !== o.room, sel: o.panelId === d.id,   // une pièce choisie, les autres appareils s'estompent
        text: big ? Ha.value(e, o.decimals, o.now) : (lab === 'room' && own && st ? e.name + ' · ' : '') + Ha.value(e, o.decimals, o.now),
        glow: e.domain === 'light' && e.on && e.available !== false ? { b: e.brightness == null ? 100 : e.brightness, col: e.color || '#ffc870' } : null,
        open: d.seg ? (e.available !== false && (e.domain === 'binary_sensor' ? !!e.on : e.state === 'open' || e.state === 'opening')) : false,
        shut: d.seg && d.seg.kind === 'cover' ? (e.position != null ? 100 - e.position : e.state === 'closed' || e.state === 'closing' ? 100 : 0) : null
      };
    });
    var labels = {};
    plan.floors.forEach(function (f) {
      (f.rooms || []).forEach(function (r) {
        var k = f.id + '/' + r.id, info = rooms[k], says = !!info && (info.lights > 0 || info.t != null);
        labels[k] = {
          name: r.name, show: K ? K === 'floor' : lab === 'room' && says, quiet: !says, sel: o.room === k,
          lights: info ? info.lights : 0, t: info ? info.t : null, color: roomColor(info, !!o.heat), lit: !!(info && info.light)
        };
      });
    });
    return { pins: pins, rooms: labels };
  }

  /** Résumé : alertes, lumières allumées, températures (une par pièce), volets ouverts / fermés, appareils indisponibles. */
  function summary(plan, ents, o) {
    var alerts = [], lights = [], temps = [], seen = {}, covers = { open: 0, closed: 0 }, off = 0;
    plan.devices.forEach(function (d) {
      var e = ents[d.id]; if (!e) return;
      if (e.state === 'absent' || e.available === false) { off++; return; }
      var st = state(e), where = roomName(plan, d);
      if (st === 'alert') alerts.push({ id: d.id, e: e, t: e.name, s: where, v: Ha.value(e, o.decimals, o.now) });
      if (e.domain === 'light' && e.on) lights.push({ id: d.id, e: e, t: where || e.name, s: where && e.name !== where ? e.name : '', v: Ha.value(e, o.decimals, o.now) });
      var t = roomTemp(e);
      if (t != null) { var k = d.room ? d.floor + '/' + d.room : d.id; if (!seen[k]) { seen[k] = 1; temps.push({ id: d.id, t: where || e.name, v: Ha.deg(Math.round(t * 10) / 10), n: t }); } }
      if (e.domain === 'cover') { if (e.state === 'closed' || e.state === 'closing' || e.position === 0) covers.closed++; else covers.open++; }
    });
    return { alerts: alerts, lights: lights, temps: temps, covers: covers, off: off };
  }

  /** « 1 ouvert · 2 fermés » ; vide sans volet. */
  function coversText(c) {
    var p = [];
    if (c.open) p.push(c.open + ' ouvert' + (c.open > 1 ? 's' : ''));
    if (c.closed) p.push(c.closed + ' fermé' + (c.closed > 1 ? 's' : ''));
    return p.join(' · ');
  }
  function plural(n, one, many) { return n + ' ' + (n > 1 ? many : one); }

  /**
   * Place du résumé : celle du réglage, sauf dans une petite case. Moins de 560 px de large : un panneau latéral devient un bandeau.
   * Moins de 380 × 220 px (widget dans un coin) : le résumé seul (« only »), sans 3D, sauf si le résumé est masqué.
   */
  function layoutMode(pref, width, height) {
    var m = pref === 'left' || pref === 'top' || pref === 'none' ? pref : 'right';
    if (m !== 'none' && width && ((width < 380) || (height && height < 220))) return 'only';
    return (m === 'right' || m === 'left') && width && width < 560 ? 'top' : m;
  }

  /** Étape de visite en cours : { i, step, left (ms avant la suivante) } ; null sans visite. Le temps est compté depuis le début de la visite. */
  function tourAt(tour, elapsed) {
    if (!tour || !tour.length) return null;
    var total = 0, i;
    for (i = 0; i < tour.length; i++) total += Math.max(1, tour[i].sec) * 1000;
    var t = ((elapsed % total) + total) % total;
    for (i = 0; i < tour.length; i++) {
      var d = Math.max(1, tour[i].sec) * 1000;
      if (t < d) return { i: i, step: tour[i], left: d - t };
      t -= d;
    }
    return { i: 0, step: tour[0], left: tour[0].sec * 1000 };
  }

  /** Étage montré d'abord à la borne : celui qui porte le plus d'appareils (le rez, le plus souvent). */
  function homeFloor(plan) {
    var best = null, n = -1;
    plan.floors.forEach(function (f) {
      var c = plan.devices.filter(function (d) { return d.floor === f.id; }).length;
      if (c > n) { n = c; best = f.id; }
    });
    return best;
  }

  /** Remonter d'un niveau à la borne : appareil → pièce (ou étage si l'appareil est hors pièce) → étage → bâtiment (floor « all », §201). */
  function navUp(nav, plan) {
    if (nav.dev) {
      var d = plan.devices.filter(function (x) { return x.id === nav.dev; })[0];
      return { floor: nav.floor, room: d && d.room && nav.room ? nav.room : null, dev: null };
    }
    if (nav.room) return { floor: nav.floor, room: null, dev: null };
    if (nav.floor !== 'all' && plan.floors.length > 1) return { floor: 'all', room: null, dev: null };
    return nav;
  }

  /**
   * Plein écran (§201) : échelle qui fait tenir une boîte du plan (px du plan) dans la zone libre, vue sous l'angle de la caméra.
   * box { w, h }, rz / rx en degrés, depth : hauteur (murs, étages empilés), area { w, h } ; spin : la caméra tourne, on cadre pour tous les angles.
   */
  function fit(box, rz, rx, depth, area, spin) {
    var a = rz * Math.PI / 180, b = rx * Math.PI / 180, ca = Math.abs(Math.cos(a)), sa = Math.abs(Math.sin(a));
    var diag = Math.sqrt(box.w * box.w + box.h * box.h);
    var bw = spin ? diag : box.w * ca + box.h * sa, bd = spin ? diag : box.w * sa + box.h * ca;
    var bh = bd * Math.cos(b) + (depth || 0) * Math.sin(b);
    return Math.max(0.05, Math.min(area.w / Math.max(1, bw), area.h / Math.max(1, bh)));
  }

  /** Taille des textes (px) calée sur l'écran : borne entre 17 et 24 px, diffusion entre 16 et 34 px (lisible de loin). */
  function uiSize(w, h, kind) {
    var m = Math.min(w || 0, h || 0);
    return kind === 'tv' ? Math.max(16, Math.min(34, Math.round(m / 38))) : Math.max(17, Math.min(24, Math.round(m / 44)));
  }

  /** Appareil qu'un toucher allume ou éteint directement à la borne (lumière, prise, interrupteur, ventilateur commandables). */
  function quick(e) {
    return !!e && e.available !== false && Ha.can(e, 'on') && ['light', 'switch', 'input_boolean', 'fan'].indexOf(e.domain) >= 0;
  }

  /** Diffusion plein écran (§201) : le résumé en pastilles dans le coin (lumières, température moyenne, alertes). */
  function pillsHtml(s) {
    var nl = s.lights.length, p = [];
    s.alerts.forEach(function (i) { p.push('<span class="hpl-pl alert">' + Ha.icon(i.e) + esc(i.t + ' · ' + i.v) + '</span>'); });
    p.push('<span class="hpl-pl' + (nl ? ' on' : '') + '">' + Ha.icon({ domain: 'light' }) + (nl ? plural(nl, 'lumière allumée', 'lumières allumées') : 'Lumières éteintes') + '</span>');
    var ts = s.temps.filter(function (i) { return i.n != null; });
    if (ts.length) {
      var avg = ts.reduce(function (a, i) { return a + i.n; }, 0) / ts.length;
      p.push('<span class="hpl-pl">' + Ha.icon({ domain: 'climate' }) + esc(Ha.deg(Math.round(avg * 10) / 10) + (ts.length > 1 ? ' en moyenne' : '')) + '</span>');
    }
    return p.join('');
  }

  function esc(s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }

  /** HTML du résumé : panneau (titre, rubriques de 5 lignes au plus) ou piste du bandeau. */
  function summaryHtml(s, mode, title, live) {
    var nl = s.lights.length, cov = coversText(s.covers), MAX = 5;
    if (mode === 'top') {
      var tk = [];
      s.alerts.forEach(function (i) { tk.push('<span class="hpl-tk alert">' + Ha.icon(i.e) + esc(i.t + ' : ' + i.v) + '</span>'); });
      tk.push('<span class="hpl-tk' + (nl ? ' on' : '') + '">' + Ha.icon({ domain: 'light' }) + (nl ? plural(nl, 'lumière allumée', 'lumières allumées') : 'Lumières éteintes') + '</span>');
      if (s.temps.length) tk.push('<span class="hpl-tk">' + Ha.icon({ domain: 'climate' }) + esc(s.temps.slice(0, 6).map(function (i) { return i.t + ' ' + i.v; }).join(' · ')) + '</span>');
      if (cov) tk.push('<span class="hpl-tk">' + Ha.icon({ domain: 'cover' }) + esc('Volets : ' + cov) + '</span>');
      return '<div class="hpl-track">' + tk.join('') + '</div>';
    }
    function row(i, cls, ic) { return '<div class="hpl-it ' + cls + '">' + ic + '<span class="hpl-it-t">' + esc(i.t) + (i.s ? ' <small>· ' + esc(i.s) + '</small>' : '') + '</span><b>' + esc(i.v) + '</b></div>'; }
    function sec(title2, items, cls, ic, extra) {
      if (!items.length && !extra) return '';
      return '<div class="hpl-sec"><span>' + esc(title2) + '</span>' + items.slice(0, MAX).map(function (i) { return row(i, cls, ic(i)); }).join('')
        + (items.length > MAX ? '<div class="hpl-more">+ ' + (items.length - MAX) + ' autres</div>' : '') + (extra || '') + '</div>';
    }
    var own = function (i) { return Ha.icon(i.e); }, thermo = function () { return Ha.icon({ domain: 'climate' }); };
    return '<h3>' + esc(title || 'Bâtiment') + (live ? '<small>en direct</small>' : '') + '</h3>'
      + sec('Alertes', s.alerts, 'alert', own)
      + sec(nl ? plural(nl, 'lumière allumée', 'lumières allumées') : 'Lumières', s.lights, 'on', own, nl ? '' : '<div class="hpl-more">Toutes éteintes</div>')
      + sec('Températures', s.temps, '', thermo)
      + (cov ? sec('Ouvertures', [{ t: 'Volets', v: cov }], '', function () { return Ha.icon({ domain: 'cover' }); }) : '')
      + (s.off ? '<div class="hpl-more">' + plural(s.off, 'appareil indisponible', 'appareils indisponibles') + '</div>' : '');
  }

  /** Message d'un événement (porte ouverte, mouvement, serrure) ; null si rien de nouveau. */
  function eventText(e, was, now) {
    if (!e || (now !== 'alert' && now !== 'move') || was === now) return null;
    return e.domain === 'lock' ? e.name + ' : déverrouillée' : now === 'move' ? e.name + ' : mouvement' : e.name + ' : ouvert';
  }

  var Logic = {
    ALERT_DC: ALERT_DC, state: state, roomTemp: roomTemp, mix: mix, roomColor: roomColor, scene: scene, summary: summary,
    summaryHtml: summaryHtml, coversText: coversText, layoutMode: layoutMode, eventText: eventText, roomName: roomName,
    tourAt: tourAt, homeFloor: homeFloor, navUp: navUp, fit: fit, uiSize: uiSize, quick: quick, pillsHtml: pillsHtml
  };

  /* =============================== rendu 3D CSS ===============================
     Étages en plaques empilées (sans bibliothèque : tient sur les petits appareils). Étage en image : une plaque avec le plan.
     Étage dessiné (§195) : dalle, pièces, murs debout (passages et ouvertures déjà retirés par le serveur), portes / fenêtres / volets dans leur mur ;
     une même échelle (P px par mètre) pour tous les étages dessinés. Pastilles toujours face à l'écran. */
  function css3d(view) {
    var world = document.createElement('div'); world.className = 'hpl-world'; view.appendChild(world);
    var PW = 480, GAP = 175, FLAT = 14, P = 40, WH = 44, plan = null;

    function build(p) {
      plan = p;
      var ratio = 1.6, i;
      for (i = 0; i < p.floors.length; i++) ratio = i === 0 ? p.floors[i].ratio : Math.max(ratio, p.floors[i].ratio);
      var PH = Math.round(PW / ratio);
      var drawn = p.floors.filter(function (f) { return f.kind === 'rooms'; });
      P = drawn.length ? Math.min.apply(null, drawn.map(function (f) { return Math.min(PW / f.w, PH / f.h); })) : 40;
      WH = Math.round(Math.min(1.5 * P, 78));   // hauteur des murs : les pièces se lisent sans cacher les pastilles (§197)
      world.style.width = PW + 'px'; world.style.height = PH + 'px';
      world.style.setProperty('--hpl-stem', (drawn.length ? WH + 12 : 26) + 'px');
      world.style.setProperty('--hpl-lift', (drawn.length ? WH + 6 : 6) + 'px');   // plein écran : étiquettes posées au-dessus des murs
      world.innerHTML = '';
      p.floors.forEach(function (f) {
        var fl = document.createElement('div');
        fl.className = 'hpl-floor'; fl.setAttribute('data-f', f.id);
        var host = fl;
        if (f.kind === 'rooms') {
          var gw = f.w * P, gh = f.h * P, gr = document.createElement('div');
          gr.className = 'hpl-grid';
          gr.style.left = (PW - gw) / 2 + 'px'; gr.style.top = (PH - gh) / 2 + 'px'; gr.style.width = gw + 'px'; gr.style.height = gh + 'px';
          var slab = document.createElement('div'); slab.className = 'hpl-plate hpl-slab'; gr.appendChild(slab);
          (f.rooms || []).forEach(function (r) {
            var rm = document.createElement('div'); rm.className = 'hpl-room'; rm.setAttribute('data-room', r.id);
            rm.style.left = r.x * P + 'px'; rm.style.top = r.y * P + 'px'; rm.style.width = r.w * P + 'px'; rm.style.height = r.h * P + 'px';
            var rn = document.createElement('span'); rn.className = 'hpl-rn'; rn.textContent = r.name; rm.appendChild(rn);
            gr.appendChild(rm);
          });
          var standing = function (cls, s) {   // élément debout sur un segment de mur
            var w = document.createElement('div'); w.className = cls;
            w.style.left = s.x * P + 'px'; w.style.top = s.y * P + 'px'; w.style.width = s.len * P + 'px'; w.style.height = WH + 'px';
            w.style.transform = 'rotateZ(' + s.d + 'deg) rotateX(90deg)';
            gr.appendChild(w); return w;
          };
          (f.walls || []).forEach(function (s) { standing('hpl-wall', s); });
          p.devices.filter(function (d) { return d.floor === f.id && d.seg; }).forEach(function (d) {
            var o = standing('hpl-op ' + d.seg.kind, d.seg); o.setAttribute('data-o', d.id);
            if (d.seg.kind === 'door') {
              var leaf = document.createElement('div'); leaf.className = 'hpl-leaf';
              leaf.style.setProperty('--swing', ((d.seg.d === 0 ? d.seg.s : -d.seg.s) * 72) + 'deg');
              o.appendChild(leaf);
            } else o.innerHTML = '<div class="hpl-glass">' + (d.seg.kind === 'cover' ? '<div class="hpl-shut"></div>' : '') + '</div>';
          });
          fl.appendChild(gr); host = gr;
        } else {
          var pl = document.createElement('div');
          pl.className = 'hpl-plate' + (f.img ? ' has-img' : '');
          if (f.img) { var im = document.createElement('div'); im.className = 'hpl-img'; im.style.backgroundImage = 'url("' + f.img.replace(/"/g, '') + '")'; pl.appendChild(im); }
          else { var nm = document.createElement('span'); nm.className = 'hpl-fname'; nm.textContent = f.name; pl.appendChild(nm); }
          fl.appendChild(pl);
        }
        var tag = document.createElement('div');
        tag.className = 'hpl-ftag'; tag.innerHTML = '<span></span>'; tag.firstChild.textContent = f.name;
        fl.appendChild(tag);
        p.devices.filter(function (d) { return d.floor === f.id; }).forEach(function (d) {
          var g = document.createElement('div'); g.className = 'hpl-glow'; g.setAttribute('data-g', d.id);
          g.style.left = d.x * 100 + '%'; g.style.top = d.y * 100 + '%';
          host.appendChild(g);
          var pin = document.createElement('div'); pin.className = 'hpl-pin'; pin.setAttribute('data-p', d.id);
          pin.style.left = d.x * 100 + '%'; pin.style.top = d.y * 100 + '%';
          pin.innerHTML = '<div class="hpl-pin-in"><button type="button" class="hpl-chip"></button><i class="hpl-stem"></i></div>';
          host.appendChild(pin);
        });
        if (f.kind === 'rooms') (f.rooms || []).forEach(function (r) {   // étiquette de la pièce (mode « Par pièce »)
          var rp = document.createElement('div'); rp.className = 'hpl-pin hpl-rpin'; rp.setAttribute('data-rp', f.id + '/' + r.id);
          rp.style.left = (r.x + r.w / 2) * P + 'px'; rp.style.top = (r.y + r.h / 2) * P + 'px';
          rp.innerHTML = '<div class="hpl-pin-in"><button type="button" class="hpl-chip hpl-room-chip"></button><i class="hpl-stem"></i></div>';
          host.appendChild(rp);
        });
        world.appendChild(fl);
      });
    }

    function floorZ(i) {
      var drawn = plan.floors.some(function (f) { return f.kind === 'rooms'; });
      return plan.explode ? i * (GAP + (drawn ? WH : 0)) : i * (drawn ? WH + 2 : FLAT);
    }

    /** Caméra : cam = { rz, rx, sel ('all' ou un étage) } ; ease = transition douce. */
    function camera(cam, ease) {
      if (!plan) return;
      world.classList.toggle('ease', !!ease);
      var floors = plan.floors, selI = cam.sel === 'all' ? -1 : floors.findIndex(function (f) { return f.id === cam.sel; });
      var zs = [], PH = world.offsetHeight || PW / 1.6, base = cam.solo && selI >= 0 ? floorZ(selI) : 0;   // étage montré seul : posé à hauteur zéro, donc centré
      floors.forEach(function (f, i) {
        var fe = world.querySelector('.hpl-floor[data-f="' + f.id + '"]'); if (!fe) return;
        fe.style.transform = 'translateZ(' + (floorZ(i) - base) + 'px)';
        // cam.solo (visite, borne) : l'étage montré seul, cadré sur lui ; sinon les étages du dessous restent en transparence
        fe.className = 'hpl-floor' + (selI >= 0 && i < selI ? (cam.solo ? ' hide' : ' ghost') : '') + (selI >= 0 && i > selI ? ' hide' : '');
        if (selI < 0 || i === selI || (!cam.solo && i < selI)) zs.push(floorZ(i) - base);
      });
      if (!zs.length) zs.push(0);
      var z0 = Math.min.apply(null, zs), z1 = Math.max.apply(null, zs) + Math.max(40, WH);
      if (cam.fit) { fitCamera(cam, ease, selI, z0, z1); return; }
      world.style.setProperty('--is', 1);
      var rx = cam.rx * Math.PI / 180, diag = Math.sqrt(PW * PW + PH * PH);
      var w = view.clientWidth || 640, hh = view.clientHeight || 360;
      var need = diag * Math.cos(rx) + (z1 - z0) * Math.sin(rx) + 90;
      var scale = Math.min(w * 0.92 / diag, hh * 0.92 / need) * (cam.solo && selI >= 0 ? 0.84 : 1);   // étage seul : un peu de marge, ses murs débordent du cadre calculé
      world.classList.toggle('all', selI < 0 && floors.length > 1);
      world.style.setProperty('--rz', cam.rz + 'deg'); world.style.setProperty('--rx', cam.rx + 'deg');
      world.style.transform = 'translate(-50%,-50%) scale(' + scale.toFixed(3) + ') rotateX(' + cam.rx + 'deg) rotateZ(' + cam.rz + 'deg) translateZ(' + (-(z0 + z1) / 2) + 'px)';
      // étage montré seul (visite, borne) : la perspective fait glisser le plan incliné vers le bas à droite ; on mesure où il tombe et on le recentre.
      // Pas pendant une transition (la mesure serait celle d'un état intermédiaire) : l'appel suivant, sans transition, corrige.
      if (cam.solo && selI >= 0 && !ease) {
        world.style.left = '50%'; world.style.top = '50%';
        var r = world.getBoundingClientRect(), v = view.getBoundingClientRect();
        if (r.width && v.width) {
          var dx = Math.round(r.left + r.width / 2 - (v.left + v.width / 2)), dy = Math.round(r.top + r.height / 2 - (v.top + v.height / 2));
          world.style.left = 'calc(50% - ' + dx + 'px)'; world.style.top = 'calc(50% - ' + dy + 'px)';
        }
      } else if (!cam.solo || selI < 0) { world.style.left = ''; world.style.top = ''; }
    }

    /**
     * Plein écran (diffusion, borne, §201) : cadré bord à bord sur ce qu'on montre (bâtiment, étage dessiné, pièce choisie), dans la zone libre
     * cam.area (sans les commandes posées par-dessus ni la feuille ouverte). cam.room : pièce zoomée ; cam.flat : vue de dessus (2D) ; cam.zoom : pincer.
     * Les étiquettes gardent leur taille à l'écran (--is = 1 / échelle).
     */
    function fitCamera(cam, ease, selI, z0, z1) {
      var flat = !!cam.flat, rx = flat ? 0 : cam.rx, rz = flat ? 0 : cam.rz, PH = world.offsetHeight || PW / 1.6;
      var A = cam.area || { x: 0, y: 0, w: view.clientWidth || 640, h: view.clientHeight || 360 };
      var fe = selI >= 0 ? world.querySelector('.hpl-floor[data-f="' + plan.floors[selI].id + '"]') : null;
      var box = { x: 0, y: 0, w: PW, h: PH }, target = null, depth = z1 - z0;
      if (fe && cam.room) {
        target = fe.querySelector('[data-room="' + cam.room + '"]');
        if (target) {
          var g = target.parentNode, pad = P * 0.5;
          box = { x: g.offsetLeft + target.offsetLeft - pad, y: g.offsetTop + target.offsetTop - pad, w: target.offsetWidth + 2 * pad, h: target.offsetHeight + 2 * pad };
          depth = WH;
        }
      }
      if (fe && !target) {
        target = fe.querySelector('.hpl-grid') || fe.querySelector('.hpl-plate');
        if (target && target.classList.contains('hpl-grid')) box = { x: target.offsetLeft - 8, y: target.offsetTop - 8, w: target.offsetWidth + 16, h: target.offsetHeight + 16 };
      }
      if (flat) depth = 0;
      var scale = Logic.fit(box, rz, rx, depth, A, !!cam.spin) * (flat ? 0.96 : 0.9) * (cam.zoom || 1);
      var dx = box.x + box.w / 2 - PW / 2, dy = box.y + box.h / 2 - PH / 2, cx = A.x + A.w / 2, cy = A.y + A.h / 2;
      world.classList.toggle('all', selI < 0 && plan.floors.length > 1);
      world.style.setProperty('--rz', rz + 'deg'); world.style.setProperty('--rx', rx + 'deg'); world.style.setProperty('--is', (1 / scale).toFixed(4));
      view.style.perspectiveOrigin = cx + 'px ' + cy + 'px';
      // caméra éloignée à l'échelle de l'écran : un plan très agrandi garde ses proportions (sinon le bord proche déborde)
      view.style.perspective = Math.round(Math.max(2600, 3.5 * Math.max(view.clientWidth || 0, view.clientHeight || 0))) + 'px';
      world.style.left = cx + 'px'; world.style.top = cy + 'px';
      world.style.transform = 'translate(-50%,-50%) scale(' + scale.toFixed(4) + ') rotateX(' + rx + 'deg) rotateZ(' + rz + 'deg) translateZ(' + (-(z0 + z1) / 2) + 'px) translate(' + (-dx) + 'px,' + (-dy) + 'px)';
      // la perspective décale un plan incliné : on mesure où tombe ce qu'on cadre et on le recentre (pas pendant une transition : l'appel suivant corrige)
      if (target && !ease) {
        var r = target.getBoundingClientRect(), v = view.getBoundingClientRect();
        if (r.width && v.width) {
          // mesures ramenées aux px de la vue : le player peut être agrandi ou réduit (transform, zoom) autour du widget
          var kx = v.width / (view.clientWidth || v.width), ky = v.height / (view.clientHeight || v.height);
          var mx = Math.round((r.left + r.width / 2 - v.left) / kx - cx), my = Math.round((r.top + r.height / 2 - v.top) / ky - cy);
          world.style.left = (cx - mx) + 'px'; world.style.top = (cy - my) + 'px';
        }
      }
    }

    /** Applique ce que Logic.scene a décidé ; ents sert aux icônes. */
    function paint(sc, ents) {
      plan.devices.forEach(function (d) {
        var v = sc.pins[d.id] || { show: false }, pin = world.querySelector('[data-p="' + d.id + '"]'), e = ents[d.id];
        if (pin) {
          pin.style.display = v.show ? '' : 'none';
          if (v.show) {
            var chip = pin.querySelector('.hpl-chip');
            chip.setAttribute('aria-label', e.name);
            if (v.big) {   // borne, pièce zoomée : gros bouton rond + nom et valeur
              chip.className = 'hpl-chip big ' + v.st + (v.pulse ? ' pulse' : '') + (v.sel ? ' sel' : '');
              chip.innerHTML = '<span class="hpl-bi">' + Ha.icon(e) + '</span><span class="hpl-bl"><span></span><small></small></span>';
              chip.querySelector('.hpl-bl span').textContent = e.name;
              chip.querySelector('.hpl-bl small').textContent = v.text;
            } else {
              chip.className = 'hpl-chip ' + v.st + (v.bare ? ' novals' : '') + (v.pulse ? ' pulse' : '') + (v.dim ? ' dim' : '') + (v.sel ? ' sel' : '');
              chip.innerHTML = Ha.icon(e) + '<span class="hpl-v"></span>';
              chip.querySelector('.hpl-v').textContent = v.text;
            }
            pin.classList.toggle('small', !!v.bare && !v.big);
            pin.classList.toggle('bigpin', !!v.big);
          }
        }
        var g = world.querySelector('[data-g="' + d.id + '"]');
        if (g) {
          if (v.glow) {
            var R = g.parentNode.classList.contains('hpl-grid') ? (1.2 + 1.8 * v.glow.b / 100) * P : (0.07 + 0.11 * v.glow.b / 100) * PW;
            g.style.width = g.style.height = 2 * R + 'px'; g.style.marginLeft = g.style.marginTop = -R + 'px';
            g.style.background = 'radial-gradient(circle, ' + rgba(v.glow.col, 0.7) + ' 0%, ' + rgba(v.glow.col, 0.22) + ' 45%, ' + rgba(v.glow.col, 0) + ' 70%)'; g.style.opacity = 1;
          } else g.style.opacity = 0;
        }
        var op = d.seg && world.querySelector('[data-o="' + d.id + '"]');
        if (op) {   // volet qui descend, fenêtre ou porte ouverte (la porte pivote)
          op.classList.toggle('open', !!v.open);
          var sh = op.querySelector('.hpl-shut');
          if (sh && v.shut != null) sh.style.height = v.shut + '%';
        }
      });
      plan.floors.forEach(function (f) {
        (f.rooms || []).forEach(function (r) {
          var k = f.id + '/' + r.id, info = sc.rooms[k]; if (!info) return;
          var rm = world.querySelector('.hpl-floor[data-f="' + f.id + '"] [data-room="' + r.id + '"]');
          if (rm) { rm.style.background = info.color; rm.classList.toggle('lit', info.lit); rm.classList.toggle('sel', !!info.sel); }
          var rp = world.querySelector('[data-rp="' + k + '"]'); if (!rp) return;
          rp.style.display = info.show ? '' : 'none';
          if (!info.show) return;
          var rc = rp.querySelector('.hpl-chip'), html = '<span class="hpl-rn2"></span>';
          rc.className = 'hpl-chip hpl-room-chip' + (info.quiet ? ' quiet' : '') + (info.lights ? ' lit' : '');
          if (info.lights) html += '<span class="hpl-g on">' + Ha.icon({ domain: 'light', on: true }) + info.lights + '</span>';
          if (info.t != null) html += '<span class="hpl-g">' + Ha.icon({ domain: 'climate' }) + Ha.deg(Math.round(info.t * 10) / 10) + '</span>';
          rc.innerHTML = html; rc.querySelector('.hpl-rn2').textContent = info.name; rc.setAttribute('aria-label', info.name);
        });
      });
    }

    /** Ce qui est sous le doigt : 'entité', 'room:étage/pièce' (étiquette ou sol de la pièce) ou null. */
    function hit(target) {
      if (!target.closest) return null;
      var chip = target.closest('.hpl-chip'), pin = chip && chip.closest('.hpl-pin');
      if (pin) return pin.getAttribute('data-p') || 'room:' + pin.getAttribute('data-rp');
      var rm = target.closest('.hpl-room'), fl = target.closest('.hpl-floor');
      if (rm && fl) return 'room:' + fl.getAttribute('data-f') + '/' + rm.getAttribute('data-room');
      return fl ? 'floor:' + fl.getAttribute('data-f') : null;   // vue d'ensemble de la borne : toucher un étage l'ouvre
    }

    function empty(text) { plan = null; world.innerHTML = '<div class="hpl-none"></div>'; world.firstChild.textContent = text; }

    return { build: build, camera: camera, paint: paint, hit: hit, empty: empty };
  }

  var renderers = { css3d: css3d };

  /* =============================== habillage commun ===============================
     Trois façons d'être montré :
     - widget « Plan du bâtiment » (sans vue) : comme avant ; barre tactile et panneau flottant si une commande est permise ;
     - vue de diffusion (plan.mode = signage) : visite (plan.tour : étapes vue d'ensemble / étage), étapes en bas à gauche, événements ;
     - vue interactive (plan.mode = interactive, borne) : Retour + fil d'Ariane en haut, rail des étages en bas à gauche, panneau de navigation à droite
       (étage → pièce → appareil), serrure en appui maintenu 1,5 s ; 60 s sans toucher : retour à l'étage d'accueil. */
  function mount(el, opts) {
    opts = opts || {};
    var now = function () { return opts.now ? opts.now() : Date.now(); };
    el.classList.add('hpl');
    el.innerHTML = '<div class="hpl-stage"><div class="hpl-view"></div><div class="hpl-bar"></div><div class="hpl-top"></div><div class="hpl-tr"></div><div class="hpl-rail"></div>'
      + '<div class="hpl-tour"></div><div class="hpl-toast"></div><div class="hpl-sheet"></div></div><div class="hpl-sum"></div>';
    var view = el.querySelector('.hpl-view'), bar = el.querySelector('.hpl-bar'), toast = el.querySelector('.hpl-toast'), top = el.querySelector('.hpl-top'),
      rail = el.querySelector('.hpl-rail'), tourEl = el.querySelector('.hpl-tour'), sum = el.querySelector('.hpl-sum'), sheet = el.querySelector('.hpl-sheet'),
      tr = el.querySelector('.hpl-tr');
    var R = renderers[opts.renderer] ? renderers[opts.renderer](view) : css3d(view);
    var RX_MIN = 40, RX_MAX = 78, NAV_IDLE = 60000, HOLD_MS = 1500, LONG_MS = 550, RZ0 = -24;
    var last = null, built = '', byId = {}, pending = {}, confirm = {}, panelId = null, holding = false, prev = null, sumHtml = '', layout = '';
    var cam = { rz: -32, rx: 62, sel: 'all', zoom: 1 }, userUntil = 0, focusUntil = 0, focusTimer = 0, toastTimer = 0, pulseId = null;
    var spinOverride = null, spinUntil = 0, dead = false, timers = [];
    var tour0 = Date.now(), tourKey = '', nav = null, navTouched = 0, sheetOpen = false, flat = false, uiKey = '';

    function interactive() { return opts.mode === 'interactive' || (opts.mode !== 'signage' && !!(last && last.plan && (last.plan.mode === 'interactive' || (!last.plan.mode && last.plan.touch)))); }
    function navMode() { return !!(last && last.plan && last.plan.mode === 'interactive' && opts.mode !== 'signage'); }
    function tvMode() { return !!(last && last.plan && last.plan.mode === 'signage'); }
    function fitMode() { return navMode() || tvMode(); }   // vues enregistrées : plein écran (§201) ; le widget sans vue garde son cadrage
    function port() { return el.clientHeight > el.clientWidth; }
    function touring() { return !!(last && last.plan && last.plan.mode === 'signage' && last.plan.tour && last.plan.tour.length); }
    function spinning() {
      if (navMode()) return false;
      if (touring()) return cam.sel === 'all';
      return Date.now() < spinUntil ? spinOverride : !!(last && last.plan && last.plan.spin);
    }

    function eView(e) {   // état affiché : celui qu'on vient de commander, jusqu'à confirmation de Home Assistant (8 s au plus)
      var p = pending[e.id];
      if (p && (Date.now() > p.until || e[p.field] === p.value)) { delete pending[e.id]; p = null; }
      if (!p) return e;
      var v = {}, k; for (k in e) v[k] = e[k];
      v[p.field] = p.value;
      if (p.field === 'state' && e.domain === 'lock') v.on = p.value === 'unlocked';
      return v;
    }
    function shown() { var m = {}, k; for (k in byId) m[k] = eView(byId[k]); return m; }

    function send(e, body, field, value) {
      if (field) pending[e.id] = { field: field, value: value, until: Date.now() + 8000 };
      body.entityId = e.id;
      update();
      var p = opts.command ? opts.command(body) : Promise.reject(new Error('aucune commande'));
      return p.then(function () { setTimeout(update, 8100); }, function () {
        delete pending[e.id];
        say('Commande non envoyée. Réessayez.', true);
        update();
      });
    }
    function say(text, bad, ms) {
      toast.textContent = text; toast.className = 'hpl-toast show' + (bad ? ' bad' : '');
      clearTimeout(toastTimer); toastTimer = setTimeout(function () { toast.className = 'hpl-toast'; }, ms || 5000);
    }

    function relayout() {
      if (!last || !last.plan) return false;
      // borne : le plan sur tout l'écran, la feuille passe par-dessus ; diffusion : le résumé en pastilles dans le coin (§201)
      // affichage « Compact » (§202) : le résumé seul, quelle que soit la taille
      var m = navMode() ? 'full' : tvMode() ? (last.plan.summary === 'none' ? 'none' : 'pills') : last.plan.mode === 'compact' ? 'only' : Logic.layoutMode(last.plan.summary, el.clientWidth, el.clientHeight);
      if (m === layout) return false;
      layout = m; sumHtml = '';
      el.classList.remove('s-right', 's-left', 's-top', 's-none', 's-only', 's-full', 's-pills');
      el.classList.add('s-' + m);
      return true;
    }
    /** Taille des textes et orientation (borne, diffusion) : tout grandit avec l'écran. */
    function sizeUi() {
      var kind = navMode() ? 'kiosk' : tvMode() ? 'tv' : '', k = kind + el.clientWidth + 'x' + el.clientHeight;
      if (k === uiKey) return;
      uiKey = k;
      el.style.fontSize = kind ? Logic.uiSize(el.clientWidth, el.clientHeight, kind) + 'px' : '';
      el.classList.toggle('hpl-port', !!kind && port());
    }
    /** Zone libre du plan (px de la vue) : l'écran moins les commandes posées par-dessus et la feuille ouverte. */
    function area() {
      var W = view.clientWidth || 640, H = view.clientHeight || 360, fs = parseFloat(root.getComputedStyle ? root.getComputedStyle(el).fontSize : 16) || 16, m = fs * 0.8;
      // en 3D, les coins du plan incliné laissent la place à la barre des étages ; en vue de dessus (rectangle plein), on la réserve
      var A = { x: m, y: m + fs * 2.4, w: W - 2 * m, h: H - 2 * m - fs * (navMode() && flat && !(sheetOpen && port()) ? 7 : 4) };
      if (navMode() && sheetOpen) {
        if (port()) A.h = H - sum.offsetHeight - A.y - (nav && nav.room ? m : fs * 4.6);
        else A.w = W - sum.offsetWidth - 2 * m;
      }
      A.w = Math.max(80, A.w); A.h = Math.max(80, A.h);
      return A;
    }
    function camera(ease) {
      if (!last || !last.plan) return;
      cam.solo = (navMode() && !!nav && nav.floor !== 'all') || touring();
      cam.fit = fitMode();
      cam.area = cam.fit ? area() : null;
      cam.room = navMode() && nav && nav.room && nav.floor !== 'all' ? nav.room : null;
      cam.flat = navMode() && flat;
      cam.spin = spinning();
      el.classList.toggle('flat', !!cam.flat);
      R.camera(cam, ease);
      if (ease) setTimeout(function () { if (!dead) R.camera(cam, false); }, 900);
    }
    function ensureNav() {
      if (!navMode()) return;
      if (!nav || (nav.floor !== 'all' && !last.plan.floors.some(function (f) { return f.id === nav.floor; }))) nav = { floor: Logic.homeFloor(last.plan), room: null, dev: null };
    }
    function kioskLevel() { return !nav ? 'floor' : nav.floor === 'all' ? 'all' : nav.room ? 'room' : 'floor'; }

    function update() {
      if (!last || !last.plan || dead) return;
      ensureNav();
      var ents = shown();
      R.paint(Logic.scene(last.plan, ents, {
        labels: last.plan.labels, hideOff: last.plan.hideOff, heat: last.plan.heat, decimals: last.decimals, now: now(), pulseId: pulseId,
        panelId: navMode() ? nav && nav.dev : panelId, room: navMode() && nav && nav.room ? nav.floor + '/' + nav.room : null,
        kiosk: navMode() ? kioskLevel() : tvMode() && cam.sel === 'all' ? 'all' : null   // diffusion, vue d'ensemble : les noms des étages seuls
      }), ents);
      if (navMode()) drawNav(ents); else drawSummary(ents);
      if (panelId && !navMode()) drawSheet();
    }

    function drawSummary(ents) {
      relayout();
      if (layout === 'none') { if (sumHtml !== '-') { sum.innerHTML = ''; sumHtml = '-'; } return; }
      if (layout === 'pills') {   // diffusion plein écran : pastilles dans le coin
        var ph = Logic.pillsHtml(Logic.summary(last.plan, ents, { decimals: last.decimals, now: now() }));
        if (ph !== sumHtml) { sumHtml = ph; sum.innerHTML = ph; }
        return;
      }
      var html = Logic.summaryHtml(Logic.summary(last.plan, ents, { decimals: last.decimals, now: now() }), layout === 'only' ? 'right' : layout, last.title, last.live);
      if (html === sumHtml) return;   // réécrit seulement quand il change : le bandeau ne repart pas à zéro
      sumHtml = html; sum.innerHTML = html;
      if (layout === 'top') {   // défile seulement s'il déborde ; la piste est doublée pour boucler sans saut
        var track = sum.firstChild;
        if (track.scrollWidth > sum.clientWidth) {
          track.innerHTML += track.innerHTML;
          track.style.animationDuration = Math.max(12, Math.round(track.scrollWidth / 2 / 40)) + 's';
          track.classList.add('run');
        }
      }
    }

    /* ----- visite de la diffusion ----- */
    function drawTour(at) {
      var steps = last.plan.tour, key = at ? at.i + '' : '';
      if (key === tourKey) return;
      tourKey = key;
      tourEl.innerHTML = '';
      if (tvMode()) {   // diffusion plein écran : nom du plan et étape en cours, en haut à gauche
        top.innerHTML = '<div class="hpl-ttl"><b></b><span></span></div>';
        top.querySelector('b').textContent = last.title || 'Bâtiment';
        top.querySelector('span').textContent = at ? (at.step.floor ? floorName(at.step.floor) : "Vue d'ensemble") : '';
      }
      if (!at) return;
      steps.forEach(function (s, i) {
        var x = document.createElement('span');
        x.className = 'hpl-ts' + (i === at.i ? ' on' : '');
        x.textContent = s.floor ? floorName(s.floor) : "Vue d'ensemble";
        if (i === at.i) x.style.setProperty('--d', Math.round(at.left / 100) / 10 + 's');
        tourEl.appendChild(x);
      });
    }
    function floorName(fid) { var f = last.plan.floors.filter(function (x) { return x.id === fid; })[0]; return f ? f.name : ''; }
    function tourTick() {
      if (!touring()) { if (tourKey) { tourKey = ''; tourEl.innerHTML = ''; } return; }
      if (focusUntil) return;   // un événement a la main ; la visite reprend après
      var at = Logic.tourAt(last.plan.tour, Date.now() - tour0), sel = at.step.floor || 'all';
      if (sel !== cam.sel) { cam.sel = sel; update(); camera(true); }
      drawTour(at);
    }

    /* ----- barre tactile du widget : étages, rotation par pas, interrupteur de rotation ----- */
    function drawBar() {
      bar.innerHTML = '';
      if (!last || !last.plan || !interactive() || navMode()) return;
      function b(label, key, on) {
        var x = document.createElement('button'); x.type = 'button'; x.className = 'hpl-k' + (on ? ' on' : ''); x.textContent = label;
        x.onclick = function () { pick(key); }; return x;
      }
      bar.appendChild(b('Tous', 'all', cam.sel === 'all'));
      last.plan.floors.slice().reverse().forEach(function (f) { bar.appendChild(b(f.name, f.id, cam.sel === f.id)); });
      var sp = document.createElement('span'); sp.className = 'hpl-sp'; bar.appendChild(sp);
      bar.appendChild(b('⟲', 'rl', false)); bar.appendChild(b('⟳', 'rr', false));
      if (last.plan.spinSwitch !== false) {
        var on = spinning(), x = document.createElement('button');
        x.type = 'button'; x.className = 'hpl-k hpl-rot' + (on ? ' on' : ''); x.setAttribute('aria-pressed', on ? 'true' : 'false');
        x.innerHTML = 'Rotation <i class="hpl-sw"></i>';
        x.onclick = function () { spinOverride = !spinning(); spinUntil = Date.now() + 120000; userUntil = 0; drawBar(); };
        bar.appendChild(x);
      }
    }
    function pick(key) {
      userUntil = Date.now() + 8000;
      if (key === 'rl') cam.rz -= 45; else if (key === 'rr') cam.rz += 45; else cam.sel = key;
      camera(true); drawBar();
    }

    /* ----- commandes d'un appareil (panneau flottant du widget ou panneau de la borne) ----- */
    function controls(box, raw, hold) {
      var e = eView(raw), d = e.domain, c = function (a) { return Ha.can(raw, a); };
      function add(tag, cls, text) { var x = document.createElement(tag); x.className = cls; if (text != null) x.textContent = text; box.appendChild(x); return x; }
      function rowBtn(cls, label, fn, disabled) { var x = add('button', 'hpl-sb ' + cls, label); x.type = 'button'; if (disabled) x.disabled = true; x.onclick = fn; return x; }
      function range(value, min, action, label) {
        var r = add('input', 'hpl-range'); r.type = 'range'; r.min = min; r.max = 100; r.step = 1; r.value = value == null ? 0 : value; r.setAttribute('aria-label', label);
        r.onpointerdown = function () { holding = true; }; r.ontouchstart = function () { holding = true; };
        r.onchange = function () { holding = false; send(raw, { action: action, value: +r.value }, action === 'brightness' ? 'brightness' : 'position', +r.value); };
        r.onblur = function () { holding = false; };
      }
      if (raw.available === false) { add('div', 'hpl-sl', 'Indisponible dans Home Assistant.'); return; }
      if (!(raw.ctrl || []).length) { add('div', 'hpl-sl', d === 'binary_sensor' || d === 'sensor' ? 'Capteur : lecture seule.' : 'Lecture seule sur cet écran.'); return; }
      if (d === 'switch' || d === 'input_boolean' || d === 'fan' || d === 'light') {
        if (c('on')) rowBtn(e.on ? 'on' : '', e.on ? 'Allumé · éteindre' : 'Éteint · allumer', function () { send(raw, { action: e.on ? 'off' : 'on' }, 'on', !e.on); }, !!pending[raw.id]);
        if (d === 'light' && e.on && c('brightness') && last.dimmer !== false && raw.dimmable) { add('div', 'hpl-sl', 'Intensité'); range(e.brightness, 1, 'brightness', 'Intensité de ' + e.name); }
        if (d === 'light' && e.on && c('color') && raw.colorable) {
          var dots = add('div', 'hpl-dots');
          Ha.COLORS.forEach(function (col) {
            var x = document.createElement('button'); x.type = 'button'; x.className = 'hpl-dot' + (raw.color && raw.color.toUpperCase() === col ? ' sel' : ''); x.style.background = col;
            x.setAttribute('aria-label', 'Couleur ' + col); x.onclick = function () { send(raw, { action: 'color', color: col }); }; dots.appendChild(x);
          });
        }
      } else if (d === 'climate') {
        add('div', 'hpl-sl', 'Consigne ' + Ha.deg(e.target) + (raw.hvac && Ha.HVAC[raw.hvac] ? ' · ' + Ha.HVAC[raw.hvac] : ''));
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
        if (!c(act) || raw.state === 'jammed') return;
        if (hold) {   // borne : appui maintenu 1,5 s
          var hb = rowBtn('hold', 'Maintenir pour ' + (act === 'lock' ? 'verrouiller' : 'déverrouiller'), function () {});
          hb.innerHTML = '<i></i>' + hb.textContent;
          var t = 0;
          var stop = function () { clearTimeout(t); hb.classList.remove('go'); holding = false; };
          hb.onpointerdown = function () { holding = true; hb.classList.add('go'); t = setTimeout(function () { stop(); send(raw, { action: act }, 'state', act === 'lock' ? 'locked' : 'unlocked'); }, HOLD_MS); };
          hb.onpointerup = stop; hb.onpointerleave = stop; hb.onpointercancel = stop;
          return;
        }
        var asking = confirm[raw.id] > Date.now();
        rowBtn(asking ? 'ask' : '', asking ? 'Confirmer ?' : act === 'lock' ? 'Verrouiller' : 'Déverrouiller', function () {
          if (!(confirm[raw.id] > Date.now())) { confirm[raw.id] = Date.now() + 4000; drawSheet(); setTimeout(drawSheet, 4100); return; }
          delete confirm[raw.id];
          send(raw, { action: act }, 'state', act === 'lock' ? 'locked' : 'unlocked');
        });
      }
    }

    /* ----- panneau flottant du widget : appareils d'une pièce, commande d'un appareil ----- */
    function drawRoomSheet() {
      var parts = panelId.slice(5).split('/'), r = roomsOf(last.plan, parts[0]).filter(function (x) { return x.id === parts[1]; })[0];
      var ds = devicesIn(parts[0], parts[1]);
      if (!r || !ds.length) { closeSheet(); return; }
      sheet.className = 'hpl-sheet open'; sheet.innerHTML = '';
      var head = document.createElement('div'); head.className = 'hpl-sh';
      head.innerHTML = '<span class="hpl-sn"></span><button type="button" class="hpl-sx" aria-label="Fermer">×</button>';
      head.querySelector('.hpl-sn').textContent = r.name; head.querySelector('.hpl-sx').onclick = closeSheet;
      sheet.appendChild(head);
      ds.forEach(function (d) { var x = deviceRow(d, false); x.onclick = function () { openSheet(d.id); }; sheet.appendChild(x); });
    }
    function drawSheet() {
      if (panelId && panelId.indexOf('room:') === 0) { drawRoomSheet(); return; }
      var raw = byId[panelId]; if (absent(raw)) { closeSheet(); return; }
      if (holding) return;
      var e = eView(raw);
      sheet.className = 'hpl-sheet open'; sheet.innerHTML = '';
      var head = document.createElement('div'); head.className = 'hpl-sh';
      head.innerHTML = Ha.icon(e) + '<span class="hpl-sn"></span><span class="hpl-sv"></span><button type="button" class="hpl-sx" aria-label="Fermer">×</button>';
      head.querySelector('.hpl-sn').textContent = e.name;
      head.querySelector('.hpl-sv').textContent = Ha.value(e, last.decimals, now());
      head.querySelector('.hpl-sx').onclick = closeSheet;
      sheet.appendChild(head);
      controls(sheet, raw, false);
    }
    function closeSheet() { panelId = null; holding = false; sheet.className = 'hpl-sheet'; sheet.innerHTML = ''; }
    function openSheet(id) { panelId = id; drawSheet(); }

    function devicesIn(fid, rid) {
      return last.plan.devices.filter(function (d) {
        var e = byId[d.id];
        return d.floor === fid && (rid === undefined || (d.room || null) === rid) && !absent(e) && !(last.plan.hideOff !== false && e.available === false);
      });
    }
    /** Ligne d'un appareil (panneaux) : icône, nom, valeur ; à la borne, un interrupteur direct pour lumières et prises. */
    function deviceRow(d, quick) {
      var e = eView(byId[d.id]), st = state(e), x = document.createElement('button');
      x.type = 'button'; x.className = 'hpl-sb hpl-srow ' + st;
      x.innerHTML = Ha.icon(e) + '<span class="hpl-sn"></span><span class="hpl-sv"></span>';
      x.querySelector('.hpl-sn').textContent = e.name;
      var v = x.querySelector('.hpl-sv');
      if (quick && Ha.can(byId[d.id], 'on') && (e.domain === 'light' || e.domain === 'switch' || e.domain === 'input_boolean' || e.domain === 'fan')) {
        v.className = 'hpl-tg' + (e.on ? ' on' : ''); v.setAttribute('role', 'switch'); v.setAttribute('aria-checked', e.on ? 'true' : 'false');
        v.onclick = function (ev) { ev.stopPropagation(); touch(); send(byId[d.id], { action: e.on ? 'off' : 'on' }, 'on', !e.on); };
      } else v.textContent = Ha.value(e, last.decimals, now());
      return x;
    }

    /* ----- borne plein écran (§201) : bâtiment → étage → pièce (zoom) → appareil ----- */
    function touch() { navTouched = Date.now(); }
    function go(n) {
      nav = n; touch();
      cam.sel = nav.floor; cam.zoom = 1;
      cam.rx = nav.floor === 'all' ? 56 : nav.room ? 34 : 46;   // pièce : vue plus plongeante ; étage : moins incliné qu'avant pour gagner de la place
      sheetOpen = !!(nav.room || nav.dev);                       // la liste ne vient que lorsqu'on en a besoin
      update(); camera(true);
    }
    function navTo(id) {
      if (id.indexOf('floor:') === 0) { go({ floor: id.slice(6), room: null, dev: null }); return; }
      if (id.indexOf('room:') === 0) {
        var p = id.slice(5).split('/');
        go(nav && nav.floor === 'all' ? { floor: p[0], room: null, dev: null } : { floor: p[0], room: p[1], dev: null });
        return;
      }
      var d = last.plan.devices.filter(function (x) { return x.id === id; })[0];
      if (d) go({ floor: d.floor, room: d.room || null, dev: d.id });
    }
    /** Toucher sur le plan : un étage (vue d'ensemble), une pièce (zoom) ; dans la pièce, une lumière ou une prise s'allume ou s'éteint, un autre appareil s'ouvre. */
    function tapNav(id) {
      if (!id) return;
      if (nav && nav.floor === 'all') { var f = id.indexOf('floor:') === 0 ? id.slice(6) : id.indexOf('room:') === 0 ? id.slice(5).split('/')[0] : null; if (f) go({ floor: f, room: null, dev: null }); return; }
      if (id.indexOf('floor:') === 0) return;
      var d = id.indexOf('room:') === 0 ? null : last.plan.devices.filter(function (x) { return x.id === id; })[0];
      if (d && nav && nav.room && d.room === nav.room && Logic.quick(byId[id])) { toggle(byId[id]); return; }
      navTo(id);
    }
    function toggle(raw) {
      var e = eView(raw);
      send(raw, { action: e.on ? 'off' : 'on' }, 'on', !e.on);
      say(e.name + (e.on ? ' : éteint' : ' : allumé'), false, 1600);
    }
    /** Allumer ou éteindre d'un coup les lumières commandables d'une pièce ou d'un étage. */
    function switchAll(ds, on) {
      ds.forEach(function (d) { var raw = byId[d.id]; if (raw.domain === 'light' && Logic.quick(raw) && !!eView(raw).on !== on) send(raw, { action: on ? 'on' : 'off' }, 'on', on); });
    }
    function lightsOn(ds) { return ds.filter(function (d) { var e = eView(byId[d.id]); return e.domain === 'light' && e.on && Logic.quick(byId[d.id]); }).length; }
    function crumb(label, cur, fn) {
      var b = document.createElement('button'); b.type = 'button'; b.className = 'hpl-cr' + (cur ? ' cur' : ''); b.textContent = label;
      if (fn) b.onclick = fn; return b;
    }
    function btn(cls, html, fn) { var b = document.createElement('button'); b.type = 'button'; b.className = cls; b.innerHTML = html; b.onclick = fn; return b; }
    function roomOf(fid, rid) { return roomsOf(last.plan, fid).filter(function (r) { return r.id === rid; })[0]; }
    function floorAlerts(fid) { return devicesIn(fid).filter(function (d) { return state(eView(byId[d.id])) === 'alert'; }); }

    function drawNav(ents) {
      relayout(); ensureNav();
      var lvl = kioskLevel(), rname = nav.room ? (roomOf(nav.floor, nav.room) || {}).name : null;
      el.classList.toggle('sheet', sheetOpen);
      el.classList.toggle('in-room', lvl === 'room');
      // haut à gauche : Retour (pas sur la vue d'ensemble) et fil d'Ariane
      top.innerHTML = '';
      if (lvl !== 'all' || nav.dev) top.appendChild(btn('hpl-up', '‹ Retour', function () { go(Logic.navUp(nav, last.plan)); }));
      var trail = document.createElement('div'); trail.className = 'hpl-crumbs'; top.appendChild(trail);
      var sep = function () { var i = document.createElement('i'); i.textContent = '›'; trail.appendChild(i); };
      trail.appendChild(crumb(last.title || 'Bâtiment', lvl === 'all', last.plan.floors.length > 1 ? function () { go({ floor: 'all', room: null, dev: null }); } : null));
      if (lvl !== 'all') { sep(); trail.appendChild(crumb(floorName(nav.floor), lvl === 'floor' && !nav.dev, function () { go({ floor: nav.floor, room: null, dev: null }); })); }
      if (rname) { sep(); trail.appendChild(crumb(rname, !nav.dev, function () { go({ floor: nav.floor, room: nav.room, dev: null }); })); }
      if (nav.dev && byId[nav.dev]) { sep(); trail.appendChild(crumb(byId[nav.dev].name, true)); }
      // haut à droite : ouvrir la liste, vue 3D / 2D
      tr.innerHTML = '';
      if (!sheetOpen) tr.appendChild(btn('hpl-tb', '☰ <span></span>', function () { sheetOpen = true; touch(); update(); camera(true); }));
      if (!sheetOpen) tr.lastChild.querySelector('span').textContent = lvl === 'all' ? 'Étages' : lvl === 'room' ? 'Appareils' : 'Pièces';
      var seg = document.createElement('div'); seg.className = 'hpl-seg';
      seg.appendChild(btn(flat ? '' : 'on', '3D', function () { flat = false; touch(); update(); camera(true); }));
      seg.appendChild(btn(flat ? 'on' : '', '2D', function () { flat = true; touch(); update(); camera(true); }));
      tr.appendChild(seg);
      // bas à gauche : la vue d'ensemble et les étages, du haut vers le bas
      rail.innerHTML = '';
      if (last.plan.floors.length > 1) rail.appendChild(btn('hpl-fl' + (lvl === 'all' ? ' on' : ''), '<span>Tout</span><small>' + plural(last.plan.floors.length, 'étage', 'étages') + '</small>', function () { go({ floor: 'all', room: null, dev: null }); }));
      last.plan.floors.slice().reverse().forEach(function (f) {
        var nr = (f.rooms || []).length, nd = last.plan.devices.filter(function (d) { return d.floor === f.id; }).length, na = floorAlerts(f.id).length;
        var b = btn('hpl-fl' + (lvl !== 'all' && f.id === nav.floor ? ' on' : ''), '<span></span><small></small>', function () { go({ floor: f.id, room: null, dev: null }); });
        b.firstChild.textContent = f.name;
        b.lastChild.textContent = (nr ? plural(nr, 'pièce', 'pièces') : plural(nd, 'appareil', 'appareils')) + (na ? ' · ⚠ ' + na : '');
        rail.appendChild(b);
      });
      if (holding) return;   // un curseur ou une serrure en cours : la feuille ne bouge pas
      // feuille (à droite en paysage, en bas en portrait), par-dessus le plan
      sum.innerHTML = ''; sumHtml = 'nav';
      var box = document.createElement('div'); box.className = 'hpl-nav'; sum.appendChild(box);
      var head = document.createElement('div'); head.className = 'hpl-nh'; box.appendChild(head);
      var body = document.createElement('div'); body.className = 'hpl-nb'; box.appendChild(body);
      function title(text, sub, iconHtml) {
        head.innerHTML = '<div class="hpl-nt"><h3>' + (iconHtml || '') + '<span></span></h3><small></small></div>';
        head.querySelector('h3 span').textContent = text; head.querySelector('small').textContent = sub || '';
        head.appendChild(btn('hpl-nx', '×', function () { sheetOpen = false; if (nav.dev) nav = Logic.navUp(nav, last.plan); touch(); update(); camera(true); }));
        head.lastChild.setAttribute('aria-label', 'Fermer');
      }
      function wide(label, fn) { body.appendChild(btn('hpl-sb hpl-wide', '', fn)); body.lastChild.textContent = label; }
      if (nav.dev && byId[nav.dev]) {
        var raw = byId[nav.dev], e = eView(raw);
        title(e.name, [rname, floorName(nav.floor)].filter(Boolean).join(' · '), Ha.icon(e));
        var big = document.createElement('div'); big.className = 'hpl-big'; big.textContent = Ha.value(e, last.decimals, now()); body.appendChild(big);
        controls(body, raw, true);
        if (nav.room) wide('‹ Tous les appareils de la pièce', function () { go({ floor: nav.floor, room: nav.room, dev: null }); });
      } else if (lvl === 'room') {
        var ds = devicesIn(nav.floor, nav.room), lit = lightsOn(ds);
        title(rname || '', floorName(nav.floor) + ' · ' + plural(ds.length, 'appareil', 'appareils'));
        if (!ds.length) { var h0 = document.createElement('div'); h0.className = 'hpl-hint'; h0.textContent = 'Aucun appareil dans cette pièce.'; body.appendChild(h0); }
        ds.forEach(function (d) { var x = deviceRow(d, true); x.onclick = function () { go({ floor: nav.floor, room: nav.room, dev: d.id }); }; body.appendChild(x); });
        var nl = ds.filter(function (d) { return byId[d.id].domain === 'light' && Logic.quick(byId[d.id]); }).length;
        if (nl > 1) wide(lit ? 'Éteindre la pièce' : 'Allumer la pièce', function () { touch(); switchAll(ds, !lit); });
      } else if (lvl === 'floor') {
        title(floorName(nav.floor), 'Touchez une pièce, ici ou sur le plan.');
        var rooms = roomsOf(last.plan, nav.floor), sc = Logic.scene(last.plan, ents, { labels: 'room', hideOff: last.plan.hideOff, now: now() });
        floorAlerts(nav.floor).forEach(function (d) { var x = deviceRow(d, false); x.onclick = function () { navTo(d.id); }; body.appendChild(x); });
        rooms.forEach(function (r) {
          var info = sc.rooms[nav.floor + '/' + r.id] || {};
          var x = btn('hpl-sb hpl-srow ' + (info.lights ? 'on' : ''), Ha.icon({ domain: info.lights ? 'light' : 'climate', on: !!info.lights }) + '<span class="hpl-sn"></span><span class="hpl-sv"></span>', function () { go({ floor: nav.floor, room: r.id, dev: null }); });
          x.querySelector('.hpl-sn').textContent = r.name;
          x.querySelector('.hpl-sv').textContent = [info.lights ? plural(info.lights, 'allumée', 'allumées') : '', info.t != null ? Ha.deg(Math.round(info.t * 10) / 10) : ''].filter(Boolean).join(' · ') || '—';
          body.appendChild(x);
        });
        devicesIn(nav.floor, null).forEach(function (d) { var x = deviceRow(d, true); x.onclick = function () { go({ floor: nav.floor, room: null, dev: d.id }); }; body.appendChild(x); });
        var fl = lightsOn(devicesIn(nav.floor));
        if (fl) wide("Éteindre l'étage (" + fl + ')', function () { touch(); switchAll(devicesIn(nav.floor), false); });
      } else {
        title(last.title || 'Bâtiment', 'Touchez un étage.');
        last.plan.floors.slice().reverse().forEach(function (f) {
          var fa = floorAlerts(f.id), fl2 = lightsOn(devicesIn(f.id)) + devicesIn(f.id).filter(function (d) { var x = eView(byId[d.id]); return x.domain === 'light' && x.on && !Logic.quick(byId[d.id]); }).length;
          var x = btn('hpl-sb hpl-srow ' + (fa.length ? 'alert' : fl2 ? 'on' : ''), Ha.icon(fa.length ? eView(byId[fa[0].id]) : { domain: 'light', on: !!fl2 }) + '<span class="hpl-sn"></span><span class="hpl-sv"></span>', function () { go({ floor: f.id, room: null, dev: null }); });
          x.querySelector('.hpl-sn').textContent = f.name;
          x.querySelector('.hpl-sv').textContent = fa.length ? fa.map(function (d) { return byId[d.id].name; }).join(', ') : fl2 ? plural(fl2, 'allumée', 'allumées') : '—';
          body.appendChild(x);
        });
      }
    }

    /* ----- rotation au doigt, toucher d'une pastille ou d'une pièce ----- */
    var drag = null, pts = {}, pinch = null;
    function npts() { return Object.keys(pts).length; }
    function spread() { var k = Object.keys(pts), a = pts[k[0]], b = pts[k[1]]; return Math.sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y)) || 1; }
    function down(ev) {
      pts[ev.pointerId] = { x: ev.clientX, y: ev.clientY };
      userUntil = Date.now() + 20000;   // on touche l'écran : la rotation s'arrête le temps de viser une pastille
      touch();
      try { view.setPointerCapture(ev.pointerId); } catch (x) {}
      if (npts() === 2 && navMode()) {   // borne : pincer pour zoomer
        if (drag) clearTimeout(drag.lp);
        drag = null; pinch = { d: spread(), z: cam.zoom || 1 }; return;
      }
      if (npts() > 1) return;
      drag = { x: ev.clientX, y: ev.clientY, rz: cam.rz, rx: cam.rx, id: R.hit(ev.target), moved: false, long: false };
      if (navMode() && drag.id && drag.id.indexOf(':') < 0) {   // appui long sur un appareil : ses commandes
        var d0 = drag;
        d0.lp = setTimeout(function () { if (drag === d0 && !d0.moved) { d0.long = true; navTo(d0.id); } }, LONG_MS);
      }
    }
    function move(ev) {
      if (pts[ev.pointerId]) pts[ev.pointerId] = { x: ev.clientX, y: ev.clientY };
      if (pinch) { cam.zoom = Math.max(0.7, Math.min(2.6, pinch.z * spread() / pinch.d)); camera(false); return; }
      if (!drag) return;
      var dx = ev.clientX - drag.x, dy = ev.clientY - drag.y;
      if (!drag.moved && Math.sqrt(dx * dx + dy * dy) < 8) return;
      drag.moved = true; clearTimeout(drag.lp); userUntil = Date.now() + 6000;
      if (navMode() && flat) return;   // vue de dessus : rien à tourner
      var lo = navMode() ? 20 : RX_MIN;
      cam.rz = drag.rz + dx * 0.35; cam.rx = Math.max(lo, Math.min(RX_MAX, drag.rx - dy * 0.2)); camera(false);
    }
    function up(ev) {
      if (ev) delete pts[ev.pointerId];
      if (pinch) { if (npts() < 2) pinch = null; drag = null; return; }
      if (!drag) return;
      clearTimeout(drag.lp);
      if (!drag.moved && !drag.long) {
        if (navMode()) tapNav(drag.id);
        else if (drag.id && drag.id.indexOf('floor:') !== 0) { if (interactive()) openSheet(drag.id); }
        else closeSheet();
      }
      drag = null;
    }
    function cancel(ev) { if (ev) delete pts[ev.pointerId]; if (drag) clearTimeout(drag.lp); drag = null; pinch = null; }
    function wheel(ev) {
      if (!navMode()) return;
      ev.preventDefault(); touch();
      cam.zoom = Math.max(0.7, Math.min(2.6, (cam.zoom || 1) * (ev.deltaY < 0 ? 1.1 : 0.9))); camera(false);
    }
    function recenter() { if (!navMode()) return; touch(); cam.zoom = 1; cam.rz = RZ0; camera(true); }   // double toucher : recadrer
    view.addEventListener('pointerdown', down); view.addEventListener('pointermove', move);
    view.addEventListener('pointerup', up); view.addEventListener('pointercancel', cancel);
    view.addEventListener('wheel', wheel, { passive: false }); view.addEventListener('dblclick', recenter);
    el.addEventListener('pointerdown', touch, true);

    /* ----- événements : porte, fenêtre ou mouvement qui change → message, pastille qui pulse, étage montré 6 s ----- */
    function events() {
      var snap = {};
      (last.entities || []).forEach(function (e) { snap[e.id] = state(e); });
      if (prev && last.plan.focus !== false) {
        last.plan.devices.forEach(function (d) {
          var e = byId[d.id], text = Logic.eventText(e, prev[d.id], snap[d.id]);
          if (!text) return;
          say(text, snap[d.id] === 'alert');
          pulseId = d.id; setTimeout(function () { if (pulseId === d.id) { pulseId = null; update(); } }, 6000);
          if (!navMode() && !panelId && Date.now() > userUntil) {
            var back = cam.sel; cam.sel = d.floor; focusUntil = Date.now() + 6000;
            camera(true);
            clearTimeout(focusTimer);
            focusTimer = setTimeout(function () { focusUntil = 0; if (!touring()) { cam.sel = back; camera(true); } else tourTick(); }, 6000);
          }
        });
      }
      prev = snap;
    }

    function setData(d) {
      if (dead) return;
      if (!d || !d.plan) { if (!last) { sum.textContent = ''; R.empty('Plan non configuré'); } return; }
      var was = last && last.plan && last.plan.mode;
      last = d; byId = {};
      (d.entities || []).forEach(function (e) { byId[e.id] = e; });
      d.plan.touch = (d.entities || []).some(function (e) { return (e.ctrl || []).length; });
      el.classList.toggle('hpl-navmode', navMode());
      el.classList.toggle('hpl-tv', tvMode());
      el.classList.toggle('hpl-fit', fitMode());
      sizeUi();
      relayout();
      var sig = JSON.stringify([d.plan.floors, d.plan.devices, d.plan.explode, d.plan.spinSwitch, d.plan.mode, interactive()]);
      if (sig !== built) {
        built = sig; R.build(d.plan);
        if (was !== d.plan.mode) { nav = null; tour0 = Date.now(); tourKey = ''; sheetOpen = false; }
        ensureNav();
        cam.sel = navMode() ? nav.floor : d.plan.floors.some(function (f) { return f.id === cam.sel; }) ? cam.sel : 'all';
        if (navMode()) { cam.rx = nav.floor === 'all' ? 56 : nav.room ? 34 : 46; cam.rz = RZ0; }
        drawBar();
      }
      events(); update(); tourTick(); camera(false);
    }

    timers.push(setInterval(function () { if (!holding && last) update(); }, 30 * 1000));   // « il y a … », états en attente expirés
    timers.push(setInterval(function () { if (last) tourTick(); }, 500));   // visite : avance même si l'animation est suspendue (onglet caché, appareil lent)
    timers.push(setInterval(function () {   // borne : 60 s sans toucher, retour à l'étage d'accueil de la vue
      if (!last || !navMode() || !nav || holding || Date.now() - navTouched < NAV_IDLE) return;
      var home = Logic.homeFloor(last.plan);
      if (nav.floor !== home || nav.room || nav.dev || sheetOpen || cam.zoom !== 1) { cam.rz = RZ0; go({ floor: home, room: null, dev: null }); }
    }, 2000));
    var t0 = Date.now(), raf = 0;
    function spin() {
      if (dead) return;
      if (spinOverride !== null && Date.now() >= spinUntil) { spinOverride = null; drawBar(); }   // l'interrupteur rend la main au réglage
      tourTick();
      if (last && last.plan && spinning() && !panelId && Date.now() > userUntil && !focusUntil) { cam.rz = -32 + 24 * Math.sin((Date.now() - t0) / 7000); camera(false); }
      raf = requestAnimationFrame(spin);
    }
    raf = requestAnimationFrame(spin);
    var ro = null;
    function resize() {
      var k = uiKey; sizeUi();
      if ((relayout() || k !== uiKey) && last && last.plan) { if (navMode()) update(); else drawSummary(shown()); }
      camera(false);
    }
    if (root.addEventListener) root.addEventListener('resize', resize);
    if (root.ResizeObserver) { ro = new ResizeObserver(resize); ro.observe(view); ro.observe(el); }

    function destroy() {
      if (dead) return;
      dead = true;
      cancelAnimationFrame(raf); clearTimeout(toastTimer); clearTimeout(focusTimer);
      timers.forEach(clearInterval); timers = [];
      if (ro) ro.disconnect();
      if (root.removeEventListener) root.removeEventListener('resize', resize);
      view.removeEventListener('pointerdown', down); view.removeEventListener('pointermove', move);
      view.removeEventListener('pointerup', up); view.removeEventListener('pointercancel', cancel);
      view.removeEventListener('wheel', wheel); view.removeEventListener('dblclick', recenter);
      el.removeEventListener('pointerdown', touch, true);
    }

    return { setData: setData, resize: resize, destroy: destroy };
  }

  var api = { version: 1, mount: mount, logic: Logic, renderers: renderers };
  root.LinkiiBuilding = api;
  if (typeof module === 'object' && module.exports) module.exports = api;
})(typeof window !== 'undefined' ? window : globalThis);
