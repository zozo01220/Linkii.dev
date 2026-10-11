/* Service worker du player : permet de redémarrer sans réseau.
   - /media/*  : cache-first (le cache est rempli par player.js), avec support des requêtes Range
   - le reste (page, JS, lib SignalR) : réseau d'abord, repli sur le cache s'il ne répond pas en NET_WAIT ms ou échoue */
var STATIC = 'linkii-static-v1';
var MEDIA = 'linkii-media-v1';
var NET_WAIT = 4000;   // au-delà, la page et les scripts du player démarrent sur la copie locale

self.addEventListener('install', function () { self.skipWaiting(); });
self.addEventListener('activate', function (e) { e.waitUntil(self.clients.claim()); });

function rangeResponse(req, res) {
  var m = /bytes=(\d+)-(\d*)/.exec(req.headers.get('range') || '');
  if (!m) return res;
  // blob() reste adossé au fichier du cache : slice() n'en lit que la tranche demandée (pas de copie complète en mémoire)
  return res.blob().then(function (blob) {
    var size = blob.size;
    var start = parseInt(m[1], 10);
    var end = m[2] ? Math.min(parseInt(m[2], 10), size - 1) : size - 1;
    var type = res.headers.get('Content-Type') || 'video/mp4';
    return new Response(blob.slice(start, end + 1, type), {
      status: 206,
      statusText: 'Partial Content',
      headers: {
        'Content-Type': type,
        'Content-Length': String(end - start + 1),
        'Content-Range': 'bytes ' + start + '-' + end + '/' + size,
        'Accept-Ranges': 'bytes'
      }
    });
  });
}

self.addEventListener('fetch', function (e) {
  var req = e.request;
  if (req.method !== 'GET') return;
  var url = new URL(req.url);

  if (url.origin === location.origin && url.pathname.indexOf('/media/') === 0) {
    e.respondWith(
      caches.open(MEDIA).then(function (c) { return c.match(url.pathname); }).then(function (hit) {
        if (hit) return rangeResponse(req, hit);
        return fetch(req);
      })
    );
    return;
  }

  var isPlayerAsset = url.origin === location.origin && url.pathname.indexOf('/player/') === 0;
  var isSignalR = url.hostname === 'cdn.jsdelivr.net';
  if (isPlayerAsset || isSignalR) {
    e.respondWith(
      caches.open(STATIC).then(function (c) {
        var fromCache = function () { return c.match(req, { ignoreSearch: true }); };
        var net = fetch(req).then(function (res) {
          if (res && (res.ok || res.type === 'opaque')) c.put(req, res.clone());
          return res;
        });
        // le réseau gagne s'il répond à temps ; sinon la copie locale (si elle existe) ; sans copie, on attend le réseau
        var timer, late = new Promise(function (resolve) {
          timer = setTimeout(function () { fromCache().then(function (hit) { if (hit) resolve(hit); }); }, NET_WAIT);
        });
        var failed = net.catch(function () { return fromCache().then(function (hit) { if (hit) return hit; throw new Error('offline'); }); });
        return Promise.race([failed, late]).then(function (r) { clearTimeout(timer); return r; }, function (err) { clearTimeout(timer); throw err; });
      })
    );
  }
});
