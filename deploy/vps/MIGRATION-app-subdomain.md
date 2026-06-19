# Migration spec — move landing + web app to `app.devlabperu.com`, keep API on `api.devlabperu.com`

> Audience: the engineer/agent executing this (minimax). Self-contained. Read it fully before starting.
> Author context: Zync Master ships a .NET 10 ASP.NET server + an Angular 21 web (`web/zync-web`) + a
> static marketing landing (`web/`) + a desktop App (loads `ui/` locally; talks to the API). Today
> **everything** is one host `api.devlabperu.com` with nginx path-routing. The desktop App is NOT affected
> by this migration and must keep talking to `api.devlabperu.com/zync`.

## 1. Goal & target architecture

| Host | Serves | Change |
|---|---|---|
| `api.devlabperu.com/zync/` | **API only**: devices, clipboard, sync, identity, OAuth callbacks, WS, health | unchanged (stays) |
| `app.devlabperu.com/` | **Landing** (marketing, `web/`), static | NEW |
| `app.devlabperu.com/app/` | **Angular web app** (`web/zync-web`), static SPA | NEW |

- **API does not move.** Every paired desktop device has `ServerBaseUrl = https://api.devlabperu.com/zync`
  baked into its config, and the Microsoft/Google OAuth app registrations have redirect URIs under
  `api.devlabperu.com/zync/...`. Moving the API would break existing devices and require OAuth
  re-registration — out of scope.
- The landing + Angular become **static files served by nginx directly** on `app.devlabperu.com`,
  decoupled from ASP.NET. The ASP.NET server may keep serving them at `/zync/` as a fallback, but the
  canonical public URLs become the `app.` subdomain.

## 2. The hard part — this becomes CROSS-ORIGIN

Today the Angular app makes **relative** `/zync/...` calls (same host → same origin → no CORS, cookies
just work). After the move, the browser is on `https://app.devlabperu.com` and calls
`https://api.devlabperu.com/zync` → **cross-origin**. Three things MUST be handled or login/sync breaks:

1. **Angular API base URL.** The SPA currently relies on same-host relative calls (`proxy.conf.json` only
   helps `ng serve` in dev). Introduce an explicit API base and use it for every HTTP call:
   - `web/zync-web/src/environments/environment.ts` (dev) → `apiBase: '/zync'` (keep dev proxy).
   - `web/zync-web/src/environments/environment.prod.ts` → `apiBase: 'https://api.devlabperu.com/zync'`.
   - Replace hardcoded/relative `/zync/...` in services with `` `${environment.apiBase}/...` ``.
   - Every `HttpClient`/`fetch` that needs the session cookie must send credentials:
     `HttpClient` with `{ withCredentials: true }` (or a global interceptor that sets it).
2. **CORS on the API.** There is currently **no CORS** in the server (grep: no `AddCors`/`UseCors`). Add a
   policy that allows the app origin WITH credentials (cannot use `AllowAnyOrigin` together with
   credentials):
   ```csharp
   // Program.cs — after builder, before app.Build()
   builder.Services.AddCors(o => o.AddPolicy("web", p => p
       .WithOrigins("https://app.devlabperu.com")     // from config in real life (Server:WebOrigin)
       .AllowAnyHeader()
       .AllowAnyMethod()
       .AllowCredentials()));
   // Program.cs — in the pipeline, BEFORE auth/authorization, AFTER UseRouting/UsePathBase
   app.UseCors("web");
   ```
   Put the origin in `ServerOptions` (`WebOrigin`) and set it in `/etc/default/syncmaster`, not hardcoded.
   NOTE: the project rule is "no CORS in internal services". This is the EXCEPTION the rule allows — a
   browser SPA on a different origin is external traffic, exactly the Gateway/edge case. Document it as such.
3. **Cross-subdomain cookies.** Auth uses a cookie set by `api.devlabperu.com`, `SameSite=Lax`,
   `Secure=IsHttps` (Program.cs ~349; also CalendarConnect/Identity endpoints). `app.` and `api.` are the
   **same site** (same registrable domain `devlabperu.com`), so `SameSite=Lax` cookies ARE sent on
   cross-subdomain XHR — but verify in a real browser. If the session cookie does not ride the XHR:
   - set the cookie `SameSite=None; Secure` (requires https — prod is https, OK), and
   - keep it host-scoped to `api.devlabperu.com` (do NOT add `Domain=.devlabperu.com` unless the App also
     needs it — widening the cookie scope is a security downgrade).
   The App (desktop) uses the **bearer/api-key** path, not the cookie, so it is unaffected either way.

## 3. OAuth post-auth redirect

OAuth callbacks land on the API (`api.devlabperu.com/zync/identity/connect/callback/...`,
`.../calendar/connect/callback/graph`) — these stay registered as-is. But after a successful sign-in the
server redirects the user back to a UI. Today that target is same-host. After the move, when login is
initiated from `app.devlabperu.com`, the post-auth redirect must return there:
- Check `ServerOptions.RedirectUri` / `IdentityRedirectUri` / `PublicBaseUrl` and any
  `Results.Redirect(...)` after OAuth/magic-link success in `Modules/Identity/*Endpoints.cs`.
- Point the post-auth landing redirect at `https://app.devlabperu.com/app/` (configurable).
- The OAuth **provider** redirect URIs (Azure/Google portals) stay on `api.devlabperu.com` — do NOT change
  them; only the server's own post-auth redirect-to-UI changes.
- Magic-link: `{PublicBaseUrl}/identity/magic-link/callback` stays on the API; after redeeming, redirect
  the browser to `app.devlabperu.com/app/`.

