// calendar-day-status.test.mjs — freezes the read-only Status popup contract of the calendar-day
// view (CalendarIA-t2 review), including the stuck-spinner / stale-counts regression.
//
// The bug (high): the popup's Force-sync onclick repainted via an immediate refreshStatusRows() plus
// an INDEPENDENT loadPairs().then(refreshStatusRows). That loadPairs is a separate listPairs fetch
// that resolves long BEFORE the actual mirror (runPairNow does a full sync), so the deferred refresh
// ran while live.syncing still held the id — the row kept the "Syncing…" spinner and stale counts
// until the user closed and reopened the popup. A view rerender can never fix it either: it repaints
// #view, but the modal body lives in document.body OUTSIDE #view, and the active view is
// 'calendar-day' (not 'calendar') while the popup is open.
//
// The fix drives the popup off the REAL sync lifecycle: app.js publishes every beginPairSync /
// endPairSync transition to subscribeSyncState listeners; the popup subscribes on open and
// unsubscribes on close, so the spinner flips ON at begin and OFF (with fresh counts, since
// runPairNow awaits loadPairs before endPairSync) at end — regardless of the active view.
//
// This is a real integration test of the SHIPPED module: it imports the actual view under a minimal
// DOM shim and exercises the genuine statusPairRow / fillStatusBody / openStatusPopup code paths,
// matching the repo's zero-JS-dependency policy (node's built-in test runner only).
//
// Run:  node --test tests/js-unit/calendar-day-status.test.mjs

import test from 'node:test';
import assert from 'node:assert/strict';

// ---- minimal DOM shim ---------------------------------------------------------------------
// The status path builds real nodes through the injected `el` helper (createElement + append +
// replaceChildren) and reads back className / textContent / disabled / title / dataset for the
// assertions. We model exactly those, plus a deep className/id walk for lookups.

class TextNode {
  constructor(value) { this.value = value; }
  get text() { return this.value; }
}

class FakeNode {
  constructor(tag) {
    this.tagName = tag;
    this.className = '';
    this.textContent = '';
    this._innerHTML = '';
    this.dataset = {};
    this.attributes = {};
    this.style = {};
    this.children = [];
    this.listeners = {};
    this.disabled = false;
  }

  // Minimal innerHTML: store the raw string AND parse out every start tag that carries an id/class
  // into a flat list of child nodes, so the view's `head.querySelector('#calDay…')` wiring (which
  // runs right after `head.innerHTML = …`) finds real nodes. We only need id/class/aria-label lookups
  // here — not a full HTML tree — so a flat scan of start tags is sufficient and keeps the shim tiny.
  set innerHTML(v) {
    this._innerHTML = v;
    this.children = [];
    const tagRe = /<([a-zA-Z][\w-]*)((?:\s+[\w-]+(?:="[^"]*")?)*)\s*\/?>/g;
    let m;
    while ((m = tagRe.exec(v)) !== null) {
      const node = new FakeNode(m[1]);
      const attrRe = /([\w-]+)="([^"]*)"/g;
      let a;
      while ((a = attrRe.exec(m[2])) !== null) {
        if (a[1] === 'class') node.className = a[2];
        else node.setAttribute(a[1], a[2]);
      }
      this.children.push(node);
    }
  }
  get innerHTML() { return this._innerHTML; }

  setAttribute(k, v) {
    this.attributes[k] = v;
    if (k === 'disabled') this.disabled = v === '' || v === true;
  }

  get title() { return this.attributes.title; }
  set title(v) { this.attributes.title = v; }
  get id() { return this.attributes.id; }
  set id(v) { this.attributes.id = v; }

  addEventListener(type, fn) { (this.listeners[type] ||= []).push(fn); }

  // The view wires some handlers via the DOM `.onclick = fn` property (not addEventListener), so the
  // shim must invoke both on click(). onchange is here for completeness (replicate/new-event paths).
  click() {
    (this.listeners.click || []).forEach((fn) => fn({}));
    if (typeof this.onclick === 'function') this.onclick({});
  }

  append(...kids) {
    for (const k of kids) {
      this.children.push(typeof k === 'string' || typeof k === 'number'
        ? new TextNode(String(k)) : k);
    }
  }

  appendChild(child) { this.append(child); return child; }
  replaceChildren(...kids) { this.children = []; this.append(...kids); }

  // Aggregate text of the whole subtree (own textContent wins, else descendants joined).
  get text() {
    if (this.textContent) return this.textContent;
    return this.children.map((c) => (c instanceof TextNode ? c.value : c.text)).join('');
  }

  hasClass(c) { return this.className.split(/\s+/).includes(c); }

