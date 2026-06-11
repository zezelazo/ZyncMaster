// palette.js — command palette Ctrl+K (spec §3.2). Overlay modal (glass atenuado permitido),
// un input + lista agrupada, fuzzy (core/fuzzy), navegación 100% teclado. Las FUENTES de
// items se registran vía registerPaletteSource(fn): cada módulo vivo aporta las suyas —
// la interfaz de providers queda definida desde v1 (los módulos futuros solo registran).

import { filterPalette, flattenGroups, moveSelection } from './core/palette-model.js';

const sources = [];
let overlay = null;
let selected = 0;
let flat = [];
let prevFocus = null; // elemento con foco antes de abrir — se restaura al cerrar (spec §8)

// fn() -> [{ group, label, hint?, run }]. Errores de una fuente nunca rompen el palette.
export function registerPaletteSource(fn) { sources.push(fn); }

function collectItems() {
  const items = [];
  for (const fn of sources) {
    try { items.push(...(fn() || [])); } catch (_) { /* una fuente rota no tumba el resto */ }
  }
  return items;
}

function highlight(doc, label, ranges) {
  const span = doc.createElement('span');
  span.className = 'p-item__lbl';
  let cursor = 0;
  for (const [s, e] of ranges || []) {
    if (s > cursor) span.append(label.slice(cursor, s));
    const m = doc.createElement('span');
    m.className = 'p-item__m';
    m.textContent = label.slice(s, e);
    span.append(m);
    cursor = e;
  }
  if (cursor < label.length) span.append(label.slice(cursor));
  return span;
}

export function closePalette() {
  if (!overlay) return;
  document.removeEventListener('keydown', onKey, true);
  overlay.remove();
  overlay = null;
  // Restore del foco al disparador (spec §8) — mismo contrato que openModal en app.js.
  if (prevFocus && prevFocus.focus) { try { prevFocus.focus(); } catch (_) {} }
  prevFocus = null;
}

function runSelected() {
  const item = flat[selected];
  if (!item) return;
  closePalette();
  try { item.run(); } catch (_) { /* la acción falla silenciosa; su vista da el feedback */ }
}

function onKey(e) {
  if (e.key === 'Escape') { e.preventDefault(); closePalette(); return; }
  if (e.key === 'Tab') {
    // Focus trap (spec §8): Tab queda dentro del dialog — mismo patrón que openModal en app.js.
    const focusables = overlay.querySelectorAll('input, [href], button, select, textarea, [tabindex]:not([tabindex="-1"])');
    if (!focusables.length) return;
    const first = focusables[0];
    const last = focusables[focusables.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    return;
  }
  if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
    e.preventDefault();
    selected = moveSelection(flat.length, selected, e.key === 'ArrowDown' ? 1 : -1);
    paintList();
    return;
  }
  if (e.key === 'Enter') { e.preventDefault(); runSelected(); }
}

function paintList() {
  const list = overlay.querySelector('.palette__list');
  const input = overlay.querySelector('input');
  const groups = filterPalette(collectItems(), input.value);
  flat = flattenGroups(groups);
  if (selected >= flat.length) selected = flat.length ? flat.length - 1 : 0;
  list.replaceChildren();
  if (!flat.length) {
    const empty = document.createElement('div');
    empty.className = 'p-empty';
    empty.textContent = 'No matching commands';
    list.append(empty);
    return;
  }
  let flatIdx = 0;
  for (const g of groups) {
    const head = document.createElement('div');
    head.className = 'p-group';
    head.textContent = g.group;
    list.append(head);
    for (const it of g.items) {
      const idx = flatIdx;
      const row = document.createElement('div');
      row.className = 'p-item' + (idx === selected ? ' p-item--sel' : '');
      row.setAttribute('role', 'option');
      row.setAttribute('aria-selected', String(idx === selected));
      row.append(highlight(document, it.label, it.ranges));
      if (it.hint) {
        const hint = document.createElement('span');
        hint.className = 'p-item__hint';
        hint.textContent = it.hint;
        row.append(hint);
      }
      row.addEventListener('click', () => { selected = idx; runSelected(); });
      row.addEventListener('mousemove', () => { if (selected !== idx) { selected = idx; paintList(); } });
      list.append(row);
      flatIdx += 1;
    }
  }
  const sel = list.querySelector('.p-item--sel');
  if (sel) sel.scrollIntoView({ block: 'nearest' });
}

export function openPalette() {
  if (overlay) { closePalette(); return; }
  prevFocus = document.activeElement;
  selected = 0;
  overlay = document.createElement('div');
  overlay.className = 'palette-overlay';
  overlay.addEventListener('click', (e) => { if (e.target === overlay) closePalette(); });

  const card = document.createElement('div');
  card.className = 'glass--overlay palette';
  card.setAttribute('role', 'dialog');
  card.setAttribute('aria-label', 'Command palette');

  const input = document.createElement('input');
  input.type = 'text';
  input.placeholder = 'Type a command or search…';
  input.setAttribute('aria-label', 'Command palette search');
  input.addEventListener('input', () => { selected = 0; paintList(); });

  const list = document.createElement('div');
  list.className = 'palette__list';
  list.setAttribute('role', 'listbox');

  const foot = document.createElement('div');
  foot.className = 'palette__foot';
  foot.innerHTML = '<span>↑↓ navigate</span><span>↵ run</span><span>esc close</span>';

  card.append(input, list, foot);
  overlay.append(card);
  document.body.append(overlay);
  document.addEventListener('keydown', onKey, true);
  paintList();
  input.focus();
}

// Cableado global: Ctrl+K (y Cmd+K en mac). En el WebView2 el keydown del documento es
// nuestro — no hay conflicto de hotkey global de SO (eso aplica solo al viewer, que ya
// tiene su fallback en el host).
export function initPalette() {
  document.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && !e.altKey && (e.key === 'k' || e.key === 'K')) {
      e.preventDefault();
      openPalette();
    }
  });
  // El botón "Search…" del view header llama a esto sin import circular.
  window.__openPalette = openPalette;
}
