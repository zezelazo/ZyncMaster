// Pure decision helper for the Calendar pairs list. NO DOM, NO bridge, NO globals on purpose so
// plain `node --test` can unit-test it (tests/js-unit/pairs-render-state.test.mjs); views/calendar.js
// wires the returned state to the DOM. Mirrors the calendar-day.js pattern (a pure, importable module
// pulled out of the render path so the load-bearing decision is testable without a DOM).
//
// The decision is the "honest three-state empty rendering" from the failure diagnosis
// (docs/research/2026-06-13-app-failure-diagnosis.md §1.2, fix #3). A failed listPairs fetch must
// NOT be rendered as the genuine-empty "No sync pairs yet." message — that is a lie when the call
// actually threw (401/expired session or a network drop).

// pairsRenderState({ available, pairCount, pairsError, loadedPairs }) -> render token.
//
//   'list'    — there are pairs to show (pairCount > 0), OR the bridge is unavailable (browser mock):
//               render the list. A transient refresh failure with pairs already on screen stays here
//               so a flaky reload never blanks a working screen.
//   'failed'  — the fetch threw (401/expired/network): show the honest, actionable error + Retry,
//               NOT "No sync pairs yet.". pairsError wins over loadedPairs.
//   'empty'   — the server returned 200 with an empty array (loadedPairs true, no error): genuinely
//               no pairs yet.
//   'loading' — no result yet and no error: the fetch is still in flight.
//
// The states are mutually exclusive and total: every combination of inputs maps to exactly one token.
export function pairsRenderState({ available, pairCount, pairsError, loadedPairs } = {}) {
  // Browser mock (no bridge) always renders the demo list; only the bridged shell has the
  // three-state empty model. When there ARE pairs, render them regardless of a transient error.
  if (!available || pairCount > 0) return 'list';
  // No pairs to show: pick the honest empty/failed/loading message.
  if (pairsError) return 'failed';            // a thrown fetch must NEVER read as genuine-empty
  return loadedPairs ? 'empty' : 'loading';   // 200-empty vs still-in-flight
}
