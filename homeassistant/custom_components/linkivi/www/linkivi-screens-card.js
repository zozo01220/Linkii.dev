/*
 * Carte Home Assistant « Écrans Linkivi » (linkivi-screens-card).
 * Trouve seule les écrans de l'intégration Linkivi (entités de la plateforme « linkivi », groupées par appareil) et les pilote :
 * interrupteur Écran, redémarrage du player, redémarrage et extinction de l'appareil. Aucun nom d'entité à écrire.
 * Deux styles : « cards » (chaque écran est une petite télé dont l'état se lit sur l'écran) et « list » (lignes givrées sur un fond dégradé).
 * Servie par l'intégration (/linkivi/linkivi-screens-card.js) : chargée toute seule, ou déclarée en ressource « module JavaScript ».
 * Sans dépendance : élément natif + shadow DOM.
 */
const CARD_VERSION = "1.2.0";

const TEXTS = {
  fr: {
    title: "Écrans Linkivi",
    summary: (on, off) => `${on} en ligne${off ? ` · ${off} hors ligne` : ""}`,
    none: "Aucun écran Linkivi trouvé. Ajoutez l'intégration Linkivi (Paramètres › Appareils et services).",
    allOn: "Tout allumer", allOff: "Tout éteindre",
    screenOn: "Écran allumé", screenOff: "Écran éteint", sleep: "En veille", turnOn: "Allumer l'écran", turnOff: "Éteindre l'écran",
    asleep: "en veille", offline: "Hors ligne", offlineSince: (d) => `Hors ligne depuis ${d}`, since: (d) => `depuis ${d}`,
    restartPlayer: "Redémarrer le player", restartDevice: "Redémarrer l'appareil", powerOffDevice: "Éteindre l'appareil…",
    pendingPlayer: "Redémarrage du player…", pendingReboot: "Redémarrage de l'appareil…", pendingPoweroff: "Extinction de l'appareil…",
    confirmReboot: (n) => `Redémarrer « ${n} » ?`, confirmRebootText: "L'appareil sera hors ligne quelques minutes, puis Linkivi reprend l'affichage.", rebootGo: "Redémarrer",
    confirmOff: (n) => `Éteindre « ${n} » ?`, confirmOffText: "Linkivi ne pourra pas le rallumer : il faudra appuyer sur son bouton ou remettre le courant.", offGo: "Éteindre",
    cancel: "Annuler", more: "Commandes de l'appareil", failed: (m) => `Linkivi : ${m}`,
    minutes: (n) => `${n} min`, hours: (n) => `${n} h`, days: (n) => `${n} j`,
    ed: { title: "Titre", style: "Style", screens: "Écrans (vide : tous)", show_player_restart: "Redémarrer le player", show_device_actions: "Commandes de l'appareil (⋮)",
      show_group_buttons: "Tout allumer / Tout éteindre", hide_offline: "Masquer les écrans hors ligne", accent_color: "Couleur d'accent (ex. #2a86ee, facultatif)",
      list: "Liste (verre)", cards: "Cartes (télés)" },
  },
  en: {
    title: "Linkivi screens",
    summary: (on, off) => `${on} online${off ? ` · ${off} offline` : ""}`,
    none: "No Linkivi screen found. Add the Linkivi integration (Settings › Devices & services).",
    allOn: "All on", allOff: "All off",
    screenOn: "Screen on", screenOff: "Screen off", sleep: "Asleep", turnOn: "Turn the screen on", turnOff: "Turn the screen off",
    asleep: "asleep", offline: "Offline", offlineSince: (d) => `Offline for ${d}`, since: (d) => `for ${d}`,
    restartPlayer: "Restart player", restartDevice: "Restart device", powerOffDevice: "Power off device…",
    pendingPlayer: "Restarting the player…", pendingReboot: "Restarting the device…", pendingPoweroff: "Powering off…",
    confirmReboot: (n) => `Restart “${n}”?`, confirmRebootText: "The device will be offline for a few minutes, then Linkivi resumes the display.", rebootGo: "Restart",
    confirmOff: (n) => `Power off “${n}”?`, confirmOffText: "Linkivi cannot turn it back on: someone must press its button or restore power.", offGo: "Power off",
    cancel: "Cancel", more: "Device commands", failed: (m) => `Linkivi: ${m}`,
    minutes: (n) => `${n} min`, hours: (n) => `${n} h`, days: (n) => `${n} d`,
    ed: { title: "Title", style: "Style", screens: "Screens (empty: all)", show_player_restart: "Restart player", show_device_actions: "Device commands (⋮)",
      show_group_buttons: "All on / All off", hide_offline: "Hide offline screens", accent_color: "Accent colour (e.g. #2a86ee, optional)",
      list: "List (glass)", cards: "Cards (screens)" },
  },
};