## 4. Infra steps (VPS)

1. **DNS:** add an `A` record `app.devlabperu.com` → the VPS IP `167.114.98.148` (same box). Wait for
   propagation (`dig app.devlabperu.com +short`).
2. **TLS:** issue a Let's Encrypt cert: `sudo certbot --nginx -d app.devlabperu.com` (or `certonly` if you
   manage the server block by hand).
3. **Docroots:** create `/var/www/devlabperu.com/app/landing` and `/var/www/devlabperu.com/app/web`
   (owner `www-data`). The deploy will sync the static files here.
4. **nginx server block** for `app.devlabperu.com` (new file `/etc/nginx/sites-available/app-zync.conf`,
   symlinked into `sites-enabled`):
   ```nginx
   server {
     listen 443 ssl;
     server_name app.devlabperu.com;
     ssl_certificate     /etc/letsencrypt/live/app.devlabperu.com/fullchain.pem;
     ssl_certificate_key /etc/letsencrypt/live/app.devlabperu.com/privkey.pem;

     # Marketing landing at the root (static).
     root /var/www/devlabperu.com/app/landing;
     index index.html;
     location / { try_files $uri $uri/ /index.html; }

     # Angular SPA under /app/ (static; SPA fallback to its own index.html).
     location /app/ {
       alias /var/www/devlabperu.com/app/web/;
       try_files $uri $uri/ /app/index.html;
     }
   }
   # plus a `listen 80` server that redirects to https (certbot usually adds this).
   ```
   - The Angular build must be configured with `--base-href /app/` so its asset paths resolve under `/app/`.
   - `sudo nginx -t && sudo systemctl reload nginx`.

## 5. Build/deploy changes (`.github/workflows/deploy-syncmaster-vps.yml` + `deploy/vps/deploy-syncmaster.sh`)

- **Angular build:** add `--base-href=/app/` to the `ng build` step (line ~61):
  `npx ng build --configuration production --base-href=/app/`.
- **Upload landing:** the marketing `web/` (everything EXCEPT `web/zync-web`) is currently bundled into the
  ASP.NET wwwroot only. Add a step to rsync `web/` (excluding `zync-web`) to the VPS staging, and in
  `deploy-syncmaster.sh` swap it into `/var/www/devlabperu.com/app/landing`.
- **Web docroot:** the existing `[3/5] Syncing static web (zync-web)` swaps the Angular dist into the
  current nginx docroot — repoint it to `/var/www/devlabperu.com/app/web`.
- Keep deploying the API + (optionally) the bundled landing/ui into ASP.NET as today, so
  `api.devlabperu.com/zync/` keeps working during the cutover.
- Update the deploy **health check** (workflow) to also probe `https://app.devlabperu.com/` (landing 200)
  and `https://app.devlabperu.com/app/` (SPA 200).

## 6. App-facing string updates (cosmetic but do them)

- `ui/js/views/settings.js` → `ABOUT_WEBSITE_URL` currently `https://api.devlabperu.com/zync/`; change the
  product "website" to `https://app.devlabperu.com/`.
- `web/index.html` (landing) download/CTA links — keep release links to GitHub; any "open the web app" CTA
  points to `https://app.devlabperu.com/app/`.
- `deploy/vps/nginx-syncmaster.conf` — once the canonical web is on `app.`, you MAY drop the `/zync-web/`
  location from the `api.` host (or keep it as a redirect to `app.devlabperu.com/app/`).
- The desktop App's `ServerBaseUrl` stays `https://api.devlabperu.com/zync` — **do not touch**.

## 7. Verification checklist (do all before declaring done)

- [ ] `dig app.devlabperu.com +short` → VPS IP; `curl -I https://app.devlabperu.com/` → 200 landing.
- [ ] `curl -I https://app.devlabperu.com/app/` → 200 (Angular index).
- [ ] In a real browser on `https://app.devlabperu.com/app/`: sign in (Microsoft / Google / magic-link)
      end-to-end; confirm the session cookie rides the XHR to `api.devlabperu.com` and the dashboard loads
      data (calendar). This is the make-or-break test for CORS + cookies.
- [ ] OAuth round-trip returns the browser to `app.devlabperu.com/app/`, not `api.`.
- [ ] `api.devlabperu.com/zync/health` still 200; a paired **desktop App** still syncs (regression guard —
      the App path must be untouched).
- [ ] CORS: a cross-origin preflight (`OPTIONS`) to `api.devlabperu.com/zync/...` from the app origin
      returns the `Access-Control-Allow-Origin: https://app.devlabperu.com` + `-Allow-Credentials: true`.

## 8. Risk & rollback

- **Lowest-risk order:** DNS + cert + nginx + deploy the static landing/web to `app.` FIRST (additive — the
  old `api.devlabperu.com/zync` keeps serving everything). Then flip the Angular API base + add CORS + test.
  The `api.` path stays as a working fallback the whole time.
- **Rollback:** if cross-origin login breaks, revert the Angular `apiBase` to relative `/zync` and keep
  using `api.devlabperu.com/zync-web` until CORS/cookies are sorted — nothing is destructive; the `app.`
  subdomain just sits unused.
- **Do NOT** widen the auth cookie to `Domain=.devlabperu.com` casually — only if a real test proves the
  host-scoped cookie won't ride the XHR, and understand it exposes the cookie to every subdomain.

## 9. What is explicitly OUT of scope

- Moving the API/identity/OAuth off `api.devlabperu.com` (breaks devices + OAuth registrations).
- The desktop App (`ui/` is loaded locally by WebView2; it talks to the API, which is unchanged).
- Clipboard/file features (App-only; not on the web at all).