  // Minimal classList over the className string (the shipped status-icon toggles a 'warn' class).
  get classList() {
    const self = this;
    const parts = () => self.className.split(/\s+/).filter(Boolean);
    return {
      add: (c) => { if (!self.hasClass(c)) self.className = [...parts(), c].join(' '); },
      remove: (c) => { self.className = parts().filter((x) => x !== c).join(' '); },
      contains: (c) => self.hasClass(c),
      toggle: (c, force) => {
        const want = force === undefined ? !self.hasClass(c) : !!force;
        self.className = want
          ? (self.hasClass(c) ? self.className : [...parts(), c].join(' '))
          : parts().filter((x) => x !== c).join(' ');
        return want;
      },
    };
  }

  // Deep collect every descendant whose className includes `cls`.
  collectByClass(cls, out = []) {
    for (const c of this.children) {
      if (c instanceof TextNode) continue;
      if (c.hasClass(cls)) out.push(c);
      c.collectByClass(cls, out);
    }
    return out;
  }

  // Deep find first descendant matching '#id' or '.class' (only the forms the view uses).
  querySelector(sel) {
    for (const c of this.children) {
      if (c instanceof TextNode) continue;
      if (sel[0] === '#' && c.attributes.id === sel.slice(1)) return c;
      if (sel[0] === '.' && c.hasClass(sel.slice(1))) return c;
      const found = c.querySelector(sel);
      if (found) return found;
    }
    return null;
  }
}

function installDomShim() {
  const target = new EventTarget();
  globalThis.document = {
    addEventListener: (...a) => target.addEventListener(...a),
    removeEventListener: (...a) => target.removeEventListener(...a),
    dispatchEvent: (ev) => target.dispatchEvent(ev),
    createElement: (tag) => new FakeNode(tag),
  };
  globalThis.Event = Event;
}

// The real `el(tag, props, ...children)` from app.js, transcribed against the shim node so the test
// drives the SAME element construction the shipped code expects.
function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v == null) continue;
      if (k === 'class') node.className = v;
      else if (k === 'text') node.textContent = v;
      else if (k === 'html') node.innerHTML = v;
      else if (k === 'style') node.setAttribute('style', v);
      else if (k.startsWith('on') && typeof v === 'function') node.addEventListener(k.slice(2), v);
      else if (k === 'dataset') Object.assign(node.dataset, v);
      else if (v === true) node.setAttribute(k, '');
      else if (v === false) { /* skip */ }
      else node.setAttribute(k, v);
    }
  }
  for (const c of children.flat()) {
    if (c == null || c === false) continue;
    node.append(typeof c === 'string' || typeof c === 'number' ? String(c) : c);
  }
  return node;
}

// ---- harness ------------------------------------------------------------------------------
// Builds the ctx the view needs, renders it into a root, opens the Status popup the REAL way
// (clicking #calDayStatus), and exposes the captured modal body + the run completion handle.
async function loadView() {
  const mod = await import(`../../ui/js/views/calendar-day.js?case=${Math.random()}`);
  return mod.registerCalendarDayView;
}

function makeHarness(pairs, opts = {}) {
  installDomShim();
  const live = { pairs, syncing: new Set(), attempts: {} };
  const calls = { runPairNow: [], syncPairRemote: [] };
  const listeners = new Set();
  let modal = null;
  let lastRun = null;

  const pairViewModel = (p) => ({
    id: p.id,
    src: { svc: 'Outlook', acct: p.srcAcct || 'job1' },
    dst: { svc: 'Outlook', acct: p.dstAcct || 'job2' },
    inFlight: live.syncing.has(p.id),
    nextSync: (p.intervalMin || 10) * 60,
    comRemote: !!p.comRemote,
    comOffline: !!p.comOffline,
    comUnclaimed: !!p.comUnclaimed,
    pinnedDeviceName: p.pinnedDeviceName || '',
  });

  const notify = () => { for (const fn of [...listeners]) try { fn(); } catch (_) { /* isolate */ } };

  const ctx = {
    Bridge: { available: opts.bridgeAvailable !== false, call: () => Promise.resolve(null) },
    state: { view: 'calendar-day' },
    navigate: () => {},
    rerenderInPlace: () => {},
    announce: () => {},
    registry: { register() {}, activeNavId: () => 'calendar' },
    live,
    openModal: (args) => {
      modal = { body: args.body, onClose: args.onClose };
      return { close() {} };
    },
    el,
    iconEl: () => el('span'),
    icon: () => '<svg></svg>',
    pairViewModel,
    fmtMMSS: (s) => `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`,
    subscribeSyncState: (fn) => { listeners.add(fn); return () => listeners.delete(fn); },
    // Lifecycle mirroring app.js: begin flips spinner on + notifies; complete() refreshes live.pairs
    // (loadPairs) BEFORE end flips spinner off + notifies — the real ordering that makes counts fresh.
    runPairNow: (id) => {
      calls.runPairNow.push(id);
      live.syncing.add(id); notify();            // beginPairSync
      lastRun = {
        complete(newResult) {
          if (newResult) {
            const p = live.pairs.find((x) => x.id === id);
            if (p) { p.lastResult = newResult.lastResult; p.lastRunUtc = newResult.lastRunUtc; }
          }
          live.syncing.delete(id); notify();     // endPairSync (after loadPairs)
        },
      };
      return lastRun;
    },
    syncPairRemote: (raw) => { calls.syncPairRemote.push(raw.id); },
  };

  const register = registerRef;
  return {
    ctx, live, calls,
    listenerCount: () => listeners.size,
    open() {
      let renderFn = null;
      ctx.registry = {
        register: (id, def) => { if (id === 'calendar-day') renderFn = def.render; },
        activeNavId: () => 'calendar',
      };
      register(ctx);
      const root = new FakeNode('div');
      renderFn(root);
      root.querySelector('#calDayStatus').click();
      return modal;
    },
    lastRun: () => lastRun,
  };
}

