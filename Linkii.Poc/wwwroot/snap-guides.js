/* Aide à l'alignement commune aux créateurs (composition, liste interactive, widgets d'un écran) — §177, maquette alignement-composants.html.
   Les rectangles { x, y, w, h } sont dans les unités de l'éditeur (px de la page, %…) ; o.sx / o.sy donnent les px affichés par unité.
   - move(b, others, o, lock)     : aimante un rectangle déplacé (bords, centres, page, écarts égaux) ; lock = true bloque l'axe x, false l'axe y
   - resize(b, dir, others, o)    : aimante le bord tiré (bords, centres, page, même taille, écart égal)
   - guides(b, others, o, sizes)  : ce qu'il faut dessiner (lignes roses, écarts orange, « = L / = H »)
   - svg(g, o)                    : le dessin, en SVG, aux dimensions affichées
   - arrange / setGap / distribute / tidy / sameSize : outils de la sélection multiple
   o = { W, H, sx, sy, xs?, ys?, tol? } : W/H taille de la page en unités, xs/ys lignes de la page (par défaut bords et milieu). */
(function (w) {
  'use strict';
  const st = (r, A) => A ? r.x : r.y, sz = (r, A) => A ? r.w : r.h, en = (r, A) => st(r, A) + sz(r, A);
  const crossOv = (p, q, A) => A ? (p.y < q.y + q.h && q.y < p.y + p.h) : (p.x < q.x + q.w && q.x < p.x + p.w);
  const tolOf = (o, A) => (o.tol || 7) / (A ? o.sx : o.sy);
  const epsOf = (o, A) => 1.5 / (A ? o.sx : o.sy);   // deux valeurs « égales » à l'écran (arrondis compris)
  const pageLines = (o, A) => (A ? o.xs : o.ys) || [0, (A ? o.W : o.H) / 2, A ? o.W : o.H];

  function lines(os, o, A) { const L = pageLines(o, A).slice(); os.forEach(r => L.push(st(r, A), st(r, A) + sz(r, A) / 2, en(r, A))); return L; }
  /** voisins directs avant / après sur l'axe, parmi ceux qui se chevauchent sur l'autre axe */
  function nb(b, os, A) {
    let prev = null, next = null;
    os.forEach(r => {
      if (!crossOv(r, b, A)) return;
      if (en(r, A) <= st(b, A) + .01) { if (!prev || en(r, A) > en(prev, A)) prev = r; }
      else if (st(r, A) >= en(b, A) - .01) { if (!next || st(r, A) < st(next, A)) next = r; }
    });
    return { prev, next };
  }
  /** écarts entre voisins directs (sans rien entre eux) parmi les autres composants */
  function pairs(os, A) {
    const res = [];
    os.forEach(p => os.forEach(q => {
      if (p === q || !crossOv(p, q, A)) return;
      const pe = en(p, A), qs = st(q, A); if (qs < pe) return;
      if (os.some(r => r !== p && r !== q && st(r, A) >= pe && en(r, A) <= qs && crossOv(r, p, A) && crossOv(r, q, A))) return;
      res.push({ p, q, g: qs - pe });
    }));
    return res;
  }

  function move(b, os, o, lock) {
    [true, false].forEach(A => {
      if (lock === A) return;
      const tol = tolOf(o, A); let best = null;
      const tryD = d => { if (Math.abs(d) < tol && (best === null || Math.abs(d) < Math.abs(best))) best = d; };
      const L = lines(os, o, A);
      [0, sz(b, A) / 2, sz(b, A)].forEach(off => L.forEach(l => tryD(l - (st(b, A) + off))));
      const { prev, next } = nb(b, os, A), pe = prev && en(prev, A), ns = next && st(next, A);
      if (prev && next) tryD((pe + ns - sz(b, A)) / 2 - st(b, A));   // centré entre ses deux voisins
      pairs(os, A).forEach(r => { if (r.g <= 0) return; if (prev) tryD(pe + r.g - st(b, A)); if (next) tryD(ns - r.g - sz(b, A) - st(b, A)); });   // même écart qu'ailleurs
      if (best !== null) { if (A) b.x += best; else b.y += best; }
    });
    return b;
  }

  function resize(b, dir, os, o) {
    [['e', true, 1], ['w', true, 0], ['s', false, 1], ['n', false, 0]].forEach(([c, A, far]) => {
      if (dir.indexOf(c) < 0) return;
      const tol = tolOf(o, A); let best = null;
      const tryD = d => { if (Math.abs(d) < tol && (best === null || Math.abs(d) < Math.abs(best))) best = d; };
      const edge = far ? en(b, A) : st(b, A);
      lines(os, o, A).forEach(l => tryD(l - edge));
      os.forEach(r => tryD(far ? sz(r, A) - sz(b, A) : sz(b, A) - sz(r, A)));   // même largeur / hauteur
      const { prev, next } = nb(b, os, A);
      pairs(os, A).forEach(r => { if (r.g <= 0) return; if (far && next) tryD(st(next, A) - r.g - edge); if (!far && prev) tryD(en(prev, A) + r.g - edge); });
      if (best === null) return;
      if (A) { if (far) b.w += best; else { b.x += best; b.w -= best; } }
      else { if (far) b.h += best; else { b.y += best; b.h -= best; } }
    });
    return b;
  }

  function guides(b, os, o, withSizes) {
    const out = { lines: [], spaces: [], sizes: [] };
    [true, false].forEach(A => {
      const e = epsOf(o, A), bp = [st(b, A), st(b, A) + sz(b, A) / 2, en(b, A)], m = new Map();
      pageLines(o, A).forEach(v => { if (bp.some(q => Math.abs(q - v) < e)) out.lines.push({ A, at: v, page: true }); });
      os.forEach(r => [st(r, A), st(r, A) + sz(r, A) / 2, en(r, A)].forEach(v => {
        const hit = bp.find(q => Math.abs(q - v) < e); if (hit === undefined) return;
        const k = Math.round(hit / e); let g = m.get(k);
        if (!g) { g = { A, at: hit, a: st(b, !A), b: en(b, !A), pts: [b] }; m.set(k, g); }
        g.a = Math.min(g.a, st(r, !A)); g.b = Math.max(g.b, en(r, !A)); if (g.pts.indexOf(r) < 0) g.pts.push(r);
      }));
      m.forEach(g => out.lines.push(g));
      const { prev, next } = nb(b, os, A), gL = prev ? st(b, A) - en(prev, A) : null, gR = next ? st(next, A) - en(b, A) : null, keys = new Set();
      const push = (p, q, g) => { const k = os.indexOf(p) + '|' + os.indexOf(q); if (keys.has(k) || g <= e) return; keys.add(k); out.spaces.push({ A, p, q, g }); };
      if (gL !== null && gR !== null && Math.abs(gL - gR) < e) { push(prev, b, gL); push(b, next, gR); }
      pairs(os, A).forEach(r => {
        if (gL !== null && Math.abs(r.g - gL) < e) { push(r.p, r.q, r.g); push(prev, b, gL); }
        if (gR !== null && Math.abs(r.g - gR) < e) { push(r.p, r.q, r.g); push(b, next, gR); }
      });
      if (withSizes) {
        const same = os.filter(r => Math.abs(sz(r, A) - sz(b, A)) < e);
        same.forEach(r => out.sizes.push({ A, r, v: sz(b, A) }));
        if (same.length) out.sizes.push({ A, r: b, v: sz(b, A) });
      }
    });
    return out;
  }

  /** dessin SVG (aux dimensions affichées) ; o.unit(v, A) formate une valeur (par défaut arrondie) */
  function svg(g, o) {
    if (!g) return '';
    const X = v => v * o.sx, Y = v => v * o.sy, pk = '#E0457B', or = '#E8833A', fmt = o.unit || (v => Math.round(v));
    const P = (A, along, across) => A ? [X(along), Y(across)] : [X(across), Y(along)];   // A : ligne verticale (along = x)
    const tag = (x, y, t, col) => { const wd = String(t).length * 6.6 + 10; return `<rect x="${x - wd / 2}" y="${y - 9}" width="${wd}" height="18" rx="4" fill="${col}"/><text x="${x}" y="${y + 4}" text-anchor="middle" fill="#fff" style="font:600 11px 'IBM Plex Sans',sans-serif">${t}</text>`; };
    let h = '';
    g.lines.forEach(l => {
      if (l.page) {
        h += l.A ? `<line x1="${X(l.at)}" y1="0" x2="${X(l.at)}" y2="${Y(o.H)}" stroke="${pk}" stroke-dasharray="5 4"/>` : `<line x1="0" y1="${Y(l.at)}" x2="${X(o.W)}" y2="${Y(l.at)}" stroke="${pk}" stroke-dasharray="5 4"/>`;
        return;
      }
      const a = P(l.A, l.at, l.a), b = P(l.A, l.at, l.b);
      h += `<line x1="${a[0]}" y1="${a[1]}" x2="${b[0]}" y2="${b[1]}" stroke="${pk}"/>`;
      l.pts.forEach(r => [st(r, !l.A), en(r, !l.A)].forEach(v => { const p = P(l.A, l.at, v); h += `<path d="M${p[0] - 3} ${p[1] - 3}l6 6M${p[0] + 3} ${p[1] - 3}l-6 6" stroke="${pk}" stroke-width="1.4"/>`; }));
    });
    g.spaces.forEach(s => {
      const A = s.A, a = en(s.p, A), b = st(s.q, A), c = (Math.max(st(s.p, !A), st(s.q, !A)) + Math.min(en(s.p, !A), en(s.q, !A))) / 2;
      if (A) { const y = Y(c); h += `<path d="M${X(a)} ${y}H${X(b)}M${X(a)} ${y - 6}v12M${X(b)} ${y - 6}v12" stroke="${or}" stroke-width="1.5" fill="none"/>` + tag((X(a) + X(b)) / 2, y - 14, fmt(s.g, A), or); }
      else { const x = X(c); h += `<path d="M${x} ${Y(a)}V${Y(b)}M${x - 6} ${Y(a)}h12M${x - 6} ${Y(b)}h12" stroke="${or}" stroke-width="1.5" fill="none"/>` + tag(x + 26, (Y(a) + Y(b)) / 2, fmt(s.g, A), or); }
    });
    g.sizes.forEach(s => { const r = s.r; h += s.A ? tag(X(r.x + r.w / 2), Y(r.y + r.h) + 14, '= L ' + fmt(s.v, true), pk) : tag(X(r.x + r.w) + 34, Y(r.y + r.h / 2), '= H ' + fmt(s.v, false), pk); });
    return `<svg class="lx-snap" width="${X(o.W)}" height="${Y(o.H)}" style="position:absolute;left:0;top:0;overflow:visible;pointer-events:none;z-index:30">${h}</svg>`;
  }

  // ---------- outils de la sélection multiple ----------
  /** rangée (A = true) ou colonne (A = false) sans chevauchement, triée, avec ses écarts ; sinon null */
  function arrange(L) {
    if (L.length < 2) return null;
    for (const A of [true, false]) {
      const s = L.slice().sort((a, b) => st(a, A) - st(b, A));
      if (s.every((r, i) => !i || st(r, A) >= en(s[i - 1], A) - .01)) return { A, items: s, gaps: s.slice(1).map((r, i) => st(r, A) - en(s[i], A)) };
    }
    return null;
  }
  function setGap(items, A, g) { let p = st(items[0], A); items.forEach(r => { if (A) r.x = p; else r.y = p; p += sz(r, A) + g; }); }
  /** écarts égaux entre le premier et le dernier ; renvoie l'écart, ou null s'il n'y a pas la place */
  function distribute(L, A) {
    const s = L.slice().sort((a, b) => st(a, A) - st(b, A)), tot = en(s[s.length - 1], A) - st(s[0], A);
    const g = (tot - s.reduce((t, r) => t + sz(r, A), 0)) / (s.length - 1);
    if (g < 0) return null;
    setGap(s, A, g); return g;
  }
  /** range en lignes et colonnes à partir du coin haut-gauche, avec l'écart gx (horizontal) et gy (vertical) */
  function tidy(L, gx, gy) {
    if (gy == null) gy = gx;
    const x0 = Math.min(...L.map(r => r.x)), y0 = Math.min(...L.map(r => r.y)), rows = [];
    L.slice().sort((p, q) => (p.y + p.h / 2) - (q.y + q.h / 2)).forEach(r => {
      const row = rows[rows.length - 1], cy = r.y + r.h / 2;
      if (row && cy < row.cy + row.h / 2) row.items.push(r); else rows.push({ cy, h: r.h, items: [r] });
    });
    rows.forEach(r => r.items.sort((p, q) => p.x - q.x));
    const nc = Math.max(...rows.map(r => r.items.length)), cw = Array(nc).fill(0);
    rows.forEach(r => r.items.forEach((o, i) => cw[i] = Math.max(cw[i], o.w)));
    let y = y0;
    rows.forEach(r => { const rh = Math.max(...r.items.map(o => o.h)); let x = x0; r.items.forEach((o, i) => { o.x = x; o.y = y; x += cw[i] + gx; }); y += rh + gy; });
  }
  function sameSize(L, A) { const m = Math.max(...L.map(r => sz(r, A))); L.forEach(r => { if (A) r.w = m; else r.h = m; }); return m; }

  /** pastilles d'écart (HTML) entre les composants d'une rangée / colonne sélectionnée */
  function gapPills(ar, o, cls) {
    if (!ar) return '';
    const fmt = o.unit || (v => Math.round(v));
    return ar.items.slice(1).map((r, i) => {
      const p = ar.items[i], A = ar.A, m = (en(p, A) + st(r, A)) / 2, c0 = Math.max(st(p, !A), st(r, !A)), c1 = Math.min(en(p, !A), en(r, !A)), c = c0 < c1 ? (c0 + c1) / 2 : (st(p, !A) + en(r, !A)) / 2;
      return `<span class="lx-gp${A ? '' : ' v'}${cls ? ' ' + cls : ''}" data-gap="${i}" title="Tirer pour changer tous les écarts (Maj : par 10)" style="left:${(A ? m : c) * o.sx}px;top:${(A ? c : m) * o.sy}px">${fmt(ar.gaps[i], A)}</span>`;
    }).join('');
  }

  /** icônes des outils (24×24, trait) */
  const ICONS = {
    dh: 'M3 3v18M21 3v18M7 7h3v10H7zM14 7h3v10h-3z', dv: 'M3 3h18M3 21h18M7 7h10v3H7zM7 14h10v3H7z',
    grid: 'M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z',
    sw: 'M4 12h16M4 12l3-3M4 12l3 3M20 12l-3-3M20 12l-3 3', sh: 'M12 4v16M12 4l-3 3M12 4l3 3M12 20l-3-3M12 20l3-3'
  };

  w.LxSnap = { move, resize, guides, svg, arrange, setGap, distribute, tidy, sameSize, gapPills, ICONS };
})(window);
