// app.js — Zync Master UI composition root. Vanilla ES module, no framework, no build step.
// This file is the shell, NOT the views: it owns the Bridge, the shared state (state/live/
// settings), theme + boot, the desktop shell chrome (sidebar, view header, offline banner),
// the view registry, the command-palette sources, the sync state machine, and the shared
// helpers/fragments. Every screen lives in ui/js/views/<name>.js and is wired in through the
// shared `ctx` object (no view imports app.js — zero import cycles).
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
import { registerCalendarDayView } from './views/calendar-day.js';
import { registerClipboardViews } from './views/clipboard.js';
import { registerDevicesViews } from './views/devices.js';
import { registerSettingsViews } from './views/settings.js';
import { registerAuthViews } from './views/auth.js';

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
  // Dark is the product default (brand: dark surfaces + aqua/cyan accents). 'auto' (follow the
  // OS) and 'light' exist only as explicit user choices from Settings — a fresh profile must
  // never boot into the light/terra palette just because the OS prefers light (VDI browsers do).
  try { return localStorage.getItem(THEME_KEY) || 'dark'; } catch (_) { return 'dark'; }
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
// no channel to read the host's .NET assembly version.
// Mantener sincronizado con <Version> en src/ZyncMaster.App/ZyncMaster.App.csproj — ÚNICA otra fuente.
const VERSION = '0.3.8';
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

// Shared section/row builders for the settings screens (consumed by the Settings, Calendar and
// Clipboard modules through ctx — they stay here as shared fragments).
function cfgSection(head, ...rows) {
  const s = el('div', { class: 'glass glass--card config-section' }, el('div', { class: 'config-section__hd', text: head }));
  rows.flat().forEach((r) => { if (r) s.append(r); });
  return s;
}
function cfgRow(label, hint, control) {
  return el('div', { class: 'cfg-row' },
    el('div', null, el('div', { class: 'cfg-row__label', text: label }), hint || null), control);
}

// ---------------- Screen: Clipboard module ----------------
// Desktop App only. Two parts (matching the glass-settings / settings-natural-accordion mocks):
//   1. "This device" — the per-machine preferences (auto-sync, send, receive, viewer hotkey,
//      viewer density, show-hints). Edits persist through updateClipboardSettings for THIS device.
//   2. "Your devices" — a collapsed-by-default accordion (a section-head with a clear rotating
//      chevron, NOT a button-box). Each device shows a status dot, its name, a "this device" badge,
//      a last-seen line and compact per-device send/receive toggles. Editing works even offline.
// All values come from getClipboardDevices; the screen never fabricates a device.

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

// --- Identity re-resolution (focus / light poll) ---------------------------------------------
// The signed-in panel renders from the IN-MEMORY identityAuth snapshot, but the truth is the
// on-disk token store the host reads (FileIdentityTokenCache) — the SAME store getIdentityState
// resolves. Those can drift: a sign-out/expiry/refresh on the host side, or a sign-in that
// completed in another window, leaves the snapshot stale (the "SIGNED IN but not registered"
// contradiction). So re-resolve against the store when the window regains focus / becomes visible,
// and on a light background poll, instead of trusting the snapshot. refreshIdentity() repaints only
// when the signed-in flag actually flips, so a no-change re-resolve is silent (no flicker).
const IDENTITY_RERESOLVE_POLL_MS = 60000;
let identityReResolvePoll = null;

// Re-pull identity from the host UNLESS a sign-in is mid-flight (a login()/magic-link round-trip
// owns the gate then — refreshIdentity already runs on its own completion path, and re-resolving
// here would race it). Desktop App only; a no-op until the boot identity gate has resolved once.
function reResolveIdentity() {
  if (!Bridge.desktopApp || !identityAuth.resolved) return;
  if (identityAuth.loading || identityAuth.magicLinkSent) return;
  refreshIdentity();
}

