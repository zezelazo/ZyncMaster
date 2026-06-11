// app.js — Zync Master UI. Vanilla ES module, no framework, no build step.
// Reimplements the design handoff (app.jsx) behaviour: launch splash, Home,
// Calendar Sync accordion, Add Pair / Add Calendar / Pairing wizards, Settings,
// About, bottom nav with sliding indicator, the sync state machine and motion.
//
// Note on innerHTML: it is used ONLY for the static, author-controlled SVG icon
// strings from icons.js (no external/user input ever flows into them). Every
// piece of dynamic text (event titles, account names, counts, device names) is
// written with textContent, so there is no injection surface even if the mock
// data — or, in the native shell, the host-supplied status — changed.

import { icon, logoSvg, microsoftLogo, hydrateIcons } from './icons.js';
import { webRequestFor, statusFromPairs } from './web-transport.js';
import { createRegistry } from './core/registry.js';
import { calendarDot, clipboardDot, devicesDot } from './core/status-model.js';
import { initPalette, registerPaletteSource } from './palette.js';
import { showToast } from './toast.js';
import { registerHomeViews } from './views/home.js';
import { registerCalendarViews } from './views/calendar.js';

const $ = (sel, root = document) => root.querySelector(sel);

// ---------------- tiny DOM builder ----------------
// el(tag, props, ...children). props.html injects a controlled icon string only.
function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v == null) continue;
      if (k === 'class') node.className = v;
      else if (k === 'text') node.textContent = v;
      else if (k === 'html') node.innerHTML = v; // controlled icon markup only
      else if (k === 'style') node.setAttribute('style', v);
      else if (k.startsWith('on') && typeof v === 'function') node.addEventListener(k.slice(2), v);
      else if (k === 'dataset') Object.assign(node.dataset, v);
      else if (v === true) node.setAttribute(k, '');
      else if (v === false) { /* skip falsy attribute */ }
      else node.setAttribute(k, v);
    }
  }
  for (const c of children.flat()) {
    if (c == null || c === false) continue;
    node.append(typeof c === 'string' || typeof c === 'number' ? document.createTextNode(String(c)) : c);
  }
  return node;
}
// iconEl(name, size, stroke) -> span wrapping a controlled icon SVG.
function iconEl(name, size, stroke) {
  return el('span', { style: 'display:inline-flex', html: icon(name, { size, stroke }) });
}

// ---------------- Platform + native-shell detection ----------------
const ua = (typeof navigator !== 'undefined' && navigator.userAgent) || '';
const isMac = /Mac/i.test(ua);
const PLATFORM = isMac ? 'mac' : 'windows'; // default windows
document.documentElement.setAttribute('data-platform', PLATFORM);

const hasWebView = typeof window !== 'undefined' && window.chrome && window.chrome.webview;
if (hasWebView) document.documentElement.classList.add('native-shell');

