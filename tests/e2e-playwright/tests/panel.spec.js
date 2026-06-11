import { test, expect } from '@playwright/test';

// Browser UI e2e for the Server-hosted web panel (the served ui/). Drives the REAL app.js
// boot path: load → /health probe (200 => web mode) → getStatus (=> GET /api/me).
//   * Unauthenticated (/api/me 401): the sign-in gate renders, the sidebar is hidden.
//   * Authenticated (mocked session): the shell renders with the left sidebar.

test.describe('SyncMaster web panel', () => {
  test('unauthenticated: sign-in gate renders and the sidebar is hidden', async ({ page }) => {
    await page.goto('/app/');
    await expect(page.getByText('Sign in to Zync Master')).toBeVisible();
    await expect(page.getByRole('button', { name: /Sign in with Microsoft/i })).toBeVisible();
    // The gate collapses the shell: no sidebar until the session resolves as signed-in.
    await expect(page.locator('#sidebar')).toBeHidden();
  });

  test('unauthenticated: the served index is the real shell UI', async ({ page }) => {
    await page.goto('/app/');
    await expect(page).toHaveTitle(/Zync Master/);
    expect(await page.locator('script[src="js/app.js"]').count()).toBe(1);
    expect(await page.locator('link[href="css/shell.css"]').count()).toBe(1);
  });

  test('authenticated (mocked session): the shell renders with the sidebar', async ({ page }) => {
    await page.route('**/health', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ status: 'ok' }) }));
    await page.route('**/api/me', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ email: 'zeze@test', displayName: 'Zeze Lazo' }) }));
    await page.route('**/api/pairs', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([]) }));
    await page.route('**/api/accounts', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([{ accountRef: 'zeze@test', displayName: 'zeze@test', isDefault: true }]) }));
    await page.route('**/api/panel/status', (route) =>
      route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ connected: true, deviceCount: 0 }) }));

    await page.goto('/app/');

    await expect(page.getByText('Sign in to Zync Master')).toHaveCount(0);
    const sidebar = page.locator('#sidebar');
    await expect(sidebar).toBeVisible();
    await expect(sidebar.getByRole('button', { name: 'Home' })).toBeVisible();
    await expect(sidebar.getByRole('button', { name: 'Settings' })).toBeVisible();
    // Clipboard is desktop-only: hidden in the web panel.
    await expect(sidebar.getByRole('button', { name: 'Clipboard' })).toHaveCount(0);
  });
});
