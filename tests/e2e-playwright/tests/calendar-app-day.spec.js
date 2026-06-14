import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

// Smoke of the desktop App's Calendar v2 day view. Serves ui/ from disk and injects a MOCK
// window.chrome.webview that answers the bridge contract (same technique as
// clipboard-viewer.spec.js), so the REAL render code runs with deterministic data. Every mock
// below mirrors the EXACT wire shape pinned in the plan header (regenerated from the backend
// plan's endpoint tests) and the EXACT serialized bridge DTOs — never an invented shape.

const here = path.dirname(fileURLToPath(import.meta.url));
const uiRoot = path.resolve(here, '../../../ui');
const CONTENT_TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml' };

// GET /api/calendar/day shape: kind/scope-PascalCase/freshness-string/title/canWrite/eventId.
const DAY = JSON.stringify({
  date: '2026-06-10',
  accounts: [
    { accountId: 'a1', email: 'z@job1.com', kind: 'graph', scope: 'ReadWrite', freshness: 'live',
      events: [{ accountId: 'a1', calendarId: 'c1', eventId: 'e1', stableId: 's-e1',
                 title: 'Comité de arquitectura', start: '2026-06-10T14:00:00',
                 end: '2026-06-10T15:30:00', isAllDay: false, showAs: 'busy', isCancelled: false,
                 isOrganizer: false, isReplica: false, canWrite: true, replicas: [] }] },
    { accountId: 'com1', email: 'classic@outlook.com', kind: 'com', scope: 'Read',
      freshness: 'snapshot_unavailable', events: [] },
  ],
});

// EXACT serialized CalendarAccountSummary (src/ZyncMaster.App/Bridge/ICalendarServerClient.cs
// through UiBridge's camelCase JsonOptions): id/kind/provider/accountEmail/scope/status/displayName.
const ACCOUNTS = JSON.stringify([
  { id: 'a1', kind: 'graph', provider: 'microsoft', accountEmail: 'z@job1.com', scope: 'ReadWrite', status: 'active', displayName: 'Work' },
  { id: 'a2', kind: 'graph', provider: 'microsoft', accountEmail: 'z@gmail.com', scope: 'ReadWrite', status: 'active', displayName: 'Personal' },
]);

// CalendarInfo[] (src/ZyncMaster.Engine/Models/CalendarInfo.cs, camelCase).
const CALENDARS = JSON.stringify([{ id: 'c1', displayName: 'Calendar', isDefault: true, owner: null }]);

// Answers per bridge action; payload values are the JSON STRINGS UiBridge would return.
const REPLIES = {
  checkServerHealth: '{"ok":true,"status":"ok","message":null}',
  getIdentityState: '{"isSignedIn":true,"userId":"u1","email":"z@x.com","displayName":"Z","expiresAt":null,"plan":"beta"}',
  getStatus: '{"status":"Idle","paired":true,"paused":false,"pairingCode":null,"noConnectedAccount":false,"lastMessage":null,"lastSyncUtc":null,"created":0,"updated":0,"deleted":0,"skipped":0}',
  getCapabilities: '{"outlookCom":false}',
  getAutoStart: 'false',
  listPairs: '[]',
  listAccounts: '[]',
  listCalendarAccounts: ACCOUNTS,
  listCalendars: CALENDARS,
  getClipboardHistory: '[]',
  getClipboardDevices: '{"devices":[],"thisDeviceId":"","pastePanelOpacity":70}',
  getCalendarDay: DAY,
  listPrefixRules: '[{"id":"r1","prefix":"Lunch","maskTitle":"Lunch","enabled":true,"sortOrder":0,"destinations":[{"accountId":"a2","calendarId":"c1"}]}]',
  createEventReplicas: '{"created":[],"failures":[]}',
  createCalendarEvent: '{"eventId":"new-1","replicas":null}',
  savePrefixRule: '{"status":"ok"}',
  deletePrefixRule: null,
};

