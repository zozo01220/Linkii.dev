/* Service worker du back-office Linkii (installation sur l'écran d'accueil).
   Il ne met rien en cache : le back-office a besoin du serveur en direct. Il fournit seulement une page claire quand le réseau est coupé.
   Le player des écrans a son propre service worker (/player/sw.js) : on ne s'en mêle pas. */
var CACHE = 'linkii-shell-v1';
var OFFLINE = '/offline.html';

self.addEventListener('install', function (e) {
  e.waitUntil(caches.open(CACHE).then(function (c) { return c.add(OFFLINE); }).then(function () { return self.skipWaiting(); }));
});

self.addEventListener('activate', function (e) {
  e.waitUntil(caches.keys().then(function (keys) {
    return Promise.all(keys.filter(function (k) { return k.indexOf('linkii-shell-') === 0 && k !== CACHE; }).map(function (k) { return caches.delete(k); }));
  }).then(function () { return self.clients.claim(); }));
});

self.addEventListener('fetch', function (e) {
  var r = e.request;
  if (r.mode !== 'navigate') return;                               // images, scripts, API, SignalR : réseau normal
  if (new URL(r.url).pathname.indexOf('/player') === 0) return;    // le player gère son propre mode hors ligne
  e.respondWith(fetch(r).catch(function () { return caches.match(OFFLINE); }));
});