let registerRef = null;
test.before(async () => { registerRef = await loadView(); });

test('fillStatusBody renders one row per ACTIVE pair and the read-only hint', () => {
  const h = makeHarness([
    { id: 'p1', state: 'active', lastResult: { created: 2, updated: 1, skipped: 3 }, lastRunUtc: '2026-06-13T10:00:00Z' },
    { id: 'p2', state: 'paused' },                                   // excluded — not active
    { id: 'p3', state: 'active', lastResult: { created: 0, updated: 0, skipped: 0 } },
  ]);
  const modal = h.open();
  const rows = modal.body.collectByClass('calday-status-row');
  assert.equal(rows.length, 2, 'only the two ACTIVE pairs get a row');
  const hints = modal.body.collectByClass('calday-hint');
  assert.ok(hints.length >= 1 && /Read-only/.test(hints[0].text), 'shows the read-only overview hint');
});

test('fillStatusBody shows the empty message when there are no active pairs', () => {
  const h = makeHarness([{ id: 'p2', state: 'paused' }]);
  const modal = h.open();
  const empty = modal.body.collectByClass('calday-empty');
  assert.equal(empty.length, 1);
  assert.match(empty[0].text, /No active sync pairs/);
});

test('fillStatusBody shows the non-bridge message when the bridge is unavailable', () => {
  const h = makeHarness([{ id: 'p1', state: 'active' }], { bridgeAvailable: false });
  const modal = h.open();
  const empty = modal.body.collectByClass('calday-empty');
  assert.match(empty[0].text, /desktop app/);
});

test('statusPairRow form: New events / Synced / Last-run lines (created+updated+skipped; never)', () => {
  const h = makeHarness([
    { id: 'p1', state: 'active', lastResult: { created: 2, updated: 1, skipped: 3 }, lastRunUtc: '2026-06-13T10:00:00Z' },
    { id: 'p2', state: 'active' },                                   // never run -> 0 / never
  ]);
  const modal = h.open();
  const [r1, r2] = modal.body.collectByClass('calday-status-row');
  // Form value cells, in order: Source, Destination, New events, Synced, Last run, Next.
  const v1 = r1.collectByClass('calday-status-line-val').map((s) => s.text);
  assert.equal(v1[2], '2', 'New events = created');
  assert.equal(v1[3], '6', 'Synced = 2+1+3');
  assert.notEqual(v1[4], 'never', 'last run is formatted, not "never"');
  const v2 = r2.collectByClass('calday-status-line-val').map((s) => s.text);
  assert.equal(v2[2], '0', 'no lastResult -> 0 new');
  assert.equal(v2[3], '0', 'no lastResult -> 0 synced');
  assert.equal(v2[4], 'never', 'no lastRunUtc -> never');
});

test('statusPairRow: Next is an em-dash while a run is in flight, mm:ss otherwise', () => {
  const h = makeHarness([{ id: 'p1', state: 'active', intervalMin: 5 }]);
  const modal = h.open();
  // Idle -> mm:ss for a 5-min interval (300s). Next is the 6th form value cell (index 5).
  let next = modal.body.collectByClass('calday-status-line-val')[5].text;
  assert.equal(next, '05:00', 'idle Next shows mm:ss');
  // The single Force-sync starts the active pair -> beginPairSync flips inFlight; the observer repaints
  // the body in place, so Next becomes an em-dash (no meaningful countdown mid-sync) WITHOUT reopening.
  modal.body.collectByClass('calday-status-forceall')[0].click();
  next = modal.body.collectByClass('calday-status-line-val')[5].text;
  assert.equal(next, '—', 'busy Next shows an em-dash');
});

