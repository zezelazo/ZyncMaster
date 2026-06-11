// palette-model.js — la mitad pura del command palette (spec §3.2): filtrado fuzzy
// agrupado y selección por teclado. El DOM vive en ui/js/palette.js. DOM-free.

import { fuzzyMatch } from './fuzzy.js';

const MAX_RESULTS = 14;

// items: [{ group, label, hint?, run }] -> [{ group, items: [{ …item, score, ranges }] }]
// Orden: con query, score desc — el sort es ESTABLE (ES2019), así que los empates de score
// preservan el orden de registro; con query vacía todos los scores son 0 y NO se ordena:
// se respeta el orden de registro tal cual. Los grupos preservan el orden de primera
// aparición (es lo que promete este módulo — nunca ordenar alfabéticamente).
export function filterPalette(items, query) {
  const q = String(query || '').trim();
  const scored = [];
  for (const it of items || []) {
    const m = fuzzyMatch(q, it.label);
    if (m) scored.push({ ...it, score: m.score, ranges: m.ranges });
  }
  if (q) scored.sort((a, b) => b.score - a.score);
  const limited = scored.slice(0, MAX_RESULTS);
  const groups = [];
  for (const it of limited) {
    let g = groups.find((x) => x.group === it.group);
    if (!g) { g = { group: it.group, items: [] }; groups.push(g); }
    g.items.push(it);
  }
  return groups;
}

export function flattenGroups(groups) {
  return (groups || []).flatMap((g) => g.items);
}

// Índice siguiente con wrap-around; -1 cuando no hay items.
export function moveSelection(flatCount, index, delta) {
  if (!flatCount || flatCount <= 0) return -1;
  return ((index + delta) % flatCount + flatCount) % flatCount;
}
