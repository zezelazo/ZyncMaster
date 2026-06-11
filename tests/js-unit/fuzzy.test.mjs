// fuzzy.test.mjs — subsequence matcher del command palette. Run: node tests/js-unit/fuzzy.test.mjs
import assert from 'node:assert/strict';
import { fuzzyMatch } from '../../ui/js/core/fuzzy.js';

let passed = 0;
function test(name, fn) {
  try { fn(); passed += 1; console.log(`  ok  - ${name}`); }
  catch (err) { console.error(`  FAIL - ${name}\n        ${err.message}`); process.exitCode = 1; }
}

test('empty query matches everything with score 0', () => {
  assert.deepEqual(fuzzyMatch('', 'Go to Calendar'), { score: 0, ranges: [] });
});
test('non-subsequence returns null', () => {
  assert.equal(fuzzyMatch('xyz', 'Go to Calendar'), null);
  assert.equal(fuzzyMatch('calendarr', 'calendar'), null);
});
test('subsequence matches case-insensitively with highlight ranges', () => {
  const m = fuzzyMatch('cal', 'Go to Calendar');
  assert.ok(m && m.score > 0);
  assert.deepEqual(m.ranges, [[6, 9]]); // "Cal"
});
test('contiguous + word-start matches beat scattered ones', () => {
  const contiguous = fuzzyMatch('cal', 'Calendar').score;
  const scattered = fuzzyMatch('cal', 'Clipboard all').score;
  assert.ok(contiguous > scattered, `${contiguous} <= ${scattered}`);
});
test('gaps split into multiple ranges', () => {
  const m = fuzzyMatch('gtc', 'Go to Calendar');
  assert.equal(m.ranges.length, 3);
});

console.log(`\n${passed} passed${process.exitCode ? ' (with failures)' : ''}`);
