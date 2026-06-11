// Pure helpers for the Calendar v2 unified day/week view. NO DOM, NO bridge, NO Date.now()
// on purpose so plain `node --test` can unit-test them (tests/js-unit/calendar-day.test.mjs);
// app.js wires them to the DOM. Mirrors the web-transport.js pattern.

// Column accent classes cycle per account, matching the approved mock's azure/aqua/cyan palette
// (docs/research/mocks/mock-app-calendar.html).
const ACCOUNT_CLASSES = ['az', 'aq', 'cy'];
export function accountColorClass(index) {
  const n = ACCOUNT_CLASSES.length;
  return ACCOUNT_CLASSES[((index % n) + n) % n];
}

// Pixel block for one event inside the [startHour, endHour) day window, given its start/end in
// minutes-of-local-day. Clamps partial overlaps to the window, hides fully-outside events
// (null), and enforces a 15-minute minimum visual height so zero-length events stay clickable.
export function clampBlock(startMin, endMin, { startHour = 7, endHour = 21, pxPerHour = 66 } = {}) {
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
export function localMinutes(iso) {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  return d.getHours() * 60 + d.getMinutes();
}

// CTA label for the Replicate panel: reflects how many destinations are checked and whether
// any checked destination still misses its REQUIRED masked title (decision D6).
export function replicateButtonLabel(count, missingTitles = false) {
  if (!count) return 'Select a destination';
  if (missingTitles) return 'Type a title for each destination';
  return count === 1 ? 'Create 1 replica' : `Create ${count} replicas`;
}

// The visible title a destination will receive: the trimmed manual mask, or null when blank.
// A blank mask is INVALID by design (decision D6, calendar-v2 spec §2/§12.1): there is NO code
// path that copies the origin title into a replica, and the server also rejects empty
// destination titles ("maskTitle is required — a replica NEVER inherits the source subject").
export function maskedTitle(input) {
  const t = (input || '').trim();
  return t.length ? t : null;
}

// The Monday-start week containing dateIso ('yyyy-mm-dd'). Pure UTC math on the string so the
// result never depends on the machine's timezone.
export function weekDates(dateIso) {
  const [y, m, d] = dateIso.split('-').map(Number);
  const base = new Date(Date.UTC(y, m - 1, d));
  const monOffset = (base.getUTCDay() + 6) % 7; // 0 = Monday
  const out = [];
  for (let i = 0; i < 7; i++) {
    const dt = new Date(Date.UTC(y, m - 1, d - monOffset + i));
    out.push(dt.toISOString().slice(0, 10));
  }
  return out;
}

// dateIso shifted by N days ('yyyy-mm-dd' in/out), pure UTC math (same TZ-safety as weekDates).
export function shiftDate(dateIso, days) {
  const [y, m, d] = dateIso.split('-').map(Number);
  const dt = new Date(Date.UTC(y, m - 1, d + days));
  return dt.toISOString().slice(0, 10);
}

// Honest-freshness badge from the per-account freshness STRING (backend decision 3): "live"
// (Graph) renders no badge; "snapshot_unavailable" (COM — the v1 server persists no snapshots)
// renders an explicit degradation badge. The minutes/device variant arrives in a later backend
// phase; do NOT branch on objects here until the server actually emits them.
export function freshnessLabel(freshness) {
  if (freshness === 'snapshot_unavailable') return 'snapshot unavailable';
  return null;
}