// ---------------- Transport (4 modes) ----------------
// 1. native    — embedded WebView2 (window.chrome.webview) -> postMessage / message event.
// 2. loopback  — the desktop App's loopback host (127.0.0.1 / localhost over http that
//                exposes /__bridge): POST /__bridge/send + long-poll GET /__bridge/poll.
// 3. web       — the Server-hosted browser panel: same-origin REST API. Bridge.call(action)
//                maps to fetch() against /api/* with credentials:'include' (cookie). This is
//                the deployed panel served by ZyncMaster.Server; it is same-origin with the API.
// 4. mock      — file:// or anything else: no bridge, the mock data above demoes standalone.
//
// native is detected synchronously (window.chrome.webview). For http(s) non-native pages we
// can't tell the App's loopback host (mode 2, serves the bundled UI + /__bridge but no REST
// API) from the Server panel (mode 3, the REST API + /health) by URL alone — both may live on
// localhost. So we probe GET /health once at boot: the Server panel answers it 200 {ok}, the
// App's loopback host has no such route and 404s it (its /__bridge/poll long-poll is a 25s
// blocking call, so it is NOT a fast discriminator). A 200 /health -> web; anything else, on a
// loopback origin, -> the App's loopback bridge. boot() awaits this resolution before the first
// data-driven render, so Bridge.available / Bridge.webPanel are stable by the time screens paint.
// Request/reply actions carry a correlationId; unsolicited {event:"status"} updates live.
const Bridge = (() => {
  const isHttp = typeof location !== 'undefined' && /^https?:$/.test(location.protocol);
  // The App's loopback host only ever binds 127.0.0.1 / localhost. On any other http(s) origin
  // (a deployed panel) the page is unambiguously the Server-hosted web panel, so we skip the
  // probe entirely and go straight to the web transport.
  const couldBeLoopback = isHttp && /^(127\.0\.0\.1|localhost)$/.test((typeof location !== 'undefined' && location.hostname) || '');

  // mode: 'native' | 'loopback' | 'web' | 'mock'. Resolved synchronously for native; for the
  // http(s) cases it defaults to 'web' and may be revised to 'loopback' by the boot probe (only
  // on a loopback origin, where the App's host could be the one serving this page).
  let mode = hasWebView ? 'native' : (isHttp ? 'web' : 'mock');

  const pending = new Map(); // correlationId -> { resolve, reject }  (native + loopback only)
  let statusCb = null;
  let unauthorizedCb = null;
  // Generic App -> UI push channel. Beyond the dedicated {event:"status"} stream, the host can
  // push named events ({event:"<name>", payload:<obj>}); listeners register per name via onEvent.
  // The clipboard viewer subscribes to "clipboard:item" so a new entry arriving over the server
  // WebSocket repaints the list live with no refresh. Multiple listeners per name are supported.
  const eventCbs = new Map(); // name -> Set<cb>
  let seq = 0;

  function newId() { return `c${Date.now()}_${seq++}`; }
  function safeParse(s) { try { return JSON.parse(s); } catch (_) { return s; } }

  function dispatchEvent(name, payload) {
    const set = eventCbs.get(name);
    if (!set) return;
    set.forEach((cb) => { try { cb(payload); } catch (_) { /* a faulty listener must not break the channel */ } });
  }

  function handleInbound(text) {
    let msg;
    try { msg = JSON.parse(text); } catch (_) { return; }
    if (msg && msg.event === 'status') { if (statusCb) statusCb(msg.payload); return; }
    // Any other named event (e.g. "clipboard:item") fans out to onEvent listeners. The host
    // serializes the payload as an OBJECT (same shape as the status push), so pass it through
    // as-is; only re-parse when it arrived as a JSON string.
    if (msg && msg.event) { dispatchEvent(msg.event, typeof msg.payload === 'string' ? safeParse(msg.payload) : msg.payload); return; }
    if (msg && msg.correlationId && pending.has(msg.correlationId)) {
      const p = pending.get(msg.correlationId);
      pending.delete(msg.correlationId);
      if (msg.ok) p.resolve(msg.payload ? safeParse(msg.payload) : null);
      else p.reject(new Error(msg.error || 'bridge error'));
    }
  }

  // send(obj) — fire a message to the host. Used both for request/reply (with a
  // correlationId) and for fire-and-forget window-control actions. Native/loopback only.
  function send(obj) {
    if (mode === 'native') { window.chrome.webview.postMessage(JSON.stringify(obj)); return; }
    if (mode === 'loopback') { fetch('/__bridge/send', { method: 'POST', body: JSON.stringify(obj) }).catch(() => {}); }
  }

  async function pollLoop() {
    // eslint-disable-next-line no-constant-condition
    while (true) {
      try {
        const res = await fetch('/__bridge/poll');
        if (res.status === 200) {
          handleInbound(await res.text());
          continue; // got a message: loop immediately to drain the next one (long-poll style).
        }
        // Any non-200 with no throw (204/404/408/empty timeout) must NOT re-enter the loop
        // immediately, or it becomes a busy-loop hammering the host. Back off briefly first.
        await new Promise((r) => setTimeout(r, 500));
      } catch (_) {
        await new Promise((r) => setTimeout(r, 1000));
      }
    }
  }

  // Probe whether THIS origin is the Server-hosted web panel (the REST API). GET /health is a
  // fast, unauthenticated 200 {status:"ok"} on the Server; the App's loopback host has no such
  // route and 404s it. Resolves true -> web transport; false -> the loopback bridge. A network
  // error resolves false (safer to assume the loopback host on a loopback origin).
  function probeIsServerPanel() {
    return new Promise((resolve) => {
      let settled = false;
      const done = (v) => { if (!settled) { settled = true; resolve(v); } };
      const t = setTimeout(() => done(false), 1500);
      fetch('/health', { method: 'GET' })
        .then((res) => res.ok ? res.text() : Promise.reject(new Error('not ok')))
        .then((body) => { clearTimeout(t); done(/"?status"?\s*:\s*"?ok/i.test(body)); })
        .catch(() => { clearTimeout(t); done(false); });
    });
  }

  // resolveTransport — settle the http(s) mode before boot paints data-driven screens. Native
  // and mock are already final. On a loopback origin the page could be the App's loopback host,
  // so we confirm via /health that it is actually the Server panel; otherwise fall to loopback.
  // On any non-loopback http(s) origin it is unambiguously the web panel — no probe.
  async function resolveTransport() {
    if (mode !== 'web') return mode;
    if (couldBeLoopback && !(await probeIsServerPanel())) mode = 'loopback';
    return mode;
  }

  function start() {
    if (mode === 'native') {
      window.chrome.webview.addEventListener('message', (e) => {
        const data = typeof e.data === 'string' ? e.data : JSON.stringify(e.data);
        handleInbound(data);
      });
    } else if (mode === 'loopback') {
      pollLoop();
    }
    // web + mock have no inbound channel.
  }

  // ---- web transport: action -> REST mapping ----
  // Every action the UI invokes through Bridge.call maps to a same-origin REST call with the
  // session cookie. A non-2xx rejects (the UI's .catch surfaces an error); a 401 also fires
  // the sign-in gate. Device-only actions (generateTxt / setAutoStart / getAutoStart / pair /
  // syncNow / saveConfig) are not reachable from the browser panel — the UI hides their
  // affordances in web mode — so they resolve to inert no-ops rather than hitting the server.
  async function webCall(action, payload) {
    const data = payload == null ? null : safeParse(String(payload));

    const req = async (method, path, body) => {
      const init = { method, credentials: 'include', headers: {} };
      if (body !== undefined) {
        init.headers['Content-Type'] = 'application/json';
        init.body = typeof body === 'string' ? body : JSON.stringify(body);
      }
      const res = await fetch(path, init);
      if (res.status === 401) { if (unauthorizedCb) unauthorizedCb(); throw new Error('unauthorized'); }
      if (!res.ok) throw new Error(`http ${res.status}`);
      if (res.status === 204) return null;
      const text = await res.text();
      return text ? safeParse(text) : null;
    };

    // getStatus is composite: compose an AppStatus-like object from /api/me (+ /api/pairs for
    // an overall state) that the existing applyNativeStatus()/UI already understand.
    if (action === 'getStatus') {
      const me = await req('GET', '/api/me');
      let pairs = [];
      try {
        pairs = await req('GET', '/api/pairs');
      } catch (err) {
        // A 401 here means the session expired between /api/me and /api/pairs: do NOT swallow it
        // and report a signed-in panel with 0 pairs. Re-throw so the sign-in gate fires
        // deterministically (req() already invoked unauthorizedCb). Only a non-auth error (e.g. a
        // transient 500) degrades to an empty pair list.
        if (err && err.message === 'unauthorized') throw err;
        pairs = [];
      }
      return statusFromPairs(me, pairs);
    }

    // Everything else maps through the pure action->REST table. A null mapping is a device-
    // only action that is inert in the browser panel (getAutoStart reports disabled; the
    // rest are no-ops) — the UI hides their entry points in web mode anyway.
    const mapped = webRequestFor(action, data);
    if (mapped === null) {
      if (action === 'getAutoStart') return { enabled: false };
      // The browser panel is not a local device: no Outlook COM, so COM affordances stay off.
      if (action === 'getCapabilities') return { outlookCom: false };
      return null;
    }
    return req(mapped.method, mapped.path, mapped.body);
  }

  function call(action, payload, timeoutMs) {
    if (mode === 'web') return webCall(action, payload);
    if (mode === 'mock') return Promise.reject(new Error('no bridge'));
    // native / loopback: correlationId request-reply over the message channel.
    const correlationId = newId();
    return new Promise((resolve, reject) => {
      pending.set(correlationId, { resolve, reject });
      send({ action, correlationId, payload: payload == null ? null : String(payload) });
      setTimeout(() => {
        if (pending.has(correlationId)) { pending.delete(correlationId); reject(new Error('bridge timeout')); }
      }, timeoutMs || 60000);
    });
  }

  // Fire-and-forget window controls. Only meaningful in the native shell; inert elsewhere.
  function windowAction(action) { if (mode === 'native') send({ action }); }

  return {
    // available — the UI has a real data backend (any non-mock transport).
    get available() { return mode !== 'mock'; },
    get nativeShell() { return mode === 'native'; },
    // webPanel — the browser panel (mode 3). The UI uses this to hide device-only affordances
    // and to apply the sign-in gate, both of which are panel-only concerns.
    get webPanel() { return mode === 'web'; },
    // desktopApp — the desktop App shell (native WebView2 or the loopback host). This is the
    // transport that owns the IDENTITY sign-in gate (Microsoft / magic-link via getIdentityState
    // + login + signOut). The web panel keeps its own /connect gate; mock has no bridge.
    get desktopApp() { return mode === 'native' || mode === 'loopback'; },
    get mode() { return mode; },
    resolveTransport, start, call, windowAction,
    onStatus(cb) { statusCb = cb; },
    onUnauthorized(cb) { unauthorizedCb = cb; },
    // onEvent(name, cb) — subscribe to a named App -> UI push. Returns an unsubscribe function.
    onEvent(name, cb) {
      let set = eventCbs.get(name);
      if (!set) { set = new Set(); eventCbs.set(name, set); }
      set.add(cb);
      return () => { const s = eventCbs.get(name); if (s) s.delete(cb); };
    },
  };
})();

// ---------------- Theme (Dark / Light / Auto) ----------------
const THEME_KEY = 'zyncmaster.theme';
const mql = typeof window !== 'undefined' && window.matchMedia
  ? window.matchMedia('(prefers-color-scheme: light)') : null;

function storedTheme() {
  try { return localStorage.getItem(THEME_KEY) || 'auto'; } catch (_) { return 'auto'; }
}
// resolve auto -> the concrete dark/light the OS asks for.
function resolveTheme(mode) {
  if (mode === 'dark' || mode === 'light') return mode;
  return mql && mql.matches ? 'light' : 'dark';
}
function applyTheme(mode) {
  document.documentElement.setAttribute('data-theme', resolveTheme(mode));
  try { localStorage.setItem(THEME_KEY, mode); } catch (_) {}
}
if (mql && mql.addEventListener) {
  mql.addEventListener('change', () => { if (storedTheme() === 'auto') applyTheme('auto'); });
}

// ---------------- App state ----------------
// Product version shown in About + the Settings "About" row. Hardcoded because the web UI has
// no channel to read the host's .NET assembly version; keep it in step with the published
// release (currently 0.3.3, beta).
const VERSION = '0.3.3';
const state = {
  view: 'home',          // home | calendar | add-pair | add-calendar | config | about | pairing
  returnTo: 'calendar',  // where add-calendar returns to
  sync: 'ok',            // ok | syncing | success | error | offline | unpaired | paused
  progress: { done: 0, total: 20 },
};

const registry = createRegistry();

// ---------------- ARIA ----------------
function announce(msg) { const live = $('#liveRegion'); if (live) live.textContent = msg; }

function fmtMMSS(s) {
  s = Math.max(0, Math.floor(s));
  const m = String(Math.floor(s / 60)).padStart(2, '0');
  const sec = String(s % 60).padStart(2, '0');
  return `${m}:${sec}`;
}

// ---------------- Mock data (ported from app.jsx) ----------------
const SERVICE_TONE = { Outlook: 'azure', Gmail: 'warn', iCloud: 'ink', Work: 'terra' };
const SERVICE_SHORT = { Outlook: 'OU', Gmail: 'GM', iCloud: 'IC', Work: 'WK' };
function svcTone(svc) { return SERVICE_TONE[svc] || 'ink'; }
function svcShort(svc) { return SERVICE_SHORT[svc] || svc.slice(0, 2).toUpperCase(); }


const PAIRS = [
  {
    id: 'p1',
    src: { svc: 'Outlook', acct: 'Personal',      email: 'daniel@outlook.com' },
    dst: { svc: 'Outlook', acct: 'Work Calendar', email: 'd.lopez@acme.com' },
    state: 'ok', lastSync: '2 min ago', nextSync: 297, total: 24, eventCount: 18,
    events: [
      { time: '10:42', title: 'Q3 Strategy review', sub: 'Mon 28 · 10:00–11:00', action: 'created' },
      { time: '10:42', title: 'Coffee w/ Maya',     sub: 'Tue 29 · 09:00–09:30', action: 'updated' },
      { time: '10:42', title: 'Design crit',        sub: 'Wed 30 · 14:00–15:30', action: 'updated' },
      { time: '10:28', title: 'Dentist',            sub: 'Past event',           action: 'skipped' },
      { time: '10:28', title: '1:1 with Sam',       sub: 'Thu 1 · 11:00',   action: 'deleted' },
    ],
  },
  {
    id: 'p2',
    src: { svc: 'Gmail',  acct: 'daniel.lopez', email: 'daniel.lopez@gmail.com' },
    dst: { svc: 'iCloud', acct: 'Family',       email: 'daniel@icloud.com' },
    state: 'ok', lastSync: '5 min ago', nextSync: 420, total: 12, eventCount: 12,
    events: [
      { time: '10:39', title: 'Yoga · Tuesday', sub: 'Tue 29 · 07:30–08:30', action: 'created' },
      { time: '10:39', title: 'School pickup',  sub: 'Daily · 15:30',        action: 'updated' },
      { time: '09:48', title: 'Movie night',    sub: 'Fri 2 · 20:00',        action: 'created' },
    ],
  },
  {
    id: 'p3',
    src: { svc: 'Work',   acct: 'Team Calendar', email: 'team@acme.com' },
    dst: { svc: 'iCloud', acct: 'Mirror',        email: 'daniel@icloud.com' },
    state: 'ok', lastSync: '8 min ago', nextSync: 540, total: 30, eventCount: 22,
    events: [
      { time: '10:43', title: 'Sprint planning', sub: 'Mon 28 · 13:00–14:00', action: 'created' },
      { time: '10:43', title: 'Design review',   sub: 'Tue 29 · 11:00–12:00', action: 'updated' },
    ],
  },
];

// ---------------- Live data (bridge) with mock fallback ----------------
// When the native bridge is available every screen reads real data through these
// helpers; in a plain browser they return the mock data above so ui/index.html still
// opens and demoes standalone. The caches let synchronous render functions paint the
// latest snapshot while a refresh runs in the background.
const live = {
  pairs: null,       // [SyncPair] from listPairs
  accounts: null,    // [AccountInfo] from listAccounts
  calendars: {},     // accountRef -> [CalendarInfo]
  localCalendars: null,      // [string] local Outlook (COM) display names from listLocalCalendars (Feature 2)
  localCalendarsLoading: false,
  localCalendarsError: false,
  autoStart: null,   // bool from getAutoStart
  me: null,          // { email, displayName } from getStatus/api/me (web panel)
  device: null,      // { deviceId, name, platform } from getDevice (desktop App, lazy-loaded)
  deviceLoading: false,  // a getDevice call is in flight
  loadedPairs: false,
  loadingPairs: false,
  pairsAttempted: false,  // web panel: home auto-loads pairs at most once (a 401 must not loop)
  // Device capabilities (getCapabilities), loaded once at boot. Until it resolves we ASSUME
  // COM is unavailable (the safer default: COM affordances stay disabled until confirmed).
  capabilities: { outlookCom: false },
  capabilitiesLoaded: false,
  // ---- Per-pair sync runtime (client-only, this session) ----
  // syncing      — Set of pair ids with a sync in flight. Single-flight: a click while the id is
  //                present is ignored, so the user cannot stack dozens of concurrent runs.
  // attempts     — pair id -> number of sync attempts triggered this session (incremental badge).
  // eventLog     — pair id -> array of { ts, ok, action, title, sub, msg } (newest first, capped).
  //                Drives the per-pair "Recent events" list so EVERY attempt (ok or fail) is
  //                visible inline instead of a popup.
  syncing: new Set(),
  attempts: {},
  eventLog: {},
  // Clipboard module: { thisDeviceId, devices: [...] } from getClipboardDevices. Lazy-loaded the
  // first time the Clipboard settings screen (or the hub summary) needs it.
  clipboardDevices: null,
  clipboardDevicesLoading: false,
  // Clipboard dashboard VIEW (the shared history with copy/delete), distinct from the devices roster
  // above. Lazy-loaded the first time the in-app clipboard screen opens; the live "clipboard:item" /
  // "clipboard:deleted" pushes keep it fresh while the screen is open.
  clipboardHistory: null,
  clipboardHistoryLoading: false,
};

// Cap on per-pair retained client events (newest kept). Keeps memory bounded over a long session.
const PAIR_EVENT_LOG_MAX = 20;

// Push a client-side event onto a pair's session log (newest first, capped). entry carries
// { ok, action, title, sub, msg }; the timestamp is stamped here.
function pushPairEvent(id, entry) {
  if (!id) return;
  const now = new Date();
  const log = live.eventLog[id] || (live.eventLog[id] = []);
  log.unshift({
    ts: now.getTime(),
    time: now.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }),
    ...entry,
  });
  if (log.length > PAIR_EVENT_LOG_MAX) log.length = PAIR_EVENT_LOG_MAX;
}

// True only once getCapabilities has confirmed Outlook Classic COM is present on this device.
// Before that (and on the web panel) it is false, so COM-only affordances stay disabled.
function comAvailable() {
  return !!(live.capabilities && live.capabilities.outlookCom);
}

// Web panel sign-in gate state. Only consulted in web mode (Bridge.webPanel). signedIn flips
// true once getStatus resolves; a 401 from any web call flips it false and shows the gate.
const webAuth = { resolved: false, signedIn: false };

// Desktop App identity gate state. Only consulted in native/loopback (Bridge.desktopApp). The
// App boot calls getIdentityState; isSignedIn === false shows the identity sign-in screen
// (Microsoft / magic-link) before the dashboard. `loading` is set while a login() is in flight;
// `error` holds the last login failure message; `magicLinkSent` is true after a magic-link
// request so the screen shows the "check your email" state while the loopback round-trip
// completes the sign-in on its own.
const identityAuth = {
  resolved: false,    // getIdentityState has answered at least once
  signedIn: false,    // isSignedIn from getIdentityState
  me: null,           // { userId, email, displayName, expiresAt, plan }
  loading: false,     // a login() request is in flight
  error: null,        // last login error message
  magicLinkSent: false, // a magic-link was requested; awaiting the email round-trip
  magicLinkEmail: '',   // the address the magic-link was sent to (shown in the sent state)
  slowHint: false,      // login has been in flight long enough to hint at a network/proxy reset
};

// How long a Microsoft login may sit "waiting for the browser" before we surface the
// ERR_CONNECTION_RESET hint (common on corporate networks that reset the first OAuth connection).
const LOGIN_SLOW_HINT_MS = 50000;
let loginSlowTimer = null;
function startLoginSlowTimer() {
  clearTimeout(loginSlowTimer);
  identityAuth.slowHint = false;
  loginSlowTimer = setTimeout(() => {
    if (identityAuth.loading && !identityAuth.signedIn) { identityAuth.slowHint = true; rerender(); }
  }, LOGIN_SLOW_HINT_MS);
}
function clearLoginSlowTimer() {
  clearTimeout(loginSlowTimer);
  loginSlowTimer = null;
  identityAuth.slowHint = false;
}

// Desktop App server warm-up gate state (Bridge.desktopApp only; web/mock skip it). The Azure F1
// free tier cold-starts, so the first request after idle can take ~30-60s. At boot we ping
// {ServerBaseUrl}/health and keep this gate IN FRONT of the identity gate until the server is
// alive. Kept separate from identityAuth on purpose so the two gates compose without one
// rewriting the other.
//   checked  — the first probe has answered at least once (controls whether the gate shows).
//   ok       — the server answered /health; the identity gate / dashboard takes over.
//   waking   — the probe is mid-flight or the server is cold-starting; spinner + "waking up".
//   error    — the retry budget ran out without a healthy answer; friendly error + Retry.
//   attempts — probes made in the current cycle (drives the retry budget).
const serverHealth = {
  checked: false,
  ok: false,
  waking: false,
  error: false,
  attempts: 0,
};

// Warm-up tuning: one probe every ~5s, up to ~60s total, to cover an Azure F1 cold start. The
// per-attempt timeout on the host side is ~8s, so MAX_ATTEMPTS * interval comfortably spans 60s.
const HEALTH_POLL_MS = 5000;
const HEALTH_MAX_ATTEMPTS = 12;
let healthPoll = null;

// Probes {ServerBaseUrl}/health once and updates serverHealth. On "ok" it clears the poll and
// hands off to the identity gate (which boot kicks once the server is alive). On "unconfigured"
// it lets the UI fall straight through to Settings. Otherwise it schedules the next attempt
// until the budget is spent, then surfaces the friendly error + Retry.
function probeServerHealth() {
  if (!Bridge.desktopApp) return;
  serverHealth.attempts += 1;
  serverHealth.waking = serverHealth.attempts > 1; // first probe shows "Connecting…", later ones "Waking up…"
  serverHealth.error = false;
  if (!serverHealth.checked && serverHealth.attempts === 1) rerender();

  Bridge.call('checkServerHealth', null, 12000)
    .then((h) => {
      const status = (h && h.status) || 'unreachable';
      if (status === 'ok' || status === 'unconfigured') {
        serverHealth.checked = true;
        serverHealth.ok = true; // "unconfigured" also clears the gate so the user can reach Settings
        serverHealth.waking = false;
        serverHealth.error = false;
        clearHealthPoll();
        onServerReady();
        rerender();
        return;
      }
      // "waking" / "unreachable": keep trying until the budget is spent.
      scheduleNextHealthProbe();
    })
    .catch(() => { scheduleNextHealthProbe(); });
}

function scheduleNextHealthProbe() {
  if (serverHealth.ok) return;
  if (serverHealth.attempts >= HEALTH_MAX_ATTEMPTS) {
    // Budget spent: stop polling and show the friendly error with a Retry button.
    serverHealth.checked = true;
    serverHealth.waking = false;
    serverHealth.error = true;
    clearHealthPoll();
    rerender();
    return;
  }
  serverHealth.checked = true;
  serverHealth.waking = true;
  rerender();
  clearHealthPoll();
  healthPoll = setTimeout(probeServerHealth, HEALTH_POLL_MS);
}

function clearHealthPoll() {
  if (healthPoll) { clearTimeout(healthPoll); healthPoll = null; }
}

// Server is alive (or unconfigured): hand off to the IDENTITY gate. Resolves getIdentityState so
// the sign-in screen or the dashboard paints. Marks the gate resolved even on failure so a probe
// error lands on the sign-in screen rather than an empty dashboard. Mirrors the pre-warmup boot
// behaviour, just deferred until the server has woken.
function onServerReady() {
  if (!Bridge.desktopApp || identityAuth.resolved) return;
  Bridge.call('getIdentityState')
    .then((s) => {
      const signedIn = !!(s && s.isSignedIn);
      identityAuth.resolved = true;
      identityAuth.signedIn = signedIn;
      identityAuth.me = signedIn
        ? { userId: s.userId, email: s.email, displayName: s.displayName, expiresAt: s.expiresAt, plan: s.plan }
        : null;
      // Already signed in at boot: make sure the device is registered (has its api key) so the
      // background scheduler / heartbeat / Sync-now work without waiting for a manual sync.
      if (signedIn) ensureDeviceRegistered();
      rerender();
    })
    .catch(() => { identityAuth.resolved = true; identityAuth.signedIn = false; rerender(); });
}

// ensureDeviceRegistered — fire-and-forget request that the host auto-register THIS device against
// the server using the signed-in identity, so it has a device api key before any sync runs. The
// host call is idempotent (a no-op when a key already exists or no identity is present), so calling
// it on every sign-in / boot-when-signed-in is safe. Errors are swallowed: a failure just means the
// host's self-heal retries on the next device-key-gated call (e.g. Sync now).
function ensureDeviceRegistered() {
  if (!Bridge.desktopApp) return;
  Bridge.call('ensureDevice').catch(() => {});
}

// Track B — lazy-load THIS device's record (id + name) once into live.device, the SAME cache the
// Settings hub fills. Used by the calendar dashboard to resolve COM-pinned pairs as local vs remote.
// Guarded by the existing live.deviceLoading flag so it never races ensureConfigData. On success it
// triggers a soft repaint so the pin badge appears without waiting for the next user interaction.
function ensureLocalDevice() {
  if (!Bridge.available) return;
  if (live.device !== null || live.deviceLoading) return;
  live.deviceLoading = true;
  Bridge.call('getDevice')
    .then((d) => { live.device = d || {}; if (d && d.name) settings.deviceName = d.name; })
    .catch(() => { live.device = {}; })
    .finally(() => {
      live.deviceLoading = false;
      if (state.view === 'calendar') softRepaint();
    });
}

// User-driven Retry from the error screen: reset the budget and probe again.
function retryServerHealth() {
  clearHealthPoll();
  serverHealth.attempts = 0;
  serverHealth.error = false;
  serverHealth.ok = false;
  serverHealth.waking = false;
  serverHealth.checked = false;
  probeServerHealth();
}

async function loadPairs() {
  if (!Bridge.available) { live.pairs = null; return PAIRS; }
  live.loadingPairs = true;
  try {
    const list = await Bridge.call('listPairs');
    live.pairs = Array.isArray(list) ? list : [];
    live.loadedPairs = true;
    // Track B — the dashboard needs THIS device's id to tell "Source is on this PC" from "Runs on
    // <device>" for COM-pinned pairs. Lazy-load it once alongside the pairs (the Settings hub also
    // loads it, but the calendar view renders first), best-effort; a failure just hides the badge.
    ensureLocalDevice();
    // Best-effort: resolve descriptive calendar names for pair endpoints by warming the account +
    // calendar lists the wizard already caches in live.accounts / live.calendars. Without these the
    // destination would have only a raw calendarId; with them resolveCalendarLabel finds the real
    // display name. Fire-and-forget; a failure just leaves the legible fallback in place.
    ensurePairCalendarNames();
    return live.pairs;
  } catch (_) {
    live.pairs = live.pairs || [];
    return live.pairs;
  } finally {
    live.loadingPairs = false;
  }
}

// Warm the account + calendar caches for every online endpoint referenced by the loaded pairs, so
// the dashboard can show a calendar's display name instead of its raw id. Runs at most once per set
// of referenced accounts (guarded by live.calendars[ref] already being present). Best-effort and
// fire-and-forget; on success it repaints the calendar view so resolved names replace the fallback.
let pairCalendarNamesPending = false;
function ensurePairCalendarNames() {
  if (!Bridge.available || pairCalendarNamesPending) return;
  const refs = new Set();
  (live.pairs || []).forEach((p) => {
    [p.source, p.destination].forEach((e) => {
      if (e && (e.provider || '').toLowerCase() !== 'outlookcom' && e.accountRef) refs.add(e.accountRef);
    });
  });
  // Only the accounts whose calendar list we have not already cached need a fetch.
  const missing = [...refs].filter((ref) => !Array.isArray(live.calendars[ref]));
  if (missing.length === 0) return;
  pairCalendarNamesPending = true;
  const work = (live.accounts ? Promise.resolve(live.accounts) : loadAccounts())
    .then(() => Promise.all(missing.map((ref) => loadCalendars(ref).catch(() => null))));
  work
    .then(() => { if (state.view === 'calendar') rerenderInPlace(); })
    .catch(() => {})
    .finally(() => { pairCalendarNamesPending = false; });
}

async function loadAccounts() {
  if (!Bridge.available) return null;
  try {
    const list = await Bridge.call('listAccounts');
    live.accounts = Array.isArray(list) ? list : [];
    return live.accounts;
  } catch (_) {
    live.accounts = live.accounts || [];
    return live.accounts;
  }
}

async function loadCalendars(accountRef) {
  if (!Bridge.available || !accountRef) return null;
  try {
    const list = await Bridge.call('listCalendars', accountRef);
    live.calendars[accountRef] = Array.isArray(list) ? list : [];
    return live.calendars[accountRef];
  } catch (_) {
    live.calendars[accountRef] = live.calendars[accountRef] || [];
    return live.calendars[accountRef];
  }
}

// Feature 2 — enumerate this device's LOCAL Outlook (COM) calendars (display-name strings) for the
// wizard's COM source multi-select. COM-only; a failure (no Outlook / launch error) sets the error
// flag so the picker can degrade to "all calendars" with a notice instead of throwing.
async function loadLocalCalendars() {
  if (!Bridge.available) return null;
  live.localCalendarsLoading = true;
  live.localCalendarsError = false;
  try {
    // Cold Outlook can take well over the 60s default to enumerate local calendars over COM.
    const list = await Bridge.call('listLocalCalendars', null, COM_SLOW_TIMEOUT_MS);
    live.localCalendars = Array.isArray(list) ? list : [];
    return live.localCalendars;
  } catch (_) {
    live.localCalendars = [];
    live.localCalendarsError = true;
    return live.localCalendars;
  } finally {
    live.localCalendarsLoading = false;
  }
}

// True when a string looks like a raw calendar id (a long opaque token), not a human display
// name. Graph calendar ids are long base64url/hex blobs with no spaces; a real display name almost
// always has a space or is short. Used to avoid ever showing the bare GUID as the primary label.
function looksLikeRawId(s) {
  if (!s) return false;
  const t = String(s);
  if (t.includes(' ')) return false;            // display names have spaces ("Calendar", "Work")
  return t.length >= 24 && /^[A-Za-z0-9_=+/-]+$/.test(t);
}

// Resolve a friendly calendar display name for an endpoint, NEVER returning the raw calendarId as
// the primary label. Order: the endpoint's own calendarName (when it is a real name) → a match in
// the already-loaded calendar list for that account (the wizard caches these in live.calendars) →
// the account's display name as a legible fallback → a generic "Calendar". The raw id is only ever
// used to look up a name, never shown verbatim.
function resolveCalendarLabel(e) {
  const name = e.calendarName;
  if (name && !looksLikeRawId(name)) return name;
  const ref = e.accountRef;
  const id = e.calendarId;
  if (ref && id) {
    const cals = live.calendars[ref];
    if (Array.isArray(cals)) {
      const hit = cals.find((c) => c && c.id === id);
      if (hit && (hit.displayName || hit.name)) return hit.displayName || hit.name;
    }
  }
  if (ref) {
    const acc = (live.accounts || []).find((a) => a && a.accountRef === ref);
    if (acc && (acc.displayName || acc.email)) return acc.displayName || acc.email;
  }
  // Last resort: never the GUID — a generic, legible label.
  return 'Calendar';
}

// Map a SyncPair (server shape) into the accordion view-model the renderer expects.
function pairViewModel(p) {
  const endpointLabel = (e, isSource) => {
    if (!e) return { svc: 'Outlook', acct: '', email: '' };
    const com = (e.provider || '').toLowerCase() === 'outlookcom';
    // Feature 2 — a source mirroring multiple calendars shows a count/"All calendars" label rather
    // than a single calendar name. The destination is always one calendar, so it keeps its name.
    // Destination / single-calendar source: resolve a DESCRIPTIVE name, never the raw calendarId.
    let acct = com ? 'Outlook (this PC)' : resolveCalendarLabel(e);
    if (isSource) {
      const ids = Array.isArray(e.calendarIds) ? e.calendarIds : [];
      const names = Array.isArray(e.calendarNames) ? e.calendarNames : [];
      const count = com ? names.length : ids.length;
      if (e.allCalendars) acct = 'All calendars';
      else if (count > 1) acct = `${count} calendars`;
    }
    return {
      svc: com ? 'Outlook' : 'Outlook',
      acct,
      email: com ? 'Local Outlook' : (e.accountRef || ''),
      provider: e.provider || '',
    };
  };
  const lr = p.lastResult;
  // Last-run summary rows from the server's MirrorResult (counts only). Shown when the user has not
  // triggered any sync this session yet, so the card is not empty on first open.
  const lastRunRows = lr
    ? [
        lr.created ? { time: '', title: `${lr.created} created`, sub: 'Last run', action: 'created' } : null,
        lr.updated ? { time: '', title: `${lr.updated} updated`, sub: 'Last run', action: 'updated' } : null,
        lr.deleted ? { time: '', title: `${lr.deleted} deleted`, sub: 'Last run', action: 'deleted' } : null,
        lr.skipped ? { time: '', title: `${lr.skipped} skipped`, sub: 'Last run', action: 'skipped' } : null,
      ].filter(Boolean)
    : [];
  const total = lr ? (lr.created + lr.updated + lr.deleted + lr.skipped) : 0;
  // Session event log for this pair (every attempt, ok + fail, newest first). This is what RECENT
  // EVENTS renders so the user can read each result inline. Falls back to the last-run summary when
  // there is no session activity yet.
  const sessionLog = (live.eventLog[p.id] || []).slice();
  const events = sessionLog.length ? sessionLog : lastRunRows;
  const inFlight = live.syncing.has(p.id);
  const attempts = live.attempts[p.id] || 0;

  // Track B — COM device-pinning. A pair whose SOURCE is OutlookCom is read on exactly one device
  // (the pinned origin). myDeviceId is this device's id (live.device, lazy-loaded). We classify the
  // pair as:
  //   comLocal    — this device IS the pinned origin (or the pin is not set yet AND this device can
  //                 read COM): "Sync now" runs locally as before.
  //   comRemote   — the source lives on another device: "Sync now" signals that device instead.
  //   comOffline  — comRemote AND the server reports the pinned device's lease as expired.
  //   comUnclaimed— the pin is not set yet AND this device CANNOT read COM (no Outlook here): there is
  //                 no origin device for this pair yet, so we must NOT claim "Source is on this PC" and
  //                 must not offer a local run (it would fail). Neutral "no source device yet" state.
  const srcCom = ((p.source && p.source.provider) || '').toLowerCase() === 'outlookcom';
  const myDeviceId = (live.device && live.device.deviceId) || '';
  const pinnedDeviceId = p.pinnedDeviceId || '';
  // A null-pin COM pair only counts as "local" when THIS device can actually read Outlook COM. On a
  // device without COM (e.g. the web panel, or a machine with no Outlook) an unpinned pair has no
  // origin yet — claiming it as local would offer a local run that immediately fails.
  const comLocal = srcCom && (
    (myDeviceId && pinnedDeviceId === myDeviceId)
    || (!pinnedDeviceId && comAvailable())
  );
  const comRemote = srcCom && !!pinnedDeviceId && (!myDeviceId || pinnedDeviceId !== myDeviceId);
  const comOffline = comRemote && p.pinnedDeviceOnline === false;
  const comUnclaimed = srcCom && !pinnedDeviceId && !comAvailable();

  return {
    id: p.id,
    name: p.name,
    serverState: p.state, // active | paused | disabled
    src: endpointLabel(p.source, true),
    dst: endpointLabel(p.destination, false),
    state: p.state === 'active' ? 'ok' : 'paused',
    lastSync: p.lastRunUtc ? new Date(p.lastRunUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '—',
    nextSync: (p.intervalMin || 10) * 60,
    total,
    eventCount: total,
    events,
    // Per-pair sync runtime (client session).
    inFlight,
    attempts,
    // COM device-pinning (Track B).
    srcCom,
    comLocal,
    comRemote,
    comOffline,
    comUnclaimed,
    pinnedDeviceId,
    pinnedDeviceName: p.pinnedDeviceName || '',
    pinnedDeviceOnline: p.pinnedDeviceOnline === true,
  };
}

// ---------------- Shared fragments ----------------
function pairBadge(svc) {
  return el('span', { class: 'pair-badge', dataset: { tone: svcTone(svc) }, text: svcShort(svc) });
}
function viewHeader(title, opts = {}) {
  const { onBack, action } = opts;
  const back = onBack
    ? el('button', { class: 'view-header__back', 'aria-label': 'Back', onclick: onBack }, iconEl('chevronleft', 18, 1.8))
    : el('span', { style: 'width:4px' });
  return el('div', { class: 'view-header' },
    back,
    el('div', { class: 'view-header__title', text: title }),
    el('div', { class: 'view-header__action' }, action || ''),
  );
}
// navRow(opts) — a clickable settings row that navigates elsewhere. label + optional sublabel
// on the left; an optional value plus a right-pointing chevron on the right, all vertically
// centred (flex align-items:center, see .nav-row in components.css). The chevron reuses the
// existing chevronleft icon rotated 180°, so no new icon asset is needed. Used for the Calendar
// and About entries in the Settings hub, fixing the previously misaligned About chevron.
function navRow({ label, sublabel, value, onClick }) {
  const left = el('div', { class: 'nav-row__text' },
    el('div', { class: 'nav-row__label', text: label }),
    sublabel ? el('div', { class: 'nav-row__sub', text: sublabel }) : null);
  const right = el('div', { class: 'nav-row__right' },
    value != null && value !== '' ? el('span', { class: 'nav-row__value num', text: String(value) }) : null,
    el('span', { class: 'nav-row__chevron', html: icon('chevronleft', { size: 16, stroke: 1.8 }) }));
  return el('button', { class: 'nav-row', type: 'button', onclick: onClick }, left, right);
}
function actionChip(kind) {
  const map = {
    created: ['chip--created', 'Created'], updated: ['chip--updated', 'Updated'],
    deleted: ['chip--deleted', 'Deleted'], skipped: ['chip--skipped', 'Skipped'],
    // Session-log outcomes: a whole sync attempt's result.
    ok: ['chip--ok', 'OK'], failed: ['chip--deleted', 'Failed'], requested: ['chip--created', 'Requested'],
    // Benign/info outcome (e.g. a scheduled run was already in progress, so the manual one was
    // skipped). Neutral azure chip — NOT a failure.
    info: ['chip--info', 'Info'],
  };
  const [cls, txt] = map[kind] || map.skipped;
  return el('span', { class: `chip ${cls}`, text: txt });
}
function activityRow(row) {
  return el('div', { class: 'activity__row' },
    el('span', { class: 'activity__time num', text: row.time }),
    el('div', { style: 'min-width:0' },
      el('div', { class: 'activity__title', text: row.title }),
      el('div', { class: 'activity__sub', text: row.sub }),
    ),
    actionChip(row.action),
  );
}

// Open-accordion state for the Calendar pair list. Lives here (not in views/calendar.js) because
// deletePair() — a shared runtime action — clears the deleted pair's id; the calendar module reads
// and toggles it through ctx.
const openPairs = new Set(['p1']);

// ---------------- Screen: Settings ----------------
// deviceName starts empty; the "Daniel's MacBook" demo name is applied at RENDER time only in
// mock mode (see renderConfig). It must NOT be decided here at module load: the loopback
// transport (the desktop App) resolves Bridge.available asynchronously after boot's /health
// probe, so reading Bridge.available now would still be false and would wrongly seed the demo
// name into the real App. Deciding in the render keeps the value impersonal for any real shell.
const settings = { autoSync: true, startup: true, interval: 15, windowDays: 14, deviceName: '', pastePanelOpacity: 70 };

function toggle(get, set) {
  const t = el('div', { class: 'toggle', role: 'switch', 'aria-checked': String(get()), tabindex: '0' });
  const flip = () => { const v = !get(); set(v); t.setAttribute('aria-checked', String(v)); pushConfig(); };
  t.addEventListener('click', flip);
  t.addEventListener('keydown', (e) => { if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); flip(); } });
  return t;
}

