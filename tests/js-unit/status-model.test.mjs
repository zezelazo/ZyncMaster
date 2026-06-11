// status-model.test.mjs — derivación de los status dots del sidebar y las filas de
// "Sync health" desde el snapshot real. Run: node tests/js-unit/status-model.test.mjs

import assert from 'node:assert/strict';
import { calendarDot, clipboardDot, devicesDot, healthRows } from '../../ui/js/core/status-model.js';

let passed = 0;
function test(name, fn) {
  try { fn(); passed += 1; console.log(`  ok  - ${name}`); }
  catch (err) { console.error(`  FAIL - ${name}\n        ${err.message}`); process.exitCode = 1; }
}

// ---- calendarDot ----------------------------------------------------------------------
test('calendarDot: null until pairs load / with zero pairs', () => {
  assert.equal(calendarDot([], false), null);
  assert.equal(calendarDot([], true), null);
});
test('calendarDot: ok when all pairs healthy', () => {
  const d = calendarDot([{ state: 'active', lastResult: { failed: 0 } }], true);
  assert.equal(d.state, 'ok');
});
test('calendarDot: err when a pair is disabled (broken)', () => {
  const d = calendarDot([{ state: 'disabled' }, { state: 'active' }], true);
  assert.equal(d.state, 'err');
});
test('calendarDot: warn on failed runs or pinned device offline', () => {
  assert.equal(calendarDot([{ state: 'active', lastResult: { failed: 2 } }], true).state, 'warn');
  assert.equal(calendarDot([{ state: 'active', pinnedDeviceOnline: false }], true).state, 'warn');
});

// ---- clipboardDot ---------------------------------------------------------------------
test('clipboardDot: off when unregistered or both directions off', () => {
  assert.equal(clipboardDot(null).state, 'off');
  assert.equal(clipboardDot({ settings: { send: false, receive: false } }).state, 'off');
});
test('clipboardDot: ok when send or receive on', () => {
  assert.equal(clipboardDot({ settings: { send: true, receive: false } }).state, 'ok');
  assert.equal(clipboardDot({ settings: { receive: true } }).state, 'ok');
});

// ---- devicesDot -----------------------------------------------------------------------
test('devicesDot: null with no roster, warn when another device offline', () => {
  assert.equal(devicesDot(null), null);
  assert.equal(devicesDot([]), null);
  assert.equal(devicesDot([{ name: 'A', online: true, isThis: true }]), null);
  const d = devicesDot([{ name: 'LAPTOP-ZL', online: false, isThis: false }]);
  assert.equal(d.state, 'warn');
  assert.ok(d.title.includes('LAPTOP-ZL'));
});

// ---- healthRows -----------------------------------------------------------------------
test('healthRows: one row per pair with state + change count', () => {
  const rows = healthRows([
    { id: 'p1', name: 'Work → Personal', state: 'active', lastRunUtc: '2026-06-10T09:42:00Z', lastResult: { created: 1, updated: 2, deleted: 0, failed: 0 }, intervalMin: 10 },
    { id: 'p2', name: 'Broken', state: 'disabled' },
  ], []);
  assert.equal(rows.length, 2);
  assert.equal(rows[0].kind, 'pair');
  assert.equal(rows[0].state, 'ok');
  assert.equal(rows[0].changes, 3);
  assert.equal(rows[1].state, 'err');
});
test('healthRows: devices row appended when roster present', () => {
  const rows = healthRows([], [{ name: 'A', online: true, isThis: true }, { name: 'B', online: false }]);
  assert.equal(rows.length, 1);
  assert.equal(rows[0].kind, 'devices');
  assert.equal(rows[0].state, 'warn');
  assert.ok(rows[0].detail.includes('B'));
});

console.log(`\n${passed} passed${process.exitCode ? ' (with failures)' : ''}`);
