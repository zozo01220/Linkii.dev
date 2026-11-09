/*
 * Carte Home Assistant « Écrans Linkivi » (linkivi-screens-card).
 * Trouve seule les écrans de l'intégration Linkivi (entités de la plateforme « linkivi », groupées par appareil) et les pilote :
 * interrupteur Écran, redémarrage du player, redémarrage et extinction de l'appareil. Aucun nom d'entité à écrire.
 * Chargée par l'intégration (add_extra_js_url) : rien à déclarer dans les ressources. Sans dépendance : élément natif + shadow DOM.
 */
const CARD_VERSION = "1.1.0";

const TEXTS = {
  fr: {
    title: "Écrans Linkivi",
    summary: (on, off) => `${on} en ligne${off ? ` · ${off} hors ligne` : ""}`,
    none: "Aucun écran Linkivi trouvé. Ajoutez l'intégration Linkivi (Paramètres › Appareils et services).",
    allOn: "Tout allumer", allOff: "Tout éteindre",
    screenOn: "Écran allumé", screenOff: "Écran éteint",
    asleep: "en veille", offline: "Hors ligne", offlineSince: (d) => `Hors ligne depuis ${d}`, online: "En ligne",
    player: "Player", device: "Appareil", powerOff: "Éteindre",
    restartPlayer: "Redémarrer le player", restartDevice: "Redémarrer l'appareil", powerOffDevice: "Éteindre l'appareil…",
    pendingPlayer: "Redémarrage du player…", pendingReboot: "Redémarrage de l'appareil…", pendingPoweroff: "Extinction de l'appareil…",
    confirmReboot: (n) => `Redémarrer « ${n} » ?`, confirmRebootText: "L'appareil sera hors ligne quelques minutes, puis Linkivi reprend l'affichage.", rebootGo: "Redémarrer",
    confirmOff: (n) => `Éteindre « ${n} » ?`, confirmOffText: "Linkivi ne pourra pas le rallumer : il faudra appuyer sur son bouton ou remettre le courant.", offGo: "Éteindre",
    cancel: "Annuler", more: "Commandes de l'appareil", failed: (m) => `Linkivi : ${m}`,
    minutes: (n) => `${n} min`, hours: (n) => `${n} h`, days: (n) => `${n} j`,
    ed: { title: "Titre", style: "Style", screens: "Écrans (vide : tous)", show_player_restart: "Redémarrer le player", show_device_actions: "Commandes de l'appareil (⋮)",
      show_group_buttons: "Tout allumer / Tout éteindre", hide_offline: "Masquer les écrans hors ligne", list: "Liste", cards: "Cartes" },
  },
  en: {
    title: "Linkivi screens",
    summary: (on, off) => `${on} online${off ? ` · ${off} offline` : ""}`,
    none: "No Linkivi screen found. Add the Linkivi integration (Settings › Devices & services).",
    allOn: "All on", allOff: "All off",
    screenOn: "Screen on", screenOff: "Screen off",
    asleep: "asleep", offline: "Offline", offlineSince: (d) => `Offline for ${d}`, online: "Online",
    player: "Player", device: "Device", powerOff: "Power off",
    restartPlayer: "Restart player", restartDevice: "Restart device", powerOffDevice: "Power off device…",
    pendingPlayer: "Restarting the player…", pendingReboot: "Restarting the device…", pendingPoweroff: "Powering off…",
    confirmReboot: (n) => `Restart “${n}”?`, confirmRebootText: "The device will be offline for a few minutes, then Linkivi resumes the display.", rebootGo: "Restart",
    confirmOff: (n) => `Power off “${n}”?`, confirmOffText: "Linkivi cannot turn it back on: someone must press its button or restore power.", offGo: "Power off",
    cancel: "Cancel", more: "Device commands", failed: (m) => `Linkivi: ${m}`,
    minutes: (n) => `${n} min`, hours: (n) => `${n} h`, days: (n) => `${n} d`,
    ed: { title: "Title", style: "Style", screens: "Screens (empty: all)", show_player_restart: "Restart player", show_device_actions: "Device commands (⋮)",
      show_group_buttons: "All on / All off", hide_offline: "Hide offline screens", list: "List", cards: "Cards" },
  },
};

