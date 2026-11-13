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
        wall.style.setProperty('--ry', (20 + x * 10) + 'deg');
        wall.style.setProperty('--rx', (5 - y * 7) + 'deg');
        tilted = wall;
    });

    document.addEventListener('submit', function (e) {
        if (!e.target.closest || !e.target.closest('.lg-main')) return;
        var wall = document.querySelector('.lw-wall');
        if (wall) wall.classList.add('hello');
    });
})();
