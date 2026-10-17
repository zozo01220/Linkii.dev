// Application Android du back-office : confie le jeton de notification (Firebase) à la session de la personne connectée.
// Le pont window.LinkiiAdmin n'existe que dans l'application ; dans un navigateur ce fichier ne fait rien.
(function () {
    var bridge = window.LinkiiAdmin;
    if (!bridge || typeof bridge.pushToken !== 'function') return;
    var tries = 0;
    function register() {
        var token = bridge.pushToken();
        if (!token) { if (++tries < 20) setTimeout(register, 3000); return; }   // le jeton arrive peu après le premier lancement
        var last = null;
        try { last = localStorage.getItem('linkii.pushSent'); } catch (e) { }
        if (last === token) return;
        fetch('/api/push/register', {
            method: 'POST', credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ token: token, name: bridge.deviceName ? bridge.deviceName() : 'Appareil Android' })
        }).then(function (r) {
            if (r.ok) { try { localStorage.setItem('linkii.pushSent', token); } catch (e) { } }   // 401 : pas encore connecté, nouvel essai au prochain chargement
        }).catch(function () { });
    }
    register();
})();

// Verrouillage biométrique de l'application (page Mon compte) : sans l'application, rien n'est proposé.
window.linkiiBio = {
    inApp: function () { var b = window.LinkiiAdmin; return !!(b && typeof b.biometricAvailable === 'function'); },
    available: function () { var b = window.LinkiiAdmin; return !!(b && typeof b.biometricAvailable === 'function' && b.biometricAvailable()); },
    enabled: function () { var b = window.LinkiiAdmin; return !!(b && typeof b.biometricEnabled === 'function' && b.biometricEnabled()); },
    set: function (on) { var b = window.LinkiiAdmin; if (b && typeof b.setBiometric === 'function') b.setBiometric(!!on); }
};