const ICON = {
  tv: '<svg viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/></svg>',
  tablet: '<svg viewBox="0 0 24 24"><rect x="6" y="2.5" width="12" height="19" rx="2.5"/><path d="M11 18.5h2"/></svg>',
  pi: '<svg viewBox="0 0 24 24"><rect x="3" y="6" width="18" height="12" rx="2"/><path d="M7 10v4M11 10v4M15 11h2M15 14h2"/></svg>',
  monitor: '<svg viewBox="0 0 24 24"><rect x="2.5" y="4" width="19" height="12.5" rx="1.5"/><path d="M8 20.5h8M12 16.5v4"/></svg>',
  globe: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c2.7 2.6 2.7 15.4 0 18M12 3c-2.7 2.6-2.7 15.4 0 18"/></svg>',
  moon: '<svg viewBox="0 0 24 24"><path d="M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z"/></svg>',
  cloud: '<svg viewBox="0 0 24 24"><path d="M3 3l18 18"/><path d="M17.5 17.5H7a4 4 0 0 1-.6-7.95M9 5.5a6 6 0 0 1 10 4.5 3.5 3.5 0 0 1 2 3"/></svg>',
  restart: '<svg viewBox="0 0 24 24"><path d="M20 12a8 8 0 1 1-2.6-5.9"/><path d="M20 4v5h-5"/></svg>',
  restartAlert: '<svg viewBox="0 0 24 24"><path d="M20 12a8 8 0 1 1-2.6-5.9"/><path d="M20 4v5h-5"/><path d="M12 8.5v4M12 15.5v.01"/></svg>',
  power: '<svg viewBox="0 0 24 24"><path d="M12 3v8"/><path d="M6.3 6.8a8 8 0 1 0 11.4 0"/></svg>',
  dots: '<svg viewBox="0 0 24 24" class="fill"><circle cx="12" cy="5" r="1.6"/><circle cx="12" cy="12" r="1.6"/><circle cx="12" cy="19" r="1.6"/></svg>',
};

const ROLE_BY_KEY = { online: "online", screen: "sw", restart_player: "player", reboot: "reboot", power_off: "poweroff" };
const PENDING_MS = { player: 20000, reboot: 120000, poweroff: 60000 };

const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

/** L'icône d'un écran d'après le modèle que l'intégration donne à l'appareil (« Player Android TV », « Player Raspberry Pi »…). */
const deviceIcon = (model) => /tv/i.test(model) ? ICON.tv : /android/i.test(model) ? ICON.tablet : /raspberry/i.test(model) ? ICON.pi
  : /windows/i.test(model) ? ICON.monitor : /navigateur|web|browser/i.test(model) ? ICON.globe : ICON.tv;

