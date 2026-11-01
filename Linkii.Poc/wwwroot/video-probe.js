/* Back-office : dimensions d'une vidéo (fichier ou adresse), lues dans ses métadonnées par le navigateur.
   window.linkiiVideo.size(url) -> [largeur, hauteur], ou null si illisible. */
(function (w) {
  'use strict';
  w.linkiiVideo = {
    size: function (url) {
      return new Promise(function (resolve) {
        var v = document.createElement('video'), done = false;
        function finish(r) {
          if (done) return; done = true;
          v.removeAttribute('src'); try { v.load(); } catch (e) {}
          resolve(r);
        }
        v.preload = 'metadata'; v.muted = true;
        v.onloadedmetadata = function () { finish(v.videoWidth > 0 && v.videoHeight > 0 ? [v.videoWidth, v.videoHeight] : null); };
        v.onerror = function () { finish(null); };
        setTimeout(function () { finish(null); }, 8000);
        v.src = url;
      });
    }
  };
})(window);