// Shared section/row builders for the settings screens.
function cfgSection(head, ...rows) {
  const s = el('div', { class: 'glass glass--card config-section' }, el('div', { class: 'config-section__hd', text: head }));
  rows.flat().forEach((r) => { if (r) s.append(r); });
  return s;
}
function cfgRow(label, hint, control) {
  return el('div', { class: 'cfg-row' },
    el('div', null, el('div', { class: 'cfg-row__label', text: label }), hint || null), control);
}

// ensureConfigData — coalesced first-load for the Settings hub. Fires every lazy fetch the hub
// needs (device, auto-start, connected accounts + their calendars) in one batch and triggers a
// SINGLE softRepaint when the whole batch settles, instead of each fetch repainting on its own.
// That stops the first open of Settings from flickering as each promise lands. Guarded so the batch
// runs at most once per session (configDataLoaded); nothing here re-fetches data already cached.
let configDataLoaded = false;
let configDataLoading = false;
function ensureConfigData() {
  if (!Bridge.available || configDataLoaded || configDataLoading) return;
  configDataLoading = true;

  const jobs = [];

  // Device record (desktop App). Cached on live.device.
  if (live.device === null && !live.deviceLoading) {
    live.deviceLoading = true;
    jobs.push(Bridge.call('getDevice')
      .then((d) => { live.device = d || {}; if (d && d.name) settings.deviceName = d.name; })
      .catch(() => { live.device = {}; })
      .finally(() => { live.deviceLoading = false; }));
  }

  // Auto-start preference (desktop App). Cached on live.autoStart.
  if (live.autoStart === null) {
    jobs.push(Bridge.call('getAutoStart')
      .then((r) => { live.autoStart = !!(r && r.enabled); })
      .catch(() => {}));
  }

  // Connected accounts + each account's calendars (desktop App only; the web panel has none).
  if (Bridge.desktopApp && live.accounts === null) {
    jobs.push(loadAccounts().then((list) => Promise.all(
      (list || []).map((acc) => (live.calendars[acc.accountRef] ? Promise.resolve() : loadCalendars(acc.accountRef)))
    )));
  } else if (Bridge.desktopApp) {
    (live.accounts || []).forEach((acc) => { if (!live.calendars[acc.accountRef]) jobs.push(loadCalendars(acc.accountRef)); });
  }

  Promise.all(jobs).finally(() => {
    configDataLoaded = true;
    configDataLoading = false;
    if (state.view === 'config') softRepaint();
  });
}

// resetSessionState — clear every per-session cache the Settings hub relies on so the next sign-in
// (possibly a DIFFERENT identity on the same machine without restarting the App) reloads fresh data
// instead of showing the previous user's device name, auto-start toggle and account count. Without
// this, ensureConfigData()'s once-per-session guard (configDataLoaded) would short-circuit and the
// hub would render stale values. Called on sign-out.
function resetSessionState() {
  configDataLoaded = false;
  configDataLoading = false;
  live.device = null;
  live.deviceLoading = false;
  live.autoStart = null;
  live.accounts = null;
  live.calendars = {};
  live.me = null;
  live.clipboardDevices = null;
  live.clipboardDevicesLoading = false;
  live.clipboardHistory = null;
  live.clipboardHistoryLoading = false;
  clipboardDevicesOpen = false;
  clipboardOpenItemId = null;
  clipboardFilter = 'all';
}

// ---------------- Screen: Settings hub ----------------
// The hub separates the three concepts the user kept conflating: IDENTITY (who signed in),
// DEVICE (this machine), and CALENDAR ACCOUNTS (OAuth grants). Identity + device live together in
// one "Account & device" card; everything calendar-shaped (accounts, target, schedule, tools) is
// behind the navigable "Calendar" row that opens its own module. About sits behind a matching
// nav-row so its chevron lines up.
function renderConfig(root) {
  // Web panel: keep the lean panel-appropriate hub (identity + appearance + about). It has no
  // device and no desktop Calendar module, so it never shows those.
  if (Bridge.webPanel) {
    const email = (live.me && live.me.email) || 'Signed in';
    const signOutBtn = el('button', { class: 'btn btn--ghost', style: 'color:var(--err)', text: 'Sign out', onclick: () => signOutWeb() });
    root.append(cfgSection('Account',
      cfgRow('Signed in as', el('div', { class: 'cfg-row__hint', text: email }), signOutBtn)));
    root.append(appearanceSection());
    root.append(aboutSection());
    return;
  }

  // Coalesced first-load: kick every lazy fetch this hub needs (device, auto-start, accounts +
  // their calendars) in ONE batch and repaint ONCE when the batch settles, instead of each fetch
  // firing its own softRepaint (which made the first open of Settings flicker repeatedly). The
  // section builders below only READ from `live`; they no longer trigger their own loads.
  ensureConfigData();

  // Account & device — identity (who) + this device (where), consolidated into one card.
  root.append(accountAndDeviceSection());

  // Calendar — navigable module. Desktop App only: renderCalendarSettings bounces back to the hub
  // unless Bridge.desktopApp, so showing the row in mock/web would be a dead-end (a flash that
  // navigates and immediately returns). Show a live summary value (account count) when known.
  if (Bridge.desktopApp) {
    const accounts = live.accounts || [];
    let calValue = '';
    if (live.accounts === null) calValue = '';
    else if (accounts.length === 0) calValue = 'Not connected';
    else calValue = `${accounts.length} ${accounts.length === 1 ? 'account' : 'accounts'}`;
    root.append(el('div', { class: 'glass glass--card config-section' },
      navRow({ label: 'Calendar', sublabel: 'Calendar accounts', value: calValue, onClick: () => navigate('calendar-settings') })));

    // Clipboard — navigable module (desktop App only). Shows a live device-count summary once the
    // device list has loaded; blank until then (never a fabricated number).
    const devs = live.clipboardDevices;
    let clipValue = '';
    if (devs && Array.isArray(devs.devices)) {
      const n = devs.devices.length;
      clipValue = `${n} ${n === 1 ? 'device' : 'devices'}`;
    }
    root.append(el('div', { class: 'glass glass--card config-section' },
      navRow({ label: 'Clipboard', sublabel: 'Sync across your devices', value: clipValue, onClick: () => navigate('clipboard-settings') })));
  }

  // Appearance.
  root.append(appearanceSection());

  // About — same nav-row component so the chevron aligns.
  root.append(aboutSection());
}

// accountAndDeviceSection — identity row + device-name row + status row, plus (desktop App only)
// the device-only "Run at startup" app preference. Honest across transports: mock seeds demo
// strings, the real desktop App reads identity + device live.
function accountAndDeviceSection() {
  const rows = [];

  // Identity row: name (displayName||email) on top, a "Signed in" chip + email/plan hint below,
  // and Sign out on the right. This single row IS the session representation — the old separate
  // "Status" row ("This device is signed in as X") was redundant with it and has been removed.
  // identityRow(primary, email, plan) builds the consolidated row.
  const identityRow = (primary, email, plan) => {
    const hint = el('div', { class: 'cfg-row__hint identity-hint' });
    hint.append(el('span', { class: 'chip chip--ok identity-signed', style: 'margin-right:6px' }, iconEl('check', 9, 2.4), 'Signed in'));
    if (email) hint.append(el('span', { class: 'identity-hint__email', text: email }));
    if (plan) hint.append(el('span', { class: 'chip chip--ok', style: 'margin-left:8px;height:18px;font-size:9.5px', text: String(plan) }));
    const signOutBtn = el('button', { class: 'btn btn--ghost', style: 'color:var(--err)', text: 'Sign out', onclick: () => signOutApp() });
    return cfgRow(primary, hint, signOutBtn);
  };

  if (Bridge.desktopApp && identityAuth.signedIn) {
    const me = identityAuth.me || {};
    rows.push(identityRow(me.displayName || me.email || 'Signed in', (me.displayName && me.email) ? me.email : '', me.plan));
  } else if (!Bridge.available) {
    // Mock-only walkthrough identity.
    rows.push(identityRow('Daniel López', 'daniel@outlook.com', null));
  }

  // Device name row — the REAL registered device name (getDevice / live.device) on the desktop App,
  // a hot rename via renameDevice on change. Mock seeds a demo name.
  if (Bridge.available) {
    const currentName = (live.device && live.device.name) || settings.deviceName || '';
    const nameInput = el('input', { class: 'field-input', value: currentName, placeholder: 'Name this device' });
    const feedback = el('span', { class: 'cfg-row__hint', style: 'margin-left:10px' });
    // Live ✓/✗ availability indicator, like an email-signup "this name is taken" hint. Sits inside
    // the input wrapper so the glyph overlays the right edge of the field.
    const indicator = el('span', { class: 'name-check' });
    const inputWrap = el('div', { class: 'name-field' }, nameInput, indicator);

    nameInput.addEventListener('input', () => {
      settings.deviceName = nameInput.value;
      scheduleDeviceNameCheck(nameInput, indicator, feedback);
    });
    nameInput.addEventListener('change', () => saveDeviceName(nameInput, feedback, indicator));
    // (The device record is fetched by the coalesced ensureConfigData() batch — this builder only
    // reads live.device so the first open of Settings repaints once, not once per lazy load.)
    rows.push(cfgRow('Device name', el('div', { class: 'cfg-row__hint' }, document.createTextNode('Visible to your other devices'), feedback), inputWrap));
  } else {
    if (!settings.deviceName) settings.deviceName = "Daniel's MacBook";
    const nameInput = el('input', { class: 'field-input', value: settings.deviceName, placeholder: 'Name this device' });
    nameInput.addEventListener('input', () => { settings.deviceName = nameInput.value; });
    rows.push(cfgRow('Device name', el('div', { class: 'cfg-row__hint', text: 'Visible to your other devices' }), nameInput));
  }

  // (The standalone "Status" row was merged into the identity row above — the "Signed in" chip
  // now lives there, next to the name/email, instead of duplicating the identity in its own row.)

  // Run at startup — a device/app preference (NOT calendar), so it lives here. Native/loopback
  // only; backed by the host auto-start manager.
  if (Bridge.available) {
    const startupToggle = toggle(
      () => (live.autoStart !== null) ? live.autoStart : settings.startup,
      (v) => { settings.startup = v; live.autoStart = v; Bridge.call('setAutoStart', v ? 'true' : 'false').catch(() => {}); });
    // (auto-start is fetched by the coalesced ensureConfigData() batch — read-only here.)
    rows.push(cfgRow('Run at startup', el('div', { class: 'cfg-row__hint', text: 'Launch Zync Master when you sign in' }), startupToggle));
  } else {
    rows.push(cfgRow('Run at startup', el('div', { class: 'cfg-row__hint', text: 'Launch Zync Master when you sign in' }),
      toggle(() => settings.startup, (v) => { settings.startup = v; })));
  }

  return cfgSection('Account & device', ...rows);
}

