// Reconnexion automatique : après un arrêt/redémarrage du serveur, le circuit Blazor est perdu.
// On sonde le serveur et on recharge la page dès qu'il répond, sans intervention de l'utilisateur.
(function () {
    var banner = null, title = null, desc = null, note = null, bar = null, ticker = null, timer = null, down = false, found = false, blocked = [];
    var nextAt = 0, delay = 2000;

    var CSS = '.lk-lock{position:fixed;inset:0;z-index:10000;display:grid;place-items:center;padding:16px;'
        + 'background:rgba(11,31,58,.78);cursor:wait;font:400 15px/1.5 "IBM Plex Sans",sans-serif;color:#0B1F3A}'
        + '.lk-box{width:min(420px,100%);background:#fff;border-radius:14px;padding:32px 28px 26px;text-align:center;'
        + 'box-shadow:0 20px 60px rgba(11,31,58,.35)}'
        + '.lk-wm{display:inline-block;padding-top:.55em;font-size:64px;line-height:1;font-weight:500;letter-spacing:1px;color:#0B1F3A;white-space:nowrap}'
        + '.lk-i{position:relative;display:inline-block}'
        + '.lk-d{position:absolute;left:50%;top:.07em;width:.15em;height:.15em;margin-left:-.075em;border-radius:50%;background:#0B1F3A;'
        + 'animation:lk-fl 2.4s ease-in-out infinite}'
        + '.lk-i:nth-of-type(2) .lk-d{animation-delay:.4s}'
        + '.lk-i:nth-of-type(3) .lk-d{animation-delay:.8s;background:#E8871E}'
        + '@keyframes lk-fl{0%,100%{transform:translateY(-.35em);opacity:.35}45%{transform:translateY(-.7em);opacity:1}}'
        + '.lk-ok .lk-d{animation:none;opacity:1;transform:translateY(0);transition:transform .5s cubic-bezier(.3,1.8,.5,1)}'
        + '.lk-ok .lk-i:nth-of-type(2) .lk-d{transition-delay:.25s}'
        + '.lk-ok .lk-i:nth-of-type(3) .lk-d{transition-delay:.5s}'
        + '.lk-box h2{font-size:20px;font-weight:500;margin:10px 0 4px}'
        + '.lk-ok h2{color:#1F5A3A}'
        + '.lk-box p{margin:0;color:#5A6B80;font-size:14px}'
        + '.lk-bar{height:4px;background:#E6E9EE;border-radius:2px;margin:14px auto 6px;width:70%;overflow:hidden}'
        + '.lk-bar i{display:block;height:100%;width:0;background:#0B1F3A}'
        + '.lk-ok .lk-bar i{width:100%!important;background:#2E9E63}'
        + '.lk-box .lk-note{margin-top:12px;padding:9px 12px;border-radius:8px;font-size:13px;background:#EAF3EE;color:#1F5A3A}'
        + '.lk-box button{margin-top:14px;padding:7px 14px;border-radius:8px;border:1px solid #C9D2DD;background:#fff;color:#0B1F3A;font:inherit;font-size:13px;cursor:pointer}'
        + '.lk-ok button{visibility:hidden}';

    function setText(isFound) {
        title.textContent = isFound ? 'Reconnecté' : 'Connexion perdue';
        desc.textContent = isFound ? 'Chaque point est revenu sur son i. Retour dans un instant…'
            : 'Les points se sont envolés. Nouvelle tentative dans ' + secs() + ' s.';
        note.textContent = isFound ? 'Rien n’a été interrompu sur vos écrans.'
            : 'Vos écrans continuent de diffuser. Cette page se rechargera d’elle-même.';
        banner.classList.toggle('lk-ok', isFound);
    }

    function secs() { return Math.max(1, Math.ceil((nextAt - Date.now()) / 1000)); }

    // Barre et décompte jusqu'à la prochaine sonde.
    function tick() {
        if (!banner || found) return;
        var left = Math.max(0, nextAt - Date.now());
        bar.style.width = Math.min(100, (1 - left / delay) * 100) + '%';
        desc.textContent = 'Les points se sont envolés. Nouvelle tentative dans ' + secs() + ' s.';
    }

    function show() {
        if (banner) return;
        var style = document.createElement('style');
        style.textContent = CSS;
        banner = document.createElement('div');
        banner.className = 'lk-lock';
        banner.setAttribute('role', 'alertdialog');
        banner.setAttribute('aria-modal', 'true');
        banner.setAttribute('aria-live', 'assertive');
        banner.innerHTML = '<div class="lk-box"><div class="lk-wm" aria-hidden="true">L<span class="lk-i">ı<b class="lk-d"></b></span>nk'
            + '<span class="lk-i">ı<b class="lk-d"></b></span>v<span class="lk-i">ı<b class="lk-d"></b></span></div>'
            + '<h2></h2><p></p><div class="lk-bar"><i></i></div><div class="lk-note"></div>'
            + '<button type="button">Réessayer maintenant</button></div>';
        banner.appendChild(style);
        title = banner.querySelector('h2');
        desc = banner.querySelector('p');
        note = banner.querySelector('.lk-note');
        bar = banner.querySelector('.lk-bar i');
        banner.querySelector('button').addEventListener('click', function () { probe(); });
        setText(false);
        ticker = setInterval(tick, 250);
        // Le reste de la page devient inerte : ni clic, ni tabulation, ni lecteur d'écran.
        blocked = Array.prototype.slice.call(document.body.children);
        blocked.forEach(function (el) { el.inert = true; el.style.visibility = 'hidden'; });   // plus rien à peindre derrière : vidéos et animations de la page ne tournent plus pour rien
        document.body.appendChild(banner);
    }

    function hide() {
        blocked.forEach(function (el) { el.inert = false; el.style.visibility = ''; });
        blocked = [];
        clearInterval(ticker);
        if (banner) { banner.remove(); banner = null; }
    }

    function probe() {
        fetch('/', { method: 'HEAD', cache: 'no-store' })
            .then(function (r) {
                if (!(r.ok || r.status < 500) || found) return;
                found = true;
                if (title) setText(true);
                setTimeout(function () { location.reload(); }, 1400);   // le temps que les points retombent sur leurs i
            })
            .catch(function () { });
    }

    // L'utilisateur quitte la page (lien vers « Se connecter avec Facebook », Google…) : le circuit se ferme de lui-même, ce n'est pas une panne.
    // Sans cela, le rechargement de la page annulerait la navigation en cours vers le fournisseur.
    var leaving = false, leavingTimer = null;
    window.addEventListener('beforeunload', function () {
        leaving = true;
        clearTimeout(leavingTimer);
        leavingTimer = setTimeout(function () { leaving = false; }, 15000);   // navigation annulée ou téléchargement : la page reste, la surveillance reprend
    });
    window.addEventListener('pageshow', function () { leaving = false; });

    // Sonde espacée au fil du temps (2 s, puis jusqu'à 15 s) et suspendue quand l'onglet est caché.
    var tries = 0;
    function schedule() {
        clearTimeout(timer);
        delay = Math.min(2000 + tries * 1000, 15000);
        nextAt = Date.now() + delay;
        timer = setTimeout(function () {
            if (!down) return;
            if (!document.hidden) probe();
            tries++;
            schedule();
        }, delay);
    }

    function start() {
        if (down || leaving) return;
        down = true;
        tries = 0;
        show();
        probe();
        schedule();
    }

    function stop() {
        down = false;
        clearTimeout(timer);
        hide();
    }

    window.addEventListener('online', function () { if (down) probe(); });
    document.addEventListener('visibilitychange', function () { if (down && !document.hidden) probe(); });

    Blazor.start({
        circuit: {
            reconnectionOptions: { maxRetries: 3, retryIntervalMilliseconds: 2000 },
            reconnectionHandler: {
                onConnectionDown: start,
                onConnectionUp: stop
            }
        }
    });
})();