const CSS = `
:host{display:block;--lk:#2a86ee;--lk-ok:var(--success-color,#43a047);--lk-warn:var(--warning-color,#ffa726);--lk-off:var(--disabled-color,#9e9e9e);
  --lk-chip:var(--secondary-background-color,rgba(127,127,127,.15));--lk-text:var(--primary-text-color);--lk-sec:var(--secondary-text-color);
  --lk-div:var(--divider-color,rgba(127,127,127,.3));--lk-danger:var(--error-color,#d32f2f);--lk-card:var(--ha-card-background,var(--card-background-color,#fff))}
ha-card{display:block;overflow:visible;position:relative}
svg{width:18px;height:18px;fill:none;stroke:currentColor;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round;pointer-events:none}
svg.fill{fill:currentColor;stroke:none}
button{font:inherit;color:inherit;cursor:pointer}
.hd{display:flex;align-items:center;gap:14px;padding:16px 18px 10px;flex-wrap:wrap}
.hd .t{font-size:19px;font-weight:600;letter-spacing:-.01em;line-height:1.15;color:var(--lk-text)}
.hd .s{font-size:12.5px;color:var(--lk-sec)}
.hd .sp{flex:1}
.ring{width:46px;height:46px;border-radius:50%;background:conic-gradient(var(--lk-ok) calc(var(--v,0)*1%),var(--lk-chip) 0);display:grid;place-items:center;flex:none}
.ring::before{content:"";width:34px;height:34px;border-radius:50%;background:var(--lk-card);grid-area:1/1}
.ring b{grid-area:1/1;font-size:13px;font-weight:600;z-index:1;color:var(--lk-text)}
.chip{border:0;background:var(--lk-chip);color:var(--lk-text);border-radius:16px;padding:6px 12px;font-size:12.5px}
.chip:hover{filter:brightness(.95)}
.ib{width:34px;height:34px;border:0;border-radius:50%;background:color-mix(in srgb,var(--lk-text) 8%,transparent);color:var(--lk-text);display:inline-flex;align-items:center;justify-content:center;padding:0;flex:none}
.ib:hover:not(:disabled){background:color-mix(in srgb,var(--lk-text) 14%,transparent)}
.ib:disabled{opacity:.4;cursor:default}
.empty{padding:16px 18px 18px;color:var(--lk-sec)}
.n{font-weight:600;line-height:1.2;color:var(--lk-text);min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.d{font-size:12px;color:var(--lk-sec);line-height:1.3;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.tx{min-width:0}
.sp{flex:1}
.spin{width:12px;height:12px;border:2px solid var(--lk-div);border-top-color:var(--lk-warn);border-radius:50%;display:inline-block;vertical-align:-2px;animation:r 1s linear infinite}
@keyframes r{to{transform:rotate(360deg)}}
@keyframes p{70%{box-shadow:0 0 0 7px rgba(125,255,180,0)}100%{box-shadow:0 0 0 0 rgba(125,255,180,0)}}
.pop{position:absolute;right:10px;z-index:5;min-width:220px;background:var(--card-background-color,#fff);border:1px solid var(--lk-div);border-radius:10px;box-shadow:0 6px 22px rgba(0,0,0,.3);padding:4px}
.pop button{display:flex;width:100%;gap:10px;align-items:center;border:0;background:none;border-radius:6px;padding:9px 12px;font-size:13.5px;text-align:left}
.pop button:hover{background:var(--lk-chip)}
.pop .dg{color:var(--lk-danger)}
.pop hr{border:0;border-top:1px solid var(--lk-div);margin:4px 0}

/* ── style « list » : lignes givrées sur un fond dégradé ── */
ha-card.glass{background:radial-gradient(120% 90% at 0% 0%,color-mix(in srgb,var(--lk) 34%,transparent),transparent 60%),radial-gradient(90% 80% at 100% 100%,color-mix(in srgb,var(--lk-ok) 22%,transparent),transparent 60%),var(--lk-card)}
.li{display:flex;flex-direction:column;gap:8px;padding:4px 14px 16px}
.r{display:flex;align-items:center;gap:12px;padding:10px 12px;border-radius:14px;background:color-mix(in srgb,var(--lk-text) 6%,transparent);border:1px solid color-mix(in srgb,var(--lk-text) 10%,transparent);backdrop-filter:blur(8px);position:relative}
.r.out .n{color:var(--lk-sec)}
.av{width:40px;height:40px;border-radius:12px;display:grid;place-items:center;flex:none;background:color-mix(in srgb,var(--lk-ok) 20%,transparent);color:var(--lk-ok);box-shadow:inset 0 0 0 1.5px color-mix(in srgb,var(--lk-ok) 55%,transparent)}
.av.sleep{background:color-mix(in srgb,var(--lk) 16%,transparent);color:var(--lk);box-shadow:inset 0 0 0 1.5px color-mix(in srgb,var(--lk) 45%,transparent)}
.av.busy{background:color-mix(in srgb,var(--lk-warn) 20%,transparent);color:var(--lk-warn);box-shadow:inset 0 0 0 1.5px color-mix(in srgb,var(--lk-warn) 55%,transparent)}
.av.off{background:var(--lk-chip);color:var(--lk-off);box-shadow:inset 0 0 0 1.5px var(--lk-div)}
.pill{width:46px;height:26px;border-radius:13px;border:0;padding:0;background:var(--lk);position:relative;flex:none;box-shadow:0 0 14px color-mix(in srgb,var(--lk) 55%,transparent)}
.pill::after{content:"";position:absolute;top:3px;left:23px;width:20px;height:20px;border-radius:50%;background:#fff;transition:left .15s}
.pill.off{background:var(--lk-off);box-shadow:none}.pill.off::after{left:3px}
.pill:disabled{opacity:.45;cursor:default}
.r .pop{top:56px}
.r.open,.it.open{z-index:6}   /* la ligne dont le menu est ouvert passe au-dessus des suivantes (chacune a son propre empilement) */

/* ── style « cards » : chaque écran est une petite télé ── */
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(190px,1fr));gap:16px 14px;padding:6px 18px 18px}
.it{position:relative;min-width:0}
.tv{position:relative;aspect-ratio:16/9;border-radius:9px;background:#05070a;border:3px solid #0a0c10;box-shadow:0 0 0 1px rgba(255,255,255,.08),0 6px 14px rgba(0,0,0,.3);overflow:hidden;color:#fff}
.tv.on{background:linear-gradient(135deg,var(--lk) 0%,color-mix(in srgb,var(--lk) 55%,#0b1f3a) 55%,#0b1f3a 100%);box-shadow:0 0 0 1px rgba(255,255,255,.1),0 8px 22px color-mix(in srgb,var(--lk) 45%,transparent)}
.tv.on::after{content:"";position:absolute;inset:0;background:linear-gradient(115deg,rgba(255,255,255,.22) 0,rgba(255,255,255,0) 38%)}
.tv.sleep{background:#06080b}
.tv.off{background:repeating-linear-gradient(135deg,#20262c 0 8px,#1a1f24 8px 16px)}
.tv .in{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:3px;z-index:1;text-align:center;padding:6px}
.tv .in b{font-size:11.5px;font-weight:600;letter-spacing:.02em}
.tv .in span{font-size:10px;opacity:.8}
.tv .in svg{width:22px;height:22px;opacity:.9}
.tv .live{position:absolute;top:7px;right:8px;width:7px;height:7px;border-radius:50%;background:#7dffb4;box-shadow:0 0 0 0 rgba(125,255,180,.7);animation:p 2s infinite;z-index:2}
.tv .big{width:20px;height:20px;border:2px solid rgba(255,255,255,.3);border-top-color:#ffb34d;border-radius:50%;animation:r 1s linear infinite}
.stand{height:5px;width:34%;margin:0 auto;background:#0a0c10;border-radius:0 0 4px 4px}
.cap{display:flex;align-items:center;gap:6px;margin-top:8px}
.cap .n{white-space:normal;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical}
.cap .ib{width:30px;height:30px}.cap .ib svg{width:16px;height:16px}
.cap .pw.on{background:var(--lk);color:#fff}
.it .pop{top:calc(100% - 2px)}

/* ── confirmation ── */
.ov{position:fixed;inset:0;z-index:9999;background:rgba(0,0,0,.5);display:flex;align-items:center;justify-content:center;padding:16px}
.dlg{background:var(--card-background-color,#fff);color:var(--lk-text);border-radius:14px;box-shadow:0 8px 32px rgba(0,0,0,.4);width:340px;max-width:100%;padding:20px}
.dlg .t{font-size:17px;font-weight:500;margin-bottom:8px}
.dlg p{margin:0 0 16px;color:var(--lk-sec);font-size:14px}
.dlg .f{display:flex;justify-content:flex-end;gap:8px}
.dlg .f button{border:0;border-radius:18px;padding:8px 14px;font-size:14px;background:var(--lk-chip)}
.dlg .f .go{background:var(--lk);color:#fff}.dlg .f .go.dg{background:var(--lk-danger)}
@media (prefers-reduced-motion:reduce){*{animation:none!important;transition:none!important}}
`;

