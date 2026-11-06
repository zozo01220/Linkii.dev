/* Éditeur du plan du bâtiment (champ « haplan », DEV-POC §193) : glisser-déposer des appareils.
   Un élément [data-hp-drag="entité"] se glisse sur le plan (.hpe-plan). Sans déplacement, c'est un simple clic.
   Le serveur est prévenu par Dropped(entité, x, y) (position en fraction du plan, 0 à 1) ou Clicked(entité). */
window.haPlanDrag = (function () {
  var cur = null;

  function init(root, ref) {
    dispose(root);
    var state = null;

    function plan() { return root.querySelector('.hpe-plan'); }

    function down(ev) {
      var el = ev.target.closest && ev.target.closest('[data-hp-drag]');
      if (!el || !root.contains(el) || (ev.button != null && ev.button !== 0)) return;
      if (ev.target.closest('button, input, select')) return;
      state = { id: el.getAttribute('data-hp-drag'), el: el, x: ev.clientX, y: ev.clientY, moved: false, ghost: null, onPlan: !!el.closest('.hpe-plan'), left: el.style.left, top: el.style.top };
      try { el.setPointerCapture(ev.pointerId); } catch (e) {}
      ev.preventDefault();
    }

    function move(ev) {
      if (!state) return;
      var dx = ev.clientX - state.x, dy = ev.clientY - state.y;
      if (!state.moved) {
        if (Math.sqrt(dx * dx + dy * dy) < 6) return;
        state.moved = true;
        if (!state.onPlan) {   // depuis la liste : une copie suit le pointeur
          var g = state.el.cloneNode(true);
          g.className += ' hpe-ghost';
          g.removeAttribute('data-hp-drag');
          document.body.appendChild(g);
          state.ghost = g;
        } else state.el.classList.add('moving');
      }
      var p = plan();
      if (state.ghost) { state.ghost.style.left = ev.clientX + 12 + 'px'; state.ghost.style.top = ev.clientY + 8 + 'px'; }
      if (p) {
        var r = p.getBoundingClientRect(), inside = ev.clientX >= r.left && ev.clientX <= r.right && ev.clientY >= r.top && ev.clientY <= r.bottom;
        p.classList.toggle('drop', inside && !state.onPlan);
        if (state.onPlan) {   // un appareil déjà posé suit le pointeur, sans passer par le serveur
          var x = Math.max(0, Math.min(1, (ev.clientX - r.left) / r.width)), y = Math.max(0, Math.min(1, (ev.clientY - r.top) / r.height));
          state.el.style.left = x * 100 + '%'; state.el.style.top = y * 100 + '%';
        }
      }
    }

    function up(ev) {
      if (!state) return;
      var s = state; state = null;
      if (s.ghost) s.ghost.remove();
      s.el.classList.remove('moving');
      var p = plan();
      if (p) p.classList.remove('drop');
      if (!s.moved) { ref.invokeMethodAsync('Clicked', s.id); return; }
      if (!p) return;
      var r = p.getBoundingClientRect();
      var inside = ev.clientX >= r.left && ev.clientX <= r.right && ev.clientY >= r.top && ev.clientY <= r.bottom;
      if (!inside) { if (s.onPlan) { s.el.style.left = s.left; s.el.style.top = s.top; } return; }   // lâché hors du plan : l'appareil reste où il était
      var x = Math.max(0, Math.min(1, (ev.clientX - r.left) / r.width)), y = Math.max(0, Math.min(1, (ev.clientY - r.top) / r.height));
      ref.invokeMethodAsync('Dropped', s.id, Math.round(x * 10000) / 10000, Math.round(y * 10000) / 10000);
    }

    function cancel() {
      if (!state) return;
      if (state.onPlan) { state.el.style.left = state.left; state.el.style.top = state.top; }
      if (state.ghost) state.ghost.remove();
      state.el.classList.remove('moving');
      state = null;
    }

    root.addEventListener('pointerdown', down);
    root.addEventListener('pointermove', move);
    root.addEventListener('pointerup', up);
    root.addEventListener('pointercancel', cancel);
    cur = { root: root, handlers: [down, move, up, cancel] };
    root._hpDrag = cur;
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

  return { init: init, dispose: dispose };
})();
