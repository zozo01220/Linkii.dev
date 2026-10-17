// Look « app » : les boutons d'ajout de la page ([data-fab="Libellé"]) sont remplacés par un bouton flottant rond.
// Un seul bouton d'ajout : il le déclenche directement. Plusieurs : le bouton se déroule (un petit bouton avec son libellé par action).
// Le bouton d'origine reste dans la page (masqué par le CSS .is-app) : le bouton flottant le « clique », donc les droits et
// les conditions de la page s'appliquent sans rien dupliquer. Sans body.is-app (PC, navigateur), ce fichier ne fait rien.
(function () {
    'use strict';
    var root = null, sig = '', open = false, timer = null;
    var PLUS = '<svg viewBox="0 0 24 24"><path d="M12 5v14M5 12h14"/></svg>';

    function sources() { return [].slice.call(document.querySelectorAll('[data-fab]')); }
    function esc(t) { return String(t).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
    function icon(el) { var s = el.querySelector('svg'); return s ? s.outerHTML : PLUS; }
    // Fenêtre, tiroir, barre de sélection ou menu « Plus » ouverts : le bouton s'efface pour ne pas gêner
    function blocked() { return [].some.call(document.querySelectorAll('.modal-backdrop, .lx-drawer, .lx-selbar, .app-more, .lp, .pn-drawer, .kebab-menu, .pe-addmenu'), function (e) { return e.getClientRects().length > 0; }); }
    function setOpen(v) { open = v; if (root) { root.classList.toggle('open', v); var m = root.querySelector('.af-main'); if (m) m.setAttribute('aria-expanded', v ? 'true' : 'false'); } }

    function remove() { if (root && root.parentNode) root.parentNode.removeChild(root); root = null; sig = ''; open = false; }

    function build() {
        if (!document.body || !document.body.classList.contains('is-app')) { remove(); return; }
        var src = sources();
        var next = src.map(function (s) { return s.getAttribute('data-fab'); }).join('|');
        if (!src.length) { remove(); return; }
        if (!root || !root.isConnected || next !== sig) {
            remove();
            sig = next;
            root = document.createElement('div');
            root.className = 'app-fab' + (document.querySelector('.shell.has-tabs') ? ' with-tabs' : '');
            root.setAttribute('data-permanent', '');
            var multi = src.length > 1;
            var html = '';
            if (multi) {
                html += '<div class="af-dim"></div><div class="af-items">';
                src.slice().reverse().forEach(function (s, i) {
                    html += '<button type="button" class="af-item" data-i="' + (src.length - 1 - i) + '" style="--n:' + i + '"><span class="af-l">' + esc(s.getAttribute('data-fab')) + '</span><span class="af-i">' + icon(s) + '</span></button>';
                });
                html += '</div>';
            }
            html += '<button type="button" class="af-main" aria-label="' + esc(multi ? 'Ajouter' : src[0].getAttribute('data-fab')) + '"' + (multi ? ' aria-haspopup="true" aria-expanded="false"' : '') + '>' + (multi ? PLUS : icon(src[0])) + '</button>';
            root.innerHTML = html;
            root.addEventListener('click', function (e) {
                var items = sources();
                var it = e.target.closest('.af-item');
                if (it) { var el = items[+it.getAttribute('data-i')]; setOpen(false); if (el) el.click(); return; }
                if (e.target.closest('.af-dim')) { setOpen(false); return; }
                if (e.target.closest('.af-main')) {
                    if (items.length > 1) setOpen(!open);
                    else if (items[0]) items[0].click();
                }
            });
            document.body.appendChild(root);
        }
        var hide = blocked();
        root.classList.toggle('hide', hide);
        if (hide && open) setOpen(false);
    }

    function schedule() { if (timer) return; timer = setTimeout(function () { timer = null; build(); }, 120); }
    function start() {
        build();
        new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
        document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && open) setOpen(false); });
    }
    if (document.body) start(); else document.addEventListener('DOMContentLoaded', start);
})();
