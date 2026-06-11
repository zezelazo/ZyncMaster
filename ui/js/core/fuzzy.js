// fuzzy.js — subsequence matcher simple para el command palette (spec §3.2). DOM-free.
// fuzzyMatch(query, text) -> { score, ranges:[[start,end],…] } | null (no es subsecuencia).
// Heurística: +1 por carácter, bonus por racha contigua y por inicio de palabra; penalización
// suave por textos largos. Suficiente para listas de decenas de items; nada más (YAGNI).

export function fuzzyMatch(query, text) {
  const q = String(query || '').toLowerCase();
  const t = String(text || '').toLowerCase();
  if (!q) return { score: 0, ranges: [] };
  let ti = 0;
  let score = 0;
  let streak = 0;
  const ranges = [];
  for (let qi = 0; qi < q.length; qi++) {
    const ch = q[qi];
    let found = -1;
    while (ti < t.length) {
      if (t[ti] === ch) { found = ti; ti += 1; break; }
      ti += 1;
    }
    if (found === -1) return null;
    const last = ranges[ranges.length - 1];
    if (last && found === last[1]) {
      last[1] = found + 1;
      streak += 1;
      score += 3 + streak;
    } else {
      ranges.push([found, found + 1]);
      streak = 0;
      score += 1;
    }
    if (found === 0 || /[\s\-—/·]/.test(t[found - 1])) score += 4; // inicio de palabra
  }
  score -= Math.floor((t.length - q.length) / 8);
  return { score, ranges };
}
