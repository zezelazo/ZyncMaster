# Zync Master

**Zync Master** is a self-hosted personal **sync suite** — it mirrors your calendars across accounts and
syncs your clipboard across your devices, quietly and continuously. It is a `.NET 10` solution (desktop
app + backend server + sync engine + Microsoft Graph client) with an Angular web UI, deployed and running
in production at `https://api.devlabperu.com/zync/`.

> **Vendor:** DevLab-Pe ([devlabperu.com](https://devlabperu.com)). Status: public beta (v0.4.x, pre-1.0).
> For the full design, roadmap and decision records see [`docs/`](docs/) (the `docs/research/`,
> `docs/superpowers/specs/` and `docs/plans/` folders are the authoritative, up-to-date references).

---

## What it does today (v0.4.x, verified in code)

- **Calendar sync (one-way mirror)** — mirror events from an Outlook Classic calendar (via COM) or any
  Microsoft account (via Graph) into another account. Multi-account, multi-pair, configurable schedule,
  per-account read/read-write scope, idempotent upsert on a stable id, run-locks with fencing tokens.
- **Calendar v2 — replica engine with title masking** — `[prefix]` rules mask the origin subject and fan
  a replica out to destination calendars carrying only the **mask** title (never the source title), so a
  second job's tenant never sees the first job's event titles. Skip-by-content-hash avoids redundant
  writes. (`src/ZyncMaster.Server/Modules/Calendar/`.)
- **Clipboard sync (multi-device)** — copy on one machine, paste on another. **Text is end-to-end
  encrypted** (the server is blind to plaintext; the wrapped key is relayed device-to-device via RSA).
  Images sync with a size cap; per-user history is bounded by FIFO + age (24h) + image-byte caps. A global
  hotkey opens a floating paste viewer.
- **Identity** — sign in with Microsoft (loopback OAuth on desktop, web flow for the SPA) or a magic link.
  Logins of the **same authoritatively-verified email** link to one account (nOAuth-hardened: trust only a
  Microsoft consumer-tenant or `xms_edov` signal, never a bare `email_verified`).
- **Multi-device** — device-pinning (a COM pair is pinned to one machine), a device lease so the App and
  the server cron never double-run, and a self-healing device API key.

Specified and on the roadmap (not yet implemented): Notes, Browser sync, File sync, lazy-blob clipboard
(images/files end-to-end), macOS. See [`docs/superpowers/specs/`](docs/superpowers/specs/).

---

## Architecture

```
src/ZyncMaster.Core/      shared models + contracts + helpers (UuidV5, DailyFileLogger, SettingsRepository)
src/ZyncMaster.CalExport/ Outlook Classic → JSON/TXT exporter (COM interop; net10.0-windows)
src/ZyncMaster.Engine/    device-side sync engine: PairScheduler/PairRunner, ClipboardService, key exchange
src/ZyncMaster.Graph/     Microsoft Graph REST client (no SDK): CalendarMirror, replica client, responder
src/ZyncMaster.Server/    ASP.NET Core + EF Core + PostgreSQL 18 — modules: Calendar, Clipboard, Devices,
                          Identity, Sync, Panel, Entitlements. Auth: ApiKey (devices) + Cookie/Bearer (panel)
src/ZyncMaster.App/       Avalonia + WebView2 desktop app (system tray). Hosts the ui/ panel; launches CalExport

ui/        vanilla HTML/CSS/JS panel mounted in the App's WebView2 (and served by the Server at /zync/app)
web/       marketing landing (web/index.html) + Angular 21 workspace (web/zync-web → /zync-web)
deploy/vps/ systemd unit, nginx location blocks, the on-VPS deploy script, DB backup script
docs/      design specs, plans, research, and decision records (the real product documentation)
```

Layered SOLID with manual constructor injection — no DI container in the device-side tools; the Server uses
ASP.NET Core DI. Dependency direction is inward through interfaces; `ZyncMaster.Core` references nothing else.

> **Note:** an earlier standalone `ZyncMaster.CalImport` console tool (JSON → Graph import) has been
> **removed**; its import logic now lives in `ZyncMaster.Graph` (`CalendarMirror`) + the Server's sync
> modules. References to it in older docs (and in `WIKI.md`, which is CalExport-specific) are historical.

## Build / test / run

```
dotnet build -c Release
dotnet test                       # 1975 tests across 7 projects (Postgres integration tests skip without a live DB)
dotnet test --settings coverage.runsettings --results-directory ./coverage
```

The desktop app release (self-contained win-x64, with CalExport bundled) is produced by the manual
`release-app.yml` workflow and published as a GitHub prerelease. The server + Angular web deploy to the VPS
via `deploy-syncmaster-vps.yml` (publish → SSH/rsync → migrate → restart → health check). See
[`deploy/vps/README.md`](deploy/vps/README.md) for the on-VPS provisioning, backup and rollback details.

CalExport CLI: `ZyncMaster.CalExport.exe` (interactive), `-a` (silent), `-a -c <config> -o <outDir>`.
`WIKI.md` is the class-by-class reference for CalExport specifically.

## Deployment & operations

- **Live service:** `https://api.devlabperu.com/zync/` (server) + `/zync-web/` (Angular) + `/zync/` landing.
- **DB backup:** nightly `pg_dump` via [`deploy/vps/syncmaster-backup.sh`](deploy/vps/syncmaster-backup.sh)
  (the DB holds the DataProtection key ring + encrypted refresh tokens — back it up off-box).
- **Deploy rollback:** `deploy/vps/deploy-syncmaster.sh` snapshots the live build and restores it if the
  swap/migrate/restart or the post-restart health check fails.
- **Monitoring:** `/zync/health` verifies the DB connection; `/api/sync/run-due` returns `hadFailures` and
  the runners log a `[Warning]` summary when any pair fails.

## Repo conventions

Commit messages and PRs read as human-written (no AI-tool attribution). Code identifiers and inline strings
are in English; team communication is in Spanish. Tests accompany every behavioural change.
