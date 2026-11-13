// Mur d'écrans de la page de connexion (LoginWall.razor) : horloge à l'heure, le mur suit la souris,
// vague de confirmation à l'envoi du formulaire. Tout est délégué au document : rien à rebrancher après une navigation.
(function () {
    function tick() {
        var hh = document.querySelector('[data-lw-hh]');
        if (!hh) return;
        var n = new Date(), mm = document.querySelector('[data-lw-mm]'), d = document.querySelector('[data-lw-date]');
        hh.textContent = String(n.getHours()).padStart(2, '0');
        if (mm) mm.textContent = String(n.getMinutes()).padStart(2, '0');
        if (d) d.textContent = n.toLocaleDateString('fr-CH', { weekday: 'long', day: 'numeric', month: 'long' });
    }
    document.addEventListener('DOMContentLoaded', tick);
    setInterval(tick, 1000);

    var still = window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;
    var tilted = null;
    if (!still) document.addEventListener('pointermove', function (e) {
        var stage = e.target.closest ? e.target.closest('.lw-stage') : null;
        if (!stage) {
            if (tilted) { tilted.style.removeProperty('--ry'); tilted.style.removeProperty('--rx'); tilted = null; }
            return;
        }
        var wall = stage.querySelector('.lw-wall'), r = stage.getBoundingClientRect();
        if (!wall || !r.width || !r.height) return;
        var x = (e.clientX - r.left) / r.width - .5, y = (e.clientY - r.top) / r.height - .5;
        wall.style.setProperty('--ry', (-20 + x * 10) + 'deg');
        wall.style.setProperty('--rx', (5 - y * 7) + 'deg');
        tilted = wall;
    });

    // De temps en temps, une seule image sur tout le mur (mur vidéo) : chaque écran reçoit l'image entière, décalée de sa propre position dans le mur.
    function pano() {
        var wall = document.querySelector('.lw-wall');
        if (!wall || !wall.offsetWidth || document.hidden || wall.classList.contains('hello')) return;
        var scr = wall.querySelectorAll('.lw-scr');
        for (var i = 0; i < scr.length; i++) {
            var v = scr[i].querySelector('.lw-view'), p = v && v.querySelector('.lw-pano');
            if (!p) continue;
            var x = 0, y = 0, el = v;
            while (el && el !== wall) { x += el.offsetLeft; y += el.offsetTop; el = el.offsetParent; }
            if (!el) continue;
            p.style.setProperty('--px', -x + 'px'); p.style.setProperty('--py', -y + 'px');
            p.style.setProperty('--pw', wall.offsetWidth + 'px'); p.style.setProperty('--ph', wall.offsetHeight + 'px');
        }
        wall.classList.add('pano');
        setTimeout(function () { wall.classList.remove('pano'); }, 6500);
    }
    if (!still) { setTimeout(pano, 8000); setInterval(pano, 24000); }

    document.addEventListener('submit', function (e) {
        if (!e.target.closest || !e.target.closest('.lg-main')) return;
        var wall = document.querySelector('.lw-wall');
        if (wall) wall.classList.add('hello');
    });
})();
