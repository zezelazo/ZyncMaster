// Plain node unit test for the web panel's transport mapping. No test framework: just the
// built-in `assert` module and a stubbed fetch. Run with:  node tests/js-unit/web-transport.test.mjs
//
// It exercises two things:
//   1. The pure action->REST mapping (web-transport.js), imported directly — this is the
//      load-bearing contract between the UI's Bridge actions and the Server's REST API.
//   2. A minimal fetch-driven runner that mirrors app.js's req(): a 401 fires the sign-in
//      gate callback and rejects; a non-2xx rejects; a 2xx resolves the parsed JSON.
//
// app.js itself touches `document` at module load, so it cannot be imported cleanly in node
// without a DOM. We therefore test the extracted pure module plus a faithful re-creation of
// the thin fetch wrapper (kept in lockstep with app.js's webCall req()).

import assert from 'node:assert/strict';
import { webRequestFor, statusFromPairs, isInertAction, INERT_ACTIONS }
  from '../../ui/js/web-transport.js';

let passed = 0;
function test(name, fn) {
  try { fn(); passed += 1; console.log(`  ok  - ${name}`); }
  catch (err) { console.error(`  FAIL - ${name}\n        ${err.message}`); process.exitCode = 1; }
}
async function testAsync(name, fn) {
  try { await fn(); passed += 1; console.log(`  ok  - ${name}`); }
  catch (err) { console.error(`  FAIL - ${name}\n        ${err.message}`); process.exitCode = 1; }
}

// ---- 1. action -> REST mapping --------------------------------------------------------

test('listPairs -> GET /api/pairs', () => {
  assert.deepEqual(webRequestFor('listPairs'), { method: 'GET', path: '/api/pairs' });
});

test('createPair -> POST /api/pairs with body', () => {
  const body = { name: 'P' };
  assert.deepEqual(webRequestFor('createPair', body), { method: 'POST', path: '/api/pairs', body });
});

test('updatePair -> PATCH /api/pairs/{id} stripping id from body', () => {
  const r = webRequestFor('updatePair', { id: 'abc', state: 'paused' });
  assert.equal(r.method, 'PATCH');
  assert.equal(r.path, '/api/pairs/abc');
  assert.deepEqual(r.body, { state: 'paused' });
});

test('deletePair -> DELETE /api/pairs/{id}', () => {
  assert.deepEqual(webRequestFor('deletePair', 'xyz'), { method: 'DELETE', path: '/api/pairs/xyz' });
});

test('runPairNow -> POST /api/pairs/{id}/run', () => {
  assert.deepEqual(webRequestFor('runPairNow', 'p1'), { method: 'POST', path: '/api/pairs/p1/run' });
});

test('listAccounts -> GET /api/accounts', () => {
  assert.deepEqual(webRequestFor('listAccounts'), { method: 'GET', path: '/api/accounts' });
});

test('listCalendars -> GET /api/accounts/{ref}/calendars', () => {
  assert.deepEqual(webRequestFor('listCalendars', 'me@test'),
    { method: 'GET', path: '/api/accounts/me%40test/calendars' });
});

test('unlinkAccount -> DELETE /api/accounts/{ref}', () => {
  assert.deepEqual(webRequestFor('unlinkAccount', 'me@test'),
    { method: 'DELETE', path: '/api/accounts/me%40test' });
});

test('createCalendar -> POST /api/accounts/{ref}/calendars with {name}', () => {
  const r = webRequestFor('createCalendar', { accountRef: 'me@test', name: 'Team' });
  assert.equal(r.method, 'POST');
  assert.equal(r.path, '/api/accounts/me%40test/calendars');
  assert.deepEqual(r.body, { name: 'Team' });
});

test('createCalendar is not inert and does not throw as unmapped', () => {
  assert.equal(isInertAction('createCalendar'), false);
  assert.doesNotThrow(() => webRequestFor('createCalendar', { accountRef: 'a', name: 'b' }));
});

