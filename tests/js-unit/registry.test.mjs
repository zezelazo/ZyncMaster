// registry.test.mjs — el registry de vistas que alimenta router, sidebar y palette.
// Run: node tests/js-unit/registry.test.mjs

import assert from 'node:assert/strict';
import { createRegistry } from '../../ui/js/core/registry.js';

let passed = 0;
function test(name, fn) {
  try { fn(); passed += 1; console.log(`  ok  - ${name}`); }
  catch (err) { console.error(`  FAIL - ${name}\n        ${err.message}`); process.exitCode = 1; }
}

const noop = () => {};

test('register + get round-trip', () => {
  const r = createRegistry();
  r.register('home', { render: noop });
  assert.equal(r.get('home').id, 'home');
  assert.equal(r.has('home'), true);
  assert.equal(r.get('nope'), null);
});

test('rejects duplicate ids and missing render', () => {
  const r = createRegistry();
  r.register('a', { render: noop });
  assert.throws(() => r.register('a', { render: noop }), /already registered/);
  assert.throws(() => r.register('b', {}), /render/);
  assert.throws(() => r.register('', { render: noop }), /id/);
});

test('soft flag defaults to false', () => {
  const r = createRegistry();
  r.register('a', { render: noop });
  r.register('b', { render: noop, soft: true });
  assert.equal(r.get('a').soft, false);
  assert.equal(r.get('b').soft, true);
});

test('parent maps sub-routes to a sidebar entry', () => {
  const r = createRegistry();
  r.register('calendar', { render: noop, nav: { label: 'Calendar', icon: 'calendar', order: 2, section: 'modules' } });
  r.register('add-pair', { render: noop, parent: 'calendar' });
  assert.equal(r.get('add-pair').parent, 'calendar');
  assert.equal(r.activeNavId('add-pair'), 'calendar');
  assert.equal(r.activeNavId('calendar'), 'calendar');
  assert.equal(r.activeNavId('missing'), null);
});

test('activeNavId walks the parent CHAIN to the nearest nav ancestor', () => {
  // IA redesign (fix #7): the day view owns the "Calendar" nav; the pairs/accounts config screen
  // ('calendar') is a no-nav sub-route of it; add-pair is a sub-route of THAT. A two-hop chain
  // (add-pair -> calendar -> calendar-day) must still resolve to the calendar-day sidebar entry.
  const r = createRegistry();
  r.register('calendar-day', { render: noop, nav: { label: 'Calendar', icon: 'calendar', order: 2, section: 'modules' } });
  r.register('calendar', { render: noop, parent: 'calendar-day' });        // no nav of its own
  r.register('add-pair', { render: noop, parent: 'calendar' });
  assert.equal(r.activeNavId('calendar-day'), 'calendar-day');
  assert.equal(r.activeNavId('calendar'), 'calendar-day');                 // one hop up, skips no-nav node
  assert.equal(r.activeNavId('add-pair'), 'calendar-day');                 // two hops up
});

test('activeNavId terminates on a parent cycle without looping', () => {
  const r = createRegistry();
  r.register('a', { render: noop, parent: 'b' });
  r.register('b', { render: noop, parent: 'a' });   // neither has a nav; mutual parents
  assert.equal(r.activeNavId('a'), null);
});

test('navItems: only nav entries, sorted by order, hidden() respected', () => {
  const r = createRegistry();
  r.register('settings', { render: noop, nav: { label: 'Settings', icon: 'settings', order: 100, section: 'system' } });
  r.register('home', { render: noop, nav: { label: 'Home', icon: 'home', order: 1, section: 'modules' } });
  r.register('hidden', { render: noop, nav: { label: 'X', icon: 'home', order: 5, section: 'modules', hidden: () => true } });
  r.register('no-nav', { render: noop });
  assert.deepEqual(r.navItems().map((v) => v.id), ['home', 'settings']);
});

console.log(`\n${passed} passed${process.exitCode ? ' (with failures)' : ''}`);
