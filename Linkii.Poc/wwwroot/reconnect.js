// Reconnexion automatique : après un arrêt/redémarrage du serveur, le circuit Blazor est perdu.
// On sonde le serveur et on recharge la page dès qu'il répond, sans intervention de l'utilisateur.
(function () {
    var banner = null, title = null, desc = null, timer = null, down = false, found = false, blocked = [];

    var CSS = '.lk-lock{position:fixed;inset:0;z-index:10000;display:grid;place-items:center;padding:16px;'
        + 'background:rgba(11,31,58,.62);backdrop-filter:blur(3px);cursor:wait;font:400 15px/1.5 "IBM Plex Sans",sans-serif;color:#0B1F3A}'
        + '.lk-box{width:min(420px,100%);background:#fff;border-radius:14px;padding:32px 28px 26px;text-align:center;'
        + 'box-shadow:0 20px 60px rgba(11,31,58,.35)}'
        + '.lk-spin{width:44px;height:44px;margin:0 auto 18px;border-radius:50%;border:4px solid #DDE3EA;'
        + 'border-top-color:#3B82C4;animation:lk-spin .9s linear infinite}'
        + '@keyframes lk-spin{to{transform:rotate(360deg)}}'
        + '.lk-box h2{font-size:19px;font-weight:600;margin:0 0 6px}'
        + '.lk-box p{margin:0;color:#5A6B80;font-size:14px}'
        + '.lk-box .lk-note{margin-top:18px;padding:8px 12px;border-radius:8px;font-size:13px;background:#FBF0DC;color:#7A4F12}';

    function setText(isFound) {
        title.textContent = isFound ? 'Serveur retrouvé' : 'Connexion perdue';
        desc.textContent = isFound ? 'Rechargement de la page…' : 'Reconnexion en cours… la page se rechargera d’elle-même.';
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
        banner.innerHTML = '<div class="lk-box"><div class="lk-spin" aria-hidden="true"></div><h2></h2><p></p>'
            + '<div class="lk-note">Merci de ne pas fermer cette page.</div></div>';
        banner.appendChild(style);
        title = banner.querySelector('h2');
        desc = banner.querySelector('p');
        setText(false);
        // Le reste de la page devient inerte : ni clic, ni tabulation, ni lecteur d'écran.
        blocked = Array.prototype.slice.call(document.body.children);
        blocked.forEach(function (el) { el.inert = true; });
        document.body.appendChild(banner);
    }

    function hide() {
        blocked.forEach(function (el) { el.inert = false; });
        blocked = [];
        if (banner) { banner.remove(); banner = null; }
    }

    function probe() {
        fetch('/', { method: 'HEAD', cache: 'no-store' })
            .then(function (r) {
                if (!(r.ok || r.status < 500) || found) return;
                found = true;
                if (title) setText(true);
                setTimeout(function () { location.reload(); }, 600);
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

    function start() {
        if (down || leaving) return;
        down = true;
        show();
        probe();
        timer = setInterval(probe, 2000);
    }

    function stop() {
        down = false;
        clearInterval(timer);
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
