import { defineConfig, devices } from '@playwright/test';

// Best-effort browser UI e2e for the Server web panel. This is deliberately standalone:
// it is NOT referenced by the .NET solution or CI. Run it manually against a locally-run
// server (see README.md). The base URL points at the ASP.NET Core server's default Kestrel
// address; override with BASE_URL when the server runs elsewhere.
const baseURL = process.env.BASE_URL ?? 'http://localhost:5000';

export default defineConfig({
  testDir: './tests',
  timeout: 30_000,
  expect: { timeout: 7_000 },
  fullyParallel: false,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL,
    headless: true,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
  ],
});
