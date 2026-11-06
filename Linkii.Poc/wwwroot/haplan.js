/* Éditeur du plan du bâtiment (DEV-POC §193-195) : glisser-déposer des appareils, pièces dessinées.
   Un élément [data-hp-drag="entité"] se glisse sur le plan (.hpe-plan). Sans déplacement, c'est un simple clic.
   Le serveur est prévenu par Dropped(entité, x, y) (position en fraction du plan, 0 à 1) ou Clicked(entité).
   Étage dessiné (.hpe-plan.drawn, grille data-gw × data-gh mètres) :
   - outil « Pièce » (.tool-room) : glisser sur la grille trace un rectangle → RoomDrawn(x, y, w, h) en mètres entiers ;
   - outil « Passage » (.tool-gap) : un clic → WallClicked(x, y) en mètres ;
   - outil « Sélection » : glisser une pièce → RoomMoved(id, x, y), sa poignée → RoomResized(id, w, h), un clic → RoomClicked(id) (vide : rien de choisi). */
window.haPlanDrag = (function () {
  function init(root, ref) {
    dispose(root);
    var state = null;

    function plan() { return root.querySelector('.hpe-plan'); }
    function inside(r, ev) { return ev.clientX >= r.left && ev.clientX <= r.right && ev.clientY >= r.top && ev.clientY <= r.bottom; }
    function grid(p) { return { w: +p.getAttribute('data-gw') || 12, h: +p.getAttribute('data-gh') || 8 }; }
    function meters(p, ev) {   // position du pointeur en mètres sur la grille
      var r = p.getBoundingClientRect(), g = grid(p);
      return { x: (ev.clientX - r.left) / r.width * g.w, y: (ev.clientY - r.top) / r.height * g.h, g: g };
    }
    function clamp(v, a, b) { return Math.max(a, Math.min(b, v)); }
    function pct(v, n) { return v / n * 100 + '%'; }

    function down(ev) {
      if (ev.button != null && ev.button !== 0) return;
      if (ev.target.closest('button, input, select, label')) return;
      var p = plan(), onPlan = p && p.contains(ev.target), drawn = p && p.classList.contains('drawn');

      // outils de dessin : tout le plan sert de toile
      if (onPlan && drawn && p.classList.contains('tool-room')) {
        var m = meters(p, ev), x0 = clamp(Math.round(m.x), 0, m.g.w), y0 = clamp(Math.round(m.y), 0, m.g.h);
        var box = document.createElement('div'); box.className = 'hpe-draw'; p.appendChild(box);
        state = { kind: 'draw', p: p, x0: x0, y0: y0, x1: x0, y1: y0, box: box };
        paintDraw(state);
        capture(p, ev); ev.preventDefault(); return;
      }
      if (onPlan && drawn && p.classList.contains('tool-gap')) {
        var mg = meters(p, ev);
        ref.invokeMethodAsync('WallClicked', Math.round(mg.x * 100) / 100, Math.round(mg.y * 100) / 100);
        ev.preventDefault(); return;
      }

      var el = ev.target.closest && ev.target.closest('[data-hp-drag]');
      if (el && root.contains(el)) {
        state = { kind: 'dev', id: el.getAttribute('data-hp-drag'), el: el, x: ev.clientX, y: ev.clientY, moved: false, ghost: null, onPlan: !!el.closest('.hpe-plan'), left: el.style.left, top: el.style.top };
        capture(el, ev); ev.preventDefault(); return;
      }

      if (onPlan && drawn) {
        var rh = ev.target.closest('[data-hp-resize]'), room = ev.target.closest('[data-hp-room]');
        if (rh || room) {
          var rm = rh ? rh.closest('[data-hp-room]') : room;
          state = {
            kind: rh ? 'resize' : 'room', p: p, el: rm, id: rm.getAttribute('data-hp-room'), x: ev.clientX, y: ev.clientY, moved: false,
            rx: +rm.getAttribute('data-x'), ry: +rm.getAttribute('data-y'), rw: +rm.getAttribute('data-w'), rh: +rm.getAttribute('data-h'),
          };
          state.nx = state.rx; state.ny = state.ry; state.nw = state.rw; state.nh = state.rh;
          capture(p, ev); ev.preventDefault(); return;
        }
        state = { kind: 'blank', x: ev.clientX, y: ev.clientY };
      }
    }

    function capture(el, ev) { try { el.setPointerCapture(ev.pointerId); } catch (e) {} }

    function paintDraw(s) {
      var g = grid(s.p), x = Math.min(s.x0, s.x1), y = Math.min(s.y0, s.y1), w = Math.abs(s.x1 - s.x0), h = Math.abs(s.y1 - s.y0);
      s.box.style.left = pct(x, g.w); s.box.style.top = pct(y, g.h); s.box.style.width = pct(w, g.w); s.box.style.height = pct(h, g.h);
      s.box.textContent = w && h ? w + ' × ' + h + ' m' : '';
    }

    function move(ev) {
      if (!state) return;
      var p = plan();
      if (state.kind === 'draw') {
        var m = meters(state.p, ev);
        state.x1 = clamp(Math.round(m.x), 0, m.g.w); state.y1 = clamp(Math.round(m.y), 0, m.g.h);
        paintDraw(state); return;
      }
      if (state.kind === 'blank') return;
      var dx = ev.clientX - state.x, dy = ev.clientY - state.y;
      if (!state.moved) {
        if (Math.sqrt(dx * dx + dy * dy) < 6) return;
        state.moved = true;
        if (state.kind === 'dev') {
          if (!state.onPlan) {   // depuis la liste : une copie suit le pointeur
            var g = state.el.cloneNode(true);
            g.className += ' hpe-ghost';
            g.removeAttribute('data-hp-drag');
            document.body.appendChild(g);
            state.ghost = g;
          } else state.el.classList.add('moving');
        } else state.el.classList.add('moving');
      }
      if (state.kind === 'room' || state.kind === 'resize') {   // la pièce suit le pointeur, mètre par mètre ; le serveur vérifie à la fin
        var r = state.p.getBoundingClientRect(), gg = grid(state.p), mx = dx / r.width * gg.w, my = dy / r.height * gg.h;
        if (state.kind === 'room') {
          state.nx = clamp(state.rx + Math.round(mx), 0, gg.w - state.rw); state.ny = clamp(state.ry + Math.round(my), 0, gg.h - state.rh);
          state.el.style.left = pct(state.nx, gg.w); state.el.style.top = pct(state.ny, gg.h);
        } else {
          state.nw = clamp(state.rw + Math.round(mx), 1, gg.w - state.rx); state.nh = clamp(state.rh + Math.round(my), 1, gg.h - state.ry);
          state.el.style.width = pct(state.nw, gg.w); state.el.style.height = pct(state.nh, gg.h);
          var lab = state.el.querySelector('small'); if (lab) lab.textContent = state.nw + ' × ' + state.nh + ' m';
        }
        return;
      }
      if (state.ghost) { state.ghost.style.left = ev.clientX + 12 + 'px'; state.ghost.style.top = ev.clientY + 8 + 'px'; }
      if (p) {
        var rr = p.getBoundingClientRect(), ins = inside(rr, ev);
        p.classList.toggle('drop', ins && !state.onPlan);
        if (state.onPlan) {   // un appareil déjà posé suit le pointeur, sans passer par le serveur
          var x = clamp((ev.clientX - rr.left) / rr.width, 0, 1), y = clamp((ev.clientY - rr.top) / rr.height, 0, 1);
          state.el.style.left = x * 100 + '%'; state.el.style.top = y * 100 + '%';
        }
      }
    }

    function up(ev) {
      if (!state) return;
      var s = state; state = null;
      var p = plan();
      if (s.kind === 'draw') {
        s.box.remove();
        var x = Math.min(s.x0, s.x1), y = Math.min(s.y0, s.y1), w = Math.abs(s.x1 - s.x0), h = Math.abs(s.y1 - s.y0);
        if (w >= 1 && h >= 1) ref.invokeMethodAsync('RoomDrawn', x, y, w, h);
        return;
      }
      if (s.kind === 'blank') { if (Math.abs(ev.clientX - s.x) < 6 && Math.abs(ev.clientY - s.y) < 6) ref.invokeMethodAsync('RoomClicked', ''); return; }
      if (s.kind === 'room' || s.kind === 'resize') {
        s.el.classList.remove('moving');
        if (!s.moved) { ref.invokeMethodAsync('RoomClicked', s.id); return; }
        if (s.kind === 'room') ref.invokeMethodAsync('RoomMoved', s.id, s.nx, s.ny);
        else ref.invokeMethodAsync('RoomResized', s.id, s.nw, s.nh);
        return;
      }
      if (s.ghost) s.ghost.remove();
      s.el.classList.remove('moving');
      if (p) p.classList.remove('drop');
      if (!s.moved) { ref.invokeMethodAsync('Clicked', s.id); return; }
      if (!p) return;
      var r = p.getBoundingClientRect();
      if (!inside(r, ev)) { if (s.onPlan) { s.el.style.left = s.left; s.el.style.top = s.top; } return; }   // lâché hors du plan : l'appareil reste où il était
      var fx = clamp((ev.clientX - r.left) / r.width, 0, 1), fy = clamp((ev.clientY - r.top) / r.height, 0, 1);
      ref.invokeMethodAsync('Dropped', s.id, Math.round(fx * 10000) / 10000, Math.round(fy * 10000) / 10000);
    }

    function cancel() {
      if (!state) return;
      var s = state; state = null;
      if (s.kind === 'draw') { s.box.remove(); return; }
      if (s.kind === 'room' || s.kind === 'resize') { ref.invokeMethodAsync('Refresh'); return; }
      if (s.kind !== 'dev') return;
      if (s.onPlan) { s.el.style.left = s.left; s.el.style.top = s.top; }
      if (s.ghost) s.ghost.remove();
      s.el.classList.remove('moving');
    }

    root.addEventListener('pointerdown', down);
    root.addEventListener('pointermove', move);
    root.addEventListener('pointerup', up);
    root.addEventListener('pointercancel', cancel);
    root._hpDrag = { handlers: [down, move, up, cancel] };
  }

  function dispose(root) {
    var c = root && root._hpDrag;
    if (!c) return;
    root.removeEventListener('pointerdown', c.handlers[0]);
    root.removeEventListener('pointermove', c.handlers[1]);
    root.removeEventListener('pointerup', c.handlers[2]);
    root.removeEventListener('pointercancel', c.handlers[3]);
    root._hpDrag = null;
  }

  /* aperçu (§196) : envoie les données du plan à player/plan-preview.html dans l'iframe, dès qu'elle est prête */
  function preview(frame, json) {
    if (!frame) return;
    frame._hplData = JSON.parse(json);
    if (frame._hplReady) post(frame);
  }
  function post(frame) {
    try { frame.contentWindow.postMessage({ type: 'hpl-preview', data: frame._hplData }, location.origin); } catch (e) {}
  }
  window.addEventListener('message', function (ev) {
    if (ev.origin !== location.origin || !ev.data || ev.data.type !== 'hpl-preview-ready') return;
    var frames = document.querySelectorAll('iframe');
    for (var i = 0; i < frames.length; i++) if (frames[i].contentWindow === ev.source) { frames[i]._hplReady = true; if (frames[i]._hplData) post(frames[i]); }
  });

  return { init: init, dispose: dispose, preview: preview };
})();
