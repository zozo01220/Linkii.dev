/* Carrousel 3D de l'aperçu d'un contenu : glisser à la souris ou au doigt, ou défilement horizontal (molette, pavé tactile).
   Blazor tient l'état (image du centre) : ici on ne fait que traduire le geste en « un cran à gauche / à droite »
   (méthode .NET Step(±1)). */
(function (w) {
  'use strict';

  function init(view, dotnet) {
    if (!view || view.__lkCar) return;
    view.__lkCar = true;

    view.addEventListener('wheel', function (e) {
      if (Math.abs(e.deltaX) > Math.abs(e.deltaY) && Math.abs(e.deltaX) > 8) {
        e.preventDefault();
        dotnet.invokeMethodAsync('Step', e.deltaX > 0 ? 1 : -1);
      }
    }, { passive: false });

    var startX = null;
    view.addEventListener('pointerdown', function (e) {
      if (e.button !== 0 || e.target.closest('button')) return;
      startX = e.clientX;
      view.classList.add('drag');
    });
    function end(e) {
      if (startX === null) return;
      var dx = e.clientX - startX;
      startX = null;
      view.classList.remove('drag');
      if (Math.abs(dx) > 40) dotnet.invokeMethodAsync('Step', dx < 0 ? 1 : -1);
    }
    view.addEventListener('pointerup', end);
    view.addEventListener('pointercancel', function () { startX = null; view.classList.remove('drag'); });
    view.addEventListener('dragstart', function (e) { e.preventDefault(); });   // pas de glisser natif des images
  }

  w.linkiiCarousel = { init: init };
})(window);
