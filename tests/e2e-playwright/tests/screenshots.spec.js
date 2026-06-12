import { test } from '@playwright/test';

// Capturas de evidencia visual del shell nuevo (no asserts — produce PNGs para revisión).
// Mismo patrón de mocks de sesión que shell.spec.js.

async function mockSession(page) {
  await page.route('**/health', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ status: 'ok' }) }));
  await page.route('**/api/me', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ email: 'zeze@test', displayName: 'Zeze Lazo' }) }));
  await page.route('**/api/pairs', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([
      { id: 'p1', name: 'Work → Personal', state: 'active', lastRunUtc: '2026-06-10T09:42:00Z', intervalMin: 10,
        lastResult: { created: 1, updated: 2, deleted: 0, skipped: 0, failed: 0 },
        source: { provider: 'MicrosoftGraph', accountRef: 'a@test', calendarIds: ['c1'] },
        destination: { provider: 'MicrosoftGraph', accountRef: 'b@test', calendarId: 'c2' } },
    ]) }));
  await page.route('**/api/accounts', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([]) }));
  await page.route('**/api/accounts/**/calendars', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([]) }));
  await page.route('**/api/calendar/day', (route) =>
    route.fulfill({ status: 404, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/panel/status', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ connected: true, deviceCount: 0 }) }));
}

test.describe('screenshots', () => {
  test('home + palette + calendar', async ({ page }) => {
    await mockSession(page);
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto('/app/');
    await page.getByRole('heading', { name: 'Today' }).waitFor();
    await page.screenshot({ path: 'shots/shell-home.png' });

    await page.keyboard.press('Control+k');
    await page.waitForTimeout(300);
    await page.screenshot({ path: 'shots/shell-palette.png' });
    await page.keyboard.press('Escape');

    await page.locator('#sidebar').getByRole('button', { name: 'Calendar' }).click();
    await page.waitForTimeout(600);
    await page.screenshot({ path: 'shots/shell-calendar.png' });
  });
});
