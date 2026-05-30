// Pure web-transport mapping for the Server browser panel (mode 'web').
//
// This module has NO DOM / window / fetch dependency on purpose so it can be unit-tested in
// plain node (see tests/js-unit/). app.js imports it and wraps these pure pieces with the
// actual fetch + cookie + 401-gate plumbing.
//
// Two responsibilities:
//   1. webRequestFor(action, data) — maps a Bridge action to the same-origin REST call it
//      performs: { method, path, body? }. Returns null for device-only actions that are
//      inert in the browser panel, and for the composite 'getStatus' (which app.js builds
//      from /api/me + /api/pairs). Throws for an unmapped action.
//   2. statusFromPairs(me, pairs) — the pure composition app.js uses to synthesize an
//      AppStatus-like object for 'getStatus' from the loaded profile + pairs.

const enc = encodeURIComponent;

// Device-only actions: no meaning in a browser panel. The UI hides their entry points in
// web mode, so these resolve to inert no-ops rather than hitting the server.
export const INERT_ACTIONS = Object.freeze([
  'getAutoStart', 'setAutoStart', 'generateTxt', 'saveConfig', 'pair', 'syncNow',
]);

// Returns the REST request for a given action, or null when the action is composite
// ('getStatus') or inert (device-only). Throws on an unknown action.
export function webRequestFor(action, data) {
  switch (action) {
    case 'getStatus':       return null; // composite: app.js calls /api/me (+ /api/pairs)
    case 'listPairs':       return { method: 'GET', path: '/api/pairs' };
    case 'createPair':      return { method: 'POST', path: '/api/pairs', body: data };
    case 'updatePair': {
      const { id, ...patch } = data || {};
      return { method: 'PATCH', path: `/api/pairs/${enc(id)}`, body: patch };
    }
    case 'deletePair':      return { method: 'DELETE', path: `/api/pairs/${enc(data)}` };
    case 'runPairNow':      return { method: 'POST', path: `/api/pairs/${enc(data)}/run` };
    case 'listAccounts':    return { method: 'GET', path: '/api/accounts' };
    case 'listCalendars':   return { method: 'GET', path: `/api/accounts/${enc(data)}/calendars` };
    case 'unlinkAccount':   return { method: 'DELETE', path: `/api/accounts/${enc(data)}` };
    default:
      if (INERT_ACTIONS.includes(action)) return null;
      throw new Error(`web transport: unmapped action "${action}"`);
  }
}

// True for the device-only actions that resolve to an inert no-op in the panel.
export function isInertAction(action) {
  return INERT_ACTIONS.includes(action);
}

// Composes the AppStatus-like object the existing UI understands from the panel profile
// (/api/me) and the loaded pairs (/api/pairs). Pure: no I/O.
export function statusFromPairs(me, pairs) {
  const list = Array.isArray(pairs) ? pairs : [];
  const active = list.filter((p) => p && p.state === 'active');
  const status = list.length === 0 ? 'Idle'
    : active.length === 0 ? 'Paused' : 'Idle';
  return {
    paired: true,           // a signed-in panel session is the web "paired" state
    signedIn: true,
    email: me && me.email,
    displayName: me && me.displayName,
    status,
    pairCount: list.length,
  };
}
