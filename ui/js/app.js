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

// ---------------- Aurora + ARIA ----------------
function setAurora(s) { const w = $('#win'); if (w) w.dataset.aurora = s; }
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

const MODULES = [
  { id: 'calendar',  title: 'Calendar Sync',  icon: 'calendar',  active: true,  stat: '2 calendars', sub: 'Last sync 2 min ago' },
  { id: 'clipboard', title: 'Clipboard Sync', icon: 'clipboard', active: false, stat: 'Coming soon', sub: 'Mirror your clipboard across devices' },
  { id: 'files',     title: 'File Sync',      icon: 'folder',    active: false, stat: 'Coming soon', sub: 'Keep folders in sync' },
  { id: 'bookmarks', title: 'Bookmark Sync',  icon: 'bookmark',  active: false, stat: 'Coming soon', sub: 'Browser bookmarks, everywhere' },
  { id: 'notes',     title: 'Notes Sync',     icon: 'note',      active: false, stat: 'Coming soon', sub: 'Sticky notes & memos' },
  { id: 'tabs',      title: 'Tab Sync',       icon: 'tab',       active: false, stat: 'Coming soon', sub: 'Open tabs across browsers' },
];

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

const CALENDAR_LIBRARY = [
  { id: 'c-out-personal', svc: 'Outlook', acct: 'Personal',      email: 'daniel@outlook.com' },
  { id: 'c-out-work',     svc: 'Outlook', acct: 'Work Calendar', email: 'd.lopez@acme.com' },
  { id: 'c-gmail',        svc: 'Gmail',   acct: 'daniel.lopez',  email: 'daniel.lopez@gmail.com' },
  { id: 'c-ic-family',    svc: 'iCloud',  acct: 'Family',        email: 'daniel@icloud.com' },
  { id: 'c-ic-mirror',    svc: 'iCloud',  acct: 'Mirror',        email: 'daniel@icloud.com' },
];

const PROVIDERS = [
  { id: 'outlook',  name: 'Microsoft 365',   sub: 'Outlook · Office 365',   tone: 'azure', letter: 'M' },
  { id: 'google',   name: 'Google Calendar', sub: 'Gmail · Workspace',      tone: 'warn',  letter: 'G' },
  { id: 'icloud',   name: 'iCloud Calendar', sub: 'Apple ID',                    tone: 'ink',   letter: 'i' },
  { id: 'exchange', name: 'Exchange Server', sub: 'Self-hosted · IMAP/EWS', tone: 'terra', letter: 'E' },
];

const DISCOVERED = [
  { id: 'd1', name: 'Personal',          color: '#5b8cff', desc: 'Default · 142 events' },
  { id: 'd2', name: 'Family',            color: '#4ec07f', desc: 'Shared with 3 people' },
  { id: 'd3', name: 'Birthdays',         color: '#f4b53b', desc: 'Read-only · auto-generated' },
  { id: 'd4', name: 'Travel',            color: '#d97757', desc: '12 upcoming trips' },
  { id: 'd5', name: 'Holidays in Spain', color: '#9b7adf', desc: 'Subscribed · public' },
];

const STATUS = {
  ok:       { label: 'Connected',   dot: 'ok' },
  syncing:  { label: 'Syncing…', dot: 'sync' },
  success:  { label: 'Up to date',  dot: 'ok' },
  error:    { label: 'Sync failed', dot: 'error' },
  offline:  { label: 'Offline',     dot: 'offline' },
  unpaired: { label: 'Not paired',  dot: 'warn' },
  paused:   { label: 'Paused',      dot: 'warn' },
};

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
    // The error screen is now the determined paint — hand the splash off to it.
    maybeDismissLaunch();
    return;
  }
  serverHealth.checked = true;
  serverHealth.waking = true;
  rerender();
  // The "waking up" warm-up screen owns the view while we keep polling; drop the splash onto it
  // rather than holding the splash for the (still pending) identity gate.
  maybeDismissLaunch();
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
    .catch(() => { identityAuth.resolved = true; identityAuth.signedIn = false; rerender(); })
    .finally(() => maybeDismissLaunch(450));
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

// Real dashboard stats for ANY transport with a bridge (desktop App native/loopback AND the
// browser web panel), derived from the pairs the shell already loads. "Items synced" sums
// created+updated+deleted across each pair's lastResult; "Sync runs" counts pairs that have a
// recorded run. The server's MirrorResult exposes no failure count, so "Conflicts" has no real
// value to show: it renders an em-dash placeholder rather than a fabricated 0. Until the pairs
// snapshot has loaded, every cell is an em-dash — never a demo number.
function liveHomeStats() {
  const dash = '—';
  if (!live.loadedPairs) return { items: dash, runs: dash, conflicts: dash };
  const pairs = live.pairs || [];
  let items = 0, runs = 0;
  pairs.forEach((p) => {
    const lr = p && p.lastResult;
    if (lr) {
      runs += 1;
      items += (lr.created || 0) + (lr.updated || 0) + (lr.deleted || 0);
    }
  });
  return {
    items: items.toLocaleString(),
    runs: String(runs),
    conflicts: dash,
  };
}

// Real module tiles for any bridged transport. Only the Calendar module ships today; its tile
// reflects the real pair state instead of the hardcoded demo ("2 calendars / Last sync 2 min
// ago / Active"). With one or more active pairs it shows "Active" + the calendar count; with
// none it shows an impersonal "Set up" state (no fake last-sync, no fake counts) while still
// opening the Calendar screen so the user can add the first pair. The remaining modules stay
// "Coming soon" exactly as in the catalog. Never returns the demo MODULES literals.
function liveModules(activePairCount) {
  const count = activePairCount || 0;
  const calendar = count > 0
    ? {
        id: 'calendar', title: 'Calendar Sync', icon: 'calendar', active: true, opens: true, route: 'calendar',
        stat: `${count} ${count === 1 ? 'pair' : 'pairs'}`,
        sub: live.loadedPairs ? 'Active sync pairs' : 'Loading…',
      }
    : {
        id: 'calendar', title: 'Calendar Sync', icon: 'calendar', active: false, opens: true, route: 'calendar',
        stat: '', sub: live.loadedPairs ? 'No pairs yet' : 'Loading…',
      };
  // Reuse the catalog for the remaining modules so titles/icons/sub stay in one place. Clipboard Sync
  // is shipped but DESKTOP-ONLY (it needs native clipboard access), so it opens its screen only in the
  // App; in the web panel it stays "coming soon" like the not-yet-shipped modules.
  const rest = MODULES.filter((m) => m.id !== 'calendar').map((m) => {
    if (m.id === 'clipboard' && Bridge.desktopApp) {
      return { ...m, opens: true, route: 'clipboard-settings', stat: '' };
    }
    return { ...m, opens: false };
  });
  return [calendar, ...rest];
}

// ---------------- Screen: Home ----------------
function renderHome(root) {
  const cfg = STATUS[state.sync];
  const statusLabel =
    state.sync === 'syncing' ? 'Syncing…' :
    state.sync === 'offline' ? 'Offline' :
    state.sync === 'error' ? 'Sync failed' :
    state.sync === 'unpaired' ? 'Not paired' : 'All systems synced';

  root.append(viewHeader('Dashboard'));

  const stat = (val, lab) => el('div', { class: 'stat' },
    el('div', { class: 'stat__val num', text: val }),
    el('div', { class: 'stat__lab', text: lab }),
  );

  // In any real transport (desktop App or web panel) we must never show fabricated demo
  // numbers to a real signed-in user. Derive the dashboard stats from the real pairs the shell
  // already loads; where no real value exists yet (snapshot not loaded) fall back to an em-dash
  // placeholder. The literal 1,248 / 24 / 0 demo numbers are kept ONLY for the standalone mock
  // (file://) demo. On a fresh, unpaired first run with a bridge, every cell is an em-dash.
  const homeStats = Bridge.available ? liveHomeStats() : { items: '1,248', runs: '24', conflicts: '0' };

  root.append(el('div', { class: 'glass glass--card stats-card' },
    el('div', { class: 'stats-card__hd' },
      el('span', { class: 'status-dot', dataset: { state: cfg.dot } }),
      el('span', { class: 'stats-card__status', text: statusLabel }),
      el('span', { class: 'stats-card__sub', text: Bridge.available ? 'LAST RUN' : 'THIS WEEK' }),
    ),
    el('div', { class: 'stats-grid' },
      stat(homeStats.items, 'Items synced'),
      el('div', { class: 'stat__sep' }),
      stat(homeStats.runs, 'Sync runs'),
      el('div', { class: 'stat__sep' }),
      stat(homeStats.conflicts, 'Conflicts'),
    ),
  ));

  // Modules summary line: "N active · M available". In any real transport, N is the count of
  // active real pairs (0 on a fresh unpaired run); M stays the catalog size (the single Calendar
  // module is the only one shipped today, so "available" mirrors the catalog count). Mock keeps
  // its demo line ("1 active").
  const activePairCount = (live.pairs || []).filter((p) => p && p.state === 'active').length;
  const moduleActive = Bridge.available ? String(activePairCount) : '1';
  root.append(el('div', { class: 'section-head' },
    el('span', { class: 'section-head__title', text: 'Sync modules' }),
    el('span', { class: 'section-head__action', style: 'pointer-events:none;color:var(--ink-3)' },
      el('span', { class: 'num', text: moduleActive }), ' active · ', el('span', { class: 'num', text: '5' }), ' available'),
  ));

  // Any real transport: ensure the pairs snapshot the stats/modules above derive from is loaded,
  // and repaint once it lands (the first home paint may precede any pairs fetch). pairsAttempted
  // fires this at most once so a 401/empty response does not loop.
  if (Bridge.available && !live.loadedPairs && !live.loadingPairs && !live.pairsAttempted) {
    live.pairsAttempted = true;  // fire once; rerenderInPlace below must not re-trigger this on a 401
    loadPairs().then(() => { if (state.view === 'home') rerenderInPlace(); });
  }

  // In a real transport the module tiles must reflect the real state: with no active pairs the
  // Calendar module is "not configured" (inactive, no fake "2 calendars / 2 min ago" line),
  // never the hardcoded demo. Mock keeps the demo MODULES untouched for the standalone file://
  // walkthrough.
  const modules = Bridge.available ? liveModules(activePairCount) : MODULES;
  const grid = el('div', { class: 'module-grid' });
  modules.forEach((m) => {
    // A tile is navigable (clickable) when it is active OR explicitly opens (the shipped Calendar
    // module always opens its screen even with zero pairs, so the user can add the first pair).
    const navigable = m.active || m.opens;
    const footChip = m.active
      ? el('span', { class: 'chip chip--ok' },
          el('span', { class: 'status-dot', dataset: { state: 'ok' }, style: 'width:6px;height:6px' }), 'Active')
      : el('span', { class: 'chip chip--skipped', text: m.opens ? 'Set up' : 'Soon' });
    const tile = el('button', {
      class: 'module-tile glass',
      dataset: { active: String(m.active) },
      disabled: !navigable,
      onclick: () => { if (navigable) navigate(m.route || 'calendar'); },
    },
      el('div', { class: 'module-tile__icon', html: icon(m.icon, { size: 20, stroke: 1.6 }) }),
      el('div', { class: 'module-tile__title', text: m.title }),
      el('div', { class: 'module-tile__sub', text: m.sub }),
      el('div', { class: 'module-tile__foot' },
        footChip,
        el('span', { class: 'module-tile__stat num', text: m.stat }),
      ),
    );
    grid.append(tile);
  });
  root.append(grid);
}

// ---------------- Screen: Calendar Sync (accordion) ----------------
const openPairs = new Set(['p1']);

