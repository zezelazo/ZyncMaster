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

import { icon, logoSvg, hydrateIcons } from './icons.js';
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
  let seq = 0;

  function newId() { return `c${Date.now()}_${seq++}`; }
  function safeParse(s) { try { return JSON.parse(s); } catch (_) { return s; } }

  function handleInbound(text) {
    let msg;
    try { msg = JSON.parse(text); } catch (_) { return; }
    if (msg && msg.event === 'status') { if (statusCb) statusCb(msg.payload); return; }
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
        if (res.status === 200) handleInbound(await res.text());
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
      try { pairs = await req('GET', '/api/pairs'); } catch (_) { pairs = []; }
      return statusFromPairs(me, pairs);
    }

    // Everything else maps through the pure action->REST table. A null mapping is a device-
    // only action that is inert in the browser panel (getAutoStart reports disabled; the
    // rest are no-ops) — the UI hides their entry points in web mode anyway.
    const mapped = webRequestFor(action, data);
    if (mapped === null) {
      return action === 'getAutoStart' ? { enabled: false } : null;
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
    get mode() { return mode; },
    resolveTransport, start, call, windowAction,
    onStatus(cb) { statusCb = cb; },
    onUnauthorized(cb) { unauthorizedCb = cb; },
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
const VERSION = '1.0.0';
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
  autoStart: null,   // bool from getAutoStart
  me: null,          // { email, displayName } from getStatus/api/me (web panel)
  loadedPairs: false,
  loadingPairs: false,
};

// Web panel sign-in gate state. Only consulted in web mode (Bridge.webPanel). signedIn flips
// true once getStatus resolves; a 401 from any web call flips it false and shows the gate.
const webAuth = { resolved: false, signedIn: false };