class LinkivScreensCard extends HTMLElement {
  constructor() {
    super();
    this.attachShadow({ mode: "open" });
    this._config = {};
    this._pending = {};     // appareil -> { label, until }
    this._optimistic = {};  // appareil -> { on, until }
    this._menu = null;      // appareil dont le menu ⋮ est ouvert
    this._dialog = null;    // fenêtre de confirmation
    this._sig = "";
    this._timer = null;
    // un clic hors du menu ⋮ le ferme ; sur le bouton ⋮ lui-même, c'est son propre clic qui le referme
    this._onDocClick = (e) => {
      if (!this._menu) return;
      const path = e.composedPath();
      if (path.includes(this.shadowRoot.querySelector(".pop")) || path.some((el) => el.dataset?.act === "menu")) return;
      this._menu = null;
      this._render(true);
    };
  }

  static getConfigElement() { return document.createElement("linkivi-screens-card-editor"); }
  static getStubConfig() { return { type: "custom:linkivi-screens-card" }; }

  setConfig(config) {
    this._config = {
      style: "list", show_player_restart: true, show_device_actions: true, show_group_buttons: true, hide_offline: false, ...config,
    };
    if (!["list", "cards"].includes(this._config.style)) this._config.style = "list";
    if (this._config.accent_color) this.style.setProperty("--lk", String(this._config.accent_color));
    else this.style.removeProperty("--lk");
    this._sig = "";
    this._render(true);
  }

