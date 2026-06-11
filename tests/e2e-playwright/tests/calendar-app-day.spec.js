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

// Helper de navegación REAL (post-P2): el sidebar se construye desde registry.navItems() y la
// vista calendar-day es sub-ruta de Calendar (sin entrada de nav propia). El boot llega al
// home tras el silent sign-in (getIdentityState mockeado como signed-in en REPLIES); de ahí se
// abre Calendar por el sidebar (igual que shell.spec.js: sidebar.getByRole('button',{name:
// 'Calendar'})) y luego la acción "Day view" del header de la vista Calendar (botón añadido en
// 6.5, id #openCalendarDay).
async function gotoDayView(page) {
  await page.goto('https://app.test/index.html');
  // Esperar a que el shell pinte el sidebar (no el gate de sign-in).
  await page.locator('#sidebar').getByRole('button', { name: 'Calendar' }).click();
  // Acción "Day view" dentro de la vista Calendar (selector estable por id, no por texto).
  await page.locator('#openCalendarDay').click();
}

test('day view renders the unified grid with the COM degradation badge', async ({ page }) => {
  await gotoDayView(page);

  // Unified day: the Graph event renders positioned, the COM account degrades VISIBLY.
  await expect(page.locator('.calday-ev')).toHaveCount(1);
  await expect(page.locator('.calday-ev')).toContainText('Comité de arquitectura');
  await expect(page.getByText('snapshot unavailable')).toBeVisible();
});