function pairAccordion(pair) {
  // p3 mirrors the global syncing state so the demo can drive a live progress bar.
  const liveState = pair.id === 'p3'
    ? (state.sync === 'syncing' ? 'syncing' : state.sync === 'error' ? 'error' : state.sync === 'offline' ? 'offline' : 'ok')
    : pair.state;
  const isSyncing = liveState === 'syncing';
  const isError = liveState === 'error';
  const isOffline = liveState === 'offline';
  // Real, client-tracked single-flight state for a live pair: a sync (local or remote request) is in
  // flight right now. Distinct from the demo p3 progress path (isSyncing). When busy, the Sync now
  // button shows a spinner, is disabled, and additional clicks are ignored.
  const busy = !!pair.inFlight;
  const dotState = (isSyncing || busy) ? 'sync' : isError ? 'error' : isOffline ? 'offline' : 'ok';
  const open = openPairs.has(pair.id);
  const progress = (pair.id === 'p3' && state.sync === 'syncing') ? state.progress : { done: 0, total: 1 };
  const nextStr = isOffline || isSyncing ? '—' : fmtMMSS(pair.nextSync);

  const card = el('div', { class: `pair glass glass--card${open ? ' is-open' : ''}`, dataset: { state: liveState } });

  const head = el('button', { class: 'pair__head', 'aria-expanded': String(open), onclick: () => togglePair(pair.id) },
    el('div', { class: 'pair__route' },
      pairBadge(pair.src.svc),
      el('div', { class: 'pair__route-text' }, el('span', { class: 'pair__name', text: `${pair.src.svc} · ${pair.src.acct}` })),
      el('span', { class: 'pair__arrow-ico', html: icon('arrowright', { size: 11, stroke: 1.8 }) }),
      pairBadge(pair.dst.svc),
      el('div', { class: 'pair__route-text' }, el('span', { class: 'pair__name', text: `${pair.dst.svc} · ${pair.dst.acct}` })),
    ),
    el('div', { class: 'pair__meta' },
      el('span', { class: 'status-dot', dataset: { state: dotState }, style: 'width:7px;height:7px' }),
      el('span', { class: 'pair__last num', text: pair.lastSync }),
      el('span', { class: `pair__chevron${open ? ' pair__chevron--open' : ''}`, html: icon('chevrondown', { size: 14, stroke: 1.8 }) }),
    ),
  );
  card.append(head);

  const substat = (lab, val) => el('span', null,
    el('span', { class: 'route__stat-label', text: lab }), el('span', { class: 'route__stat-val num', text: String(val) }));

  // Track B — COM device-pinning. The "Sync now" button is disabled when:
  //   comOffline   — the pair reads Outlook on ANOTHER device whose lease has expired (a local run /
  //                  request-sync would be a no-op);
  //   comUnclaimed — the source is COM but no device has claimed it yet AND this device cannot read
  //                  COM, so there is no origin to run it (a local run would fail immediately).
  // Otherwise the click routes either to a local run (this PC is the origin) or to a request-sync
  // signal (the origin runs it).
  const comBlocked = pair.comOffline || pair.comUnclaimed;
  // a11y — a disabled button is not focusable and its `title` is not announced by screen readers, so
  // carry the disabled reason in aria-label and point aria-describedby at the pin-note element below.
  const pinNoteId = pair.id ? `pin-note-${pair.id}` : null;
  const disabledReason = isOffline
    ? 'Sync now unavailable — the app is offline'
    : pair.comOffline
      ? `Sync now unavailable — origin device ${pair.pinnedDeviceName || 'is'} is offline`
      : pair.comUnclaimed
        ? 'Sync now unavailable — no source device has claimed this sync yet'
        : '';
  const syncBtnAttrs = {
    class: 'pair__sync-btn',
    // Single-flight: disabled while a run is in flight (busy) so stacked clicks cannot launch more.
    disabled: isOffline || comBlocked || busy,
    title: busy ? 'Sync in progress…' : disabledReason,
    onclick: (e) => {
      e.stopPropagation();
      if (busy) return;                          // single-flight guard at the click site too
      if (!Bridge.available || !pair.id) { runSync(); return; }
      if (pair.comRemote) syncPairRemote(pair); else runPairNow(pair.id);
    },
  };
  if (busy) {
    // a11y — announce the busy state on the control itself; aria-busy reflects the in-flight work.
    syncBtnAttrs['aria-busy'] = 'true';
    syncBtnAttrs['aria-label'] = 'Sync in progress';
  } else if (disabledReason) {
    syncBtnAttrs['aria-label'] = disabledReason;
    if (pinNoteId) syncBtnAttrs['aria-describedby'] = pinNoteId;
  }
  // Spinner whenever busy (real in-flight) OR the demo p3 progress path is active.
  const spinning = busy || isSyncing;
  const syncBtnLabel = busy ? 'Syncing…' : isSyncing ? `${progress.done}/${progress.total}` : 'Sync now';
  const syncBtn = el('button', syncBtnAttrs,
    spinning ? el('span', { class: 'spinner', style: 'width:12px;height:12px;border-width:1.6px' }) : el('span', { style: 'display:inline-flex', html: icon('sync', { size: 12, stroke: 1.8 }) }),
    el('span', { class: 'num', text: syncBtnLabel }),
  );
  // Header layout (point 2): in a ~340px window all five items + the button cannot share one row
  // without the "Sync now" button being clipped at the right edge. So the stats (Events / Next /
  // Status) sit on their own row, and "Sync now" gets a dedicated full-width row directly below —
  // the button is therefore always complete and clickable. "Attempt N" is moved OUT of this row (it
  // was the item pushing the button off-screen) and is rendered as a sub-badge next to "Recent
  // events" further down.
  const substatChildren = [
    substat('Events', pair.eventCount),
    el('span', null, el('span', { class: 'route__stat-label', text: 'Next' }), el('span', { class: 'route__stat-val num', text: nextStr })),
    el('span', null, el('span', { class: 'route__stat-label', text: 'Status' }),
      el('span', { class: 'route__stat-val', text: (isSyncing || busy) ? 'Syncing…' : isError ? 'Failed' : isOffline ? 'Offline' : 'Connected' })),
  ];
  card.append(el('div', { class: 'pair__substats' }, ...substatChildren));
  // Sync now on its own row, full-width — never clipped.
  card.append(el('div', { class: 'pair__sync-row' }, syncBtn));

  // Track B — COM device-pinning note. Tells the user WHERE this pair's Outlook source is read:
  //   local     → "Source is on this PC" (this device runs it);
  //   remote    → "Runs on <device>" (another device is the origin), with an "origin offline" hint
  //               when that device's lease has expired so the signal cannot be picked up right now;
  //   unclaimed → neutral "No source device yet" — the source is COM but no device has claimed it and
  //               this device cannot read COM, so we do NOT assert it runs here.
  if (pair.srcCom && (pair.comLocal || pair.comRemote || pair.comUnclaimed)) {
    const kind = pair.comLocal ? 'local' : pair.comUnclaimed ? 'unclaimed' : (pair.comOffline ? 'offline' : 'remote');
    const pinNoteOpts = { class: 'pair__pin-note', dataset: { kind } };
    if (pinNoteId) pinNoteOpts.id = pinNoteId;
    const pinNote = el('div', pinNoteOpts);
    const noteIcon = pair.comLocal ? 'check' : pair.comUnclaimed ? 'pin' : 'sync';
    pinNote.append(el('span', { style: 'display:inline-flex', html: icon(noteIcon, { size: 12, stroke: 1.8 }) }));
    if (pair.comLocal) {
      pinNote.append(el('span', { text: 'Source is on this PC' }));
    } else if (pair.comUnclaimed) {
      pinNote.append(el('span', { text: 'No source device yet' }));
      pinNote.append(el('span', { class: 'pair__pin-note-sub', text: '· open the app where Outlook is installed' }));
    } else {
      const who = pair.pinnedDeviceName || 'another device';
      pinNote.append(el('span', { text: `Runs on ${who}` }));
      if (pair.comOffline) pinNote.append(el('span', { class: 'pair__pin-note-sub', text: '· origin offline' }));
    }
    card.append(pinNote);
  }

  if (open) {
    const body = el('div', { class: 'pair__body' });
    const block = (label, name, email) => el('div', { class: 'pair__route-block' },
      el('div', { class: 'route__label', text: label }),
      el('div', { class: 'pair__route-name-full', text: name }),
      el('div', { class: 'pair__route-email', text: email }),
    );
    body.append(el('div', { class: 'pair__route-detail' },
      block('SOURCE', `${pair.src.svc} · ${pair.src.acct}`, pair.src.email),
      el('div', { class: 'pair__route-divider', html: icon('arrowright', { size: 14, stroke: 1.8 }) }),
      block('DESTINATION', `${pair.dst.svc} · ${pair.dst.acct}`, pair.dst.email),
    ));

    if (isSyncing) {
      const pct = (progress.done / progress.total) * 100;
      const nowTitle = pair.events[progress.done % pair.events.length]?.title || '…';
      body.append(el('div', { class: 'route__progress', style: 'margin-top:0' },
        el('div', { class: 'route__progress-head' }, el('span', { text: 'Mirroring events' }),
          el('span', { class: 'num', text: `${progress.done} / ${progress.total}` })),
        el('div', { class: 'route__progress-bar' }, el('div', { style: `width:${pct}%` })),
        el('div', { class: 'route__progress-now' },
          el('span', { class: 'status-dot', dataset: { state: 'sync' }, style: 'width:6px;height:6px' }),
          el('span', { class: 'route__progress-item', text: nowTitle })),
      ));
    }

    // RECENT EVENTS — driven by the per-pair session log (every attempt, ok + fail) when present,
    // otherwise the last-run summary from the server. The counter reflects how many rows are shown.
    const activityHead = el('div', { class: 'pair__activity-head' },
      el('span', { text: 'Recent events' }),
      el('span', { class: 'num', text: String(pair.events.length) }));
    // "Attempt N" relocated here (out of the cramped header row) as a subtle sub-badge.
    if (pair.attempts > 0) {
      activityHead.append(el('span', { class: 'pair__attempts', title: `${pair.attempts} sync ${pair.attempts === 1 ? 'attempt' : 'attempts'} this session` },
        el('span', { class: 'num', text: `Attempt ${pair.attempts}` })));
    }
    body.append(activityHead);
    const act = el('div', { class: 'pair__activity' });
    if (pair.events.length) pair.events.forEach((row) => act.append(activityRow(row)));
    else act.append(el('div', { class: 'activity__sub', style: 'padding:8px 2px', text: 'No changes yet.' }));
    body.append(act);

    // Live controls (native shell only): pause/resume, disable/enable, delete.
    if (Bridge.available && pair.id) {
      const paused = pair.serverState === 'paused';
      const disabled = pair.serverState === 'disabled';
      // Footer controls live on a SINGLE line and are ICON-ONLY so all five fit, uncut, even in a
      // ~340px-wide window. Each button carries an aria-label + title (tooltip) describing its action;
      // there is no visible text label to truncate. Layout/sizing lives in layout.css (.pair__ctrl-btn
      // is a fixed square icon button). Delete sits last, tinted with --err.
      const controls = el('div', { class: 'pair__controls' });

      const pauseLabel = paused ? 'Resume' : 'Pause';
      controls.append(el('button', { class: 'btn btn--ghost pair__ctrl-btn', 'aria-label': pauseLabel, title: pauseLabel,
        onclick: (e) => { e.stopPropagation(); setPairState(pair.id, paused ? 'active' : 'paused'); } },
        iconEl(paused ? 'sync' : 'pause', 15, 1.8)));

      const disableLabel = disabled ? 'Enable' : 'Disable';
      controls.append(el('button', { class: 'btn btn--ghost pair__ctrl-btn', 'aria-label': disableLabel, title: disableLabel,
        onclick: (e) => { e.stopPropagation(); setPairState(pair.id, disabled ? 'active' : 'disabled'); } },
        iconEl(disabled ? 'check' : 'disable', 15, 1.8)));

      // Export .txt — per-pair export of the pair's SOURCE calendar. Routing is by source
      // provider, inside openExportTxtModal: a COM source exports the local Outlook via
      // generateTxt (requires Outlook installed); a Graph source exports via the server, which then
      // saves the .txt through a desktop save dialog. BOTH write to a local path, so the whole
      // affordance is desktop-only — the browser panel has no save-to-disk channel (exportSourceTxt
      // is inert in web mode and would falsely report "File saved."). Gate the button to
      // Bridge.desktopApp so it never shows in the web panel.
      if (Bridge.desktopApp) {
        const isCom = (pair.src && pair.src.provider || '').toLowerCase() === 'outlookcom';
        const comBlocked = isCom && !comAvailable();
        const txtTitle = comBlocked ? 'Outlook is not available on this device' : 'Export .txt';
        const txtBtn = el('button', { class: 'btn btn--ghost pair__ctrl-btn',
          disabled: comBlocked, 'aria-label': 'Export .txt', title: txtTitle,
          onclick: (e) => { e.stopPropagation(); if (!comBlocked) openExportTxtModal(pair); } },
          iconEl('download', 15, 1.7));
        controls.append(txtBtn);
      }

      // Edit — open the wizard preloaded with this pair (F2). Reuses renderAddPairLive in edit mode.
      controls.append(el('button', { class: 'btn btn--ghost pair__ctrl-btn', 'aria-label': 'Edit', title: 'Edit',
        onclick: (e) => { e.stopPropagation(); startEditPair(pair.id); } },
        iconEl('pencil', 15, 1.8)));

      controls.append(el('button', { class: 'btn btn--ghost pair__ctrl-btn pair__ctrl-btn--danger', 'aria-label': 'Delete', title: 'Delete',
        onclick: (e) => { e.stopPropagation(); deletePair(pair.id); } },
        iconEl('trash', 15, 1.7)));

      body.append(controls);
    }

    card.append(body);
  }
  return card;
}

function renderCalendar(root) {
  const addPairBtn = el('button', { class: 'btn btn--ghost', style: 'height:28px;padding:0 8px', onclick: () => navigate('add-pair') },
    iconEl('plus', 12, 2), el('span', { style: 'font-size:12px', text: 'Add pair' }));
  root.append(viewHeader('Calendar Sync', { onBack: () => navigate('home'), action: addPairBtn }));

  // Bridge: render the live pairs snapshot (refreshing in the background). Browser: mock.
  const pairs = Bridge.available
    ? (live.pairs || []).map(pairViewModel)
    : PAIRS;

  const list = el('div', { class: 'pair-list' });
  if (Bridge.available && pairs.length === 0) {
    list.append(el('div', { class: 'glass glass--card', style: 'padding:18px;text-align:center;color:var(--ink-3)' },
      el('div', { text: live.loadedPairs ? 'No sync pairs yet.' : 'Loading sync pairs…' })));
  } else {
    pairs.forEach((p) => list.append(pairAccordion(p)));
  }
  root.append(list);

  // Kick a one-shot background refresh the first time we open this screen; repaint when it
  // lands. Live actions (run/pause/delete) refresh explicitly, so we don't poll on paint.
  if (Bridge.available && !live.loadedPairs && !live.loadingPairs) {
    loadPairs().then(() => { if (state.view === 'calendar') rerenderInPlace(); });
  }
}

// ---------------- Wizard stepper (shared) ----------------
function wizardStepper(step, labels) {
  const stepper = el('div', { class: 'stepper' });
  labels.forEach((lab, i) => {
    const st = i < step ? 'done' : i === step ? 'active' : null;
    stepper.append(el('div', { class: 'stepper__dot', dataset: st ? { state: st } : {}, html: i < step ? icon('check', { size: 11, stroke: 2.4 }) : '' },
      i < step ? '' : String(i + 1)));
    if (i < labels.length - 1) stepper.append(el('div', { class: 'stepper__line', dataset: i < step ? { state: 'done' } : {} }));
  });
  const labelsRow = el('div', { class: 'wizard-stepper-labels' });
  labels.forEach((lab, i) => labelsRow.append(el('div', { class: i === step ? 'is-active' : i < step ? 'is-done' : '', text: lab })));
  return el('div', { class: 'glass glass--card wizard-stepper' }, stepper, labelsRow);
}

// shared slider + interval rows
function sliderRow(label, get, set) {
  const fill = el('div', { class: 'slider__fill' });
  const thumb = el('div', { class: 'slider__thumb' });
  const slider = el('div', { class: 'slider' }, fill, thumb);
  const hintVal = el('b', { class: 'num', style: 'color:var(--ink-1)', text: String(get()) });
  const paint = () => { const pct = ((get() - 7) / 23) * 100; fill.style.width = `${pct}%`; thumb.style.left = `${pct}%`; hintVal.textContent = String(get()); };
  slider.addEventListener('click', (e) => {
    const r = slider.getBoundingClientRect();
    const pct = Math.min(1, Math.max(0, (e.clientX - r.left) / r.width));
    set(Math.round(7 + pct * 23)); paint();
  });
  paint();
  return el('div', { class: 'cfg-row' },
    el('div', null, el('div', { class: 'cfg-row__label', text: label }),
      el('div', { class: 'cfg-row__hint' }, 'Next ', hintVal, ' days · past events are never touched')), slider);
}
// intervalRow — segmented 5/15/30/60-minute control. Selecting an option updates aria-pressed on
// the buttons IN PLACE (the same technique the theme picker uses) instead of repainting the whole
// view, so picking an interval never reconstructs the screen or flickers. The caller's `set` still
// runs (to persist / update state) but must NOT trigger a full rerender for this control.
function intervalRow(get, set) {
  const seg = el('div', { class: 'segmented' });
  const buttons = [];
  [5, 15, 30, 60].forEach((n) => {
    const b = el('button', { class: 'segmented__item', 'aria-pressed': String(get() === n),
      onclick: () => { set(n); buttons.forEach((btn) => btn.setAttribute('aria-pressed', String(Number(btn.dataset.val) === n))); } },
      el('span', { class: 'num', text: String(n) }), 'm');
    b.dataset.val = String(n);
    buttons.push(b);
    seg.append(b);
  });
  return el('div', { class: 'cfg-row' },
    el('div', null, el('div', { class: 'cfg-row__label', text: 'Interval' }), el('div', { class: 'cfg-row__hint', text: 'How often we check for changes' })), seg);
}

// ---------------- Screen: Add Pair wizard ----------------
// Mock-flow state (browser standalone). Bridged flow uses addPairLive below.
const addPair = { step: 0, srcId: null, dstId: null, name: '', windowDays: 14, intervalMin: 15 };

// Bridged flow: source is local Outlook COM or an online account+calendar; destination is
// always an online account+calendar. accountRef/calendarId come from the host.
const addPairLive = {
  step: 0,
  editId: null,               // null = creating; a pair id = editing that pair (F2)
  sourceKind: null,           // 'com' | 'online'
  srcAccountRef: null, srcCalendarId: null, srcCalendarName: null,
  // Feature 2 — source multi-calendar selection. srcAllCalendars defaults ON ("all of the origin").
  // srcCalendarNames: COM display names; srcCalendarIds: Graph calendar ids (subset when not all).
  // The single srcCalendarId/srcCalendarName above are kept as the legacy/back-compat anchor (the
  // server still requires a non-empty source.calendarId), and as the label for the preview.
  srcAllCalendars: true, srcCalendarNames: [], srcCalendarIds: [],
  dstAccountRef: null, dstCalendarId: null, dstCalendarName: null,
  name: '', intervalMin: 15,
  // Originals captured on edit so step 3 can warn when source/destination changed (fresh start /
  // orphans left in the old destination). Unused when creating.
  origSourceKind: null, origSrcAccountRef: null, origSrcCalendarId: null,
  origSrcAllCalendars: true, origSrcCalendarNames: [], origSrcCalendarIds: [],
  origDstAccountRef: null, origDstCalendarId: null,
  origDstCalendarName: null,
  // Opt-in cleanup of the PREVIOUS destination when the destination is re-targeted (F2). Default
  // OFF — it is destructive. cleanupCount is the live "N events this pair copied there" the bridge
  // reports (null = not loaded yet, 'err' = the count call failed). The count is keyed to the old
  // destination so it is invalidated whenever that endpoint signature changes.
  cleanupPrevDest: false, cleanupCount: null, cleanupCountKey: null,
  // Inline error surfaced on the configure step (step 2) when create/updatePair fails. Kept on the
  // wizard state (not a local) so a failed save leaves the user on step 2 with everything they
  // entered intact and a red message + Retry, instead of silently dropping their work.
  submitError: null, submitting: false,
  // Subset mode (online source): a pending account switch awaiting confirmation. When the user taps
  // a calendar of a DIFFERENT account while a subset selection already exists, we stash the intent
  // here and show an inline confirm instead of silently wiping their prior selection.
  srcSwitchPrompt: null, // null | { accountRef, calendarId }
  // Account-accordion expand state for the wizard pickers (Sets of accountRefs). null = not yet
  // initialized; each picker lazily opens the selected/only account on first render.
  expandedSrc: null, expandedDst: null,
};
function resetAddPairLive() {
  Object.assign(addPairLive, {
    step: 0, editId: null, sourceKind: null,
    srcAccountRef: null, srcCalendarId: null, srcCalendarName: null,
    srcAllCalendars: true, srcCalendarNames: [], srcCalendarIds: [],
    dstAccountRef: null, dstCalendarId: null, dstCalendarName: null,
    name: '', intervalMin: 15,
    origSourceKind: null, origSrcAccountRef: null, origSrcCalendarId: null,
    origSrcAllCalendars: true, origSrcCalendarNames: [], origSrcCalendarIds: [],
    origDstAccountRef: null, origDstCalendarId: null, origDstCalendarName: null,
    cleanupPrevDest: false, cleanupCount: null, cleanupCountKey: null,
    submitError: null, submitting: false,
    srcSwitchPrompt: null,
    expandedSrc: null, expandedDst: null,
  });
}

