/* Premiers pas avec l'assistant (DEV-POC §232) : la zone de saisie de l'accueil (exemples qui s'écrivent, Entrée pour lancer),
   l'aperçu en direct (le player en mode aperçu dans un cadre, relié au fil de la liste : page en cours, clic pour y aller) et les confettis de la fin. */
(function (w) {
  'use strict';

  var EX = ['www.votre-site.ch', 'Un cabinet qui veut accueillir ses visiteurs…', "Une salle d'attente : infos pratiques, météo, actualités…",
    'Une boutique qui veut montrer ses nouveautés…', "Informer l'équipe : annonces, agenda, bienvenue aux nouveaux…"];

  function enter(ta, form, max) {
    function size() { ta.style.height = 'auto'; ta.style.height = Math.min(max, ta.scrollHeight) + 'px'; }
    ta.addEventListener('input', size);
    ta.addEventListener('keydown', function (e) {
      if (e.key !== 'Enter' || e.shiftKey || e.isComposing) return;
      e.preventDefault();
      if (form && form.requestSubmit) setTimeout(function () { form.requestSubmit(); }, 0);   // laisse Blazor recevoir la dernière saisie
    });
    size();
  }

  /** Accueil : des exemples s'écrivent puis s'effacent dans la zone tant qu'elle est vide et sans le curseur. */
  function hero(ta, form) {
    if (!ta || ta.__pa) return;
    ta.__pa = true;
    enter(ta, form, 150);
    var i = 0, c = 0, del = false, pause = 0;
    var t = setInterval(function () {
      if (!document.body.contains(ta)) { clearInterval(t); return; }
      if (ta.value || document.activeElement === ta || Date.now() < pause) return;
      var s = EX[i];
      if (!del) { c++; if (c >= s.length) { del = true; pause = Date.now() + 1600; } }
      else { c -= 3; if (c <= 0) { c = 0; del = false; i = (i + 1) % EX.length; pause = Date.now() + 260; } }
      ta.placeholder = s.slice(0, c);
    }, 55);
    ta.addEventListener('focus', function () { if (!ta.value) ta.placeholder = 'Adresse de votre site, ou décrivez votre activité…'; });
    ta.addEventListener('blur', function () { if (!ta.value) { c = 0; del = false; } });
  }

  function compose(ta, form) { if (ta && !ta.__pa) { ta.__pa = true; enter(ta, form, 110); } }

  /* ---------- Aperçu en direct : le player en mode aperçu (même rendu que l'écran), relié au fil ---------- */
  var tv = null;

  function Tv(host, film) {
    var self = this;
    this.host = host; this.film = film; this.idx = -1; this.go = -1; this.frame = null; this.old = null;
    host.innerHTML = '';
    this.onMsg = function (e) {
      if (!self.frame || e.source !== self.frame.contentWindow || e.origin !== location.origin || !e.data || e.data.lk !== 'sim' || e.data.ev !== 'state') return;
      self.state(e.data);
    };
    this.onClick = function (e) {
      var c = e.target && e.target.closest ? e.target.closest('[data-k]') : null;
      if (!c || !self.film || !self.film.contains(c)) return;
      self.send({ cmd: 'goto', index: +c.getAttribute('data-k') });
    };
    w.addEventListener('message', this.onMsg);
    if (film) film.addEventListener('click', this.onClick);
  }
  /** Nouveau contenu : un nouveau cadre se charge par-dessus l'ancien, invisible ; il apparaît en fondu dès qu'il montre la bonne page (pas d'écran noir). */
  Tv.prototype.load = function (src, at) {
    var self = this;
    this.go = at > 0 ? at : -1; this.idx = -1;
    if (this.old && this.old.parentNode) this.old.parentNode.removeChild(this.old);
    this.old = this.frame;
    var f = document.createElement('iframe');
    f.title = 'Aperçu en direct de la liste'; f.setAttribute('allow', 'autoplay');
    if (this.old) f.style.opacity = '0';
    this.host.appendChild(f);
    this.frame = f;
    f.setAttribute('src', src);
    clearTimeout(this.fade);
    this.fade = setTimeout(function () { self.reveal(); }, 5000);   // filet : le cadre apparaît même sans nouvelles du player
  };
  Tv.prototype.reveal = function () {
    clearTimeout(this.fade);
    if (!this.frame) return;
    this.frame.style.opacity = '1';
    var o = this.old; this.old = null;
    if (o) setTimeout(function () { if (o.parentNode) o.parentNode.removeChild(o); }, 450);
  };
  Tv.prototype.send = function (m) { m.lk = 'sim'; try { this.frame.contentWindow.postMessage(m, location.origin); } catch (e) { } };
  Tv.prototype.state = function (s) {
    if (this.go >= 0 && s.items && s.items.length > this.go) { var g = this.go; this.go = -1; this.send({ cmd: 'goto', index: g }); return; }
    if (this.old) this.reveal();
    if (!this.film || s.index === this.idx) return;
    this.idx = s.index;
    var cards = this.film.querySelectorAll('[data-k]');
    for (var i = 0; i < cards.length; i++) {
      var card = cards[i];
      if (+card.getAttribute('data-k') !== s.index) { card.removeAttribute('data-cur'); continue; }
      card.removeAttribute('data-cur'); void card.offsetWidth;   // relance la barre de progression
      card.style.setProperty('--left', Math.max(500, s.left || s.dur || 10000) + 'ms');
      card.setAttribute('data-cur', '1');
      var f = this.film, l = card.offsetLeft;
      if (l < f.scrollLeft || l + card.offsetWidth > f.scrollLeft + f.clientWidth) f.scrollTo({ left: Math.max(0, l - 40), behavior: 'smooth' });
    }
  };
  Tv.prototype.dispose = function () {
    w.removeEventListener('message', this.onMsg);
    if (this.film) this.film.removeEventListener('click', this.onClick);
    clearTimeout(this.fade);
    [this.frame, this.old].forEach(function (f) { try { if (f) f.setAttribute('src', 'about:blank'); } catch (e) { } });
  };

  /* ---------- Confettis de la fin ---------- */
  function confetti(host) {
    if (!host || (w.matchMedia && w.matchMedia('(prefers-reduced-motion: reduce)').matches)) return;
    var cols = ['#3B82C4', '#0B1F3A', '#E2733D', '#A9CFF2', '#1E8E5A', '#F2B84B'];
    for (var i = 0; i < 90; i++) {
      var p = document.createElement('i');
      p.className = 'pa-conf';
      p.style.left = (22 + Math.random() * 14) + '%';
      p.style.background = cols[i % cols.length];
      p.style.setProperty('--x', ((Math.random() - 0.5) * 900) + 'px');
      p.style.setProperty('--y', (-140 - Math.random() * 240) + 'px');
      p.style.setProperty('--r', (Math.random() * 720 - 360) + 'deg');
      p.style.animationDelay = (Math.random() * 0.2) + 's';
      host.appendChild(p);
      (function (q) { setTimeout(function () { if (q.parentNode) q.parentNode.removeChild(q); }, 2900); })(p);
    }
  }

  w.linkiiStart = {
    hero: hero,
    compose: compose,
    /** Monte (ou recharge) le player en aperçu dans host ; film : le fil de la liste (page en cours, clic) ; at : page à montrer d'abord. */
    tv: function (host, film, src, at) {
      if (!host) return;
      if (!tv || tv.host !== host || !document.body.contains(tv.host)) { if (tv) tv.dispose(); tv = new Tv(host, film || null); }
      else if (film && tv.film !== film) { if (tv.film) tv.film.removeEventListener('click', tv.onClick); tv.film = film; film.addEventListener('click', tv.onClick); }
      tv.load(src, at || 0);
    },
    stop: function () { if (tv) { tv.dispose(); tv = null; } },
    end: function (el) { if (el) el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' }); },
    confetti: confetti
  };
})(window);