async function loadPairs() {
  if (!Bridge.available) { live.pairs = null; return PAIRS; }
  live.loadingPairs = true;
  try {
    const list = await Bridge.call('listPairs');
    live.pairs = Array.isArray(list) ? list : [];
    live.loadedPairs = true;
    return live.pairs;
  } catch (_) {
    live.pairs = live.pairs || [];
    return live.pairs;
  } finally {
    live.loadingPairs = false;
  }
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

// Map a SyncPair (server shape) into the accordion view-model the renderer expects.
function pairViewModel(p) {
  const endpointLabel = (e) => {
    if (!e) return { svc: 'Outlook', acct: '', email: '' };
    const com = (e.provider || '').toLowerCase() === 'outlookcom';
    return {
      svc: com ? 'Outlook' : 'Outlook',
      acct: e.calendarName || (com ? 'Outlook (this PC)' : 'Calendar'),
      email: com ? 'Local Outlook' : (e.accountRef || ''),
      provider: e.provider || '',
    };
  };
  const lr = p.lastResult;
  const events = lr
    ? [
        lr.created ? { time: '', title: `${lr.created} created`, sub: 'Last run', action: 'created' } : null,
        lr.updated ? { time: '', title: `${lr.updated} updated`, sub: 'Last run', action: 'updated' } : null,
        lr.deleted ? { time: '', title: `${lr.deleted} deleted`, sub: 'Last run', action: 'deleted' } : null,
        lr.skipped ? { time: '', title: `${lr.skipped} skipped`, sub: 'Last run', action: 'skipped' } : null,
      ].filter(Boolean)
    : [];
  const total = lr ? (lr.created + lr.updated + lr.deleted + lr.skipped) : 0;
  return {
    id: p.id,
    name: p.name,
    serverState: p.state, // active | paused | disabled
    src: endpointLabel(p.source),
    dst: endpointLabel(p.destination),
    state: p.state === 'active' ? 'ok' : 'paused',
    lastSync: p.lastRunUtc ? new Date(p.lastRunUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '—',
    nextSync: (p.intervalMin || 10) * 60,
    total,
    eventCount: total,
    events,
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
function actionChip(kind) {
  const map = { created: ['chip--created', 'Created'], updated: ['chip--updated', 'Updated'], deleted: ['chip--deleted', 'Deleted'], skipped: ['chip--skipped', 'Skipped'] };
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

// Real dashboard stats for the browser panel, derived from the pairs the panel already
// loads. "Items synced" sums created+updated+deleted across each pair's lastResult; "Sync
// runs" counts pairs that have a recorded run. The server's MirrorResult exposes no failure
// count, so "Conflicts" has no real value to show: it renders an em-dash placeholder rather
// than a fabricated 0. Until the pairs snapshot has loaded, every cell is an em-dash.
function webPanelHomeStats() {
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

  // In the browser panel we must never show fabricated demo numbers to a real signed-in
  // user. Derive the dashboard stats from the real pairs the panel already loads; where no
  // real value exists yet (snapshot not loaded) fall back to an em-dash placeholder. The
  // literal 1,248 / 24 / 0 demo numbers are kept ONLY for the standalone mock (file://) demo.
  const homeStats = Bridge.webPanel ? webPanelHomeStats() : { items: '1,248', runs: '24', conflicts: '0' };

  root.append(el('div', { class: 'glass glass--card stats-card' },
    el('div', { class: 'stats-card__hd' },
      el('span', { class: 'status-dot', dataset: { state: cfg.dot } }),
      el('span', { class: 'stats-card__status', text: statusLabel }),
      el('span', { class: 'stats-card__sub', text: Bridge.webPanel ? 'LAST RUN' : 'THIS WEEK' }),
    ),
    el('div', { class: 'stats-grid' },
      stat(homeStats.items, 'Items synced'),
      el('div', { class: 'stat__sep' }),
      stat(homeStats.runs, 'Sync runs'),
      el('div', { class: 'stat__sep' }),
      stat(homeStats.conflicts, 'Conflicts'),
    ),
  ));

  // Modules summary line: "N active · M available". In the web panel, N is the count of
  // active real pairs; M stays the catalog size (the single Calendar module is the only one
  // shipped today, so "available" mirrors the demo's catalog count). Mock keeps its demo line.
  const moduleActive = Bridge.webPanel
    ? String((live.pairs || []).filter((p) => p && p.state === 'active').length)
    : '1';
  root.append(el('div', { class: 'section-head' },
    el('span', { class: 'section-head__title', text: 'Sync modules' }),
    el('span', { class: 'section-head__action', style: 'pointer-events:none;color:var(--ink-3)' },
      el('span', { class: 'num', text: moduleActive }), ' active · ', el('span', { class: 'num', text: '5' }), ' available'),
  ));

  // Browser panel: ensure the pairs snapshot the stats above derive from is actually loaded,
  // and repaint once it lands (the first home paint may precede any pairs fetch).
  if (Bridge.webPanel && !live.loadedPairs && !live.loadingPairs) {
    loadPairs().then(() => { if (state.view === 'home') rerenderInPlace(); });
  }

  const grid = el('div', { class: 'module-grid' });
  MODULES.forEach((m) => {
    const tile = el('button', {
      class: 'module-tile glass',
      dataset: { active: String(m.active) },
      disabled: !m.active,
      onclick: () => { if (m.active) navigate('calendar'); },
    },
      el('div', { class: 'module-tile__icon', html: icon(m.icon, { size: 20, stroke: 1.6 }) }),
      el('div', { class: 'module-tile__title', text: m.title }),
      el('div', { class: 'module-tile__sub', text: m.sub }),
      el('div', { class: 'module-tile__foot' },
        m.active
          ? el('span', { class: 'chip chip--ok' },
              el('span', { class: 'status-dot', dataset: { state: 'ok' }, style: 'width:6px;height:6px' }), 'Active')
          : el('span', { class: 'chip chip--skipped', text: 'Soon' }),
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
  const dotState = isSyncing ? 'sync' : isError ? 'error' : isOffline ? 'offline' : 'ok';
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
  const syncBtn = el('button', { class: 'pair__sync-btn', disabled: isOffline, onclick: (e) => { e.stopPropagation(); (Bridge.available && pair.id) ? runPairNow(pair.id) : runSync(); } },
    isSyncing ? el('span', { class: 'spinner', style: 'width:12px;height:12px;border-width:1.6px' }) : el('span', { style: 'display:inline-flex', html: icon('sync', { size: 12, stroke: 1.8 }) }),
    el('span', { class: 'num', text: isSyncing ? `${progress.done}/${progress.total}` : 'Sync now' }),
  );
  card.append(el('div', { class: 'pair__substats' },
    substat('Events', pair.eventCount),
    el('span', null, el('span', { class: 'route__stat-label', text: 'Next' }), el('span', { class: 'route__stat-val num', text: nextStr })),
    el('span', null, el('span', { class: 'route__stat-label', text: 'Status' }),
      el('span', { class: 'route__stat-val', text: isSyncing ? 'Syncing…' : isError ? 'Failed' : isOffline ? 'Offline' : 'Connected' })),
    syncBtn,
  ));

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

    body.append(el('div', { class: 'pair__activity-head' },
      el('span', { text: pair.events.length ? 'Last result' : 'Recent events' }),
      el('span', { class: 'num', text: `${pair.events.length} of ${pair.eventCount}` })));
    const act = el('div', { class: 'pair__activity' });
    if (pair.events.length) pair.events.forEach((row) => act.append(activityRow(row)));
    else act.append(el('div', { class: 'activity__sub', style: 'padding:8px 2px', text: 'No changes yet.' }));
    body.append(act);

    // Live controls (native shell only): pause/resume, disable/enable, delete.
    if (Bridge.available && pair.id) {
      const paused = pair.serverState === 'paused';
      const disabled = pair.serverState === 'disabled';
      const controls = el('div', { class: 'pair__controls', style: 'display:flex;gap:6px;flex-wrap:wrap;margin-top:10px' });

      controls.append(el('button', { class: 'btn btn--ghost', style: 'height:30px;padding:0 10px',
        onclick: (e) => { e.stopPropagation(); setPairState(pair.id, paused ? 'active' : 'paused'); } },
        iconEl(paused ? 'sync' : 'pause', 12, 1.8), el('span', { style: 'font-size:12px', text: paused ? 'Resume' : 'Pause' })));

      controls.append(el('button', { class: 'btn btn--ghost', style: 'height:30px;padding:0 10px',
        onclick: (e) => { e.stopPropagation(); setPairState(pair.id, disabled ? 'active' : 'disabled'); } },
        iconEl(disabled ? 'check' : 'close', 12, 1.8), el('span', { style: 'font-size:12px', text: disabled ? 'Enable' : 'Disable' })));

      controls.append(el('button', { class: 'btn btn--ghost', style: 'height:30px;padding:0 10px;color:var(--err);margin-left:auto',
        onclick: (e) => { e.stopPropagation(); deletePair(pair.id); } },
        iconEl('close', 12, 1.8), el('span', { style: 'font-size:12px', text: 'Delete' })));

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

  root.append(el('div', { style: 'margin-top:10px;text-align:center' },
    el('button', { class: 'btn', onclick: () => navigate('add-pair') }, iconEl('plus', 13, 2), el('span', { text: 'Add a calendar pair' }))));

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
function intervalRow(get, set) {
  const seg = el('div', { class: 'segmented' });
  [5, 15, 30, 60].forEach((n) => seg.append(el('button', { class: 'segmented__item', 'aria-pressed': String(get() === n), onclick: () => set(n) },
    el('span', { class: 'num', text: String(n) }), 'm')));
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
  sourceKind: null,           // 'com' | 'online'
  srcAccountRef: null, srcCalendarId: null, srcCalendarName: null,
  dstAccountRef: null, dstCalendarId: null, dstCalendarName: null,
  name: '', intervalMin: 15,
};
function resetAddPairLive() {
  Object.assign(addPairLive, {
    step: 0, sourceKind: null,
    srcAccountRef: null, srcCalendarId: null, srcCalendarName: null,
    dstAccountRef: null, dstCalendarId: null, dstCalendarName: null,
    name: '', intervalMin: 15,
  });
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
    cfg.append(intervalRow(() => addPair.intervalMin, (v) => { addPair.intervalMin = v; rerender(); }));
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
// A list of online accounts (from listAccounts), each expandable into its calendars
// (from listCalendars). Selecting a calendar invokes onPick with the chosen endpoint.
function onlineCalendarPicker(selectedCalendarId, onPick) {
  const wrap = el('div', { class: 'glass glass--card', style: 'padding:4px' });
  const list = el('div', { class: 'cal-list' });

  const accounts = live.accounts || [];
  if (accounts.length === 0) {
    list.append(el('div', { class: 'cal-item__sub', style: 'padding:12px', text: 'No connected accounts. Connect one on the server first.' }));
  }

  accounts.forEach((acc) => {
    const cals = live.calendars[acc.accountRef];
    list.append(el('div', { class: 'route__label', style: 'padding:8px 10px 2px', text: acc.displayName || acc.accountRef }));
    if (!cals) {
      list.append(el('div', { class: 'cal-item__sub', style: 'padding:6px 12px', text: 'Loading calendars…' }));
      loadCalendars(acc.accountRef).then(() => { if (state.view === 'add-pair') rerender(); });
      return;
    }
    if (cals.length === 0) {
      list.append(el('div', { class: 'cal-item__sub', style: 'padding:6px 12px', text: 'No calendars on this account.' }));
    }
    cals.forEach((c) => {
      const selected = selectedCalendarId === c.id;
      list.append(el('button', { class: `cal-item${selected ? ' is-selected' : ''}`,
        onclick: () => onPick(acc, c) },
        pairBadge('Outlook'),
        el('div', null,
          el('div', { class: 'cal-item__name', text: c.displayName || c.id }),
          el('div', { class: 'cal-item__sub', text: acc.displayName || acc.accountRef })),
        el('div', { class: 'cal-item__check', html: selected ? icon('check', { size: 12, stroke: 2.6 }) : '' })));
    });
  });

  wrap.append(list);
  return wrap;
}

function renderAddPairLive(root) {
  const labels = ['Source', 'Destination', 'Configure'];
  const a = addPairLive;

  root.append(viewHeader('Add a sync pair', { onBack: () => { if (a.step === 0) navigate('calendar'); else { a.step--; rerender(); } } }));
  root.append(wizardStepper(a.step, labels));

  if (live.accounts === null) {
    loadAccounts().then(() => { if (state.view === 'add-pair') rerender(); });
  }

  if (a.step === 0) {
    root.append(el('div', { class: 'wizard-title', text: 'Pick the source calendar' }));
    root.append(el('div', { class: 'wizard-sub', text: 'Changes here are mirrored to the destination. The source is never modified.' }));

    // Two source kinds: local Outlook (COM) or an online account calendar.
    const kinds = el('div', { class: 'provider-grid' });
    kinds.append(el('button', { class: `provider-tile glass${a.sourceKind === 'com' ? ' is-selected' : ''}`,
      onclick: () => { a.sourceKind = 'com'; a.srcCalendarId = 'local'; a.srcCalendarName = 'Outlook (this PC)'; a.srcAccountRef = null; rerender(); } },
      el('div', { class: 'provider-tile__logo', dataset: { tone: 'azure' }, text: 'PC' }),
      el('div', { class: 'provider-tile__name', text: 'Outlook on this PC' }),
      el('div', { class: 'provider-tile__sub', text: 'Local Outlook · read via COM' })));
    kinds.append(el('button', { class: `provider-tile glass${a.sourceKind === 'online' ? ' is-selected' : ''}`,
      onclick: () => { a.sourceKind = 'online'; rerender(); } },
      el('div', { class: 'provider-tile__logo', dataset: { tone: 'ink' }, text: 'M' }),
      el('div', { class: 'provider-tile__name', text: 'Online account' }),
      el('div', { class: 'provider-tile__sub', text: 'outlook.com · via the server' })));
    root.append(kinds);

    if (a.sourceKind === 'online') {
      root.append(onlineCalendarPicker(a.srcCalendarId, (acc, c) => {
        a.srcAccountRef = acc.accountRef; a.srcCalendarId = c.id; a.srcCalendarName = c.displayName || c.id; rerender();
      }));
    }

    const srcReady = a.sourceKind === 'com' || (a.sourceKind === 'online' && a.srcCalendarId);
    root.append(el('div', { class: 'wizard-foot' },
      el('button', { class: 'btn btn--ghost', text: 'Cancel', onclick: () => navigate('calendar') }),
      el('button', { class: 'btn btn--primary', disabled: !srcReady, onclick: () => { a.step = 1; rerender(); } },
        el('span', { text: 'Continue' }), iconEl('arrowright', 14, 1.8))));
  } else if (a.step === 1) {
    root.append(el('div', { class: 'wizard-title', text: 'Pick the destination' }));
    root.append(el('div', { class: 'wizard-sub', text: 'Events are written here. Past events on the destination are never touched.' }));
    root.append(onlineCalendarPicker(a.dstCalendarId, (acc, c) => {
      a.dstAccountRef = acc.accountRef; a.dstCalendarId = c.id; a.dstCalendarName = c.displayName || c.id; rerender();
    }));
    root.append(el('div', { class: 'wizard-foot' },
      el('button', { class: 'btn btn--ghost', text: 'Back', onclick: () => { a.step = 0; rerender(); } }),
      el('button', { class: 'btn btn--primary', disabled: !a.dstCalendarId, onclick: () => { a.step = 2; rerender(); } },
        el('span', { text: 'Continue' }), iconEl('arrowright', 14, 1.8))));
  } else if (a.step === 2) {
    if (!a.name) a.name = `${a.srcCalendarName} → ${a.dstCalendarName}`;
    root.append(el('div', { class: 'wizard-title', text: 'Configure the sync' }));
    root.append(el('div', { class: 'wizard-sub', text: 'Review the pair and tune how often it runs.' }));

    const col = (label, name, sub) => el('div', { class: 'pair-preview__col' },
      el('div', { class: 'route__label', text: label }), el('div', { class: 'pair-preview__name', text: name }), el('div', { class: 'pair-preview__email', text: sub }));
    root.append(el('div', { class: 'glass glass--card pair-preview' },
      pairBadge('Outlook'), col('SOURCE', a.srcCalendarName, a.sourceKind === 'com' ? 'Local Outlook' : a.srcAccountRef),
      el('span', { style: 'color:var(--ink-3);display:inline-flex', html: icon('arrowright', { size: 14, stroke: 1.8 }) }),
      pairBadge('Outlook'), col('DESTINATION', a.dstCalendarName, a.dstAccountRef)));

    const cfg = el('div', { class: 'glass glass--card config-section', style: 'margin-top:10px' });
    const nameInput = el('input', { class: 'field-input', value: a.name });
    nameInput.addEventListener('input', () => { a.name = nameInput.value; });
    cfg.append(el('div', { class: 'cfg-row' },
      el('div', null, el('div', { class: 'cfg-row__label', text: 'Pair name' }), el('div', { class: 'cfg-row__hint', text: 'Shown on your dashboard' })), nameInput));
    cfg.append(intervalRow(() => a.intervalMin, (v) => { a.intervalMin = v; rerender(); }));
    root.append(cfg);

    root.append(el('div', { class: 'wizard-foot' },
      el('button', { class: 'btn btn--ghost', text: 'Back', onclick: () => { a.step = 1; rerender(); } }),
      el('button', { class: 'btn btn--primary', onclick: () => completeAddPairLive() }, iconEl('check', 14, 2.2), el('span', { text: 'Create pair' }))));
  }
}

function completeAddPairLive() {
  const a = addPairLive;
  const source = a.sourceKind === 'com'
    ? { provider: 'OutlookCom', calendarId: 'local', calendarName: 'Outlook (this PC)' }
    : { provider: 'MicrosoftGraph', accountRef: a.srcAccountRef, calendarId: a.srcCalendarId, calendarName: a.srcCalendarName };
  const destination = { provider: 'MicrosoftGraph', accountRef: a.dstAccountRef, calendarId: a.dstCalendarId, calendarName: a.dstCalendarName };
  const req = { name: a.name, source, destination, intervalMin: a.intervalMin };

  Bridge.call('createPair', JSON.stringify(req))
    .then(() => loadPairs())
    .then(() => { resetAddPairLive(); navigate('calendar'); })
    .catch(() => { resetAddPairLive(); navigate('calendar'); });
}

// ---------------- Screen: Add Calendar wizard ----------------
const addCal = { step: 0, providerId: null, selected: new Set(['d1', 'd2']), timer: null };

function renderAddCalendar(root) {
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
const settings = { autoSync: true, startup: true, interval: 15, windowDays: 14, deviceName: "Daniel's MacBook" };

function toggle(get, set) {
  const t = el('div', { class: 'toggle', role: 'switch', 'aria-checked': String(get()), tabindex: '0' });
  const flip = () => { const v = !get(); set(v); t.setAttribute('aria-checked', String(v)); pushConfig(); };
  t.addEventListener('click', flip);
  t.addEventListener('keydown', (e) => { if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); flip(); } });
  return t;
}

function renderConfig(root) {
  const section = (head, ...rows) => {
    const s = el('div', { class: 'glass glass--card config-section' }, el('div', { class: 'config-section__hd', text: head }));
    rows.forEach((r) => s.append(r));
    return s;
  };
  const row = (label, hint, control) => el('div', { class: 'cfg-row' },
    el('div', null, el('div', { class: 'cfg-row__label', text: label }), hint), control);

  // Account (web panel only): show the signed-in identity and a sign-out affordance. The
  // identity comes from /api/me via getStatus; we cache it on live.me at boot.
  if (Bridge.webPanel) {
    const email = (live.me && live.me.email) || 'Signed in';
    const signOutBtn = el('button', { class: 'btn btn--ghost', style: 'color:var(--err)', text: 'Sign out', onclick: () => signOutWeb() });
    root.append(section('Account',
      row('Signed in as', el('div', { class: 'cfg-row__hint', text: email }), signOutBtn)));
  }

  // Calendar
  const targetSel = el('select', { class: 'field-select' });
  ['Work · Calendar', 'Personal · Family', '+ Create new…'].forEach((o) => targetSel.append(el('option', { text: o })));
  targetSel.addEventListener('change', pushConfig);
  root.append(section('Calendar',
    row('Target calendar', el('div', { class: 'cfg-row__hint', text: 'Where mirrored events are written' }), targetSel),
    sliderRow('Sync window', () => settings.windowDays, (v) => { settings.windowDays = v; pushConfig(); })));

  // Schedule. Run-at-startup is backed by the host's auto-start manager when bridged. It is a
  // device-only concern (a browser can't launch on OS sign-in), so the row is hidden in the
  // web panel; auto-sync + interval still show as plain preferences.
  const scheduleRows = [
    row('Auto-sync', el('div', { class: 'cfg-row__hint', text: 'Mirror changes automatically' }), toggle(() => settings.autoSync, (v) => { settings.autoSync = v; })),
    intervalRow(() => settings.interval, (v) => { settings.interval = v; pushConfig(); rerender(); }),
  ];
  if (!Bridge.webPanel) {
    // Run-at-startup is backed by the host's auto-start manager (native/loopback only).
    const startupToggle = toggle(
      () => (Bridge.available && live.autoStart !== null) ? live.autoStart : settings.startup,
      (v) => {
        settings.startup = v;
        if (Bridge.available) {
          live.autoStart = v;
          Bridge.call('setAutoStart', v ? 'true' : 'false').catch(() => {});
        }
      });
    if (Bridge.available && live.autoStart === null) {
      Bridge.call('getAutoStart').then((r) => { live.autoStart = !!(r && r.enabled); if (state.view === 'config') rerender(); }).catch(() => {});
    }
    scheduleRows.push(row('Run at startup', el('div', { class: 'cfg-row__hint', text: 'Launch Zync Master when you sign in' }), startupToggle));
  }
  root.append(section('Schedule', ...scheduleRows));

  // Tools. The .txt export is a device-only capability (writes a local file via Outlook COM),
  // so it is hidden in the web panel; unlinking the Microsoft account works over REST and is
  // offered in any non-mock transport.
  if (Bridge.available) {
    const toolRows = [];
    if (!Bridge.webPanel) {
      const txtBtn = el('button', { class: 'btn btn--ghost', onclick: () => generateBasicTxt(txtBtn) }, iconEl('folder', 13, 1.6), el('span', { text: 'Generate .txt' }));
      toolRows.push(row('Basic .txt export', el('div', { class: 'cfg-row__hint', text: 'Save this month as a pipe-delimited text file' }), txtBtn));
    }
    const unlinkBtn = el('button', { class: 'btn btn--ghost', style: 'color:var(--err)', onclick: () => unlinkAccount() }, el('span', { text: 'Unlink…' }));
    toolRows.push(row('Microsoft account', el('div', { class: 'cfg-row__hint', text: 'Disconnect and disable its sync pairs' }), unlinkBtn));
    root.append(section('Tools', ...toolRows));
  }

  // This device — device identity + the device's own pairing key. Device-only: a browser panel
  // is not a paired device, so the whole section is hidden in the web panel.
  if (!Bridge.webPanel) {
    const nameInput = el('input', { class: 'field-input', value: settings.deviceName });
    nameInput.addEventListener('input', () => { settings.deviceName = nameInput.value; });
    nameInput.addEventListener('change', pushConfig);
    root.append(section('This device',
      row('Device name', el('div', { class: 'cfg-row__hint', text: 'Visible to your other devices' }), nameInput),
      row('Pairing key',
        el('div', { class: 'cfg-row__hint' }, el('span', { class: 'chip chip--ok', style: 'margin-right:6px' }, iconEl('check', 9, 2.4), 'Paired'),
          el('span', { class: 'num', style: 'color:var(--ink-2)', text: '•••• 3f2a' })),
        el('button', { class: 'btn btn--ghost', style: 'color:var(--err)', text: 'Unpair…', onclick: () => { state.sync = 'unpaired'; pushConfig(); rerender(); } }))));
  }

  // Appearance — Dark / Light / Auto (Auto follows the OS)
  const seg = el('div', { class: 'segmented' });
  ['Dark', 'Light', 'Auto'].forEach((opt) => {
    const val = opt.toLowerCase();
    seg.append(el('button', { class: 'segmented__item', 'aria-pressed': String(storedTheme() === val), text: opt,
      onclick: () => { applyTheme(val); pushConfig(); rerender(); } }));
  });
  root.append(section('Appearance',
    row('Theme', el('div', { class: 'cfg-row__hint', text: 'Auto follows your system' }), seg)));

  // About entry
  const aboutRow = el('div', { class: 'cfg-row', style: 'cursor:pointer', onclick: () => navigate('about') },
    el('div', null, el('div', { class: 'cfg-row__label', text: 'About Zync Master' }), el('div', { class: 'cfg-row__hint', text: 'Version, credits, links' })),
    el('span', { class: 'num', style: 'font-size:11px;color:var(--ink-3);margin-right:4px', text: VERSION }),
    el('span', { style: 'transform:rotate(180deg);color:var(--ink-3);display:inline-flex', html: icon('chevronleft', { size: 14, stroke: 1.8 }) }));
  root.append(el('div', { class: 'glass glass--card config-section' }, el('div', { class: 'config-section__hd', text: 'About' }), aboutRow));
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
    el('button', { class: 'btn btn--primary', style: 'align-self:stretch',
      onclick: () => { window.location.href = '/connect?returnTo=/'; } },
      el('div', { class: 'provider-tile__logo', dataset: { tone: 'azure' }, style: 'width:18px;height:18px;border-radius:5px;font-size:11px', text: 'M' }),
      el('span', { text: 'Sign in with Microsoft' })));
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

// ---------------- Screen: About ----------------
function renderAbout(root) {
  root.append(viewHeader('About', { onBack: () => navigate('config') }));
  const link = (ic, label) => el('button', { class: 'about-link' }, iconEl(ic, 13, 1.6), label);
  root.append(el('div', { class: 'glass glass--card about-card' },
    el('div', { class: 'about-logo', html: logoSvg({ size: 64 }) }),
    el('div', { class: 'about-name', text: 'Zync Master' }),
    el('div', { class: 'about-version num', text: 'VERSION 1.0.0 · BUILD 248' }),
    el('div', { class: 'about-tag', text: 'A quiet desktop utility for mirroring calendars across Microsoft, Google and iCloud accounts. Past events are never touched.' }),
    el('div', { class: 'about-links' }, link('link', 'Website'), link('sparkle', "What's new"), link('note', 'Privacy policy'), link('folder', 'Open-source notices')),
  ));
  root.append(el('div', { class: 'glass glass--card about-credits' },
    el('div', { class: 'about-credits__hd', text: 'Made with care' }),
    el('div', { class: 'about-credits__txt', text: 'Designed and built by a small group of people who really, really hate copy-pasting events between calendars.' }),
    el('div', { class: 'about-credits__list' },
      el('div', null, el('b', { text: 'Design' }), ' · Zync Master team'),
      el('div', null, el('b', { text: 'Engineering' }), ' · Zync Master team'),
      el('div', null, el('b', { text: 'Icon' }), ' · custom · 24px line set')),
    el('div', { class: 'about-sys', text: 'Built on WebView2 · Chromium · © 2026' }),
  ));
}

// ---------------- Screen: Pairing ----------------
const pairing = { step: 0, name: "Daniel's MacBook", timer: null };

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
    const nameInput = el('input', { class: 'field-input', value: pairing.name, style: 'width:100%;height:36px;margin-top:4px' });
    nameInput.addEventListener('input', () => { pairing.name = nameInput.value; });
    root.append(el('div', { class: 'glass glass--card pair-card', style: 'margin-top:14px' },
      el('div', { style: 'width:56px;height:56px;margin:4px auto 0;border-radius:14px;display:grid;place-items:center;background:var(--azure-soft);color:var(--azure);border:1px solid var(--azure-edge)', html: icon('link', { size: 26, stroke: 1.6 }) }),
      el('div', { class: 'pair-title', text: 'Name this device' }),
      el('div', { class: 'pair-sub', text: 'So you can recognise it from other devices in your account.' }),
      nameInput,
      el('button', { class: 'btn btn--primary', style: 'align-self:stretch', onclick: () => { if (Bridge.available) Bridge.call('pair', null, 210000).catch(() => {}); pairing.step = 1; rerender(); } },
        el('span', { text: 'Continue' }), iconEl('arrowright', 14, 1.8))));
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
function runPairNow(id) {
  if (!Bridge.available || !id) return;
  announce('Sync started');
  Bridge.call('runPairNow', id)
    .then(() => loadPairs())
    .then(() => { if (state.view === 'calendar') rerenderInPlace(); })
    .catch(() => { announce('Sync failed.'); });
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

// ---------------- Settings tools (native shell) ----------------
function generateBasicTxt(btn) {
  if (!Bridge.available) return;
  const span = btn && btn.querySelector('span');
  const orig = span ? span.textContent : '';
  if (span) span.textContent = 'Saving…';
  Bridge.call('generateTxt')
    .then((r) => { if (span) span.textContent = (r && r.cancelled) ? 'Cancelled' : 'Saved'; })
    .catch(() => { if (span) span.textContent = 'Failed'; })
    .finally(() => { if (span) setTimeout(() => { span.textContent = orig || 'Generate .txt'; }, 1800); });
}

function unlinkAccount() {
  if (!Bridge.available) return;
  const run = () => {
    const accounts = live.accounts || [];
    const target = accounts.find((x) => x.isDefault) || accounts[0];
    if (!target) { announce('No connected account to unlink.'); return; }
    Bridge.call('unlinkAccount', target.accountRef)
      .then(() => { live.accounts = null; return loadPairs(); })
      .then(() => { announce('Account unlinked.'); if (state.view === 'config') rerender(); })
      .catch(() => { announce('Unlink failed.'); });
  };
  if (live.accounts === null) loadAccounts().then(run); else run();
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
  if (!s.paired) state.sync = 'unpaired';
  else if (s.status === 'Error') state.sync = 'error';
  else if (s.status === 'Offline') state.sync = 'offline';
  else if (s.status === 'Paused') state.sync = 'paused';
  else if (s.status === 'Syncing') state.sync = 'syncing';
  else state.sync = 'ok';
  const tag = $('#pausedTag'); if (tag) tag.hidden = state.sync !== 'paused';
  rerender();
}

// ---------------- Bottom nav ----------------
const NAV = [
  { id: 'home',    label: 'Home',     icon: 'home' },
  { id: 'config',  label: 'Settings', icon: 'settings' },
  { id: 'pairing', label: 'Pair',     icon: 'link' },
];
// Map sub-routes back to a parent tab so the indicator follows the user.
const TAB_MAP = { home: 'home', calendar: 'home', 'add-pair': 'home', 'add-calendar': 'home', config: 'config', about: 'config', pairing: 'pairing' };

// The visible tabs. "Pair" is device self-pairing — meaningless in the browser panel — so it
// is dropped in web mode. The pairing screen is gated separately in navigate().
function navItems() {
  return Bridge.webPanel ? NAV.filter((i) => i.id !== 'pairing') : NAV;
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
  const nav = $('#navbar');
  if (nav) nav.hidden = false;

  switch (state.view) {
    case 'home': renderHome(root); break;
    case 'calendar': renderCalendar(root); break;
    case 'add-pair': renderAddPair(root); break;
    case 'add-calendar': renderAddCalendar(root); break;
    case 'config': renderConfig(root); break;
    case 'about': renderAbout(root); break;
    case 'pairing': renderPairing(root); break;
    default: renderHome(root);
  }
  // retrigger the staggered card entrance
  void root.offsetWidth;
  root.classList.add('enter');
  renderNav();
}
// rerenderInPlace — update progress-driven views during the syncing tick without
// replaying the entrance animation (so cards don't flicker each frame).
function rerenderInPlace() {
  if (state.view !== 'calendar' && state.view !== 'home') return;
  const root = $('#view');
  if (!root) return;
  root.replaceChildren();
  if (state.view === 'calendar') renderCalendar(root); else renderHome(root);
  renderNav();
}

function navigate(view) {
  // Device self-pairing has no meaning in the browser panel; bounce it to Settings.
  if (view === 'pairing' && Bridge.webPanel) view = 'config';
  state.view = view;
  rerender();
}

function togglePair(id) {
  if (openPairs.has(id)) openPairs.delete(id); else openPairs.add(id);
  rerender();
}

// ---------------- Config push to host ----------------
function pushConfig() {
  if (!Bridge.available) return;
  const cfg = {
    deviceName: settings.deviceName,
    syncWindowDays: settings.windowDays,
    intervalMinutes: settings.interval,
    autoSync: settings.autoSync,
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
  // Hard cap so the splash always clears, even with no bridge or a slow host.
  setTimeout(dismissLaunch, reduce ? 600 : 1400);
}

// ---------------- Boot ----------------
async function boot() {
  hydrateIcons();
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
    // Dismiss the splash shortly after the first status settles (with a small floor) so the
    // app feels instant when the host responds quickly, instead of a fixed long hold.
    Bridge.call('getStatus')
      .then((s) => { applyNativeStatus(s); if (Bridge.webPanel) { webAuth.resolved = true; rerender(); } })
      .catch(() => {
        // In the web panel a failed getStatus (typically 401) is the unauthenticated state:
        // resolve the gate so the sign-in screen shows instead of an empty dashboard.
        if (Bridge.webPanel) { webAuth.resolved = true; webAuth.signedIn = false; rerender(); }
      })
      .finally(() => setTimeout(dismissLaunch, 450));
  }

  rerender();
  playLaunch();
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
else boot();