// startEditPair(id) — preload the wizard from a SyncPair and open it in edit mode (F2). Looks the
// raw pair up in live.pairs (it carries source/destination; the view model does not). Captures the
// original source/destination so step 3 can warn when they change.
function startEditPair(id) {
  if (!Bridge.available || !id) return;
  const pair = (live.pairs || []).find((p) => p && p.id === id);
  if (!pair) return;
  const src = pair.source || {};
  const dst = pair.destination || {};
  const com = (src.provider || '').toLowerCase() === 'outlookcom';
  // Feature 2 — preload the source selection. A legacy pair has no allCalendars/calendarIds/
  // calendarNames; treat the absence as "all" (the server's legacy fallback also reads everything
  // configured), so an unedited legacy pair round-trips without forcing a subset.
  const srcNames = Array.isArray(src.calendarNames) ? src.calendarNames.slice() : [];
  const srcIds = Array.isArray(src.calendarIds) ? src.calendarIds.slice() : [];
  const hasSubset = com ? srcNames.length > 0 : srcIds.length > 0;
  const allCalendars = src.allCalendars === true || (!src.allCalendars && !hasSubset);
  Object.assign(addPairLive, {
    step: 0,
    editId: id,
    sourceKind: com ? 'com' : 'online',
    srcAccountRef: com ? null : (src.accountRef || null),
    srcCalendarId: src.calendarId || (com ? 'local' : null),
    srcCalendarName: src.calendarName || (com ? 'Outlook (this PC)' : null),
    srcAllCalendars: allCalendars,
    srcCalendarNames: srcNames,
    srcCalendarIds: srcIds,
    dstAccountRef: dst.accountRef || null,
    dstCalendarId: dst.calendarId || null,
    dstCalendarName: dst.calendarName || null,
    name: pair.name || '',
    intervalMin: pair.intervalMin || 15,
    origSourceKind: com ? 'com' : 'online',
    origSrcAccountRef: com ? null : (src.accountRef || null),
    origSrcCalendarId: src.calendarId || (com ? 'local' : null),
    origSrcAllCalendars: allCalendars,
    origSrcCalendarNames: srcNames.slice(),
    origSrcCalendarIds: srcIds.slice(),
    origDstAccountRef: dst.accountRef || null,
    origDstCalendarId: dst.calendarId || null,
    origDstCalendarName: dst.calendarName || null,
    cleanupPrevDest: false, cleanupCount: null, cleanupCountKey: null,
    submitError: null, submitting: false, srcSwitchPrompt: null,
    expandedSrc: null, expandedDst: null,
  });
  navigate('add-pair');
}

function calendarPicker(value, onChange, exclude) {
  const wrap = el('div', { class: 'glass glass--card', style: 'padding:4px' });
  const list = el('div', { class: 'cal-list' });
  CALENDAR_LIBRARY.forEach((c) => {
    const disabled = exclude.includes(c.id);
    const selected = value === c.id;
    list.append(el('button', { class: `cal-item${selected ? ' is-selected' : ''}`, disabled, onclick: () => onChange(c.id) },
      pairBadge(c.svc),
      el('div', null, el('div', { class: 'cal-item__name', text: `${c.svc} · ${c.acct}` }), el('div', { class: 'cal-item__sub', text: c.email })),
      el('div', { class: 'cal-item__check', html: selected ? icon('check', { size: 12, stroke: 2.6 }) : '' }),
    ));
  });
  wrap.append(list);
  wrap.append(el('button', { class: 'cal-add', onclick: () => { state.returnTo = 'add-pair'; navigate('add-calendar'); } },
    iconEl('plus', 13, 2.2), el('span', { text: 'Connect a new calendar account…' })));
  return wrap;
}

function renderAddPair(root) {
  if (Bridge.available) { renderAddPairLive(root); return; }
  const labels = ['Source', 'Destination', 'Configure'];
  const src = CALENDAR_LIBRARY.find((c) => c.id === addPair.srcId);
  const dst = CALENDAR_LIBRARY.find((c) => c.id === addPair.dstId);
  if (src && dst && !addPair.name) addPair.name = `${src.acct} → ${dst.acct}`;

  root.append(viewHeader('Add a sync pair', { onBack: () => { if (addPair.step === 0) navigate('calendar'); else { addPair.step--; rerender(); } } }));
  root.append(wizardStepper(addPair.step, labels));

  if (addPair.step === 0) {
    root.append(el('div', { class: 'wizard-title', text: 'Pick the source calendar' }));
    root.append(el('div', { class: 'wizard-sub', text: 'Changes here will be mirrored to the destination. The source is never modified.' }));
    root.append(calendarPicker(addPair.srcId, (id) => { addPair.srcId = id; rerender(); }, [addPair.dstId]));
    root.append(el('div', { class: 'wizard-foot' },
      el('button', { class: 'btn btn--ghost', text: 'Cancel', onclick: () => navigate('calendar') }),
      el('button', { class: 'btn btn--primary', disabled: !addPair.srcId, onclick: () => { addPair.step = 1; rerender(); } },
        el('span', { text: 'Continue' }), iconEl('arrowright', 14, 1.8))));
  } else if (addPair.step === 1) {
    root.append(el('div', { class: 'wizard-title', text: 'Pick the destination' }));
    root.append(el('div', { class: 'wizard-sub', text: 'Events will be written here. Past events on the destination are never touched.' }));
    root.append(calendarPicker(addPair.dstId, (id) => { addPair.dstId = id; rerender(); }, [addPair.srcId]));
    root.append(el('div', { class: 'wizard-foot' },
      el('button', { class: 'btn btn--ghost', text: 'Back', onclick: () => { addPair.step = 0; rerender(); } }),
      el('button', { class: 'btn btn--primary', disabled: !addPair.dstId, onclick: () => { addPair.step = 2; rerender(); } },
        el('span', { text: 'Continue' }), iconEl('arrowright', 14, 1.8))));
  } else if (addPair.step === 2 && src && dst) {
    root.append(el('div', { class: 'wizard-title', text: 'Configure the sync' }));
    root.append(el('div', { class: 'wizard-sub', text: 'Review the pair and tune how often it runs.' }));
    const col = (label, name, email) => el('div', { class: 'pair-preview__col' },
      el('div', { class: 'route__label', text: label }), el('div', { class: 'pair-preview__name', text: name }), el('div', { class: 'pair-preview__email', text: email }));
    root.append(el('div', { class: 'glass glass--card pair-preview' },
      pairBadge(src.svc), col('SOURCE', `${src.svc} · ${src.acct}`, src.email),
      el('span', { style: 'color:var(--ink-3);display:inline-flex', html: icon('arrowright', { size: 14, stroke: 1.8 }) }),
      pairBadge(dst.svc), col('DESTINATION', `${dst.svc} · ${dst.acct}`, dst.email)));

    const cfg = el('div', { class: 'glass glass--card config-section', style: 'margin-top:10px' });
    const nameInput = el('input', { class: 'field-input', value: addPair.name });
    nameInput.addEventListener('input', () => { addPair.name = nameInput.value; });
    cfg.append(el('div', { class: 'cfg-row' },
      el('div', null, el('div', { class: 'cfg-row__label', text: 'Pair name' }), el('div', { class: 'cfg-row__hint', text: 'Shown on your dashboard' })), nameInput));
    cfg.append(sliderRow('Sync window', () => addPair.windowDays, (v) => { addPair.windowDays = v; }));
    cfg.append(intervalRow(() => addPair.intervalMin, (v) => { addPair.intervalMin = v; }));
    root.append(cfg);

    root.append(el('div', { class: 'wizard-foot' },
      el('button', { class: 'btn btn--ghost', text: 'Back', onclick: () => { addPair.step = 1; rerender(); } }),
      el('button', { class: 'btn btn--primary', onclick: () => completeAddPair() }, iconEl('check', 14, 2.2), el('span', { text: 'Create pair' }))));
  }
}
function completeAddPair() {
  // Mock/standalone-only path (the bridged shell routes through renderAddPairLive →
  // createPair). No bridge call here: persisting a near-empty config would clobber settings.
  addPair.step = 0; addPair.srcId = null; addPair.dstId = null; addPair.name = '';
  navigate('calendar');
}

// ---------------- Add Pair wizard (bridged / live data) ----------------
// accountAccordionHead(acc, expandedSet, summary, active) — a clickable collapsible header for an
// account in the wizard pickers. Toggling flips membership in expandedSet (a Set of accountRefs kept
// on addPairLive, so it survives rerenders) and re-renders. `summary` is the right-side hint shown
// (e.g. calendar count / selection); `active` highlights the header when this account is the chosen
// source/destination. Returns { head, open } so the caller renders the calendars only when open.
function accountAccordionHead(acc, expandedSet, summary, active) {
  const open = expandedSet.has(acc.accountRef);
  const accName = acc.displayName || acc.accountRef;
  const head = el('button', {
    class: `cal-acct-head${open ? ' is-open' : ''}${active ? ' is-active' : ''}`,
    type: 'button', 'aria-expanded': String(open),
    onclick: () => {
      if (open) expandedSet.delete(acc.accountRef); else expandedSet.add(acc.accountRef);
      if (state.view === 'add-pair') rerender();
    },
  },
    el('span', { class: 'cal-acct-head__chev', 'aria-hidden': 'true' }, iconEl('chevrondown', 14, 2.4)),
    el('span', { class: 'cal-acct-head__name', text: accName }),
    summary ? el('span', { class: 'cal-acct-head__sum', text: summary }) : null);
  return { head, open };
}

// A list of online accounts (from listAccounts) as collapsible accordions: an "+ Add account" button
// sits on TOP, and each account's calendars (from listCalendars) show only when that account is
// expanded. Selecting a calendar invokes onPick with the chosen endpoint. When `allowCreate` is true
// (the DESTINATION step) each writable account also gets a "+ New calendar" affordance.
function onlineCalendarPicker(selectedCalendarId, onPick, allowCreate) {
  const a = addPairLive;
  const wrap = el('div', { class: 'glass glass--card', style: 'padding:4px' });

  const accounts = live.accounts || [];

  // "+ Add account" on TOP (above the accounts), not buried after the calendars. A destination must
  // be writable, so connect read/write; clear any chosen calendar so Continue can't point at the old
  // account's calendar, and auto-expand the freshly added account.
  if (allowCreate) wrap.append(addAccountRow('readwrite', (newRef) => {
    a.dstAccountRef = newRef; a.dstCalendarId = null; a.dstCalendarName = null;
    if (newRef) { (a.expandedDst = a.expandedDst || new Set()).add(newRef); }
  }));

  if (accounts.length === 0) {
    wrap.append(el('div', { class: 'cal-item__sub', style: 'padding:12px', text: 'No connected accounts yet.' }));
    return wrap;
  }

  // Accordion expand state (survives rerenders). Initialize once: open the selected account, or the
  // only account when there is just one; otherwise everything starts collapsed.
  if (!a.expandedDst) {
    a.expandedDst = new Set();
    if (a.dstAccountRef) a.expandedDst.add(a.dstAccountRef);
    else if (accounts.length === 1) a.expandedDst.add(accounts[0].accountRef);
  }

  const list = el('div', { class: 'cal-list', role: 'list' });

  accounts.forEach((acc) => {
    const accName = acc.displayName || acc.accountRef;
    const cals = live.calendars[acc.accountRef];
    const active = a.dstAccountRef === acc.accountRef;
    const summary = cals ? `${cals.length} calendar${cals.length === 1 ? '' : 's'}` : '';

    const { head, open } = accountAccordionHead(acc, a.expandedDst, summary, active);
    const group = el('div', { class: `cal-acct-group${open ? ' is-open' : ''}`, role: 'group', 'aria-label': accName });
    group.append(head);
    list.append(group);
    if (!open) return;

    const body = el('div', { class: 'cal-acct-body' });
    group.append(body);

    if (!cals) {
      body.append(el('div', { class: 'cal-item__sub', style: 'padding:6px 12px', text: 'Loading calendars…' }));
      loadCalendars(acc.accountRef).then(() => { if (state.view === 'add-pair') rerender(); });
      return;
    }
    if (cals.length === 0) {
      body.append(el('div', { class: 'cal-item__sub', style: 'padding:6px 12px', text: 'No calendars on this account.' }));
    }
    cals.forEach((c) => {
      const selected = selectedCalendarId === c.id;
      body.append(el('button', { class: `cal-item${selected ? ' is-selected' : ''}`,
        onclick: () => onPick(acc, c) },
        pairBadge('Outlook'),
        el('div', null,
          el('div', { class: 'cal-item__name', text: c.displayName || c.id }),
          el('div', { class: 'cal-item__sub', text: acc.displayName || acc.accountRef })),
        el('div', { class: 'cal-item__check', html: selected ? icon('check', { size: 12, stroke: 2.6 }) : '' })));
    });

    // "+ New calendar" — destination only, and only on a WRITABLE account. Creating a calendar is a
    // Graph write, so a read-only destination account must be upgraded first (handled in the select
    // path); offering create here would write before the upgrade and fail.
    if (allowCreate && acc.scope !== 'read') body.append(newCalendarRow(acc, onPick));
  });

  wrap.append(list);
  return wrap;
}

