import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

// Regression for the stale "this device is not registered" clipboard panel (v0.4.3). Repro: the
// clipboard roster is loaded ONCE at boot; before sign-in that load caches an empty list, so the
// clipboard Settings panel reads "this device is not registered". After the user signs in the host
// registers the device (ensureDevice), but nothing re-fetched the roster, so the panel stayed stale.
//
// The fix: ensureDeviceRegistered() invalidates live.clipboardDevices and re-fetches getClipboardDevices
// once ensureDevice resolves. This test installs a STATEFUL bridge whose getClipboardDevices returns an
// EMPTY roster until ensureDevice has been called, and the registered device afterwards. So the panel
// can only show the registered card if the app re-fetched the roster AFTER registration.

const here = path.dirname(fileURLToPath(import.meta.url));
const uiRoot = path.resolve(here, '../../../ui');
const CONTENT_TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml' };

// Static answers for the non-clipboard-device actions; the stateful handler falls through to these.
const REPLIES = {
  checkServerHealth: '{"ok":true,"status":"ok","message":null}',
  getIdentityState: '{"isSignedIn":true,"userId":"u1","email":"z@x.com","displayName":"Z","expiresAt":null,"plan":"beta"}',
  getStatus: '{"status":"Idle","paired":true,"paused":false,"pairingCode":null,"noConnectedAccount":false,"lastMessage":null,"lastSyncUtc":null,"created":0,"updated":0,"deleted":0,"skipped":0}',
  getCapabilities: '{"outlookCom":false}',
  getAutoStart: 'true',
  listPairs: '[]',
  listAccounts: '[]',
  listCalendarAccounts: '[]',
  listCalendars: '[]',
  getCalendarDay: '{"date":"2026-06-15","accounts":[]}',
  listPrefixRules: '[]',
  getClipboardHistory: '[]',
};

const REGISTERED_DEVICES = JSON.stringify({
  devices: [{ id: 'd1', name: 'DEVLAB2', isThis: true, settings: { send: true, receive: true, autoSync: true } }],
  thisDeviceId: 'd1', pastePanelOpacity: 70,
});
const EMPTY_DEVICES = JSON.stringify({ devices: [], thisDeviceId: null, pastePanelOpacity: 70 });
// The shared history is also empty until the device is registered (getClipboardHistory needs the key).
const REGISTERED_HISTORY = JSON.stringify([
  { id: 'h1', type: 'text', text: 'synced from the server', createdUtc: '2026-06-15T07:00:00Z', originDeviceName: 'DEVLAB2' },
]);

test.beforeEach(async ({ page }) => {
  await page.addInitScript((args) => {
    const { replies, registeredDevices, emptyDevices, registeredHistory } = args;
    const listeners = new Set();
    let ensured = false;                 // flips true once the host registers the device
    window.__bridgeCalls = [];
    window.chrome = {
      webview: {
        addEventListener: (name, cb) => { if (name === 'message') listeners.add(cb); },
        removeEventListener: (name, cb) => { listeners.delete(cb); },
        postMessage: (raw) => {
          const msg = typeof raw === 'string' ? JSON.parse(raw) : raw;
          window.__bridgeCalls.push(msg);
          if (!msg.correlationId) return;
          let payload;
          if (msg.action === 'ensureDevice') { ensured = true; payload = '"dev-key"'; }
          else if (msg.action === 'getClipboardDevices') { payload = ensured ? registeredDevices : emptyDevices; }
          else if (msg.action === 'getClipboardHistory') { payload = ensured ? registeredHistory : '[]'; }
          else payload = Object.prototype.hasOwnProperty.call(replies, msg.action) ? replies[msg.action] : null;
          const reply = JSON.stringify({ correlationId: msg.correlationId, ok: true, payload, error: null });
          setTimeout(() => listeners.forEach((cb) => cb({ data: reply })), 5);
        },
      },
    };
  }, { replies: REPLIES, registeredDevices: REGISTERED_DEVICES, emptyDevices: EMPTY_DEVICES, registeredHistory: REGISTERED_HISTORY });

  await page.route('https://app.test/**', async (route) => {
    const url = new URL(route.request().url());
    const rel = url.pathname === '/' ? '/index.html' : url.pathname;
    try {
      const body = await readFile(path.join(uiRoot, rel), null);
      await route.fulfill({ body, contentType: CONTENT_TYPES[path.extname(rel)] ?? 'application/octet-stream' });
    } catch { await route.fulfill({ status: 404, body: 'not found' }); }
  });
});

test('clipboard Settings shows the device as registered after sign-in re-fetches the roster', async ({ page }) => {
  await page.goto('https://app.test/index.html');
  // Boot reaches the signed-in dashboard; ensureDeviceRegistered() fired and (with the fix) re-fetched.
  await page.locator('#sidebar').getByRole('button', { name: 'Clipboard' }).click();
  await page.getByRole('tab', { name: 'Settings' }).click();

  // The registered "this device" card must be shown — NOT the stale "not registered" placeholder.
  await expect(page.getByText('This device is not registered yet')).toHaveCount(0);
  await expect(page.getByText('Send my clipboard')).toBeVisible();

  // Prove the fix actually drove it: a getClipboardDevices call happened AFTER ensureDevice.
  const order = await page.evaluate(() => {
    const calls = window.__bridgeCalls.map((c) => c.action);
    return { ensureAt: calls.indexOf('ensureDevice'), devicesAfter: calls.lastIndexOf('getClipboardDevices') };
  });
  expect(order.ensureAt).toBeGreaterThanOrEqual(0);
  expect(order.devicesAfter).toBeGreaterThan(order.ensureAt);
});

test('in-app clipboard history re-fetches after sign-in so it matches the server (not the empty boot snapshot)', async ({ page }) => {
  await page.goto('https://app.test/index.html');
  // The history is loaded once at boot — empty before the device registered. The fix invalidates and
  // re-fetches it once ensureDevice resolves, so the in-app history shows the real server item instead
  // of the stale empty snapshot (which is what made the in-app list diverge from the floating viewer).
  await page.locator('#sidebar').getByRole('button', { name: 'Clipboard' }).click();
  await expect(page.getByText('synced from the server')).toBeVisible();

  // The history was re-fetched AFTER the device was registered.
  const order = await page.evaluate(() => {
    const calls = window.__bridgeCalls.map((c) => c.action);
    return { ensureAt: calls.indexOf('ensureDevice'), historyAfter: calls.lastIndexOf('getClipboardHistory') };
  });
  expect(order.ensureAt).toBeGreaterThanOrEqual(0);
  expect(order.historyAfter).toBeGreaterThan(order.ensureAt);
});
