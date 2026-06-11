import test from 'node:test';
import assert from 'node:assert/strict';
import {
  accountColorClass, clampBlock, localMinutes, replicateButtonLabel,
  maskedTitle, weekDates, shiftDate, freshnessLabel,
} from '../../ui/js/calendar-day.js';

test('accountColorClass cycles az/aq/cy per account index', () => {
  assert.equal(accountColorClass(0), 'az');
  assert.equal(accountColorClass(1), 'aq');
  assert.equal(accountColorClass(2), 'cy');
  assert.equal(accountColorClass(3), 'az');
});

test('clampBlock positions an event inside the visible window', () => {
  // window 07:00-21:00, 66 px/h: 08:30-09:25 → top 99px, height ~60.5px
  const b = clampBlock(8 * 60 + 30, 9 * 60 + 25, { startHour: 7, endHour: 21, pxPerHour: 66 });
  assert.equal(b.top, 99);
  assert.ok(Math.abs(b.height - 60.5) < 0.01);
});

test('clampBlock clamps to the window edges and hides fully-outside events', () => {
  const clipped = clampBlock(6 * 60, 8 * 60, { startHour: 7, endHour: 21, pxPerHour: 60 });
  assert.equal(clipped.top, 0);
  assert.equal(clipped.height, 60); // only 07:00-08:00 is visible
  assert.equal(clampBlock(22 * 60, 23 * 60, { startHour: 7, endHour: 21, pxPerHour: 60 }), null);
  assert.equal(clampBlock(4 * 60, 5 * 60, { startHour: 7, endHour: 21, pxPerHour: 60 }), null);
});

test('clampBlock enforces a 15-minute minimum visual height', () => {
  const b = clampBlock(10 * 60, 10 * 60 + 5, { startHour: 7, endHour: 21, pxPerHour: 60 });
  assert.equal(b.height, 15); // 15 min * 60px/h = 15px
});

test('localMinutes converts an ISO instant to minutes-of-local-day and rejects junk', () => {
  const d = new Date(2026, 5, 10, 14, 30); // 14:30 LOCAL, TZ-independent assertion
  assert.equal(localMinutes(d.toISOString()), 14 * 60 + 30);
  assert.equal(localMinutes('garbage'), null);
});

test('replicateButtonLabel pluralises, handles zero and flags missing titles', () => {
  assert.equal(replicateButtonLabel(0), 'Select a destination');
  assert.equal(replicateButtonLabel(1), 'Create 1 replica');
  assert.equal(replicateButtonLabel(2), 'Create 2 replicas');
  assert.equal(replicateButtonLabel(2, true), 'Type a title for each destination');
});

test('maskedTitle returns the typed mask or null — a blank mask NEVER becomes the original title', () => {
  // Privacy invariant (calendar-v2 spec §2/§12.1 + decision D6): there is no code path that
  // copies the origin title into a replica. Blank input = invalid (null), never a fallback.
  assert.equal(maskedTitle('Busy'), 'Busy');
  assert.equal(maskedTitle('  Busy  '), 'Busy');
  assert.equal(maskedTitle('   '), null);
  assert.equal(maskedTitle(undefined), null);
  assert.equal(maskedTitle(null), null);
});

test('weekDates returns the Monday-start week containing the date', () => {
  assert.deepEqual(weekDates('2026-06-10'), [ // Wednesday
    '2026-06-08', '2026-06-09', '2026-06-10', '2026-06-11',
    '2026-06-12', '2026-06-13', '2026-06-14',
  ]);
  assert.equal(weekDates('2026-06-08')[0], '2026-06-08'); // a Monday starts its own week
});

test('shiftDate moves across month boundaries with pure date math', () => {
  assert.equal(shiftDate('2026-06-30', 1), '2026-07-01');
  assert.equal(shiftDate('2026-06-01', -1), '2026-05-31');
  assert.equal(shiftDate('2026-06-10', 7), '2026-06-17');
});

test('freshnessLabel maps the server freshness STRING to badge text', () => {
  // Backend decision 3: v1 emits "live" (Graph) or "snapshot_unavailable" (COM) — never an
  // object and never null. "live" renders NO badge; minutes/device come in a later phase.
  assert.equal(freshnessLabel('live'), null);
  assert.equal(freshnessLabel('snapshot_unavailable'), 'snapshot unavailable');
  assert.equal(freshnessLabel(null), null);
  assert.equal(freshnessLabel(undefined), null);
});

test('replicateRequest builds the bridge payload from checked destinations with REQUIRED typed titles', async () => {
  const { replicateRequest } = await import('../../ui/js/calendar-day.js');
  const req = replicateRequest('acc-1', 'evt-1', [
    { checked: true, accountId: 'a1', calendarId: 'c1', title: ' Busy ' },
    { checked: false, accountId: 'a2', calendarId: 'c2', title: 'ignored' },
  ]);
  assert.deepEqual(req, {
    accountId: 'acc-1',
    eventId: 'evt-1',
    destinations: [{ accountId: 'a1', calendarId: 'c1', title: 'Busy' }],
  });
  assert.equal(replicateRequest('acc-1', 'evt-1', [{ checked: false }]), null); // nothing checked
  // Decision D6: a checked destination with a blank mask makes the whole request invalid.
  assert.equal(replicateRequest('acc-1', 'evt-1', [
    { checked: true, accountId: 'a1', calendarId: 'c1', title: '   ' },
  ]), null);
});

test('the origin title never appears in a replicate payload unless the user typed it', async () => {
  // Privacy contract (calendar-v2 spec §12.1): replicateRequest does not even RECEIVE the
  // origin title, so no code path can copy it. Typing it is the only way it travels.
  const { replicateRequest } = await import('../../ui/js/calendar-day.js');
  const origin = 'Dentista — chequeo anual';
  const req = replicateRequest('acc-1', 'evt-1', [
    { checked: true, accountId: 'a1', calendarId: 'c1', title: 'Busy' },
  ]);
  assert.ok(!JSON.stringify(req).includes(origin));
  const typed = replicateRequest('acc-1', 'evt-1', [
    { checked: true, accountId: 'a1', calendarId: 'c1', title: origin },
  ]);
  assert.equal(typed.destinations[0].title, origin);
});
