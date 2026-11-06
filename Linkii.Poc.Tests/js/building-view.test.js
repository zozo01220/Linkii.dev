// Tests de la logique de la vue du bâtiment (player/building-view.js, DEV-POC §199) : node --test Linkii.Poc.Tests/js
// Aussi lancés par dotnet test (BuildingViewJsTests) quand Node est installé.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const player = path.join(__dirname, '..', '..', 'Linkii.Poc', 'wwwroot', 'player');
const Ha = require(path.join(player, 'ha-format.js'));
const { logic: L } = require(path.join(player, 'building-view.js'));

const E = (id, name, domain, o) => Object.assign({ id, name, domain, available: true, ctrl: [] }, o);
const NOW = Date.parse('2026-11-06T12:00:00Z');

// Maison d'exemple : un étage dessiné (salon, entrée) et un étage en image
const plan = {
  floors: [
    { id: 'rdc', name: 'Rez', kind: 'rooms', w: 10, h: 8, rooms: [{ id: 'salon', name: 'Salon', x: 0, y: 0, w: 6, h: 4 }, { id: 'entree', name: 'Entrée', x: 6, y: 0, w: 4, h: 4 }], walls: [] },
    { id: 'e1', name: 'Étage', kind: 'image', img: '/media/e1.png', ratio: 1.5 }
  ],
  devices: [
    { id: 'light.salon', floor: 'rdc', x: .2, y: .2, room: 'salon' },
    { id: 'light.lampadaire', floor: 'rdc', x: .3, y: .3, room: 'salon' },
    { id: 'sensor.temp_salon', floor: 'rdc', x: .1, y: .1, room: 'salon' },
    { id: 'binary_sensor.porte', floor: 'rdc', x: .9, y: .1, room: 'entree', seg: { x: 10, y: 1, d: 90, len: 1, s: -1, kind: 'door' } },
    { id: 'binary_sensor.brother', floor: 'rdc', x: .8, y: .3, room: 'entree' },
    { id: 'light.garage', floor: 'rdc', x: .7, y: .3, room: 'entree' },
    { id: 'cover.volet', floor: 'rdc', x: .3, y: .02, room: 'salon', seg: { x: 2, y: 0, d: 0, len: 2, s: 1, kind: 'cover' } },
    { id: 'switch.prise', floor: 'e1', x: .5, y: .5 }
  ]
};
const ents = {
  'light.salon': E('light.salon', 'Salon', 'light', { state: 'on', on: true, brightness: 80, color: '#ffc870' }),
  'light.lampadaire': E('light.lampadaire', 'Lampadaire', 'light', { state: 'on', on: true, brightness: 40 }),
  'sensor.temp_salon': E('sensor.temp_salon', 'Température salon', 'sensor', { state: '18.2', unit: '°C', deviceClass: 'temperature' }),
  'binary_sensor.porte': E('binary_sensor.porte', "Porte d'entrée", 'binary_sensor', { state: 'on', on: true, deviceClass: 'door' }),
  'binary_sensor.brother': E('binary_sensor.brother', 'Imprimante', 'binary_sensor', { state: 'on', on: true, deviceClass: 'connectivity' }),
  'light.garage': E('light.garage', 'Garage', 'light', { state: 'unavailable', available: false }),
  'cover.volet': E('cover.volet', 'Volet salon', 'cover', { state: 'open', position: 30 }),
  'switch.prise': E('switch.prise', 'Prise', 'switch', { state: 'off', on: false })
};

test('état des pastilles : vraies alertes, mouvements, neutres', () => {
  assert.equal(L.state(ents['binary_sensor.porte']), 'alert');
  assert.equal(L.state(ents['binary_sensor.brother']), '');          // « Connecté » n'est pas une alerte (§198)
  assert.equal(L.state(E('m', 'M', 'binary_sensor', { on: true, deviceClass: 'motion' })), 'move');
  assert.equal(L.state(E('l', 'L', 'lock', { on: true })), 'alert');   // serrure déverrouillée
  assert.equal(L.state(ents['light.salon']), 'on');
  assert.equal(L.state(ents['light.garage']), 'off');
});