  set hass(hass) {
    this._hass = hass;
    this._render();
  }

  connectedCallback() { document.addEventListener("click", this._onDocClick, true); }
  disconnectedCallback() { document.removeEventListener("click", this._onDocClick, true); clearTimeout(this._timer); }

  getCardSize() { return this._config.style === "cards" ? 2 + 2 * Math.ceil(((this._items || []).length || 1) / 3) : 1 + Math.max(1, (this._items || []).length); }
  getGridOptions() { return { columns: 12, min_columns: 6 }; }

  get _t() { return TEXTS[(this._hass?.language || "en").startsWith("fr") ? "fr" : "en"]; }

  /* ── les écrans : les entités de la plateforme linkivi, groupées par appareil ── */
  _discover() {
    const hass = this._hass;
    const byDevice = new Map();
    for (const [eid, reg] of Object.entries(hass.entities || {})) {
      if (reg.platform !== "linkivi" || !reg.device_id || reg.hidden) continue;
      let role = ROLE_BY_KEY[reg.translation_key];
      if (!role) role = eid.startsWith("switch.") ? "sw" : eid.startsWith("binary_sensor.") ? "online"
        : /redemarrer_le_player$|restart_player$/.test(eid) ? "player" : /redemarrer_l_appareil$|restart_device$/.test(eid) ? "reboot"
        : /eteindre_l_appareil$|power_off/.test(eid) ? "poweroff" : null;
      if (!role) continue;
      const s = byDevice.get(reg.device_id) || { id: reg.device_id };
      s[role] = eid;
      byDevice.set(reg.device_id, s);
    }
    const wanted = (this._config.screens || []).map((x) => String(x).toLowerCase());
    const now = Date.now();
    const items = [];
    for (const s of byDevice.values()) {
      const dev = hass.devices?.[s.id];
      s.name = dev?.name_by_user || dev?.name || s.id;
      if (wanted.length && !wanted.includes(s.name.toLowerCase())) continue;
      const online = hass.states[s.online]?.state === "on";
      const swState = hass.states[s.sw]?.state;
      const opt = this._optimistic[s.id];
      s.swOn = opt && opt.until > now ? opt.on : swState === "on";
      s.swDisabled = !s.sw || swState === undefined || swState === "unavailable";
      s.isOnline = online;
      s.asleep = swState === "off";
      s.model = (dev?.model || "").replace(/^Player /, "");
      s.area = hass.areas?.[dev?.area_id]?.name || "";
      const seen = hass.states[s.online]?.attributes?.last_seen;
      s.offlineFor = !online && seen ? this._since(seen) : "";
      const p = this._pending[s.id];
      s.pending = p && p.until > now ? p.label : "";
      if (this._config.hide_offline && !online) continue;
      items.push(s);
    }
    items.sort((a, b) => a.name.localeCompare(b.name, hass.language || "fr", { sensitivity: "base" }));
    return items;
  }

  _since(iso) {
    const t = this._t;
    const m = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
    if (Number.isNaN(m)) return "";
    return m < 60 ? t.minutes(Math.max(1, m)) : m < 2880 ? t.hours(Math.round(m / 60)) : t.days(Math.round(m / 1440));
  }

  /** « allumé » (online), « sleep », « off » (hors ligne) : la classe visuelle d'un écran. */
  _look(s) { return !s.isOnline ? "off" : s.swOn ? "on" : "sleep"; }