// newCalendarRow(acc, onPick) — an inline "+ New calendar" control for one account. Clicking it
// swaps in a name input + Create/Cancel; Create calls createCalendarFor, which on success refreshes
// the account's calendars, selects the new one (onPick), and re-renders the wizard. Failures show
// inline feedback without disturbing the rest of the picker. Pattern mirrors connectAccount.
function newCalendarRow(acc, onPick) {
  const row = el('div', { class: 'cal-new' });

  const showForm = () => {
    row.replaceChildren();
    const input = el('input', { class: 'field-input cal-new__input', type: 'text', placeholder: 'New calendar name', 'aria-label': 'New calendar name', maxlength: '120' });
    const feedback = el('div', { class: 'cfg-row__hint cal-new__feedback' });
    const createBtn = el('button', { class: 'btn btn--primary cal-new__create', type: 'button' }, iconEl('plus', 12, 2), el('span', { text: 'Create' }));
    const cancelBtn = el('button', { class: 'btn btn--ghost cal-new__cancel', type: 'button', text: 'Cancel', onclick: showButton });
    const submit = () => {
      const name = (input.value || '').trim();
      if (!name) { feedback.textContent = 'Enter a name.'; feedback.style.color = 'var(--err)'; input.focus(); return; }
      createBtn.disabled = true; cancelBtn.disabled = true; input.disabled = true;
      const span = createBtn.querySelector('span'); if (span) span.textContent = 'Creating…';
      feedback.textContent = ''; feedback.style.color = '';
      createCalendarFor(acc.accountRef, name, onPick, (err) => {
        // Failure path: restore the form with an inline error.
        createBtn.disabled = false; cancelBtn.disabled = false; input.disabled = false;
        if (span) span.textContent = 'Create';
        feedback.textContent = err || 'Could not create the calendar.'; feedback.style.color = 'var(--err)';
        input.focus();
      });
    };
    createBtn.addEventListener('click', submit);
    input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); submit(); } });
    row.append(el('div', { class: 'cal-new__form' }, input, createBtn, cancelBtn), feedback);
    input.focus();
  };

  const showButton = () => {
    row.replaceChildren();
    row.append(el('button', { class: 'cal-new__trigger', type: 'button', onclick: showForm },
      iconEl('plus', 13, 2.2), el('span', { text: 'New calendar' })));
  };

  showButton();
  return row;
}

// addAccountRow(scope, onAdded) — inline "+ Add account" control for the wizard, mirroring
// newCalendarRow. Clicking it runs connectAccount (OAuth in the system browser) with a Cancel; on
// success onAdded(newRef) fires so the step can select the new account (newRef may be null if the
// account was already connected — Phase B idempotency — in which case we just re-render and the user
// taps it in the list). `scope` is 'read' for the source step, 'readwrite' for the destination step.
function addAccountRow(scope, onAdded) {
  // role=listitem so it traverses cleanly when appended inside the wizard's role=list calendar list.
  // The trigger keeps `connect-cal__label` (connectAccount swaps that span to "Connecting…") but NOT
  // `connect-cal` itself, so it shares newCalendarRow's left-aligned pill look instead of stretching
  // full-width — the two inline "+ New calendar" / "+ Add account" rows then render consistently.
  const row = el('div', { class: 'cal-new', role: 'listitem' });
  const trigger = el('button', { class: 'cal-new__trigger', type: 'button' },
    iconEl('plus', 13, 2.2), el('span', { class: 'connect-cal__label', text: 'Add account' }));
  trigger.addEventListener('click', () => {
    connectAccount({ scope, btn: trigger, wrap: row, onConnected: (newRef) => {
      if (newRef && typeof onAdded === 'function') onAdded(newRef);
      if (state.view === 'add-pair') rerender();
    } });
  });
  row.append(trigger);
  return row;
}

// createCalendarFor(accountRef, name, onPick, onError) — create a calendar on the account through
// the createCalendar bridge action, then invalidate + reload that account's calendar list, select
// the freshly-created calendar (onPick) and re-render the wizard. onError gets a message on failure.
function createCalendarFor(accountRef, name, onPick, onError) {
  if (!Bridge.available || !accountRef) { if (onError) onError('No bridge available.'); return; }
  Bridge.call('createCalendar', JSON.stringify({ accountRef, name }))
    .then((created) => {
      // Drop the cached calendars so the new one is included on reload, then select it.
      delete live.calendars[accountRef];
      return loadCalendars(accountRef).then((cals) => {
        const newId = created && created.id;
        const picked = (cals || []).find((c) => c.id === newId)
          || (created && created.id ? { id: created.id, displayName: created.displayName || name } : null);
        const acc = (live.accounts || []).find((a) => a.accountRef === accountRef) || { accountRef };
        if (picked && onPick) onPick(acc, picked);
        if (state.view === 'add-pair') rerender();
      });
    })
    .catch((e) => { if (onError) onError((e && e.message) || 'Could not create the calendar.'); });
}

// Feature 2 — the "All calendars" header row shared by both source multi-selects. A toggle that,
// when ON, means "read every calendar of this origin"; when OFF, the per-calendar checkboxes below
// drive the explicit subset.
function allCalendarsRow(get, set) {
  // The toggle gets the REAL setter (plus a rerender) so BOTH mouse click and keyboard
  // (Space/Enter on the role=switch) flip the state and repaint. A no-op setter here would
  // make keyboard activation move aria-checked without changing the actual selection.
  const toggle = toggleLocal(get, (v) => { set(v); rerender(); }, 'All calendars');
  // Plain div (not <label>) and no row-level click handler: a label+click wrapper around the
  // switch double-fires on mouse (label click + toggle click) and bypasses the switch on keyboard.
  // Clicking the text label area instead forwards to the toggle so the whole row stays clickable.
  const row = el('div', { class: 'cal-multi__all' },
    el('div', { class: 'cal-multi__all-text' },
      el('div', { class: 'cal-item__name', text: 'All calendars' }),
      el('div', { class: 'cal-item__sub', text: 'Mirror every calendar from this source into the destination' })),
    toggle);
  row.querySelector('.cal-multi__all-text').addEventListener('click', () => { set(!get()); rerender(); });
  return row;
}

// A single multi-select calendar checkbox row (used for both COM names and Graph ids).
function calCheckRow(label, sub, checked, onToggle) {
  return el('button', { class: `cal-item${checked ? ' is-selected' : ''}`, type: 'button', onclick: () => { onToggle(); rerender(); } },
    pairBadge('Outlook'),
    el('div', null,
      el('div', { class: 'cal-item__name', text: label }),
      sub ? el('div', { class: 'cal-item__sub', text: sub }) : null),
    el('div', { class: 'cal-item__check', html: checked ? icon('check', { size: 12, stroke: 2.6 }) : '' }));
}

// COM source multi-select: "All calendars" + a checkbox per local Outlook calendar (display names).
// Lazy-loads the device's local calendars via the listLocalCalendars bridge action.
function comSourceMultiSelect() {
  const a = addPairLive;
  const wrap = el('div', { class: 'glass glass--card cal-multi', style: 'padding:4px' });
  wrap.append(allCalendarsRow(() => a.srcAllCalendars, (v) => { a.srcAllCalendars = v; }));

  if (live.localCalendars === null) {
    if (!live.localCalendarsLoading)
      loadLocalCalendars().then(() => { if (state.view === 'add-pair') rerender(); });
    wrap.append(el('div', { class: 'cal-item__sub', style: 'padding:8px 12px', text: 'Loading calendars…' }));
    return wrap;
  }

  if (live.localCalendarsError) {
    wrap.append(el('div', { class: 'cal-item__sub', style: 'padding:8px 12px', text: 'Could not list local calendars. Outlook may not be available; "All calendars" will be used.' }));
    return wrap;
  }

  if (a.srcAllCalendars) return wrap; // subset list is hidden while "All" is on

  if (live.localCalendars.length === 0) {
    wrap.append(el('div', { class: 'cal-item__sub', style: 'padding:8px 12px', text: 'No local calendars found.' }));
    return wrap;
  }

  const list = el('div', { class: 'cal-list' });
  live.localCalendars.forEach((name) => {
    const checked = a.srcCalendarNames.includes(name);
    list.append(calCheckRow(name, 'Local Outlook', checked, () => {
      if (checked) a.srcCalendarNames = a.srcCalendarNames.filter((n) => n !== name);
      else a.srcCalendarNames = a.srcCalendarNames.concat([name]);
    }));
  });
  wrap.append(list);
  return wrap;
}

// Online source multi-select: an "+ Add account" button on TOP, then "All calendars", then each
// connected account as a COLLAPSIBLE accordion — its calendars show only when the account is
// expanded. Selecting calendars records their Graph ids in srcCalendarIds and pins srcAccountRef to
// the account of the first selected calendar (a source pair targets ONE account's calendars).
function onlineSourceMultiSelect() {
  const a = addPairLive;
  const wrap = el('div', { class: 'glass glass--card cal-multi', style: 'padding:4px' });

  const accounts = live.accounts || [];

  // "+ Add account" on TOP (above the accounts/options), not buried after the calendars. A source
  // only needs to be read, so connect read-only; auto-expand the freshly added account.
  wrap.append(addAccountRow('read', (newRef) => {
    a.srcAccountRef = newRef; a.srcSwitchPrompt = null;
    if (newRef) { (a.expandedSrc = a.expandedSrc || new Set()).add(newRef); }
  }));

  if (accounts.length === 0) {
    wrap.append(el('div', { class: 'cal-item__sub', style: 'padding:12px', text: 'No connected accounts yet.' }));
    return wrap;
  }

  // Accordion expand state (survives rerenders). Initialize once: open the selected account, or the
  // only account when there is just one; otherwise everything starts collapsed.
  if (!a.expandedSrc) {
    a.expandedSrc = new Set();
    if (a.srcAccountRef) a.expandedSrc.add(a.srcAccountRef);
    else if (accounts.length === 1) a.expandedSrc.add(accounts[0].accountRef);
  }

  // Flipping All on/off makes a pending subset account-switch confirm meaningless — clear it.
  wrap.append(allCalendarsRow(() => a.srcAllCalendars, (v) => { a.srcAllCalendars = v; a.srcSwitchPrompt = null; }));

  const list = el('div', { class: 'cal-list', role: 'list' });

  // applySwitch — commit a confirmed account switch in subset mode: drop the old account's
  // selection, pin the new account, and select the tapped calendar as the first member.
  const applySwitch = (acc, c, cals) => {
    a.srcAccountRef = acc.accountRef;
    a.srcCalendarIds = [c.id];
    a.srcCalendarId = c.id;
    a.srcCalendarName = c.displayName || c.id;
    a.srcSwitchPrompt = null;
  };

  accounts.forEach((acc) => {
    const accName = acc.displayName || acc.accountRef;
    const cals = live.calendars[acc.accountRef];
    const accountChosen = a.srcAccountRef === acc.accountRef;

    // Collapsed summary: the selection state when chosen, else the calendar count.
    let summary;
    if (accountChosen && a.srcAllCalendars) summary = 'Source · all';
    else if (accountChosen && a.srcCalendarIds.length) summary = `${a.srcCalendarIds.length} selected`;
    else if (cals) summary = `${cals.length} calendar${cals.length === 1 ? '' : 's'}`;
    else summary = '';

    const { head, open } = accountAccordionHead(acc, a.expandedSrc, summary, accountChosen);
    const group = el('div', { class: `cal-acct-group${open ? ' is-open' : ''}`, role: 'group', 'aria-label': accName });
    group.append(head);
    list.append(group);
    if (!open) return;

    const body = el('div', { class: 'cal-acct-body' });
    group.append(body);

    if (!cals) {
      body.append(el('div', { class: 'cal-item__sub', style: 'padding:6px 12px', text: 'Loading calendars…' }));
      loadCalendars(acc.accountRef).then(() => { if (state.view === 'add-pair') rerender(); });
      return;
    }
    if (cals.length === 0) {
      body.append(el('div', { class: 'cal-item__sub', style: 'padding:6px 12px', text: 'No calendars on this account.' }));
      return;
    }

    // While "All" is on, individual calendar checkboxes do NOT apply — "All" already covers every
    // calendar of the chosen account. Instead of faking each calendar as checked (confusing: a tap
    // re-pins the same account with no real change), pick the ACCOUNT explicitly and render its
    // calendars as a read-only, informative list so it is clear what "All" will include.
    if (a.srcAllCalendars) {
      body.append(calCheckRow(
        accName,
        accountChosen ? 'Source account — All calendars below will be mirrored' : 'Use this account as the source',
        accountChosen,
        () => {
          a.srcAccountRef = acc.accountRef;
          // Anchor the legacy fields on the account's first calendar (server requires a calendarId).
          const first = cals[0];
          a.srcCalendarId = first ? first.id : null;
          a.srcCalendarName = first ? (first.displayName || first.id) : null;
        }));
      // Read-only "included by All" list, as a semantic list of the account's calendars.
      const roList = el('div', { class: 'cal-readonly-list', role: 'list', 'aria-label': `Calendars on ${accName}` });
      cals.forEach((c) => {
        roList.append(el('div', { class: `cal-item is-readonly${accountChosen ? '' : ' is-muted'}`, role: 'listitem', 'aria-disabled': 'true' },
          pairBadge('Outlook'),
          el('div', null,
            el('div', { class: 'cal-item__name', text: c.displayName || c.id }),
            el('div', { class: 'cal-item__sub', text: accountChosen ? 'Included by “All calendars”' : 'Pick this account to include' }))));
      });
      body.append(roList);
      return;
    }

    // Subset mode: each calendar is an independently selectable checkbox.
    cals.forEach((c) => {
      const checked = accountChosen && a.srcCalendarIds.includes(c.id);
      const itemRow = el('div', { role: 'listitem' });
      itemRow.append(calCheckRow(c.displayName || c.id, accName, checked, () => {
        const switchingAccount = a.srcAccountRef && a.srcAccountRef !== acc.accountRef;
        // Switching to a DIFFERENT account would wipe the existing subset. Don't do it silently:
        // stash the intent and surface an inline confirm (rendered just below this row). A tap on
        // the SAME account, or when nothing is selected yet, applies immediately as before.
        if (switchingAccount && a.srcCalendarIds.length > 0) {
          a.srcSwitchPrompt = { accountRef: acc.accountRef, calendarId: c.id };
          return;
        }
        if (a.srcAccountRef !== acc.accountRef) { a.srcAccountRef = acc.accountRef; a.srcCalendarIds = []; }
        a.srcSwitchPrompt = null;
        if (a.srcCalendarIds.includes(c.id)) a.srcCalendarIds = a.srcCalendarIds.filter((x) => x !== c.id);
        else a.srcCalendarIds = a.srcCalendarIds.concat([c.id]);
        // Keep the legacy anchor pointing at the first selected calendar (server requires calendarId).
        const first = a.srcCalendarIds[0];
        const fc = first ? cals.find((x) => x.id === first) : null;
        a.srcCalendarId = first || null;
        a.srcCalendarName = fc ? (fc.displayName || fc.id) : null;
      }));
      body.append(itemRow);

      // Inline confirm for the pending account switch, anchored under the tapped calendar.
      if (a.srcSwitchPrompt && a.srcSwitchPrompt.accountRef === acc.accountRef && a.srcSwitchPrompt.calendarId === c.id) {
        const prevName = (accounts.find((x) => x.accountRef === a.srcAccountRef) || {});
        const prevLabel = prevName.displayName || prevName.accountRef || 'the other account';
        const confirm = el('div', { class: 'glass glass--card cal-switch-confirm', role: 'alert' },
          el('div', { class: 'cal-switch-confirm__text',
            text: `A source pair uses one account. Switching to “${accName}” clears the ${a.srcCalendarIds.length} calendar${a.srcCalendarIds.length === 1 ? '' : 's'} selected on “${prevLabel}”.` }),
          el('div', { class: 'cal-switch-confirm__actions' },
            el('button', { class: 'btn btn--ghost', type: 'button', text: 'Keep current', onclick: () => { a.srcSwitchPrompt = null; rerender(); } }),
            el('button', { class: 'btn btn--primary', type: 'button', text: 'Switch account', onclick: () => { applySwitch(acc, c, cals); rerender(); } })));
        body.append(confirm);
      }
    });
  });
  wrap.append(list);
  return wrap;
}

// Human label for the source selection on the configure/preview step.
function sourceSelectionLabel() {
  const a = addPairLive;
  if (a.srcAllCalendars) return 'All calendars';
  if (a.sourceKind === 'com') {
    const n = a.srcCalendarNames.length;
    return n === 0 ? '(no calendars selected)' : (n === 1 ? a.srcCalendarNames[0] : `${n} calendars`);
  }
  const n = a.srcCalendarIds.length;
  return n === 0 ? '(no calendars selected)' : (n === 1 ? (a.srcCalendarName || '1 calendar') : `${n} calendars`);
}

