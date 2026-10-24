/* Rebond de ce qui est nouveau (DEV-POC.md §116), appelé par le composant NewMark :
   linkiiNew.mark(portée, famille, identifiants, via) compare la liste reçue à celle de la dernière visite (mémoire du navigateur, par organisation,
   personne et aire) et fait rebondir (classe « landed », styles de la médiathèque §113) les éléments data-nid apparus depuis.
   La première visite d'une liste sert de point de départ. Sans mémoire disponible (navigation privée) : aucun rebond. */
(function (w, d) {
  'use strict';
  var DURATION = 2800, STAGGER = 90, MAX = 12;

  function load(key) {
    try { var s = w.localStorage.getItem(key); return s === null ? null : JSON.parse(s); } catch (x) { return undefined; }
  }
  function save(key, ids) { try { w.localStorage.setItem(key, JSON.stringify(ids)); } catch (x) { /* plein ou refusé */ } }
  function find(id) { return id ? d.querySelector('[data-nid="' + String(id).replace(/["\\]/g, '') + '"]') : null; }
  function shown(el) { return !!(el && el.getClientRects && el.getClientRects().length); }

  // éléments à faire rebondir : l'élément lui-même, sinon celui que « via » désigne (dossier qui contient le nouveau média)
  function targets(fresh, via) {
    var list = [];
    for (var i = 0; i < fresh.length; i++) {
      var el = find(fresh[i]);
      if (!shown(el) && via) el = find(via[fresh[i]]);
      if (shown(el) && list.indexOf(el) === -1) list.push(el);
    }
    return list;
  }

  function play(list) {
    list = list.filter(function (el) { return !el.classList.contains('landed'); }).slice(0, MAX);   // déjà en train de rebondir (import de la médiathèque)
    list.forEach(function (el, i) {
      if (i > 0) el.style.animationDelay = (i * STAGGER) + 'ms';
      el.classList.add('landed');
      setTimeout(function () { el.classList.remove('landed'); el.style.animationDelay = ''; }, DURATION + i * STAGGER);
    });
    var first = list[0];
    if (first) {   // le premier est amené à l'écran s'il est hors de la fenêtre
      var r = first.getBoundingClientRect(), h = w.innerHeight || d.documentElement.clientHeight;
      if (r.top < 70 || r.bottom > h - 20) first.scrollIntoView({ block: 'center', behavior: 'smooth' });
    }
  }

  w.linkiiNew = {
    mark: function (scope, kind, ids, via) {
      var key = 'lk-new:' + scope + ':' + kind, seen = load(key);
      if (seen === undefined) return;
      ids = ids || [];
      save(key, ids);
      if (seen === null) return;   // première visite : point de départ
      var old = {};
      seen.forEach(function (i) { old[i] = 1; });
      var fresh = ids.filter(function (i) { return !old[i]; });
      if (!fresh.length) return;
      var tries = 0;
      (function attempt() {   // la page peut finir de s'afficher (miniatures, listes) juste après
        var list = targets(fresh, via);
        if (list.length || ++tries >= 6) { if (list.length) play(list); return; }
        setTimeout(attempt, 80);
      })();
    }
  };
})(window, document);
