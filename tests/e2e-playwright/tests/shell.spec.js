import { test, expect } from '@playwright/test';

// Smoke del shell nuevo contra el web panel servido con sesión mockeada (mismo patrón
// de route-interception que panel.spec.js): sidebar, navegación, command palette, board.

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
  // loadPairs() warms each referenced account's calendar list (ensurePairCalendarNames). With a
  // real session these answer 200; mock them so an unmocked 401 never flips the sign-in gate.
  await page.route('**/api/accounts/**/calendars', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([]) }));
  await page.route('**/api/calendar/day', (route) =>
    route.fulfill({ status: 404, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/panel/status', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ connected: true, deviceCount: 0 }) }));
}

test.describe('SyncMaster shell', () => {
  test('today board renders the three blocks with honest empties', async ({ page }) => {
    await mockSession(page);
    await page.goto('/app/');
    await expect(page.getByRole('heading', { name: 'Today' })).toBeVisible();
    await expect(page.getByText('Next events')).toBeVisible();
    await expect(page.getByText('No upcoming events today')).toBeVisible();
    await expect(page.getByText('Sync health')).toBeVisible();
    await expect(page.getByText('Work → Personal')).toBeVisible();
    // Cero píxeles muertos: nada de "Soon" ni stats demo.
    await expect(page.getByText('Soon', { exact: true })).toHaveCount(0);
    await expect(page.getByText('Conflicts')).toHaveCount(0);
  });

  test('sidebar navigates between modules and marks the active entry', async ({ page }) => {
    await mockSession(page);
    await page.goto('/app/');
    const sidebar = page.locator('#sidebar');
    await sidebar.getByRole('button', { name: 'Calendar' }).click();
    await expect(sidebar.getByRole('button', { name: 'Calendar' })).toHaveAttribute('aria-current', 'page');
    await sidebar.getByRole('button', { name: 'Settings' }).click();
    await expect(sidebar.getByRole('button', { name: 'Settings' })).toHaveAttribute('aria-current', 'page');
  });

  test('Ctrl+K opens the palette; fuzzy filter + Enter navigates', async ({ page }) => {
    await mockSession(page);
    await page.goto('/app/');
    await page.keyboard.press('Control+k');
    const palette = page.getByRole('dialog', { name: 'Command palette' });
    await expect(palette).toBeVisible();
    await page.keyboard.type('go to cal');
    await expect(palette.getByText(/Go to .*Cal/i)).toBeVisible();
    await page.keyboard.press('Enter');
    await expect(palette).toBeHidden();
    await expect(page.locator('#sidebar').getByRole('button', { name: 'Calendar' })).toHaveAttribute('aria-current', 'page');
    // Esc cierra.
    await page.keyboard.press('Control+k');
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog', { name: 'Command palette' })).toHaveCount(0);
  });

  test('no aurora / glass shell material is served', async ({ page }) => {
    await mockSession(page);
    await page.goto('/app/');
    expect(await page.locator('link[href="css/aurora.css"]').count()).toBe(0);
    expect(await page.locator('.aurora').count()).toBe(0);
    expect(await page.locator('#navbar').count()).toBe(0);
  });
});