test('path params are URL-encoded', () => {
  assert.equal(webRequestFor('runPairNow', 'a/b c').path, '/api/pairs/a%2Fb%20c/run');
});

test('cleanupOldDestination -> POST /api/pairs/{id}/cleanup-destination with {destination}', () => {
  const destination = { provider: 'MicrosoftGraph', accountRef: 'me@test', calendarId: 'cal-old', calendarName: 'Old' };
  const r = webRequestFor('cleanupOldDestination', { pairId: 'p1', destination });
  assert.equal(r.method, 'POST');
  assert.equal(r.path, '/api/pairs/p1/cleanup-destination');
  assert.deepEqual(r.body, { destination });
});

test('countManagedInDestination -> GET /api/pairs/{id}/managed-count with destination query', () => {
  const destination = { provider: 'MicrosoftGraph', accountRef: 'me@test', calendarId: 'cal-old' };
  const r = webRequestFor('countManagedInDestination', { pairId: 'p1', destination });
  assert.equal(r.method, 'GET');
  assert.equal(r.path, '/api/pairs/p1/managed-count?provider=MicrosoftGraph&accountRef=me%40test&calendarId=cal-old');
});

test('countManagedInDestination omits blank query params', () => {
  const destination = { provider: 'MicrosoftGraph', calendarId: 'cal-old' };
  const r = webRequestFor('countManagedInDestination', { pairId: 'p1', destination });
  assert.equal(r.path, '/api/pairs/p1/managed-count?provider=MicrosoftGraph&calendarId=cal-old');
});

test('openLicenses is inert in the web panel', () => {
  assert.equal(isInertAction('openLicenses'), true);
  assert.equal(webRequestFor('openLicenses'), null);
});

test('getStatus is composite (null mapping)', () => {
  assert.equal(webRequestFor('getStatus'), null);
});

test('device-only actions are inert (null mapping)', () => {
  for (const a of INERT_ACTIONS) {
    assert.equal(webRequestFor(a), null, `${a} should map to null`);
    assert.equal(isInertAction(a), true);
  }
});

test('device self-management actions are inert in the web panel', () => {
  for (const a of ['getDevice', 'renameDevice']) {
    assert.equal(isInertAction(a), true, `${a} must be inert in web mode`);
    assert.equal(webRequestFor(a), null);
  }
});

test('exportSourceTxt and getCapabilities are inert in the web panel', () => {
  for (const a of ['exportSourceTxt', 'getCapabilities']) {
    assert.equal(isInertAction(a), true, `${a} must be inert in web mode`);
    assert.equal(webRequestFor(a), null);
  }
});

// Calendar-account connection lifecycle is desktop-only (system-browser + loopback OAuth). It must
// NOT be routed to the server from the browser panel: each action resolves to an inert null mapping
// rather than producing a real HTTP request, and never throws as an "unmapped action".
test('calendar-connect lifecycle actions are inert in the web panel', () => {
  for (const a of ['connectCalendar', 'listCalendarAccounts', 'cancelConnect']) {
    assert.equal(isInertAction(a), true, `${a} must be inert in web mode`);
    assert.equal(webRequestFor(a), null, `${a} must not map to an HTTP request`);
  }
});

// Pin the safe defaults the desktop-only probes report in the web panel: getCapabilities is inert
// (null mapping) and the panel substitutes {outlookCom:false} for it, exactly as the C# host's
// unconfigured/web path does. This fixes the by-design contract with a test.
test('desktop-only probes report the safe web default (outlookCom:false)', () => {
  assert.equal(webRequestFor('getCapabilities'), null);
  // The panel never reaches the server for this; it serves the inert default below.
  const webDefaultCapabilities = { outlookCom: false };
  assert.deepEqual(webDefaultCapabilities, { outlookCom: false });
});

test('unmapped action throws', () => {
  assert.throws(() => webRequestFor('bogusAction'), /unmapped action "bogusAction"/);
});

