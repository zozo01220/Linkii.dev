// Tests de la logique de la porte d'un écran de salle (player/room-door.js) : node --test Linkii.Poc.Tests/js
// Aussi lancés par dotnet test (RoomDoorJsTests) quand Node est installé. Mêmes règles que RoomRules (C#, RoomsTests).
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { logic: L } = require(path.join(__dirname, '..', '..', 'Linkii.Poc', 'wwwroot', 'player', 'room-door.js'));

const TZ = 'Europe/Zurich';
// lundi 9 novembre 2026, heure de Zurich (UTC+1)
const at = (h, m = 0, day = 9) => Date.UTC(2026, 10, day, h - 1, m);
const ev = (h1, m1, h2, m2, title = '', linkivi = false) => ({ title, start: new Date(at(h1, m1)).toISOString(), end: new Date(at(h2, m2)).toISOString(), linkivi });

test('occupée pendant une réunion, jusqu’à la fin des réunions qui se chevauchent', () => {
  const s = L.state([ev(13, 30, 15, 0), ev(14, 45, 15, 30), ev(16, 30, 17, 30)], at(14, 10), 10, false, TZ);
  assert.equal(s.st, 'busy');
  assert.equal(s.until, at(15, 30));
  assert.equal(s.next.length, 1);
  assert.equal(L.words(s, TZ).sub, 'jusqu’à 15:30');
});

test('bientôt occupée juste avant une réunion, libre avant', () => {
  const evs = [ev(14, 18, 15, 30)];
  assert.equal(L.state(evs, at(14, 10), 10, false, TZ).st, 'soon');
  const free = L.state(evs, at(14, 0), 10, false, TZ);
  assert.equal(free.st, 'free');
  assert.equal(L.words(free, TZ).sub, 'jusqu’à 14:18');
});

test('libre toute la journée sans réunion aujourd’hui', () => {
  const s = L.state([{ start: new Date(at(9, 0, 10)).toISOString(), end: new Date(at(10, 0, 10)).toISOString() }], at(14, 10), 10, false, TZ);
  assert.equal(s.st, 'free');
  assert.equal(s.until, null);
  assert.equal(L.words(s, TZ).sub, 'toute la journée');
});

test('le titre n’apparaît que s’il a été envoyé ; une réservation Linkivi s’affiche « Réservé sur place »', () => {
  const s = L.state([ev(14, 0, 15, 0, '')], at(14, 10), 10, false, TZ);
  assert.equal(L.words(s, TZ).sub, 'jusqu’à 15:00');
  const sl = L.slots([ev(14, 10, 14, 40, 'Réservé sur place', true), ev(16, 0, 17, 0)], at(14, 20), false, TZ);
  assert.equal(sl.length, 2);
  assert.ok(sl[0].lk);
  assert.equal(sl[1].title, '');
});

test('écrans gris : sans salle, salle désactivée, application retirée, données trop anciennes', () => {
  assert.equal(L.grey('none', {})[0], 'APPAIRÉE');
  assert.equal(L.grey('off', {})[0], 'SALLE INDISPONIBLE');
  assert.equal(L.grey('retired', {})[0], 'APPLICATION RETIRÉE');
  assert.equal(L.grey('ok', { stale: true })[0], 'DONNÉES INDISPONIBLES');
  assert.equal(L.grey('ok', { stale: false }), null);
});

test('la zone de réservation n’apparaît que pour une salle réservable, libre, boutons activés', () => {
  const d = { canBook: true, options: [{ min: 15, ok: true }] };
  assert.ok(L.bookable(d, { st: 'free' }, true));
  assert.ok(L.bookable(d, { st: 'soon' }, true));
  assert.ok(!L.bookable(d, { st: 'busy' }, true));
  assert.ok(!L.bookable(d, { st: 'free' }, false));
  assert.ok(!L.bookable({ canBook: false, options: d.options }, { st: 'free' }, true));
});

test('réponses du serveur : jamais « Réservée » sans 200', () => {
  assert.equal(L.answer(200), null);
  assert.match(L.answer(409), /Déjà réservée/);
  assert.match(L.answer(502, { code: 'rights' }), /Prévenez l’administrateur/);
  assert.match(L.answer(429), /réessayez/);
});

test('libellés des durées', () => {
  assert.equal(L.label(15), '15 min');
  assert.equal(L.label(60), '1 h');
  assert.equal(L.label(90), '1 h 30');
});
