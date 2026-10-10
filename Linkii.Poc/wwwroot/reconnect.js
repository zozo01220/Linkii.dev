// Reconnexion automatique : après un arrêt/redémarrage du serveur, le circuit Blazor est perdu.
// On sonde le serveur et on recharge la page dès qu'il répond, sans intervention de l'utilisateur.
(function () {
    var banner = null, timer = null, down = false;

    function show() {
        if (banner) return;
        banner = document.createElement('div');
        banner.setAttribute('role', 'status');
        banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:10000;padding:8px 16px;'
            + 'background:#0B1F3A;color:#fff;font:500 14px/1.4 "IBM Plex Sans",sans-serif;text-align:center';
        banner.textContent = 'Connexion perdue, reconnexion en cours…';
        document.body.appendChild(banner);
    }

    function hide() {
        if (banner) { banner.remove(); banner = null; }
    }

    function probe() {
        fetch('/', { method: 'HEAD', cache: 'no-store' })
            .then(function (r) { if (r.ok || r.status < 500) location.reload(); })
            .catch(function () { });
    }

    function start() {
        if (down) return;
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