test('Force-sync skips a pair whose origin device is offline', () => {
  const h = makeHarness([{ id: 'p1', state: 'active', comRemote: true, comOffline: true, pinnedDeviceName: 'Laptop' }]);
  const modal = h.open();
  modal.body.collectByClass('calday-status-forceall')[0].click();
  assert.deepEqual(h.calls.syncPairRemote, [], 'an offline origin is not signalled');
  assert.deepEqual(h.calls.runPairNow, [], 'and not run locally');
});

test('Force-sync skips a pair whose sync is unclaimed', () => {
  const h = makeHarness([{ id: 'p1', state: 'active', comRemote: false, comUnclaimed: true }]);
  const modal = h.open();
  modal.body.collectByClass('calday-status-forceall')[0].click();
  assert.deepEqual(h.calls.runPairNow, [], 'an unclaimed sync is not run');
  assert.deepEqual(h.calls.syncPairRemote, []);
});

test('Force-sync routes to syncPairRemote when the source is on another device', () => {
  const h = makeHarness([{ id: 'p1', state: 'active', comRemote: true, pinnedDeviceName: 'Laptop' }]);
  const modal = h.open();
  modal.body.collectByClass('calday-status-forceall')[0].click();
  assert.deepEqual(h.calls.syncPairRemote, ['p1'], 'comRemote -> syncPairRemote');
  assert.deepEqual(h.calls.runPairNow, [], 'comRemote must NOT runPairNow locally');
});

test('Force-sync routes to runPairNow for a local pair', () => {
  const h = makeHarness([{ id: 'p1', state: 'active' }]);
  const modal = h.open();
  modal.body.collectByClass('calday-status-forceall')[0].click();
  assert.deepEqual(h.calls.runPairNow, ['p1']);
  assert.deepEqual(h.calls.syncPairRemote, []);
});

test('STUCK-SPINNER FIX: spinner clears + counts update after the run resolves (not on a loadPairs race)', () => {
  const h = makeHarness([{ id: 'p1', state: 'active', lastResult: { created: 0, updated: 0, skipped: 0 } }]);
  const modal = h.open();

  // Before the run: the single Force-sync is labelled "Force-sync", enabled.
  let btn = modal.body.collectByClass('calday-status-forceall')[0];
  assert.match(btn.text, /Force-sync/);
  assert.equal(btn.disabled, false);

  // Click -> beginPairSync flips spinner ON and notifies; the observer repaints the body in place.
  btn.click();

  btn = modal.body.collectByClass('calday-status-forceall')[0];
  assert.equal(btn.disabled, true, 'the button is disabled mid-run');
  assert.match(btn.text, /Syncing/, 'shows the Syncing… label mid-run');
  assert.equal(modal.body.collectByClass('spinner').length, 1, 'spinner element present mid-run');

  // The run completes: loadPairs refreshes live.pairs with new counts, THEN endPairSync notifies.
  h.lastRun().complete({ lastResult: { created: 4, updated: 1, skipped: 2 }, lastRunUtc: '2026-06-13T12:00:00Z' });

  btn = modal.body.collectByClass('calday-status-forceall')[0];
  assert.equal(btn.disabled, false, 'spinner CLEARS after the run resolves');
  assert.match(btn.text, /Force-sync/, 'label returns to Force-sync (no stuck spinner)');
  assert.equal(modal.body.collectByClass('spinner').length, 0, 'no spinner after completion');

  // Counts come from the per-pair form: New events (index 2), Synced (index 3).
  const vals = modal.body.collectByClass('calday-status-line-val').map((s) => s.text);
  assert.equal(vals[2], '4', 'New events updates to created=4 after the run');
  assert.equal(vals[3], '7', 'Synced updates to 4+1+2 after the run (no stale 0)');
});

test('closing the popup unsubscribes the sync-state observer (no stale repaint of a detached node)', () => {
  const h = makeHarness([{ id: 'p1', state: 'active' }]);
  const modal = h.open();
  assert.equal(h.listenerCount(), 1, 'opening the popup subscribes one observer');
  modal.onClose();                                 // user closes via X / overlay / Escape
  assert.equal(h.listenerCount(), 0, 'closing the popup unsubscribes the observer');
});
