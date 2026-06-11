// board-model.test.mjs — filas del tablero today (spec §3.3). Run: node tests/js-unit/board-model.test.mjs
import assert from 'node:assert/strict';
import { eventRows, clipboardRows } from '../../ui/js/core/board-model.js';

let passed = 0;
function test(name, fn) {
  try { fn(); passed += 1; console.log(`  ok  - ${name}`); }
  catch (err) { console.error(`  FAIL - ${name}\n        ${err.message}`); process.exitCode = 1; }
}

test('eventRows: null/missing payload -> [] (vacío honesto)', () => {
  assert.deepEqual(eventRows(null), []);
  assert.deepEqual(eventRows({}), []);
  assert.deepEqual(eventRows({ events: 'nope' }), []);
});
test('eventRows: maps time/title/source and drops finished events', () => {
  const now = new Date('2026-06-10T12:00:00');
  const rows = eventRows({ events: [
    { start: '2026-06-10T09:00:00', end: '2026-06-10T09:30:00', subject: 'Past', calendarName: 'Work' },
    { start: '2026-06-10T13:00:00', end: '2026-06-10T14:00:00', subject: 'Lunch', calendarName: 'Personal' },
    { start: '2026-06-10T15:00:00', end: '2026-06-10T16:30:00', calendarName: 'Work' },
  ] }, now);
  assert.equal(rows.length, 2);
  assert.equal(rows[0].title, 'Lunch');
  assert.equal(rows[0].source, 'Personal');
  assert.ok(rows[0].time.includes('–'));
  assert.equal(rows[1].title, '(no subject)');
});
test('eventRows: caps at max', () => {
  const now = new Date('2026-06-10T08:00:00');
  const ev = (i) => ({ start: `2026-06-10T1${i}:00:00`, end: `2026-06-10T1${i}:30:00`, subject: `E${i}` });
  assert.equal(eventRows({ events: [0, 1, 2, 3, 4, 5, 6].map(ev) }, now, 6).length, 6);
});
test('clipboardRows: maps text/image/file shapes (same fields the clipboard view reads)', () => {
  const rows = clipboardRows([
    { id: 1, type: 'Text', text: '  hello   world ', createdUtc: 'x', originDeviceName: 'LAPTOP' },
    { id: 2, type: 'image', text: null, sizeBytes: 1024 },
    { id: 3, type: 'text', text: null },
  ]);
  assert.equal(rows.length, 3);
  assert.deepEqual(rows[0], { id: 1, kind: 'text', title: 'hello world', device: 'LAPTOP', createdUtc: 'x' });
  assert.equal(rows[1].kind, 'image');
  assert.equal(rows[1].title, 'Image');
  assert.equal(rows[2].title, 'Waiting for key from your other device');
});
test('clipboardRows: caps at 5 by default and tolerates non-arrays', () => {
  assert.deepEqual(clipboardRows(null), []);
  assert.equal(clipboardRows(Array.from({ length: 9 }, (_, i) => ({ id: i, type: 'text', text: 'x' }))).length, 5);
});

console.log(`\n${passed} passed${process.exitCode ? ' (with failures)' : ''}`);
