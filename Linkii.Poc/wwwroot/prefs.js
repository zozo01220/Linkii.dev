/* Back-office : préférences d'affichage mémorisées dans le navigateur (ex. vue cartes / liste).
   Navigation privée ou stockage bloqué : sans effet, la valeur par défaut s'applique. */
(function (w) {
  'use strict';
  w.linkiiPrefs = {
    get: function (k) { try { return w.localStorage.getItem('lk_pref_' + k); } catch (e) { return null; } },
    isApp: function () { return !!(w.document.body && w.document.body.classList.contains('is-app')); },
    reveal: function (id) { var el = w.document.getElementById(id); if (el && el.scrollIntoView) el.scrollIntoView({ behavior: "smooth", block: "center" }); },
    set: function (k, v) { try { w.localStorage.setItem('lk_pref_' + k, v); } catch (e) {} }
  };
})(window);
