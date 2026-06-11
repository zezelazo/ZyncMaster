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

test('navItems: only nav entries, sorted by order, hidden() respected', () => {
  const r = createRegistry();
  r.register('settings', { render: noop, nav: { label: 'Settings', icon: 'settings', order: 100, section: 'system' } });
  r.register('home', { render: noop, nav: { label: 'Home', icon: 'home', order: 1, section: 'modules' } });
  r.register('hidden', { render: noop, nav: { label: 'X', icon: 'home', order: 5, section: 'modules', hidden: () => true } });
  r.register('no-nav', { render: noop });
  assert.deepEqual(r.navItems().map((v) => v.id), ['home', 'settings']);
});

console.log(`\n${passed} passed${process.exitCode ? ' (with failures)' : ''}`);
