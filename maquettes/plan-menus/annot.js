/* Annotations posées sur les captures réelles du back-office (maquette, rien de codé) */
(function () {
  const marks = [];
  window.$a = (s, root = document) => root.querySelector(s);
  window.$$a = (s, root = document) => [...root.querySelectorAll(s)];
  window.MARK = (target, n, text, side = 'right') => marks.push({ target, n, text, side });
  window.ICON = {
    list: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01"/></svg>',
    screen: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8M12 17v4"/></svg>',
    hand: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M8 13V5a2 2 0 0 1 4 0v6M12 11V4a2 2 0 0 1 4 0v7M16 11V6a2 2 0 0 1 4 0v8a7 7 0 0 1-7 7h-1a7 7 0 0 1-6-3l-3-5a2 2 0 0 1 3-2l2 2"/></svg>',
    plus: '<svg class="ico-svg" viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><path d="M12 5v14M5 12h14"/></svg>',
    layers: '<svg class="ico-svg" viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l9 5-9 5-9-5zM3 13l9 5 9-5"/></svg>',
    eye: '<svg class="ico-svg" viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.7"><path d="M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/></svg>'
  };
  window.POP = (html, anchor, dx = 0, dy = 0) => {
    const el = typeof anchor === 'string' ? $a(anchor) : anchor;
    const p = document.createElement('div'); p.className = 'an-pop'; p.innerHTML = html; document.body.appendChild(p);
    const r = el.getBoundingClientRect();
    p.style.left = (r.left + scrollX + dx) + 'px'; p.style.top = (r.top + scrollY + dy) + 'px';
    return p;
  };
  function place() {
    $$a('.an-m').forEach(e => e.remove());
    marks.forEach(m => {
      const el = typeof m.target === 'string' ? $a(m.target) : m.target; if (!el) return;
      el.classList.add('an-hl');
      const r = el.getBoundingClientRect(); if (!r.width && !r.height) return;
      const b = document.createElement('div');
      b.className = 'an-m an-' + m.side;
      b.innerHTML = `<span class="an-n">${m.n}</span>${m.text ? `<span class="an-t">${m.text}</span>` : ''}`;
      document.body.appendChild(b);
      let x = r.right + scrollX + 10, y = r.top + scrollY + r.height / 2;
      if (m.side === 'left') x = r.left + scrollX - 10;
      if (m.side === 'below') { x = r.left + scrollX; y = r.bottom + scrollY + 10; }
      if (m.side === 'above') { x = r.left + scrollX; y = r.top + scrollY - 10; }
      if (m.side === 'bl') { b.className = 'an-m an-below'; b.style.flexDirection = 'row-reverse'; b.style.transform = 'translateX(-100%)'; x = r.right + scrollX; y = r.bottom + scrollY + 10; }
      b.style.left = x + 'px'; b.style.top = y + 'px';
    });
  }
  window.PLACE = place;
  window.CLEARMARKS = () => { marks.length = 0; $$a('.an-hl').forEach(e => e.classList.remove('an-hl')); place(); };
  window.ANNOT = fn => {
    const run = () => { try { fn(); } catch (e) { console.error(e); } requestAnimationFrame(place); };
    if (document.readyState === 'complete') setTimeout(run, 30); else addEventListener('load', () => setTimeout(run, 30));
    addEventListener('resize', place);
  };
})();