// appearanceSection — Dark / Light / Auto segmented. The theme handler updates aria-pressed on the
// buttons directly (no full rerender) so toggling the theme never flickers the screen.
function appearanceSection() {
  const seg = el('div', { class: 'segmented' });
  const buttons = [];
  ['Dark', 'Light', 'Auto'].forEach((opt) => {
    const val = opt.toLowerCase();
    const b = el('button', { class: 'segmented__item', 'aria-pressed': String(storedTheme() === val), text: opt,
      onclick: () => {
        applyTheme(val);
        pushConfig();
        buttons.forEach((btn) => btn.setAttribute('aria-pressed', String(btn.dataset.val === val)));
      } });
    b.dataset.val = val;
    buttons.push(b);
    seg.append(b);
  });
  return cfgSection('Appearance',
    cfgRow('Theme', el('div', { class: 'cfg-row__hint', text: 'Auto follows your system' }), seg));
}

// aboutSection — the navigable About row (nav-row component, so the chevron is centred and the
// version sits to its left).
function aboutSection() {
  return el('div', { class: 'glass glass--card config-section' },
    navRow({ label: 'About Zync Master', sublabel: 'Version, credits, links', value: VERSION, onClick: () => navigate('about') }));
}

// ---------------- Screen: Clipboard module ----------------
// Desktop App only. Two parts (matching the glass-settings / settings-natural-accordion mocks):
//   1. "This device" — the per-machine preferences (auto-sync, send, receive, viewer hotkey,
//      viewer density, show-hints). Edits persist through updateClipboardSettings for THIS device.
//   2. "Your devices" — a collapsed-by-default accordion (a section-head with a clear rotating
//      chevron, NOT a button-box). Each device shows a status dot, its name, a "this device" badge,
//      a last-seen line and compact per-device send/receive toggles. Editing works even offline.
// All values come from getClipboardDevices; the screen never fabricates a device.

// Devices accordion open/closed state (collapsed by default). Module-scoped so a softRepaint keeps
// the user's expand state instead of snapping it shut on every data refresh.
let clipboardDevicesOpen = false;

// loadClipboardDevices — fetch the device list once and cache it on live.clipboardDevices, then
// softRepaint the screen(s) that read it. Guarded so it runs at most once until reset.
function loadClipboardDevices(repaintView) {
  if (!Bridge.available || live.clipboardDevices !== null || live.clipboardDevicesLoading) return;
  live.clipboardDevicesLoading = true;
  Bridge.call('getClipboardDevices')
    .then((d) => { live.clipboardDevices = d || { thisDeviceId: null, devices: [] }; })
    .catch(() => { live.clipboardDevices = { thisDeviceId: null, devices: [] }; })
    .finally(() => {
      live.clipboardDevicesLoading = false;
      if (repaintView && state.view === repaintView) softRepaint();
    });
}

// refreshClipboardDevices — FORCED re-fetch of the roster, bypassing the once-only guard in
// loadClipboardDevices. Driven by the live "clipboard:presence" / "clipboard:settings" pushes so the
// online dots, "(N online)" count and per-device send/receive toggles update in near-real-time across
// the user's open windows. Cheap: it only runs when the clipboard settings screen is actually visible
// (the only screen that reads live.clipboardDevices), and it skips while a fetch is already in flight
// so a burst of presence frames coalesces into one refresh. On success it softRepaints that screen so
// the user's accordion/expand state and scroll are preserved (no full re-render).
function refreshClipboardDevices() {
  if (!Bridge.available || live.clipboardDevicesLoading) return;
  if (state.view !== 'clipboard-settings') return;
  live.clipboardDevicesLoading = true;
  Bridge.call('getClipboardDevices')
    .then((d) => { live.clipboardDevices = d || { thisDeviceId: null, devices: [] }; })
    .catch(() => { /* keep the last good roster on a transient failure */ })
    .finally(() => {
      live.clipboardDevicesLoading = false;
      if (state.view === 'clipboard-settings') softRepaint();
    });
}

// thisClipboardDevice — the device record flagged isThis (or matched by thisDeviceId), or null.
function thisClipboardDevice() {
  const d = live.clipboardDevices;
  if (!d || !Array.isArray(d.devices)) return null;
  return d.devices.find((x) => x.isThis) || d.devices.find((x) => x.id === d.thisDeviceId) || null;
}

// clipboardActive — true when THIS device is registered (present in the roster) AND has send OR
// receive enabled. Drives the home tile's "Active" vs "Set up" chip. Returns false until the roster
// loads or when this device is not yet registered / has both directions off.
function clipboardActive() {
  const me = thisClipboardDevice();
  if (!me) return false;
  const s = me.settings || {};
  return !!(s.send || s.receive);
}

// persistClipboardSettings(dev) — push one device's full settings block to the host. Sends every
// field the contract expects so a partial update never resets the others to defaults server-side.
function persistClipboardSettings(dev) {
  if (!Bridge.available || !dev) return Promise.resolve();
  const s = dev.settings || {};
  const payload = {
    deviceId: dev.id,
    autoSync: !!s.autoSync,
    send: !!s.send,
    receive: !!s.receive,
    viewerHotkey: s.viewerHotkey || '',
    density: s.density === 'mini' ? 'mini' : 'rich',
    showHints: !!s.showHints,
  };
  return Bridge.call('updateClipboardSettings', JSON.stringify(payload)).catch(() => {});
}

// clipToggle(dev, key, opts) — a .toggle bound to dev.settings[key] that persists on flip. opts.sm
// renders the compact per-device variant. opts.after runs after persisting (e.g. re-register the
// hotkey). Optimistic: the local model flips immediately so the UI feels instant.
function clipToggle(dev, key, opts = {}) {
  const get = () => !!(dev.settings && dev.settings[key]);
  const t = el('div', {
    class: opts.sm ? 'toggle toggle--sm' : 'toggle',
    role: 'switch', 'aria-checked': String(get()), tabindex: '0',
    'aria-label': opts.label || key,
  });
  const flip = () => {
    if (!dev.settings) dev.settings = {};
    const v = !get();
    dev.settings[key] = v;
    t.setAttribute('aria-checked', String(v));
    persistClipboardSettings(dev).then(() => { if (opts.after) opts.after(v); });
  };
  t.addEventListener('click', flip);
  t.addEventListener('keydown', (e) => { if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); flip(); } });
  return t;
}

// hotkeyChip(dev) — the editable viewer-hotkey chip. Click (or focus + Enter/Space) to capture the
// next chord; the first key combo that includes at least one modifier is recorded, persisted, and
// re-registered as the global hotkey (setClipboardHotkey). Esc cancels capture.
function hotkeyChip(dev) {
  const chip = el('span', { class: 'cb-hotkey', role: 'button', tabindex: '0' });
  const labelOf = () => (dev.settings && dev.settings.viewerHotkey) || 'Not set';
  chip.textContent = labelOf();
  let capturing = false;

  const stop = () => {
    capturing = false;
    chip.classList.remove('is-capturing');
    chip.textContent = labelOf();
    document.removeEventListener('keydown', onKey, true);
  };
  const begin = () => {
    if (capturing) return;
    capturing = true;
    chip.classList.add('is-capturing');
    chip.textContent = 'Press keys…';
    document.addEventListener('keydown', onKey, true);
  };
  function onKey(e) {
    if (!capturing) return;
    e.preventDefault(); e.stopPropagation();
    if (e.key === 'Escape') { stop(); return; }
    // Ignore lone modifier presses; wait for a real key plus at least one modifier.
    if (['Control', 'Shift', 'Alt', 'Meta'].includes(e.key)) return;
    const parts = [];
    if (e.ctrlKey) parts.push('Ctrl');
    if (e.metaKey) parts.push('Win');
    if (e.altKey) parts.push('Alt');
    if (e.shiftKey) parts.push('Shift');
    if (!parts.length) return; // require a modifier so we don't bind a bare letter
    const key = e.key.length === 1 ? e.key.toUpperCase() : e.key;
    parts.push(key);
    const combo = parts.join('+');
    if (!dev.settings) dev.settings = {};
    dev.settings.viewerHotkey = combo;
    stop();
    // Persist the settings block AND re-register the global hotkey.
    persistClipboardSettings(dev);
    if (Bridge.available) Bridge.call('setClipboardHotkey', combo).catch(() => {});
  }
  chip.addEventListener('click', begin);
  chip.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); begin(); } });
  chip.addEventListener('blur', () => { if (capturing) stop(); });
  return chip;
}

// densitySegmented(dev) — the Rich / Mini segmented for the viewer density. Persists on change.
function densitySegmented(dev) {
  const seg = el('div', { class: 'segmented' });
  const buttons = [];
  const current = () => ((dev.settings && dev.settings.density) === 'mini' ? 'mini' : 'rich');
  [['rich', 'Rich'], ['mini', 'Mini']].forEach(([val, label]) => {
    const b = el('button', { class: 'segmented__item', 'aria-pressed': String(current() === val), text: label,
      onclick: () => {
        if (!dev.settings) dev.settings = {};
        dev.settings.density = val;
        buttons.forEach((btn) => btn.setAttribute('aria-pressed', String(btn.dataset.val === val)));
        persistClipboardSettings(dev);
      } });
    b.dataset.val = val;
    buttons.push(b);
    seg.append(b);
  });
  return seg;
}

// pasteOpacitySlider() — the App-local "paste panel opacity" control (0..100). The floating hotkey
// paste panel's card is drawn as a dark fill at this opacity over the desktop (70 = 70% opaque /
// 30% transparent). App-local (settings.json), NOT a per-device/server setting, so it is NOT routed
// through persistClipboardSettings — it saves via setPastePanelOpacity. Applies ONLY to the hotkey
// viewer; the in-dashboard clipboard view is unaffected. A native range + a live numeric readout;
// the value persists on release (change), while input only updates the readout (no save spam).
function pasteOpacitySlider() {
  const wrap = el('div', { class: 'cb-opacity' });
  const value = Math.max(0, Math.min(100, Number(settings.pastePanelOpacity)));
  const readout = el('span', { class: 'cb-opacity__val', text: `${value}%` });
  const range = el('input', {
    type: 'range', min: '0', max: '100', step: '5', value: String(value),
    class: 'cb-opacity__range', 'aria-label': 'Paste panel opacity',
  });
  range.addEventListener('input', () => { readout.textContent = `${range.value}%`; });
  range.addEventListener('change', () => {
    const v = Math.max(0, Math.min(100, parseInt(range.value, 10) || 0));
    settings.pastePanelOpacity = v;
    readout.textContent = `${v}%`;
    if (Bridge.available) Bridge.call('setPastePanelOpacity', String(v)).catch(() => {});
  });
  wrap.append(range, readout);
  return wrap;
}

// deviceRow(dev, thisId) — one row in the "Your devices" accordion. status dot + name (+ "this
// device" badge) + last-seen line + compact send/receive toggles. Works for offline devices too.
function deviceRow(dev, thisId) {
  const isThis = dev.isThis || dev.id === thisId;
  const dot = el('span', { class: 'status-dot', 'data-state': dev.online ? 'ok' : 'offline', 'aria-hidden': 'true' });
  const name = el('div', { class: 'cb-dev__name' },
    el('span', { class: 'cb-dev__name-text', text: dev.name || 'Device' }),
    isThis ? el('span', { class: 'cb-dev__badge', text: 'this device' }) : null);
  const sub = el('div', { class: 'cb-dev__sub', text: dev.online ? 'online' : 'offline' });
  const info = el('div', { class: 'cb-dev__info' }, name, sub);
  const toggles = el('div', { class: 'cb-dev__toggles' },
    el('div', { class: 'cb-dev__tg' }, el('small', { text: 'send' }), clipToggle(dev, 'send', { sm: true, label: `Send from ${dev.name}` })),
    el('div', { class: 'cb-dev__tg' }, el('small', { text: 'receive' }), clipToggle(dev, 'receive', { sm: true, label: `Receive on ${dev.name}` })));
  return el('div', { class: 'cb-dev' }, dot, info, toggles);
}

// ---------------- Screen: Clipboard view (shared history with copy/delete) ----------------
// Desktop App only. The home "Clipboard" tile opens THIS screen (route 'clipboard'), not the settings
// screen. It lists the shared clipboard history (getClipboardHistory) reusing the floating viewer's
// item-row style (.cb-row / .cb-av / .cb-title / .cb-meta). Tapping an item reveals a full overlay over
// it with two icon-only actions: COPY (copyClipboardEntry → sets the OS clipboard, nothing else) and a TRASH that
// deletes WITHOUT confirmation (optimistic: the row leaves the list at once, then deleteClipboardEntry
// propagates it to the server + the user's other devices). The live "clipboard:item" / "clipboard:
// deleted" pushes keep the list fresh while this screen is open. Config stays in Settings → Clipboard.
// Opening/closing a row's overlay and deleting a row are TARGETED DOM updates (no full repaint): a
// repaint would rebuild the whole list just to toggle one overlay, visibly flashing on long histories.

// The id of the item whose action overlay is currently open (only one at a time), or null.
let clipboardOpenItemId = null;

// Active type filter for the in-app clipboard view. Module-level so it survives repaints while the
// App runs (same lifetime as clipboardDevicesOpen). Same values/labels as the floating viewer's.
let clipboardFilter = 'all';
const CLIPBOARD_FILTERS = [['all', 'All'], ['text', 'Text'], ['image', 'Img'], ['file', 'File']];

// loadClipboardHistory — fetch the shared history once and cache it on live.clipboardHistory, then
// softRepaint the clipboard screen. Once-only until reset (a forced refresh uses refreshClipboardHistory).
function loadClipboardHistory(repaintView) {
  if (!Bridge.available || live.clipboardHistory !== null || live.clipboardHistoryLoading) return;
  live.clipboardHistoryLoading = true;
  Bridge.call('getClipboardHistory')
    .then((h) => { live.clipboardHistory = Array.isArray(h) ? h : []; })
    .catch(() => { live.clipboardHistory = []; })
    .finally(() => {
      live.clipboardHistoryLoading = false;
      if (repaintView && state.view === repaintView) softRepaint();
    });
}

// refreshClipboardHistory — FORCED re-fetch of the shared history, bypassing the once-only guard.
// The "clipboard:key" push uses it directly, and applyClipboardHistoryItem falls back to it when a
// targeted insert is not possible. Cheap: only runs while the clipboard view is visible and skips
// while a fetch is already in flight.
function refreshClipboardHistory() {
  if (!Bridge.available || live.clipboardHistoryLoading) return;
  if (state.view !== 'clipboard') return;
  live.clipboardHistoryLoading = true;
  Bridge.call('getClipboardHistory')
    .then((h) => { live.clipboardHistory = Array.isArray(h) ? h : []; })
    .catch(() => { /* keep the last good history on a transient failure */ })
    .finally(() => {
      live.clipboardHistoryLoading = false;
      if (state.view === 'clipboard') softRepaint();
    });
}

// applyClipboardHistoryItem — live "clipboard:item" push: a new entry from another device, or this
// device's own just-published copy (the host mirrors local publishes because the server broadcast
// excludes the origin device). With the clipboard view open over a loaded history this is a
// TARGETED insert: the new row lands at the top of the list (reusing clipRowEl) with no full
// repaint, deduped by id (a re-pushed id moves up instead of producing a twin row) and respecting
// the active type filter. Anything structurally off — no usable item, no loaded cache, no list in
// the DOM — falls back to the forced re-fetch. The cache is patched even while the view is closed
// so reopening shows the item without another fetch; the home Clipboard tile shows a state chip,
// not a count, so it needs no repaint here.
function applyClipboardHistoryItem(item) {
  if (!item || item.id == null || !Array.isArray(live.clipboardHistory)) {
    refreshClipboardHistory();
    return;
  }
  live.clipboardHistory = [item, ...live.clipboardHistory.filter((x) => x && x.id !== item.id)];
  if (state.view !== 'clipboard') return;
  const list = clipScreenList();
  if (!list) { softRepaint(); return; } // loading/empty state in the DOM: repaint composes the list
  const dup = Array.from(list.querySelectorAll('.cb-row')).find((n) => n.dataset.id === String(item.id));
  if (dup) dup.remove();
  if (clipboardFilter !== 'all' && clipNormalizeType(item) !== clipboardFilter) {
    // The new item is filtered out of view; if removing its older twin emptied the list, repaint so
    // the "no match" state takes its place.
    if (!list.querySelector('.cb-row')) softRepaint();
    return;
  }
  const noMatch = list.querySelector('.cb-empty');
  if (noMatch) noMatch.remove();
  list.prepend(clipRowEl(item));
}

// dropClipboardHistoryItem — remove the item with the given id from the cached list and, if the
// clipboard view is open, remove JUST that row from the DOM (no full repaint). Shared by the trash
// action (optimistic) and the live "clipboard:deleted" push. Falls back to a full render only when
// the view structure is missing (e.g. the push raced a navigation), and repaints once the list runs
// empty so the empty / "no match" state can take its place.
function dropClipboardHistoryItem(id) {
  if (!id || !Array.isArray(live.clipboardHistory)) return;
  const before = live.clipboardHistory.length;
  live.clipboardHistory = live.clipboardHistory.filter((x) => x && x.id !== id);
  if (clipboardOpenItemId === id) clipboardOpenItemId = null;
  if (live.clipboardHistory.length === before || state.view !== 'clipboard') return;
  const list = clipScreenList();
  if (!list) { softRepaint(); return; }
  const row = Array.from(list.querySelectorAll('.cb-row')).find((n) => n.dataset.id === String(id));
  if (row) row.remove(); // not found = the row is filtered out of view; the DOM is already consistent
  if (!list.querySelector('.cb-row')) softRepaint();
}

// clipScreenList — the in-app clipboard view's list element, or null when the view (or its list,
// e.g. the loading/empty states) is not currently in the DOM.
function clipScreenList() {
  return document.querySelector('.cb-list--screen');
}

// clipCloseOpenOverlay — targeted close of the currently open row's action overlay: drop the acting
// class and remove the overlay element in place, WITHOUT re-rendering the list. No-op when nothing
// is open.
function clipCloseOpenOverlay() {
  clipboardOpenItemId = null;
  const acting = document.querySelector('.cb-list--screen .cb-row--acting');
  if (!acting) return;
  acting.classList.remove('cb-row--acting');
  const overlay = acting.querySelector('.cb-actions');
  if (overlay) overlay.remove();
}