  _subline(s) {
    const t = this._t;
    if (s.pending) return `<span class="spin"></span> ${esc(s.pending)}`;
    if (!s.isOnline) return esc(s.offlineFor ? t.offlineSince(s.offlineFor) : t.offline);
    return esc([s.model, s.area, s.asleep ? t.asleep : ""].filter(Boolean).join(" · "));
  }

  /* ── affichage ── */
  _render(force) {
    if (!this._hass || !this._config) return;
    const items = this._discover();
    this._items = items;
    const sig = JSON.stringify([items.map((s) => [s.id, s.name, s.isOnline, s.swOn, s.asleep, s.swDisabled, s.pending, s.offlineFor, s.model, s.area, !!s.player, !!s.reboot, !!s.poweroff]),
      this._config, this._menu, !!this._dialog, this._hass.language]);
    if (!force && sig === this._sig) return;
    this._sig = sig;
    this._schedule();

    const t = this._t, c = this._config;
    const online = items.filter((s) => s.isOnline).length;
    const cards = c.style === "cards";
    const body = !items.length ? `<div class="empty">${esc(t.none)}</div>` : cards ? this._cards(items) : this._list(items);
    const group = c.show_group_buttons && items.some((s) => !s.swDisabled)
      ? `<button class="chip" data-act="all-on">${esc(t.allOn)}</button><button class="chip" data-act="all-off">${esc(t.allOff)}</button>` : "";
    this.shadowRoot.innerHTML = `<style>${CSS}</style><ha-card class="${cards ? "" : "glass"}">
      <div class="hd"><div class="ring" style="--v:${items.length ? Math.round((online / items.length) * 100) : 0}"><b>${online}</b></div>
        <div><div class="t">${esc(c.title || t.title)}</div><div class="s">${esc(t.summary(online, items.length - online))}</div></div><span class="sp"></span>${group}</div>
      ${body}${this._dialogHtml()}</ha-card>`;
    this._bind();
  }

  _schedule() {
    clearTimeout(this._timer);
    const now = Date.now();
    const next = [...Object.values(this._pending), ...Object.values(this._optimistic)].map((p) => p.until).filter((u) => u > now);
    if (next.length) this._timer = setTimeout(() => { this._sig = ""; this._render(); }, Math.min(...next) - now + 50);
  }

  /** Le menu ⋮ (commandes de l'appareil), commun aux deux styles. */
  _pop(s) {
    if (this._menu !== s.id) return "";
    const t = this._t;
    return `<div class="pop">
      ${s.reboot ? `<button data-act="reboot" data-id="${esc(s.id)}">${ICON.restartAlert}${esc(t.restartDevice)}</button>` : ""}
      ${s.reboot && s.poweroff ? "<hr>" : ""}
      ${s.poweroff ? `<button class="dg" data-act="poweroff" data-id="${esc(s.id)}">${ICON.power}${esc(t.powerOffDevice)}</button>` : ""}</div>`;
  }

  _buttons(s) {
    const t = this._t, c = this._config, id = esc(s.id);
    const restart = c.show_player_restart && s.player
      ? `<button class="ib" data-act="player" data-id="${id}" ${s.isOnline && !s.pending ? "" : "disabled"} title="${esc(t.restartPlayer)}" aria-label="${esc(t.restartPlayer)}">${ICON.restart}</button>` : "";
    const more = c.show_device_actions && (s.reboot || s.poweroff)
      ? `<button class="ib" data-act="menu" data-id="${id}" title="${esc(t.more)}" aria-label="${esc(t.more)}">${ICON.dots}</button>` : "";
    return { restart, more };
  }

  /** Style « list » : une ligne givrée par écran ; le médaillon donne l'état par sa couleur. */
  _list(items) {
    const t = this._t;
    return `<div class="li">${items.map((s) => {
      const look = this._look(s), id = esc(s.id), b = this._buttons(s);
      const state = s.pending ? "busy" : look === "on" ? "" : look;
      const icon = s.pending ? ICON.restart : look === "off" ? ICON.cloud : look === "sleep" ? ICON.moon : deviceIcon(s.model);
      return `<div class="r ${look === "off" ? "out" : ""} ${this._menu === s.id ? "open" : ""}">
        <div class="av ${state}">${icon}</div>
        <div class="tx"><div class="n">${esc(s.name)}</div><div class="d">${this._subline(s)}</div></div><span class="sp"></span>
        ${b.restart}${b.more}
        <button class="pill ${s.swOn ? "" : "off"}" data-act="toggle" data-id="${id}" ${s.swDisabled ? "disabled" : ""} title="${esc(s.swOn ? t.turnOff : t.turnOn)}" aria-label="${esc(s.swOn ? t.screenOn : t.screenOff)}"></button>
        ${this._pop(s)}</div>`;
    }).join("")}</div>`;
  }

