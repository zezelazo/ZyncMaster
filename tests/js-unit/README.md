# js-unit — node unit tests for the web panel transport

Lightweight, dependency-free unit tests for the browser panel's web transport. No test
framework: just node's built-in `assert` and a stubbed `fetch`. **Not wired into the .NET
build or CI** — run it manually:

```
node tests/js-unit/web-transport.test.mjs
```

## What it covers

- **`ui/js/web-transport.js`** (imported directly): the pure action → REST mapping
  (`webRequestFor`), the inert device-only action list, URL-encoding of path params, and the
  `statusFromPairs` composition. This is the load-bearing contract between the UI's `Bridge`
  actions and the Server's REST API.
- A faithful re-creation of `app.js` `webCall`'s `fetch` wrapper, driven by a stubbed
  `fetch`, asserting: a **401 fires the sign-in-gate callback** and rejects; a non-2xx
  rejects; a 204 resolves null; a 2xx resolves parsed JSON.

## Why the transport mapping is an extracted module

`ui/js/app.js` is a browser ES module that touches `document` at module-load time, so it
cannot be imported cleanly in node without a DOM. The pure, DOM-free pieces were therefore
extracted into `ui/js/web-transport.js`, which `app.js` imports. The `fetch` wrapper is
re-created here (kept in lockstep with `app.js`) so the 401 → gate behaviour is still tested
with real assertions rather than mocked away.

A non-zero process exit code signals failure.