test('température d\'une pièce : thermostat ou capteur de température', () => {
  assert.equal(L.roomTemp(ents['sensor.temp_salon']), 18.2);
  assert.equal(L.roomTemp(E('c', 'C', 'climate', { current: 21.5 })), 21.5);
  assert.equal(L.roomTemp(E('h', 'H', 'sensor', { state: '55', unit: '%' })), null);
  assert.equal(L.roomTemp(E('c', 'C', 'climate', { current: 20, available: false })), null);
});

test('étiquettes « Par pièce » : une par pièce, alertes à part, indisponibles masqués', () => {
  const sc = L.scene(plan, ents, { labels: 'room', hideOff: true, heat: true, decimals: 'auto', now: NOW });
  assert.equal(sc.pins['light.salon'].show, false);                      // résumé par la pièce
  assert.equal(sc.pins['binary_sensor.porte'].show, true);               // alerte : sa propre étiquette, avec son nom
  assert.equal(sc.pins['binary_sensor.porte'].text, "Porte d'entrée · Ouverte");
  assert.equal(sc.pins['light.garage'].show, false);                     // indisponible masqué
  assert.equal(sc.pins['switch.prise'].show, true);                      // hors pièce (étage en image) : comme « actifs seulement »
  assert.equal(sc.pins['switch.prise'].bare, true);
  const salon = sc.rooms['rdc/salon'];
  assert.deepEqual([salon.show, salon.lights, salon.t, salon.lit], [true, 2, 18.2, true]);
  assert.equal(sc.rooms['rdc/entree'].show, false);                      // rien à dire : ni lumière, ni température
  assert.equal(sc.pins['cover.volet'].shut, 70);                         // volet ouvert à 30 % : 70 % de tablier
  assert.equal(sc.pins['binary_sensor.porte'].open, true);
  assert.deepEqual(sc.pins['light.salon'].glow, { b: 80, col: '#ffc870' });
});

test('autres modes d\'étiquettes', () => {
  const all = L.scene(plan, ents, { labels: 'all', hideOff: false, now: NOW });
  assert.equal(all.pins['light.salon'].show, true);
  assert.equal(all.pins['light.salon'].text, '80 %');
  assert.equal(all.pins['light.garage'].show, true);                     // indisponibles gardés
  assert.equal(all.rooms['rdc/salon'].show, false);                      // pas d'étiquette de pièce hors « Par pièce »
  const none = L.scene(plan, ents, { labels: 'none', now: NOW });
  assert.equal(none.pins['light.salon'].bare, true);
  const pulse = L.scene(plan, ents, { labels: 'room', now: NOW, pulseId: 'light.salon' });
  assert.equal(pulse.pins['light.salon'].show, true);                    // appareil mis en avant : visible même résumé par sa pièce
});

test('couleur des pièces : carte de chaleur et lumière', () => {
  assert.equal(L.roomColor(null, true), '#21406a');
  const cold = L.roomColor({ t: 18 }, true), warm = L.roomColor({ t: 24 }, true);
  assert.notEqual(cold, '#21406a'); assert.notEqual(warm, cold);
  assert.equal(L.roomColor({ t: 18 }, false), '#21406a');                // carte de chaleur coupée
  assert.equal(L.mix('#000000', '#ffffff', 0.5), '#808080');
});

test('résumé : alertes, lumières, températures, volets, indisponibles', () => {
  const s = L.summary(plan, ents, { decimals: 'auto', now: NOW });
  assert.deepEqual(s.alerts.map(i => i.t), ["Porte d'entrée"]);
  assert.deepEqual(s.lights.map(i => [i.t, i.s]), [['Salon', ''], ['Salon', 'Lampadaire']]);   // pas de « Salon · Salon »
  assert.deepEqual(s.temps.map(i => [i.t, i.v]), [['Salon', '18,2°']]);
  assert.deepEqual(s.covers, { open: 1, closed: 0 });
  assert.equal(s.off, 1);
  assert.equal(L.coversText({ open: 1, closed: 2 }), '1 ouvert · 2 fermés');
  const html = L.summaryHtml(s, 'right', 'Maison <Gex>', true);
  assert.match(html, /Maison &lt;Gex&gt;/);                              // texte échappé
  assert.match(html, /2 lumières allumées/);
  assert.match(html, /1 appareil indisponible/);
  assert.match(L.summaryHtml(s, 'top', 'Maison', true), /^<div class="hpl-track">/);
});

