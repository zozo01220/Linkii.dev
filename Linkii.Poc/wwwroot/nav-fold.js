// Menu latéral pliable : classe html.nav-shut (mémorisée dans localStorage), bouton .nav-fold ou Ctrl+B.
// Replié, les entrées n'ont plus de texte : on leur pose une infobulle (data-tip) d'après leur libellé.
(function () {
    var root = document.documentElement, KEY = 'linkii-nav-shut';

    function sync() {
        var shut = root.classList.contains('nav-shut');
        var b = document.querySelector('.sidebar .nav-fold');
        if (b) { b.setAttribute('aria-expanded', shut ? 'false' : 'true'); b.setAttribute('aria-label', shut ? 'Déplier le menu' : 'Replier le menu'); b.dataset.tip = shut ? 'Déplier le menu' : 'Replier le menu'; }
    }
    function tips() {
        document.querySelectorAll('.sidebar a.nav, .sidebar button.nav, .sidebar .user-btn').forEach(function (el) {
            if (el.classList.contains('nav-fold')) return;
            var c = el.cloneNode(true);
            c.querySelectorAll('.nbadge, .switch, svg, .menu').forEach(function (x) { x.remove(); });
            var t = el.classList.contains('user-btn') ? ((el.querySelector('.who b') || {}).textContent || '') : (c.textContent || '').trim();
            if (t && el.dataset.tip !== t) el.dataset.tip = t;
        });
    }
    function toggle() {
        root.classList.toggle('nav-shut');
        try { localStorage.setItem(KEY, root.classList.contains('nav-shut') ? '1' : '0'); } catch (e) { }
        sync();
    }

    document.addEventListener('click', function (e) {
        var b = e.target.closest && e.target.closest('.sidebar .nav-fold');
        if (b) { e.preventDefault(); e.stopPropagation(); toggle(); }
    }, true);
    document.addEventListener('keydown', function (e) {
        if (!(e.ctrlKey || e.metaKey) || e.shiftKey || e.altKey || e.key.toLowerCase() !== 'b') return;
        var t = e.target;
        if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) return;
        if (!document.querySelector('.sidebar .nav-fold')) return;
        e.preventDefault(); toggle();
    });

    var pending = false;
    new MutationObserver(function () {
        if (pending) return; pending = true;
        requestAnimationFrame(function () { pending = false; tips(); sync(); });
    }).observe(document.body, { childList: true, subtree: true });
    document.addEventListener('DOMContentLoaded', function () { tips(); sync(); });
})();