// True when the source selection is complete enough to continue.
function sourceSelectionReady() {
  const a = addPairLive;
  if (!a.sourceKind) return false;
  if (a.srcAllCalendars) {
    // COM all-calendars needs nothing else; online all-calendars needs an account pinned.
    return a.sourceKind === 'com' || !!a.srcAccountRef;
  }
  return a.sourceKind === 'com'
    ? a.srcCalendarNames.length > 0
    : (!!a.srcAccountRef && a.srcCalendarIds.length > 0);
}

function renderAddPairLive(root) {
  const labels = ['Source', 'Destination', 'Configure'];
  const a = addPairLive;
  const editing = !!a.editId;

  root.append(viewHeader(editing ? 'Edit sync pair' : 'Add a sync pair', { onBack: () => { if (a.step === 0) { resetAddPairLive(); navigate('calendar'); } else { a.step--; rerender(); } } }));
  root.append(wizardStepper(a.step, labels));

  if (live.accounts === null) {
    loadAccounts().then(() => { if (state.view === 'add-pair') rerender(); });
  }

  if (a.step === 0) {
    root.append(el('div', { class: 'wizard-title', text: 'Pick the source calendar' }));
    root.append(el('div', { class: 'wizard-sub', text: 'Changes here are mirrored to the destination. The source is never modified.' }));

    // Two source kinds: local Outlook (COM) or an online account calendar. The COM tile is ALWAYS
    // rendered, but disabled (no-op onclick) when Outlook is not available on this device.
    const kinds = el('div', { class: 'provider-grid' });
    const comOff = !comAvailable();
    kinds.append(el('button', { class: `provider-tile glass${a.sourceKind === 'com' ? ' is-selected' : ''}${comOff ? ' is-disabled' : ''}`,
      disabled: comOff,
      title: comOff ? 'Not available on this device' : undefined,
      onclick: () => { if (comOff) return; a.sourceKind = 'com'; a.srcCalendarId = 'local'; a.srcCalendarName = 'Outlook (this PC)'; a.srcAccountRef = null; rerender(); } },
      el('div', { class: 'provider-tile__logo', dataset: { tone: 'azure' }, text: 'PC' }),
      el('div', { class: 'provider-tile__name', text: 'Outlook on this PC' }),
      el('div', { class: 'provider-tile__sub', text: comOff ? 'Not available on this device' : 'Local Outlook · read via COM' })));
    kinds.append(el('button', { class: `provider-tile glass${a.sourceKind === 'online' ? ' is-selected' : ''}`,
      onclick: () => { a.sourceKind = 'online'; rerender(); } },
      el('div', { class: 'provider-tile__logo', dataset: { tone: 'ink' }, text: 'M' }),
      el('div', { class: 'provider-tile__name', text: 'Online account' }),
      el('div', { class: 'provider-tile__sub', text: 'outlook.com · via the server' })));
    root.append(kinds);

    // Feature 2 — once a source kind is chosen, show the multi-calendar selection for THAT origin:
    // "All calendars" (default) or an explicit subset. The destination (step 1) stays single.
    if (a.sourceKind === 'com') {
      root.append(comSourceMultiSelect());
      // Track B — tell the user up front that a COM source is read on THIS machine, mirroring the
      // dashboard pin-note. The pair will be pinned to this device (completeAddPairLive sends our
      // deviceId), so it can only sync while this computer's app is running.
      root.append(el('div', { class: 'pair__pin-note', dataset: { kind: 'local' } },
        el('span', { style: 'display:inline-flex', html: icon('check', { size: 12, stroke: 1.8 }) }),
        el('span', { text: 'This sync will run on this computer (where Outlook is installed).' })));
    } else if (a.sourceKind === 'online') {
      root.append(onlineSourceMultiSelect());
    }

    const srcReady = sourceSelectionReady();
    root.append(el('div', { class: 'wizard-foot' },
      el('button', { class: 'btn btn--ghost', text: 'Cancel', onclick: () => { resetAddPairLive(); navigate('calendar'); } }),
      el('button', { class: 'btn btn--primary', disabled: !srcReady, onclick: () => { a.step = 1; rerender(); } },
        el('span', { text: 'Continue' }), iconEl('arrowright', 14, 1.8))));
  } else if (a.step === 1) {
    root.append(el('div', { class: 'wizard-title', text: 'Pick the destination' }));
    root.append(el('div', { class: 'wizard-sub', text: 'Events are written here. Past events on the destination are never touched.' }));
    root.append(onlineCalendarPicker(a.dstCalendarId, (acc, c) => {
      // A destination must be writable. If this account is read-only, grant read/write first
      // (interactive consent) before committing it as the destination; on cancel/fail, do nothing.
      if (acc.scope === 'read') {
        announce('Granting write access to this account…');
        Bridge.call('upgradeAccountScope', JSON.stringify(acc.accountRef), 210000)
          .then((r) => {
            if (r && r.connected) {
              live.accounts = null;
              return loadAccounts().then(() => {
                a.dstAccountRef = acc.accountRef; a.dstCalendarId = c.id; a.dstCalendarName = c.displayName || c.id;
                rerender();
              });
            }
            announce((r && r.cancelled) ? 'Upgrade cancelled.' : 'Could not grant write access.');
          })
          .catch(() => announce('Could not grant write access.'));
        return;
      }
      a.dstAccountRef = acc.accountRef; a.dstCalendarId = c.id; a.dstCalendarName = c.displayName || c.id; rerender();
    }, true));
    root.append(el('div', { class: 'wizard-foot' },
      el('button', { class: 'btn btn--ghost', text: 'Back', onclick: () => { a.step = 0; rerender(); } }),
      el('button', { class: 'btn btn--primary', disabled: !a.dstCalendarId, onclick: () => { a.step = 2; rerender(); } },
        el('span', { text: 'Continue' }), iconEl('arrowright', 14, 1.8))));
  } else if (a.step === 2) {
    if (!a.name) a.name = `${sourceSelectionLabel()} → ${a.dstCalendarName}`;
    root.append(el('div', { class: 'wizard-title', text: editing ? 'Edit the sync' : 'Configure the sync' }));
    root.append(el('div', { class: 'wizard-sub', text: 'Review the pair and tune how often it runs.' }));

    const col = (label, name, sub) => el('div', { class: 'pair-preview__col' },
      el('div', { class: 'route__label', text: label }), el('div', { class: 'pair-preview__name', text: name }), el('div', { class: 'pair-preview__email', text: sub }));
    root.append(el('div', { class: 'glass glass--card pair-preview' },
      pairBadge('Outlook'), col('SOURCE', sourceSelectionLabel(), a.sourceKind === 'com' ? 'Local Outlook' : (a.srcAccountRef || '')),
      el('span', { style: 'color:var(--ink-3);display:inline-flex', html: icon('arrowright', { size: 14, stroke: 1.8 }) }),
      pairBadge('Outlook'), col('DESTINATION', a.dstCalendarName, a.dstAccountRef)));

    // F2 warning: when editing and the source OR destination changed, the next run starts fresh
    // (the new destination has no events tagged with this source).
    if (editing && editEndpointsChanged()) {
      const destChanged = editDestChanged();
      if (destChanged) {
        // The previous destination keeps the events this pair copied there. Offer an opt-in,
        // destructive cleanup of ONLY those events (the bridge enforces "this pair only" and refuses
        // the current destination). Default OFF; the live count is shown once the bridge reports it.
        root.append(buildCleanupPrevDestCard());
      } else {
        root.append(el('div', { class: 'glass glass--card wizard-warn' },
          el('div', { class: 'wizard-warn__text', text: 'Changing the source restarts the sync. The destination will be reconciled against the new source on the next run.' })));
      }
    }

    const cfg = el('div', { class: 'glass glass--card config-section', style: 'margin-top:10px' });
    const nameInput = el('input', { class: 'field-input', value: a.name });
    nameInput.addEventListener('input', () => { a.name = nameInput.value; });
    cfg.append(el('div', { class: 'cfg-row' },
      el('div', null, el('div', { class: 'cfg-row__label', text: 'Pair name' }), el('div', { class: 'cfg-row__hint', text: 'Shown on your dashboard' })), nameInput));
    cfg.append(intervalRow(() => a.intervalMin, (v) => { a.intervalMin = v; }));
    root.append(cfg);

    // Secondary action while editing: export this month of the pair's source to a .txt, reusing
    // the same modal the per-pair Export button opens (routes by source provider inside).
    // The export reads the SAVED source from the server (the value before this edit's PATCH), so
    // while there are unsaved endpoint changes it would export the OLD source — confusing and wrong.
    // Disable it in that case with a clear "save first" hint; for a COM source it also needs Outlook.
    // Desktop-only: the export ends in a local save dialog (no save-to-disk channel in the web
    // panel), so the affordance is hidden unless Bridge.desktopApp — same gate as the per-pair button.
    if (editing && Bridge.desktopApp) {
      const srcCom = a.sourceKind === 'com';
      const unsavedEndpoints = editEndpointsChanged();
      const exportBlocked = unsavedEndpoints || (srcCom && !comAvailable());
      const blockTitle = unsavedEndpoints
        ? 'Save changes before exporting'
        : (srcCom && !comAvailable() ? 'Outlook is not available on this device' : 'Export a month of the source calendar to a .txt file');
      root.append(el('div', { style: 'margin-top:10px' },
        el('button', { class: 'btn btn--ghost', style: 'width:100%',
          disabled: exportBlocked,
          title: blockTitle,
          onclick: () => { if (!exportBlocked) openExportTxtModal(editPairForModal()); } },
          iconEl('folder', 13, 1.6),
          el('span', { text: unsavedEndpoints ? 'Save changes before exporting' : 'Export this month to .txt' }))));
    }

    // Inline error from a failed create/updatePair: a red message right above the actions so the
    // user sees WHY the save failed without leaving the step (their entries are untouched).
    if (a.submitError) {
      root.append(el('div', { class: 'glass glass--card wizard-warn wizard-warn--err', role: 'alert' },
        el('div', { class: 'wizard-warn__text', text: a.submitError })));
    }

    const submitBtn = el('button', { class: 'btn btn--primary', disabled: !!a.submitting, onclick: () => completeAddPairLive() },
      iconEl('check', 14, 2.2),
      el('span', { text: a.submitting ? 'Saving…' : (a.submitError ? 'Retry' : (editing ? 'Save changes' : 'Create pair')) }));
    root.append(el('div', { class: 'wizard-foot' },
      el('button', { class: 'btn btn--ghost', text: 'Back', disabled: !!a.submitting, onclick: () => { a.step = 1; rerender(); } }),
      submitBtn));
  }
}

// True when the wizard is editing and the source or destination differs from the loaded original.
function editEndpointsChanged() {
  const a = addPairLive;
  if (!a.editId) return false;
  // Compare a sorted copy so reordering selections is not treated as a change.
  const sameSet = (x, y) => {
    const ax = (x || []).slice().sort();
    const ay = (y || []).slice().sort();
    return ax.length === ay.length && ax.every((v, i) => v === ay[i]);
  };
  const selectionChanged = (!!a.srcAllCalendars !== !!a.origSrcAllCalendars)
    || !sameSet(a.srcCalendarNames, a.origSrcCalendarNames)
    || !sameSet(a.srcCalendarIds, a.origSrcCalendarIds);
  const srcChanged = a.sourceKind !== a.origSourceKind
    || a.srcCalendarId !== a.origSrcCalendarId
    || ((a.srcAccountRef || null) !== (a.origSrcAccountRef || null))
    || selectionChanged;
  const dstChanged = a.dstCalendarId !== a.origDstCalendarId
    || ((a.dstAccountRef || null) !== (a.origDstAccountRef || null));
  return srcChanged || dstChanged;
}

// True when editing and the DESTINATION specifically changed (account or calendar). The
// source-only case is handled separately — only a destination change can orphan events in a
// previous calendar.
function editDestChanged() {
  const a = addPairLive;
  if (!a.editId) return false;
  return a.dstCalendarId !== a.origDstCalendarId
    || ((a.dstAccountRef || null) !== (a.origDstAccountRef || null));
}

// The PREVIOUS destination endpoint (the value before this edit), as the bridge expects it. Always
// MicrosoftGraph — destinations are online accounts. Returns null when there is no original (creating).
function oldDestinationEndpoint() {
  const a = addPairLive;
  if (!a.editId || !a.origDstCalendarId) return null;
  return {
    provider: 'MicrosoftGraph',
    accountRef: a.origDstAccountRef || null,
    calendarId: a.origDstCalendarId,
    calendarName: a.origDstCalendarName || a.origDstCalendarId,
  };
}

// A stable signature of the old destination, used to invalidate the cached cleanup count when the
// edit context changes (e.g. a different pair is opened).
function oldDestinationKey() {
  const a = addPairLive;
  return `${a.editId || ''}|${a.origDstAccountRef || ''}|${a.origDstCalendarId || ''}`;
}

// buildCleanupPrevDestCard() — the destination-changed card: a plain notice that the old
// destination keeps its copied events, plus an OPT-IN (default OFF) toggle to delete only the events
// THIS pair copied there. Shows the live count once the bridge reports it. The toggle drives
// addPairLive.cleanupPrevDest, which completeAddPairLive reads after a successful PATCH.
function buildCleanupPrevDestCard() {
  const a = addPairLive;
  const card = el('div', { class: 'glass glass--card wizard-warn' });
  card.append(el('div', { class: 'wizard-warn__text',
    text: 'Changing the destination starts a fresh sync into the new calendar. Events this pair already copied into the previous destination are left there unless you remove them below.' }));

  // The count is tied to the old destination; refetch when the context key changes.
  const key = oldDestinationKey();
  if (a.cleanupCountKey !== key) { a.cleanupCount = null; a.cleanupCountKey = key; }

  const countLabel = el('span', { class: 'cleanup-opt__count' });
  const renderCount = () => {
    if (a.cleanupCount === null) { countLabel.textContent = 'Counting…'; countLabel.dataset.state = 'loading'; }
    else if (a.cleanupCount === 'err') { countLabel.textContent = 'count unavailable'; countLabel.dataset.state = 'err'; }
    else { countLabel.textContent = `${a.cleanupCount} event${a.cleanupCount === 1 ? '' : 's'}`; countLabel.dataset.state = 'ok'; }
  };
  renderCount();

  // Fetch the count once per context (only when a bridge is available and there is an old dest).
  if (a.cleanupCount === null && Bridge.available) {
    const dest = oldDestinationEndpoint();
    if (dest) {
      Bridge.call('countManagedInDestination', JSON.stringify({ pairId: a.editId, destination: dest }))
        .then((r) => { a.cleanupCount = (r && typeof r.count === 'number') ? r.count : 0; })
        .catch(() => { a.cleanupCount = 'err'; })
        .then(() => { if (state.view === 'add-pair') renderCount(); });
    } else {
      a.cleanupCount = 0; renderCount();
    }
  }

  const toggle = toggleLocal(() => a.cleanupPrevDest, (v) => { a.cleanupPrevDest = v; }, 'Remove the events already copied to the previous destination');
  const optRow = el('label', { class: 'cleanup-opt' },
    el('div', { class: 'cleanup-opt__text' },
      el('div', { class: 'cleanup-opt__title', text: 'Also remove the events already copied to the previous destination' }),
      el('div', { class: 'cleanup-opt__hint' },
        el('span', { text: 'Permanently deletes only the events this pair created there — ' }), countLabel, el('span', { text: '. Events you created yourself are never touched.' }))),
    toggle);
  card.append(optRow);
  return card;
}

// Builds a minimal pair-like object the export modal understands (it reads pair.id and
// pair.src.provider), from the in-edit wizard state.
function editPairForModal() {
  const a = addPairLive;
  return {
    id: a.editId,
    src: { provider: a.sourceKind === 'com' ? 'OutlookCom' : 'MicrosoftGraph' },
  };
}

