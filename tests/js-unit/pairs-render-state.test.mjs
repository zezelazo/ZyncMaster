// Plain node unit test for the Calendar pairs-list render decision. No test framework beyond the
// built-in node:test + node:assert. Run with:  node --test tests/js-unit/pairs-render-state.test.mjs
//
// Two halves:
//   1. The pure decision helper pairsRenderState (ui/js/pairs-render-state.js), imported directly.
//      This is the load-bearing "honest three-state empty rendering" from the failure diagnosis
//      (docs/research/2026-06-13-app-failure-diagnosis.md §1.2, fix #3): a FAILED listPairs fetch
//      must NEVER read as the genuine-empty "No sync pairs yet." message.
//   2. A faithful re-creation of app.js's loadPairs() state mutations, driven by a stubbed bridge.
//      app.js touches `document` at module load, so it cannot be imported cleanly in node without a
//      DOM (same constraint web-transport.test.mjs documents). We therefore mirror loadPairs's
//      live-state transitions here and assert them — kept in lockstep with app.js's loadPairs().

import test from 'node:test';
import assert from 'node:assert/strict';
import { pairsRenderState } from '../../ui/js/pairs-render-state.js';

// ---- 1. pure decision helper --------------------------------------------------------------

test('FAILED fetch renders the error state, NOT the lying empty state', () => {
  // The exact bug fix #3 targets: pairsError true must NOT yield "empty".
  assert.equal(pairsRenderState({ available: true, pairCount: 0, pairsError: true, loadedPairs: false }), 'failed');
  // pairsError wins even if a prior load somehow set loadedPairs.
  assert.equal(pairsRenderState({ available: true, pairCount: 0, pairsError: true, loadedPairs: true }), 'failed');
});

test('200-with-empty-array renders the genuine-empty state', () => {
  // loadedPairs true, pairsError false, no pairs → genuinely "No sync pairs yet."
  assert.equal(pairsRenderState({ available: true, pairCount: 0, pairsError: false, loadedPairs: true }), 'empty');
});

test('not-yet-loaded (no result, no error) renders the loading state', () => {
  assert.equal(pairsRenderState({ available: true, pairCount: 0, pairsError: false, loadedPairs: false }), 'loading');
});

test('pairs present renders the list even with a transient error (a flaky reload never blanks a working screen)', () => {
  assert.equal(pairsRenderState({ available: true, pairCount: 3, pairsError: false, loadedPairs: true }), 'list');
  // Transient refresh failure WITH pairs on screen: still the list, never the error card.
  assert.equal(pairsRenderState({ available: true, pairCount: 3, pairsError: true, loadedPairs: true }), 'list');
});

test('browser mock (bridge unavailable) always renders the demo list', () => {
  assert.equal(pairsRenderState({ available: false, pairCount: 0, pairsError: false, loadedPairs: false }), 'list');
  assert.equal(pairsRenderState({ available: false, pairCount: 0, pairsError: true, loadedPairs: true }), 'list');
});

test('the decision is total and self-consistent over every input combination', () => {
  // Every (available, pairCount, pairsError, loadedPairs) combination must resolve to exactly one of
  // the four known tokens, and the invariants must hold: a failed fetch with no pairs is NEVER empty,
  // and any non-empty (or no-bridge) case is always the list.
  const tokens = new Set(['list', 'failed', 'empty', 'loading']);
  for (const available of [true, false]) {
    for (const pairCount of [0, 1, 5]) {
      for (const pairsError of [true, false]) {
        for (const loadedPairs of [true, false]) {
          const s = pairsRenderState({ available, pairCount, pairsError, loadedPairs });
          assert.ok(tokens.has(s), `unexpected token "${s}"`);
          if (!available || pairCount > 0) {
            assert.equal(s, 'list');
          } else if (pairsError) {
            assert.equal(s, 'failed', 'a failed fetch with no pairs must never be empty/loading/list');
          } else {
            assert.equal(s, loadedPairs ? 'empty' : 'loading');
          }
        }
      }
    }
  }
});

test('a bare/empty call does not throw and resolves to the list (no-bridge default)', () => {
  // Defensive: a paint before live-state init must not crash the view. With no fields, `available`
  // is undefined (falsy) → the no-bridge branch → 'list' (the browser-mock path). The contract is
  // simply that the helper never throws on a malformed/partial argument.
  assert.doesNotThrow(() => pairsRenderState());
  assert.equal(pairsRenderState(), 'list');
  assert.equal(pairsRenderState({}), 'list');
  // But once the bridge flag is present, a partial object still resolves correctly.
  assert.equal(pairsRenderState({ available: true }), 'loading'); // no pairs, no error, not loaded
});

