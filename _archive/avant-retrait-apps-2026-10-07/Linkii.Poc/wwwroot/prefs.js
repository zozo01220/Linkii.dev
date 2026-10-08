/* Back-office : préférences d'affichage mémorisées dans le navigateur (ex. vue cartes / liste).
   Navigation privée ou stockage bloqué : sans effet, la valeur par défaut s'applique. */
(function (w) {
  'use strict';
  w.linkiiPrefs = {
    get: function (k) { try { return w.localStorage.getItem('lk_pref_' + k); } catch (e) { return null; } },
    set: function (k, v) { try { w.localStorage.setItem('lk_pref_' + k, v); } catch (e) {} }
  };
})(window);
