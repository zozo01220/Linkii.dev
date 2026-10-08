/* Service worker du player : permet de redémarrer sans réseau.
   - /media/*  : cache-first (le cache est rempli par player.js), avec support des requêtes Range
   - le reste (page, JS, lib SignalR) : réseau d'abord, repli sur le cache */
var STATIC = 'linkii-static-v1';
var MEDIA = 'linkii-media-v1';

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
      fetch(req).then(function (res) {
        if (res && (res.ok || res.type === 'opaque')) {
          var copy = res.clone();
          caches.open(STATIC).then(function (c) { c.put(req, copy); });
        }
        return res;
      }).catch(function () {
        return caches.open(STATIC).then(function (c) { return c.match(req, { ignoreSearch: true }); });
      })
    );
  }
});
