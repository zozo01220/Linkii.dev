/* Back-office : durée d'une vidéo YouTube, lue avec l'API IFrame de YouTube (aucune clé d'API).
   window.linkiiYt.duration(videoId) -> durée en secondes, ou 0 si elle est inconnue (vidéo privée, intégration refusée, hors ligne). */
(function (w) {
  'use strict';
  var apiState = 0, waiting = [];   // 0 : pas chargée, 1 : chargement, 2 : prête, -1 : indisponible

  function load(cb) {
    if (apiState === 2) { cb(w.YT); return; }
    if (apiState === -1) { cb(null); return; }
    waiting.push(cb);
    if (apiState === 1) return;
    apiState = 1;
    var previous = w.onYouTubeIframeAPIReady;
    w.onYouTubeIframeAPIReady = function () {
      if (previous) previous();
      apiState = 2;
      var q = waiting; waiting = [];
      for (var i = 0; i < q.length; i++) q[i](w.YT);
    };
    var s = document.createElement('script');
    s.src = 'https://www.youtube.com/iframe_api';
    s.onerror = function () { apiState = -1; var q = waiting; waiting = []; for (var i = 0; i < q.length; i++) q[i](null); };
    document.head.appendChild(s);
    setTimeout(function () { if (apiState === 1) { apiState = -1; var q = waiting; waiting = []; for (var i = 0; i < q.length; i++) q[i](null); } }, 12000);
  }

  var queue = Promise.resolve();   // une sonde à la fois

  function probe(videoId) {
    return new Promise(function (resolve) {
      load(function (YT) {
        if (!YT || !YT.Player) { resolve(0); return; }
        var holder = document.createElement('div');
        holder.style.cssText = 'position:fixed;left:-10000px;top:0;width:200px;height:120px;overflow:hidden';
        var target = document.createElement('div');
        holder.appendChild(target);
        document.body.appendChild(holder);
        var player = null, done = false, timer = null;
        function finish(seconds) {
          if (done) return; done = true;
          clearInterval(timer);
          try { if (player && player.destroy) player.destroy(); } catch (e) {}
          if (holder.parentNode) holder.parentNode.removeChild(holder);
          resolve(seconds > 0 ? Math.round(seconds) : 0);
        }
        try {
          player = new YT.Player(target, {
            videoId: videoId,
            host: 'https://www.youtube-nocookie.com',
            playerVars: { controls: 0, rel: 0, playsinline: 1, origin: location.origin },
            events: {
              onReady: function () {
                var tries = 0;
                timer = setInterval(function () {   // la durée est disponible quand les métadonnées sont chargées
                  tries++;
                  var d = 0;
                  try { d = player.getDuration(); } catch (e) {}
                  if (d > 0 || tries > 24) finish(d);
                }, 400);
              },
              onError: function () { finish(0); }
            }
          });
        } catch (e) { finish(0); }
        setTimeout(function () { finish(0); }, 12000);
      });
    });
  }

  w.linkiiYt = {
    duration: function (videoId) {
      var run = queue.then(function () { return probe(videoId); });
      queue = run.catch(function () { return 0; });
      return run.catch(function () { return 0; });
    }
  };
})(window);
