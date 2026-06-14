// Plain node unit test for the live "pair-run" push decision (diagnosis §B, fix #5). No framework
// beyond the built-in node:test + node:assert. Run with:
//   node --test tests/js-unit/pair-run-apply.test.mjs
//
// Covers the pure decision helper planPairRun (ui/js/pair-run-apply.js), imported directly. app.js
// touches `document` at module load, so its onPairRunPushed handler cannot be imported in node
// without a DOM (same constraint pairs-render-state.test.mjs / web-transport.test.mjs document); the
// load-bearing de-dupe / patch / reload decision is extracted here so it is testable without a DOM.

import test from 'node:test';
import assert from 'node:assert/strict';
import { planPairRun } from '../../ui/js/pair-run-apply.js';

const PAIRS = () => [
  { id: 'p1', lastResult: { created: 1 }, lastRunUtc: '2026-06-01T00:00:00Z' },
  { id: 'p2', lastResult: { created: 0 } },
];

// ---- ignore: malformed frames -------------------------------------------------------------

test('ignore: null payload', () => {
  assert.equal(planPairRun(null, PAIRS()).kind, 'ignore');
});

test('ignore: payload without a pairId', () => {
  assert.equal(planPairRun({ lastResult: { created: 5 } }, PAIRS()).kind, 'ignore');
});

// ---- reload: the pair is not in this snapshot ---------------------------------------------

test('reload: a run for a pair we do not have (created elsewhere)', () => {
  const plan = planPairRun({ pairId: 'p-new', lastResult: { created: 3 } }, PAIRS());
  assert.equal(plan.kind, 'reload');
});

test('reload: pairs snapshot is null (list never loaded)', () => {
  const plan = planPairRun({ pairId: 'p1', lastResult: { created: 3 } }, null);
  assert.equal(plan.kind, 'reload');
});

// ---- patch: an existing pair's row gets the new last-run + result --------------------------

test('patch: existing pair gets lastResult + lastRunUtc from the frame', () => {
  const plan = planPairRun(
    { pairId: 'p1', lastResult: { created: 4, updated: 2 }, lastRunUtc: '2026-06-13T18:30:00Z' },
    PAIRS());
  assert.equal(plan.kind, 'patch');
  assert.equal(plan.pairId, 'p1');
  assert.deepEqual(plan.lastResult, { created: 4, updated: 2 });
  assert.equal(plan.lastRunUtc, '2026-06-13T18:30:00Z');
});

test('patch: a missing lastResult degrades to an empty object (never undefined)', () => {
  const plan = planPairRun({ pairId: 'p2' }, PAIRS());
  assert.equal(plan.kind, 'patch');
  assert.deepEqual(plan.lastResult, {});
});

test('patch: a non-object lastResult degrades to an empty object', () => {
  const plan = planPairRun({ pairId: 'p2', lastResult: 'oops' }, PAIRS());
  assert.deepEqual(plan.lastResult, {});
});

test('patch: an absent lastRunUtc yields null so the caller keeps the existing timestamp', () => {
  const plan = planPairRun({ pairId: 'p1', lastResult: { created: 1 } }, PAIRS());
  assert.equal(plan.lastRunUtc, null);
});

test('patch: an empty-string lastRunUtc also yields null (must not wipe the timestamp)', () => {
  const plan = planPairRun({ pairId: 'p1', lastResult: { created: 1 }, lastRunUtc: '' }, PAIRS());
  assert.equal(plan.lastRunUtc, null);
});

// ---- de-dupe contract: the origin device is excluded server-side, so a frame is always a -----
// peer/cron run; planPairRun has no special-casing for "our own" run because it never receives one.
// This test documents that a normal patch for a known pair is the expected single application.
test('de-dupe: a peer run for a known pair is a single in-place patch (no reload, no dup)', () => {
  const pairs = PAIRS();
  const plan = planPairRun({ pairId: 'p1', lastResult: { created: 9 }, lastRunUtc: '2026-06-13T19:00:00Z' }, pairs);
  assert.equal(plan.kind, 'patch');
  // The helper is pure — it does not mutate the input array (app.js applies the patch separately).
  assert.deepEqual(pairs[0].lastResult, { created: 1 }, 'planPairRun must not mutate the snapshot');
});