  /** Style « cards » : chaque écran est une petite télé dont l'état se lit sur l'écran, avec ses commandes dessous. */
  _cards(items) {
    const t = this._t;
    return `<div class="grid">${items.map((s) => {
      const look = this._look(s), id = esc(s.id), b = this._buttons(s);
      let inner;
      if (s.pending) inner = `<div class="big"></div><b>${esc(s.pending)}</b>`;
      else if (look === "off") inner = `${ICON.cloud}<b>${esc(t.offline)}</b>${s.offlineFor ? `<span>${esc(t.since(s.offlineFor))}</span>` : ""}`;
      else if (look === "sleep") inner = `${ICON.moon}<b>${esc(t.sleep)}</b>`;
      else inner = `${deviceIcon(s.model)}<b>${esc(t.screenOn)}</b>`;
      const sub = esc([s.model, s.area].filter(Boolean).join(" · "));
      return `<div class="it ${this._menu === s.id ? "open" : ""}"><div class="tv ${s.pending ? "on" : look}">${look === "on" && !s.pending ? '<i class="live"></i>' : ""}<div class="in">${inner}</div></div><div class="stand"></div>
        <div class="cap"><div class="tx"><div class="n">${esc(s.name)}</div><div class="d">${sub}</div></div><span class="sp"></span>
          <button class="ib pw ${s.swOn ? "on" : ""}" data-act="toggle" data-id="${id}" ${s.swDisabled ? "disabled" : ""} title="${esc(s.swOn ? t.turnOff : t.turnOn)}" aria-label="${esc(s.swOn ? t.screenOn : t.screenOff)}">${ICON.power}</button>
          ${b.restart}${b.more}</div>
        ${this._pop(s)}</div>`;
    }).join("")}</div>`;
  }

  _dialogHtml() {
    const d = this._dialog;
    if (!d) return "";
    const t = this._t;
    return `<div class="ov" data-act="dlg-bg"><div class="dlg" role="dialog" aria-modal="true"><div class="t">${esc(d.title)}</div><p>${esc(d.text)}</p>
      <div class="f"><button data-act="dlg-cancel">${esc(t.cancel)}</button><button class="go ${d.danger ? "dg" : ""}" data-act="dlg-ok">${esc(d.go)}</button></div></div></div>`;
  }

  _bind() {
    this.shadowRoot.querySelectorAll("[data-act]").forEach((el) => {
      el.addEventListener("click", (e) => {
        if (el.dataset.act === "dlg-bg" && e.target !== el) return;
        e.stopPropagation();
        this._act(el.dataset.act, el.dataset.id);
      });
    });
  }

  _item(id) { return (this._items || []).find((s) => s.id === id); }

  _act(act, id) {
    const t = this._t, s = id ? this._item(id) : null;
    switch (act) {
      case "toggle": return s && this._toggle(s, !s.swOn);
      case "all-on": return this._items.filter((x) => !x.swDisabled).forEach((x) => this._toggle(x, true));
      case "all-off": return this._items.filter((x) => !x.swDisabled).forEach((x) => this._toggle(x, false));
      case "player": return s && this._press(s, "player", t.pendingPlayer);
      case "menu": this._menu = this._menu === id ? null : id; return this._render(true);
      case "reboot":
        this._menu = null;
        return s && this._confirm({ title: t.confirmReboot(s.name), text: t.confirmRebootText, go: t.rebootGo, danger: false, run: () => this._press(s, "reboot", t.pendingReboot) });
      case "poweroff":
        this._menu = null;
        return s && this._confirm({ title: t.confirmOff(s.name), text: t.confirmOffText, go: t.offGo, danger: true, run: () => this._press(s, "poweroff", t.pendingPoweroff) });
      case "dlg-ok": { const run = this._dialog?.run; this._dialog = null; this._render(true); return run && run(); }
      case "dlg-cancel": case "dlg-bg": this._dialog = null; return this._render(true);
      default: return undefined;
    }
  }

