/* Installation de Linkii sur l'écran d'accueil (Android, iPhone, iPad).
   - Android / Chrome / Edge : l'événement beforeinstallprompt ouvre la boîte d'installation du système.
   - iPhone / iPad : Safari n'a pas d'API d'installation ; on montre le geste (Partager › Sur l'écran d'accueil).
   Un bandeau propose l'installation (masquable 14 jours). Tout élément portant data-install la déclenche aussi
   et n'est visible que si l'installation est possible (classe can-install sur <html>). JS volontairement simple (ES5). */
(function () {
  'use strict';
  var KEY = 'lk_install_dismissed', DAYS = 14;
  var deferred = null;

  if ('serviceWorker' in navigator) {
    window.addEventListener('load', function () { navigator.serviceWorker.register('/sw.js').catch(function () {}); });
  }

  function installed() {
    return (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches) || window.navigator.standalone === true;
  }
  function isIos() {
    var ua = navigator.userAgent || '';
    return /iPad|iPhone|iPod/.test(ua) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);   // iPadOS se présente comme un Mac
  }
  function dismissed() {
    try { return Date.now() - (parseInt(localStorage.getItem(KEY), 10) || 0) < DAYS * 86400000; } catch (e) { return false; }
  }
  function remember() { try { localStorage.setItem(KEY, String(Date.now())); } catch (e) {} }
  function canInstall() { return !installed() && (!!deferred || isIos()); }

  function appName() {
    var m = document.querySelector('meta[name="apple-mobile-web-app-title"]');
    return (m && m.content) || 'Linkii';
  }
  function esc(s) { var d = document.createElement('div'); d.textContent = s; return d.innerHTML; }

  var SHARE = '<svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true" style="vertical-align:-3px;fill:none;stroke:currentColor;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round"><path d="M12 15V3M8 7l4-4 4 4M5 12v7a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-7"/></svg>';

  function hide() { var b = document.getElementById('pwa-install'); if (b) b.hidden = true; }

  function show(force) {
    var bar = document.getElementById('pwa-install');
    if (!bar || !canInstall() || (!force && dismissed())) return;
    var name = esc(appName());
    if (deferred) {
      bar.innerHTML = '<div class="pwa-text"><b>Installer ' + name + '</b><span>Ajoutez l\'application à votre écran d\'accueil pour l\'ouvrir en un geste.</span></div>' +
        '<div class="pwa-actions"><button type="button" class="btn sm" data-pwa="go">Installer</button><button type="button" class="btn ghost sm" data-pwa="later">Plus tard</button></div>';
    } else {
      bar.innerHTML = '<div class="pwa-text"><b>Installer ' + name + ' sur votre écran d\'accueil</b><span>Touchez ' + SHARE + ' (Partager) puis « Sur l\'écran d\'accueil ».</span></div>' +
        '<div class="pwa-actions"><button type="button" class="btn ghost sm" data-pwa="later">Compris</button></div>';
    }
    bar.hidden = false;
  }

  function refresh() { document.documentElement.classList.toggle('can-install', canInstall()); }

  function install() {
    if (deferred) {
      var p = deferred; deferred = null;
      hide(); refresh();
      p.prompt();
      if (p.userChoice) p.userChoice.then(function (c) { if (c && c.outcome !== 'accepted') remember(); });
    } else if (isIos()) {
      show(true);
    }
  }

  window.addEventListener('beforeinstallprompt', function (e) { e.preventDefault(); deferred = e; refresh(); show(false); });
  window.addEventListener('appinstalled', function () { deferred = null; hide(); refresh(); });

  document.addEventListener('click', function (e) {
    var t = e.target && e.target.closest ? e.target.closest('[data-pwa],[data-install]') : null;
    if (!t) return;
    if (t.hasAttribute('data-install')) { install(); return; }
    var a = t.getAttribute('data-pwa');
    if (a === 'go') install();
    else if (a === 'later') { remember(); hide(); }
  });

  function start() { refresh(); if (isIos()) show(false); }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})();
