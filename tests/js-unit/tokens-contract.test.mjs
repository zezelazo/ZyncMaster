// tokens-contract.test.mjs — congela el contrato de design tokens (spec ui-redesign §5).
// La web Angular replica la elevación border-light y los acentos desde EXACTAMENTE estas
// variables; cambiarlas es un cambio de contrato explícito. Run:
//   node tests/js-unit/tokens-contract.test.mjs

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const css = (rel) => {
  try { return readFileSync(new URL(`../../ui/css/${rel}`, import.meta.url), 'utf8'); }
  catch (_) { return null; }
};
const tokens = css('tokens.css');

let passed = 0;
function test(name, fn) {
  try { fn(); passed += 1; console.log(`  ok  - ${name}`); }
  catch (err) { console.error(`  FAIL - ${name}\n        ${err.message}`); process.exitCode = 1; }
}

// Secciones por tema para poder asertar dark y light por separado.
const darkBlock = tokens.slice(tokens.indexOf('[data-theme="dark"]'), tokens.indexOf('[data-theme="light"]'));
const lightBlock = tokens.slice(tokens.indexOf('[data-theme="light"]'));

// ---- nuevos tokens del contrato (deben existir en AMBOS temas) ------------------------
for (const name of ['--cyan:', '--cyan-soft:', '--cyan-edge:', '--aqua:', '--aqua-soft:', '--aqua-edge:', '--border-1:', '--border-2:']) {
  test(`dark defines ${name}`, () => assert.ok(darkBlock.includes(name), `${name} missing in dark theme`));
  test(`light defines ${name}`, () => assert.ok(lightBlock.includes(name), `${name} missing in light theme`));
}

// ---- fix AA de --ink-3 (spec §5 / §8): alpha >= 0.55 dark, >= 0.60 light ---------------
test('--ink-3 dark alpha >= 0.55', () => {
  const m = darkBlock.match(/--ink-3:\s*rgba\([^)]*,\s*(0?\.\d+)\)/);
  assert.ok(m, '--ink-3 rgba not found in dark theme');
  assert.ok(parseFloat(m[1]) >= 0.55, `dark --ink-3 alpha ${m[1]} < 0.55`);
});
test('--ink-3 light alpha >= 0.60', () => {
  const m = lightBlock.match(/--ink-3:\s*rgba\([^)]*,\s*(0?\.\d+)\)/);
  assert.ok(m, '--ink-3 rgba not found in light theme');
  assert.ok(parseFloat(m[1]) >= 0.60, `light --ink-3 alpha ${m[1]} < 0.60`);
});

// ---- aurora muere (spec §5) ------------------------------------------------------------
test('no --aurora-* tokens remain', () => assert.ok(!tokens.includes('--aurora-'), '--aurora-* still defined'));
test('aurora.css no longer exists', () => assert.equal(css('aurora.css'), null, 'ui/css/aurora.css still exists'));

// ---- surface-solid dark = bg-1 (escala de elevación plana del mock) --------------------
test('dark --surface-solid is var(--bg-1)', () =>
  assert.ok(/--surface-solid:\s*var\(--bg-1\)/.test(darkBlock), 'dark --surface-solid must be var(--bg-1)'));

// ---- manifest público (web-ui §4: vive como comentario-sección dentro de tokens.css) ---
test('public token manifest comment present and lists the new tokens', () => {
  const start = tokens.indexOf('PUBLIC TOKEN MANIFEST');
  assert.ok(start >= 0, 'PUBLIC TOKEN MANIFEST comment-section missing');
  const manifest = tokens.slice(start, tokens.indexOf('*/', start));
  for (const name of ['--cyan', '--aqua', '--border-1', '--border-2']) {
    assert.ok(manifest.includes(name), `${name} missing from the manifest`);
  }
});

console.log(`\n${passed} passed${process.exitCode ? ' (with failures)' : ''}`);
