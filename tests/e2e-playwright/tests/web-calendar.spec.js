import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

// Smoke of the Angular web calendar: serves the PRODUCTION build from disk under /zync-web/
// (deep link, exercising the base-href + client routing) and mocks the /zync API with
// page.route. Auth enters through the real guard via a mocked /identity/refresh.

const here = path.dirname(fileURLToPath(import.meta.url));
const dist = path.resolve(here, '../../../web/zync-web/dist/zync-web/browser');
const CONTENT_TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.ico': 'image/x-icon' };

// EXACT wire shape of GET /api/calendar/day (plan header, regenerated from the backend
// plan's CalendarDayEndpointTests): kind/scope-PascalCase/freshness-STRING/title/canWrite.
const DAY = {
  date: '2026-06-10',
  accounts: [
    { accountId: 'a1', email: 'z@job1.com', kind: 'graph', scope: 'ReadWrite', freshness: 'live',
      events: [{ accountId: 'a1', calendarId: 'c1', eventId: 'e1', stableId: 's1',
                 title: 'Comité de arquitectura', start: '2026-06-10T14:00:00',
                 end: '2026-06-10T15:30:00', isAllDay: false, showAs: 'busy', isCancelled: false,
                 isOrganizer: false, isReplica: false, canWrite: true,
                 replicas: [{ linkId: 'l1', maskTitle: 'Busy', destinationAccountId: 'a2', destinationCalendarId: 'c2', status: 'active' }] }] },
    { accountId: 'com1', email: 'classic@outlook.com', kind: 'com', scope: 'Read',
      freshness: 'snapshot_unavailable', events: [] },
  ],
};

test.beforeEach(async ({ page }) => {
  // Persisted refresh token -> the guard's silent refresh signs the session in.
  await page.addInitScript(() => localStorage.setItem('zw.refresh', 'ref-e2e'));

  await page.route('https://web.test/zync/identity/refresh', (route) =>
    route.fulfill({ json: { accessToken: 'acc-e2e', newRefreshToken: 'ref-e2e-2' } }));
  await page.route('https://web.test/zync/api/calendar/day**', (route) =>
    route.fulfill({ json: DAY }));
  page.respondBodies = [];
  page.respondUrls = [];
  // Two-segment respond route ({accountId}/{eventId} — backend decision 1); ** spans both.
  await page.route('https://web.test/zync/api/calendar/events/**/respond', async (route) => {
    page.respondBodies.push(route.request().postDataJSON());
    page.respondUrls.push(new URL(route.request().url()).pathname);
    await route.fulfill({ json: { status: 'ok' } });
  });

  // Static SPA files under /zync-web/ with try_files-style index.html fallback.
  await page.route('https://web.test/zync-web/**', async (route) => {
    const url = new URL(route.request().url());
    const rel = url.pathname.replace(/^\/zync-web\/?/, '') || 'index.html';
    try {
      const body = await readFile(path.join(dist, rel), null);
      await route.fulfill({ body, contentType: CONTENT_TYPES[path.extname(rel)] ?? 'application/octet-stream' });
    } catch {
      const body = await readFile(path.join(dist, 'index.html'), null);
      await route.fulfill({ body, contentType: 'text/html; charset=utf-8' });
    }
  });
});

test('deep link renders the unified columns and the event detail responds with a message', async ({ page }) => {
  await page.goto('https://web.test/zync-web/calendar'); // deep link: index fallback + guard refresh

  await expect(page.getByTestId('col-head')).toHaveCount(2);
  await expect(page.getByText('snapshot unavailable')).toBeVisible(); // COM degradation badge

  await page.getByTestId('event').click();
  await expect(page.getByText('Replicas')).toBeVisible();
  await expect(page.getByText('“Busy”')).toBeVisible();

  await page.getByTestId('respond-decline').click();
  await page.getByTestId('respond-message').fill('No puedo asistir');
  await page.getByTestId('respond-send').click();
  await expect.poll(() => page.respondBodies.length).toBe(1);
  expect(page.respondBodies[0]).toEqual({ action: 'decline', message: 'No puedo asistir' });
  expect(page.respondUrls[0]).toBe('/zync/api/calendar/events/a1/e1/respond');
});

test('cancel is organizer-only and asks for confirmation', async ({ page }) => {
  DAY.accounts[0].events[0].isOrganizer = true;
  await page.goto('https://web.test/zync-web/calendar');
  await page.getByTestId('event').click();

  await page.getByTestId('cancel-event').click();
  await expect(page.getByTestId('cancel-confirm')).toBeVisible();
  expect(page.respondBodies.length).toBe(0); // nothing posted before confirming
  await page.getByTestId('cancel-confirm').click();
  await expect.poll(() => page.respondBodies.length).toBe(1);
  expect(page.respondBodies[0]).toEqual({ action: 'cancel' });
  DAY.accounts[0].events[0].isOrganizer = false;
});