// Wire the focus / visibility / poll re-resolution. Idempotent guard via the poll handle so a
// double boot (defensive) never stacks intervals. Called from boot() in the desktop App only.
function startIdentityReResolution() {
  if (!Bridge.desktopApp || identityReResolvePoll) return;
  // Window regained focus or tab became visible: cheap to re-check, catches an out-of-band sign-out
  // / expiry / sibling-window sign-in the moment the user returns to the panel.
  window.addEventListener('focus', reResolveIdentity);
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') reResolveIdentity();
  });
  // Light steady poll so a change is reflected even while the window stays focused (e.g. the host
  // refreshed/rotated the token, or it expired). 60s is well inside the token lifetime.
  identityReResolvePoll = setInterval(reResolveIdentity, IDENTITY_RERESOLVE_POLL_MS);
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
      // resetSessionState vive en views/settings.js (owner del Settings hub); re-expuesto en ctx.
      if (ctx.resetSessionState) ctx.resetSessionState();
      state.view = 'home';
      rerender();
    });
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
    ctx.gates.renderSignIn(root);
    void root.offsetWidth;
    root.classList.add('enter');
    return;
  }

  // Desktop App identity gate: in native/loopback, until getIdentityState reports signed-in the
  // only screen is the identity sign-in card (Microsoft / magic-link). The web panel and the mock
  // demo do NOT use this gate.
  if (Bridge.desktopApp && identityAuth.resolved && !identityAuth.signedIn) {
    ctx.gates.renderIdentitySignIn(root);
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

// ---------------- Config push to host ----------------
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

    // Las suscripciones de historial (clipboard:item / :deleted / :key) viven ahora en
    // views/clipboard.js (registerClipboardViews), junto a las funciones que disparan —
    // se registran al cargar el módulo, antes de boot().

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
      // Keep the signed-in panel honest: re-resolve identity against the host's on-disk token store
      // on focus / visibility / a light poll, instead of trusting the in-memory snapshot. This is
      // what corrects a stale "SIGNED IN" after an out-of-band sign-out / expiry / sibling sign-in.
      startIdentityReResolution();
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
  // sync runtime
  runPairNow, setPairState, deletePair, syncAllPairs, runSync, syncPairRemote,
  // calendar module (extraído a views/calendar.js)
  openPairs, PAIRS,
  calendarStatusDot: () => calendarDot(live.pairs, live.loadedPairs),
  // clipboard module (extraído a views/clipboard.js): el módulo re-expone loadClipboardHistory
  // en ctx (lo consumen home/palette) y resetClipboardViewState (lo invoca resetSessionState).
  clipboardStatusDot: () => clipboardDot(thisClipboardDevice()),
  // devices module (views/devices.js)
  devicesStatusDot: () => devicesDot((live.clipboardDevices && live.clipboardDevices.devices) || []),
  // settings module (views/settings.js): pushConfig + sign-out helpers se quedan en app.js.
  // El módulo re-expone resetSessionState en ctx (lo invoca signOutApp arriba).
  pushConfig, signOutApp, signOutWeb,
  // auth module (views/auth.js): los helpers de sesión se quedan en app.js (boot/gates) y entran
  // aquí; el módulo re-expone ctx.gates.{renderSignIn,renderIdentitySignIn} para los gates.
  startMicrosoftLogin, retryMicrosoftLogin, cancelAppLogin, startMagicLinkLogin,
  // tema + versión
  applyTheme, storedTheme, resolveTheme, VERSION,
};

// ---------------- View registry ----------------
// Cada vista vive en ui/js/views/<name>.js y registra sus ids vía register<Name>Views(ctx).
// El sidebar se construye desde nav/statusDot. Clipboard primero: re-expone ctx.loadClipboardHistory
// antes de que registerHomeViews lo destructure de ctx (home pinta el clipboard reciente del tablero
// today). Settings/Auth re-exponen ctx.resetSessionState / ctx.gates para los helpers compartidos.
registerClipboardViews(ctx);
registerHomeViews(ctx);
registerCalendarViews(ctx);
registerCalendarDayView(ctx);
registerDevicesViews(ctx);
registerSettingsViews(ctx);
registerAuthViews(ctx);

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