const ICON = {
  restart: '<svg viewBox="0 0 24 24"><path d="M20 12a8 8 0 1 1-2.6-5.9"/><path d="M20 4v5h-5"/></svg>',
  restartAlert: '<svg viewBox="0 0 24 24"><path d="M20 12a8 8 0 1 1-2.6-5.9"/><path d="M20 4v5h-5"/><path d="M12 8.5v4M12 15.5v.01"/></svg>',
  power: '<svg viewBox="0 0 24 24"><path d="M12 3v8"/><path d="M6.3 6.8a8 8 0 1 0 11.4 0"/></svg>',
  dots: '<svg viewBox="0 0 24 24" class="fill"><circle cx="12" cy="5" r="1.6"/><circle cx="12" cy="12" r="1.6"/><circle cx="12" cy="19" r="1.6"/></svg>',
};

const ROLE_BY_KEY = { online: "online", screen: "sw", restart_player: "player", reboot: "reboot", power_off: "poweroff" };
const PENDING_MS = { player: 20000, reboot: 120000, poweroff: 60000 };

const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

const CSS = `
:host{display:block}
ha-card{display:block;overflow:visible;position:relative}
svg{width:18px;height:18px;fill:none;stroke:currentColor;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round;pointer-events:none}
svg.fill{fill:currentColor;stroke:none}
button{font:inherit;color:inherit;cursor:pointer}
.hd{display:flex;align-items:center;gap:8px;padding:14px 16px 6px;flex-wrap:wrap}
.hd .t{font-size:var(--ha-card-header-font-size,24px);font-weight:400;line-height:1.2;color:var(--ha-card-header-color,var(--primary-text-color))}
.hd .s{font-size:13px;color:var(--secondary-text-color)}
.hd .sp{flex:1}
.chip{border:0;background:var(--secondary-background-color,rgba(127,127,127,.15));color:var(--primary-text-color);border-radius:16px;padding:5px 11px;font-size:12.5px}
.chip:hover{filter:brightness(.95)}
.ic{width:32px;height:32px;border:0;border-radius:50%;background:var(--secondary-background-color,rgba(127,127,127,.15));display:inline-flex;align-items:center;justify-content:center;padding:0}
.ic:disabled{opacity:.4;cursor:default}
.empty{padding:16px;color:var(--secondary-text-color)}
/* liste */
.row{display:grid;grid-template-columns:12px 1fr auto auto 32px;gap:10px;align-items:center;padding:10px 16px;border-top:1px solid var(--divider-color);position:relative}
.row:first-of-type{border-top:0}
.row .ph{width:32px}
.dot{width:10px;height:10px;border-radius:50%;background:var(--success-color,#43a047)}
.dot.off{background:var(--disabled-color,#9e9e9e)}.dot.busy{background:var(--warning-color,#ffa726)}
.n{font-weight:500;line-height:1.25;color:var(--primary-text-color)}
.d{font-size:12px;color:var(--secondary-text-color);line-height:1.3}
.row.out .n{color:var(--secondary-text-color)}
.sw{width:38px;height:22px;border-radius:11px;border:0;padding:0;background:var(--primary-color);position:relative;flex:none}
.sw::after{content:"";position:absolute;top:2px;left:18px;width:18px;height:18px;border-radius:50%;background:#fff;box-shadow:0 1px 2px rgba(0,0,0,.35);transition:left .15s}
.sw.off{background:var(--disabled-color,#9e9e9e)}.sw.off::after{left:2px}
.sw:disabled{opacity:.45;cursor:default}
.spin{width:12px;height:12px;border:2px solid var(--divider-color);border-top-color:var(--warning-color,#ffa726);border-radius:50%;display:inline-block;vertical-align:-2px;animation:r 1s linear infinite}
@keyframes r{to{transform:rotate(360deg)}}
.pop{position:absolute;right:12px;top:46px;z-index:5;min-width:220px;background:var(--card-background-color,#fff);border:1px solid var(--divider-color);border-radius:8px;box-shadow:0 4px 18px rgba(0,0,0,.28);padding:4px}
.pop button{display:flex;width:100%;gap:10px;align-items:center;border:0;background:none;border-radius:6px;padding:9px 12px;font-size:13.5px;text-align:left}
.pop button:hover{background:var(--secondary-background-color)}
.pop .dg{color:var(--error-color,#d32f2f)}
.pop hr{border:0;border-top:1px solid var(--divider-color);margin:4px 0}
/* cartes */
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(230px,1fr));gap:12px;padding:8px 16px 16px}
.tile{border:1px solid var(--divider-color);border-radius:12px;padding:12px;position:relative}
.tile .top{display:flex;align-items:center;gap:8px}
.tile .top .n{font-size:16px}
.badge{margin-left:auto;font-size:11.5px;border-radius:12px;padding:2px 9px;white-space:nowrap;background:color-mix(in srgb,var(--success-color,#43a047) 18%,transparent);color:var(--success-color,#43a047)}
.badge.off{background:var(--secondary-background-color);color:var(--secondary-text-color)}
.badge.busy{background:color-mix(in srgb,var(--warning-color,#ffa726) 20%,transparent);color:var(--warning-color,#ef6c00)}
.power{width:100%;margin:10px 0;height:56px;border:0;border-radius:12px;background:var(--primary-color);color:#fff;display:flex;align-items:center;justify-content:center;gap:10px;font-size:15px;font-weight:500}
.power.off{background:var(--secondary-background-color);color:var(--secondary-text-color)}
.power:disabled{opacity:.5;cursor:default}
.power svg{width:24px;height:24px}
.acts{display:grid;grid-auto-flow:column;grid-auto-columns:1fr;gap:8px}
.acts button{border:0;border-radius:10px;background:var(--secondary-background-color);padding:8px 4px;display:flex;flex-direction:column;align-items:center;gap:3px;font-size:11.5px}
.acts button:disabled{opacity:.4;cursor:default}
.acts .dg{color:var(--error-color,#d32f2f)}
.acts svg{width:20px;height:20px}
.pend{font-size:12px;color:var(--secondary-text-color);margin:2px 0 4px}
/* confirmation */
.ov{position:fixed;inset:0;z-index:9999;background:rgba(0,0,0,.5);display:flex;align-items:center;justify-content:center;padding:16px}
.dlg{background:var(--card-background-color,#fff);color:var(--primary-text-color);border-radius:14px;box-shadow:0 8px 32px rgba(0,0,0,.4);width:340px;max-width:100%;padding:20px}
.dlg .t{font-size:17px;font-weight:500;margin-bottom:8px}
.dlg p{margin:0 0 16px;color:var(--secondary-text-color);font-size:14px}
.dlg .f{display:flex;justify-content:flex-end;gap:8px}
.dlg .f button{border:0;border-radius:18px;padding:8px 14px;font-size:14px;background:var(--secondary-background-color)}
.dlg .f .go{background:var(--primary-color);color:#fff}.dlg .f .go.dg{background:var(--error-color,#d32f2f)}
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
    this._sig = "";
    this._render(true);
  }

  set hass(hass) {
    this._hass = hass;
    this._render();
  }

  connectedCallback() { document.addEventListener("click", this._onDocClick, true); }
  disconnectedCallback() { document.removeEventListener("click", this._onDocClick, true); clearTimeout(this._timer); }

  getCardSize() { return 1 + Math.max(1, (this._items || []).length); }
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
    const t = this._t;
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
    const body = !items.length ? `<div class="empty">${esc(t.none)}</div>` : c.style === "cards" ? this._cards(items) : this._list(items);
    const group = c.show_group_buttons && items.some((s) => !s.swDisabled)
      ? `<button class="chip" data-act="all-on">${esc(t.allOn)}</button><button class="chip" data-act="all-off">${esc(t.allOff)}</button>` : "";
    this.shadowRoot.innerHTML = `<style>${CSS}</style><ha-card>
      <div class="hd"><div><div class="t">${esc(c.title || t.title)}</div><div class="s">${esc(t.summary(online, items.length - online))}</div></div><span class="sp"></span>${group}</div>
      ${body}${this._dialogHtml()}</ha-card>`;
    this._bind();
  }

  _schedule() {
    clearTimeout(this._timer);
    const now = Date.now();
    const next = [...Object.values(this._pending), ...Object.values(this._optimistic)].map((p) => p.until).filter((u) => u > now);
    if (next.length) this._timer = setTimeout(() => { this._sig = ""; this._render(); }, Math.min(...next) - now + 50);
  }

  _list(items) {
    const t = this._t, c = this._config;
    return items.map((s) => {
      const canDev = c.show_device_actions && (s.reboot || s.poweroff);
      const pop = this._menu === s.id ? `<div class="pop">
        ${s.reboot ? `<button data-act="reboot" data-id="${esc(s.id)}">${ICON.restartAlert}${esc(t.restartDevice)}</button>` : ""}
        ${s.reboot && s.poweroff ? "<hr>" : ""}
        ${s.poweroff ? `<button class="dg" data-act="poweroff" data-id="${esc(s.id)}">${ICON.power}${esc(t.powerOffDevice)}</button>` : ""}</div>` : "";
      return `<div class="row ${s.isOnline ? "" : "out"}">
        <i class="dot ${s.pending ? "busy" : s.isOnline ? "" : "off"}"></i>
        <div><div class="n">${esc(s.name)}</div><div class="d">${this._subline(s)}</div></div>
        <button class="sw ${s.swOn ? "" : "off"}" data-act="toggle" data-id="${esc(s.id)}" ${s.swDisabled ? "disabled" : ""} title="${esc(s.swOn ? t.screenOn : t.screenOff)}" aria-label="${esc(t.screenOn)}"></button>
        ${c.show_player_restart && s.player ? `<button class="ic" data-act="player" data-id="${esc(s.id)}" ${s.isOnline && !s.pending ? "" : "disabled"} title="${esc(t.restartPlayer)}">${ICON.restart}</button>` : '<span class="ph"></span>'}
        ${canDev ? `<button class="ic" data-act="menu" data-id="${esc(s.id)}" title="${esc(t.more)}">${ICON.dots}</button>` : '<span class="ph"></span>'}
        ${pop}</div>`;
    }).join("");
  }

  _cards(items) {
    const t = this._t, c = this._config;
    return `<div class="grid">${items.map((s) => {
      const acts = [];
      if (c.show_player_restart && s.player) acts.push(`<button data-act="player" data-id="${esc(s.id)}" ${s.isOnline && !s.pending ? "" : "disabled"}>${ICON.restart}${esc(t.player)}</button>`);
      if (c.show_device_actions && s.reboot) acts.push(`<button data-act="reboot" data-id="${esc(s.id)}" ${s.isOnline && !s.pending ? "" : "disabled"}>${ICON.restartAlert}${esc(t.device)}</button>`);
      if (c.show_device_actions && s.poweroff) acts.push(`<button class="dg" data-act="poweroff" data-id="${esc(s.id)}" ${s.isOnline && !s.pending ? "" : "disabled"}>${ICON.power}${esc(t.powerOff)}</button>`);
      const badge = s.pending ? `<span class="badge busy">…</span>` : s.isOnline ? `<span class="badge">${esc(t.online)}</span>` : `<span class="badge off">${esc(t.offline)}</span>`;
      return `<div class="tile"><div class="top"><div><div class="n">${esc(s.name)}</div><div class="d">${this._subline(s)}</div></div>${badge}</div>
        <button class="power ${s.swOn ? "" : "off"}" data-act="toggle" data-id="${esc(s.id)}" ${s.swDisabled ? "disabled" : ""}>${ICON.power}${esc(s.swOn ? t.screenOn : t.screenOff)}</button>
        ${acts.length ? `<div class="acts">${acts.join("")}</div>` : ""}</div>`;
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
