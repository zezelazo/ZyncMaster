// Pure decision helper for the live "pair-run" push (diagnosis §B, fix #5). NO DOM, NO bridge, NO
// globals on purpose so plain `node --test` can unit-test it (tests/js-unit/pair-run-apply.test.mjs);
// app.js wires the returned plan to live state + the repaint. Mirrors the pairs-render-state.js /
// calendar-day.js pattern (a pure, importable module pulled out of the handler so the load-bearing
// decision is testable without a DOM).
//
// Context: the Sync module had no push channel — a run on another machine (or a cron RunDue on the
// VPS) only touched the DB, and this App learned about it solely by re-opening the Calendar screen.
// The server now fans a completed run over the shared clipboard socket (SyncBroadcaster), EXCLUDING
// the device that ran the pair. So a "pair-run" frame is always a PEER/cron run: applying it is never
// a double-apply of the origin's own run (runPairNow already patched the origin from its HTTP reply).

// planPairRun(payload, pairs) -> a plan describing how the UI should react to one "pair-run" frame.
//
//   { kind: 'ignore' }                              — malformed frame (no pairId): do nothing.
//   { kind: 'reload' }                              — the pair is not in this snapshot (newly created
//                                                     elsewhere, or the list never loaded): a full
//                                                     loadPairs() is the only correct reaction so the
//                                                     newly-relevant pair appears.
//   { kind: 'patch', pairId, lastResult, lastRunUtc } — patch that pair's row in place. lastResult is
//                                                     always an object (empty when the frame omitted
//                                                     it); lastRunUtc is null when absent, so the
//                                                     caller leaves the existing timestamp untouched.
//
// `pairs` is the current live.pairs array (or null/undefined before the first load).
export function planPairRun(payload, pairs) {
  if (!payload || !payload.pairId) return { kind: 'ignore' };
  const id = payload.pairId;
  const list = Array.isArray(pairs) ? pairs : [];
  const has = list.some((p) => p && p.id === id);
  if (!has) return { kind: 'reload' };
  return {
    kind: 'patch',
    pairId: id,
    // The frame's lastResult is the server MirrorResult object; degrade a missing/non-object value to
    // an empty object so a patch never writes undefined onto the row.
    lastResult: (payload.lastResult && typeof payload.lastResult === 'object') ? payload.lastResult : {},
    // Only overwrite lastRunUtc when the frame actually carried one (an empty/absent value must not
    // wipe the existing timestamp). null signals "leave it as-is".
    lastRunUtc: payload.lastRunUtc ? payload.lastRunUtc : null,
  };
}
