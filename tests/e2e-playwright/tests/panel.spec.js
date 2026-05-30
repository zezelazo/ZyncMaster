import { test, expect } from '@playwright/test';

// Browser UI e2e for the Server-hosted web panel (the served ui/). Complements the
// deterministic .NET API e2e by driving the REAL browser through app.js's boot path:
//
//   load → app.js probes GET /health (200 => web mode) → calls getStatus (=> GET /api/me).
//   * Unauthenticated (/api/me 401): the sign-in gate renders, the bottom nav is hidden.
//   * Authenticated (/api/me 200): the dashboard + accordion renders, nav shows.
//
// The unauthenticated path runs against a real, freshly-started server with no session.
// The authenticated path cannot mint a real DPAPI-signed sm_session cookie from outside the
// server, so it fakes a signed-in session by mocking the same-origin REST responses the
// panel reads at boot (route interception) — the UI code under test is unchanged.

test.describe('ZyncMaster web panel', () => {
  test('unauthenticated: sign-in gate renders and the nav is hidden', async ({ page }) => {
    await page.goto('/');

    // The launch splash dismisses and the sign-in card appears once getStatus resolves 401.
    await expect(page.getByText('Sign in to Zync Master')).toBeVisible();
    await expect(page.getByRole('button', { name: /Sign in with Microsoft/i })).toBeVisible();

    // The gate hides the primary nav until the session resolves as signed-in.
    const nav = page.locator('#navbar');
    await expect(nav).toBeHidden();
  });

  test('unauthenticated: the served index is the real Liquid Glass UI', async ({ page }) => {
    await page.goto('/');
    await expect(page).toHaveTitle(/Zync Master/);
    // app.js + the design tokens stylesheet are wired in the real index.
    expect(await page.locator('script[src="js/app.js"]').count()).toBe(1);
  });

  test('authenticated (mocked session): the dashboard renders, gate is gone', async ({ page }) => {
    // Mock the same-origin REST the panel reads at boot so the gate resolves as signed-in.
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

    await page.goto('/');

    // The sign-in gate must NOT be shown for an authenticated session...
    await expect(page.getByText('Sign in to Zync Master')).toHaveCount(0);
    // ...and the primary nav becomes visible (Home / Settings tabs in web mode).
    const nav = page.locator('#navbar');
    await expect(nav).toBeVisible();
    await expect(page.getByRole('button', { name: 'Home' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Settings' })).toBeVisible();
  });
});