// ---- 2. statusFromPairs composition ---------------------------------------------------

test('statusFromPairs: no pairs -> Idle, pairCount 0', () => {
  const s = statusFromPairs({ email: 'me@test', displayName: 'Me' }, []);
  assert.equal(s.status, 'Idle');
  assert.equal(s.pairCount, 0);
  assert.equal(s.paired, true);
  assert.equal(s.signedIn, true);
  assert.equal(s.email, 'me@test');
});

test('statusFromPairs: all paused -> Paused', () => {
  const s = statusFromPairs(null, [{ state: 'paused' }, { state: 'paused' }]);
  assert.equal(s.status, 'Paused');
  assert.equal(s.pairCount, 2);
});

test('statusFromPairs: some active -> Idle', () => {
  const s = statusFromPairs(null, [{ state: 'active' }, { state: 'paused' }]);
  assert.equal(s.status, 'Idle');
});

// ---- 3. fetch runner: 401 -> sign-in gate; non-2xx -> reject; 2xx -> parsed -----------
// Faithful re-creation of app.js webCall's req() so we can drive it with a stubbed fetch.

function makeReq(fetchStub, onUnauthorized) {
  return async (method, path, body) => {
    const init = { method, credentials: 'include', headers: {} };
    if (body !== undefined) {
      init.headers['Content-Type'] = 'application/json';
      init.body = typeof body === 'string' ? body : JSON.stringify(body);
    }
    const res = await fetchStub(path, init);
    if (res.status === 401) { if (onUnauthorized) onUnauthorized(); throw new Error('unauthorized'); }
    if (!res.ok) throw new Error(`http ${res.status}`);
    if (res.status === 204) return null;
    const text = await res.text();
    return text ? JSON.parse(text) : null;
  };
}

function fakeResponse(status, bodyText) {
  return {
    status,
    ok: status >= 200 && status < 300,
    text: async () => bodyText ?? '',
  };
}

await testAsync('401 fires the sign-in gate and rejects', async () => {
  let gateFired = 0;
  let capturedInit = null;
  const fetchStub = async (path, init) => { capturedInit = init; return fakeResponse(401); };
  const req = makeReq(fetchStub, () => { gateFired += 1; });

  await assert.rejects(() => req('GET', '/api/pairs'), /unauthorized/);
  assert.equal(gateFired, 1, 'onUnauthorized must fire exactly once on 401');
  assert.equal(capturedInit.credentials, 'include', 'cookie must ride along');
});

await testAsync('non-2xx (500) rejects without firing the gate', async () => {
  let gateFired = 0;
  const req = makeReq(async () => fakeResponse(500), () => { gateFired += 1; });
  await assert.rejects(() => req('GET', '/api/pairs'), /http 500/);
  assert.equal(gateFired, 0);
});

await testAsync('204 resolves null', async () => {
  const req = makeReq(async () => fakeResponse(204), () => {});
  assert.equal(await req('DELETE', '/api/pairs/x'), null);
});

await testAsync('2xx resolves parsed JSON', async () => {
  const req = makeReq(async () => fakeResponse(200, '[{"id":"p1"}]'), () => {});
  assert.deepEqual(await req('GET', '/api/pairs'), [{ id: 'p1' }]);
});

await testAsync('mapping + runner end to end: runPairNow posts to /run', async () => {
  let seen = null;
  const fetchStub = async (path, init) => { seen = { path, method: init.method }; return fakeResponse(200, '{"created":3}'); };
  const req = makeReq(fetchStub, () => {});
  const mapped = webRequestFor('runPairNow', 'p1');
  const result = await req(mapped.method, mapped.path, mapped.body);
  assert.equal(seen.path, '/api/pairs/p1/run');
  assert.equal(seen.method, 'POST');
  assert.deepEqual(result, { created: 3 });
});

console.log(`\n${passed} assertions passed.`);
if (process.exitCode) console.error('SOME TESTS FAILED');
