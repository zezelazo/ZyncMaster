// Pure layout helpers for the calendar management day grid. NO DOM, NO Date.now() on purpose
// so plain Vitest can unit-test them (day-layout.spec.ts).
// Kept in sync with ui/js/calendar-day.js by hand — change both.

// Pixel block for one event inside the [startHour, endHour) day window, given its start/end in
// minutes-of-local-day. Clamps partial overlaps to the window, hides fully-outside events
// (null), and enforces a 15-minute minimum visual height so zero-length events stay clickable.
export function clampBlock(
  startMin: number,
  endMin: number,
  { startHour = 7, endHour = 21, pxPerHour = 66 }: { startHour?: number; endHour?: number; pxPerHour?: number } = {},
): { top: number; height: number } | null {
  if (!Number.isFinite(startMin) || !Number.isFinite(endMin)) return null;
  const winStart = startHour * 60;
  const winEnd = endHour * 60;
  if (endMin <= winStart || startMin >= winEnd) return null;
  const s = Math.max(startMin, winStart);
  const e = Math.min(Math.max(endMin, s + 15), winEnd);
  return {
    top: ((s - winStart) / 60) * pxPerHour,
    height: ((e - s) / 60) * pxPerHour,
  };
}

// Minutes since LOCAL midnight for an ISO instant (the day grid is the user's local day).
// Returns null for an unparsable value so the caller can skip the event instead of NaN-ing.
export function localMinutes(iso: string): number | null {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  return d.getHours() * 60 + d.getMinutes();
}