test.beforeEach(async ({ page }) => {
  await page.addInitScript((replies) => {
    const listeners = new Set();
    window.__bridgeCalls = [];
    window.chrome = {
      webview: {
        addEventListener: (name, cb) => { if (name === 'message') listeners.add(cb); },
        removeEventListener: (name, cb) => { listeners.delete(cb); },
        postMessage: (raw) => {
          const msg = typeof raw === 'string' ? JSON.parse(raw) : raw;
          window.__bridgeCalls.push(msg);
          if (!msg.correlationId) return; // fire-and-forget window controls
          const payload = Object.prototype.hasOwnProperty.call(replies, msg.action)
            ? replies[msg.action] : null;
          const reply = JSON.stringify({ correlationId: msg.correlationId, ok: true, payload, error: null });
          setTimeout(() => listeners.forEach((cb) => cb({ data: reply })), 5);
        },
      },
    };
  }, REPLIES);

  await page.route('https://app.test/**', async (route) => {
    const url = new URL(route.request().url());
    const rel = url.pathname === '/' ? '/index.html' : url.pathname;
    try {
      const body = await readFile(path.join(uiRoot, rel), null);
      await route.fulfill({ body, contentType: CONTENT_TYPES[path.extname(rel)] ?? 'application/octet-stream' });
    } catch {
      await route.fulfill({ status: 404, body: 'not found' });
    }
  });
});

// Helper de navegación REAL (tras el rediseño de info-arch): el sidebar se construye desde
// registry.navItems() y la entrada "Calendar" ahora ATERRIZA DIRECTO en la vista día/semana
// (calendar-day registra el bloque nav). La pantalla de configuración pares/accounts vive
// DEBAJO en el árbol, alcanzable por el engranaje. El boot llega al home tras el silent
// sign-in (getIdentityState mockeado como signed-in en REPLIES); de ahí se abre Calendar por
// el sidebar (igual que shell.spec.js: sidebar.getByRole('button',{name:'Calendar'})) y eso
// ya es la vista día — no hay paso intermedio "Day view".
async function gotoDayView(page) {
  await page.goto('https://app.test/index.html');
  // Esperar a que el shell pinte el sidebar (no el gate de sign-in).
  await page.locator('#sidebar').getByRole('button', { name: 'Calendar' }).click();
  // El click en "Calendar" aterriza directamente en la vista día (su header propio .calday-head).
  await expect(page.locator('.calday-head')).toBeVisible();
}

test('day view renders the unified grid with the COM degradation badge', async ({ page }) => {
  await gotoDayView(page);

  // Unified day: the Graph event renders positioned, the COM account degrades VISIBLY.
  await expect(page.locator('.calday-ev')).toHaveCount(1);
  await expect(page.locator('.calday-ev')).toContainText('Comité de arquitectura');
  await expect(page.getByText('snapshot unavailable')).toBeVisible();
});

test('replicate panel requires a typed mask title before creating', async ({ page }) => {
  // gotoDayView: sidebar → Calendar aterriza directo en la vista día.
  await gotoDayView(page);
  await page.locator('.calday-ev').click();
  await expect(page.getByRole('heading', { name: 'Replicate event' })).toBeVisible();

  const destRow = page.locator('.calday-dest').first();
  await destRow.locator('input[type="checkbox"]').check();
  const mask = destRow.locator('input[type="text"]');
  await expect(mask).toHaveValue('');

  // Decision D6: a blank mask is INVALID — the CTA stays disabled and demands a title.
  const cta = page.locator('#calDayCreateReplicas');
  await expect(cta).toBeDisabled();
  await expect(cta).toHaveText('Type a title for each destination');

  await mask.fill('Busy');
  await expect(cta).toBeEnabled();
  await expect(cta).toHaveText('Create 1 replica');
  await cta.click();
  await expect.poll(async () =>
    page.evaluate(() => window.__bridgeCalls.filter((c) => c.action === 'createEventReplicas').length),
  ).toBe(1);

  // Privacy contract: the origin title never travels in the payload — only the typed mask.
  const sent = await page.evaluate(() =>
    window.__bridgeCalls.find((c) => c.action === 'createEventReplicas').payload);
  expect(sent).toContain('"Busy"');
  expect(sent).toContain('"eventId":"e1"');
  expect(sent).not.toContain('Comité');
});

test('prefix rules panel lists the rules from the bridge', async ({ page }) => {
  await gotoDayView(page);  // sidebar → Calendar aterriza directo en la vista día
  await page.locator('.calday-ev').click();
  await page.locator('#calDayManageRules').click();
  await expect(page.getByRole('heading', { name: 'Prefix rules' })).toBeVisible();
  // exact: the static hint copy also mentions “[Lunch] X”; the rule row from the bridge is the
  // element whose text is exactly the bracketed prefix.
  await expect(page.getByText('[Lunch]', { exact: true })).toBeVisible();
});

test('the gear opens the pairs/accounts configuration sub-route', async ({ page }) => {
  await gotoDayView(page);
  await expect(page.locator('.calday-head')).toBeVisible();
  await page.locator('#calDayConfig').click();
  // The gear navigates to the 'calendar' config screen (pairs/accounts): its title is "Calendar
  // Sync" and it carries the "Day view" button that routes back here. The day/week header is gone.
  await expect(page.locator('.view-header__title')).toHaveText('Calendar Sync');
  await expect(page.locator('#openCalendarDay')).toBeVisible();
  await expect(page.locator('.calday-head')).toHaveCount(0);
});