// clipOpenOverlayOn(row, item) — targeted open of the action overlay over THIS row (closing any other
// open one first — only one at a time), WITHOUT re-rendering the list.
function clipOpenOverlayOn(row, item) {
  clipCloseOpenOverlay();
  clipboardOpenItemId = item.id;
  row.classList.add('cb-row--acting');
  row.append(clipActionOverlay(item));
}

// clipNormalizeType — 'text' | 'image' | 'file' from the history item (mirrors the viewer's itemType).
function clipNormalizeType(item) {
  const t = (item && item.type ? String(item.type) : '').toLowerCase();
  if (t === 'image') return 'image';
  if (t === 'file') return 'file';
  return 'text';
}

// clipIsLocked — a Text item whose plaintext is not available yet: the bridge surfaces rows it cannot
// decrypt (the E2E text key has not been relayed to this device) with text=null, so the user still
// sees the items exist. The "clipboard:key" push re-fetches once the key lands and unlocks them.
function clipIsLocked(item) {
  return clipNormalizeType(item) === 'text' && item.text == null;
}

// clipTitleOf — the one-line row title (mirrors the viewer's titleOf, plus the locked placeholder).
function clipTitleOf(item) {
  const type = clipNormalizeType(item);
  if (clipIsLocked(item)) return 'Waiting for key from your other device';
  if (type === 'text') return (item.text || '').replace(/\s+/g, ' ').trim() || '(empty)';
  if (item.text) return item.text;
  return type === 'image' ? 'Image' : 'File';
}

