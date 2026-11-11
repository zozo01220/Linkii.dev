/* Panneau de l'assistant (AssistantDock.razor) : Ctrl + J pour l'ouvrir ou le fermer, largeur réglée en glissant le bord gauche,
   classe « ai-docked » sur body (la page et l'éditeur plein écran laissent la place au panneau), état retenu par le navigateur. */
(function (w) {
  'use strict';
  var ref = null, MIN = 340, MAX = 560;
  var prefs = function () { return w.linkiiPrefs || { get: function () { return null; }, set: function () {} }; };
  function setWidth(px) { w.document.documentElement.style.setProperty('--aiw', px + 'px'); }
  w.document.addEventListener('keydown', function (e) {
    if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && (e.key === 'j' || e.key === 'J')) {
      if (!ref) return;
      e.preventDefault();
      ref.invokeMethodAsync('ToggleFromKey').catch(function () {});
      return;
    }
    // Entrée envoie la demande (Maj + Entrée : nouvelle ligne) ; le composant reçoit aussi la touche
    if (e.key === 'Enter' && !e.shiftKey && e.target && e.target.closest && e.target.closest('.aip-comp')) e.preventDefault();
  }, true);
  w.linkiiAiPanel = {
    init: function (r) {
      ref = r;
      var v = parseInt(prefs().get('aiw'), 10);
      if (v >= MIN && v <= MAX) setWidth(v);
      return prefs().get('aiopen');
    },
    docked: function (on) {
      w.document.body.classList.toggle('ai-docked', !!on);
      prefs().set('aiopen', on ? '1' : '0');
    },
    grip: function (el) {
      if (!el || el.__aip) return;
      el.__aip = true;
      var x0 = null, w0 = 0;
      el.addEventListener('pointerdown', function (e) {
        var p = el.closest('.aip'); if (!p) return;
        x0 = e.clientX; w0 = p.offsetWidth; el.setPointerCapture(e.pointerId);
        w.document.body.classList.add('aip-resizing');
      });
      el.addEventListener('pointermove', function (e) {
        if (x0 === null) return;
        setWidth(Math.max(MIN, Math.min(MAX, w0 + (x0 - e.clientX))));
      });
      var end = function () {
        if (x0 === null) return;
        x0 = null;
        w.document.body.classList.remove('aip-resizing');
        var p = el.closest('.aip'); if (p) prefs().set('aiw', String(p.offsetWidth));
        w.dispatchEvent(new Event('resize'));
      };
      el.addEventListener('pointerup', end);
      el.addEventListener('pointercancel', end);
    }
  };
})(window);