function completeAddPairLive() {
  const a = addPairLive;
  // Feature 2 — carry the source multi-calendar selection. allCalendars=true => read every calendar
  // of the origin; otherwise the typed subset (calendarNames for COM, calendarIds for Graph). The
  // single calendarId stays as the server-required anchor (a non-empty calendarId). The destination
  // is always a single calendar and never carries the selection fields.
  const source = a.sourceKind === 'com'
    ? {
        provider: 'OutlookCom', calendarId: 'local', calendarName: 'Outlook (this PC)',
        allCalendars: !!a.srcAllCalendars,
        calendarNames: a.srcAllCalendars ? [] : a.srcCalendarNames.slice(),
      }
    : {
        provider: 'MicrosoftGraph', accountRef: a.srcAccountRef,
        calendarId: a.srcCalendarId, calendarName: a.srcCalendarName,
        allCalendars: !!a.srcAllCalendars,
        calendarIds: a.srcAllCalendars ? [] : a.srcCalendarIds.slice(),
      };
  const destination = { provider: 'MicrosoftGraph', accountRef: a.dstAccountRef, calendarId: a.dstCalendarId, calendarName: a.dstCalendarName };

  // Decide cleanup BEFORE the save mutates state: only when editing, the destination changed, the
  // user opted in, and there is a real previous destination. Capture the old endpoint + label now.
  const wantsCleanup = !!a.editId && editDestChanged() && a.cleanupPrevDest;
  const oldDest = wantsCleanup ? oldDestinationEndpoint() : null;
  const oldDestName = a.origDstCalendarName || a.origDstCalendarId || 'the previous calendar';
  const pairId = a.editId;
  const knownCount = (typeof a.cleanupCount === 'number') ? a.cleanupCount : null;

  // Edit (F2) updates in place (preserving the id); create makes a new pair. Both send the same
  // {name, source, destination, intervalMin}; updatePair additionally carries the id.
  // Enter the submitting state and clear any prior error before the call (drives the button label +
  // disables Back/Submit so the user can't double-submit or navigate mid-flight).
  a.submitting = true;
  a.submitError = null;
  if (state.view === 'add-pair') rerender();

  // Track B — pin a COM source to THIS device up front, using the deviceId cached by ensureLocalDevice.
  // The server pins the pair at creation so the dashboard shows "Source is on this PC" immediately
  // instead of waiting for the first push to claim it. Only for create (edit keeps its existing pin)
  // and only when the source is COM and we actually know our deviceId; otherwise omit it (the server
  // ignores a pin on a non-COM pair, and claim-on-first-push remains the safety net).
  const createPayload = { name: a.name, source, destination, intervalMin: a.intervalMin };
  if (a.sourceKind === 'com' && live.device && live.device.deviceId)
    createPayload.pinnedDeviceId = live.device.deviceId;

  const call = a.editId
    ? Bridge.call('updatePair', JSON.stringify({ id: a.editId, name: a.name, intervalMin: a.intervalMin, source, destination }))
    : Bridge.call('createPair', JSON.stringify(createPayload));

  const finish = () => loadPairs().then(() => { resetAddPairLive(); navigate('calendar'); });

  call
    .then(() => {
      // PATCH succeeded. Run the destructive cleanup ONLY now (never if the save failed), behind an
      // explicit confirm. cleanupOldDestination deletes only the events this pair created in the old
      // destination — the server refuses the pair's CURRENT destination, so this can't undo the save.
      if (wantsCleanup && oldDest) {
        return confirmCleanupOldDestination(pairId, oldDest, oldDestName, knownCount).then(finish);
      }
      return finish();
    })
    .catch((e) => {
      // Save failed: do NOT discard what the user entered or navigate away. Surface the reason
      // inline on the configure step and let them fix/retry — the button flips to "Retry".
      a.submitting = false;
      a.submitError = (e && e.message)
        ? `Could not ${a.editId ? 'save the pair' : 'create the pair'}: ${e.message}`
        : `Could not ${a.editId ? 'save the pair' : 'create the pair'}. Please try again.`;
      if (state.view === 'add-pair') rerender();
    });
}

// confirmCleanupOldDestination(pairId, oldDest, oldDestName, knownCount) — a secondary, explicit
// confirm for the destructive cleanup, then the cleanupOldDestination bridge call. Always resolves
// (never rejects): a declined confirm or a failed cleanup still lets the edit finish cleanly — the
// PATCH already succeeded, and the cleanup is idempotent (a later edit can retry). Returns a Promise.
function confirmCleanupOldDestination(pairId, oldDest, oldDestName, knownCount) {
  return new Promise((resolve) => {
    let settled = false;
    const done = () => { if (!settled) { settled = true; resolve(); } };

    const countText = (knownCount === null)
      ? `the events this pair copied to ${oldDestName}`
      : `${knownCount} event${knownCount === 1 ? '' : 's'} this pair copied to ${oldDestName}`;

    const feedback = el('div', { class: 'cfg-row__hint modal__feedback', style: 'min-height:16px' });
    const cancelBtn = el('button', { class: 'btn btn--ghost', type: 'button', text: 'Keep them' });
    const confirmBtn = el('button', { class: 'btn btn--danger', type: 'button' }, iconEl('alert', 13, 1.8), el('span', { text: 'Delete them' }));

    const body = el('div', { class: 'cleanup-confirm' },
      el('div', { class: 'cleanup-confirm__text',
        text: `This permanently deletes ${countText}. Events you created yourself in that calendar are never touched.` }),
      feedback,
      el('div', { class: 'modal__foot' }, cancelBtn, confirmBtn));

    const modal = openModal({ title: 'Remove old copies?', body, onClose: done });

    cancelBtn.addEventListener('click', () => modal.close());
    confirmBtn.addEventListener('click', () => {
      const span = confirmBtn.querySelector('span');
      confirmBtn.disabled = true; cancelBtn.disabled = true;
      if (span) span.textContent = 'Removing…';
      feedback.textContent = ''; feedback.style.color = '';
      Bridge.call('cleanupOldDestination', JSON.stringify({ pairId, destination: oldDest }), 120000)
        .then((r) => {
          const deleted = (r && typeof r.deleted === 'number') ? r.deleted : 0;
          const failures = (r && Array.isArray(r.failures)) ? r.failures.length : 0;
          announce(failures ? `Removed ${deleted}; ${failures} could not be removed.` : `Removed ${deleted} old ${deleted === 1 ? 'copy' : 'copies'}.`);
          if (span) span.textContent = 'Done';
          feedback.style.color = failures ? 'var(--warn)' : 'var(--ok)';
          feedback.textContent = failures ? `Removed ${deleted}; ${failures} could not be removed.` : `Removed ${deleted}.`;
          setTimeout(() => modal.close(), 800);
        })
        .catch((e) => {
          // The edit is already saved; surface the cleanup error but let the user dismiss and move on.
          confirmBtn.disabled = false; cancelBtn.disabled = false;
          if (span) span.textContent = 'Delete them';
          feedback.textContent = (e && e.message) || 'Could not remove the old copies.';
          feedback.style.color = 'var(--err)';
        });
    });
  });
}

// ---------------- Screen: Add Calendar wizard ----------------
const addCal = { step: 0, providerId: null, selected: new Set(['d1', 'd2']), timer: null };

function renderAddCalendar(root) {
  // Demo-only wizard. Every literal below (PROVIDERS / DISCOVERED, the fabricated
  // `zyncmaster.app/auth/.../8h3-4f2a` link, the 2.4s OAuth-simulating timer) is mock data, so it
  // must never paint in a real transport. In the App / web panel, connecting an account is the
  // server-driven flow (renderAddPairLive → onlineCalendarPicker, sourced from listAccounts /
  // listCalendars), so bounce there instead of showing this fabricated wizard.
  if (Bridge.available) {
    clearTimeout(addCal.timer);
    navigate('add-pair');
    return;
  }

  const labels = ['Provider', 'Authorize', 'Calendars'];
  const provider = PROVIDERS.find((p) => p.id === addCal.providerId);

  root.append(viewHeader('Connect a calendar account', { onBack: () => { if (addCal.step === 0) backFromAddCalendar(); else { addCal.step--; rerender(); } } }));
  root.append(wizardStepper(addCal.step, labels));

  if (addCal.step === 0) {
    root.append(el('div', { class: 'wizard-title', text: 'Choose a provider' }));
    root.append(el('div', { class: 'wizard-sub', text: 'Zync Master will use a read-only or read/write connection to this account, as you choose later.' }));
    const grid = el('div', { class: 'provider-grid' });
    PROVIDERS.forEach((p) => grid.append(el('button', { class: 'provider-tile glass', onclick: () => { addCal.providerId = p.id; addCal.step = 1; rerender(); } },
      el('div', { class: 'provider-tile__logo', dataset: { tone: p.tone }, text: p.id === 'icloud' ? 'i' : p.letter }),
      el('div', { class: 'provider-tile__name', text: p.name }),
      el('div', { class: 'provider-tile__sub', text: p.sub }))));
    root.append(grid);
    root.append(el('div', { class: 'wizard-foot' },
      el('button', { class: 'btn btn--ghost', text: 'Cancel', onclick: () => backFromAddCalendar() }),
      el('span', { style: 'font-size:11px;color:var(--ink-3)', text: 'Your credentials stay with the provider.' })));
  } else if (addCal.step === 1 && provider) {
    const orb = el('div', { class: 'provider-tile__logo', dataset: { tone: provider.tone }, style: 'width:56px;height:56px;margin:4px auto 0;border-radius:14px;position:relative' },
      el('span', { style: 'font-size:22px;font-weight:700', text: provider.id === 'icloud' ? 'i' : provider.letter }),
      el('span', { class: 'spinner', style: 'position:absolute;inset:-6px;width:68px;height:68px;border-width:1.5px;border-color:transparent;border-top-color:currentColor;border-radius:16px' }));
    root.append(el('div', { class: 'glass glass--card pair-card' },
      orb,
      el('div', { class: 'pair-title', text: 'Approve in your browser' }),
      el('div', { class: 'pair-sub' }, 'We opened a sign-in page for ', el('b', { style: 'color:var(--ink-1)', text: provider.name }), '. Approve there to grant Zync Master access to your calendars.'),
      el('button', { class: 'pair-link' }, iconEl('copy', 13, 1.6), el('span', { text: `zyncmaster.app/auth/${provider.id}/8h3-4f2a` })),
      el('button', { class: 'btn btn--ghost', text: 'Cancel', onclick: () => { addCal.step = 0; rerender(); } }),
    ));
    clearTimeout(addCal.timer);
    addCal.timer = setTimeout(() => { if (state.view === 'add-calendar' && addCal.step === 1) { addCal.step = 2; rerender(); } }, 2400);
  } else if (addCal.step === 2 && provider) {
    root.append(el('div', { class: 'wizard-title', text: 'Choose calendars to import' }));
    root.append(el('div', { class: 'wizard-sub' }, 'Pick which calendars from ', el('b', { style: 'color:var(--ink-1)', text: provider.name }), ' you want to make available inside Zync Master.'));
    const wrap = el('div', { class: 'glass glass--card', style: 'padding:4px' });
    DISCOVERED.forEach((d) => {
      const cb = el('input', { type: 'checkbox' });
      cb.checked = addCal.selected.has(d.id);
      cb.addEventListener('change', () => { if (cb.checked) addCal.selected.add(d.id); else addCal.selected.delete(d.id); rerender(); });
      wrap.append(el('label', { class: 'discover-item' }, cb,
        el('span', { class: 'discover-item__dot', style: `background:${d.color}` }),
        el('div', null, el('div', { class: 'discover-item__name', text: d.name }), el('div', { class: 'discover-item__sub', text: d.desc }))));
    });
    root.append(wrap);
    const count = addCal.selected.size;
    root.append(el('div', { class: 'wizard-foot' },
      el('button', { class: 'btn btn--ghost', text: 'Use a different provider', onclick: () => { addCal.step = 0; rerender(); } }),
      el('button', { class: 'btn btn--primary', disabled: count === 0, onclick: () => backFromAddCalendar() },
        el('span', { text: `Connect ${count} ${count === 1 ? 'calendar' : 'calendars'}` }))));
  }
}
function backFromAddCalendar() { clearTimeout(addCal.timer); addCal.step = 0; navigate(state.returnTo || 'calendar'); }

// ---------------- Screen: Settings ----------------
// deviceName starts empty; the "Daniel's MacBook" demo name is applied at RENDER time only in
// mock mode (see renderConfig). It must NOT be decided here at module load: the loopback
// transport (the desktop App) resolves Bridge.available asynchronously after boot's /health
// probe, so reading Bridge.available now would still be false and would wrongly seed the demo
// name into the real App. Deciding in the render keeps the value impersonal for any real shell.
const settings = { autoSync: true, startup: true, interval: 15, windowDays: 14, deviceName: '' };

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