// ---- 2. loadPairs() state transitions (mirrors app.js) ------------------------------------
// Faithful re-creation of app.js's loadPairs() so we can drive it with a stubbed bridge and assert
// the live-state it sets. Kept in lockstep with app.js loadPairs(): a throw flags pairsError and
// does NOT mark loadedPairs; a 200 marks loadedPairs and CLEARS pairsError; loadingPairs is always
// reset in finally. The fire-and-forget side calls (ensureLocalDevice / ensurePairCalendarNames)
// are no-ops here — they don't touch the three-state model.
function makeLoadPairs(bridge, live) {
  return async function loadPairs() {
    if (!bridge.available) { live.pairs = null; return bridge.mockPairs; }
    live.loadingPairs = true;
    try {
      const list = await bridge.call('listPairs');
      live.pairs = Array.isArray(list) ? list : [];
      live.loadedPairs = true;
      live.pairsError = false;
      return live.pairs;
    } catch (_) {
      live.pairs = live.pairs || [];
      live.pairsError = true;
      return live.pairs;
    } finally {
      live.loadingPairs = false;
    }
  };
}

function freshLive() {
  return { pairs: null, loadedPairs: false, loadingPairs: false, pairsError: false };
}

test('loadPairs sets pairsError=true and leaves loadedPairs false on a thrown fetch', async () => {
  const live = freshLive();
  const bridge = { available: true, call: async () => { throw new Error('http 401'); } };
  const loadPairs = makeLoadPairs(bridge, live);

  await loadPairs();
  assert.equal(live.pairsError, true, 'a thrown fetch must flag pairsError');
  assert.equal(live.loadedPairs, false, 'a FAILED fetch is not "loaded" — it failed');
  assert.equal(live.loadingPairs, false, 'finally always resets the in-flight flag');
  assert.deepEqual(live.pairs, [], 'no prior pairs → empty array, not null (so render is stable)');

  // And the render decision off that state is the honest error, never the lying empty.
  assert.equal(pairsRenderState({
    available: bridge.available, pairCount: live.pairs.length,
    pairsError: live.pairsError, loadedPairs: live.loadedPairs,
  }), 'failed');
});

test('loadPairs preserves previously-loaded pairs on a transient failure (does not blank a working screen)', async () => {
  const live = freshLive();
  // First a good load, then a failing reload.
  let mode = 'ok';
  const bridge = {
    available: true,
    call: async () => { if (mode === 'fail') throw new Error('network'); return [{ id: 'p1' }, { id: 'p2' }]; },
  };
  const loadPairs = makeLoadPairs(bridge, live);

  await loadPairs();
  assert.equal(live.loadedPairs, true);
  assert.equal(live.pairs.length, 2);

  mode = 'fail';
  await loadPairs();
  assert.equal(live.pairsError, true, 'the reload failed');
  assert.deepEqual(live.pairs.map((p) => p.id), ['p1', 'p2'], 'prior pairs survive a transient failure');
  // With pairs still on screen the render stays the list, never the error card.
  assert.equal(pairsRenderState({
    available: true, pairCount: live.pairs.length,
    pairsError: live.pairsError, loadedPairs: live.loadedPairs,
  }), 'list');
});

test('loadPairs clears pairsError and marks loadedPairs on a 200 (Retry recovers the view)', async () => {
  const live = freshLive();
  // Start in the failed state, then a successful retry.
  live.pairsError = true;
  live.pairs = [];
  let mode = 'fail';
  const bridge = {
    available: true,
    call: async () => { if (mode === 'fail') throw new Error('401'); return mode === 'empty' ? [] : [{ id: 'p1' }]; },
  };
  const loadPairs = makeLoadPairs(bridge, live);

  // Retry returns a non-empty list: error cleared, pairs shown.
  mode = 'ok';
  await loadPairs();
  assert.equal(live.pairsError, false, 'a 200 clears the prior error');
  assert.equal(live.loadedPairs, true);
  assert.equal(live.pairs.length, 1);

  // Retry returns 200-empty: error cleared, genuine-empty (not failed).
  Object.assign(live, freshLive(), { pairsError: true, pairs: [] });
  mode = 'empty';
  await loadPairs();
  assert.equal(live.pairsError, false, 'even a 200-EMPTY clears the error — it is the genuine-empty state');
  assert.equal(live.loadedPairs, true);
  assert.equal(live.pairs.length, 0);
  assert.equal(pairsRenderState({
    available: true, pairCount: 0, pairsError: live.pairsError, loadedPairs: live.loadedPairs,
  }), 'empty');
});

test('loadPairs returns the mock and nulls pairs when the bridge is unavailable (browser panel)', async () => {
  const live = freshLive();
  const bridge = { available: false, mockPairs: [{ id: 'demo' }] };
  const loadPairs = makeLoadPairs(bridge, live);
  const out = await loadPairs();
  assert.deepEqual(out, [{ id: 'demo' }]);
  assert.equal(live.pairs, null, 'web panel uses the mock list, not live.pairs');
});
