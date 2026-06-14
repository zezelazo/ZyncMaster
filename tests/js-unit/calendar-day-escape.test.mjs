// calendar-day-escape.test.mjs — freezes the Escape-key contract of the calendar-day view's
// global keydown handler against the double-dismiss regression (CalendarIA-t1 review, high).
//
// The bug: onCalDayKeydown (ui/js/views/calendar-day.js) is a BUBBLE-phase document handler that
// only guarded on state.view === 'calendar-day'. The command palette (Ctrl+K) and the modal both
// register their Escape handlers in the CAPTURE phase and call e.preventDefault() WITHOUT
// stopPropagation(). While such an overlay was open over the calendar-day view, a single Escape
// fired BOTH handlers: the overlay closed AND the calendar simultaneously closed its detail panel
// — an unintended two-action dismiss for one keypress.
//
// The fix adds `if (e.defaultPrevented) return;` so an Escape already consumed by an overlay's
// capture handler never reaches the calendar's panel-close branch.
//
// Info-arch redesign (fix #7): this view is now the TOP-LEVEL "Calendar" sidebar destination, so a bare
// Escape with no open panel is a NO-OP — there is nothing above it to navigate "back" to. Escape
// still closes an open detail panel first. The tests below freeze exactly that contract.
//
// This is a real integration test of the SHIPPED handler: it imports the actual view module under
// a minimal EventTarget-based DOM shim and dispatches genuine capture→bubble Escape events. No DOM
// framework — just node's built-in EventTarget, matching the repo's zero-JS-dependency policy.
//
// Run:  node --test tests/js-unit/calendar-day-escape.test.mjs

import test from 'node:test';
import assert from 'node:assert/strict';

// ---- minimal DOM shim ---------------------------------------------------------------------
// registerCalendarDayView only needs document.addEventListener/dispatchEvent (for the global
// keydown wiring) and document.createElement (never reached on the Escape path). We provide a
// real EventTarget so capture/bubble ordering and defaultPrevented behave exactly like a browser.

class FakeKeyboardEvent extends Event {
  constructor(type, init = {}) {
    // cancelable:true so preventDefault() actually flips defaultPrevented, like a real keydown.
    super(type, { bubbles: true, cancelable: true });
    this.key = init.key;
  }
}

function installDomShim() {
  const target = new EventTarget();
  const doc = {
    addEventListener: (...a) => target.addEventListener(...a),
    removeEventListener: (...a) => target.removeEventListener(...a),
    dispatchEvent: (ev) => target.dispatchEvent(ev),
    // createElement is only used by renderCalendarDay, which the Escape path never calls.
    createElement: () => ({ className: '', setAttribute() {}, appendChild() {}, querySelector: () => null }),
  };
  globalThis.document = doc;
  globalThis.Event = Event;
  return { target, doc };
}

// A registry stub that records the parent id and the navigate target so the test can assert
// whether goBack() ran. activeNavId returns the parent the real view registers ('calendar').
function makeCtx() {
  const calls = { navigate: [], rerenders: 0 };
  const state = { view: 'calendar-day' };
  const ctx = {
    Bridge: { available: true, call: () => Promise.resolve(null) },
    state,
    navigate: (id) => calls.navigate.push(id),
    rerenderInPlace: () => { calls.rerenders += 1; },
    announce: () => {},
    registry: {
      register() {},
      activeNavId: () => 'calendar',
    },
  };
  return { ctx, calls, state };
}

// Loads the REAL view module under the shim and returns its handle. A cache-busting query keeps
// each test isolated (the module registers a document-level keydown listener at import time).
async function loadView() {
  installDomShim();
  const mod = await import(`../../ui/js/views/calendar-day.js?case=${Math.random()}`);
  return mod.registerCalendarDayView;
}

// Simulates an overlay's CAPTURE-phase Escape handler: preventDefault but NOT stopPropagation,
// exactly like ui/js/palette.js onKey and app.js openModal onKey. In a real browser the capture
// phase always precedes the bubble phase regardless of registration order; node's EventTarget has
// no propagation phases and simply fires listeners in registration order, so we register this
// overlay handler BEFORE the view's bubble handler to reproduce the browser's capture-first order
// (and thus the defaultPrevented flag the bubble handler must observe).
function addOverlayCaptureHandler(doc) {
  doc.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') e.preventDefault(); // no stopPropagation — the real overlays do the same
  }, true);
}

test('Escape consumed by an open overlay does NOT also close the panel or navigate back', async () => {
  const { ctx, calls } = makeCtx();
  const register = await loadView(); // (re)installs the shim
  const doc = globalThis.document;

  // An overlay (palette/modal) is open over the view: its capture handler runs FIRST, before the
  // view registers its bubble handler — matching the browser's capture-before-bubble ordering.
  addOverlayCaptureHandler(doc);
  register(ctx); // now wires onCalDayKeydown (bubble) — it runs AFTER the overlay handler

  doc.dispatchEvent(new FakeKeyboardEvent('keydown', { key: 'Escape' }));

  // The overlay already handled it (defaultPrevented true) → the calendar handler must bail:
  // no navigate and no rerender (panel close) triggered by THIS keypress.
  assert.deepEqual(calls.navigate, [], 'navigation must not run for an overlay-consumed Escape');
  assert.equal(calls.rerenders, 0, 'panel-close rerender must not run for an overlay-consumed Escape');
});

test('Escape with NO overlay and no open panel is a no-op (top-level view: nothing to go back to)', async () => {
  const { ctx, calls } = makeCtx();
  const register = await loadView();
  register(ctx);

  // No overlay/capture handler this time: a plain Escape with no open panel does nothing — this is
  // the top-level "Calendar" destination now, so Escape never navigates the user away from it.
  globalThis.document.dispatchEvent(new FakeKeyboardEvent('keydown', { key: 'Escape' }));

  assert.deepEqual(calls.navigate, [], 'a bare Escape on the top-level calendar view must not navigate');
  assert.equal(calls.rerenders, 0, 'a bare Escape with no open panel must not rerender');
});

test('Escape on a DIFFERENT view is ignored (the handler never leaks onto other screens)', async () => {
  const { ctx, calls, state } = makeCtx();
  const register = await loadView();
  register(ctx);

  state.view = 'home'; // user left the calendar-day view
  globalThis.document.dispatchEvent(new FakeKeyboardEvent('keydown', { key: 'Escape' }));

  assert.deepEqual(calls.navigate, [], 'Escape on another view must not navigate the calendar');
  assert.equal(calls.rerenders, 0, 'Escape on another view must not rerender the calendar');
});
