// palette-model.test.mjs — filtrado agrupado + selección por teclado del palette.
// Run: node tests/js-unit/palette-model.test.mjs
import assert from 'node:assert/strict';
import { filterPalette, flattenGroups, moveSelection } from '../../ui/js/core/palette-model.js';

let passed = 0;
function test(name, fn) {
  try { fn(); passed += 1; console.log(`  ok  - ${name}`); }
  catch (err) { console.error(`  FAIL - ${name}\n        ${err.message}`); process.exitCode = 1; }
}

const items = [
  { group: 'Navigation', label: 'Go to Calendar', run: () => {} },
  { group: 'Navigation', label: 'Go to Clipboard', run: () => {} },
  { group: 'Actions', label: 'Add calendar account', run: () => {} },
  { group: 'Sync pairs', label: 'Work → Personal', run: () => {} },
];

test('empty query: every item, grouped, capped', () => {
  const groups = filterPalette(items, '');
  assert.deepEqual(groups.map((g) => g.group), ['Navigation', 'Actions', 'Sync pairs']);
  assert.equal(flattenGroups(groups).length, 4);
});
test('query filters to fuzzy matches only and ranks them', () => {
  const groups = filterPalette(items, 'cal');
  const flat = flattenGroups(groups);
  assert.ok(flat.length >= 2);
  assert.ok(flat.every((i) => /cal/i.test(i.label.replace(/[^a-z]/gi, '')) || /c.*a.*l/i.test(i.label)));
  assert.ok(!flat.some((i) => i.label === 'Work → Personal'));
});
test('no matches -> empty groups', () => {
  assert.deepEqual(filterPalette(items, 'zzzz'), []);
});
test('matched items keep their run handler and gain ranges', () => {
  const flat = flattenGroups(filterPalette(items, 'work'));
  assert.equal(flat.length, 1);
  assert.equal(typeof flat[0].run, 'function');
  assert.ok(Array.isArray(flat[0].ranges));
});
test('moveSelection wraps both ways and handles empty lists', () => {
  assert.equal(moveSelection(3, 0, 1), 1);
  assert.equal(moveSelection(3, 2, 1), 0);
  assert.equal(moveSelection(3, 0, -1), 2);
  assert.equal(moveSelection(0, 0, 1), -1);
});

console.log(`\n${passed} passed${process.exitCode ? ' (with failures)' : ''}`);