// clipFormatSize — short byte size (mirrors the viewer's formatSize).
function clipFormatSize(bytes) {
  if (bytes == null || isNaN(bytes)) return '';
  const b = Number(bytes);
  if (b < 1024) return `${b} B`;
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(b < 10 * 1024 ? 1 : 0)} KB`;
  if (b < 1024 * 1024 * 1024) return `${(b / (1024 * 1024)).toFixed(1)} MB`;
  return `${(b / (1024 * 1024 * 1024)).toFixed(1)} GB`;
}

// clipRelTime — short relative time (mirrors the viewer's relTime).
function clipRelTime(iso) {
  if (!iso) return '';
  const t = Date.parse(iso);
  if (isNaN(t)) return '';
  const s = Math.max(0, Math.floor((Date.now() - t) / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} min`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h} h`;
  return `${Math.floor(h / 24)} d`;
}

// clipRowEl(item) — one history row reusing the floating viewer's .cb-row markup. Tapping the row opens
// the action overlay (copy + trash) over THIS row — a targeted DOM toggle, never a list re-render.
// The data-id is what dropClipboardHistoryItem uses to remove just this row on delete.
function clipRowEl(item) {
  const type = clipNormalizeType(item);
  const isImg = type === 'image';
  const hasThumb = isImg && !!item.imagePreviewDataUri;
  const cls = ['cb-row'];
  if (isImg) cls.push('cb-row--img');
  if (hasThumb) cls.push('cb-row--thumb');

  // Avatar slot: image items with a preview render a small inline thumbnail (same markup as the
  // floating viewer's rows); everything else keeps the typed tile.
  const av = hasThumb
    ? el('img', { class: 'cb-av cb-av--thumb', src: item.imagePreviewDataUri, alt: '', 'aria-hidden': 'true' })
    : el('div', {
        class: 'cb-av' + (type === 'file' ? ' cb-av--file' : type === 'image' ? ' cb-av--img' : ''),
        'aria-hidden': 'true',
      }, type === 'text' ? 'T' : type === 'file' ? 'F' : '');

  const title = el('div', {
    class: 'cb-title' + (clipIsLocked(item) ? ' cb-title--waiting' : ''),
    text: clipTitleOf(item),
  });
  const body = el('div', { class: 'cb-body' }, title);

  const metaParts = [];
  const time = clipRelTime(item.createdUtc);
  if (time) metaParts.push(el('span', { text: time }));
  if (type === 'image' || type === 'file') {
    const sz = clipFormatSize(item.sizeBytes);
    if (sz) { metaParts.push(el('span', { class: 'cb-meta__sep', text: '·' })); metaParts.push(el('span', { text: sz })); }
  }
  if (item.originDeviceName) {
    if (metaParts.length) metaParts.push(el('span', { class: 'cb-meta__sep', text: '·' }));
    metaParts.push(el('span', { class: 'cb-meta__from', text: item.originDeviceName }));
  }
  if (metaParts.length) body.append(el('div', { class: 'cb-meta' }, ...metaParts));

  const head = el('div', { class: 'cb-row__head' }, av, body);
  const open = clipboardOpenItemId === item.id;
  const row = el('div', {
    class: cls.join(' ') + (open ? ' cb-row--acting' : ''),
    role: 'button', tabindex: '0', 'aria-label': clipTitleOf(item),
    dataset: { id: String(item.id) },
  }, head);

  // Toggle in place: compare against the LIVE open id (not the render-time `open`) so the row keeps
  // working across targeted opens/closes without being rebuilt.
  const toggle = () => {
    if (clipboardOpenItemId === item.id) clipCloseOpenOverlay();
    else clipOpenOverlayOn(row, item);
  };
  row.addEventListener('click', toggle);
  row.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } });

  if (open) row.append(clipActionOverlay(item));
  return row;
}

// clipActionOverlay(item) — the full overlay over an open row carrying two icon-only buttons: copy
// (sets the OS clipboard via copyClipboardEntry — copy ONLY, it never closes the floating viewer,
// steals focus or synthesizes Ctrl+V the way the viewer's paste action does) and trash (deletes
// WITHOUT confirmation). Tapping either stops propagation so it does not re-toggle the row.
function clipActionOverlay(item) {
  const overlay = el('div', { class: 'cb-actions' });

  const copyBtn = el('button', {
    class: 'cb-action cb-action--copy', type: 'button', 'aria-label': 'Copy to clipboard', title: 'Copy',
    onclick: (e) => {
      e.stopPropagation();
      clipCloseOpenOverlay();
      if (Bridge.available) {
        Bridge.call('copyClipboardEntry', item.id)
          .then((r) => { announce(r && r.status === 'ok' ? 'Copied' : 'Could not copy this item.'); })
          .catch(() => { announce('Could not copy this item.'); });
      }
    },
  }, iconEl('copy', 18, 1.7));

  const trashBtn = el('button', {
    class: 'cb-action cb-action--del', type: 'button', 'aria-label': 'Delete', title: 'Delete',
    onclick: (e) => {
      e.stopPropagation();
      // Optimistic, NO confirm: drop the row immediately, then propagate the delete to the server and
      // the user's other devices. A transport failure is swallowed (a stale id is a server no-op).
      const id = item.id;
      dropClipboardHistoryItem(id);
      announce('Deleted');
      if (Bridge.available) Bridge.call('deleteClipboardEntry', id).catch(() => {});
    },
  }, iconEl('trash', 18, 1.7));

  overlay.append(copyBtn, trashBtn);
  // A tap on the overlay backdrop (not a button) closes it without acting.
  overlay.addEventListener('click', (e) => { if (e.target === overlay) { e.stopPropagation(); clipCloseOpenOverlay(); } });
  return overlay;
}

function renderClipboard(root) {
  root.append(viewHeader('Clipboard', { onBack: () => navigate('home') }));

  if (!Bridge.desktopApp) {
    // Defensive: the in-app clipboard view is a desktop concern. Other transports bounce to the hub.
    navigate('home');
    return;
  }

  loadClipboardHistory('clipboard');
  const items = live.clipboardHistory;

  const card = el('div', { class: 'glass glass--card cb-screen' });

  if (items === null) {
    card.append(el('div', { class: 'cb-screen__empty' },
      el('span', { class: 'spinner' }), el('span', { text: 'Loading your clipboard…' })));
    root.append(card);
    return;
  }

  if (!items.length) {
    card.append(el('div', { class: 'cb-screen__empty' },
      el('div', { class: 'cb-screen__empty-title', text: 'Nothing copied yet' }),
      el('div', { class: 'cb-screen__empty-sub', text: 'Copy something on any of your devices to see it here.' })));
    root.append(card);
    return;
  }

  // Type filter chips — same segmented control (and labels) as the floating viewer. Switching the
  // filter is a full repaint (the list composition changes anyway); any open overlay is dropped
  // because its row may leave the view.
  const seg = el('div', { class: 'cb-filter' });
  CLIPBOARD_FILTERS.forEach(([val, label]) => {
    seg.append(el('button', {
      class: 'cb-filter__item', type: 'button', 'aria-pressed': String(clipboardFilter === val), text: label,
      onclick: () => {
        if (clipboardFilter === val) return;
        clipboardFilter = val;
        clipboardOpenItemId = null;
        softRepaint();
      },
    }));
  });
  card.append(seg);

  const visible = items.filter((it) => clipboardFilter === 'all' || clipNormalizeType(it) === clipboardFilter);
  const list = el('div', { class: 'cb-list cb-list--screen' });
  if (!visible.length) {
    list.append(el('div', { class: 'cb-empty', text: 'No items match this filter' }));
  } else {
    visible.forEach((item) => list.append(clipRowEl(item)));
  }
  card.append(list);
  root.append(card);
}

function renderClipboardSettings(root) {
  root.append(viewHeader('Clipboard', { onBack: () => navigate('config') }));

  if (!Bridge.desktopApp) {
    // Defensive: clipboard sync is a desktop concern. Other transports bounce back to the hub.
    navigate('config');
    return;
  }

  loadClipboardDevices('clipboard-settings');
  const data = live.clipboardDevices;

  if (data === null) {
    root.append(cfgSection('This device',
      cfgRow('Loading…', el('div', { class: 'cfg-row__hint', text: 'Reading your devices' }), el('span', { class: 'spinner' }))));
    return;
  }

  // Hydrate the App-local paste-panel opacity from the host (settings.json) so the slider shows the
  // persisted value. It rides along on the devices view (App-local, not a per-device server setting).
  if (typeof data.pastePanelOpacity === 'number') {
    settings.pastePanelOpacity = Math.max(0, Math.min(100, data.pastePanelOpacity));
  }

  const me = thisClipboardDevice();
  const thisName = (me && me.name) ? ` (${me.name})` : '';

  // ---- This device card ----
  if (me) {
    if (!me.settings) me.settings = {};
    root.append(cfgSection(`Clipboard · this device${thisName}`,
      cfgRow('Auto-sync',
        el('div', { class: 'cfg-row__hint', text: 'Copy on another device → Ctrl+V here' }),
        clipToggle(me, 'autoSync', { label: 'Auto-sync' })),
      cfgRow('Send my clipboard', null, clipToggle(me, 'send', { label: 'Send my clipboard' })),
      cfgRow('Receive clipboards', null, clipToggle(me, 'receive', { label: 'Receive clipboards' })),
      cfgRow('Viewer hotkey', null, hotkeyChip(me)),
      cfgRow('Viewer density', null, densitySegmented(me)),
      cfgRow('Paste panel opacity',
        el('div', { class: 'cfg-row__hint', text: 'Transparency of the floating hotkey paste panel' }),
        pasteOpacitySlider()),
      cfgRow('Show shortcut hints',
        el('div', { class: 'cfg-row__hint', text: 'Key bar at the foot of the viewer (Rich only)' }),
        clipToggle(me, 'showHints', { label: 'Show shortcut hints' }))));
  } else {
    root.append(cfgSection('Clipboard · this device',
      cfgRow('This device is not registered yet', el('div', { class: 'cfg-row__hint', text: 'Sign in on this device to manage its clipboard.' }), null)));
  }

  // ---- Your devices accordion (collapsed by default) ----
  const devices = data.devices || [];
  const online = devices.filter((d) => d.online).length;
  const section = el('div', { class: 'glass glass--card config-section' });

  const chev = el('span', { class: 'cb-acc__chev', html: icon('chevrondown', { size: 15, stroke: 2.4 }) });
  const titleEl = el('span', { class: 'cb-acc__title' });
  titleEl.append(document.createTextNode('Your devices '), el('b', { text: `(${devices.length})` }),
    document.createTextNode(` · ${online} online`));
  const header = el('button', { class: 'cb-acc', type: 'button', 'aria-expanded': String(clipboardDevicesOpen) },
    titleEl, chev);
  const body = el('div', { class: 'cb-acc__body' });
  if (clipboardDevicesOpen) {
    if (devices.length === 0) {
      body.append(cfgRow('No other devices', el('div', { class: 'cfg-row__hint', text: 'Sign in on another device to mirror your clipboard.' }), null));
    } else {
      devices.forEach((d) => body.append(deviceRow(d, data.thisDeviceId)));
    }
  } else {
    body.hidden = true;
  }
  header.addEventListener('click', () => {
    clipboardDevicesOpen = !clipboardDevicesOpen;
    softRepaint();
  });
  section.append(header, body);
  root.append(section);
}

// ---------------- Screen: Sign in (web panel gate) ----------------
// Shown only in web mode when /api/me reported 401. The button starts the server's OAuth
// flow; after the callback signs in the cookie and redirects to returnTo=/, the panel reloads
// authenticated and the gate clears. Styled with the existing glass tokens.
function renderSignIn(root) {
  const card = el('div', { class: 'glass glass--card pair-card', style: 'margin-top:14px' },
    el('div', { class: 'about-logo', style: 'margin:4px auto 0', html: logoSvg({ size: 56 }) }),
    el('div', { class: 'pair-title', text: 'Sign in to Zync Master' }),
    el('div', { class: 'pair-sub', text: 'Connect your Microsoft account to manage your calendar sync pairs from the web panel.' }),
    el('button', { class: 'btn btn--primary ms-signin', style: 'align-self:stretch',
      onclick: () => { window.location.href = '/connect?returnTo=/'; } },
      el('span', { class: 'ms-signin__logo', html: microsoftLogo({ size: 18 }) }),
      el('span', { class: 'ms-signin__label', text: 'Sign in with Microsoft' })));
  root.append(card);
}

// signOutWeb — clears the panel session cookie on the server and returns to the sign-in gate.
function signOutWeb() {
  fetch('/signout', { method: 'POST', credentials: 'include' })
    .catch(() => {})
    .finally(() => {
      webAuth.signedIn = false;
      live.me = null;
      state.view = 'home';
      rerender();
    });
}

// ---------------- Identity (desktop App: native / loopback) ----------------
// The App's own sign-in gate, distinct from the web panel's /connect gate above and from
// "connect a calendar account" (which lives inside the dashboard). It drives Bridge actions:
//   getIdentityState -> { isSignedIn, userId, email, displayName, expiresAt, plan }
//   login { provider:'microsoft' } | { provider:'magic-link', email }
//   signOut
// Microsoft + magic-link are the only providers the backend supports today.

const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

// refreshIdentity — pull the current identity from the host and cache it. Used at boot, after a
// login completes, and after sign-out. Repaints when the signed-in flag actually changes so the
// gate and dashboard swap without flicker. Returns the resolved snapshot (or null on failure).
async function refreshIdentity() {
  if (!Bridge.desktopApp) return null;
  try {
    const s = await Bridge.call('getIdentityState');
    const signedIn = !!(s && s.isSignedIn);
    const was = identityAuth.signedIn;
    identityAuth.resolved = true;
    identityAuth.signedIn = signedIn;
    identityAuth.me = signedIn
      ? { userId: s.userId, email: s.email, displayName: s.displayName, expiresAt: s.expiresAt, plan: s.plan }
      : null;
    if (signedIn) { clearLoginSlowTimer(); identityAuth.loading = false; identityAuth.error = null; identityAuth.magicLinkSent = false; }
    // A fresh sign-in (the flag flipped on) is exactly when the device must be registered so the
    // scheduler/heartbeat/Sync-now have a key. Idempotent in the host, so it is safe to call here.
    if (signedIn && !was) ensureDeviceRegistered();
    if (was !== signedIn) rerender();
    return s;
  } catch (_) {
    // A failed probe leaves us unauthenticated so the gate shows rather than an empty dashboard.
    identityAuth.resolved = true;
    return null;
  }
}

// startMicrosoftLogin — kick the host's Microsoft sign-in. The host opens its own browser/flow;
// on success getIdentityState flips to signedIn and we enter the dashboard. We poll once the
// login() promise resolves (the host may resolve immediately and complete asynchronously).
function startMicrosoftLogin() {
  if (!Bridge.desktopApp || identityAuth.loading) return;
  identityAuth.loading = true;
  identityAuth.error = null;
  identityAuth.magicLinkSent = false;
  startLoginSlowTimer();
  rerender();
  Bridge.call('login', JSON.stringify({ provider: 'microsoft' }), 210000)
    .then((outcome) => {
      // Cancelled (user hit Cancel, or a newer login superseded this one): cancelAppLogin already
      // reset the form, so do nothing — never show an error banner for a cancel.
      if (outcome && outcome.cancelled) return;
      if (outcome && outcome.error) { clearLoginSlowTimer(); identityAuth.error = String(outcome.error); identityAuth.loading = false; rerender(); return; }
      // Re-read the identity; success swaps to the dashboard, otherwise we stay on the gate.
      return refreshIdentity().then(() => { if (!identityAuth.signedIn) { clearLoginSlowTimer(); identityAuth.loading = false; } rerender(); });
    })
    .catch((e) => { clearLoginSlowTimer(); identityAuth.error = (e && e.message) || 'Sign-in failed.'; identityAuth.loading = false; rerender(); });
}

// retryMicrosoftLogin — one-click recovery from the loading state: cancel the stuck attempt (frees
// the loopback port) and immediately start a fresh one, so the user does not have to Cancel then
// re-click Sign in. Used by the slow-login warning's "Cancel and try again" button.
function retryMicrosoftLogin() {
  if (!Bridge.desktopApp) return;
  clearLoginSlowTimer();
  identityAuth.loading = false;
  Bridge.call('cancelLogin').catch(() => {});
  startMicrosoftLogin();
}

// startMagicLinkLogin — request a magic-link email. After the host accepts the request we show
// the "check your email" state; the actual sign-in completes when the user clicks the emailed
// link, which returns through the loopback host and flips getIdentityState to signedIn. We
// poll getIdentityState a few times so the screen advances on its own once that happens.
let magicLinkPoll = null;
function startMagicLinkLogin(email) {
  if (!Bridge.desktopApp || identityAuth.loading) return;
  const addr = String(email || '').trim();
  if (!EMAIL_RE.test(addr)) { identityAuth.error = 'Enter a valid email address.'; rerender(); return; }
  identityAuth.loading = true;
  identityAuth.error = null;
  rerender();
  Bridge.call('login', JSON.stringify({ provider: 'magic-link', email: addr }), 60000)
    .then((outcome) => {
      if (outcome && outcome.error) { identityAuth.error = String(outcome.error); identityAuth.loading = false; rerender(); return; }
      identityAuth.loading = false;
      identityAuth.magicLinkSent = true;
      identityAuth.magicLinkEmail = addr;
      rerender();
      // Poll for the loopback round-trip to complete the sign-in. refreshIdentity repaints and
      // clears magicLinkSent once signedIn flips, so the poll naturally lands on the dashboard.
      clearInterval(magicLinkPoll);
      magicLinkPoll = setInterval(() => {
        if (!identityAuth.magicLinkSent || identityAuth.signedIn) { clearInterval(magicLinkPoll); return; }
        refreshIdentity();
      }, 2500);
    })
    .catch((e) => { identityAuth.error = (e && e.message) || 'Could not send the magic link.'; identityAuth.loading = false; rerender(); });
}

// cancelAppLogin — abort the sign-in attempt in flight and return to the sign-in form. The host
// cancels the pending loopback wait and frees the port (cancelLogin), so the next login() starts
// clean — this is the recovery path when the user closed the browser tab before finishing OAuth.
// Resets the gate state synchronously so the form reappears immediately; the host call is
// fire-and-forget (a failure to cancel still leaves the UI on the retry-able form).
function cancelAppLogin() {
  if (!Bridge.desktopApp) return;
  clearInterval(magicLinkPoll);
  clearLoginSlowTimer();
  identityAuth.loading = false;
  identityAuth.error = null;
  identityAuth.magicLinkSent = false;
  rerender();
  Bridge.call('cancelLogin').catch(() => {});
}

// signOutApp — desktop App sign-out. Clears the host session, drops back to the identity gate.
function signOutApp() {
  if (!Bridge.desktopApp) return;
  clearInterval(magicLinkPoll);
  Bridge.call('signOut')
    .catch(() => {})
    .finally(() => {
      identityAuth.signedIn = false;
      identityAuth.me = null;
      identityAuth.magicLinkSent = false;
      identityAuth.error = null;
      resetSessionState();
      state.view = 'home';
      rerender();
    });
}

// renderIdentitySignIn — the desktop App identity gate. Liquid Glass card with the Microsoft
// 4-square button and an email magic-link form. States: idle, loading (login in flight), error,
// and the "magic-link sent" confirmation. Decorative glow lives on the buttons' own
// border-light; nothing here moves the hit area or flickers on hover.
function renderIdentitySignIn(root) {
  // Magic-link sent confirmation — replaces the form until the round-trip completes. The user
  // still has to click the emailed link (which opens the browser), so we also nudge them to
  // continue in the browser and give a Cancel/Back path (cancelLogin) for the closed-tab case.
  if (identityAuth.magicLinkSent) {
    const card = el('div', { class: 'glass glass--card pair-card identity-card', style: 'margin-top:14px' },
      el('div', { class: 'about-logo', style: 'margin:4px auto 0', html: logoSvg({ size: 56 }) }),
      el('div', { class: 'pair-title', text: 'Check your email' }),
      el('div', { class: 'pair-sub' },
        'We sent a sign-in link to ',
        el('b', { style: 'color:var(--ink-1)', text: identityAuth.magicLinkEmail || 'your inbox' }),
        '. Open it on this device to finish signing in — this screen updates automatically.'),
      el('div', { class: 'identity-waiting' },
        el('span', { class: 'spinner', style: 'width:14px;height:14px;border-color:var(--azure-edge);border-top-color:var(--azure)' }),
        el('span', { class: 'identity-waiting__txt', text: 'Continue in your browser to finish signing in…' })),
      el('button', { class: 'btn btn--ghost', text: 'Cancel and use a different email',
        onclick: () => cancelAppLogin() }),
    );
    root.append(card);
    return;
  }

  // Login in flight (Microsoft, or magic-link request before it lands) — the host has opened the
  // system browser and is waiting on the loopback callback. Replace the form with a clear
  // "continue in your browser" panel plus a Cancel button so a closed tab is recoverable without
  // restarting the App (cancelLogin frees the port and resets the attempt).
  if (identityAuth.loading) {
    const card = el('div', { class: 'glass glass--card pair-card identity-card', style: 'margin-top:14px' },
      el('div', { class: 'about-logo', style: 'margin:4px auto 0', html: logoSvg({ size: 56 }) }),
      el('div', { class: 'pair-title', text: 'Continue in your browser' }),
      el('div', { class: 'pair-sub', text: 'We opened your browser to finish signing in. Come back here once you are done — this screen updates on its own.' }),
      el('div', { class: 'identity-waiting' },
        el('span', { class: 'spinner', style: 'width:14px;height:14px;border-color:var(--azure-edge);border-top-color:var(--azure)' }),
        el('span', { class: 'identity-waiting__txt', text: 'Waiting for the browser…' })));

    // After a while with no callback, hint at the common corporate-network cause: a firewall/proxy
    // that resets the FIRST OAuth connection (ERR_CONNECTION_RESET). Retrying usually works because
    // the proxy session is then established. The retry button frees the port and starts a fresh attempt
    // in one click, so the user does not have to Cancel and re-open the form.
    if (identityAuth.slowHint) {
      card.append(el('div', { class: 'identity-error identity-error--warn', role: 'alert', style: 'margin-top:2px' },
        iconEl('alert', 13, 1.8),
        el('span', { text: 'Taking longer than usual. If your browser shows “ERR_CONNECTION_RESET” (common on work/corporate networks), just try again — the second attempt usually goes through.' })));
      card.append(el('button', { class: 'btn btn--primary', style: 'align-self:stretch',
        onclick: () => retryMicrosoftLogin() },
        iconEl('sync', 14, 1.8), el('span', { text: 'Cancel and try again' })));
    }

    card.append(el('button', { class: 'btn btn--ghost', text: 'Cancel', onclick: () => cancelAppLogin() }));
    root.append(card);
    return;
  }

  const loading = identityAuth.loading;
  const card = el('div', { class: 'glass glass--card pair-card identity-card', style: 'margin-top:14px' });
  card.append(
    el('div', { class: 'about-logo', style: 'margin:4px auto 0', html: logoSvg({ size: 56 }) }),
    el('div', { class: 'pair-title', text: 'Sign in to Zync Master' }),
    el('div', { class: 'pair-sub', text: 'Sign in to mirror your calendars across your accounts and devices.' }),
  );

  // Error banner (login failure). Calm, no flicker; cleared on the next attempt.
  if (identityAuth.error) {
    card.append(el('div', { class: 'identity-error', role: 'alert' },
      iconEl('alert', 13, 1.8), el('span', { text: identityAuth.error })));
  }

  // Microsoft — official 4-square logo + label, on the primary glass button.
  const msBtn = el('button', { class: 'btn btn--primary ms-signin', style: 'align-self:stretch',
    disabled: loading, onclick: () => startMicrosoftLogin() },
    loading
      ? el('span', { class: 'spinner', style: 'width:16px;height:16px' })
      : el('span', { class: 'ms-signin__logo', html: microsoftLogo({ size: 18 }) }),
    el('span', { class: 'ms-signin__label', text: loading ? 'Signing in…' : 'Sign in with Microsoft' }));
  card.append(msBtn);

  // Divider between the providers.
  card.append(el('div', { class: 'identity-divider' },
    el('span', { class: 'identity-divider__line' }),
    el('span', { class: 'identity-divider__txt', text: 'or' }),
    el('span', { class: 'identity-divider__line' })));

  // Magic-link — email input + send button.
  const emailInput = el('input', {
    class: 'field-input identity-email', type: 'email', inputmode: 'email',
    autocomplete: 'email', placeholder: 'you@example.com', 'aria-label': 'Email address',
  });
  const submit = () => startMagicLinkLogin(emailInput.value);
  emailInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); submit(); } });
  emailInput.disabled = loading;
  const sendBtn = el('button', { class: 'btn identity-email__send', disabled: loading, onclick: submit },
    iconEl('arrowright', 14, 1.8), el('span', { text: 'Sign in with email' }));
  card.append(el('div', { class: 'identity-email-row' }, emailInput, sendBtn));
  card.append(el('div', { class: 'identity-foot', text: 'We will email you a one-time sign-in link. No password needed.' }));

  root.append(card);
}

// ---------------- Screen: About ----------------
// External destinations. The landing is the product's website/source; release notes ("What's
// new") live on the GitHub Releases page. Rendered as real <a target="_blank"> links so the
// browser panel / standalone demo open them in a new tab natively. In the native WebView2 shell
// the host would need a NewWindowRequested->shell-open handler for these to open the system
// browser (out of scope here); the links are never dead — the href always points somewhere real.
const ABOUT_WEBSITE_URL = 'https://zyncmaster.azurewebsites.net';
const ABOUT_RELEASES_URL = 'https://github.com/zezelazo/ZyncMaster/releases';
// Company site (DevLab-Pe), distinct from the product landing above.
const ABOUT_COMPANY_URL = 'https://devlabperu.com';

function renderAbout(root) {
  root.append(viewHeader('About', { onBack: () => navigate('config') }));
  const link = (ic, label, href) => el('a', {
    class: 'about-link', href, target: '_blank', rel: 'noopener noreferrer',
  }, iconEl(ic, 13, 1.6), label);

  // The open-source notices live in a file bundled next to the desktop exe; opening it is a
  // desktop-only bridge action (openLicenses) that hands the file to the system's default viewer.
  // In the web panel / standalone mock there is no such file, so the row is desktop-app only.
  const links = el('div', { class: 'about-links' },
    link('link', 'Website', ABOUT_WEBSITE_URL),
    link('sparkle', "What's new", ABOUT_RELEASES_URL));
  if (Bridge.desktopApp) {
    links.append(el('button', {
      class: 'about-link', type: 'button',
      style: 'grid-column:1 / -1',
      onclick: () => { Bridge.call('openLicenses').catch(() => {}); },
    }, iconEl('note', 13, 1.6), 'Open-source notices'));
  }

  root.append(el('div', { class: 'glass glass--card about-card' },
    el('div', { class: 'about-logo', html: logoSvg({ size: 64 }) }),
    el('div', { class: 'about-name', text: 'Zync Master' }),
    // Version is hardcoded: the web UI has no channel to read the .NET assembly version of the
    // host. Keep this in step with the published release (currently 0.3.3, beta). No build number.
    el('div', { class: 'about-version num', text: 'VERSION 0.3.3 · BETA' }),
    el('div', { class: 'about-tag', text: 'A quiet desktop utility for mirroring calendars across Microsoft, Google and iCloud accounts. Past events are never touched.' }),
    links,
  ));
  root.append(el('div', { class: 'glass glass--card about-credits' },
    el('div', { class: 'about-credits__hd', text: 'Made by DevLab-Pe' }),
    el('div', { class: 'about-credits__txt', text: 'For people who are tired of keeping things in sync across their devices — PCs, Macs and phones — by hand, over and over.' }),
    el('a', { class: 'about-credits__link', href: ABOUT_COMPANY_URL, target: '_blank', rel: 'noopener noreferrer' },
      iconEl('link', 12, 1.6), el('span', { text: 'devlabperu.com' })),
    el('div', { class: 'about-sys', text: '© 2026 DevLab-Pe · still in beta' }),
  ));
}

// ---------------- Screen: Pairing (MOCK-ONLY) ----------------
// Legacy manual pairing-by-key walkthrough. A device now registers as part of the identity
// sign-in, so this screen is unreachable in every real transport: navItems() drops the "Pair"
// tab and navigate() bounces 'pairing' to Settings whenever Bridge.available. It survives only in
// the standalone mock (file://) demo, so the data below — the demo name, the fabricated link, the
// timer that auto-advances — is fixed mock content that never runs against a real host.
const pairing = { step: 0, name: '', timer: null };

function renderPairing(root) {
  const labels = ['Name', 'Approve', 'Done'];
  const stepper = el('div', { class: 'stepper' });
  labels.forEach((lab, i) => {
    const st = i < pairing.step ? 'done' : i === pairing.step ? 'active' : null;
    stepper.append(el('div', { class: 'stepper__dot', dataset: st ? { state: st } : {}, html: i < pairing.step ? icon('check', { size: 11, stroke: 2.4 }) : '' }, i < pairing.step ? '' : String(i + 1)));
    if (i < labels.length - 1) stepper.append(el('div', { class: 'stepper__line', dataset: st ? { state: st } : {} }));
  });
  root.append(el('div', { class: 'glass glass--card', style: 'margin-top:6px;padding:0' }, stepper));

  if (pairing.step === 0) {
    // Mock-only demo name for the standalone walkthrough (this screen never renders with a bridge).
    if (!pairing.name) pairing.name = "Daniel's MacBook";
    const nameInput = el('input', { class: 'field-input', value: pairing.name, placeholder: 'Name this device', style: 'width:100%;height:36px;margin-top:4px' });
    nameInput.addEventListener('input', () => { pairing.name = nameInput.value; });
    root.append(el('div', { class: 'glass glass--card pair-card', style: 'margin-top:14px' },
      el('div', { style: 'width:56px;height:56px;margin:4px auto 0;border-radius:14px;display:grid;place-items:center;background:var(--azure-soft);color:var(--azure);border:1px solid var(--azure-edge)', html: icon('link', { size: 26, stroke: 1.6 }) }),
      el('div', { class: 'pair-title', text: 'Name this device' }),
      el('div', { class: 'pair-sub', text: 'So you can recognise it from other devices in your account.' }),
      nameInput,
      el('div', { style: 'display:flex;gap:10px;align-self:stretch' },
        el('button', { class: 'btn btn--ghost', style: 'flex:none', text: 'Cancel', onclick: () => { pairing.step = 0; navigate('home'); } }),
        el('button', { class: 'btn btn--primary', style: 'flex:1', onclick: () => { pairing.step = 1; rerender(); } },
          el('span', { text: 'Continue' }), iconEl('arrowright', 14, 1.8)))));
    clearTimeout(pairing.timer);
  } else if (pairing.step === 1) {
    root.append(el('div', { class: 'glass glass--card pair-card', style: 'margin-top:14px' },
      el('div', { style: 'width:56px;height:56px;margin:4px auto 0;border-radius:50%;display:grid;place-items:center;background:var(--terra-soft);color:var(--terra);border:1px solid var(--terra-edge);position:relative' },
        el('span', { class: 'spinner', style: 'width:26px;height:26px;border-width:2px;border-color:var(--terra-edge);border-top-color:var(--terra)' })),
      el('div', { class: 'pair-title', text: 'Approve in your browser' }),
      el('div', { class: 'pair-sub' }, 'We opened a sign-in page. Approve ', el('b', { style: 'color:var(--ink-1)', text: pairing.name }), ' there to finish pairing.'),
      el('button', { class: 'pair-link', onclick: copyPairLink }, iconEl('copy', 13, 1.6), el('span', { text: 'zyncmaster.app/pair/8h3-4f2a' })),
      el('button', { class: 'btn btn--ghost', text: 'Cancel', onclick: () => { pairing.step = 0; rerender(); } })));
    clearTimeout(pairing.timer);
    pairing.timer = setTimeout(() => {
      if (state.view === 'pairing' && pairing.step === 1) {
        pairing.step = 2; announce('Device paired successfully.'); rerender();
      }
    }, 2400);
  } else if (pairing.step === 2) {
    root.append(el('div', { class: 'glass glass--card pair-card', style: 'margin-top:14px' },
      el('div', { class: 'big-check', html: icon('check', { size: 28, stroke: 2.6 }) }),
      el('div', { class: 'pair-title', text: 'Paired!' }),
      el('div', { class: 'pair-sub' }, el('b', { style: 'color:var(--ink-1)', text: pairing.name }), ' is now mirroring your calendar. First sync starts in a moment.'),
      el('button', { class: 'btn btn--primary', style: 'align-self:stretch', text: 'Open dashboard', onclick: () => { state.sync = 'ok'; pairing.step = 0; navigate('home'); } })));
  }
}
async function copyPairLink() {
  try { await navigator.clipboard.writeText('https://zyncmaster.app/pair/8h3-4f2a'); } catch (_) {}
}

// ---------------- Sync state machine ----------------
let syncTimer = null;
function runSync() {
  if (state.sync === 'syncing' || state.sync === 'offline' || state.sync === 'unpaired') return;

  // Native shell: run a REAL cycle and reflect ONLY its result — no mock animation, so the
  // UI can never show a fake "complete" that disagrees with what actually happened.
  if (Bridge.available) {
    state.sync = 'syncing';
    state.progress = { done: 0, total: 0 };
    announce('Sync started');
    rerender();
    Bridge.call('syncNow')
      .then(() => Bridge.call('getStatus'))
      .then((s) => applyNativeStatus(s))
      .catch(() => { state.sync = 'error'; announce('Sync failed.'); rerender(); });
    return;
  }

  // Standalone / demo (no host): the mock progress animation.
  state.sync = 'syncing';
  state.progress = { done: 0, total: 20 };
  announce('Sync started');
  rerender();

  clearInterval(syncTimer);
  syncTimer = setInterval(() => {
    state.progress.done += 1;
    if (state.progress.done >= state.progress.total) {
      clearInterval(syncTimer);
      state.sync = 'success';
      announce('Sync complete. 20 events synchronised.');
      rerender();
      setTimeout(() => { state.sync = 'ok'; rerender(); }, 1600);
    } else {
      rerenderInPlace();
    }
  }, 180);
}

// ---------------- Live per-pair actions (native shell) ----------------
// Build the success summary line from a MirrorResult ({created,updated,deleted,skipped}). When the
// bridge returns no counts we fall back to a neutral "Completed" so a successful run still logs an
// entry rather than reading as a no-op.
function syncResultSummary(res) {
  const r = res || {};
  const created = r.created || 0, updated = r.updated || 0, deleted = r.deleted || 0, skipped = r.skipped || 0;
  const parts = [];
  if (created) parts.push(`${created} created`);
  if (updated) parts.push(`${updated} updated`);
  if (deleted) parts.push(`${deleted} deleted`);
  if (skipped) parts.push(`${skipped} skipped`);
  return parts.length ? parts.join(' · ') : 'No changes';
}

// Pull a human-readable message out of whatever the bridge rejected with (Error, string, or a
// shaped object). Never surfaces a raw [object Object].
function errorMessage(err) {
  if (!err) return 'Sync failed';
  if (typeof err === 'string') return err;
  if (err.message) return String(err.message);
  if (err.error) return String(err.error);
  try { return JSON.stringify(err); } catch (_) { return 'Sync failed'; }
}

// Mark a pair's sync as in flight (single-flight) and repaint so the button shows its busy state.
// Returns false when a run is already in flight for this id (the caller must abort).
function beginPairSync(id) {
  if (!id) return false;
  if (live.syncing.has(id)) return false;       // single-flight: ignore stacked clicks
  live.syncing.add(id);
  live.attempts[id] = (live.attempts[id] || 0) + 1;
  if (state.view === 'calendar') rerenderInPlace();
  return true;
}

// Clear the in-flight flag for a pair and repaint (button returns to its idle state).
function endPairSync(id) {
  if (!id) return;
  live.syncing.delete(id);
  if (state.view === 'calendar') rerenderInPlace();
}

function runPairNow(id) {
  if (!Bridge.available || !id) return;
  if (!beginPairSync(id)) return;               // a run is already in flight for this pair
  announce('Sync started');
  Bridge.call('runPairNow', id)
    .then((res) => {
      // Benign skip: a scheduled run was already in progress so the manual one was a no-op. This is
      // NOT a failure — log it as a neutral info event and do not count it as an error.
      if (res && res.runInProgress === true) {
        pushPairEvent(id, { ok: true, action: 'info', title: 'Already syncing', sub: 'A scheduled run is in progress', result: res });
        announce('Already syncing — a scheduled run is in progress.');
        return loadPairs();
      }
      pushPairEvent(id, { ok: true, action: 'ok', title: syncResultSummary(res), sub: 'Sync completed', result: res });
      announce('Sync completed.');
      return loadPairs();
    })
    .catch((err) => {
      const msg = errorMessage(err);
      pushPairEvent(id, { ok: false, action: 'failed', title: 'Sync failed', sub: msg, msg });
      announce('Sync failed.');
    })
    .finally(() => endPairSync(id));
}

// Track B — "Sync now" for a COM pair whose Outlook source lives on ANOTHER device. This device
// cannot read that Outlook, so it asks the server to signal the pinned origin device, which runs the
// pair on its next scheduler tick. The reply status drives clear, non-silent feedback:
//   requested          → the origin will run it shortly ("Requested — runs on <device>");
//   origin_unavailable → the origin device is offline (its App is not running);
//   local              → the caller actually IS the origin (should not happen on this branch) — run
//                        locally as a fallback so the click is never a no-op;
//   not_com_pinned     → not a COM pair after all — run it directly.
function syncPairRemote(pair) {
  if (!Bridge.available || !pair || !pair.id) return;
  const id = pair.id;
  if (!beginPairSync(id)) return;               // a request is already in flight for this pair
  const who = pair.pinnedDeviceName || 'another device';
  announce(`Asking ${who} to sync…`);
  Bridge.call('requestPairSync', id)
    .then((res) => {
      const status = (res && res.status) || '';
      const device = (res && res.deviceName) || who;
      if (status === 'requested') {
        pushPairEvent(id, { ok: true, action: 'requested', title: 'Sync requested', sub: `Will run on ${device}` });
        announce(`Requested — will run on ${device}.`);
      } else if (status === 'origin_unavailable') {
        const msg = `Origin device ${device} is offline`;
        pushPairEvent(id, { ok: false, action: 'failed', title: 'Could not request sync', sub: msg, msg });
        announce(`Origin device ${device} is not available (it is offline).`);
      } else if (status === 'local' || status === 'not_com_pinned') {
        // The server says THIS device is the origin after all — release the remote in-flight flag
        // and run it locally so the click works (runPairNow owns its own single-flight + logging).
        // Roll back the attempt this remote call counted: runPairNow re-counts the SAME user click,
        // so without this one click would show as two attempts.
        if (live.attempts[id]) live.attempts[id] -= 1;
        endPairSync(id);
        runPairNow(id);
        return null;                            // skip the shared finally's endPairSync (already done)
      } else {
        // Unknown / unexpected status — do NOT claim success (it might be a no-op). Report a neutral,
        // honest outcome so the user is not misled into thinking the origin definitely got the signal.
        const msg = 'Could not confirm the request to the origin device';
        pushPairEvent(id, { ok: false, action: 'failed', title: 'Sync not confirmed', sub: msg, msg });
        announce('Could not confirm the request to the origin device.');
      }
      endPairSync(id);
      return loadPairs();
    })
    .catch((err) => {
      const msg = errorMessage(err);
      pushPairEvent(id, { ok: false, action: 'failed', title: 'Could not request sync', sub: msg, msg });
      announce('Could not request a sync from the origin device.');
      endPairSync(id);
    });
}
function setPairState(id, newState) {
  if (!Bridge.available || !id) return;
  Bridge.call('updatePair', JSON.stringify({ id, state: newState }))
    .then(() => loadPairs())
    .then(() => { if (state.view === 'calendar') rerender(); })
    .catch(() => {});
}
function deletePair(id) {
  if (!Bridge.available || !id) return;
  Bridge.call('deletePair', id)
    .then(() => loadPairs())
    .then(() => { openPairs.delete(id); if (state.view === 'calendar') rerender(); })
    .catch(() => {});
}

// ---------------- Modal helper ----------------
// openModal({ title, body, onClose }) — a focus-trapped, dismissable overlay built from the existing
// glass tokens. The backdrop is a real interactive layer (it catches the dismiss click) — NOT a
// decorative pointer-events:none overlay — while the card itself stops propagation so clicks inside
// never close it. Escape and the backdrop close it; focus moves into the card and returns to the
// previously-focused element on close. Returns { close }.
function openModal({ title, body, onClose }) {
  const prevFocus = document.activeElement;
  let closed = false;

  const close = () => {
    if (closed) return;
    closed = true;
    document.removeEventListener('keydown', onKey, true);
    overlay.classList.add('is-closing');
    const remove = () => { if (overlay.parentNode) overlay.parentNode.removeChild(overlay); };
    overlay.addEventListener('animationend', remove, { once: true });
    setTimeout(remove, 300);
    if (prevFocus && prevFocus.focus) { try { prevFocus.focus(); } catch (_) {} }
    if (onClose) onClose();
  };

  const onKey = (e) => {
    if (e.key === 'Escape') { e.preventDefault(); close(); return; }
    if (e.key === 'Tab') {
      // Simple focus trap: keep Tab within the card's focusable elements.
      const focusables = card.querySelectorAll('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])');
      if (!focusables.length) return;
      const first = focusables[0];
      const last = focusables[focusables.length - 1];
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    }
  };

  const closeBtn = el('button', { class: 'modal__close', type: 'button', 'aria-label': 'Close', onclick: close }, iconEl('close', 16, 1.8));
  const head = el('div', { class: 'modal__head' }, el('div', { class: 'modal__title', text: title }), closeBtn);
  const card = el('div', { class: 'glass--overlay modal__card', role: 'dialog', 'aria-modal': 'true', 'aria-label': title },
    head, el('div', { class: 'modal__body' }, body));
  card.addEventListener('click', (e) => e.stopPropagation());

  const overlay = el('div', { class: 'modal-overlay', onclick: close });
  overlay.append(card);
  document.body.append(overlay);

  document.addEventListener('keydown', onKey, true);
  // Move focus into the dialog (first focusable, else the close button).
  const firstField = card.querySelector('input, select, button');
  if (firstField && firstField.focus) firstField.focus(); else closeBtn.focus();

  return { close };
}

// confirmModal — confirmación destructiva sobre openModal. Resuelve true/false; cerrar
// con Esc/backdrop cuenta como cancelar.
function confirmModal({ title, text, confirmLabel = 'Delete' }) {
  return new Promise((resolve) => {
    let decided = false;
    const decide = (v) => { if (!decided) { decided = true; resolve(v); } modal.close(); };
    const body = el('div', { class: 'confirm' },
      el('div', { class: 'confirm__text', text }),
      el('div', { class: 'confirm__actions' },
        el('button', { class: 'btn btn--ghost', type: 'button', onclick: () => decide(false) }, 'Cancel'),
        el('button', { class: 'btn btn--danger', type: 'button', onclick: () => decide(true) }, confirmLabel)));
    const modal = openModal({ title, body, onClose: () => { if (!decided) { decided = true; resolve(false); } } });
  });
}

// ---------------- Shell chrome: sidebar + view header ----------------
const SIDEBAR_KEY = 'zyncmaster.sidebar';
function sidebarCollapsed() { try { return localStorage.getItem(SIDEBAR_KEY) === 'collapsed'; } catch (_) { return false; } }
function toggleSidebar() {
  try { localStorage.setItem(SIDEBAR_KEY, sidebarCollapsed() ? 'expanded' : 'collapsed'); } catch (_) {}
  renderShellChrome();
}

// Los gates (sign-in / identidad) ocupan la pantalla completa: sin sidebar ni header.
function shellGated() {
  if (Bridge.webPanel) return webAuth.resolved && !webAuth.signedIn;
  if (Bridge.desktopApp) return serverHealth.ok && identityAuth.resolved && !identityAuth.signedIn;
  return false;
}

function sideDotEl(dot) {
  if (!dot) return null;
  return el('span', { class: `side-dot side-dot--${dot.state}`, title: dot.title, 'aria-label': dot.title });
}

function renderSidebar() {
  const side = $('#sidebar');
  if (!side) return;
  side.replaceChildren();
  side.append(el('div', { class: 'brand' },
    el('span', { class: 'brand__mark', html: logoSvg({ size: 22 }) }),
    el('span', { class: 'brand__name', text: 'SyncMaster' })));

  const nav = el('nav', { class: 'side-nav' });
  const items = registry.navItems();
  const activeId = registry.activeNavId(state.view);
  let sepDone = false;
  items.forEach((v, i) => {
    if (v.nav.section === 'system' && !sepDone) { nav.append(el('div', { class: 'side-nav__sep' })); sepDone = true; }
    const active = v.id === activeId;
    const btn = el('button', {
      class: 'side-item', type: 'button',
      'aria-current': active ? 'page' : false,
      tabindex: active || (i === 0 && !activeId) ? '0' : '-1',
      onclick: () => navigate(v.id),
    },
      el('span', { class: 'side-item__ico', html: icon(v.nav.icon, { size: 16, stroke: 1.5 }) }),
      el('span', { class: 'side-item__lbl', text: v.nav.label }),
      sideDotEl(v.statusDot ? v.statusDot() : null),
      v.nav.section === 'modules' ? el('span', { class: 'side-item__kbd num', text: String(i + 1) }) : null,
    );
    nav.append(btn);
  });
  // Roving tabindex: flechas mueven el foco, Enter/Space activan (spec §8).
  nav.addEventListener('keydown', (e) => {
    if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
    e.preventDefault();
    const btns = [...nav.querySelectorAll('.side-item')];
    const cur = btns.indexOf(document.activeElement);
    const next = btns[(cur + (e.key === 'ArrowDown' ? 1 : -1) + btns.length) % btns.length];
    btns.forEach((b) => b.setAttribute('tabindex', '-1'));
    next.setAttribute('tabindex', '0');
    next.focus();
  });
  side.append(nav);

  const health = serverHealth.ok
    ? { state: 'ok', text: 'Connected' }
    : serverHealth.waking ? { state: 'warn', text: 'Reconnecting…' } : { state: 'off', text: 'Offline' };
  side.append(el('div', { class: 'side-foot' },
    el('span', { class: `side-dot side-dot--${health.state}` }),
    el('span', { text: Bridge.available ? health.text : 'Demo data' })));
  side.append(el('button', {
    class: 'side-collapse', type: 'button',
    'aria-label': sidebarCollapsed() ? 'Expand sidebar' : 'Collapse sidebar',
    onclick: toggleSidebar,
  }, iconEl('chevronleft', 14, 1.8)));
}

function renderViewHeader() {
  const head = $('#vhead');
  if (!head) return;
  head.replaceChildren();
  const def = registry.get(state.view);
  const info = def && def.header ? def.header() : null;
  const navDef = def && registry.activeNavId(state.view) ? registry.get(registry.activeNavId(state.view)) : null;
  const title = (info && info.title) || (def && def.nav && def.nav.label) || (navDef && navDef.nav.label) || 'SyncMaster';
  head.append(el('h1', { class: 'vhead__title', text: title }));
  if (info && info.meta) head.append(el('span', { class: 'vhead__meta num', text: info.meta }));
  head.append(el('span', { class: 'vhead__spacer' }));
  if (Bridge.available) {
    head.append(el('button', { class: 'btn btn--primary', type: 'button', onclick: () => syncAllPairs() }, 'Sync now'));
  }
  const search = el('button', { class: 'btn', type: 'button', 'aria-label': 'Open command palette (Ctrl+K)' },
    'Search… ', el('kbd', { class: 'kbd', text: 'Ctrl' }), el('kbd', { class: 'kbd', text: 'K' }));
  search.addEventListener('click', () => { if (typeof window.__openPalette === 'function') window.__openPalette(); });
  head.append(search);
}

// "Sync now" global del header: dispara el run de cada par activo (real y honesto: con
// 0 pares es un no-op silencioso; el botón existe igualmente porque la vista lo explica).
function syncAllPairs() {
  const pairs = (live.pairs || []).filter((p) => p && p.state === 'active');
  pairs.forEach((p) => runPairNow(p.id));
  announce(pairs.length ? 'Sync started.' : 'No active sync pairs.');
  showToast(pairs.length ? 'Sync started' : 'No active sync pairs', { kind: pairs.length ? 'ok' : 'warn' });
}

function renderShellChrome() {
  const shell = $('#shell');
  if (!shell) return;
  shell.classList.toggle('shell--collapsed', sidebarCollapsed());
  shell.classList.toggle('shell--gated', shellGated());
  renderSidebar();
  renderViewHeader();
  renderOfflineBanner();
}

// Banner offline no-bloqueante (spec §3.1): la app SIEMPRE renderiza; si el server no
// responde se avisa arriba del contenido. Settings, pares cacheados y export COM siguen
// accesibles. El retry reusa la misma lógica del viejo gate de warm-up.
function renderOfflineBanner() {
  const banner = $('#offlineBanner');
  if (!banner) return;
  const show = Bridge.desktopApp && serverHealth.checked && !serverHealth.ok;
  banner.hidden = !show;
  if (!show) { banner.replaceChildren(); return; }
  banner.replaceChildren(
    el('span', { class: 'side-dot' }),
    el('span', { text: serverHealth.waking ? 'Connecting to the server…' : 'Server unreachable — working offline.' }),
    el('span', { class: 'offline-banner__spacer' }),
    el('button', { class: 'btn', type: 'button', onclick: () => retryServerHealth() }, 'Retry'),
  );
}

// Timeout for slow Outlook COM round-trips (listLocalCalendars / generateTxt / exportSourceTxt). A
// cold Outlook plus the native save dialog can run far past the 60s Bridge.call default, which would
// otherwise reject as "bridge timeout" mid-operation. Matches the connect flow's 210s budget.
const COM_SLOW_TIMEOUT_MS = 210000;
// ---------------- Native status mapping ----------------
// Translate a native AppStatus into the view-model state the renderer drives.
function applyNativeStatus(s) {
  if (!s) return;
  // Web panel: getStatus carries the signed-in identity; capture it for the gate + Settings.
  if (Bridge.webPanel && s.signedIn) {
    webAuth.signedIn = true;
    live.me = { email: s.email, displayName: s.displayName };
  }
  const prevSync = state.sync;
  if (!s.paired) state.sync = 'unpaired';
  else if (s.status === 'Error') state.sync = 'error';
  else if (s.status === 'Offline') state.sync = 'offline';
  else if (s.status === 'Paused') state.sync = 'paused';
  else if (s.status === 'Syncing') state.sync = 'syncing';
  else state.sync = 'ok';
  const tag = $('#pausedTag'); if (tag) tag.hidden = state.sync !== 'paused';

  // The engine pushes a status event on every scheduler tick (~30s) and on each sync cycle.
  // A full rerender() here replays the staggered entrance animation and rebuilds the whole
  // view, which the user sees as the screen flickering/redrawing every few seconds. Only
  // repaint when the sync state actually CHANGED, and even then do it in place (no entrance
  // replay) so a routine "still ok" heartbeat is a no-op instead of a visible flash.
  if (state.sync === prevSync) return;
  rerenderInPlace();
}

// ---------------- Render dispatch ----------------
function rerender() {
  renderShellChrome();
  const root = $('#view');
  if (!root) return;
  root.replaceChildren();
  root.classList.remove('enter');

  // Web panel sign-in gate: until the session resolves as signed-in, the only screen is the
  // sign-in card and the shell chrome collapses (renderShellChrome applies .shell--gated).
  if (Bridge.webPanel && webAuth.resolved && !webAuth.signedIn) {
    renderSignIn(root);
    void root.offsetWidth;
    root.classList.add('enter');
    return;
  }

  // Desktop App identity gate: in native/loopback, until getIdentityState reports signed-in the
  // only screen is the identity sign-in card (Microsoft / magic-link). The web panel and the mock
  // demo do NOT use this gate.
  if (Bridge.desktopApp && identityAuth.resolved && !identityAuth.signedIn) {
    renderIdentitySignIn(root);
    void root.offsetWidth;
    root.classList.add('enter');
    return;
  }

  const def = registry.get(state.view) || registry.get('home');
  def.render(root);
  // retrigger the staggered card entrance
  void root.offsetWidth;
  root.classList.add('enter');
}
// rerenderInPlace — repaint the current view WITHOUT replaying the entrance animation (no
// `enter` class) and preserving scroll, so progress ticks and in-screen control changes don't
// flicker the whole screen. Covers the dashboard views (home/calendar) used during the syncing
// tick AND the settings views (config/calendar-settings) so toggling interval/theme/select on
// those screens is a smooth repaint instead of a full re-entrance via rerender().
function rerenderInPlace() {
  const def = registry.get(state.view);
  if (!def || !def.soft) return;
  // The sign-in gate owns the screen: a stale background repaint (e.g. a late loadPairs)
  // must never paint the dashboard over the sign-in card and leave the nav hidden.
  if (Bridge.webPanel && webAuth.resolved && !webAuth.signedIn) return;
  if (Bridge.desktopApp && !serverHealth.ok) return;
  if (Bridge.desktopApp && identityAuth.resolved && !identityAuth.signedIn) return;
  const root = $('#view');
  if (!root) return;
  const prevScroll = root.scrollTop;   // a tick/refresh repaint must not snap the user back to the top
  root.replaceChildren();
  def.render(root);
  root.scrollTop = prevScroll;
  renderShellChrome();
}
// softRepaint — alias used by in-screen control handlers (interval/theme/select on the settings
// screens) to repaint without the entrance animation. Distinct name from rerenderInPlace to make
// the intent obvious at the call site.
function softRepaint() { rerenderInPlace(); }

function navigate(view) {
  // Legacy manual pairing-by-key is retired in every real transport (the device registers via the
  // identity sign-in), so bounce it to Settings. It stays reachable only in the mock file:// demo.
  if (view === 'pairing' && Bridge.available) view = 'config';
  state.view = view;
  rerender();
}

// ---------------- Device-name live availability (✓/✗) ----------------
// Tracks the latest known availability so saveDeviceName can refuse a name we already know is
// taken without a round-trip. { name, state } where state is 'available' | 'taken' | 'invalid'.
const deviceNameCheck = { name: null, state: null };
let deviceNameCheckTimer = null;
let deviceNameCheckSeq = 0;

const DEVICE_NAME_DEBOUNCE_MS = 400;
const DEVICE_NAME_MAX = 100;

// Paints the indicator span: ✓ (available, aqua/ok), ✗ (taken/invalid, err), a subtle dot while
// checking, or nothing when the field is empty / unchanged. `hint` (the row hint span) carries a
// short message for the taken/invalid states.
function renderNameCheck(indicator, hint, st) {
  if (!indicator) return;
  indicator.innerHTML = '';
  indicator.className = 'name-check';
  const setHint = (text, color) => { if (hint) { hint.textContent = text; hint.style.color = color || ''; } };

  if (st === 'available') {
    indicator.classList.add('name-check--ok');
    indicator.innerHTML = icon('check', { size: 15, stroke: 2.4 });
    setHint('', '');
  } else if (st === 'taken') {
    indicator.classList.add('name-check--err');
    indicator.innerHTML = icon('close', { size: 15, stroke: 2.4 });
    setHint('Name already used', 'var(--err)');
  } else if (st === 'invalid') {
    indicator.classList.add('name-check--err');
    indicator.innerHTML = icon('close', { size: 15, stroke: 2.4 });
    setHint(`Use 1–${DEVICE_NAME_MAX} characters`, 'var(--err)');
  } else if (st === 'checking') {
    indicator.classList.add('name-check--busy');
    indicator.innerHTML = '<span class="name-check__dot"></span>';
    setHint('', '');
  } else {
    setHint('', '');
  }
}

// Debounced live check as the user types: ~400ms after the last keystroke, ask the host whether
// the trimmed name is free (excluding this device). Mock has no bridge, so the caller never wires
// this in mock mode. Empty / unchanged names clear the indicator; over-long names flag invalid
// without a round-trip.
function scheduleDeviceNameCheck(input, indicator, hint) {
  if (!Bridge.available || !input) return;
  const name = (input.value || '').trim();

  if (deviceNameCheckTimer) { clearTimeout(deviceNameCheckTimer); deviceNameCheckTimer = null; }

  // Empty, or unchanged from the device's current name → no indicator (nothing to validate).
  if (!name || (live.device && live.device.name === name)) {
    deviceNameCheck.name = name; deviceNameCheck.state = null;
    renderNameCheck(indicator, hint, null);
    return;
  }
  if (name.length > DEVICE_NAME_MAX) {
    deviceNameCheck.name = name; deviceNameCheck.state = 'invalid';
    renderNameCheck(indicator, hint, 'invalid');
    return;
  }

  renderNameCheck(indicator, hint, 'checking');
  const seq = ++deviceNameCheckSeq;
  deviceNameCheckTimer = setTimeout(() => {
    Bridge.call('checkDeviceName', JSON.stringify({ name }))
      .then((r) => {
        if (seq !== deviceNameCheckSeq) return; // a newer keystroke superseded this check
        const st = (r && r.available) ? 'available' : 'taken';
        deviceNameCheck.name = name; deviceNameCheck.state = st;
        renderNameCheck(indicator, hint, st);
      })
      .catch(() => {
        if (seq !== deviceNameCheckSeq) return;
        deviceNameCheck.name = name; deviceNameCheck.state = null;
        renderNameCheck(indicator, hint, null); // a transient failure: clear, don't block save
      });
  }, DEVICE_NAME_DEBOUNCE_MS);
}

// ---------------- Config push to host ----------------
// Renames the current device in place (hot rename) through the host. Shows inline feedback next
// to the input: "Saving…", then "Saved" or an error. A no-op when the name is unchanged or blank.
// On success live.device is updated so a later render keeps showing the real name. If the name is
// known-taken (or the server returns name_taken) the rename is refused with an inline ✗.
function saveDeviceName(input, feedback, indicator) {
  if (!Bridge.available || !input) return;
  const name = (input.value || '').trim();
  const setMsg = (text, color) => { if (feedback) { feedback.textContent = text; feedback.style.color = color || ''; } };

  if (!name) { setMsg('Name cannot be empty', 'var(--err)'); return; }
  if (live.device && live.device.name === name) { setMsg(''); return; }
  if (name.length > DEVICE_NAME_MAX) {
    renderNameCheck(indicator, feedback, 'invalid');
    return;
  }
  // Already known to be taken (from the live check): don't even try to rename.
  if (deviceNameCheck.name === name && deviceNameCheck.state === 'taken') {
    renderNameCheck(indicator, feedback, 'taken');
    return;
  }

  setMsg('Saving…', 'var(--ink-2)');
  Bridge.call('renameDevice', JSON.stringify({ name }))
    .then((d) => {
      const saved = (d && d.name) || name;
      live.device = Object.assign({}, live.device, { name: saved });
      settings.deviceName = saved;
      input.value = saved;
      deviceNameCheck.name = saved; deviceNameCheck.state = null;
      renderNameCheck(indicator, feedback, null);
      setMsg('Saved', 'var(--ok)');
      setTimeout(() => { if (feedback) feedback.textContent = ''; }, 2000);
    })
    .catch((err) => {
      const msg = (err && err.message) ? err.message : '';
      // The server rejects a duplicate with a 409 carrying "name_taken"; show the inline ✗ instead
      // of a generic error so it reads like the live indicator.
      if (/name_taken/i.test(msg)) {
        deviceNameCheck.name = name; deviceNameCheck.state = 'taken';
        renderNameCheck(indicator, feedback, 'taken');
        return;
      }
      setMsg(msg || 'Could not rename', 'var(--err)');
    });
}

function pushConfig() {
  if (!Bridge.available) return;
  // The host's SaveConfig is a WHOLESALE replace: it deserializes this object straight into a fresh
  // AppSettings and persists it. So any field this payload omits resets to the AppSettings POCO
  // default on disk. Two consequences for the now per-sync settings:
  //   * syncWindowDays / intervalMinutes map to real AppSettings properties (SyncWindowDays /
  //     IntervalMinutes). IntervalMinutes is the default interval CreatePair falls back to
  //     (dto.IntervalMin ?? EngineSettings.IntervalMinutes), so we keep sending them to avoid
  //     silently snapping the engine default down to the bare POCO value on every save. The UI no
  //     longer edits these (interval/window moved to the per-pair Add-pair wizard); they are legacy
  //     globals carried through unchanged.
  //   * autoSync was dropped: AppSettings has NO matching property, so the host ignored it on
  //     deserialization anyway. It was dead weight in the payload and is removed here.
  const cfg = {
    deviceName: settings.deviceName,
    syncWindowDays: settings.windowDays,
    intervalMinutes: settings.interval,
    runAtStartup: settings.startup,
    theme: storedTheme(),
    // App-local paste-panel opacity. saveConfig is a wholesale replace, so carry it through here too
    // (it is otherwise saved on its own via setPastePanelOpacity) to avoid snapping it back to the
    // POCO default whenever the main config is saved.
    pastePanelOpacity: settings.pastePanelOpacity,
  };
  Bridge.call('saveConfig', JSON.stringify(cfg)).catch(() => {});
}

// ---------------- Title bar wiring ----------------
function wireTitlebar() {
  const tb = $('#titlebar');
  if (tb) tb.setAttribute('data-platform', isMac ? 'mac' : 'win');
  const send = (a) => Bridge.windowAction(a);
  const bind = (id, action) => { const b = $('#' + id); if (b) b.addEventListener('click', () => send(action)); };
  bind('wcClose', 'windowClose');
  bind('wcMin', 'windowMinimize');
  bind('wcMax', 'windowToggleMaximize');
  bind('tlClose', 'windowClose');
  bind('tlMin', 'windowMinimize');
  bind('tlMax', 'windowToggleMaximize');
  // Drag region: pressing the title bar (away from controls) asks the host to move the window.
  if (tb) tb.addEventListener('pointerdown', (e) => {
    if (e.target && e.target.closest && e.target.closest('.titlebar__controls')) return;
    send('windowDragMove');
  });
}

// ---------------- Boot ----------------
async function boot() {
  hydrateIcons();
  initPalette();
  applyTheme(storedTheme());
  wireTitlebar();
  const tag = $('#pausedTag'); if (tag) tag.hidden = true;

  // Settle the http(s) transport (web vs the App's loopback host) before the first
  // data-driven paint. Native + mock resolve synchronously; this only awaits the probe.
  await Bridge.resolveTransport();
  document.documentElement.setAttribute('data-transport', Bridge.mode);

  // Web panel: a 401 from any call drops us back to the sign-in gate.
  if (Bridge.webPanel) Bridge.onUnauthorized(() => { webAuth.resolved = true; webAuth.signedIn = false; rerender(); });

  if (Bridge.available) {
    Bridge.start();
    Bridge.onStatus((s) => applyNativeStatus(s));

    // Live clipboard roster + per-device settings: the host pushes "clipboard:presence" when a device
    // goes on/offline (or the socket dropped) and "clipboard:settings" when a sibling window edits a
    // device's send/receive/autoSync. Both are a "re-fetch the roster" signal — refreshClipboardDevices
    // forces a fresh getClipboardDevices ONLY while the clipboard settings screen is visible, then
    // softRepaints, so the online dots, "(N online)" count and toggles update across the user's windows
    // without a manual refresh. Registered once; the payloads are not trusted as the whole view.
    Bridge.onEvent('clipboard:presence', () => refreshClipboardDevices());
    Bridge.onEvent('clipboard:settings', () => refreshClipboardDevices());

    // Live clipboard history (the in-app clipboard VIEW): a new entry — from another device OR this
    // device's own just-published copy — arrives as "clipboard:item" (targeted insert at the top of
    // the open list, full re-fetch as the fallback), and a deletion from another device / the human
    // panel arrives as "clipboard:deleted" (drop that row from the open list at once).
    Bridge.onEvent('clipboard:item', (item) => applyClipboardHistoryItem(item));
    Bridge.onEvent('clipboard:deleted', (p) => { if (p && p.id) dropClipboardHistoryItem(p.id); });

    // The E2E text key just arrived on this device ("clipboard:key"): rows that were rendered with
    // the cannot-decrypt placeholder are readable now, so force a history re-fetch. Cheap no-op
    // unless the clipboard view is open.
    Bridge.onEvent('clipboard:key', () => refreshClipboardHistory());

    // Load device capabilities once. The web panel maps this to {outlookCom:false}; the desktop
    // App probes Outlook Classic. A failure leaves the safe default (COM disabled). Repaint so
    // tiles/buttons that gate on COM reflect the real value once it lands.
    Bridge.call('getCapabilities')
      .then((c) => { if (c) live.capabilities = { outlookCom: !!c.outlookCom }; live.capabilitiesLoaded = true; rerenderInPlace(); })
      .catch(() => { live.capabilitiesLoaded = true; });

    // Desktop App: warm up the server FIRST. The Azure F1 free tier cold-starts, so before the
    // identity gate (which talks to the server) we ping /health and show "waking up" feedback.
    // probeServerHealth → onServerReady resolves the identity gate once the server answers; the
    // identity call is no longer kicked directly here so a sleeping server never stalls login.
    if (Bridge.desktopApp) {
      probeServerHealth();
    }

    // First status settle. In the web panel getStatus IS the sign-in gate, so it resolves
    // webAuth and repaints; in the desktop app the health/identity gates own the first paint.
    Bridge.call('getStatus')
      .then((s) => { applyNativeStatus(s); if (Bridge.webPanel) { webAuth.resolved = true; rerender(); } })
      .catch(() => {
        // In the web panel a failed getStatus (typically 401) is the unauthenticated state:
        // resolve the gate so the sign-in screen shows instead of an empty dashboard.
        if (Bridge.webPanel) { webAuth.resolved = true; webAuth.signedIn = false; rerender(); }
      });
  }

  // Ctrl+1..9 salta a los módulos del sidebar en su orden visible (spec §3.2).
  document.addEventListener('keydown', (e) => {
    if (!e.ctrlKey || e.altKey || e.metaKey) return;
    const n = parseInt(e.key, 10);
    if (!n || n < 1 || n > 9) return;
    const mods = registry.navItems().filter((v) => v.nav.section === 'modules');
    if (mods[n - 1]) { e.preventDefault(); navigate(mods[n - 1].id); }
  });

  rerender();
}

// ---------------- Shared context para los módulos de vista ----------------
// Cada ui/js/views/<name>.js exporta register<Name>Views(ctx) y recibe ESTE objeto.
// Las vistas no importan app.js (cero ciclos); todo lo compartido viaja por aquí.
const ctx = {
  // DOM + iconos
  $, el, iconEl, icon, logoSvg, microsoftLogo,
  // transporte + estado compartido
  Bridge, state, live, settings, serverHealth, identityAuth, webAuth,
  // navegación + repintado
  registry, navigate, rerender, rerenderInPlace, softRepaint,
  // feedback
  announce, openModal, confirmModal, showToast,
  // fragments compartidos
  viewHeader, navRow, actionChip, activityRow, pairBadge,
  cfgSection, cfgRow,
  // datos compartidos
  loadPairs, loadAccounts, loadCalendars, loadLocalCalendars, ensurePairCalendarNames,
  pairViewModel, resolveCalendarLabel, comAvailable, fmtMMSS, COM_SLOW_TIMEOUT_MS,
  loadClipboardDevices, refreshClipboardDevices, persistClipboardSettings, thisClipboardDevice, clipboardActive,
  loadClipboardHistory,
  // sync runtime
  runPairNow, setPairState, deletePair, syncAllPairs, runSync, syncPairRemote,
  // calendar module (extraído a views/calendar.js)
  openPairs, PAIRS,
  calendarStatusDot: () => calendarDot(live.pairs, live.loadedPairs),
  // tema + versión
  applyTheme, storedTheme, resolveTheme, VERSION,
};

// ---------------- View registry (fase de transición) ----------------
// Las render functions siguen viviendo en este archivo; cada tarea de extracción las muda
// a ui/js/views/<name>.js conservando estos ids. El sidebar se construye desde nav/statusDot.
registerHomeViews(ctx);
registerCalendarViews(ctx);
registry.register('clipboard', {
  render: renderClipboard, soft: true,
  nav: { label: 'Clipboard', icon: 'clipboard', order: 3, section: 'modules', hidden: () => Bridge.webPanel },
  statusDot: () => clipboardDot(thisClipboardDevice()),
});
registry.register('clipboard-settings', { render: renderClipboardSettings, soft: true, parent: 'clipboard' });
registry.register('config', {
  render: renderConfig, soft: true,
  nav: { label: 'Settings', icon: 'settings', order: 100, section: 'system' },
});
registry.register('about', { render: renderAbout, parent: 'config' });
registry.register('pairing', { render: renderPairing });

// ---------------- Palette sources (v1: navegación + acciones + pairs + clipboard) ----------------
registerPaletteSource(() => registry.navItems().map((v, i) => ({
  group: 'Navigation',
  label: `Go to ${v.nav.label}`,
  hint: v.nav.section === 'modules' ? `Ctrl+${i + 1}` : '',
  run: () => navigate(v.id),
})));

registerPaletteSource(() => {
  const items = [
    { group: 'Actions', label: 'Sync now', hint: 'all active pairs', run: () => syncAllPairs() },
    { group: 'Actions', label: 'New sync pair', hint: 'Calendar', run: () => navigate('add-pair') },
    { group: 'Actions', label: 'Add calendar account', hint: 'Calendar', run: () => { state.returnTo = state.view; navigate('add-calendar'); } },
    { group: 'Actions', label: 'Toggle theme', hint: 'dark / light', run: () => { applyTheme(resolveTheme(storedTheme()) === 'dark' ? 'light' : 'dark'); rerender(); } },
  ];
  if (Bridge.desktopApp) {
    items.push({ group: 'Actions', label: 'Open clipboard history', hint: 'Clipboard', run: () => navigate('clipboard') });
    const latest = (live.clipboardHistory && live.clipboardHistory[0]) || null;
    if (latest) items.push({
      group: 'Actions', label: 'Copy latest clipboard item', hint: 'copies to clipboard',
      run: () => { Bridge.call('copyClipboardEntry', String(latest.id)).then(() => announce('Copied.')).catch(() => {}); },
    });
  }
  return items;
});

// La fuente del palette de "Sync pairs" vive ahora en views/calendar.js (registerCalendarViews),
// porque openPairs es módulo-local del calendario y su run abre el detalle del par.

// Items recientes del clipboard como entradas del palette (spec §3.2: texto = primeras
// palabras; acción = copiar). Misma normalización de tipo que board-model.clipboardRows.
registerPaletteSource(() => {
  if (!Bridge.desktopApp) return [];
  return (live.clipboardHistory || []).slice(0, 5).filter((it) => it && it.id != null).map((it) => {
    const t = (it.type ? String(it.type) : '').toLowerCase();
    const words = it.text == null ? '' : String(it.text).replace(/\s+/g, ' ').trim().split(' ').slice(0, 6).join(' ');
    const label = t === 'image' ? 'Image' : t === 'file' ? (words || 'File') : (words || 'Encrypted item');
    return {
      group: 'Clipboard',
      label,
      hint: 'copy',
      run: () => Bridge.call('copyClipboardEntry', String(it.id)).then(() => announce('Copied.')).catch(() => {}),
    };
  });
});

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
else boot();