// Stuck-spinner regression (CalendarIA-t2, high): a Force-sync from the read-only Status popup must
// clear its spinner and refresh its counts when the ACTUAL run completes — not on an independent
// loadPairs() that resolved before the mirror finished. This test installs a STATEFUL bridge: the
// first listPairs returns the pre-run pair, runPairNow only resolves once the test releases it, and
// every listPairs AFTER runPairNow returns the post-run counts. So the popup can only show the fresh
// numbers (and the cleared spinner) if it is driven off the real run lifecycle.
test('Status popup Force-sync clears the spinner and updates counts when the run completes', async ({ page }) => {
  await page.addInitScript(() => {
    const listeners = new Set();
    let runResolved = false;       // flips true once the run is released
    let releaseRun = null;         // setTimeout-style callback the page can trigger
    window.__releaseRun = () => { runResolved = true; if (releaseRun) releaseRun(); };

    const PRE = [{ id: 'p1', name: 'Work → Personal', state: 'active', intervalMin: 10,
      lastRunUtc: '2026-06-10T09:00:00Z',
      lastResult: { created: 0, updated: 0, deleted: 0, skipped: 0, failed: 0 },
      source: { provider: 'MicrosoftGraph', accountRef: 'a@job1.com', calendarIds: ['c1'] },
      destination: { provider: 'MicrosoftGraph', accountRef: 'b@job2.com', calendarId: 'c2' } }];
    const POST = [{ ...PRE[0], lastRunUtc: '2026-06-13T12:00:00Z',
      lastResult: { created: 4, updated: 1, deleted: 0, skipped: 2, failed: 0 } }];

    const baseReplies = window.__replies || {};
    window.chrome = {
      webview: {
        addEventListener: (name, cb) => { if (name === 'message') listeners.add(cb); },
        removeEventListener: (name, cb) => { listeners.delete(cb); },
        postMessage: (raw) => {
          const msg = typeof raw === 'string' ? JSON.parse(raw) : raw;
          if (!msg.correlationId) return;
          const send = (payload) => {
            const reply = JSON.stringify({ correlationId: msg.correlationId, ok: true, payload, error: null });
            setTimeout(() => listeners.forEach((cb) => cb({ data: reply })), 5);
          };
          if (msg.action === 'listPairs') { send(JSON.stringify(runResolved ? POST : PRE)); return; }
          if (msg.action === 'runPairNow') {
            // Hold the run open until the test releases it, mirroring a real multi-second mirror.
            releaseRun = () => send(JSON.stringify({ created: 4, updated: 1, deleted: 0, skipped: 2 }));
            if (runResolved) releaseRun();
            return;
          }
          const payload = Object.prototype.hasOwnProperty.call(baseReplies, msg.action)
            ? baseReplies[msg.action] : null;
          send(payload);
        },
      },
    };
  });
  // Carry REPLIES into the page so the stateful bridge can fall through to them for non-pair actions.
  await page.addInitScript((replies) => { window.__replies = replies; }, REPLIES);

  await gotoDayView(page);
  await page.locator('#calDayStatus').click();

  const row = page.locator('.calday-status-row');
  await expect(row).toHaveCount(1);
  const force = row.locator('.calday-status-force');
  await expect(force).toContainText('Force-sync');
  // Pre-run counts: Synced 0, New 0.
  await expect(row.locator('.calday-status-stat-val').nth(0)).toHaveText('0');
  await expect(row.locator('.calday-status-stat-val').nth(1)).toHaveText('0');

  await force.click();
  // Mid-run: the spinner shows and the button is disabled — the popup reflects the live run state.
  await expect(force.locator('.spinner')).toBeVisible();
  await expect(force).toBeDisabled();
  await expect(force).toContainText('Syncing');

  // Release the run; only the real completion (endPairSync after loadPairs) repaints the popup.
  await page.evaluate(() => window.__releaseRun());

  await expect(force.locator('.spinner')).toHaveCount(0);
  await expect(force).toBeEnabled();
  await expect(force).toContainText('Force-sync');
  // Fresh counts: Synced 4+1+2 = 7, New = created = 4.
  await expect(row.locator('.calday-status-stat-val').nth(0)).toHaveText('7');
  await expect(row.locator('.calday-status-stat-val').nth(1)).toHaveText('4');
});