test('place du résumé : un panneau latéral devient un bandeau dans un widget étroit', () => {
  assert.equal(L.layoutMode('right', 1200), 'right');
  assert.equal(L.layoutMode('left', 1200), 'left');
  assert.equal(L.layoutMode('right', 500), 'top');
  assert.equal(L.layoutMode('none', 500), 'none');
  assert.equal(L.layoutMode('n importe quoi', 1200), 'right');
});

test('événements : seulement un nouveau signal', () => {
  const porte = ents['binary_sensor.porte'];
  assert.equal(L.eventText(porte, '', 'alert'), "Porte d'entrée : ouvert");
  assert.equal(L.eventText(porte, 'alert', 'alert'), null);
  assert.equal(L.eventText(E('m', 'Couloir', 'binary_sensor', {}), '', 'move'), 'Couloir : mouvement');
  assert.equal(L.eventText(porte, 'alert', ''), null);
});

test('formatage commun (ha-format.js)', () => {
  assert.equal(Ha.value(ents['sensor.temp_salon'], 'auto', NOW), '18,2 °C');
  assert.equal(Ha.value(ents['light.garage'], 'auto', NOW), 'Indisponible');
  assert.equal(Ha.deg(21), '21°');
  assert.match(Ha.icon({ domain: 'light' }), /^<svg class="ha-i"/);
});

test('visite de la diffusion : étape en cours et temps restant, en boucle', () => {
  const tour = [{ floor: null, sec: 12 }, { floor: 'rdc', sec: 8 }, { floor: 'ss', sec: 8 }];
  assert.equal(L.tourAt(tour, 0).i, 0);
  assert.equal(L.tourAt(tour, 11999).left, 1);
  assert.equal(L.tourAt(tour, 12000).step.floor, 'rdc');
  assert.equal(L.tourAt(tour, 27999).i, 2);
  assert.equal(L.tourAt(tour, 28000).i, 0);                              // la visite recommence
  assert.equal(L.tourAt([], 5000), null);
});

test('borne : étage d\'accueil et retour d\'un niveau', () => {
  assert.equal(L.homeFloor(plan), 'rdc');                                // l'étage qui porte le plus d'appareils
  assert.deepEqual(L.navUp({ floor: 'rdc', room: 'salon', dev: 'light.salon' }, plan), { floor: 'rdc', room: 'salon', dev: null });
  assert.deepEqual(L.navUp({ floor: 'rdc', room: 'salon', dev: null }, plan), { floor: 'rdc', room: null, dev: null });
  assert.deepEqual(L.navUp({ floor: 'e1', room: null, dev: 'switch.prise' }, plan), { floor: 'e1', room: null, dev: null });   // appareil hors pièce : retour à l'étage
  const n = { floor: 'rdc', room: null, dev: null };
  assert.equal(L.navUp(n, plan), n);                                     // à l'étage : la borne a son propre retour
});

test('borne : la pièce choisie est surlignée, les autres appareils s\'estompent', () => {
  const sc = L.scene(plan, ents, { labels: 'all', now: NOW, room: 'rdc/salon', panelId: 'light.salon' });
  assert.equal(sc.rooms['rdc/salon'].sel, true);
  assert.equal(sc.rooms['rdc/entree'].sel, false);
  assert.equal(sc.pins['light.salon'].dim, false);
  assert.equal(sc.pins['light.salon'].sel, true);
  assert.equal(sc.pins['binary_sensor.porte'].dim, true);
});

test('widget dans une petite case : le résumé seul', () => {
  assert.equal(L.layoutMode('right', 360, 300), 'only');
  assert.equal(L.layoutMode('right', 500, 200), 'only');
  assert.equal(L.layoutMode('none', 300, 200), 'none');                  // résumé masqué : la 3D reste
  assert.equal(L.layoutMode('right', 500, 400), 'top');
});
