# ZyncMaster Server panel — browser UI e2e (Playwright)

Best-effort **real-browser** coverage of the Server-hosted web panel (the served `ui/`),
complementing the deterministic .NET API e2e in
`tests/ZyncMaster.Server.Tests/E2E/`.

> This suite is **manual / out-of-band**. It is intentionally **NOT** part of the .NET test
> run (`dotnet test`) or CI — it needs a browser engine and a running server. Nothing in the
> solution references it.

## What it covers

> The clipboard paste popup is now a **native Avalonia window** (a DWM-backed acrylic popup, not a
> WebView2 page), so it is no longer browser-testable — the old `clipboard-viewer.spec.js` was
> removed. Its native row-mapping logic is covered by `ClipboardRowMapperTests` in the .NET suite.

### `tests/calendar-app-day.spec.js` — desktop App Calendar v2 day view (self-contained)

Drives the App shell's unified calendar day view in a real Chromium. **No running App or Server
is needed**: it serves `ui/` from disk via route interception and injects a mock
`window.chrome.webview` answering the real bridge contract; every mock mirrors the exact
`/api/calendar/day` wire shape and the bridge's serialized DTOs. It covers:

- the unified day **grid** (event rendered positioned, COM account degraded with the visible
  "snapshot unavailable" badge);
- the **Replicate modal** mask contract (decision D6): checking a destination with a blank mask
  keeps the CTA disabled ("Type a title for each destination"); typing a mask enables
  "Create 1 replica"; the `createEventReplicas` payload carries the typed mask and the
  two-segment event identity, and **never** the origin title;
- the **Prefix rules modal** listing the rules returned by the bridge (reached via the Replicate
  modal's "Manage");
- the **gear** routing to the pairs/accounts configuration sub-route;
- the read-only **Status popup** single Force-sync clearing its spinner and refreshing the per-pair
  form counts when the real run completes (the stuck-spinner regression guard).

```
npx playwright test calendar-app-day
```

### `tests/web-calendar.spec.js` — Angular web calendar (self-contained, needs a prior build)

Smoke of the Angular web UI. **Requires the production bundle on disk first**:

```
cd web/zync-web && npx ng build --configuration production
```

It serves `web/zync-web/dist/zync-web/browser` under `https://web.test/zync-web/` with a
`try_files`-style index fallback (exercising the `/zync-web/` base-href + client routing via a
deep link) and mocks the `/zync` API with `page.route`; auth enters through the real route guard
via a mocked `/identity/refresh`. It covers the unified per-account columns with the COM
"snapshot unavailable" badge, the event detail's replicas section, respond-to-organizer with a
message (decline + message posted to the two-segment respond route), and organizer-only cancel
behind an explicit confirmation.

```
npx playwright test web-calendar
```

### `tests/panel.spec.js` — Server web panel (needs a running Server)

Drives `app.js`'s boot path in a real Chromium:

1. **Unauthenticated** — load `/`, the panel probes `GET /health` (200 ⇒ web mode), calls
   `getStatus` (⇒ `GET /api/me`, 401 with no session). Asserts the **sign-in gate** renders
   ("Sign in to Zync Master" + "Sign in with Microsoft"), and the bottom nav is hidden.
2. **Served index** — the real Liquid Glass `index.html` is served (title + `js/app.js`).
3. **Authenticated (mocked session)** — a real DPAPI-signed `sm_session` cookie cannot be
   minted from outside the server, so this test fakes a signed-in session by intercepting the
   same-origin REST the panel reads at boot (`/api/me`, `/api/pairs`, `/api/accounts`,
   `/api/panel/status`, `/health`). It asserts the gate is gone and the dashboard + nav render.
   The UI code under test is unmodified — only the network is stubbed.

## Prerequisites

- Node 18+ and npm.
- A locally-running Server.

## Run

From the repo root, start the Server (leave it running):

```
dotnet run --project src/ZyncMaster.Server
```

By default Kestrel listens on `http://localhost:5000` (and `https://localhost:5001`). If your
launch profile uses a different port, set `BASE_URL` accordingly below.

Then, in `tests/e2e-playwright/`:

```
npm install
npx playwright install chromium   # one-time: fetch the browser engine
npx playwright test                # run the suite (headless)
npx playwright test --headed       # watch it drive the browser
```

Point at a non-default address:

```
BASE_URL=http://localhost:5123 npx playwright test
```

## Status

Authored and wired; **best-effort**. Run it manually against a live Server as above. The
deterministic backbone (the .NET API full-flow e2e) is what gates correctness in CI; this
suite adds real-browser confidence on top.
