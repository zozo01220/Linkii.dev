// Look « app » : garde body.is-app dans l'app Android du back-office, même si la page est relue (bascule simple / expert, rechargement).
// Le serveur la pose d'après l'identifiant LinkiiBackOffice/ ; ici, le pont window.LinkiiAdmin (qui n'existe que dans l'app) et un drapeau de session la rétablissent.
(function () {
    function keep() {
        try {
            var inApp = document.body.classList.contains('is-app') || !!window.LinkiiAdmin || sessionStorage.getItem('linkii-app') === '1';
            if (!inApp) return;
            sessionStorage.setItem('linkii-app', '1');
            if (!document.body.classList.contains('is-app')) document.body.classList.add('is-app');
        } catch (e) { if (window.LinkiiAdmin) document.body.classList.add('is-app'); }
    }
    keep();
    new MutationObserver(keep).observe(document.body, { attributes: true, attributeFilter: ['class'] });
})();