  _confirm(d) { this._dialog = d; this._render(true); }

  _fail(err) {
    const message = this._t.failed(err?.message || String(err));
    this.dispatchEvent(new CustomEvent("hass-notification", { bubbles: true, composed: true, detail: { message } }));
  }

  async _toggle(s, on) {
    this._optimistic[s.id] = { on, until: Date.now() + 8000 };
    this._render(true);
    try {
      await this._hass.callService("switch", on ? "turn_on" : "turn_off", { entity_id: s.sw });
    } catch (err) {
      delete this._optimistic[s.id];
      this._fail(err);
      this._render(true);
    }
  }

  async _press(s, role, label) {
    this._pending[s.id] = { label, until: Date.now() + PENDING_MS[role] };
    this._render(true);
    try {
      await this._hass.callService("button", "press", { entity_id: s[role] });
    } catch (err) {
      delete this._pending[s.id];
      this._fail(err);   // Linkivi dit pourquoi : écran hors ligne, commande déjà en cours, appareil non compatible…
      this._render(true);
    }
  }
}

/* ── éditeur visuel : l'éditeur de formulaire de Home Assistant (ha-form) ── */
class LinkivScreensCardEditor extends HTMLElement {
  setConfig(config) { this._config = config; this._draw(); }
  set hass(hass) { this._hass = hass; this._draw(); }

  _names() {
    const names = new Set();
    for (const reg of Object.values(this._hass?.entities || {})) {
      if (reg.platform === "linkivi" && reg.device_id) {
        const dev = this._hass.devices?.[reg.device_id];
        const n = dev?.name_by_user || dev?.name;
        if (n) names.add(n);
      }
    }
    return [...names].sort((a, b) => a.localeCompare(b, "fr", { sensitivity: "base" }));
  }

  _draw() {
    if (!this._config || !this._hass) return;
    const lang = (this._hass.language || "en").startsWith("fr") ? "fr" : "en";
    const e = TEXTS[lang].ed;
    if (!this._form) {
      this._form = document.createElement("ha-form");
      this._form.addEventListener("value-changed", (ev) => {
        const config = { ...ev.detail.value };
        if (!config.screens?.length) delete config.screens;
        if (!config.title) delete config.title;
        if (!config.accent_color) delete config.accent_color;
        this.dispatchEvent(new CustomEvent("config-changed", { detail: { config }, bubbles: true, composed: true }));
      });
      this.appendChild(this._form);
    }
    this._form.hass = this._hass;
    this._form.data = { style: "list", show_player_restart: true, show_device_actions: true, show_group_buttons: true, hide_offline: false, ...this._config };
    this._form.computeLabel = (s) => e[s.name] || s.name;
    this._form.schema = [
      { name: "title", selector: { text: {} } },
      { name: "style", selector: { select: { mode: "box", options: [{ value: "list", label: e.list }, { value: "cards", label: e.cards }] } } },
      { name: "screens", selector: { select: { multiple: true, mode: "dropdown", options: this._names() } } },
      { name: "show_player_restart", selector: { boolean: {} } },
      { name: "show_device_actions", selector: { boolean: {} } },
      { name: "show_group_buttons", selector: { boolean: {} } },
      { name: "hide_offline", selector: { boolean: {} } },
      { name: "accent_color", selector: { text: {} } },
    ];
  }
}

if (!customElements.get("linkivi-screens-card")) customElements.define("linkivi-screens-card", LinkivScreensCard);
if (!customElements.get("linkivi-screens-card-editor")) customElements.define("linkivi-screens-card-editor", LinkivScreensCardEditor);

window.customCards = window.customCards || [];
if (!window.customCards.some((c) => c.type === "linkivi-screens-card")) {
  window.customCards.push({
    type: "linkivi-screens-card",
    name: "Écrans Linkivi",
    description: "Voir et commander les écrans Linkivi : écran, player, appareil.",
    preview: false,
  });
}
console.info(`%c LINKIVI-SCREENS-CARD %c ${CARD_VERSION} `, "color:#fff;background:#0B1F3A", "color:#0B1F3A;background:#3B82C4");
