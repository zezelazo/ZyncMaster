# ZyncMaster Server panel — browser UI e2e (Playwright)

Best-effort **real-browser** coverage of the Server-hosted web panel (the served `ui/`),
complementing the deterministic .NET API e2e in
`tests/ZyncMaster.Server.Tests/E2E/`.

> This suite is **manual / out-of-band**. It is intentionally **NOT** part of the .NET test
> run (`dotnet test`) or CI — it needs a browser engine and a running server. Nothing in the
> solution references it.

## What it covers

`tests/panel.spec.js` drives `app.js`'s boot path in a real Chromium:

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