// ensureCalendarAccounts — lazy-load ONLY the connected accounts once for the Calendar module, then
// do a SINGLE softRepaint when the list settles. The module no longer lists calendars per account
// (the target-calendar select moved to the per-pair wizard), so it never precharges
// live.calendars[*] — that would be wasted fetches and extra repaints. Guarded so it runs once: if
// live.accounts is already populated there is nothing to do. softRepaint() avoids replaying the
// entrance animation when the data arrives.
function ensureCalendarAccounts(view) {
  if (!Bridge.available) return;
  // Also prime live.pairs (desktop App) so the forget confirm can count the syncs that will be
  // deleted. Without this, opening Calendar Settings without first visiting the pair list leaves
  // live.pairs === null, and confirmDisconnectAccount would under-warn ("no syncs") about a delete
  // the server will still perform. loadPairs() is idempotent/cached, so this is cheap on repeat.
  if (Bridge.desktopApp && live.pairs === null) loadPairs();
  if (live.accounts !== null) return;
  loadAccounts().finally(() => { if (state.view === view) softRepaint(); });
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
  clipboardDevicesOpen = false;
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

// ---------------- Screen: Calendar module ----------------
// Desktop App only. This module is now ONLY the connected calendar accounts (connect / per-account
// unlink). The target calendar, the schedule (interval / window) and the .txt export are no longer
// global settings: target + interval belong to each sync pair (chosen in the Add-pair wizard) and
// the .txt export is a per-pair action with its own popup. Reached from the hub's Calendar nav-row.
function renderCalendarSettings(root) {
  root.append(viewHeader('Calendar', { onBack: () => navigate('config') }));

  if (!Bridge.desktopApp) {
    // Defensive: this module is desktop-only. Any other transport bounces back to the hub.
    navigate('config');
    return;
  }

  ensureCalendarAccounts('calendar-settings');
  const accounts = live.accounts || [];

  // Calendar accounts — one humane row per connected account (email as the headline, a friendly
  // sub-line, a clear Disconnect with confirm), plus a Connect affordance. The internal accountRef
  // (a GUID) is NEVER shown to the user.
  const accountRows = [];
  if (live.accounts === null) {
    accountRows.push(cfgRow('Loading…', el('div', { class: 'cfg-row__hint', text: 'Reading your connected accounts' }), el('span', { class: 'spinner' })));
  } else if (accounts.length === 0) {
    accountRows.push(el('div', { class: 'empty-cal' },
      el('div', { class: 'empty-cal__title', text: 'No calendar accounts yet' }),
      el('div', { class: 'empty-cal__sub', text: 'Connect a calendar below to pick a target and start syncing.' })));
  } else {
    accounts.forEach((acc) => { accountRows.push(accountCard(acc)); });
  }

  root.append(cfgSection('Your calendar accounts', ...accountRows));

  // Connect section — its own card so the heading explains what connecting does, with a one-click
  // "Use <identity email>" primary when the signed-in identity is a Microsoft account.
  root.append(connectAccountCard());

  // Note: the per-sync target calendar, interval/window and the .txt export used to live here as
  // global settings. They are now per-pair concerns — target + interval are picked in the Add-pair
  // wizard, and the .txt export is a per-pair action with its own popup on the dashboard.
  root.append(el('div', { class: 'cfg-note', style: 'margin-top:10px;padding:0 4px;font-size:11px;color:var(--ink-3);line-height:16px' },
    'Target calendar, interval and .txt export are set per sync. Manage them on each pair from the Calendar Sync screen.'));
}

// accountLabel(acc) — the humane { title, sub } for a connected account. The headline is the real
// email when known, then a display name, and only ever a dignified generic ("Microsoft account")
// if neither is present — NEVER the internal accountRef GUID. The sub-line is a short, human note
// about what the account is, defaulting to "Connected" when we have nothing more specific.
function accountLabel(acc) {
  const email = (acc.email || '').trim();
  const display = (acc.displayName || '').trim();
  // A displayName that is actually the GUID (older servers fell back to the ref) is not a name.
  const displayIsRef = display && acc.accountRef && display === acc.accountRef;
  const niceDisplay = displayIsRef ? '' : display;

  // Headline preference: email > a real (non-GUID) display name > a dignified generic.
  const title = email || niceDisplay || 'Microsoft account';

  // Sub-line: if the headline is the email, optionally show the person's name; otherwise a short
  // human descriptor. We don't have the scope on /api/accounts, so keep it generic and friendly.
  let sub;
  if (email && niceDisplay && niceDisplay.toLowerCase() !== email.toLowerCase()) {
    sub = niceDisplay;
  } else if (acc.isDefault) {
    sub = 'Microsoft calendar - default account';
  } else {
    sub = 'Microsoft calendar - connected';
  }
  return { title, sub };
}

// accountCard(acc) — one connected-account row: a calendar glyph, the email headline + friendly
// sub-line, and a clear Disconnect action (which confirms before removing). Reuses the cfg-row
// layout + glass tokens so it matches the rest of Settings.
function accountCard(acc) {
  const { title, sub } = accountLabel(acc);
  const avatar = el('div', { class: 'acct-avatar', 'aria-hidden': 'true' }, iconEl('calendar', 15, 1.7));

  // Per-account consent badge (spec Pieza 1). Legacy accounts without a scope show no badge.
  const scopeLabel = acc.scope === 'read' ? 'Read-only (source)'
    : (acc.scope === 'readwrite' ? 'Read & write' : '');
  const subEl = el('div', { class: 'acct-text__sub cfg-row__hint', text: sub });
  // Own class (not a nested cfg-row__hint) so the muted styling isn't applied twice (compounded).
  if (scopeLabel) subEl.append(el('span', { class: 'acct-text__scope', text: ` · ${scopeLabel}` }));

  const text = el('div', { class: 'acct-text' },
    el('div', { class: 'acct-text__title', text: title }),
    subEl);
  const left = el('div', { class: 'acct-id' }, avatar, text);
  const disconnectBtn = el('button', {
    class: 'btn btn--ghost acct-disconnect',
    title: `Disconnect ${title}`,
    'aria-label': `Disconnect ${title}`,
    onclick: () => confirmDisconnectAccount(acc),
  }, el('span', { text: 'Disconnect' }));
  return el('div', { class: 'cfg-row acct-row' }, left, disconnectBtn);
}

// confirmDisconnectAccount(acc) — confirm before forgetting a calendar account. It DELETES every sync
// pair that uses this account (as source or destination), on every device, while events already
// created in the destination calendars stay untouched. You stay signed in. The shown count is an
// estimate from the loaded pairs (the server resolves the canonical accountId, so legacy/pool mixes
// can differ); unlinkAccount announces the real deleted count from affectedPairIds afterwards.
function confirmDisconnectAccount(acc) {
  const { title } = accountLabel(acc);
  const ref = acc.accountRef;
  // Three states: known-some (count > 0), known-none (pairs loaded, count 0), and UNKNOWN
  // (live.pairs === null — the pair list was never loaded on this screen). We must NOT collapse
  // unknown into "no syncs": that would tell the user nothing destructive happens when the server
  // may still delete syncs. So when pairs are unknown we show the destructive copy unconditionally
  // (dropping the "about N" clause), and only show the benign copy when we KNOW the count is zero.
  const pairsKnown = live.pairs !== null;
  const affected = (live.pairs || []).filter((p) => {
    const s = (p.source && p.source.accountRef) || null;
    const d = (p.destination && p.destination.accountRef) || null;
    return s === ref || d === ref;
  }).length;
  const destructive = !pairsKnown || affected > 0;

  let detail;
  if (affected > 0) {
    detail = `Forgetting this account deletes the syncs that use it as a source or destination ` +
      `(about ${affected}), on all your devices. Events already created in the destination ` +
      `calendars are NOT deleted. You stay signed in to the app.`;
  } else if (!pairsKnown) {
    detail = `Forgetting this account deletes the syncs that use it as a source or destination, on ` +
      `all your devices. Events already created in the destination calendars are NOT deleted. You ` +
      `stay signed in to the app.`;
  } else {
    detail = `This calendar account has no syncs. Forgetting it only removes the connection, on all ` +
      `your devices. You stay signed in to the app.`;
  }

  const cancelBtn = el('button', { class: 'btn btn--ghost', type: 'button', text: 'Keep connected' });
  const confirmBtn = el('button', { class: 'btn btn--primary acct-disconnect--confirm', type: 'button' },
    el('span', { text: destructive ? 'Forget and delete syncs' : 'Forget' }));

  const body = el('div', { class: 'disconnect-modal' },
    el('div', { class: 'disconnect-modal__lead', text: `Forget ${title}?` }),
    el('div', { class: 'cfg-row__hint disconnect-modal__detail', text: detail }),
    el('div', { class: 'modal__foot' }, cancelBtn, confirmBtn));

  const modal = openModal({ title: 'Forget calendar account', body });
  cancelBtn.addEventListener('click', () => modal.close());
  confirmBtn.addEventListener('click', () => {
    confirmBtn.disabled = true; cancelBtn.disabled = true;
    const span = confirmBtn.querySelector('span'); if (span) span.textContent = 'Forgetting…';
    modal.close();
    unlinkAccount(acc.accountRef);
  });
}

// connectAccountCard() — the "add a calendar account" card on the Calendar screen (which is ONLY the
// account list; identity lives in Settings). Adding an account never changes your sign-in; the copy
// says so. Two scopes: a source-only mailbox connects read-only (least privilege, friendlier to
// enterprise tenants); a destination connects read/write.
function connectAccountCard() {
  const wrap = el('div', { class: 'connect-cal__wrap' });
  const repaint = () => { if (state.view === 'calendar-settings') softRepaint(); };

  const sourceBtn = el('button', { class: 'btn btn--primary connect-cal',
    onclick: () => connectAccount({ scope: 'read', btn: sourceBtn, wrap, onConnected: repaint }) },
    iconEl('link', 13, 1.8), el('span', { class: 'connect-cal__label', text: 'Add a source account (read-only)' }));

  const destBtn = el('button', { class: 'btn btn--ghost connect-cal connect-cal--other',
    onclick: () => connectAccount({ scope: 'readwrite', btn: destBtn, wrap, onConnected: repaint }) },
    el('span', { class: 'connect-cal__label', text: 'Add a destination account (read & write)' }));

  wrap.append(sourceBtn, destBtn);

  return cfgSection('Add a calendar account',
    el('div', { class: 'cfg-row__hint', style: 'padding:0 2px 6px',
      text: 'Connect the calendar of an account you want to sync. Pick read-only for a source you only ' +
            'mirror FROM, or read & write for a destination you sync INTO. This does not change your sign-in.' }),
    wrap);
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

// renderServerWarmup — the desktop App server warm-up gate. Shown before the identity gate while
// the Azure F1 server cold-starts. Three states: connecting (first probe), waking (cold start in
// progress, with a subtle border-light spinner), and error (budget spent → friendly message +
// Retry). Liquid Glass card; decorative glow lives on the spinner's own ring, nothing flickers on
// hover and the Retry button stays clickable.
function renderServerWarmup(root) {
  // Error state: the warm-up budget ran out without a healthy answer.
  if (serverHealth.error) {
    const card = el('div', { class: 'glass glass--card pair-card identity-card', style: 'margin-top:14px' },
      el('div', { class: 'about-logo', style: 'margin:4px auto 0', html: logoSvg({ size: 56 }) }),
      el('div', { class: 'pair-title', text: 'Can’t reach Zync Master' }),
      el('div', { class: 'pair-sub', text: 'Can’t reach the Zync Master server right now. Check your connection and try again.' }),
      el('div', { class: 'identity-error', role: 'alert', style: 'margin-top:2px' },
        iconEl('alert', 13, 1.8), el('span', { text: 'The server did not respond in time.' })),
      el('button', { class: 'btn btn--primary', style: 'align-self:stretch', onclick: () => retryServerHealth() },
        iconEl('sync', 14, 1.8), el('span', { text: 'Retry' })),
    );
    root.append(card);
    return;
  }

  // Connecting (first probe) vs waking (cold start in progress).
  const waking = serverHealth.waking;
  const title = waking ? 'Waking up the server…' : 'Connecting to Zync Master…';
  const sub = waking
    ? 'This can take a moment on first launch — the server is starting up.'
    : 'Reaching the Zync Master server.';
  const card = el('div', { class: 'glass glass--card pair-card identity-card warmup-card', style: 'margin-top:14px' },
    el('div', { class: 'about-logo', style: 'margin:4px auto 0', html: logoSvg({ size: 56 }) }),
    el('div', { class: 'pair-title', text: title }),
    el('div', { class: 'pair-sub', text: sub }),
    el('div', { class: 'identity-waiting warmup-waiting' },
      el('span', { class: 'spinner warmup-spinner' }),
      el('span', { class: 'identity-waiting__txt', text: waking ? 'Waking up…' : 'Connecting…' })),
  );
  root.append(card);
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
        pairing.step = 2; setAurora('success'); announce('Device paired successfully.'); rerender();
        setTimeout(() => setAurora('idle'), 2600);
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
    setAurora('syncing');
    announce('Sync started');
    rerender();
    Bridge.call('syncNow')
      .then(() => Bridge.call('getStatus'))
      .then((s) => applyNativeStatus(s))
      .catch(() => { state.sync = 'error'; setAurora('error'); announce('Sync failed.'); rerender(); });
    return;
  }

  // Standalone / demo (no host): the mock progress animation.
  state.sync = 'syncing';
  state.progress = { done: 0, total: 20 };
  setAurora('syncing');
  announce('Sync started');
  rerender();

  clearInterval(syncTimer);
  syncTimer = setInterval(() => {
    state.progress.done += 1;
    if (state.progress.done >= state.progress.total) {
      clearInterval(syncTimer);
      state.sync = 'success';
      setAurora('success');
      announce('Sync complete. 20 events synchronised.');
      rerender();
      setTimeout(() => { state.sync = 'ok'; setAurora('idle'); rerender(); }, 1600);
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
  const card = el('div', { class: 'glass glass--card modal__card', role: 'dialog', 'aria-modal': 'true', 'aria-label': title },
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

// ---------------- Per-pair .txt export popup ----------------
// openExportTxtModal(pair) — month/year/include-cancelled options for exporting the pair's SOURCE
// calendar for one month. Routing is by source provider:
//   * OutlookCom source → generateTxt (reads the local Outlook on THIS PC via COM). Requires
//     Outlook installed; the modal does not open when COM is unavailable (defence in depth).
//   * MicrosoftGraph source → exportSourceTxt (the server reads the online source calendar and
//     returns the .txt; the App saves it). No COM dependency.
// The copy adapts to the provider, so a Graph source never claims "local Outlook on this PC".
const MONTH_LABELS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
// Timeout for slow Outlook COM round-trips (listLocalCalendars / generateTxt / exportSourceTxt). A
// cold Outlook plus the native save dialog can run far past the 60s Bridge.call default, which would
// otherwise reject as "bridge timeout" mid-operation. Matches the connect flow's 210s budget.
const COM_SLOW_TIMEOUT_MS = 210000;
function openExportTxtModal(pair) {
  if (!Bridge.available || !pair) return;
  const isCom = (pair.src && pair.src.provider || '').toLowerCase() === 'outlookcom';
  // A COM source needs Outlook on this device; bail rather than opening a modal that can't export.
  if (isCom && !comAvailable()) return;
  const now = new Date();
  const selected = { month: now.getMonth() + 1, year: now.getFullYear(), includeCancelled: true };

  // Month select.
  const monthSel = el('select', { class: 'field-select', 'aria-label': 'Month' });
  MONTH_LABELS.forEach((label, i) => monthSel.append(el('option', { value: String(i + 1), text: label, selected: (i + 1) === selected.month })));
  monthSel.addEventListener('change', () => { selected.month = Number(monthSel.value); });

  // Year select — current year and the four preceding years.
  const yearSel = el('select', { class: 'field-select', 'aria-label': 'Year' });
  for (let y = now.getFullYear(); y >= now.getFullYear() - 4; y--) {
    yearSel.append(el('option', { value: String(y), text: String(y), selected: y === selected.year }));
  }
  yearSel.addEventListener('change', () => { selected.year = Number(yearSel.value); });

  // Include cancelled toggle (default on).
  const cancelToggle = toggleLocal(() => selected.includeCancelled, (v) => { selected.includeCancelled = v; }, 'Include cancelled events');

  const feedback = el('div', { class: 'cfg-row__hint modal__feedback', style: 'min-height:16px' });
  // Explicit Cancel (btn--ghost) alongside the X/overlay/Escape, for parity with the cleanup and
  // new-calendar modals. Closes the modal — the close handler also cancels any pending export.
  const cancelBtn = el('button', { class: 'btn btn--ghost', type: 'button', text: 'Cancel' });
  const confirmBtn = el('button', { class: 'btn btn--primary', type: 'button' }, iconEl('folder', 13, 1.6), el('span', { text: 'Export .txt' }));

  const srcText = isCom
    ? 'Exports your local Outlook calendar on this PC for the selected month.'
    : 'Exports the source calendar (read online by the server) for the selected month.';
  const body = el('div', { class: 'export-modal' },
    el('div', { class: 'export-modal__src cfg-row__hint', style: 'margin-bottom:4px' }, srcText),
    el('div', { class: 'glass glass--card config-section' },
      cfgRow('Month', null, monthSel),
      cfgRow('Year', null, yearSel),
      cfgRow('Include cancelled', el('div', { class: 'cfg-row__hint', text: 'Keep events marked as cancelled' }), cancelToggle)),
    feedback,
    el('div', { class: 'modal__foot' }, cancelBtn, confirmBtn));

  // Pending guard: a slow COM export (Outlook cold start + the host save dialog) can outlive the
  // modal if the user closes it mid-flight. Track that so the late .then/.catch becomes a no-op
  // instead of poking a detached span and leaving the UI "stuck", and so onClose can fire the
  // host's cancel for the in-flight action rather than dangling the COM call.
  let pending = false;
  let stale = false;

  const modal = openModal({
    title: 'Export to .txt',
    body,
    onClose: () => {
      // Closing while an export is in flight: mark the response stale and ask the host to abort the
      // COM/server export so it doesn't keep a save dialog / Graph read alive behind a closed modal.
      if (pending) { stale = true; Bridge.call('cancelExport').catch(() => {}); }
    },
  });

  cancelBtn.addEventListener('click', () => modal.close());

  confirmBtn.addEventListener('click', () => {
    const span = confirmBtn.querySelector('span');
    pending = true;
    confirmBtn.disabled = true;
    cancelBtn.disabled = false; // Cancel stays live so the user can bail out of a slow export.
    if (span) span.textContent = 'Saving…';
    feedback.textContent = ''; feedback.style.color = '';
    // COM source → generateTxt (local Outlook); Graph source → exportSourceTxt (server reads the
    // online source). The latter needs the pair id so the server knows which source to read.
    const action = isCom ? 'generateTxt' : 'exportSourceTxt';
    const req = isCom
      ? { year: selected.year, month: selected.month, includeCancelled: selected.includeCancelled }
      : { pairId: pair.id, year: selected.year, month: selected.month, includeCancelled: selected.includeCancelled };
    // Generous timeout: a cold Outlook plus the host save dialog can run well past the 60s default,
    // mirroring the connect flow. Without this the call would reject as "bridge timeout" mid-save.
    Bridge.call(action, JSON.stringify(req), COM_SLOW_TIMEOUT_MS)
      .then((r) => {
        pending = false;
        if (stale) return; // modal already closed; nothing to update.
        if (r && r.cancelled) {
          // The host's save dialog was dismissed — leave the modal open so the user can retry.
          confirmBtn.disabled = false;
          if (span) span.textContent = 'Export .txt';
          feedback.textContent = 'Save cancelled.'; feedback.style.color = 'var(--ink-2)';
          return;
        }
        if (span) span.textContent = 'Saved';
        feedback.textContent = 'File saved.'; feedback.style.color = 'var(--ok)';
        setTimeout(() => modal.close(), 900);
      })
      .catch((e) => {
        pending = false;
        if (stale) return; // modal already closed; do not poke a detached UI.
        confirmBtn.disabled = false;
        if (span) span.textContent = 'Export .txt';
        feedback.textContent = (e && e.message) || 'Export failed.'; feedback.style.color = 'var(--err)';
      });
  });
}

// toggleLocal — a toggle whose set() does NOT call pushConfig (the shared toggle() pushes settings to
// the host on every flip, which is wrong for an ephemeral in-modal option). Same look + a11y. The
// optional label becomes the accessible name (aria-label) — the role=switch element has no visible
// text of its own, so without it screen readers announce an unnamed switch.
function toggleLocal(get, set, label) {
  const attrs = { class: 'toggle', role: 'switch', 'aria-checked': String(get()), tabindex: '0' };
  if (label) attrs['aria-label'] = label;
  const t = el('div', attrs);
  const flip = () => { const v = !get(); set(v); t.setAttribute('aria-checked', String(v)); };
  t.addEventListener('click', flip);
  t.addEventListener('keydown', (e) => { if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); flip(); } });
  return t;
}

// unlinkAccount(accountRef?) — unlink a calendar account. With an explicit accountRef (the
// Calendar module's per-account Unlink button) it targets exactly that account; with no argument
// it falls back to the default/first connected account. Refreshes accounts + pairs and repaints
// whichever settings screen is up.
function unlinkAccount(accountRef) {
  if (!Bridge.available) return;
  const run = () => {
    const accounts = live.accounts || [];
    const target = accountRef
      ? accounts.find((x) => x.accountRef === accountRef) || { accountRef }
      : accounts.find((x) => x.isDefault) || accounts[0];
    if (!target) { announce('No connected account to unlink.'); return; }
    let removed = 0;
    Bridge.call('unlinkAccount', target.accountRef)
      .then((r) => { removed = (r && r.affectedPairIds || []).length; live.accounts = null; return loadPairs(); })
      .then(() => loadAccounts())
      .then(() => {
        announce(removed > 0
          ? `Forgot the account; removed ${removed} sync${removed === 1 ? '' : 's'}.`
          : 'Forgot the account.');
        if (state.view === 'config' || state.view === 'calendar-settings') softRepaint();
      })
      .catch(() => { announce('Unlink failed.'); });
  };
  if (live.accounts === null && !accountRef) loadAccounts().then(run); else run();
}

// connectAccount({ scope, btn, wrap, onConnected }) — core OAuth connect of a calendar account into
// the per-user pool. `scope` is 'read' (source-only; least privilege, friendlier to enterprise
// tenants) or 'readwrite' (destination). Long-running interactive flow (system browser) with a Cancel
// affordance. On success it refreshes the account list and calls onConnected(newRef): newRef is the
// accountRef that appeared since the pre-connect snapshot, or null when the connect refreshed an
// already-listed account (server connect is idempotent by email — Phase B). All feedback is inline.
function connectAccount({ scope, btn, wrap, onConnected }) {
  if (!Bridge.desktopApp) return;
  const span = btn && btn.querySelector('span.connect-cal__label');
  const orig = span ? span.textContent : '';
  if (span) span.textContent = 'Connecting…';
  if (btn) btn.disabled = true;

  const before = new Set((live.accounts || []).map((a) => a.accountRef));

  let cancelBtn = null;
  if (wrap) {
    cancelBtn = el('button', { class: 'btn btn--ghost connect-cal__cancel', text: 'Cancel',
      onclick: () => { if (cancelBtn) cancelBtn.disabled = true; Bridge.call('cancelConnect').catch(() => {}); } });
    wrap.append(cancelBtn);
  }
  const removeCancel = () => { if (cancelBtn && cancelBtn.parentNode) cancelBtn.parentNode.removeChild(cancelBtn); cancelBtn = null; };

  Bridge.call('connectCalendar', JSON.stringify({ scope: scope === 'readwrite' ? 'readwrite' : 'read' }), 210000)
    .then((r) => {
      if (r && r.connected) {
        announce('Calendar account connected.');
        live.accounts = null;
        return loadAccounts().then(() => {
          (live.accounts || []).forEach((acc) => { if (!live.calendars[acc.accountRef]) loadCalendars(acc.accountRef); });
          const newRef = (live.accounts || []).map((a) => a.accountRef).find((ref) => !before.has(ref)) || null;
          if (typeof onConnected === 'function') onConnected(newRef);
        });
      }
      if (r && r.cancelled) { if (span) span.textContent = 'Cancelled'; }
      else { if (span) span.textContent = 'Failed'; announce((r && r.error) ? `Connect failed: ${r.error}` : 'Connect failed.'); }
    })
    .catch(() => { if (span) span.textContent = 'Failed'; announce('Connect failed.'); })
    .finally(() => {
      removeCancel();
      if (btn) btn.disabled = false;
      if (span && span.textContent !== orig) setTimeout(() => { if (span) span.textContent = orig; }, 1800);
    });
}

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

// ---------------- Bottom nav ----------------
const NAV = [
  { id: 'home',    label: 'Home',     icon: 'home' },
  { id: 'config',  label: 'Settings', icon: 'settings' },
  { id: 'pairing', label: 'Pair',     icon: 'link' },
];
// Map sub-routes back to a parent tab so the indicator follows the user.
const TAB_MAP = { home: 'home', calendar: 'home', 'add-pair': 'home', 'add-calendar': 'home', config: 'config', 'calendar-settings': 'config', 'clipboard-settings': 'config', about: 'config', pairing: 'pairing' };

// The visible tabs. "Pair" is the legacy manual pairing-by-key walkthrough; a device now
// registers as part of the identity sign-in, so the tab is meaningless in every REAL transport
// (the App and the web panel). It survives ONLY in the mock file:// demo so the standalone page
// still showcases the flow. The pairing screen is gated again in navigate().
function navItems() {
  return Bridge.available ? NAV.filter((i) => i.id !== 'pairing') : NAV;
}

function renderNav() {
  const nav = $('#navbar');
  if (!nav) return;
  const items = navItems();
  const currentTab = TAB_MAP[state.view] || 'home';
  const activeIndex = Math.max(0, items.findIndex((i) => i.id === currentTab));
  nav.style.setProperty('--nav-count', String(items.length));
  nav.style.setProperty('--nav-idx', String(activeIndex));
  nav.replaceChildren();
  nav.append(el('span', { class: 'navbar__indicator' }));
  items.forEach((it) => {
    const active = it.id === currentTab;
    nav.append(el('button', { class: 'nav-item', 'aria-current': String(active), onclick: () => navigate(it.id) },
      iconEl(it.icon, 15, 1.7), el('span', { text: it.label })));
  });
}

// ---------------- Render dispatch ----------------
function rerender() {
  const root = $('#view');
  if (!root) return;
  root.replaceChildren();
  root.classList.remove('enter');

  // Web panel sign-in gate: until the session resolves as signed-in, the only screen is the
  // sign-in card and the bottom nav is hidden. Other transports gate themselves (pairing).
  if (Bridge.webPanel && webAuth.resolved && !webAuth.signedIn) {
    renderSignIn(root);
    const nav = $('#navbar');
    if (nav) { nav.replaceChildren(); nav.hidden = true; }
    void root.offsetWidth;
    root.classList.add('enter');
    return;
  }

  // Desktop App server warm-up gate: in native/loopback, this gate sits IN FRONT of the identity
  // gate. While the server is cold-starting (Azure F1) we show "connecting / waking up" with a
  // spinner; if the warm-up budget runs out we show a friendly error + Retry. Only once the
  // server is alive (serverHealth.ok) does the identity gate below get a chance to paint. The web
  // panel and mock demo do NOT use this gate.
  if (Bridge.desktopApp && !serverHealth.ok && (serverHealth.waking || serverHealth.error || !serverHealth.checked)) {
    renderServerWarmup(root);
    const nav = $('#navbar');
    if (nav) { nav.replaceChildren(); nav.hidden = true; }
    void root.offsetWidth;
    root.classList.add('enter');
    return;
  }

  // Desktop App identity gate: in native/loopback, until getIdentityState reports signed-in the
  // only screen is the identity sign-in card (Microsoft / magic-link) and the bottom nav is
  // hidden. The web panel and the mock demo do NOT use this gate.
  if (Bridge.desktopApp && identityAuth.resolved && !identityAuth.signedIn) {
    renderIdentitySignIn(root);
    const nav = $('#navbar');
    if (nav) { nav.replaceChildren(); nav.hidden = true; }
    void root.offsetWidth;
    root.classList.add('enter');
    return;
  }
  const nav = $('#navbar');
  if (nav) nav.hidden = false;

  switch (state.view) {
    case 'home': renderHome(root); break;
    case 'calendar': renderCalendar(root); break;
    case 'add-pair': renderAddPair(root); break;
    case 'add-calendar': renderAddCalendar(root); break;
    case 'config': renderConfig(root); break;
    case 'calendar-settings': renderCalendarSettings(root); break;
    case 'clipboard-settings': renderClipboardSettings(root); break;
    case 'about': renderAbout(root); break;
    case 'pairing': renderPairing(root); break;
    default: renderHome(root);
  }
  // retrigger the staggered card entrance
  void root.offsetWidth;
  root.classList.add('enter');
  renderNav();
}
// rerenderInPlace — repaint the current view WITHOUT replaying the entrance animation (no
// `enter` class) and preserving scroll, so progress ticks and in-screen control changes don't
// flicker the whole screen. Covers the dashboard views (home/calendar) used during the syncing
// tick AND the settings views (config/calendar-settings) so toggling interval/theme/select on
// those screens is a smooth repaint instead of a full re-entrance via rerender().
const SOFT_REPAINT_VIEWS = { home: renderHome, calendar: renderCalendar, config: renderConfig, 'calendar-settings': renderCalendarSettings, 'clipboard-settings': renderClipboardSettings };
function rerenderInPlace() {
  const render = SOFT_REPAINT_VIEWS[state.view];
  if (!render) return;
  // The sign-in gate owns the screen: a stale background repaint (e.g. a late loadPairs)
  // must never paint the dashboard over the sign-in card and leave the nav hidden.
  if (Bridge.webPanel && webAuth.resolved && !webAuth.signedIn) return;
  if (Bridge.desktopApp && !serverHealth.ok) return;
  if (Bridge.desktopApp && identityAuth.resolved && !identityAuth.signedIn) return;
  const root = $('#view');
  if (!root) return;
  const prevScroll = root.scrollTop;   // a tick/refresh repaint must not snap the user back to the top
  root.replaceChildren();
  render(root);
  root.scrollTop = prevScroll;
  const nav = $('#navbar');
  if (nav) nav.hidden = false;          // a real view is up → the bottom nav must be visible
  renderNav();
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

function togglePair(id) {
  if (openPairs.has(id)) openPairs.delete(id); else openPairs.add(id);
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

// ---------------- Launch splash ----------------
let launchDismissed = false;
function dismissLaunch() {
  if (launchDismissed) return;
  launchDismissed = true;
  const launch = $('#launch');
  if (!launch) return;
  launch.classList.add('is-out');
  launch.addEventListener('animationend', () => { launch.hidden = true; }, { once: true });
  setTimeout(() => { launch.hidden = true; }, 800); // safety in case animationend never fires
}
function playLaunch() {
  const reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  // Hard cap so the splash ALWAYS clears, even with no bridge, a wedged gate, or a slow host.
  // This is the only unconditional dismissal; every other path goes through maybeDismissLaunch,
  // which keeps the splash up until the gate that will own the screen has settled.
  setTimeout(dismissLaunch, reduce ? 4000 : 6000);
}

// splashGatesSettled — true once the screen that will replace the splash is determined, so the
// splash can clear WITHOUT flashing an empty/half-built dashboard before a gate decides. Single
// source of truth for "is it safe to drop the splash":
//   * web panel    — the session probe has answered (webAuth.resolved); the next paint is either
//                    the sign-in gate or the dashboard.
//   * desktop app  — the warm-up gate decides first: while the health probe is still pending we
//                    hold the splash; once it errors or is still waking, that gate screen is the
//                    next paint, so we may drop. Only when the server is healthy do we additionally
//                    wait for the identity gate to resolve (so we never flash an empty dashboard in
//                    the gap between health-ok and identity-resolved).
//   * mock / other — no gates, always settled.
function splashGatesSettled() {
  if (Bridge.webPanel) return webAuth.resolved;
  if (Bridge.desktopApp) {
    if (!serverHealth.checked) return false;
    if (serverHealth.error || !serverHealth.ok) return true;
    return identityAuth.resolved;
  }
  return true;
}

// maybeDismissLaunch — the SINGLE deterministic dismissal point. No-op until the gates settle, so
// callers can fire it freely from every settle path (boot, health probe, onServerReady, the
// unauthorized gate) without racing two independent timers against the gate. The optional floor
// gives the splash a brief minimum hold so a near-instant host still feels intentional.
function maybeDismissLaunch(floorMs) {
  if (!splashGatesSettled()) return;
  if (floorMs) setTimeout(dismissLaunch, floorMs);
  else dismissLaunch();
}

// ---------------- Boot ----------------
async function boot() {
  hydrateIcons();
  applyTheme(storedTheme());
  wireTitlebar();
  const tag = $('#pausedTag'); if (tag) tag.hidden = true;
  // Single source of truth for the splash version: write VERSION over the HTML placeholder.
  const launchVer = $('#launchVersion'); if (launchVer) launchVer.textContent = `v${VERSION}`;

  // Settle the http(s) transport (web vs the App's loopback host) before the first
  // data-driven paint. Native + mock resolve synchronously; this only awaits the probe.
  await Bridge.resolveTransport();
  document.documentElement.setAttribute('data-transport', Bridge.mode);

  // Web panel: a 401 from any call drops us back to the sign-in gate.
  if (Bridge.webPanel) Bridge.onUnauthorized(() => { webAuth.resolved = true; webAuth.signedIn = false; rerender(); maybeDismissLaunch(); });

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

    // Dismiss the splash shortly after the first status settles (with a small floor) so the
    // app feels instant when the host responds quickly, instead of a fixed long hold.
    Bridge.call('getStatus')
      .then((s) => { applyNativeStatus(s); if (Bridge.webPanel) { webAuth.resolved = true; rerender(); } })
      .catch(() => {
        // In the web panel a failed getStatus (typically 401) is the unauthenticated state:
        // resolve the gate so the sign-in screen shows instead of an empty dashboard.
        if (Bridge.webPanel) { webAuth.resolved = true; webAuth.signedIn = false; rerender(); }
      })
      // Route through the single gate-aware dismissal. In the web panel getStatus IS the gate, so
      // this drops the splash; in the desktop app the health/identity gates own dismissal (this is
      // a no-op until they settle), so getStatus resolving early can no longer reveal the dashboard
      // before those gates have decided what to paint.
      .finally(() => maybeDismissLaunch(450));
  }

  rerender();
  playLaunch();
  // Non-bridge transports (the mock/demo panel) have no gates and no inbound settle callback, so
  // dismiss as soon as the first paint is up — splashGatesSettled() is already true for them, and
  // this keeps the demo splash short instead of waiting on playLaunch's hard cap.
  if (!Bridge.available) maybeDismissLaunch(450);
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
else boot();
