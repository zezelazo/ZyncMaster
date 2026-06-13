// Pure helpers for the Calendar v2 unified day/week view. NO DOM, NO bridge, NO Date.now()
// on purpose so plain `node --test` can unit-test them (tests/js-unit/calendar-day.test.mjs);
// app.js wires them to the DOM. Mirrors the web-transport.js pattern.
// clampBlock/localMinutes: kept in sync with
// web/zync-web/src/app/features/calendar/day-layout.ts by hand — change both.

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

// Parse a 'yyyy-mm-dd' day string into a LOCAL Date (no timezone shift). Passing the raw ISO to
// `new Date('2026-06-13')` parses it as UTC midnight, which renders the PREVIOUS day in negative
// UTC offsets — so build the date from its parts to keep the weekday/label honest everywhere.
// A malformed/empty value degrades to an Invalid Date rather than throwing; the formatters below
// then return '' so the UI shows a blank label instead of crashing or "Invalid Date".
export function isoToLocalDate(iso) {
  const [y, m, d] = String(iso).split('-').map(Number);
  if (!Number.isFinite(y) || !Number.isFinite(m) || !Number.isFinite(d)) return new Date(NaN);
  return new Date(y, m - 1, d);
}

// "Wed, June 13" — weekday + month + day, matching the mock's date label (mock line 127). The
// sticky day banner and the #vhead meta both use this so the topbar and the in-view banner agree.
export function formatDayLabel(iso) {
  const d = isoToLocalDate(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleDateString(undefined, { weekday: 'short', month: 'long', day: 'numeric' });
}

// Per-column header in week mode: "Wed" over "Jun 13" so a 7-wide row stays readable.
export function formatWeekday(iso) {
  const d = isoToLocalDate(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleDateString(undefined, { weekday: 'short' });
}

// "Jun 13" — the short month + day under the weekday in a week column.
export function formatShortDate(iso) {
  const d = isoToLocalDate(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
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

// The createEventReplicas bridge payload from the panel's destination rows, or null when
// nothing is checked OR any checked destination still misses its REQUIRED mask title
// (decision D6). The origin title is NOT a parameter on purpose: no code path in this module
// can copy it into a replica — the server enforces the same invariant on its side.
export function replicateRequest(accountId, eventId, rows) {
  const checked = (rows || []).filter((r) => r && r.checked);
  if (!checked.length) return null;
  const destinations = [];
  for (const r of checked) {
    const title = maskedTitle(r.title);
    if (!title) return null; // blank mask = invalid; the CTA stays disabled
    destinations.push({ accountId: r.accountId, calendarId: r.calendarId, title });
  }
  return { accountId, eventId, destinations };
}

// Validates + normalises the prefix-rule form into the savePrefixRule payload. Returns the
// payload object, or a human error STRING when the form is invalid (the panel shows it).
// Brackets are forbidden inside the prefix: the stored value is the bare word, the UI renders
// the [brackets] (calendar-v2 spec §5 — the bracket is instruction, not content).
export function prefixRulePayload(form) {
  const prefix = (form.prefix || '').trim();
  const maskTitle = (form.maskTitle || '').trim();
  if (!prefix) return 'The prefix is required.';
  if (/[\[\]]/.test(prefix)) return 'Type the prefix without brackets — they are added automatically.';
  if (!maskTitle) return 'The mask title is required.';
  const destinations = (form.destinations || []).filter((d) => d && d.accountId && d.calendarId);
  if (!destinations.length) return 'Pick at least one destination calendar.';
  const sortOrder = Number.isFinite(Number(form.sortOrder)) ? Number(form.sortOrder) : 0;
  const payload = { prefix, maskTitle, enabled: form.enabled !== false, sortOrder, destinations };
  if (form.id) payload.id = form.id;
  return payload;
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

// Builds the createCalendarEvent body from the New-event form (+ replicate-on-create rows),
// or returns a human error STRING when invalid. startTime is local 'HH:mm' on the local
// 'date'; the wire carries instants in start/end (server CreateEventRequest: accountId,
// calendarId, title, start, end, showAs, replicas). Replica rows reuse the replicate-panel
// semantics: only checked rows travel and every checked row REQUIRES a typed title — a blank
// mask NEVER falls back to the event title (decision D6).
export function newEventPayload(form, replicaRows) {
  const title = (form.title || '').trim();
  if (!form.accountId || !form.calendarId) return 'Pick the calendar the event lives in.';
  if (!title) return 'The title is required.';
  if (!/^\d{4}-\d{2}-\d{2}$/.test(form.date || '')) return 'Pick a date.';
  if (!/^\d{2}:\d{2}$/.test(form.startTime || '')) return 'Pick a start time.';
  const duration = Number(form.durationMinutes);
  if (!Number.isFinite(duration) || duration < 5) return 'Duration must be at least 5 minutes.';

  const replicas = [];
  for (const r of (replicaRows || []).filter((x) => x && x.checked)) {
    const mask = maskedTitle(r.title);
    if (!mask) return 'Type a visible title for every replica destination.';
    replicas.push({ accountId: r.accountId, calendarId: r.calendarId, title: mask });
  }

  const [y, m, d] = form.date.split('-').map(Number);
  const [hh, mm] = form.startTime.split(':').map(Number);
  const startLocal = new Date(y, m - 1, d, hh, mm);
  const endLocal = new Date(startLocal.getTime() + duration * 60 * 1000);

  return {
    accountId: form.accountId,
    calendarId: form.calendarId,
    title,
    start: startLocal.toISOString(),
    end: endLocal.toISOString(),
    showAs: form.showAs || 'busy',
    replicas,
  };
}

// Honest-freshness badge from the per-account freshness STRING (backend decision 3): "live"
// (Graph) renders no badge; "snapshot_unavailable" (COM — the v1 server persists no snapshots)
// renders an explicit degradation badge. The minutes/device variant arrives in a later backend
// phase; do NOT branch on objects here until the server actually emits them.
export function freshnessLabel(freshness) {
  if (freshness === 'snapshot_unavailable') return 'snapshot unavailable';
  return null;
}
