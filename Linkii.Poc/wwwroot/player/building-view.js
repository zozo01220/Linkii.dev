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
    var lab = o.labels || 'room', hideOff = o.hideOff !== false, pins = {}, rooms = {};
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
      pins[d.id] = {
        show: lab === 'room' && d.room ? own : true, bare: bare, st: st, pulse: o.pulseId === d.id,
        dim: !!o.room && d.floor + '/' + d.room !== o.room, sel: o.panelId === d.id,   // borne : une pièce choisie, les autres appareils s'estompent
        text: (lab === 'room' && own && st ? e.name + ' · ' : '') + Ha.value(e, o.decimals, o.now),
        glow: e.domain === 'light' && e.on && e.available !== false ? { b: e.brightness == null ? 100 : e.brightness, col: e.color || '#ffc870' } : null,
        open: d.seg ? (e.available !== false && (e.domain === 'binary_sensor' ? !!e.on : e.state === 'open' || e.state === 'opening')) : false,
        shut: d.seg && d.seg.kind === 'cover' ? (e.position != null ? 100 - e.position : e.state === 'closed' || e.state === 'closing' ? 100 : 0) : null
      };
    });
    var labels = {};
    plan.floors.forEach(function (f) {
      (f.rooms || []).forEach(function (r) {
        var k = f.id + '/' + r.id, info = rooms[k];
        labels[k] = {
          name: r.name, show: lab === 'room' && !!info && (info.lights > 0 || info.t != null), sel: o.room === k,
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
      if (t != null) { var k = d.room ? d.floor + '/' + d.room : d.id; if (!seen[k]) { seen[k] = 1; temps.push({ id: d.id, t: where || e.name, v: Ha.deg(Math.round(t * 10) / 10) }); } }
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

  /** Remonter d'un niveau à la borne : appareil → pièce (ou étage si l'appareil est hors pièce) → étage. */
  function navUp(nav, plan) {
    if (nav.dev) {
      var d = plan.devices.filter(function (x) { return x.id === nav.dev; })[0];
      return { floor: nav.floor, room: d && d.room && nav.room ? nav.room : null, dev: null };
    }
    if (nav.room) return { floor: nav.floor, room: null, dev: null };
    return nav;
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
    tourAt: tourAt, homeFloor: homeFloor, navUp: navUp
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

    /** Applique ce que Logic.scene a décidé ; ents sert aux icônes. */
    function paint(sc, ents) {
      plan.devices.forEach(function (d) {
        var v = sc.pins[d.id] || { show: false }, pin = world.querySelector('[data-p="' + d.id + '"]'), e = ents[d.id];
        if (pin) {
          pin.style.display = v.show ? '' : 'none';
          if (v.show) {
            var chip = pin.querySelector('.hpl-chip');
            chip.className = 'hpl-chip ' + v.st + (v.bare ? ' novals' : '') + (v.pulse ? ' pulse' : '') + (v.dim ? ' dim' : '') + (v.sel ? ' sel' : '');
            chip.setAttribute('aria-label', e.name);
            chip.innerHTML = Ha.icon(e) + '<span class="hpl-v"></span>';
            chip.querySelector('.hpl-v').textContent = v.text;
            pin.classList.toggle('small', !!v.bare);
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
      var rm = target.closest('.hpl-room'), fl = rm && rm.closest('.hpl-floor');
      return rm && fl ? 'room:' + fl.getAttribute('data-f') + '/' + rm.getAttribute('data-room') : null;
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
    el.innerHTML = '<div class="hpl-stage"><div class="hpl-view"></div><div class="hpl-bar"></div><div class="hpl-top"></div><div class="hpl-rail"></div>'
      + '<div class="hpl-tour"></div><div class="hpl-toast"></div><div class="hpl-sheet"></div></div><div class="hpl-sum"></div>';
    var view = el.querySelector('.hpl-view'), bar = el.querySelector('.hpl-bar'), toast = el.querySelector('.hpl-toast'), top = el.querySelector('.hpl-top'),
      rail = el.querySelector('.hpl-rail'), tourEl = el.querySelector('.hpl-tour'), sum = el.querySelector('.hpl-sum'), sheet = el.querySelector('.hpl-sheet');
    var R = renderers[opts.renderer] ? renderers[opts.renderer](view) : css3d(view);
    var RX_MIN = 40, RX_MAX = 78, NAV_IDLE = 60000, HOLD_MS = 1500;
    var last = null, built = '', byId = {}, pending = {}, confirm = {}, panelId = null, holding = false, prev = null, sumHtml = '', layout = '';
    var cam = { rz: -32, rx: 62, sel: 'all' }, userUntil = 0, focusUntil = 0, focusTimer = 0, toastTimer = 0, pulseId = null;
    var spinOverride = null, spinUntil = 0, dead = false, timers = [];
    var tour0 = Date.now(), tourKey = '', nav = null, navTouched = 0;

    function interactive() { return opts.mode === 'interactive' || (opts.mode !== 'signage' && !!(last && last.plan && (last.plan.mode === 'interactive' || (!last.plan.mode && last.plan.touch)))); }
    function navMode() { return !!(last && last.plan && last.plan.mode === 'interactive' && opts.mode !== 'signage'); }
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
      var m = navMode() ? 'right' : Logic.layoutMode(last.plan.summary, el.clientWidth, el.clientHeight);
      if (m === layout) return false;
      layout = m; sumHtml = '';
      el.classList.remove('s-right', 's-left', 's-top', 's-none', 's-only');
      el.classList.add('s-' + m);
      return true;
    }
    function camera(ease) {
      if (!last || !last.plan) return;
      cam.solo = navMode() || touring();
      R.camera(cam, ease);
      if (ease) setTimeout(function () { if (!dead) R.camera(cam, false); }, 900);
    }

    function update() {
      if (!last || !last.plan || dead) return;
      var ents = shown();
      R.paint(Logic.scene(last.plan, ents, {
        labels: last.plan.labels, hideOff: last.plan.hideOff, heat: last.plan.heat, decimals: last.decimals, now: now(), pulseId: pulseId,
        panelId: navMode() ? nav && nav.dev : panelId, room: navMode() && nav && nav.room ? nav.floor + '/' + nav.room : null
      }), ents);
      if (navMode()) drawNav(ents); else drawSummary(ents);
      if (panelId && !navMode()) drawSheet();
    }

    function drawSummary(ents) {
      relayout();
      if (layout === 'none') { if (sumHtml !== '-') { sum.innerHTML = ''; sumHtml = '-'; } return; }
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
      if (sel !== cam.sel) { cam.sel = sel; camera(true); }
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

    /* ----- borne : navigation étage → pièce → appareil ----- */
    function touch() { navTouched = Date.now(); }
    function go(n) { nav = n; touch(); cam.sel = nav.floor; camera(true); update(); }
    function navTo(id) {
      if (id.indexOf('room:') === 0) { var p = id.slice(5).split('/'); go({ floor: p[0], room: p[1], dev: null }); return; }
      var d = last.plan.devices.filter(function (x) { return x.id === id; })[0];
      if (d) go({ floor: d.floor, room: d.room || null, dev: d.id });
    }
    function crumb(label, cur, fn) {
      var b = document.createElement('button'); b.type = 'button'; b.className = 'hpl-cr' + (cur ? ' cur' : ''); b.textContent = label;
      if (fn) b.onclick = fn; return b;
    }
    function drawNav(ents) {
      relayout();
      if (!nav || !last.plan.floors.some(function (f) { return f.id === nav.floor; })) nav = { floor: Logic.homeFloor(last.plan), room: null, dev: null };
      // haut : Retour (pas à l'étage d'accueil de la vue : la borne a son propre retour) et fil d'Ariane
      top.innerHTML = '';
      if (nav.room || nav.dev) { var up = crumb('‹ Retour', false, function () { go(Logic.navUp(nav, last.plan)); }); up.className = 'hpl-up'; top.appendChild(up); }
      var trail = document.createElement('div'); trail.className = 'hpl-crumbs'; top.appendChild(trail);
      var rname = nav.room ? (roomsOf(last.plan, nav.floor).filter(function (r) { return r.id === nav.room; })[0] || {}).name : null;
      trail.appendChild(crumb(floorName(nav.floor), !nav.room && !nav.dev, function () { go({ floor: nav.floor, room: null, dev: null }); }));
      if (rname) { trail.appendChild(document.createTextNode('›')); trail.appendChild(crumb(rname, !nav.dev, function () { go({ floor: nav.floor, room: nav.room, dev: null }); })); }
      if (nav.dev && byId[nav.dev]) { trail.appendChild(document.createTextNode('›')); trail.appendChild(crumb(byId[nav.dev].name, true)); }
      // rail : les étages, du haut vers le bas, toujours visibles pour changer d'étage depuis une pièce
      rail.innerHTML = '';
      last.plan.floors.slice().reverse().forEach(function (f) {
        var b = document.createElement('button'); b.type = 'button'; b.className = 'hpl-fl' + (f.id === nav.floor ? ' on' : '');
        b.innerHTML = '<span></span><small></small>';
        b.firstChild.textContent = f.name;
        var nr = (f.rooms || []).length, nd = last.plan.devices.filter(function (d) { return d.floor === f.id; }).length;
        b.lastChild.textContent = nr ? nr + ' pièce' + (nr > 1 ? 's' : '') : nd + ' appareil' + (nd > 1 ? 's' : '');
        b.onclick = function () { go({ floor: f.id, room: null, dev: null }); };
        rail.appendChild(b);
      });
      if (holding) return;   // un curseur ou une serrure en cours : le panneau ne bouge pas
      // panneau de droite
      sum.innerHTML = ''; sumHtml = 'nav';
      var box = document.createElement('div'); box.className = 'hpl-nav'; sum.appendChild(box);
      function back(label, fn) { var b = document.createElement('button'); b.type = 'button'; b.className = 'hpl-back'; b.textContent = '‹ ' + label; b.onclick = fn; box.appendChild(b); }
      function h3(text, iconHtml) { var h = document.createElement('h3'); h.innerHTML = (iconHtml || '') + '<span></span>'; h.lastChild.textContent = text; box.appendChild(h); }
      function hint(text) { var s = document.createElement('div'); s.className = 'hpl-hint'; s.textContent = text; box.appendChild(s); }
      if (nav.dev && byId[nav.dev]) {
        var raw = byId[nav.dev], e = eView(raw);
        back(rname || floorName(nav.floor), function () { go(Logic.navUp(nav, last.plan)); });
        h3(e.name, Ha.icon(e));
        var big = document.createElement('div'); big.className = 'hpl-big'; big.textContent = Ha.value(e, last.decimals, now()); box.appendChild(big);
        controls(box, raw, true);
      } else if (nav.room) {
        back(floorName(nav.floor), function () { go({ floor: nav.floor, room: null, dev: null }); });
        h3(rname || '');
        var ds = devicesIn(nav.floor, nav.room);
        hint(ds.length ? 'Touchez un appareil pour le commander.' : 'Aucun appareil dans cette pièce.');
        ds.forEach(function (d) { var x = deviceRow(d, true); x.onclick = function () { go({ floor: nav.floor, room: nav.room, dev: d.id }); }; box.appendChild(x); });
      } else {
        h3(floorName(nav.floor));
        var rooms = roomsOf(last.plan, nav.floor), sc = Logic.scene(last.plan, ents, { labels: 'room', hideOff: last.plan.hideOff, now: now() });
        hint(rooms.length ? 'Touchez une pièce, ici ou sur le plan.' : 'Touchez un appareil, ici ou sur le plan.');
        rooms.forEach(function (r) {
          var info = sc.rooms[nav.floor + '/' + r.id] || {}, alerts = devicesIn(nav.floor, r.id).filter(function (d) { return state(eView(byId[d.id])) === 'alert'; });
          var x = document.createElement('button'); x.type = 'button';
          x.className = 'hpl-sb hpl-srow ' + (alerts.length ? 'alert' : info.lights ? 'on' : '');
          x.innerHTML = Ha.icon(alerts.length ? eView(byId[alerts[0].id]) : { domain: info.lights ? 'light' : 'climate' }) + '<span class="hpl-sn"></span><span class="hpl-sv"></span>';
          x.querySelector('.hpl-sn').textContent = r.name;
          x.querySelector('.hpl-sv').textContent = alerts.length ? byId[alerts[0].id].name + ' · ' + Ha.value(eView(byId[alerts[0].id]), last.decimals, now())
            : [info.lights ? info.lights + ' allumée' + (info.lights > 1 ? 's' : '') : '', info.t != null ? Ha.deg(Math.round(info.t * 10) / 10) : ''].filter(Boolean).join(' · ') || '—';
          x.onclick = function () { go({ floor: nav.floor, room: r.id, dev: null }); };
          box.appendChild(x);
        });
        devicesIn(nav.floor, null).forEach(function (d) { var x = deviceRow(d, true); x.onclick = function () { go({ floor: nav.floor, room: null, dev: d.id }); }; box.appendChild(x); });
      }
    }

    /* ----- rotation au doigt, toucher d'une pastille ou d'une pièce ----- */
    var drag = null;
    function down(ev) {
      drag = { x: ev.clientX, y: ev.clientY, rz: cam.rz, rx: cam.rx, id: R.hit(ev.target), moved: false };
      userUntil = Date.now() + 20000;   // on touche l'écran : la rotation s'arrête le temps de viser une pastille
      touch();
      try { view.setPointerCapture(ev.pointerId); } catch (x) {}
    }
    function move(ev) {
      if (!drag) return;
      var dx = ev.clientX - drag.x, dy = ev.clientY - drag.y;
      if (!drag.moved && Math.sqrt(dx * dx + dy * dy) < 6) return;
      drag.moved = true; userUntil = Date.now() + 6000;
      cam.rz = drag.rz + dx * 0.35; cam.rx = Math.max(RX_MIN, Math.min(RX_MAX, drag.rx - dy * 0.2)); camera(false);
    }
    function up() {
      if (!drag) return;
      if (!drag.moved) {
        if (navMode()) { if (drag.id) navTo(drag.id); }
        else if (drag.id) { if (interactive()) openSheet(drag.id); }
        else closeSheet();
      }
      drag = null;
    }
    function cancel() { drag = null; }
    view.addEventListener('pointerdown', down); view.addEventListener('pointermove', move);
    view.addEventListener('pointerup', up); view.addEventListener('pointercancel', cancel);
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
      relayout();
      var sig = JSON.stringify([d.plan.floors, d.plan.devices, d.plan.explode, d.plan.spinSwitch, d.plan.mode, interactive()]);
      if (sig !== built) {
        built = sig; R.build(d.plan);
        if (was !== d.plan.mode) { nav = null; tour0 = Date.now(); tourKey = ''; }
        cam.sel = navMode() ? (nav ? nav.floor : Logic.homeFloor(d.plan)) : d.plan.floors.some(function (f) { return f.id === cam.sel; }) ? cam.sel : 'all';
        if (navMode()) { cam.rx = 56; }
        drawBar();
      }
      events(); update(); tourTick(); camera(false);
    }

    timers.push(setInterval(function () { if (!holding && last) update(); }, 30 * 1000));   // « il y a … », états en attente expirés
    timers.push(setInterval(function () { if (last) tourTick(); }, 500));   // visite : avance même si l'animation est suspendue (onglet caché, appareil lent)
    timers.push(setInterval(function () {   // borne : 60 s sans toucher, retour à l'étage d'accueil de la vue
      if (!last || !navMode() || !nav || holding || Date.now() - navTouched < NAV_IDLE) return;
      var home = Logic.homeFloor(last.plan);
      if (nav.floor !== home || nav.room || nav.dev) { nav = { floor: home, room: null, dev: null }; cam.sel = home; camera(true); update(); }
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
    function resize() { if (relayout() && last) { if (navMode()) update(); else drawSummary(shown()); } camera(false); }
    if (root.ResizeObserver) { ro = new ResizeObserver(resize); ro.observe(view); ro.observe(el); }

    function destroy() {
      if (dead) return;
      dead = true;
      cancelAnimationFrame(raf); clearTimeout(toastTimer); clearTimeout(focusTimer);
      timers.forEach(clearInterval); timers = [];
      if (ro) ro.disconnect();
      view.removeEventListener('pointerdown', down); view.removeEventListener('pointermove', move);
      view.removeEventListener('pointerup', up); view.removeEventListener('pointercancel', cancel);
      el.removeEventListener('pointerdown', touch, true);
    }

    return { setData: setData, resize: resize, destroy: destroy };
  }

  var api = { version: 1, mount: mount, logic: Logic, renderers: renderers };
  root.LinkiiBuilding = api;
  if (typeof module === 'object' && module.exports) module.exports = api;
})(typeof window !== 'undefined' ? window : globalThis);
