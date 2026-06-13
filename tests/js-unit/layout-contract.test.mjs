// layout-contract.test.mjs — congela la cadena de alto "single-scroller" que arregla el
// scroll de toda la ventana y el dead-space del sidebar (diagnóstico 2026-06-13, área C).
//
// El bug original: .win__body no tenía NINGUNA regla, así que era display:block / height:auto.
// Eso volvía inerte .shell{flex:1;min-height:0} (su padre no era flex), .view nunca recibía
// alto acotado, su overflow-y:auto nunca enganchaba, y el documento entero scrolleaba. Estas
// aserciones congelan los eslabones de la cadena para que un refactor que borre cualquiera de
// ellos rompa CI en vez de regresar en silencio (el mismo patrón que tokens-contract.test.mjs).
//
// La cadena (DOM en ui/index.html):
//   .stage → .win → .win__body → .shell → .main → .view (único scroller)
//
// Run:  node --test tests/js-unit/layout-contract.test.mjs

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const css = (rel) => readFileSync(new URL(`../../ui/css/${rel}`, import.meta.url), 'utf8');
const shell = css('shell.css');
const layout = css('layout.css');

const esc = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

// Extrae el cuerpo `{ ... }` de la PRIMERA regla cuyo selector contiene `selectorNeedle` como
// TOKEN de selector (no como substring). El needle debe terminar el identificador (lo sigue un
// borde de selector: espacio, coma, `{` o fin) — así `.win__body` NO casa con `.win__body_x` y
// `.win` NO casa con `.window`. Localiza la primera `{` tras ese match y devuelve el interior con
// cierre balanceado, normalizado a minúsculas y sin espacios redundantes.
function ruleBody(cssText, selectorNeedle) {
  // El needle puede traer combinadores con espacios (".native-shell .win"); colapsa el
  // whitespace interno a `\s+` y exige un borde de selector justo después del último token.
  const pattern = esc(selectorNeedle).replace(/\\?\s+/g, '\\s+') + '(?=$|[\\s,{])';
  const m = new RegExp(pattern).exec(cssText);
  assert.ok(m, `selector token "${selectorNeedle}" not found`);
  const open = cssText.indexOf('{', m.index + m[0].length - 1);
  assert.notEqual(open, -1, `no opening brace after "${selectorNeedle}"`);
  let depth = 0;
  let close = -1;
  for (let i = open; i < cssText.length; i += 1) {
    const c = cssText[i];
    if (c === '{') depth += 1;
    else if (c === '}') { depth -= 1; if (depth === 0) { close = i; break; } }
  }
  assert.notEqual(close, -1, `unbalanced braces after "${selectorNeedle}"`);
  return cssText.slice(open + 1, close).toLowerCase().replace(/\s+/g, ' ').trim();
}

// Una declaración `prop: value` está presente si aparece tolerando el espaciado. El value se
// ancla por un delimitador de fin de declaración (`;`, `}`, espacio o fin de cadena) en vez de
// `\b`, porque valores como `100%` o `100dvh` terminan en un carácter no-word donde `\b` no casa.
function hasDecl(body, prop, value) {
  const re = new RegExp(`(?:^|;|{|\\s)${prop}\\s*:\\s*${value}\\s*(?:;|}|$)`, 'i');
  return re.test(body);
}

// ---- eslabón 1: .win__body es la columna flex de alto completo (antes no existía) ----------
test('shell.css: .win__body is a full-height flex column that hides its own overflow', () => {
  const body = ruleBody(shell, '.win__body');
  assert.ok(hasDecl(body, 'display', 'flex'), '.win__body must be display:flex');
  assert.ok(hasDecl(body, 'flex-direction', 'column'), '.win__body must be flex-direction:column');
  assert.ok(hasDecl(body, 'height', '100%'), '.win__body must be height:100%');
  assert.ok(hasDecl(body, 'overflow', 'hidden'), '.win__body must be overflow:hidden');
});

// ---- eslabón 2: .win (native + web) es columna flex que recorta su overflow -----------------
test('layout.css: shared .win block is a flex column with overflow hidden and 100dvh', () => {
  // El bloque es compartido: `.native-shell .win, :root[data-transport="web"] .win { ... }`.
  const body = ruleBody(layout, '.native-shell .win');
  assert.ok(hasDecl(body, 'display', 'flex'), '.win must be display:flex');
  assert.ok(hasDecl(body, 'flex-direction', 'column'), '.win must be flex-direction:column');
  assert.ok(hasDecl(body, 'overflow', 'hidden'), '.win must be overflow:hidden');
  assert.ok(hasDecl(body, 'height', '100dvh'), '.win must set height:100dvh');
});

test('layout.css: the web transport shares the .win height-chain block', () => {
  // El medium-finding exige que el panel web NO quede aislado: comparte el mismo selector.
  const between = layout.slice(layout.indexOf('.native-shell .win'), layout.indexOf('{', layout.indexOf('.native-shell .win')));
  assert.ok(/\[data-transport="web"\]\s+\.win/.test(between),
    'the .win rule must also target :root[data-transport="web"] .win (shared web/native chain)');
});

// ---- eslabón 3: .native-shell .stage da el alto del viewport como columna flex --------------
test('layout.css: .native-shell .stage is a 100dvh flex column', () => {
  const body = ruleBody(layout, '.native-shell .stage');
  assert.ok(hasDecl(body, 'height', '100dvh'), '.native-shell .stage must be height:100dvh');
  assert.ok(hasDecl(body, 'display', 'flex'), '.native-shell .stage must be display:flex');
  assert.ok(hasDecl(body, 'flex-direction', 'column'), '.native-shell .stage must be flex-direction:column');
});

// ---- eslabón 4: el guard html/body { overflow:hidden } cubre el native-shell, no solo web ---
test('layout.css: html/body overflow guard covers the native-shell path (not only web)', () => {
  const body = ruleBody(layout, ':root.native-shell, :root.native-shell body');
  assert.ok(hasDecl(body, 'overflow', 'hidden'),
    'the :root.native-shell, :root.native-shell body guard must set overflow:hidden');
  assert.ok(hasDecl(body, 'height', '100%'), 'the native-shell guard must set height:100%');
});

test('layout.css: the web transport keeps its own html/body overflow guard', () => {
  // Confirma que el panel servido sigue cubierto por su propio guard (no se rompió al compartir).
  const body = ruleBody(layout, ':root[data-transport="web"], :root[data-transport="web"] body');
  assert.ok(hasDecl(body, 'overflow', 'hidden'),
    'the web transport html/body guard must set overflow:hidden');
});

// ---- eslabón 5: .view sigue siendo el ÚNICO scroller (flex:1; min-height:0; overflow-y) -----
test('layout.css: .view stays the single scroller (flex:1; min-height:0; overflow-y:auto)', () => {
  const body = ruleBody(layout, '.view {');
  assert.ok(hasDecl(body, 'flex', '1'), '.view must be flex:1');
  assert.ok(hasDecl(body, 'min-height', '0'), '.view must be min-height:0');
  assert.ok(hasDecl(body, 'overflow-y', 'auto'), '.view must be overflow-y:auto');
});

// ---- eslabón puente: .shell{flex:1;min-height:0} y .main columna flex con min-height:0 ------
// Sin estos, la columna flex de .win__body no propaga alto acotado hasta .view.
test('shell.css: .shell remains flex:1 / min-height:0 (load-bearing under the flex parent)', () => {
  const body = ruleBody(shell, '.shell {');
  assert.ok(hasDecl(body, 'flex', '1'), '.shell must be flex:1');
  assert.ok(hasDecl(body, 'min-height', '0'), '.shell must be min-height:0');
});

test('shell.css: .main is a flex column with min-height:0', () => {
  const body = ruleBody(shell, '.main {');
  assert.ok(hasDecl(body, 'display', 'flex'), '.main must be display:flex');
  assert.ok(hasDecl(body, 'flex-direction', 'column'), '.main must be flex-direction:column');
  assert.ok(hasDecl(body, 'min-height', '0'), '.main must be min-height:0');
});
